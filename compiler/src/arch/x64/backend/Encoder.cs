#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X64;

/// <summary>
/// Machine instructions into bytes, for the text section of one object.
///
/// Per function the instructions are laid out until every branch has the
/// size it needs: all start short, the ones whose target turns out further
/// than a signed byte grow to their 32-bit form, and the layout is repeated
/// until nothing grows. Growing only makes distances longer, so it ends.
/// A final pass writes the bytes with every block where the layout put it.
///
/// Everything named -- a call target, a static, a string -- is reached
/// relative to the instruction (R_X86_64_PC32/PLT32); a block of the same
/// function is a displacement written in place.
/// </summary>
internal sealed class Encoder
{
    private readonly Section _text;
    private List<byte> _b = new();
    private readonly List<(int At, string Symbol, long Addend, RelocKind Kind)> _relocs = new();
    private readonly Dictionary<MInstr, bool> _long = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MInstr, (int End, MBlock Target)> _jumps = new(ReferenceEqualityComparer.Instance);
    private MFunction _m = null!;
    private int _frameBytes;
    private bool _final;

    public List<(MInstr Call, int Return)> CallSites { get; } = new();
    public List<(int Offset, int Line)> Lines { get; } = new();
    public List<(string Symbol, List<MBlock> Targets)> Tables { get; } = new();

    public Encoder(Section text) => _text = text;

    public void ReleaseFunction()
    {
        CallSites.Clear();
        Lines.Clear();
        Tables.Clear();
        _long.Clear();
        _jumps.Clear();
        _relocs.Clear();
    }

    /// <summary>Encode one function at the end of the text section; returns its size.</summary>
    public int Encode(MFunction m)
    {
        _m = m;
        int saved = m.SavedRegs.Count * 8;
        // RBP is sixteen-byte aligned after `push rbp`, so the frame below it
        // is a multiple of sixteen and RSP is aligned at every call.
        _frameBytes = (m.Frame.Size + saved + 15) & ~15;

        _final = false;
        while (true)
        {
            Pass();
            bool grew = false;
            foreach ((MInstr jump, (int end, MBlock target)) in _jumps)
            {
                if (!_long.GetValueOrDefault(jump))
                {
                    long disp = target.Offset - end;
                    if (disp is < -128 or > 127)
                    {
                        _long[jump] = true;
                        grew = true;
                    }
                }
            }
            if (!grew)
            {
                break;
            }
        }
        _final = true;
        Pass();

        int start = _text.Bytes.Count;
        _text.Bytes.AddRange(_b);
        foreach ((int at, string symbol, long addend, RelocKind kind) in _relocs)
        {
            _text.Relocs.Add(new Relocation(start + at, symbol, addend, kind));
        }
        return _b.Count;
    }

    private void Pass()
    {
        _b = new List<byte>();
        _relocs.Clear();
        _jumps.Clear();
        CallSites.Clear();
        Lines.Clear();
        Tables.Clear();
        int line = 0;
        foreach (MBlock block in _m.Blocks)
        {
            block.Offset = _b.Count;
            foreach (MInstr i in block.Instrs)
            {
                if (i.Line != 0 && i.Line != line)
                {
                    Lines.Add((_b.Count, i.Line));
                    line = i.Line;
                }
                Instr(i);
            }
        }
    }

    // ---- bytes -------------------------------------------------------------------------

    private void Byte(int v) => _b.Add((byte)v);

    private void Imm8(long v) => _b.Add((byte)v);

    private void Imm16(long v)
    {
        _b.Add((byte)v);
        _b.Add((byte)(v >> 8));
    }

    private void Imm32(long v)
    {
        for (int k = 0; k < 4; k++)
        {
            _b.Add((byte)(v >> (8 * k)));
        }
    }

    private void Imm64(long v)
    {
        for (int k = 0; k < 8; k++)
        {
            _b.Add((byte)(v >> (8 * k)));
        }
    }

    private void Patch32(int at, long v)
    {
        for (int k = 0; k < 4; k++)
        {
            _b[at + k] = (byte)(v >> (8 * k));
        }
    }

    // ---- prefixes and addressing ----------------------------------------------------------

    private static int Hw(MOperand o) => o is MReg r ? r.Hw : 0;

    /// <summary>
    /// The REX prefix, when one is needed: a 64-bit operation, a register
    /// numbered 8 or above in any field, or an 8-bit operation on SPL, BPL,
    /// SIL or DIL, whose low bytes are only reachable with a REX present.
    /// </summary>
    private void Rex(bool w, int reg, MOperand rm, bool byteRegs = false)
    {
        int rex = 0;
        if (w) rex |= 8;
        if ((reg & 8) != 0) rex |= 4;
        if (rm is MMem m)
        {
            if (m.Index is not null && (m.Index.Hw & 8) != 0) rex |= 2;
            if (m.Base is not null && (m.Base.Hw & 8) != 0) rex |= 1;
        }
        else if (rm is MReg r && (r.Hw & 8) != 0)
        {
            rex |= 1;
        }
        bool needsEmpty = byteRegs && ((reg is >= 4 and <= 7) || (rm is MReg br && !br.IsFloat && br.Hw is >= 4 and <= 7));
        if (rex != 0 || needsEmpty)
        {
            Byte(0x40 | rex);
        }
    }

    /// <summary>
    /// ModRM, then SIB and displacement as the operand needs. `tail` is how
    /// many immediate bytes follow the displacement, which a RIP-relative
    /// displacement must allow for: it is measured from the end of the
    /// instruction, not from the end of the displacement.
    /// </summary>
    private void ModRM(int reg, MOperand rm, int tail)
    {
        int r = reg & 7;
        if (rm is MReg rr)
        {
            Byte(0xC0 | (r << 3) | (rr.Hw & 7));
            return;
        }
        MMem m = (MMem)rm;
        if (m.Symbol is not null || m.Label is not null)
        {
            Byte((r << 3) | 5);
            int at = _b.Count;
            Imm32(0);
            if (m.Symbol is not null)
            {
                _relocs.Add((at, m.Symbol, m.Disp - 4 - tail, m.Got ? RelocKind.GotPcRel : RelocKind.Rel32));
            }
            else
            {
                // A block of this function: its offset less the end of the instruction.
                Patch32(at, m.Label!.Offset + m.Disp - (at + 4 + tail));
            }
            return;
        }
        if (m.Base is null && m.Index is null)
        {
            // [disp32]: in long mode this needs the SIB form with no base and
            // no index, since mod 00 r/m 101 means RIP-relative.
            Byte((r << 3) | 4);
            Byte(0x25);
            Imm32(m.Disp);
            return;
        }
        int disp = m.Disp;
        int mod;
        int baseLow = m.Base is null ? 5 : m.Base.Hw & 7;
        if (m.Base is null)
        {
            mod = 0;               // [index*scale + disp32] with SIB base 101
        }
        else if (disp == 0 && baseLow != 5)
        {
            mod = 0;               // RBP and R13 as a base always need a displacement
        }
        else if (disp is >= -128 and <= 127)
        {
            mod = 1;
        }
        else
        {
            mod = 2;
        }
        bool sib = m.Index is not null || baseLow == 4;
        if (sib)
        {
            Byte((mod << 6) | (r << 3) | 4);
            int scale = m.Scale switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => throw new InvalidOperationException($"scale {m.Scale}") };
            int index = m.Index is null ? 4 : m.Index.Hw & 7;
            if (m.Index is not null && m.Index.Hw == (int)Gpr.Rsp)
            {
                throw new InvalidOperationException("RSP cannot be an index");
            }
            Byte((scale << 6) | (index << 3) | baseLow);
        }
        else
        {
            Byte((mod << 6) | (r << 3) | baseLow);
        }
        if (m.Base is null || mod == 2)
        {
            Imm32(disp);
        }
        else if (mod == 1)
        {
            Imm8(disp);
        }
    }

    private void Segment(MOperand rm)
    {
        if (rm is MMem { Segment: not 0 } m)
        {
            Byte(m.Segment);
        }
    }

    /// <summary>
    /// An integer instruction with a ModRM: operand-size prefix for 16 bits,
    /// REX (with W for 64), the opcode bytes, then the operand.
    /// </summary>
    private void Op(int width, int reg, MOperand rm, int tail, params int[] opcode)
    {
        Segment(rm);
        if (width == 2)
        {
            Byte(0x66);
        }
        Rex(width == 8, reg, rm, width == 1);
        foreach (int b in opcode)
        {
            Byte(b);
        }
        ModRM(reg, rm, tail);
    }

    /// <summary>
    /// An SSE instruction: its mandatory prefix (66, F2 or F3) BEFORE the REX,
    /// then 0F and the opcode.
    /// </summary>
    private void Sse(int prefix, bool w, int reg, MOperand rm, int opcode)
    {
        Segment(rm);
        if (prefix != 0)
        {
            Byte(prefix);
        }
        Rex(w, reg, rm);
        Byte(0x0F);
        Byte(opcode);
        ModRM(reg, rm, 0);
    }

    private static int ScalarPrefix(int width) => width == 4 ? 0xF3 : 0xF2;

    // ---- instructions ------------------------------------------------------------------------

    private void Instr(MInstr i)
    {
        if (i.Lock)
        {
            Byte(0xF0);
        }
        int w = i.Width;
        switch (i.Op)
        {
            case MOp.Mov:
                Mov(i, w);
                break;
            case MOp.Movsx:
                // Width is the source's; the destination is 64 bits.
                if (w == 4)
                {
                    Op(8, Hw(i.Operands[0]), i.Operands[1], 0, 0x63);
                }
                else
                {
                    Rex(true, Hw(i.Operands[0]), i.Operands[1], w == 1);
                    Segment(i.Operands[1]);
                    Byte(0x0F);
                    Byte(w == 1 ? 0xBE : 0xBF);
                    ModRM(Hw(i.Operands[0]), i.Operands[1], 0);
                }
                break;
            case MOp.Movzx:
                // Width is the source's; the destination is 32 bits, which zero-extends to 64.
                Segment(i.Operands[1]);
                Rex(false, Hw(i.Operands[0]), i.Operands[1], w == 1);
                Byte(0x0F);
                Byte(w == 1 ? 0xB6 : 0xB7);
                ModRM(Hw(i.Operands[0]), i.Operands[1], 0);
                break;
            case MOp.Lea:
                Op(8, Hw(i.Operands[0]), i.Operands[1], 0, 0x8D);
                break;
            case MOp.Push:
                Push(i.Operands[0]);
                break;
            case MOp.Pop:
                if (i.Operands[0] is MReg pr)
                {
                    if ((pr.Hw & 8) != 0) Byte(0x41);
                    Byte(0x58 + (pr.Hw & 7));
                }
                else
                {
                    Op(4, 0, i.Operands[0], 0, 0x8F);
                }
                break;
            case MOp.Add: Alu(0, i, w); break;
            case MOp.Or: Alu(1, i, w); break;
            case MOp.Adc: Alu(2, i, w); break;
            case MOp.Sbb: Alu(3, i, w); break;
            case MOp.And: Alu(4, i, w); break;
            case MOp.Sub: Alu(5, i, w); break;
            case MOp.Xor: Alu(6, i, w); break;
            case MOp.Cmp: Alu(7, i, w); break;
            case MOp.Test:
                if (i.Operands[1] is MImm ti)
                {
                    Op(w, 0, i.Operands[0], w == 1 ? 1 : w == 2 ? 2 : 4, w == 1 ? 0xF6 : 0xF7);
                    if (w == 1) Imm8(ti.Value); else if (w == 2) Imm16(ti.Value); else Imm32(ti.Value);
                }
                else
                {
                    Op(w, Hw(i.Operands[1]), i.Operands[0], 0, w == 1 ? 0x84 : 0x85);
                }
                break;
            case MOp.Imul:
                Op(w, Hw(i.Operands[0]), i.Operands[1], 0, 0x0F, 0xAF);
                break;
            case MOp.Imul3:
            {
                MImm k = (MImm)i.Operands[2];
                if (k.FitsSbyte)
                {
                    Op(w, Hw(i.Operands[0]), i.Operands[1], 1, 0x6B);
                    Imm8(k.Value);
                }
                else
                {
                    Op(w, Hw(i.Operands[0]), i.Operands[1], 4, 0x69);
                    Imm32(k.Value);
                }
                break;
            }
            case MOp.MulWide: Op(w, 4, i.Operands[0], 0, 0xF7); break;
            case MOp.ImulWide: Op(w, 5, i.Operands[0], 0, 0xF7); break;
            case MOp.Div: Op(w, 6, i.Operands[0], 0, 0xF7); break;
            case MOp.Idiv: Op(w, 7, i.Operands[0], 0, 0xF7); break;
            case MOp.Neg: Op(w, 3, i.Operands[0], 0, w == 1 ? 0xF6 : 0xF7); break;
            case MOp.Not: Op(w, 2, i.Operands[0], 0, w == 1 ? 0xF6 : 0xF7); break;
            case MOp.Cwd:
                if (w == 8) Byte(0x48);
                Byte(0x99);
                break;
            case MOp.Bswap:
            {
                MReg r = i.Reg(0);
                int rex = (w == 8 ? 8 : 0) | ((r.Hw & 8) != 0 ? 1 : 0);
                if (rex != 0) Byte(0x40 | rex);
                Byte(0x0F);
                Byte(0xC8 + (r.Hw & 7));
                break;
            }
            case MOp.Rol: Shift(0, i, w); break;
            case MOp.Ror: Shift(1, i, w); break;
            case MOp.Shl: Shift(4, i, w); break;
            case MOp.Shr: Shift(5, i, w); break;
            case MOp.Sar: Shift(7, i, w); break;
            case MOp.Setcc:
                Op(1, 0, i.Operands[0], 0, 0x0F, 0x90 + (int)i.Cond);
                break;
            case MOp.Cmovcc:
                Op(w, Hw(i.Operands[0]), i.Operands[1], 0, 0x0F, 0x40 + (int)i.Cond);
                break;
            case MOp.Jmp:
                Jump(i, ((MLabel)i.Operands[0]).Target, -1);
                break;
            case MOp.Jcc:
                Jump(i, ((MLabel)i.Operands[0]).Target, (int)i.Cond);
                break;
            case MOp.JmpTable:
                JumpTable(i);
                break;
            case MOp.JmpInd:
                Op(4, 4, i.Operands[0], 0, 0xFF);
                break;
            case MOp.Call:
            {
                MImm target = (MImm)i.Operands[0];
                Byte(0xE8);
                int at = _b.Count;
                Imm32(0);
                _relocs.Add((at, target.Symbol!, target.Value - 4, RelocKind.Plt32));
                CallSites.Add((i, _b.Count));
                break;
            }
            case MOp.CallInd:
                Op(4, 2, i.Operands[0], 0, 0xFF);
                CallSites.Add((i, _b.Count));
                break;
            case MOp.Ret:
                Byte(0xC3);
                break;
            case MOp.Xchg:
            {
                // xchg r/m, r: the memory side first as the selector writes it.
                MOperand rm = i.Operands[0] is MMem ? i.Operands[0] : i.Operands[1];
                MOperand reg = i.Operands[0] is MMem ? i.Operands[1] : i.Operands[0];
                Op(w, Hw(reg), rm, 0, w == 1 ? 0x86 : 0x87);
                break;
            }
            case MOp.Xadd:
                Op(w, Hw(i.Operands[1]), i.Operands[0], 0, 0x0F, w == 1 ? 0xC0 : 0xC1);
                break;
            case MOp.Cmpxchg:
                Op(w, Hw(i.Operands[1]), i.Operands[0], 0, 0x0F, w == 1 ? 0xB0 : 0xB1);
                break;
            case MOp.Mfence:
                Byte(0x0F); Byte(0xAE); Byte(0xF0);
                break;
            case MOp.RepMovsb:
                Byte(0xF3); Byte(0xA4);
                break;
            case MOp.RepMovsq:
                Byte(0xF3); Byte(0x48); Byte(0xA5);
                break;
            case MOp.RepStosb:
                Byte(0xF3); Byte(0xAA);
                break;
            case MOp.RepStosq:
                Byte(0xF3); Byte(0x48); Byte(0xAB);
                break;

            // ---- SSE2 ----
            case MOp.MovF:
                if (i.Operands[0] is MMem)
                {
                    Sse(ScalarPrefix(w), false, Hw(i.Operands[1]), i.Operands[0], 0x11);
                }
                else
                {
                    Sse(ScalarPrefix(w), false, Hw(i.Operands[0]), i.Operands[1], 0x10);
                }
                break;
            case MOp.MovGx:
                if (i.Operands[0] is MReg { IsFloat: true } xd)
                {
                    // movd/movq xmm, r/m
                    Sse(0x66, w == 8, xd.Hw, i.Operands[1], 0x6E);
                }
                else
                {
                    // movd/movq r/m, xmm
                    Sse(0x66, w == 8, Hw(i.Operands[1]), i.Operands[0], 0x7E);
                }
                break;
            case MOp.AddF: Sse(ScalarPrefix(w), false, Hw(i.Operands[0]), i.Operands[1], 0x58); break;
            case MOp.MulF: Sse(ScalarPrefix(w), false, Hw(i.Operands[0]), i.Operands[1], 0x59); break;
            case MOp.SubF: Sse(ScalarPrefix(w), false, Hw(i.Operands[0]), i.Operands[1], 0x5C); break;
            case MOp.DivF: Sse(ScalarPrefix(w), false, Hw(i.Operands[0]), i.Operands[1], 0x5E); break;
            case MOp.SqrtF: Sse(ScalarPrefix(w), false, Hw(i.Operands[0]), i.Operands[1], 0x51); break;
            case MOp.UcomiF: Sse(w == 8 ? 0x66 : 0, false, Hw(i.Operands[0]), i.Operands[1], 0x2E); break;
            case MOp.XorF: Sse(0, false, Hw(i.Operands[0]), i.Operands[1], 0x57); break;
            case MOp.CvtIntToF:
                Sse(ScalarPrefix(w), i.SourceWidth == 8, Hw(i.Operands[0]), i.Operands[1], 0x2A);
                break;
            case MOp.CvtFToInt:
                Sse(ScalarPrefix(i.SourceWidth), w == 8, Hw(i.Operands[0]), i.Operands[1], 0x2C);
                break;
            case MOp.CvtFToF:
                // To double from single is F3 5A; to single from double is F2 5A.
                Sse(w == 8 ? 0xF3 : 0xF2, false, Hw(i.Operands[0]), i.Operands[1], 0x5A);
                break;

            // ---- odds and ends ----
            case MOp.Int3: Byte(0xCC); break;
            case MOp.Ud2: Byte(0x0F); Byte(0x0B); break;
            case MOp.Nop: Byte(0x90); break;
            case MOp.Pause: Byte(0xF3); Byte(0x90); break;
            case MOp.Syscall: Byte(0x0F); Byte(0x05); break;
            case MOp.In:
                if (w == 2) Byte(0x66);
                Byte(w == 1 ? 0xEC : 0xED);
                break;
            case MOp.Out:
                if (w == 2) Byte(0x66);
                Byte(w == 1 ? 0xEE : 0xEF);
                break;
            case MOp.Cli: Byte(0xFA); break;
            case MOp.Sti: Byte(0xFB); break;
            case MOp.Hlt: Byte(0xF4); break;
            case MOp.RepInsw: Byte(0x66); Byte(0xF3); Byte(0x6D); break;
            case MOp.RepOutsw: Byte(0x66); Byte(0xF3); Byte(0x6F); break;
            case MOp.Lgdt: Op(4, 2, new MMem((MReg)i.Operands[0], 0), 0, 0x0F, 0x01); break;
            case MOp.Lidt: Op(4, 3, new MMem((MReg)i.Operands[0], 0), 0, 0x0F, 0x01); break;
            case MOp.Invlpg: Op(4, 7, new MMem((MReg)i.Operands[0], 0), 0, 0x0F, 0x01); break;
            case MOp.Ltr: Op(4, 3, i.Operands[0], 0, 0x0F, 0x00); break;
            case MOp.MovFromCr:
                // 0F 20 /r: the control register in the reg field (REX.R for
                // CR8), the general register in r/m; always 64 bits here.
                Op(4, (int)((MImm)i.Operands[1]).Value, i.Operands[0], 0, 0x0F, 0x20);
                break;
            case MOp.MovToCr:
                Op(4, (int)((MImm)i.Operands[0]).Value, i.Operands[1], 0, 0x0F, 0x22);
                break;
            case MOp.LoadSegments:
                // mov ds, es, fs, gs, ss <- ax. In long mode loading FS or GS
                // clears its base; a kernel sets GS's through its MSR after.
                Byte(0x8E); Byte(0xD8); Byte(0x8E); Byte(0xC0); Byte(0x8E); Byte(0xE0);
                Byte(0x8E); Byte(0xE8); Byte(0x8E); Byte(0xD0);
                break;
            case MOp.LoadCs:
                // push sel; lea rax, [rip + 3]; push rax; retfq -- a far
                // return to the instruction after it, under the new CS.
                Op(4, 6, i.Operands[0], 0, 0xFF);                        // push r/m64
                Byte(0x48); Byte(0x8D); Byte(0x05); Imm32(3);           // lea rax, [rip+3]
                Byte(0x50);                                             // push rax
                Byte(0x48); Byte(0xCB);                                 // retfq
                break;
            case MOp.Rdmsr: Byte(0x0F); Byte(0x32); break;
            case MOp.Wrmsr: Byte(0x0F); Byte(0x30); break;
            case MOp.Cpuid: Byte(0x0F); Byte(0xA2); break;
            case MOp.Rdtsc: Byte(0x0F); Byte(0x31); break;
            case MOp.ReadFlags:
            {
                // 9C pushfq (64 bits in long mode), then pop r64.
                MReg fr = (MReg)i.Operands[0];
                Byte(0x9C);
                if ((fr.Hw & 8) != 0) Byte(0x41);
                Byte(0x58 + (fr.Hw & 7));
                break;
            }
            case MOp.Swapgs: Byte(0x0F); Byte(0x01); Byte(0xF8); break;
            case MOp.SoftInt: Byte(0xCD); Byte((int)((MImm)i.Operands[0]).Value); break;
            case MOp.Prologue:
                Prologue();
                break;
            case MOp.Epilogue:
                Epilogue();
                break;
            default:
                throw new InvalidOperationException($"{_m.Source.Name}: cannot encode {i.Op}");
        }
    }

    private void Mov(MInstr i, int w)
    {
        MOperand dst = i.Operands[0], src = i.Operands[1];
        if (src is MImm imm)
        {
            if (!imm.IsPlain)
            {
                throw new InvalidOperationException($"{_m.Source.Name}: a symbol's address as an immediate; it is made with LEA");
            }
            if (dst is MReg r)
            {
                long v = imm.Value;
                if (w == 8 && v is >= 0 and <= uint.MaxValue)
                {
                    w = 4;     // the 32-bit form zero-extends
                    v = (uint)v;
                }
                switch (w)
                {
                    case 1:
                        Rex(false, 0, r, true);
                        Byte(0xB0 + (r.Hw & 7));
                        Imm8(v);
                        return;
                    case 2:
                        Byte(0x66);
                        Rex(false, 0, r);
                        Byte(0xB8 + (r.Hw & 7));
                        Imm16(v);
                        return;
                    case 4:
                        Rex(false, 0, r);
                        Byte(0xB8 + (r.Hw & 7));
                        Imm32(v);
                        return;
                    default:
                        if (v is >= int.MinValue and <= int.MaxValue)
                        {
                            Op(8, 0, r, 4, 0xC7);
                            Imm32(v);
                        }
                        else
                        {
                            Rex(true, 0, r);
                            Byte(0xB8 + (r.Hw & 7));
                            Imm64(v);
                        }
                        return;
                }
            }
            switch (w)
            {
                case 1:
                    Op(1, 0, dst, 1, 0xC6);
                    Imm8(imm.Value);
                    return;
                case 2:
                    Op(2, 0, dst, 2, 0xC7);
                    Imm16(imm.Value);
                    return;
                default:
                    if (w == 8 && !imm.FitsInt32)
                    {
                        throw new InvalidOperationException($"{_m.Source.Name}: a 64-bit immediate into memory");
                    }
                    Op(w, 0, dst, 4, 0xC7);
                    Imm32(imm.Value);
                    return;
            }
        }
        if (dst is MMem)
        {
            Op(w, Hw(src), dst, 0, w == 1 ? 0x88 : 0x89);
        }
        else if (src is MMem)
        {
            Op(w, Hw(dst), src, 0, w == 1 ? 0x8A : 0x8B);
        }
        else
        {
            Op(w, Hw(src), dst, 0, w == 1 ? 0x88 : 0x89);
        }
    }

    private void Push(MOperand o)
    {
        switch (o)
        {
            case MReg r:
                if ((r.Hw & 8) != 0) Byte(0x41);
                Byte(0x50 + (r.Hw & 7));
                break;
            case MImm imm when imm.FitsSbyte:
                Byte(0x6A);
                Imm8(imm.Value);
                break;
            case MImm imm:
                Byte(0x68);
                Imm32(imm.Value);
                break;
            default:
                Op(4, 6, o, 0, 0xFF);
                break;
        }
    }

    /// <summary>The eight two-operand ALU forms: digit is ADD=0 .. CMP=7.</summary>
    private void Alu(int digit, MInstr i, int w)
    {
        MOperand dst = i.Operands[0], src = i.Operands[1];
        if (src is MImm imm)
        {
            if (w == 1)
            {
                Op(1, digit, dst, 1, 0x80);
                Imm8(imm.Value);
            }
            else if (imm.FitsSbyte)
            {
                Op(w, digit, dst, 1, 0x83);
                Imm8(imm.Value);
            }
            else
            {
                Op(w, digit, dst, w == 2 ? 2 : 4, 0x81);
                if (w == 2) Imm16(imm.Value); else Imm32(imm.Value);
            }
            return;
        }
        int basis = digit * 8;
        if (src is MMem)
        {
            Op(w, Hw(dst), src, 0, basis + (w == 1 ? 2 : 3));
        }
        else
        {
            Op(w, Hw(src), dst, 0, basis + (w == 1 ? 0 : 1));
        }
    }

    private void Shift(int digit, MInstr i, int w)
    {
        MOperand dst = i.Operands[0];
        if (i.Operands[1] is MImm imm)
        {
            Op(w, digit, dst, 1, w == 1 ? 0xC0 : 0xC1);
            Imm8(imm.Value);
        }
        else
        {
            Op(w, digit, dst, 0, w == 1 ? 0xD2 : 0xD3);
        }
    }

    private void Jump(MInstr i, MBlock target, int cond)
    {
        bool isLong = _long.GetValueOrDefault(i);
        if (!isLong)
        {
            Byte(cond < 0 ? 0xEB : 0x70 + cond);
            int at = _b.Count;
            Byte(0);
            _jumps[i] = (_b.Count, target);
            if (_final)
            {
                _b[at] = (byte)(target.Offset - _b.Count);
            }
            return;
        }
        if (cond < 0)
        {
            Byte(0xE9);
        }
        else
        {
            Byte(0x0F);
            Byte(0x80 + cond);
        }
        int at32 = _b.Count;
        Imm32(0);
        _jumps[i] = (_b.Count, target);
        if (_final)
        {
            Patch32(at32, target.Offset - _b.Count);
        }
    }

    /// <summary>
    /// `jmp [table + index*8]`, the table's address loaded before it. The table is eight-byte
    /// absolute addresses of the blocks, written by the backend beside the
    /// code with one R_X86_64_64 each.
    /// </summary>
    private void JumpTable(MInstr i)
    {
        MReg index = i.Reg(0);
        MReg table = i.Reg(1);
        Op(4, 4, new MMem(table, 0) { Index = index, Scale = 8 }, 0, 0xFF);
        Tables.Add((i.TableSymbol!, i.Table!));
    }

    // ---- the frame ---------------------------------------------------------------------

    /// <summary>
    /// push rbp; mov rbp, rsp; sub rsp, frame; then each saved register into
    /// its place at the bottom of the frame, below the locals.
    /// </summary>
    private void Prologue()
    {
        Byte(0x55);
        Byte(0x48); Byte(0x89); Byte(0xE5);
        if (_m.RealignsStack)
        {
            Byte(0x48); Byte(0x83); Byte(0xE4); Byte(0xF0);     // and rsp, -16
        }
        if (_frameBytes > 0)
        {
            Alu(5, new MInstr(MOp.Sub, MReg.Of(Gpr.Rsp), new MImm(_frameBytes)), 8);
        }
        for (int k = 0; k < _m.SavedRegs.Count; k++)
        {
            Op(8, (int)_m.SavedRegs[k], SaveSlot(k), 0, 0x89);
        }
    }

    private MMem SaveSlot(int k) => MMem.Frame(-_m.Frame.Size - 8 * (k + 1));

    private void Epilogue()
    {
        for (int k = 0; k < _m.SavedRegs.Count; k++)
        {
            Op(8, (int)_m.SavedRegs[k], SaveSlot(k), 0, 0x8B);
        }
        Byte(0xC9);        // leave: mov rsp, rbp; pop rbp
        Byte(0xC3);
    }

    /// <summary>Real no-ops for alignment padding between functions.</summary>
    public static void Nops(List<byte> bytes, int count)
    {
        for (int k = 0; k < count; k++)
        {
            bytes.Add(0x90);
        }
    }
}
