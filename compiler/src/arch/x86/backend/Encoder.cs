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
    private readonly ChunkedBytes _out;
    private readonly List<Relocation> _pending = new();
    private MFunction _m = null!;
    private int _funcStart;
    private readonly HashSet<MInstr> _long = new();

    /// <summary>
    /// The blocks a backward jump of the function goes to -- its loops' heads
    /// -- which on the K7 and the K8 start at a sixteen-byte line (LoopAlign).
    /// Empty for every other processor, and under --opt-size.
    /// </summary>
    private readonly HashSet<MBlock> _loopHeads = new(ReferenceEqualityComparer.Instance);

    /// <summary>--opt-size: no loop head padded (LoopAlign), the bytes kept.</summary>
    public bool OptimizeSize { get; init; }
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

    public Encoder(Section text, bool optimizeSize = false)
    {
        _text = text;
        _out = text.Bytes;
        OptimizeSize = optimizeSize;
    }

    public void ReleaseFunction()
    {
        _m = null!;
        _long.Clear(); Tables.Clear(); CallSites.Clear(); Lines.Clear(); _pending.Clear();
    }

    /// <summary>Encode a function at the current end of the section; returns its size.</summary>
    public int Encode(MFunction m)
    {
        Audit(m);
        _m = m;
        _funcStart = _out.Count;
        _long.Clear();
        _loopHeads.Clear();
        if (CpuModel.Athlon && !OptimizeSize)
        {
            // A JUMP TO ITS OWN BLOCK OR ONE BEFORE IT: a loop's latch, and its
            // target the head every lap starts at.
            Dictionary<MBlock, int> at = new(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < m.Blocks.Count; k++) at[m.Blocks[k]] = k;
            for (int k = 0; k < m.Blocks.Count; k++)
                foreach (MInstr i in m.Blocks[k].Instrs)
                    if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands.Count > 0 && i.Operands[0] is MLabel label
                        && at.TryGetValue(label.Target, out int to) && to <= k && to > 0)
                        _loopHeads.Add(label.Target);
        }

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
        // AND UNTIL THE LAYOUT HOLDS where loop heads are padded: a pad is what
        // the head's offset leaves of its line, so code before it that grew
        // can shrink it, and a pass that widened nothing may still have moved
        // the blocks after the pad from where the forward jumps took them to
        // be. With the jumps' sizes settled every pass lays out the same, so
        // one more pass than the widening needed is the end of it.
        bool grew, moved;
        int passes = 0;
        do
        {
            int[]? before = _loopHeads.Count == 0 ? null : m.Blocks.Select(b => b.Offset).ToArray();
            _pending.Clear();
            _out.Truncate(_funcStart);
            grew = Pass();
            moved = before is not null && !before.SequenceEqual(m.Blocks.Select(b => b.Offset));
            if (_loopHeads.Count > 0 && ++passes > 64) throw new InvalidOperationException($"{m.Source.Name}: the layout of its loop heads did not settle");
        }
        while (grew || moved);
        _text.Relocs.AddRange(_pending);
        return _out.Count - _funcStart;
    }

    /// <summary>
    /// THE EMITTED-CODE AUDIT: every instruction of the function against what
    /// --cpu permits (Isa), once, before any byte of it is written. Whatever
    /// chose an instruction -- selection, a later pass, a unit regenerated
    /// for the link -- one the profile lacks stops the compile here, naming
    /// the function, the instruction and what it needs; nothing a 486 cannot
    /// run reaches a 486 image unless the source asked for it after its own
    /// processor check (MInstr.Probed).
    ///
    /// The packed registers have a second rule: they are the x87's, and only
    /// code selected with the hosted ABI's leave (MFunction.AutomaticPacked),
    /// or inside a packed block, between its save of the thread's state and
    /// its give-back (MInstr.OwnsState), may use them, whatever the
    /// processor has.
    /// </summary>
    private static void Audit(MFunction m)
    {
        X86Cpu profile = Target.Current.X86Profile;
        foreach (MBlock block in m.Blocks)
        foreach (MInstr i in block.Instrs)
        {
            X86Feature missing = Isa.Requires(i) & ~profile.Features;
            if (missing != X86Feature.None && !i.Probed)
                throw new InvalidOperationException(
                    $"{m.Source.Name}: {i.Op}{At(i)} needs {Isa.Describe(missing)}, which --cpu={profile.Name} does not permit");
            if (!m.AutomaticPacked && i.OwnsState == 0 && Isa.UsesPackedState(i.Op))
                throw new InvalidOperationException(
                    $"{m.Source.Name}: {i.Op}{At(i)} uses the MMX registers in code that does not own the x87 state");
            if (!m.SseState && i.OwnsState != 2 && Isa.UsesSseState(i.Op))
                throw new InvalidOperationException(
                    $"{m.Source.Name}: {i.Op}{At(i)} uses SSE in code that runs without an operating system to enable it");
        }
    }

    private static string At(MInstr i) => i.Line > 0 ? $" (line {i.Line})" : "";

    private bool Pass()
    {
        bool grew = false;
        Tables.Clear();
        CallSites.Clear();
        Lines.Clear();
        for (int b = 0; b < _m.Blocks.Count; b++)
        {
            MBlock block = _m.Blocks[b];
            if (_loopHeads.Contains(block)) LoopAlign();
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

    private static int Reg(MOperand o) => ((MReg)o).Id is var id && id < 8 ? id
        : throw new InvalidOperationException($"register v{id} left unallocated (EBP's value is made EBP by FramePointerFree)");

    /// <summary>An XMM register's hardware number.</summary>
    private static int Xmm(MOperand o) => o is MReg { IsXmm: true } r ? r.XmmNumber
        : throw new InvalidOperationException($"{o} is not an XMM register (one left unallocated?)");

    /// <summary>ModRM for a register field and an r/m that is an XMM register or memory.</summary>
    private void XmmRM(int reg, MOperand rm)
    {
        if (rm is MReg { IsXmm: true } r) _out.Add((byte)(0xC0 | ((reg & 7) << 3) | r.XmmNumber));
        else ModRM(reg, rm);
    }

    /// <summary>A scalar instruction's prefix: F3 for a float, F2 for a double.</summary>
    private static byte Scalar(MInstr i) => i.Width == 8 ? (byte)0xF2 : (byte)0xF3;

    /// <summary>
    /// ModRM, SIB and displacement for a register field and an r/m operand.
    /// [EBP] with no displacement does not exist (that encoding means
    /// [disp32]), and [ESP] always needs a SIB byte; both are handled here so
    /// nothing else has to know.
    /// </summary>
    /// <summary>A packed instruction's register (mmN or xmmN) named at operand `k` (an MImm), or 0 -- mm0, xmm0 -- where it names none.</summary>
    private static int Packed(MInstr i, int k) => i.Operands.Count > k && i.Operands[k] is MImm { Value: var n } ? (int)n : 0;

    /// <summary>A packed instruction's source: memory, or a register (an MImm: mmS, xmmS) in r/m, with `reg` the destination's.</summary>
    private void PackedSource(MOperand source, int reg)
    {
        if (source is MImm { Value: var s }) B((byte)(0xC0 | (reg << 3) | (int)s));
        else ModRM(reg, source);
    }

    private void ModRM(int reg, MOperand rm)
    {
        reg &= 7;
        if (rm is MReg { Id: >= 8 } or MMem { Base.Id: >= 8 } or MMem { Index.Id: >= 8 })
            throw new InvalidOperationException("a register left unallocated (EBP's value is made EBP by FramePointerFree)");
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
        // [ESI] ALONE IS VECTOR DECODED ON THE K6 (AMD 21924, "Avoid using
        // the [ESI] addressing mode"): written [ESI+0] it decodes short, a
        // byte longer.
        else if (m.Disp == 0 && baseId != (int)Gpr.Ebp && !(baseId == (int)Gpr.Esi && !sib && CpuModel.K6))
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
            case MOp.Inc:
            case MOp.Dec:
                // A register's in one byte (40h+r, 48h+r: no long mode here);
                // memory's FE/FF /0, /1.
                if (i.Operands[0] is MReg incReg && w == 4) B((byte)((i.Op == MOp.Inc ? 0x40 : 0x48) + (incReg.Id & 7)));
                else { B(Wide(0xFF, w)); ModRM(i.Op == MOp.Inc ? 0 : 1, i.Operands[0]); }
                break;
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
            case MOp.Pushfd:
                B(0x9C);
                break;
            case MOp.Popfd:
                B(0x9D);
                break;
            case MOp.Setcc:
                B(0x0F, (byte)(0x90 + (int)i.Cond));
                ModRM(0, i.Operands[0]);
                break;
            case MOp.Cmov:
                // An undefined opcode on a Pentium or a K6: the audit has
                // refused it for a profile without CMOV (Isa), as MMX is for
                // one without MMX.
                if (i.Width != 4 || i.Operands[0] is not MReg)
                    throw new InvalidOperationException("CMOV takes a register destination");
                B(0x0F, (byte)(0x40 + (int)i.Cond));
                ModRM(Reg(i.Operands[0]), i.Operands[1]);
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
            case MOp.CallKeepInd:
            case MOp.CallKeepEaxInd:
                B(0xFF);
                ModRM(2, i.Operands[0]);
                break;
            case MOp.CallKeep:
            case MOp.CallKeepEax:
            {
                MImm stub = (MImm)i.Operands[0];
                B(0xE8);
                _pending.Add(new Relocation(_out.Count, stub.Symbol!, stub.Value - 4, RelocKind.Rel32));
                D32(0);
                break;
            }
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
                // movq mmN, m64 / movq m64, mmN: mm0 unless a register is named.
                if (i.Operands.Count is < 1 or > 2 || i.Operands[0] is not MMem)
                    throw new InvalidOperationException("MMX memory move requires memory");
                B(0x0f, i.Op == MOp.MmxLoad ? (byte)0x6f : (byte)0x7f); ModRM(Packed(i, 1), i.Operands[0]);
                break;
            case MOp.MmxLoadD:
            case MOp.MmxStoreD:
                // movd mmN, m32 / movd m32, mmN: mm0 unless a register is named.
                if (i.Operands.Count is < 1 or > 2 || i.Operands[0] is not MMem)
                    throw new InvalidOperationException("MOVD requires memory");
                B(0x0f, i.Op == MOp.MmxLoadD ? (byte)0x6e : (byte)0x7e); ModRM(Packed(i, 1), i.Operands[0]); break;
            case MOp.MmxDuplicateLowWords:
                B(0x0f, 0x61, 0xc0); break;
            case MOp.MmxWidenUnsignedWords:
                B(0x0f, 0xef, 0xc9, 0x0f, 0x61, 0xc1); break;
            case MOp.ThreeDNowIntToFloat: case MOp.ThreeDNowShortToFloat:
                B(0x0f, 0x0f, 0xc0, i.Op == MOp.ThreeDNowIntToFloat ? (byte)0x0d : (byte)0x0c); break;
            case MOp.MmxSaveToTwo:
                B(0x0f, 0x6f, 0xd0); break;
            case MOp.ThreeDNowAddFromTwo: case MOp.ThreeDNowSubFromTwo: case MOp.ThreeDNowMulFromTwo: case MOp.ThreeDNowFloatToInt:
                B(0x0f, 0x0f, i.Op == MOp.ThreeDNowFloatToInt ? (byte)0xc0 : (byte)0xc2,
                    i.Op switch { MOp.ThreeDNowAddFromTwo => (byte)0x9e, MOp.ThreeDNowSubFromTwo => (byte)0xaa,
                        MOp.ThreeDNowMulFromTwo => (byte)0xb4, _ => (byte)0x1d }); break;
            case MOp.MmxZero:
                B(0x0f, 0xef, 0xc0); break;
            case MOp.MmxAddB: case MOp.MmxAddW: case MOp.MmxAddD:
            case MOp.MmxEqualB: case MOp.MmxEqualW: case MOp.MmxEqualD:
            case MOp.MmxGreaterB: case MOp.MmxGreaterW: case MOp.MmxGreaterD:
            case MOp.MmxSubB: case MOp.MmxSubW: case MOp.MmxSubD:
            case MOp.MmxAnd: case MOp.MmxOr: case MOp.MmxXor: case MOp.MmxMulW: case MOp.MmxMulHighW: case MOp.MmxMultiplyAddW:
                if (i.Operands.Count != 1 || i.Operands[0] is not MMem)
                    throw new InvalidOperationException("Packed arithmetic requires memory");
                B(0x0f, i.Op switch {
                    MOp.MmxAddB => (byte)0xfc, MOp.MmxAddW => (byte)0xfd, MOp.MmxAddD => (byte)0xfe,
                    MOp.MmxEqualB => (byte)0x74, MOp.MmxEqualW => (byte)0x75, MOp.MmxEqualD => (byte)0x76,
                    MOp.MmxGreaterB => (byte)0x64, MOp.MmxGreaterW => (byte)0x65, MOp.MmxGreaterD => (byte)0x66,
                    MOp.MmxSubB => (byte)0xf8, MOp.MmxSubW => (byte)0xf9, MOp.MmxSubD => (byte)0xfa,
                    MOp.MmxAnd => (byte)0xdb, MOp.MmxOr => (byte)0xeb, MOp.MmxXor => (byte)0xef,
                    MOp.MmxMulHighW => (byte)0xe5, MOp.MmxMultiplyAddW => (byte)0xf5, _ => (byte)0xd5 });
                ModRM(0, i.Operands[0]); break;
            case MOp.ThreeDNowAverageB: case MOp.ThreeDNowMulRoundW:
                if (i.Operands.Count != 1 || i.Operands[0] is not MMem)
                    throw new InvalidOperationException("3DNow packed arithmetic requires memory");
                B(0x0f, 0x0f); ModRM(0, i.Operands[0]); B(i.Op == MOp.ThreeDNowAverageB ? (byte)0xbf : (byte)0xb7); break;
            case MOp.MmxShlW: case MOp.MmxShlD: case MOp.MmxShrW: case MOp.MmxShrD: case MOp.MmxSarW: case MOp.MmxSarD:
                if (i.Operands.Count != 1 || i.Operands[0] is not MImm { IsPlain: true, Value: >= 0 and <= 31 } shift)
                    throw new InvalidOperationException("Packed shift requires a normalized C# shift count");
                B(0x0f, i.Op is MOp.MmxShlW or MOp.MmxShrW or MOp.MmxSarW ? (byte)0x71 : (byte)0x72);
                B(i.Op is MOp.MmxShlW or MOp.MmxShlD ? (byte)0xf0 : i.Op is MOp.MmxShrW or MOp.MmxShrD ? (byte)0xd0 : (byte)0xe0);
                B((byte)shift.Value); break;
            case MOp.MmxOp:
                // mmD op= m64 or mmS: 0F xx /r, mmD (mm0 unless named) the register.
                B(0x0f, (byte)((MImm)i.Operands[0]).Value); PackedSource(i.Operands[1], Packed(i, 2)); break;
            case MOp.MmxShiftImm:
                // 0F 71/72/73 /digit ib, mmD (mm0 unless named) the operand.
                B(0x0f, (byte)((MImm)i.Operands[0]).Value, (byte)(0xC0 | (int)(((MImm)i.Operands[1]).Value << 3) | Packed(i, 3)), (byte)((MImm)i.Operands[2]).Value); break;
            case MOp.MmxShuffleW:
                B(0x0f, 0x70); PackedSource(i.Operands[0], Packed(i, 2)); B((byte)((MImm)i.Operands[1]).Value); break;
            case MOp.MmxExtractW:
                // pextrw r32, mmS, ib: 0F C5 /r, the register in reg.
                B(0x0f, 0xc5, (byte)(0xC0 | (Reg(i.Operands[0]) << 3) | Packed(i, 2)), (byte)((MImm)i.Operands[1]).Value); break;
            case MOp.MmxInsertW:
                // pinsrw mmD, r32, ib: 0F C4 /r, mmD in reg, the register in r/m.
                B(0x0f, 0xc4, (byte)(0xC0 | (Packed(i, 2) << 3) | Reg(i.Operands[0])), (byte)((MImm)i.Operands[1]).Value); break;
            case MOp.XmmLoad:
                // movups xmmN, m128: 0F 10 /r.
                B(0x0f, 0x10); ModRM((int)((MImm)i.Operands[0]).Value, i.Operands[1]); break;
            case MOp.XmmStore:
                B(0x0f, 0x11); ModRM(Packed(i, 1), i.Operands[0]); break;
            case MOp.XmmLoadLow:
                // movlps xmmN, m64: 0F 12 /r.
                B(0x0f, 0x12); ModRM((int)((MImm)i.Operands[0]).Value, i.Operands[1]); break;
            case MOp.XmmStoreLow:
                B(0x0f, 0x13); ModRM(Packed(i, 1), i.Operands[0]); break;
            case MOp.XmmOpRR:
            {
                // [prefix] 0F op /r: xmmD the register, xmmS or memory the r/m;
                // the immediate where there is one.
                long prefix = ((MImm)i.Operands[0]).Value;
                if (prefix != 0) B((byte)prefix);
                B(0x0f, (byte)((MImm)i.Operands[1]).Value);
                PackedSource(i.Operands[3], (int)((MImm)i.Operands[2]).Value);
                if (i.Operands.Count > 4) B((byte)((MImm)i.Operands[4]).Value);
                break;
            }
            case MOp.XmmOp:
            {
                // [prefix] 0F op /r with xmm0 the register and xmm1 the r/m,
                // and the immediate where there is one.
                long prefix = ((MImm)i.Operands[0]).Value;
                if (prefix != 0) B((byte)prefix);
                B(0x0f, (byte)((MImm)i.Operands[1]).Value, 0xC1);
                if (i.Operands.Count > 2) B((byte)((MImm)i.Operands[2]).Value);
                break;
            }
            case MOp.XmmShiftImm:
                B(0x66, 0x0f, (byte)((MImm)i.Operands[0]).Value, (byte)(0xC0 | (int)(((MImm)i.Operands[1]).Value << 3) | Packed(i, 3)), (byte)((MImm)i.Operands[2]).Value); break;
            case MOp.XmmExtractW:
                B(0x66, 0x0f, 0xc5, (byte)(0xC0 | (Reg(i.Operands[0]) << 3) | Packed(i, 2)), (byte)((MImm)i.Operands[1]).Value); break;
            case MOp.XmmInsertW:
                B(0x66, 0x0f, 0xc4, (byte)(0xC0 | (Packed(i, 2) << 3) | Reg(i.Operands[0])), (byte)((MImm)i.Operands[1]).Value); break;
            case MOp.ThreeDNowOp:
                B(0x0f, 0x0f); PackedSource(i.Operands[1], Packed(i, 2)); B((byte)((MImm)i.Operands[0]).Value); break;
            case MOp.Emms:
                B(0x0f, 0x77); break;
            case MOp.Femms:
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
            case MOp.Fucomipp:
                if (!Target.Current.X86Profile.FloatCmov) throw new InvalidOperationException("FUCOMIP requires a Pentium Pro x87");
                B(0xDF, 0xE9, 0xDD, 0xD8);
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
            case MOp.Cvttss2si:
            case MOp.Cvttsd2si:
                // F3 0F 2C /r is cvttss2si, F2 0F 2C /r cvttsd2si; the audit
                // has checked the profile has SSE or SSE2 (Isa). The source
                // is memory, or an XMM register of the scalar class.
                if (i.Operands.Count != 2 || i.Operands[0] is not MReg || i.Operands[1] is not (MMem or MReg { IsXmm: true }))
                    throw new InvalidOperationException("Truncating conversion takes a register and memory or an XMM register");
                B(i.Op == MOp.Cvttss2si ? (byte)0xF3 : (byte)0xF2, 0x0F, 0x2C);
                XmmRM(Reg(i.Operands[0]), i.Operands[1]);
                break;
            // ---- scalar SSE (Select.Sse) -------------------------------------
            case MOp.MovF:
                if (i.Operands[0] is MReg { IsXmm: true } && i.Operands[1] is MReg { IsXmm: true })
                {
                    // movaps xmm, xmm: the whole register, no merge with what
                    // the destination held -- movss between two registers keeps
                    // its top, a dependency on whatever was there.
                    B(0x0F, 0x28);
                    XmmRM(Xmm(i.Operands[0]), i.Operands[1]);
                }
                else if (i.Operands[0] is MReg { IsXmm: true })
                {
                    B(Scalar(i), 0x0F, 0x10);
                    ModRM(Xmm(i.Operands[0]), i.Operands[1]);
                }
                else
                {
                    B(Scalar(i), 0x0F, 0x11);
                    ModRM(Xmm(i.Operands[1]), i.Operands[0]);
                }
                break;
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.SqrtF:
                B(Scalar(i), 0x0F, i.Op switch { MOp.AddF => (byte)0x58, MOp.MulF => (byte)0x59, MOp.SubF => (byte)0x5C, MOp.DivF => (byte)0x5E, _ => (byte)0x51 });
                XmmRM(Xmm(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.UcomiF:
                // ucomiss 0F 2E, ucomisd 66 0F 2E.
                if (i.Width == 8) B(0x66);
                B(0x0F, 0x2E);
                XmmRM(Xmm(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.CvtFToF:
                // To a double: cvtss2sd (F3); to a float: cvtsd2ss (F2).
                B(i.Width == 8 ? (byte)0xF3 : (byte)0xF2, 0x0F, 0x5A);
                XmmRM(Xmm(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.CvtIntToF:
                B(Scalar(i), 0x0F, 0x2A);
                ModRM(Xmm(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.XorPs:
                B(0x0F, 0x57);
                XmmRM(Xmm(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.MovdToX:
                B(0x66, 0x0F, 0x6E);
                ModRM(Xmm(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.MovdFromX:
                B(0x66, 0x0F, 0x7E);
                ModRM(Xmm(i.Operands[1]), i.Operands[0]);
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
            case MOp.CompilerFence:
            case MOp.SaveRegs:
                break;
            case MOp.SpillRegs:
            {
                // After `push ebp; mov ebp, esp; sub esp, Size` the prologue
                // pushes SavedRegs in order: the k-th at EBP - Size - 4(k+1).
                if (_m.FramePointerFree) throw new InvalidOperationException($"{_m.Source.Name}: Sys.SpillRegisters in a function without a frame pointer");
                MReg at = (MReg)i.Operands[0];
                Gpr[] kept = { Gpr.Ebx, Gpr.Esi, Gpr.Edi };
                for (int k = 0; k < kept.Length; k++)
                {
                    int saved = _m.SavedRegs.IndexOf(kept[k]);
                    if (saved >= 0)
                    {
                        B(0xFF); ModRM(6, MMem.Frame(-_m.Frame.Size - 4 * (saved + 1)));   // push dword [ebp-...]
                        B(0x8F); ModRM(0, new MMem(at, 4 * k));                          // pop dword [at+4k]
                    }
                    else
                    {
                        B(0x89); ModRM((int)kept[k], new MMem(at, 4 * k));               // mov [at+4k], reg
                    }
                }
                break;
            }
            case MOp.Pause:
                // rep nop: a plain nop to a 486, the spin hint to anything newer.
                B(0xF3, 0x90);
                break;
            case MOp.SyscallTrap:
                B(0xCD, i.Operands.Count > 0 ? (byte)((MImm)i.Operands[0]).Value : (byte)0x80);
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
            case MOp.Rdmsr: B(0x0F, 0x32); break;
            case MOp.Wrmsr: B(0x0F, 0x30); break;
            case MOp.Cpuid: B(0x0F, 0xA2); break;
            case MOp.Rdtsc: B(0x0F, 0x31); break;
            case MOp.WriteFlags:
                // 50+r push r32, then 9D popfd.
                _out.Add((byte)(0x50 + ((MReg)i.Operands[0]).Id));
                B(0x9D);
                break;
            case MOp.Bsf:
                B(0x0F, 0xBC);
                ModRM(Reg(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.Bsr:
                B(0x0F, 0xBD);
                ModRM(Reg(i.Operands[0]), i.Operands[1]);
                break;
            case MOp.MmxStoreNt:
                B(0x0F, 0xE7);
                ModRM(Packed(i, 1), i.Operands[0]);
                break;
            case MOp.PrefetchNta:
                B(0x0F, 0x18);
                ModRM(0, i.Operands[0]);
                break;
            case MOp.PrefetchLine:
            case MOp.PrefetchWrite:
                // 0F 0D /0 prefetch, /1 prefetchw.
                B(0x0F, 0x0D);
                ModRM(i.Op == MOp.PrefetchWrite ? 1 : 0, i.Operands[0]);
                break;
            case MOp.XmmStoreNt:
                B(0x66, 0x0F, 0xE7);
                ModRM(Packed(i, 1), i.Operands[0]);
                break;
            case MOp.Sfence:
                B(0x0F, 0xAE, 0xF8);
                break;
            case MOp.MmxFindW4:
            {
                int r = Reg(i.Operands[0]);
                B(0x0F, 0x6F); ModRM(0, i.Operands[1]);         // movq mm0, m
                B(0x0F, 0x75); ModRM(0, i.Operands[2]);         // pcmpeqw mm0, p
                B(0x0F, 0x63, 0xC0);                            // packsswb mm0, mm0
                B(0x0F, 0x7E); _out.Add((byte)(0xC0 | r));      // movd r32, mm0
                break;
            }
            case MOp.MmxFindW8:
            {
                int r = Reg(i.Operands[0]);
                MMem at = (MMem)i.Operands[1];
                MMem next = new(at.Base, at.Disp + 8) { Index = at.Index, Scale = at.Scale, Symbol = at.Symbol, Reloc = at.Reloc, Label = at.Label };
                B(0x0F, 0x6F); ModRM(0, at);                    // movq mm0, m
                B(0x0F, 0x6F); ModRM(1, next);                  // movq mm1, m + 8
                B(0x0F, 0x75); ModRM(0, i.Operands[2]);         // pcmpeqw mm0, p
                B(0x0F, 0x75); ModRM(1, i.Operands[2]);         // pcmpeqw mm1, p
                B(0x0F, 0x63, 0xC1);                            // packsswb mm0, mm1
                B(0x0F, 0xD7); _out.Add((byte)(0xC0 | (r << 3))); // pmovmskb r32, mm0
                break;
            }
            case MOp.MmxEqual8:
            {
                int r = Reg(i.Operands[0]);
                B(0x0F, 0x6F); ModRM(0, i.Operands[1]);         // movq mm0, a
                B(0x0F, 0x76); ModRM(0, i.Operands[2]);         // pcmpeqd mm0, b
                B(0x0F, 0x6B, 0xC0);                            // packssdw mm0, mm0
                B(0x0F, 0x7E); _out.Add((byte)(0xC0 | r));      // movd r32, mm0
                break;
            }
            case MOp.PackedSaveX87:
                B(0xD9, 0x30);                                  // fnstenv [eax]
                B(0x66, 0x83, 0x78, 0x08, 0xFF);                // cmp word [eax+8], 0xFFFF: the tag word
                B(0x75, 0x09);                                  // jne whole
                B(0xC7, 0x40, 0x6C, 0x00, 0x00, 0x00, 0x00);    // mov dword [eax+108], 0
                B(0xEB, 0x0B);                                  // jmp control
                B(0xD9, 0x20);                                  // whole: fldenv [eax]
                B(0xDD, 0x30);                                  // fnsave [eax]
                B(0xC7, 0x40, 0x6C, 0x01, 0x00, 0x00, 0x00);    // mov dword [eax+108], 1
                // control: the program's own control word, a double's 53
                // bits (MachineIntrinsics.DoublePrecision), not FNSAVE's or
                // FNSTENV's -- extended precision rounded every double twice.
                B(0x68, 0x7F, 0x02, 0x00, 0x00);                // push 0x027F
                B(0xD9, 0x2C, 0x24);                            // fldcw [esp]
                B(0x83, 0xC4, 0x04);                            // add esp, 4
                break;
            case MOp.PackedRestoreX87:
                B(0x83, 0x78, 0x6C, 0x00);                      // cmp dword [eax+108], 0
                B(0x75, 0x04);                                  // jne whole
                B(0xD9, 0x20);                                  // fldenv [eax]
                B(0xEB, 0x02);                                  // jmp done
                B(0xDD, 0x20);                                  // whole: frstor [eax]
                break;
            case MOp.PackedSaveFxsr:
                B(0x0F, 0xAE, 0x00);                            // fxsave [eax]
                B(0xDB, 0xE3);                                  // fninit
                B(0xC7, 0x80, 0x00, 0x02, 0x00, 0x00, 0x80, 0x1F, 0x00, 0x00); // mov dword [eax+512], 0x1F80
                B(0x0F, 0xAE, 0x90, 0x00, 0x02, 0x00, 0x00);    // ldmxcsr [eax+512]
                // The program's control word, as the x87's save loads it:
                // FNINIT's is extended precision.
                B(0x68, 0x7F, 0x02, 0x00, 0x00);                // push 0x027F
                B(0xD9, 0x2C, 0x24);                            // fldcw [esp]
                B(0x83, 0xC4, 0x04);                            // add esp, 4
                break;
            case MOp.PackedRestoreFxsr:
                B(0x0F, 0xAE, 0x08);                            // fxrstor [eax]
                break;
            case MOp.Cmpxchg8b:
                // 0F C7 /1, its lock prefix with the others' (MInstr.Lock).
                B(0x0F, 0xC7);
                ModRM(1, i.Operands[0]);
                break;
            case MOp.ReadFlags:
                // 9C pushfd, then 58+r pop r32.
                B(0x9C);
                _out.Add((byte)(0x58 + ((MReg)i.Operands[0]).Id));
                break;
            case MOp.Ltr:
                B(0x0F, 0x00);
                ModRM(3, i.Operands[0]);
                break;
            case MOp.LoadCs:
            {
                // push r; call $+5; add dword [esp], 5; retf -- the far
                // return lands on the instruction after the retf.
                int r = ((MReg)i.Operands[0]).Id;
                _out.Add((byte)(0x50 + r));                 // push r
                B(0xE8, 0x00, 0x00, 0x00, 0x00);            // call $+5
                B(0x83, 0x04, 0x24, 0x05);                  // add dword [esp], 5
                B(0xCB);                                    // retf
                break;
            }
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
            case MOp.GsLoadAt:
            case MOp.GsStoreAt:
            {
                // 65 8B /r (load) or 65 89 /r (store), mod 00 and r/m 101: a
                // disp32 with no base, taken in GS.
                bool load = i.Op == MOp.GsLoadAt;
                int r = ((MReg)i.Operands[load ? 0 : 1]).Id;
                B(0x65, load ? (byte)0x8B : (byte)0x89);
                _out.Add((byte)(0x05 | (r << 3)));
                Imm32((MImm)i.Operands[load ? 1 : 0]);
                break;
            }
            case MOp.GsLoadReg:
            case MOp.GsStoreReg:
            {
                // The same through a register: mod 01, r/m 100 and a SIB of
                // the register as base and no index, displacement 0, so that
                // every register, ESP and EBP included, may be the offset.
                bool load = i.Op == MOp.GsLoadReg;
                int r = ((MReg)i.Operands[load ? 0 : 1]).Id;
                int at = ((MReg)i.Operands[load ? 1 : 0]).Id;
                B(0x65, load ? (byte)0x8B : (byte)0x89);
                _out.Add((byte)(0x44 | (r << 3)));
                _out.Add((byte)(0x20 | at));
                _out.Add(0x00);
                break;
            }
            case MOp.FsLoad:
            {
                // 64 8B /r with mod 01, r/m 100 and a SIB of the register as
                // base and no index, displacement 0: [r] in FS for every
                // register, ESP's and EBP's encodings included.
                int r = ((MReg)i.Operands[0]).Id;
                B(0x64, 0x8B);
                _out.Add((byte)(0x44 | (r << 3)));
                _out.Add((byte)(0x20 | r));
                _out.Add(0x00);
                break;
            }
            case MOp.FsSehChain:
            case MOp.FsSehChainStore:
            {
                // 64 8B /r and 64 89 /r with mod 00, r/m 101: [disp32] in FS,
                // the displacement 0, where NT_TIB keeps ExceptionList.
                int r = ((MReg)i.Operands[0]).Id;
                B(0x64, i.Op == MOp.FsSehChain ? (byte)0x8B : (byte)0x89);
                _out.Add((byte)(0x05 | (r << 3)));
                B(0x00, 0x00, 0x00, 0x00);
                break;
            }
            case MOp.GetGs:
            {
                // xor r, r; then 66 8C /r, mov r16, gs: a 32-bit move from a
                // segment register leaves the upper half undefined before
                // the Pentium Pro, so only the low half is written.
                int r = ((MReg)i.Operands[0]).Id;
                B(0x31);
                _out.Add((byte)(0xC0 | (r << 3) | r));
                B(0x66, 0x8C);
                _out.Add((byte)(0xE8 | r));
                break;
            }
            case MOp.ReadCs:
            {
                // The same with CS, sreg 1: xor r, r; mov r16, cs.
                int r = ((MReg)i.Operands[0]).Id;
                B(0x31);
                _out.Add((byte)(0xC0 | (r << 3) | r));
                B(0x66, 0x8C);
                _out.Add((byte)(0xC8 | r));
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
        // mov ebp, esp: 89 E5, or 8B EC in a function that gives EBP a value
        // after (FramePointerFree) -- the mark a fault reads (Runtime.FrameAt)
        // to find the frame at ESP plus the frame's size and the pushes below.
        if (_m.FramePointerFree) B(0x8B, 0xEC);
        else B(0x89, 0xE5);
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
        if (_m.FramePointerFree)
        {
            // EBP holds a value, not the frame: ESP past the frame, then EBP back.
            int n = _m.Frame.Size;
            if (n is > 0 and <= 127) B(0x83, 0xC4, (byte)n);  // add esp, n
            else if (n > 127) { B(0x81, 0xC4); D32(n); }
            B(0x5D);  // pop ebp
        }
        else
        {
            // LEAVE EVEN ON A PENTIUM: `mov esp, ebp ; pop ebp` would pair with
            // nothing either -- the pop uses ESP the move has just written, an
            // address generation interlock (Agner Fog, example 4.8: `mov esp, ebp
            // / ret`) -- three clocks for LEAVE's three and a byte more.
            B(0xC9);
        }
        int pops = _m.Source.CalleePops;
        if (pops > 0)
        {
            B(0xC2);  // ret n: stdcall's callee takes its arguments off
            B((byte)pops, (byte)(pops >> 8));
            return;
        }
        B(0xC3);  // ret
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
    /// A LOOP'S HEAD AT A SIXTEEN-BYTE LINE on the K7 and the K8, which fetch
    /// and predecode an aligned sixteen bytes a clock (AMD 22007 and 25112,
    /// "Align branch targets"): a head near a line's end costs a fetch more on
    /// every lap. The pad is put before every head, and runs only where the
    /// block laid out before it falls into it -- the way into the loop, as a
    /// rule; behind a jump it is bytes nothing runs. AMD's K8 guide's NOP:
    /// 0x66 prefixes on 0x90, one decoded operation apiece, which the K7
    /// decodes as it does (it has no 0F 1F). Not under --opt-size.
    /// </summary>
    private void LoopAlign()
    {
        int pad = (16 - _out.Count % 16) % 16;
        while (pad > 0)
        {
            int run = Math.Min(pad, 4);
            for (int k = 1; k < run; k++) _out.Add(0x66);
            _out.Add(0x90);
            pad -= run;
        }
    }

    /// <summary>
    /// Padding that executes as nothing on a 486: mov esi,esi and
    /// lea esi,[esi+0] in its various widths, as GAS emits for i486
    /// targets. The 0F 1F multi-byte nop is Pentium Pro and later and is
    /// not used.
    /// </summary>
    public static void Nops(ChunkedBytes into, int count)
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
