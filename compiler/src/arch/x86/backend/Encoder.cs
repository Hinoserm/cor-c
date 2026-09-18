#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// Turns allocated machine instructions into bytes in the code section.
///
/// Jumps inside a function are resolved here by relaxation: every one
/// starts out as the two-byte short form, and any whose target turns out
/// to be further than a signed byte away is widened, until a pass changes
/// nothing. Widening only ever grows the code, so this converges. Calls,
/// symbol addresses and jump-table entries become relocations for the
/// linker; a block's address is a relocation against the function's own
/// symbol with the block's offset as the addend.
/// </summary>
internal sealed class Encoder
{
    private readonly Section _text;
    private readonly List<byte> _out;
    private readonly List<Relocation> _pending = new();
    private MFunction _m = null!;
    private int _funcStart;
    private readonly HashSet<MInstr> _long = new();
    private bool _widen;

    /// <summary>Jump tables this function needs in .rodata: the symbol to place them at, and their entries.</summary>
    public List<(string Symbol, List<MBlock> Targets)> Tables { get; } = new();

    /// <summary>
    /// Every call in the function just encoded, as the offset of the byte
    /// it RETURNS to. That offset is what a stack map is keyed by: the
    /// collector holds a return address it found on the stack and has
    /// nothing else to look the frame up with.
    /// </summary>
    public List<(MInstr Call, int Return)> CallSites { get; } = new();

    /// <summary>
    /// Where each source line begins within this function, as (offset, line)
    /// in increasing offset order and with no two entries running.
    ///
    /// This is the raw material of the line table a stack trace reads: a
    /// return address lands somewhere inside a function, and the line it
    /// belongs to is the last entry at or before it.
    /// </summary>
    public List<(int Offset, int Line)> Lines { get; } = new();

    public Encoder(Section text)
    {
        _text = text;
        _out = text.Bytes;
    }

    public void ReleaseFunction()
    {
        _m = null!;
        _long.Clear(); Tables.Clear(); CallSites.Clear(); Lines.Clear(); _pending.Clear();
    }

    /// <summary>Encode a function at the current end of the section; returns its size.</summary>
    public int Encode(MFunction m)
    {
        _m = m;
        _funcStart = _out.Count;
        _long.Clear();

        // Layout passes until no jump needs widening. A forward jump reads
        // its target's offset from the previous pass, so the first pass
        // only establishes offsets and at least one more is always needed;
        // a pass that widens nothing has the same sizes as the pass before
        // it, so the offsets it used were exact and its bytes stand.
        foreach (MBlock b in m.Blocks)
        {
            b.Offset = 0;
        }
        // The first pass sees forward targets at offset 0, so it must not
        // widen anything: a widening decision is permanent, and one made
        // against a stale offset would leave a five-byte jump where two
        // bytes would do.
        _widen = false;
        Pass();
        _widen = true;
        bool grew;
        do
        {
            _pending.Clear();
            _out.RemoveRange(_funcStart, _out.Count - _funcStart);
            grew = Pass();
        }
        while (grew);
        _text.Relocs.AddRange(_pending);
        return _out.Count - _funcStart;
    }

    private bool Pass()
    {
        bool grew = false;
        Tables.Clear();
        CallSites.Clear();
        Lines.Clear();
        for (int b = 0; b < _m.Blocks.Count; b++)
        {
            MBlock block = _m.Blocks[b];
            block.Offset = _out.Count - _funcStart;
            MBlock? next = b + 1 < _m.Blocks.Count ? _m.Blocks[b + 1] : null;
            foreach (MInstr i in block.Instrs)
            {
                // ONE ENTRY PER RUN OF A LINE, not one per instruction: a
                // statement becomes a dozen instructions and they all belong
                // to it. A line of 0 means the instruction came from no line
                // of source -- a prologue, a spill -- and keeps whatever line
                // was current, because that is the line the reader is on.
                int here = _out.Count - _funcStart;
                if (i.Line > 0 && (Lines.Count == 0 || Lines[^1].Line != i.Line))
                {
                    if (Lines.Count > 0 && Lines[^1].Offset == here)
                    {
                        Lines[^1] = (here, i.Line);
                    }
                    else
                    {
                        Lines.Add((here, i.Line));
                    }
                }

                if (i.Op is MOp.Jmp or MOp.Jcc)
                {
                    grew |= EncodeJump(i, next);
                }
                else
                {
                    EncodeInstr(i);
                    if (i.Op is MOp.Call or MOp.CallInd)
                    {
                        CallSites.Add((i, _out.Count - _funcStart));
                    }
                }
            }
        }
        return grew;
    }

    // ---- primitives ---------------------------------------------------------------

    private void B(params byte[] bytes) => _out.AddRange(bytes);

    private void D32(long v)
    {
        _out.Add((byte)v);
        _out.Add((byte)(v >> 8));
        _out.Add((byte)(v >> 16));
        _out.Add((byte)(v >> 24));
    }

    private void D16(long v)
    {
        _out.Add((byte)v);
        _out.Add((byte)(v >> 8));
    }

    private int Here => _out.Count - _funcStart;

    /// <summary>A 32-bit field holding an absolute address: a relocation and a placeholder.</summary>
    private void Abs32(string symbol, long addend)
    {
        SymRef(symbol, addend, RelocKind.Abs32);
    }

    /// <summary>A 32-bit field the linker fills from a symbol, however this reference is made.</summary>
    private void SymRef(string symbol, long addend, RelocKind kind)
    {
        _pending.Add(new Relocation(_out.Count, symbol, addend, kind));
        D32(0);
    }

    private void Imm32(MImm imm)
    {
        if (imm.Label is not null)
        {
            // The label's offset is only right on the last pass; earlier
            // passes just need the size, which is four bytes regardless.
            Abs32(_m.Source.Name, imm.Label.Offset);
        }
        else if (imm.Symbol is not null)
        {
            Abs32(imm.Symbol, imm.Value);
        }
        else
        {
            D32(imm.Value);
        }
    }

    private void Imm(MImm imm, int width)
    {
        switch (width)
        {
            case 1:
                _out.Add((byte)imm.Value);
                break;
            case 2:
                D16(imm.Value);
                break;
            default:
                Imm32(imm);
                break;
        }
    }

    private static int Reg(MOperand o) => ((MReg)o).Id;

    /// <summary>
    /// ModRM, SIB and displacement for a register field and an r/m operand.
    /// [EBP] with no displacement does not exist (that encoding means
    /// [disp32]), and [ESP] always needs a SIB byte; both are handled here so
    /// nothing else has to know.
    /// </summary>
    private void ModRM(int reg, MOperand rm)
    {
        reg &= 7;
        if (rm is MReg r)
        {
            _out.Add((byte)(0xC0 | (reg << 3) | (r.Id & 7)));
            return;
        }
        MMem m = (MMem)rm;
        int scaleBits = m.Scale switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => throw new InvalidOperationException("scale") };
        if (m.Base is null)
        {
            if (m.Index is null)
            {
                _out.Add((byte)((reg << 3) | 5));
            }
            else
            {
                _out.Add((byte)((reg << 3) | 4));
                _out.Add((byte)((scaleBits << 6) | (m.Index.Id << 3) | 5));
            }
            MemDisp32(m);
            return;
        }

        int baseId = m.Base.Id;
        bool sib = m.Index is not null || baseId == (int)Gpr.Esp;
        int mod;
        if (m.Symbol is not null || m.Label is not null)
        {
            mod = 2;
        }
        else if (m.Disp == 0 && baseId != (int)Gpr.Ebp)
        {
            mod = 0;
        }
        else if (m.Disp is >= -128 and <= 127)
        {
            mod = 1;
        }
        else
        {
            mod = 2;
        }
        _out.Add((byte)((mod << 6) | (reg << 3) | (sib ? 4 : baseId)));
        if (sib)
        {
            int index = m.Index?.Id ?? 4;
            _out.Add((byte)((scaleBits << 6) | (index << 3) | baseId));
        }
        if (mod == 1)
        {
            _out.Add((byte)m.Disp);
        }
        else if (mod == 2)
        {
            MemDisp32(m);
        }
    }

    /// <summary>The four-byte displacement of a memory operand, relocated if it names anything.</summary>
    private void MemDisp32(MMem m)
    {
        if (m.Label is not null)
        {
            // Only the last layout pass has the right offset; every pass
            // needs the same four bytes, which is all the earlier ones use.
            SymRef(_m.Source.Name, m.Label.Offset + m.Disp, m.Reloc);
        }
        else if (m.Symbol is not null)
        {
            SymRef(m.Symbol, m.Disp, m.Reloc);
        }
        else
        {
            D32(m.Disp);
        }
    }

    private void Prefixes(MInstr i)
    {
        if (i.Lock)
        {
            _out.Add(0xF0);
        }
        // A 16-bit memory operand of movzx/movsx or the x87 control-word
        // instructions is implied by the opcode; only a 16-bit register or
        // ALU operand takes the operand-size prefix.
        if (i.Width == 2 && i.Op is not (MOp.Movzx or MOp.Movsx or MOp.Fnstcw or MOp.Fldcw or MOp.RepInsw or MOp.RepOutsw))
        {
            _out.Add(0x66);
        }
    }

    /// <summary>The opcode byte for an instruction that has a byte form one below its full form.</summary>
    private static byte Wide(byte full, int width) => width == 1 ? (byte)(full - 1) : full;

    // ---- instructions -----------------------------------------------------------------

    private static int AluNumber(MOp op) => op switch
    {
        MOp.Add => 0, MOp.Or => 1, MOp.Adc => 2, MOp.Sbb => 3, MOp.And => 4, MOp.Sub => 5, MOp.Xor => 6, _ => 7,
    };

    private void EncodeInstr(MInstr i)
    {
        Prefixes(i);
        int w = i.Width;
        switch (i.Op)
        {
            case MOp.Mov:
                EncodeMov(i);
                break;
            case MOp.Bswap:
                if (w != 4 || i.Operands.Count != 1 || i.Operands[0] is not MReg { IsPhys: true })
                    throw new InvalidOperationException("BSWAP requires a 32-bit register");
                B(0x0f); B((byte)(0xc8 + Reg(i.Operands[0])));
                break;
            case MOp.Add:
            case MOp.Adc:
            case MOp.Sub:
            case MOp.Sbb:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Cmp:
            {
                int n = AluNumber(i.Op);
                MOperand dst = i.Operands[0];
                MOperand src = i.Operands[1];
                if (src is MImm imm)
                {
                    if (w == 1)
                    {
                        B(0x80);
                        ModRM(n, dst);
                        _out.Add((byte)imm.Value);
                    }
                    else if (imm.FitsSbyte)
                    {
                        B(0x83);
                        ModRM(n, dst);
                        _out.Add((byte)imm.Value);
                    }
                    else
                    {
                        B(0x81);
                        ModRM(n, dst);
                        Imm(imm, w);
                    }
                }
                else if (src is MReg)
                {
                    B(Wide((byte)(0x01 + 8 * n), w));
                    ModRM(Reg(src), dst);
                }
                else
                {
                    B(Wide((byte)(0x03 + 8 * n), w));
                    ModRM(Reg(dst), src);
                }
                break;
            }
            case MOp.Test:
                if (i.Operands[1] is MImm timm)
                {
                    B(Wide(0xF7, w));
                    ModRM(0, i.Operands[0]);
                    Imm(timm, w);
                }
                else
                {
                    B(Wide(0x85, w));
                    ModRM(Reg(i.Operands[1]), i.Operands[0]);
                }
                break;
            case MOp.Movzx:
            case MOp.Movsx:
            {
                byte op = (byte)((i.Op == MOp.Movzx ? 0xB6 : 0xBE) + (w == 2 ? 1 : 0));
                B(0x0F, op);
                ModRM(Reg(i.Operands[0]), i.Operands[1]);
                break;
            }
            case MOp.Lea:
                B(0x8D);
                ModRM(Reg(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.Xchg:
                B(Wide(0x87, w));
                ModRM(Reg(i.Operands[1]), i.Operands[0]);
                break;
            case MOp.Push:
                switch (i.Operands[0])
                {
                    case MReg r:
                        _out.Add((byte)(0x50 + r.Id));
                        break;
                    case MImm imm when imm.FitsSbyte:
                        B(0x6A, (byte)imm.Value);
                        break;
                    case MImm imm:
                        B(0x68);
                        Imm32(imm);
                        break;
                    default:
                        B(0xFF);
                        ModRM(6, i.Operands[0]);
                        break;
                }
                break;
            case MOp.Pop:
                _out.Add((byte)(0x58 + Reg(i.Operands[0])));
                break;
            case MOp.Imul:
                B(0x0F, 0xAF);
                ModRM(Reg(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.Imul3:
            {
                MImm imm = (MImm)i.Operands[2];
                B(imm.FitsSbyte ? (byte)0x6B : (byte)0x69);
                ModRM(Reg(i.Operands[0]), i.Operands[1]);
                if (imm.FitsSbyte)
                {
                    _out.Add((byte)imm.Value);
                }
                else
                {
                    Imm32(imm);
                }
                break;
            }
            case MOp.Mul:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
            case MOp.Neg:
            case MOp.Not:
            {
                int n = i.Op switch { MOp.Mul => 4, MOp.ImulWide => 5, MOp.Div => 6, MOp.Idiv => 7, MOp.Neg => 3, _ => 2 };
                B(Wide(0xF7, w));
                ModRM(n, i.Operands[0]);
                break;
            }
            case MOp.Shl:
            case MOp.Ror:
            case MOp.Shr:
            case MOp.Sar:
            {
                int n = i.Op switch { MOp.Ror => 1, MOp.Shl => 4, MOp.Shr => 5, _ => 7 };
                if (i.Operands[1] is MImm imm)
                {
                    if (imm.Value == 1)
                    {
                        B(Wide(0xD1, w));
                        ModRM(n, i.Operands[0]);
                    }
                    else
                    {
                        B(Wide(0xC1, w));
                        ModRM(n, i.Operands[0]);
                        _out.Add((byte)imm.Value);
                    }
                }
                else
                {
                    RequireCl(i, 1);
                    B(Wide(0xD3, w));
                    ModRM(n, i.Operands[0]);
                }
                break;
            }
            case MOp.Shld:
            case MOp.Shrd:
            {
                byte baseOp = i.Op == MOp.Shld ? (byte)0xA4 : (byte)0xAC;
                if (i.Operands[2] is MImm imm)
                {
                    B(0x0F, baseOp);
                    ModRM(Reg(i.Operands[1]), i.Operands[0]);
                    _out.Add((byte)imm.Value);
                }
                else
                {
                    RequireCl(i, 2);
                    B(0x0F, (byte)(baseOp + 1));
                    ModRM(Reg(i.Operands[1]), i.Operands[0]);
                }
                break;
            }
            case MOp.Cdq:
                B(0x99);
                break;
            case MOp.Setcc:
                B(0x0F, (byte)(0x90 + (int)i.Cond));
                ModRM(0, i.Operands[0]);
                break;
            case MOp.JmpTable:
            {
                // jmp dword [index*4 + table]: SIB with no base, disp32 relocated to the table.
                string sym = TableSymbol();
                Tables.Add((sym, i.Table!));
                B(0xFF);
                // Position-independent code brings the GOT pointer along as a
                // second operand, and the table is then an offset from it:
                // the alternative is an absolute address in the instruction,
                // which in a shared object is a relocation in the text.
                MReg? tableBase = i.Operands.Count > 1 ? (MReg)i.Operands[1] : null;
                ModRM(4, tableBase is null
                    ? new MMem(null, 0) { Index = i.Reg(0), Scale = 4, Symbol = sym }
                    : new MMem(tableBase, 0) { Index = i.Reg(0), Scale = 4, Symbol = sym, Reloc = RelocKind.GotOff });
                break;
            }
            case MOp.JmpInd:
                B(0xFF);
                ModRM(4, i.Operands[0]);
                break;
            case MOp.Call:
            {
                MImm target = (MImm)i.Operands[0];
                B(0xE8);
                // The displacement is from the end of the instruction, four bytes past the field.
                _pending.Add(new Relocation(_out.Count, target.Symbol!, target.Value - 4, i.CallReloc));
                D32(0);
                break;
            }
            case MOp.CallInd:
                B(0xFF);
                ModRM(2, i.Operands[0]);
                break;
            case MOp.Ret:
                B(0xC3);
                break;
            case MOp.Xadd:
                B(0x0F, Wide(0xC1, w));
                ModRM(Reg(i.Operands[1]), i.Operands[0]);
                break;
            case MOp.Cmpxchg:
                B(0x0F, Wide(0xB1, w));
                ModRM(Reg(i.Operands[1]), i.Operands[0]);
                break;
            case MOp.RepMovsb:
                B(0xF3, 0xA4);
                break;
            case MOp.MmxLoad:
            case MOp.MmxStore:
                if (!Target.Current.X86Profile.Mmx || i.Operands.Count != 1 || i.Operands[0] is not MMem)
                    throw new InvalidOperationException("MMX memory move requires an enabled MMX profile and memory");
                B(0x0f, i.Op == MOp.MmxLoad ? (byte)0x6f : (byte)0x7f); ModRM(0, i.Operands[0]);
                break;
            case MOp.MmxZero:
                if (!Target.Current.X86Profile.Mmx) throw new InvalidOperationException("MMX is disabled");
                B(0x0f, 0xef, 0xc0); break;
            case MOp.MmxAddB: case MOp.MmxAddW: case MOp.MmxAddD:
            case MOp.MmxSubB: case MOp.MmxSubW: case MOp.MmxSubD:
            case MOp.MmxAnd: case MOp.MmxOr: case MOp.MmxXor: case MOp.MmxMulW:
                if (!Target.Current.X86Profile.Mmx || i.Operands.Count != 1 || i.Operands[0] is not MMem)
                    throw new InvalidOperationException("Packed arithmetic requires an MMX profile and memory");
                B(0x0f, i.Op switch {
                    MOp.MmxAddB => (byte)0xfc, MOp.MmxAddW => (byte)0xfd, MOp.MmxAddD => (byte)0xfe,
                    MOp.MmxSubB => (byte)0xf8, MOp.MmxSubW => (byte)0xf9, MOp.MmxSubD => (byte)0xfa,
                    MOp.MmxAnd => (byte)0xdb, MOp.MmxOr => (byte)0xeb, MOp.MmxXor => (byte)0xef, _ => (byte)0xd5 });
                ModRM(0, i.Operands[0]); break;
            case MOp.Emms:
                if (!Target.Current.X86Profile.Mmx) throw new InvalidOperationException("MMX is disabled");
                B(0x0f, 0x77); break;
            case MOp.Femms:
                if (!Target.Current.X86Profile.ThreeDNow) throw new InvalidOperationException("3DNow is disabled");
                B(0x0f, 0x0e); break;
            case MOp.RepMovsd:
                B(0xF3, 0xA5);
                break;
            case MOp.RepStosb:
                B(0xF3, 0xAA);
                break;
            case MOp.RepStosd:
                B(0xF3, 0xAB);
                break;
            case MOp.LockOrEsp:
                B(0xF0, 0x83, 0x0C, 0x24, 0x00);
                break;
            case MOp.Fld:
                B(w == 4 ? (byte)0xD9 : (byte)0xDD);
                ModRM(0, i.Operands[0]);
                break;
            case MOp.Fstp:
                B(w == 4 ? (byte)0xD9 : (byte)0xDD);
                ModRM(3, i.Operands[0]);
                break;
            case MOp.Fild:
                B(w == 4 ? (byte)0xDB : (byte)0xDF);
                ModRM(w == 4 ? 0 : 5, i.Operands[0]);
                break;
            case MOp.Fistp:
                B(w == 4 ? (byte)0xDB : (byte)0xDF);
                ModRM(w == 4 ? 3 : 7, i.Operands[0]);
                break;
            case MOp.Fadd:
            case MOp.Fsub:
            case MOp.Fmul:
            case MOp.Fdiv:
            {
                int n = i.Op switch { MOp.Fadd => 0, MOp.Fmul => 1, MOp.Fsub => 4, _ => 6 };
                B(w == 4 ? (byte)0xD8 : (byte)0xDC);
                ModRM(n, i.Operands[0]);
                break;
            }
            case MOp.Fchs:
                B(0xD9, 0xE0);
                break;
            case MOp.Fsqrt:
                B(0xD9, 0xFA);
                break;
            case MOp.Fucompp:
                B(0xDA, 0xE9);
                break;
            case MOp.Fnstsw:
                B(0xDF, 0xE0);
                break;
            case MOp.Sahf:
                B(0x9E);
                break;
            case MOp.Fnstcw:
                B(0xD9);
                ModRM(7, i.Operands[0]);
                break;
            case MOp.Fldcw:
                B(0xD9);
                ModRM(5, i.Operands[0]);
                break;
            case MOp.Int:
                B(0xCD, (byte)((MImm)i.Operands[0]).Value);
                break;
            case MOp.Int3:
                B(0xCC);
                break;
            case MOp.Nop:
                B(0x90);
                break;
            case MOp.Pause:
                // rep nop: a plain nop to a 486, the spin hint to anything newer.
                B(0xF3, 0x90);
                break;
            case MOp.SyscallTrap:
                B(0xCD, 0x80);
                break;

            // ---- driver and kernel instructions ---------------------------
            //
            // The operand-size prefix for the 16-bit forms of IN and OUT is
            // emitted by Prefixes; the string forms carry their own, after
            // the REP, so that objdump prints `rep insw` and not `data16 rep`.
            case MOp.In:
                B(w == 1 ? (byte)0xEC : (byte)0xED);
                break;
            case MOp.Out:
                B(w == 1 ? (byte)0xEE : (byte)0xEF);
                break;
            case MOp.RepInsw:
                B(0xF3, 0x66, 0x6D);
                break;
            case MOp.RepOutsw:
                B(0xF3, 0x66, 0x6F);
                break;
            case MOp.Cli:
                B(0xFA);
                break;
            case MOp.Sti:
                B(0xFB);
                break;
            case MOp.Hlt:
                B(0xF4);
                break;
            case MOp.Lgdt:
                B(0x0F, 0x01);
                ModRM(2, i.Operands[0]);
                break;
            case MOp.Lidt:
                B(0x0F, 0x01);
                ModRM(3, i.Operands[0]);
                break;
            case MOp.Invlpg:
                B(0x0F, 0x01);
                ModRM(7, i.Operands[0]);
                break;
            case MOp.MovFromCr:
                // 0F 20 /r, mod always 11: the control register is the reg
                // field and the general register is the r/m field.
                B(0x0F, 0x20);
                _out.Add((byte)(0xC0 | (CrNumber(i) << 3) | ((MReg)i.Operands[0]).Id));
                break;
            case MOp.GotPc:
            {
                // The address of the GOT, in whatever register the allocator
                // chose. There is no thunk: `call $+5` pushes the address of
                // the next instruction and the pop takes it back, which needs
                // no second symbol and no fixed register. It unbalances the
                // return predictor, which a 486 does not have.
                int r = ((MReg)i.Operands[0]).Id;
                B(0xE8, 0x00, 0x00, 0x00, 0x00);
                _out.Add((byte)(0x58 + r));
                // add r, _GLOBAL_OFFSET_TABLE_. R_386_GOTPC computes
                // GOT + A - P, so the immediate wanted here is GOT minus the
                // address the register already holds -- which is the address
                // of the `pop`, THREE bytes before the displacement field:
                // one for the pop, two for this instruction's opcode and
                // ModRM. (The ABI's A = 2 is for the sequence with a thunk,
                // where the add follows the call directly and there is no
                // pop in between.)
                B(0x81, (byte)(0xC0 + r));
                SymRef("_GLOBAL_OFFSET_TABLE_", 3, RelocKind.GotPc);
                break;
            }
            case MOp.GsSelf:
            {
                // 65 8B /r with mod 00 and r/m 101: a disp32 with no base,
                // taken in GS rather than DS. The displacement is zero -- the
                // self pointer is the block's first word.
                int r = ((MReg)i.Operands[0]).Id;
                B(0x65, 0x8B);
                _out.Add((byte)(0x05 | (r << 3)));
                B(0x00, 0x00, 0x00, 0x00);
                break;
            }
            case MOp.SetGs:
                // 8E /r with the segment number in the reg field: GS is 5.
                B(0x8E);
                _out.Add((byte)(0xE8 | ((MReg)i.Operands[0]).Id));
                break;
            case MOp.LoadSegments:
                // mov ds/es/fs/gs/ss, ax -- 8E /r with the segment number in
                // the reg field: ES 0, SS 2, DS 3, FS 4, GS 5.
                B(0x8E, 0xD8, 0x8E, 0xC0, 0x8E, 0xE0, 0x8E, 0xE8, 0x8E, 0xD0);
                break;
            case MOp.MovToCr:
                B(0x0F, 0x22);
                _out.Add((byte)(0xC0 | (CrNumber(i) << 3) | ((MReg)i.Operands[0]).Id));
                break;
            case MOp.Prologue:
                EncodePrologue();
                break;
            case MOp.Epilogue:
                EncodeEpilogue();
                break;
            default:
                throw new InvalidOperationException($"cannot encode {i.Op}");
        }
    }

    /// <summary>
    /// Which control register a `mov` names. It has to be a plain number the
    /// compiler knew: there is no form of the instruction that takes one from
    /// a register, so a non-constant would silently encode the wrong one.
    /// </summary>
    private int CrNumber(MInstr i)
    {
        if (i.Operands[1] is not MImm { IsPlain: true } n || n.Value is < 0 or > 7)
        {
            throw new InvalidOperationException($"{_m.Source.Name}: {i.Op} needs a control register number 0-7");
        }
        return (int)n.Value;
    }

    /// <summary>
    /// A variable shift count is CL by the instruction's definition; the
    /// operand only names it for the allocator. Anything else there means
    /// an earlier pass renamed it, and encoding it would shift by whatever
    /// CL holds.
    /// </summary>
    private void RequireCl(MInstr i, int operand)
    {
        if (i.Operands[operand] is not MReg { Id: (int)Gpr.Ecx })
        {
            throw new InvalidOperationException($"{_m.Source.Name}: {i.Op} count must be CL, not {i.Operands[operand]}");
        }
    }

    private void EncodeMov(MInstr i)
    {
        int w = i.Width;
        MOperand dst = i.Operands[0];
        MOperand src = i.Operands[1];
        switch (src)
        {
            case MImm imm when dst is MReg r:
                _out.Add((byte)((w == 1 ? 0xB0 : 0xB8) + r.Id));
                Imm(imm, w);
                break;
            case MImm imm:
                B(Wide(0xC7, w));
                ModRM(0, dst);
                Imm(imm, w);
                break;
            case MReg:
                B(Wide(0x89, w));
                ModRM(Reg(src), dst);
                break;
            default:
                B(Wide(0x8B, w));
                ModRM(Reg(dst), src);
                break;
        }
    }

    private void EncodePrologue()
    {
        B(0x55);        // push ebp
        B(0x89, 0xE5);  // mov ebp, esp
        int n = _m.Frame.Size;
        if (n > 0)
        {
            if (n <= 127)
            {
                B(0x83, 0xEC, (byte)n);
            }
            else
            {
                B(0x81, 0xEC);
                D32(n);
            }
        }
        foreach (Gpr g in _m.SavedRegs)
        {
            _out.Add((byte)(0x50 + (int)g));
        }
    }

    private void EncodeEpilogue()
    {
        for (int k = _m.SavedRegs.Count - 1; k >= 0; k--)
        {
            _out.Add((byte)(0x58 + (int)_m.SavedRegs[k]));
        }
        B(0xC9, 0xC3);  // leave; ret
    }

    /// <summary>Encode a local jump; returns true if it had to be widened this pass.</summary>
    private bool EncodeJump(MInstr i, MBlock? next)
    {
        MBlock target = ((MLabel)i.Operands[0]).Target;
        if (i.Op == MOp.Jmp && ReferenceEquals(target, next))
        {
            return false;
        }
        bool isLong = _long.Contains(i);
        bool widened = false;
        int size = i.Op == MOp.Jmp ? (isLong ? 5 : 2) : (isLong ? 6 : 2);
        int disp = target.Offset - (Here + size);
        if (_widen && !isLong && disp is < -128 or > 127)
        {
            // Offsets of blocks after this one are from the previous pass
            // and may still shrink relative to here; on a backward jump
            // they are exact. Either way, widening is safe and monotone.
            _long.Add(i);
            isLong = true;
            widened = true;
            size += i.Op == MOp.Jmp ? 3 : 4;
            disp = target.Offset - (Here + size);
        }
        if (i.Op == MOp.Jmp)
        {
            if (isLong)
            {
                B(0xE9);
                D32(disp);
            }
            else
            {
                B(0xEB, (byte)disp);
            }
        }
        else if (isLong)
        {
            B(0x0F, (byte)(0x80 + (int)i.Cond));
            D32(disp);
        }
        else
        {
            B((byte)(0x70 + (int)i.Cond), (byte)disp);
        }
        return widened;
    }

    /// <summary>
    /// Padding that executes as nothing on a 486: mov esi,esi and
    /// lea esi,[esi+0] in its various widths, as GAS emits for i486
    /// targets. The 0F 1F multi-byte nop is Pentium Pro and later and is
    /// not used.
    /// </summary>
    public static void Nops(List<byte> into, int count)
    {
        while (count > 0)
        {
            byte[] fill = count switch
            {
                1 => new byte[] { 0x90 },
                2 => new byte[] { 0x89, 0xF6 },
                3 => new byte[] { 0x8D, 0x76, 0x00 },
                4 => new byte[] { 0x8D, 0x74, 0x26, 0x00 },
                5 => new byte[] { 0x90, 0x8D, 0x74, 0x26, 0x00 },
                6 => new byte[] { 0x8D, 0xB6, 0x00, 0x00, 0x00, 0x00 },
                _ => new byte[] { 0x8D, 0xB4, 0x26, 0x00, 0x00, 0x00, 0x00 },
            };
            into.AddRange(fill);
            count -= fill.Length;
        }
    }

    private string TableSymbol() => $".L{_m.Source.Name}.table{Tables.Count}";
}
