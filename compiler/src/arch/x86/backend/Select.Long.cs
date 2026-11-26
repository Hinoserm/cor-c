#nullable enable
using Corsac.Lang.Ir;

using Corsac.Lang.Calls;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

// LONG MODE'S METHODS OF THE FAMILY'S SELECTOR (Select.cs has the class and
// i386's). A method with i386's name that is still long mode's own carries
// an L; the function, the blocks, the emission and the tables of registers,
// uses and constants are the class's. Each is to be
// made one method for both modes.
//
// Instruction selection for x86-64: one IR function into machine
// instructions over virtual registers, in the shape the allocator works on.
//
// Every value is one register: an I32 or an I64 in a general register (the
// 32-bit forms of the instructions for I32, the 64-bit ones for I64 and for
// every address), an F32 or F64 in an XMM register. Two-address instructions
// are preceded by a move into their destination, which the allocator
// coalesces away when it can; fixed-register instructions -- the divide,
// the shifts by CL, the string moves, a call's arguments -- name the
// physical registers directly, and the allocator treats those as busy.
//
// THE CALLING CONVENTION IS SYSTEM V AMD64: integer arguments in RDI, RSI,
// RDX, RCX, R8, R9, floating-point ones in XMM0..XMM7, the rest on the
// stack, eight bytes each; results in RAX or XMM0. RBX, RBP and R12..R15
// are preserved by the callee, everything else is not. The stack is
// sixteen-byte aligned at every call.
internal sealed partial class Selector
{
    internal static Gpr[] IntArgRegs => LongAbi.IntArgRegs;
    internal const int FloatArgRegs = LongAbi.FloatArgRegs;

    private static readonly MReg Rax = MReg.Of(Gpr.Rax);
    private static readonly MReg Rcx = MReg.Of(Gpr.Rcx);
    private static readonly MReg Rdx = MReg.Of(Gpr.Rdx);
    private static readonly MReg Rsi = MReg.Of(Gpr.Rsi);
    private static readonly MReg Rdi = MReg.Of(Gpr.Rdi);
    private static readonly MReg Rsp = MReg.Of(Gpr.Rsp);
    private static readonly MReg Rbp = MReg.Of(Gpr.Rbp);
    private static readonly MReg R8 = MReg.Of(Gpr.R8);
    private static readonly MReg R9 = MReg.Of(Gpr.R9);
    private static readonly MReg R10 = MReg.Of(Gpr.R10);
    private static readonly MReg Xmm0 = MReg.Xmm(0);

    private Selector(Function f, List<string> errors, Func<string, bool>? isExternal)
    {
        _f = f;
        _m = new MFunction(f);
        _errors = errors;
        _external = isExternal;
        // The family's tables: a value's register by its number (_lo; no
        // value has a second, _hi is the pairs' of i386), and its uses.
        _lo = new ByRegister<MReg>(f.RegCount);
        _hi = new ByRegister<MReg>(f.RegCount);
        _useCount = new UseCounts(f.RegCount);
        NoteDefinitions();
        // EVERY FLOAT IS OF THE XMM CLASS (Select.Sse): long mode's processor
        // has SSE2, and nothing here is the x87's.
        _xmm32 = _xmm64 = true;
    }

    /// <summary>
    /// Select one function. <paramref name="isExternal"/> names what another
    /// image defines -- in a shared object everything this object does not,
    /// in a dynamically linked program what its libraries supply -- which is
    /// reached through its GOT slot rather than at its own address.
    /// </summary>
    public static MFunction RunLong(Function f, List<string> errors, Func<string, bool>? isExternal = null, string? streamCopy = null, string? streamFill = null)
    {
        if (!X86Mode.Long) throw new InvalidOperationException("long mode's selector was asked for a function outside long mode");
        // (Not in the stream routines themselves, as i386's: Selector.Run.)
        bool stream = f.Name != streamCopy && f.Name != streamFill;
        Selector s = new(f, errors, isExternal) { _streamCopy = stream ? streamCopy : null, _streamFill = stream ? streamFill : null };
        s.Select();
        return s._m;
    }

    private readonly Func<string, bool>? _external;

    private bool External(string symbol) => _external is not null && _external(symbol);

    /// <summary>
    /// The address of a symbol another image defines, plus an offset: its
    /// GOT slot read (`mov r, [rip + sym@GOTPCREL]`), which the linker turns
    /// back into `lea r, [rip + sym]` when the symbol is defined after all.
    /// </summary>
    private MReg GotAddress(string symbol, long offset, MReg? into = null)
    {
        MReg t = into ?? Temp();
        EmitW(MOp.Mov, 8, t, MMem.RipGot(symbol));
        if (offset != 0)
        {
            Emit(MOp.Lea, t, new MMem(t, checked((int)offset)));
        }
        return t;
    }

    // ---- emission ----------------------------------------------------------

    private MReg FTemp() => _m.NewFloat();

    private static int Width(IrType t) => t is IrType.I64 or IrType.F64 ? 8 : 4;

    /// <summary>A move of an integer of the given width. A move of 4 bytes into a register clears the top half.</summary>
    private void MovL(MOperand dst, MOperand src, int width = 8) => EmitW(MOp.Mov, width, dst, src);

    // ---- operands ----------------------------------------------------------

    /// <summary>The machine register a virtual one lives in, of the class its type says.</summary>
    private MReg V(VReg v)
    {
        MReg? r = _lo[v.Id];
        if (r is null)
        {
            r = v.Type.IsFloat() ? _m.NewFloat() : _m.NewReg();
            _lo[v.Id] = r;
        }
        return r;
    }

    /// <summary>An integer operand in a register, materialising a constant, a symbol or a frame address.</summary>
    private MReg RL(Operand o)
    {
        switch (o)
        {
            case RegOperand r:
                return V(r.Reg);
            case SlotOperand s:
            {
                MReg t = Temp();
                Emit(MOp.Lea, t, MMem.Frame(_m.Frame.SlotOffset(s.Slot)));
                return t;
            }
            case SymOperand s:
            {
                if (External(s.Name))
                {
                    return GotAddress(s.Name, s.Offset);
                }
                MReg t = Temp();
                Emit(MOp.Lea, t, MMem.Rip(s.Name, checked((int)s.Offset)));
                return t;
            }
            case ImmOperand i:
            {
                MReg t = Temp();
                LoadConstant(t, i.Value, Width(i.Type));
                return t;
            }
            default:
                throw new InvalidOperationException(o.GetType().Name);
        }
    }

    /// <summary>A constant into a register, by the shortest form that makes it.</summary>
    private void LoadConstant(MReg r, long value, int width)
    {
        if (width == 4)
        {
            MovL(r, Imm(unchecked((int)value)), 4);
        }
        else if (value is >= 0 and <= uint.MaxValue)
        {
            // A 32-bit move zero-extends: five bytes rather than ten.
            MovL(r, Imm(unchecked((int)(uint)value)), 4);
        }
        else
        {
            // mov r64, imm32 sign-extends; anything else is movabs.
            MovL(r, Imm(value), 8);
        }
    }

    /// <summary>
    /// An integer operand as a register or an immediate, the source side of
    /// an ALU instruction. An immediate there is 32 bits, sign-extended to
    /// the width of the operation, so a 64-bit constant that is not such a
    /// value goes through a register.
    /// </summary>
    private MOperand RML(Operand o, int width)
    {
        if (o is ImmOperand i)
        {
            long v = width == 4 ? unchecked((int)i.Value) : i.Value;
            if (v is >= int.MinValue and <= int.MaxValue)
            {
                return Imm(v);
            }
        }
        return RL(o);
    }

    /// <summary>A floating-point operand in an XMM register.</summary>
    private MReg F(Operand o)
    {
        if (o is RegOperand r)
        {
            return V(r.Reg);
        }
        if (o is ImmOperand i)
        {
            // A float constant arrives as its bits.
            MReg bits = Temp();
            LoadConstant(bits, i.Value, 8);
            MReg f = FTemp();
            EmitW(MOp.MovGx, Width(o.Type), f, bits);
            return f;
        }
        throw new InvalidOperationException($"{o} is not a floating-point value");
    }

    /// <summary>The memory an address operand plus displacement names.</summary>
    private MMem AddressL(Operand addr, long offset)
    {
        int disp = checked((int)offset);
        switch (addr)
        {
            case RegOperand r:
                return new MMem(V(r.Reg), disp);
            case SymOperand s:
                if (External(s.Name))
                {
                    return new MMem(GotAddress(s.Name, 0), checked((int)(s.Offset + disp)));
                }
                return MMem.Rip(s.Name, checked((int)(s.Offset + disp)));
            case SlotOperand s:
                return MMem.Frame(_m.Frame.SlotOffset(s.Slot) + disp);
            case ImmOperand i:
            {
                long at = i.Value + disp;
                if (at is >= int.MinValue and <= int.MaxValue)
                {
                    // [disp32] with no base: the encoder writes it with a SIB
                    // byte, since the plain form means RIP-relative in long mode.
                    return new MMem(null, (int)at);
                }
                MReg t = Temp();
                LoadConstant(t, at, 8);
                return new MMem(t, 0);
            }
            default:
                Error($"cannot address through {addr} of type {addr.Type}");
                return new MMem(null, 0);
        }
    }

    // ---- the walk (Select, Select.cs) -----------------------------------------

    /// <summary>Long mode: a parameter from its word on the stack (LoadParams).</summary>
    private void ParamFromStackL(VReg p, int off)
    {
        if (p.Type.IsFloat()) EmitW(MOp.MovF, Width(p.Type), V(p), MMem.Frame(off));
        else if (p.Type.IsInt()) MovL(V(p), MMem.Frame(off), Width(p.Type));
        else Error($"parameter {p} has type {p.Type}");
    }

    private void SelectInstrL(Instr i)
    {
        switch (i.Op)
        {
            case Opcode.Call:
            case Opcode.CallIndirect:
                SelectCall(i);
                break;
            case Opcode.Ret:
                SelectRet(i);
                break;
            case Opcode.Unwind:
                SelectUnwindL(i);
                break;
            case Opcode.Syscall:
                SelectSyscallL(i);
                break;
            // EVERY OTHER OPCODE IS THE FAMILY'S (SelectInstr, Select.cs): its
            // case there serves both modes, or says it is unsupported.
            default:
                SelectInstr(i);
                break;
        }
    }

    // ---- moves and arithmetic -----------------------------------------------------

    /// <summary>
    /// An integer copied into its one register in long mode, from what is no
    /// frame slot: zero by a 32-bit XOR, another constant by the shortest
    /// move that makes it (LoadConstant), a symbol's address RIP-relative or
    /// from its GOT slot, a register by a move of the value's width.
    /// </summary>
    private void CopyWordL(VReg d, Operand s)
    {
        int w = Width(d.Type);
        switch (s)
        {
            case ImmOperand imm:
                if ((w == 4 ? unchecked((int)imm.Value) : imm.Value) == 0)
                {
                    MReg z = V(d);
                    EmitW(MOp.Xor, 4, z, z);
                }
                else
                {
                    LoadConstant(V(d), imm.Value, w);
                }
                break;
            case SymOperand sym:
                if (External(sym.Name))
                {
                    GotAddress(sym.Name, sym.Offset, V(d));
                    break;
                }
                Emit(MOp.Lea, V(d), MMem.Rip(sym.Name, checked((int)sym.Offset)));
                break;
            default:
                MovL(V(d), RL(s), w);
                break;
        }
    }

    /// <summary>
    /// DIVISION BY A CONSTANT, AS A MULTIPLICATION: the high half of the
    /// dividend times a magic number, shifted, which is what every compiler
    /// since Granlund and Montgomery (1994) does -- a multiply is a few cycles
    /// where a 64-bit DIV on a K8 is dozens. Unsigned uses their round-up
    /// method with the one-bit fix-up that makes it exact for every dividend;
    /// signed, Hacker's Delight's (10-1). The remainder is n - q*d. Zero, one
    /// and minus one, and the most negative value, are left to DIV, whose
    /// trap or identity is the answer they need.
    /// </summary>
    private bool SelectDivByConstant(Instr i, long value, int w, bool signed, bool rem)
    {
        int bits = w * 8;
        long divisor = w == 4 ? (signed ? unchecked((int)value) : (long)(uint)value) : value;
        if (divisor == 0 || divisor == 1 || (signed && divisor == -1))
        {
            return false;
        }
        if (signed && divisor == (w == 4 ? int.MinValue : long.MinValue))
        {
            return false;
        }
        if (!signed && w == 8 && divisor < 0)
        {
            // An unsigned 64-bit divisor at or above 2^63: the quotient is 0 or 1.
            return false;
        }

        VReg d = i.Dest!;
        MReg n = Temp();
        MovL(n, RML(i.Operands[0], w), w);
        MReg q = Temp();

        if (!signed)
        {
            ulong ud = (ulong)divisor;
            int l = 0;
            while (l < bits && (UInt128.One << l) < ud)
            {
                l++;
            }
            UInt128 magic = ((UInt128.One << bits) * ((UInt128.One << l) - ud)) / ud + 1;
            MulHigh(q, n, unchecked((long)(ulong)magic), w, signedMultiply: false);
            // t = (n - q) >> 1; q = (t + q) >> (l - 1)
            MReg t = Temp();
            MovL(t, n, w);
            EmitW(MOp.Sub, w, t, q);
            EmitW(MOp.Shr, w, t, Imm(1));
            EmitW(MOp.Add, w, q, t);
            if (l > 1)
            {
                EmitW(MOp.Shr, w, q, Imm(l - 1));
            }
        }
        else
        {
            (long magic, int shift) = SignedMagic(divisor, bits);
            MulHigh(q, n, magic, w, signedMultiply: true);
            if (divisor > 0 && magic < 0)
            {
                EmitW(MOp.Add, w, q, n);
            }
            else if (divisor < 0 && magic > 0)
            {
                EmitW(MOp.Sub, w, q, n);
            }
            if (shift > 0)
            {
                EmitW(MOp.Sar, w, q, Imm(shift));
            }
            // Toward zero: one more when the quotient came out negative.
            MReg sign = Temp();
            MovL(sign, q, w);
            EmitW(MOp.Shr, w, sign, Imm(bits - 1));
            EmitW(MOp.Add, w, q, sign);
        }

        if (!rem)
        {
            MovL(V(d), q, w);
            return true;
        }
        MReg product = Temp();
        if (divisor is >= int.MinValue and <= int.MaxValue)
        {
            Emit(new MInstr(MOp.Imul3, product, q, Imm(divisor)) { Width = w });
        }
        else
        {
            MovL(product, q, w);
            MReg k = Temp();
            LoadConstant(k, divisor, 8);
            EmitW(MOp.Imul, w, product, k);
        }
        MReg r = Temp();
        MovL(r, n, w);
        EmitW(MOp.Sub, w, r, product);
        MovL(V(d), r, w);
        return true;
    }

    /// <summary>
    /// The high half of n times a constant: for 32 bits, the exact 64-bit
    /// product of the widened operands, shifted down; for 64 bits, the upper
    /// word MUL or IMUL leaves in RDX.
    /// </summary>
    private void MulHigh(MReg into, MReg n, long magic, int w, bool signedMultiply)
    {
        if (w == 4)
        {
            MReg wide = Temp(), m = Temp();
            if (signedMultiply)
            {
                EmitW(MOp.Movsx, 4, wide, n);
                LoadConstant(m, unchecked((int)magic), 8);
                EmitW(MOp.Imul, 8, wide, m);
                EmitW(MOp.Sar, 8, wide, Imm(32));
            }
            else
            {
                MovL(wide, n, 4);
                LoadConstant(m, (long)(uint)magic, 8);
                EmitW(MOp.Imul, 8, wide, m);
                EmitW(MOp.Shr, 8, wide, Imm(32));
            }
            MovL(into, wide, 4);
            return;
        }
        MReg k = Temp();
        LoadConstant(k, magic, 8);
        MovL(Rax, n, 8);
        EmitW(signedMultiply ? MOp.ImulWide : MOp.MulWide, 8, k);
        MovL(into, Rdx, 8);
    }

    /// <summary>
    /// The signed magic number and shift for dividing by d in `bits`-bit
    /// arithmetic: Hacker's Delight, figure 10-1, in 128-bit arithmetic so one
    /// routine serves both widths.
    /// </summary>
    private static (long Magic, int Shift) SignedMagic(long d, int bits)
    {
        UInt128 two = UInt128.One << (bits - 1);
        UInt128 mask = (UInt128.One << bits) - 1;
        UInt128 ad = (UInt128)(ulong)Math.Abs(d);
        UInt128 t = two + (d < 0 ? UInt128.One : UInt128.Zero);
        UInt128 anc = t - 1 - t % ad;
        int p = bits - 1;
        UInt128 q1 = two / anc, r1 = two - q1 * anc;
        UInt128 q2 = two / ad, r2 = two - q2 * ad;
        UInt128 delta;
        do
        {
            p++;
            q1 = (2 * q1) & mask;
            r1 = (2 * r1) & mask;
            if (r1 >= anc)
            {
                q1 = (q1 + 1) & mask;
                r1 = (r1 - anc) & mask;
            }
            q2 = (2 * q2) & mask;
            r2 = (2 * r2) & mask;
            if (r2 >= ad)
            {
                q2 = (q2 + 1) & mask;
                r2 = (r2 - ad) & mask;
            }
            delta = ad - r2;
        }
        while (q1 < delta || (q1 == delta && r1 == 0));
        UInt128 magic = (q2 + 1) & mask;
        long m = bits == 32 ? unchecked((int)(uint)magic) : unchecked((long)(ulong)magic);
        if (d < 0)
        {
            m = bits == 32 ? unchecked(-(int)m) : unchecked(-m);
        }
        return (m, p - bits);
    }

    // ---- conversions between integers and floats -----------------------------------------

    /// <summary>
    /// Unsigned to float. A 32-bit value is a non-negative 64-bit one, which
    /// the signed conversion takes exactly. A 64-bit value with its top bit
    /// set is halved -- keeping the lowest bit, so rounding comes out as it
    /// would for the whole number -- converted, and doubled.
    /// </summary>
    private void SelectUnsignedToFloat(Instr i)
    {
        VReg d = i.Dest!;
        int fw = Width(d.Type);
        if (i.Operands[0].Type == IrType.I32)
        {
            MReg wide = Temp();
            MovL(wide, RML(i.Operands[0], 4), 4);
            Emit(new MInstr(MOp.CvtIntToF, V(d), wide) { Width = fw, SourceWidth = 8 });
            return;
        }
        MReg s = RL(i.Operands[0]);
        MReg dr = V(d);
        MBlock big = Aside();
        MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", big);
        EmitW(MOp.Test, 8, s, s);
        Jcc(Cond.S, big);
        Emit(new MInstr(MOp.CvtIntToF, dr, s) { Width = fw, SourceWidth = 8 });
        Jmp(done);
        _cur = big;
        MReg half = Temp(), low = Temp();
        MovL(half, s, 8);
        EmitW(MOp.Shr, 8, half, Imm(1));
        MovL(low, s, 8);
        EmitW(MOp.And, 8, low, Imm(1));
        EmitW(MOp.Or, 8, half, low);
        Emit(new MInstr(MOp.CvtIntToF, dr, half) { Width = fw, SourceWidth = 8 });
        EmitW(MOp.AddF, fw, dr, dr);
        _cur = done;
    }

    /// <summary>
    /// Float to integer as .NET converts since it made the conversion the same
    /// on every machine: NaN is zero, a value beyond the destination's range
    /// is its limit, and a negative value into an unsigned destination is
    /// zero -- the i386 backend's rule, decided the same way, from the IEEE
    /// bits; in range, the truncating conversion below answers. For a
    /// destination of sixty-four bits this is the whole conversion. For one
    /// of thirty-two it is only the classification, reached where the XMM
    /// class's truncation answered "indefinite" (SseToInt): the truncation
    /// has run by then, and the same register gets the answer.
    /// </summary>
    private void SelectFloatToInteger(Instr i)
    {
        VReg d = i.Dest!;
        bool wide = d.Type == IrType.I64, unsigned = i.Op == Opcode.FToU;
        int fw = Width(i.Operands[0].Type);
        bool single = fw == 4;
        MReg x = F(i.Operands[0]);
        MReg bits = Temp(), magnitude = Temp(), limit = Temp(), dr = V(d);
        int dw = wide ? 8 : 4;

        EmitW(MOp.MovGx, fw, bits, x);
        MovL(magnitude, bits, fw);
        if (single)
        {
            EmitW(MOp.And, 4, magnitude, Imm(int.MaxValue));
        }
        else
        {
            MReg mask = Temp();
            MovL(mask, Imm(long.MaxValue), 8);
            EmitW(MOp.And, 8, magnitude, mask);
        }

        MBlock anchor = _cur;
        MBlock New(string name) { MBlock b = _m.NewBlock($"{name}{_splits++}", anchor); anchor = b; return b; }
        MBlock finite = New("cast-finite"), negative = New("cast-negative"), zero = New("cast-zero"),
            maximum = New("cast-maximum"), minimum = New("cast-minimum"), normal = New("cast-normal"),
            done = New("cast-done");

        // NaN: above the infinity's bits.
        LoadConstant(limit, single ? 0x7f800000 : 0x7ff0000000000000, 8);
        EmitW(MOp.Cmp, fw, magnitude, limit);
        Jcc(Cond.A, zero);
        Jmp(finite);

        _cur = finite;
        EmitW(MOp.Test, fw, bits, bits);
        Jcc(Cond.S, unsigned ? zero : negative);
        // 2^31, 2^32, 2^63 or 2^64 in the source format: the first value
        // that does not fit.
        long bound = single
            ? (wide ? (unsigned ? 0x5f800000 : 0x5f000000) : (unsigned ? 0x4f800000 : 0x4f000000))
            : (wide ? (unsigned ? 0x43f0000000000000 : 0x43e0000000000000) : (unsigned ? 0x41f0000000000000 : 0x41e0000000000000));
        LoadConstant(limit, bound, 8);
        EmitW(MOp.Cmp, fw, magnitude, limit);
        Jcc(Cond.Ae, maximum);
        Jmp(normal);

        _cur = negative;
        EmitW(MOp.Cmp, fw, magnitude, limit);
        Jcc(Cond.Ae, minimum);
        Jmp(normal);

        _cur = zero;
        EmitW(MOp.Xor, 4, dr, dr);
        Jmp(done);

        _cur = maximum;
        LoadConstant(dr, wide ? (unsigned ? -1L : long.MaxValue) : (unsigned ? 0xFFFFFFFFL : int.MaxValue), dw);
        Jmp(done);

        _cur = minimum;
        LoadConstant(dr, wide ? long.MinValue : int.MinValue, dw);
        Jmp(done);

        _cur = normal;
        if (!unsigned)
        {
            Emit(new MInstr(MOp.CvtFToInt, dr, x) { Width = dw, SourceWidth = fw });
        }
        else if (!wide)
        {
            // Below 2^32: the 64-bit signed conversion holds it, and its low
            // half is the answer.
            MReg wideResult = Temp();
            Emit(new MInstr(MOp.CvtFToInt, wideResult, x) { Width = 8, SourceWidth = fw });
            MovL(dr, wideResult, 4);
        }
        else
        {
            // Below 2^64: at or above 2^63 the value comes down by 2^63 first
            // (exact, a power of two) and the top bit goes back on after.
            MReg limitBits = Temp();
            LoadConstant(limitBits, single ? 0x5F000000 : 0x43E0000000000000, 8);
            MReg half = FTemp();
            EmitW(MOp.MovGx, fw, half, limitBits);
            MBlock big = New("cast-unsigned-high");
            EmitW(MOp.UcomiF, fw, x, half);
            Jcc(Cond.Ae, big);
            Emit(new MInstr(MOp.CvtFToInt, dr, x) { Width = 8, SourceWidth = fw });
            Jmp(done);
            _cur = big;
            MReg reduced = FTemp();
            EmitW(MOp.MovF, fw, reduced, x);
            EmitW(MOp.SubF, fw, reduced, half);
            Emit(new MInstr(MOp.CvtFToInt, dr, reduced) { Width = 8, SourceWidth = fw });
            MReg top = Temp();
            MovL(top, Imm(long.MinValue), 8);
            EmitW(MOp.Or, 8, dr, top);
        }
        Jmp(done);
        _cur = done;
    }

    // ---- memory --------------------------------------------------------------------

    /// <summary>The most bytes a constant clear or copy is written out as moves.</summary>
    private const long InlineBytes = 128;

    /// <summary>Stores of 8, 4, 2 and 1 bytes covering [at, at + count), each from `value(width)`.</summary>
    private void Pieces(long count, Action<int, int> piece)
    {
        int offset = 0;
        foreach (int width in new[] { 8, 4, 2, 1 })
        {
            while (count - offset >= width)
            {
                piece(offset, width);
                offset += width;
            }
        }
    }

    /// <summary>
    /// A SMALL COPY OF KNOWN LENGTH is loads and stores, in order: a string
    /// instruction's start-up costs more than the whole copy. False for any
    /// other, which the family's string form copies (InlineMemCopy).
    /// </summary>
    private bool SmallCopyL(Instr i)
    {
        if (i.Operands[2] is not ImmOperand { Value: >= 0 and <= InlineBytes } small) return false;
        MReg dst = RL(i.Operands[0]);
        MReg src = RL(i.Operands[1]);
        MReg t = Temp();
        Pieces(small.Value, (offset, width) =>
        {
            if (width >= 4)
            {
                MovL(t, new MMem(src, offset), width);
            }
            else
            {
                EmitW(MOp.Movzx, width, t, new MMem(src, offset));
            }
            MovL(new MMem(dst, offset), t, width);
        });
        return true;
    }

    /// <summary>
    /// A SMALL CLEAR OF KNOWN LENGTH AND BYTE is stores, of an immediate when
    /// the repeated byte fits one (zero always does), else of a register.
    /// False for any other, which the family's string form fills (InlineMemSet).
    /// </summary>
    private bool SmallSetL(Instr i)
    {
        if (i.Operands[2] is not ImmOperand { Value: >= 0 and <= InlineBytes } small || i.Operands[1] is not ImmOperand smallFill) return false;
        MReg dst = RL(i.Operands[0]);
        ulong pattern = (byte)smallFill.Value * 0x0101010101010101UL;
        MOperand source;
        if ((long)pattern is >= int.MinValue and <= int.MaxValue)
        {
            source = Imm((long)pattern);
        }
        else
        {
            MReg p = Temp();
            LoadConstant(p, unchecked((long)pattern), 8);
            source = p;
        }
        Pieces(small.Value, (offset, width) =>
        {
            MOperand value = source is MImm imm ? Imm(width == 8 ? imm.Value : imm.Value & ((1L << (width * 8)) - 1)) : source;
            if (value is MImm narrow && width == 4)
            {
                value = Imm(unchecked((int)narrow.Value));
            }
            MovL(new MMem(dst, offset), value, width);
        });
        return true;
    }

    // ---- calls -------------------------------------------------------------------------

    /// <summary>Long mode's call sequence (SelectCall): the stack arguments pushed last first over a pad that keeps the sixteen, the registers' moved, the call, the stack put back.</summary>
    private void CallSequenceL(Instr i, int first)
    {
        // WHERE EACH ARGUMENT GOES is the convention's word (Conventions.
        // SystemV), as at the callee's entry (LoadParams).
        Convention convention = Conventions.SystemV;
        CallPlan plan = PlanOf(i, first);
        List<(Operand Value, int Reg)> inRegs = new();
        List<Operand> onStack = new();
        for (int k = 0; k < plan.Places.Length; k++)
        {
            ArgPlace place = plan.Places[k];
            if (place.Kind == PlaceKind.Float) inRegs.Add((i.Operands[first + k], MReg.XmmBase + place.Register));
            else if (place.Kind == PlaceKind.General) inRegs.Add((i.Operands[first + k], place.Register));
            else onStack.Add(i.Operands[first + k]);
        }
        int ints = plan.Generals, floats = plan.Floats;

        MReg? target = i.Op == Opcode.CallIndirect ? RL(i.Operands[0]) : null;

        // THE STACK ARGUMENTS FIRST, last to first, with a pad beneath them
        // when there is an odd number, so RSP is sixteen-byte aligned at the
        // call as it was at the frame's bottom.
        int pushed = plan.StackBytes;
        int pad = (convention.StackAlignment - pushed % convention.StackAlignment) % convention.StackAlignment;
        if (pad != 0)
        {
            EmitW(MOp.Sub, 8, Rsp, Imm(pad));
        }
        for (int k = onStack.Count - 1; k >= 0; k--)
        {
            Operand o = onStack[k];
            if (o.Type.IsFloat())
            {
                EmitW(MOp.Sub, 8, Rsp, Imm(8));
                EmitW(MOp.MovF, Width(o.Type), new MMem(Rsp, 0), F(o));
            }
            else
            {
                Emit(MOp.Push, RML(o, 8));
            }
        }

        // The register arguments are evaluated into virtual registers first
        // and only then moved to their fixed homes: evaluating one may need a
        // temporary, and none of those homes may be taken while it is made.
        List<(MReg Value, int Reg, int Width)> moves = new();
        foreach ((Operand o, int reg) in inRegs)
        {
            if (o.Type.IsFloat())
            {
                moves.Add((F(o), reg, Width(o.Type)));
            }
            else
            {
                moves.Add((RL(o), reg, 8));
            }
        }
        foreach ((MReg value, int reg, int width) in moves)
        {
            if (reg >= MReg.XmmBase)
            {
                EmitW(MOp.MovF, width, MReg.Xmm(reg - MReg.XmmBase), value);
            }
            else
            {
                MovL(new MReg(reg), value, 8);
            }
        }

        // A CALL INTO C: a variadic function (printf) reads in AL how many
        // vector registers carry arguments, and every other function ignores
        // it, so a native call always says.
        bool native = NativeCall.Is(i);
        if (native)
        {
            MovL(Rax, Imm(floats), 4);
        }
        if (target is null && !native && ints == 0 && floats == 0 && i.Callee == Corsac.Lang.Lto.RuntimeAbi.Poll)
        {
            // THE BACK EDGE'S POLL keeps the general registers (MOp.CallSafe).
            Emit(new MInstr(MOp.CallSafe, MImm.Sym(i.Callee!, 0)));
        }
        else if (target is null)
        {
            Emit(new MInstr(MOp.Call, MImm.Sym(NativeCall.Symbol(i.Callee!), 0)) { IntArgs = ints, FloatArgs = floats, NativeAl = native });
        }
        else
        {
            // The target as the register it is in, as i386's: the allocation
            // keeps it out of the registers the arguments are moved into
            // (they are written while it is live), may leave it in memory
            // (Roles.Foldable), and a target loaded only to be called is
            // called where it lies (AddressFold's step 5) -- a virtual
            // method's slot, a delegate's target. Copied to a fixed scratch
            // register first, none of that was seen.
            Emit(new MInstr(MOp.CallInd, target) { IntArgs = ints, FloatArgs = floats, NativeAl = native });
        }
        if (pushed + pad > 0)
        {
            EmitW(MOp.Add, 8, Rsp, Imm(pushed + pad));
        }
    }

    // ---- control ---------------------------------------------------------------------

    /// <summary>
    /// Everything into fixed registers before RSP and RBP move: once they
    /// have, no frame-relative operand means what it did.
    /// </summary>
    private void SelectUnwindL(Instr i)
    {
        int w = Target.Current.WordSize;
        MReg rec = Temp(), exception = Temp();
        MovL(rec, RML(i.Operands[0], 8));
        MovL(exception, RML(i.Operands[1], 8));
        MovL(Rcx, rec);
        MovL(Rax, exception);
        MovL(Rsp, new MMem(Rcx, 2 * w));
        MovL(Rbp, new MMem(Rcx, 3 * w));
        Emit(MOp.JmpInd, new MMem(Rcx, 1 * w));
    }

    // ---- the operating system and the machine ------------------------------------------

    /// <summary>
    /// Linux x86-64: number in RAX, arguments in RDI, RSI, RDX, R10, R8, R9,
    /// result in RAX; the instruction destroys RCX and R11.
    /// </summary>
    private void SelectSyscallL(Instr i)
    {
        MReg[] regs = { Rdi, Rsi, Rdx, R10, R8, R9 };
        int args = i.Operands.Count - 1;
        if (args > 6)
        {
            Error("syscall with more than six arguments");
            return;
        }
        List<MReg> values = new();
        MReg number = RL(i.Operands[0]);
        for (int k = 0; k < args; k++)
        {
            values.Add(RL(i.Operands[k + 1]));
        }
        for (int k = 0; k < args; k++)
        {
            MovL(regs[k], values[k]);
        }
        MovL(Rax, number);
        // Linux's system call is the syscall instruction; any other gate --
        // a kernel's own, CORSAC's GUI module's -- is a software interrupt taking
        // the same registers.
        Emit(i.Offset == 0 ? new MInstr(MOp.Syscall) { IntArgs = args }
                           : new MInstr(MOp.SoftInt, Imm(i.Offset)) { IntArgs = args });
        if (i.Dest is not null)
        {
            MovL(V(i.Dest), Rax, Width(i.Dest.Type));
        }
    }

    /// <summary>The control register a ReadCr or WriteCr names, which has to be a constant: CR0, CR2, CR3, CR4 or CR8.</summary>
    private long ControlRegisterL(Operand o)
    {
        long n = o is ImmOperand imm ? imm.Value : -1;
        if (n is not (0 or 2 or 3 or 4 or 8))
        {
            Error($"CR{n}: the control register must be a constant 0, 2, 3, 4 or 8");
            return 0;
        }
        return n;
    }

    /// <summary>The per-processor word at `offset` from GS's base: gs:[disp32], or gs:[reg].</summary>
    private MMem PerCpuWord(Operand offset)
    {
        if (offset is ImmOperand { Value: var at } && at >= int.MinValue && at <= int.MaxValue)
        {
            return new MMem(null, (int)at) { Segment = 0x65 };
        }
        return new MMem(RL(offset), 0) { Segment = 0x65 };
    }

    /// <summary>
    /// LONG MODE'S PACKED OPERATIONS (Lowering.PackedIntrinsic): the XMM
    /// registers' and a Packed64's on their low halves, each on registers
    /// the allocator gives (SelectPackedValueOp) -- there are no packed
    /// registers here to hold or give up; a word stored around the caches;
    /// the fence and the prefetches, which touch no register.
    /// </summary>
    private void SelectPackedOpL(string name, Instr i)
    {
        if (XmmValueOp(name, out string[] part))
        {
            SelectPackedValueOp(part, i);
            return;
        }
        switch (part[0])
        {
            case "fence":
                Emit(new MInstr(MOp.Sfence) { Probed = true });
                return;
            case "prefetch":
                Emit(new MInstr(part[1] == "write" ? MOp.PrefetchWrite : MOp.PrefetchNta, new MMem(RL(i.Operands[0]), 0)) { Probed = true });
                return;
            // MOVNTI: the word a Packed64 is, or a half of a Packed128 whose address is not at sixteen.
            case "storentw":
                Emit(new MInstr(MOp.Movnti, new MMem(RL(i.Operands[0]), 0), RL(i.Operands[1])) { Width = 8, Probed = true });
                return;
        }
        Error("the packed operation " + name + " has no x86-64 form");
    }

    private void SelectMachineIntrinsicL(string name, Instr i)
    {
        if (name.StartsWith(Corsac.Lang.X86.MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal))
        {
            SelectPackedOpL(name, i);
            return;
        }
        // The ones that are one instruction in both modes (Select.Intrinsics.cs).
        if (SelectFamilyIntrinsic(name, i)) return;
        switch (name)
        {
            case Corsac.Lang.X86.MachineIntrinsics.ThreadBlock:
                // The self pointer at gs:[0]: the first word of the block is
                // its own address, as glibc's thread control block is.
                if (i.Dest is not null)
                {
                    MovL(V(i.Dest), new MMem(null, 0) { Segment = 0x65 });
                }
                return;
            case Corsac.Lang.X86.MachineIntrinsics.DoublePrecision:
                // SSE2's doubles round once already.
                return;
            case Corsac.Lang.X86.MachineIntrinsics.RefStore:
            case Corsac.Lang.X86.MachineIntrinsics.CardStore:
                // A REFERENCE STORED IN ONE SEQUENCE (CardMarks.FuseStores):
                // the slot in RAX, the value in RDX, every register kept, by
                // the image's stub (Corsac.Lang.X64.X64Backend.RefStoreStub).
                MovL(Rax, RL(i.Operands[0]));
                MovL(Rdx, RL(i.Operands[1]));
                Emit(MOp.CallKeep, MImm.Sym(name == Corsac.Lang.X86.MachineIntrinsics.RefStore ? Corsac.Lang.X64.X64Backend.RefStoreStub : Corsac.Lang.X64.X64Backend.CardStoreStub, 0));
                return;
            case Corsac.Lang.X86.MachineIntrinsics.RefExchange:
                // The exchange's stub: slot RAX, value RDX; the old reference
                // in RAX, every other register kept.
                MovL(Rax, RL(i.Operands[0]));
                MovL(Rdx, RL(i.Operands[1]));
                Emit(MOp.CallKeepRax, MImm.Sym(Corsac.Lang.X64.X64Backend.RefExchangeStub, 0));
                if (i.Dest is not null) MovL(V(i.Dest), Rax);
                return;
            case Corsac.Lang.X86.MachineIntrinsics.RefCompareExchange:
                // The compare-exchange's: slot RCX, value RDX, expected RAX
                // (CMPXCHG's own); what the slot held in RAX.
                MovL(Rcx, RL(i.Operands[0]));
                MovL(Rdx, RL(i.Operands[2]));
                MovL(Rax, RL(i.Operands[1]));
                Emit(MOp.CallKeepCas, MImm.Sym(Corsac.Lang.X64.X64Backend.RefCompareExchangeStub, 0));
                if (i.Dest is not null) MovL(V(i.Dest), Rax);
                return;
            case Corsac.Lang.X86.MachineIntrinsics.PerCpuRead:
                // A word of this processor's own area, gs-relative: one
                // instruction, so no preemption falls between finding the
                // processor and reading its word (Linux's this_cpu_read). The
                // offset a constant where it fits a displacement, else in a
                // register.
                if (i.Dest is not null) MovL(V(i.Dest), PerCpuWord(i.Operands[0]));
                return;
            case Corsac.Lang.X86.MachineIntrinsics.PerCpuWrite:
            {
                MReg value = RL(i.Operands[1]);
                MovL(PerCpuWord(i.Operands[0]), value);
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.Out8:
            case Corsac.Lang.X86.MachineIntrinsics.Out16:
            case Corsac.Lang.X86.MachineIntrinsics.Out32:
            {
                int w = name == Corsac.Lang.X86.MachineIntrinsics.Out8 ? 1 : name == Corsac.Lang.X86.MachineIntrinsics.Out16 ? 2 : 4;
                MReg port = RL(i.Operands[0]);
                MReg value = RL(i.Operands[1]);
                MovL(Rdx, port);
                MovL(Rax, value);
                EmitW(MOp.Out, w);
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.InString16:
            case Corsac.Lang.X86.MachineIntrinsics.OutString16:
            {
                // (port, address, count): rep insw into [RDI] or rep outsw from [RSI].
                bool reading = name == Corsac.Lang.X86.MachineIntrinsics.InString16;
                MReg port = RL(i.Operands[0]);
                MReg address = RL(i.Operands[1]);
                MReg count = RL(i.Operands[2]);
                MovL(Rdx, port, 4);
                MovL(reading ? Rdi : Rsi, address, 8);
                MovL(Rcx, count, 4);
                Emit(reading ? MOp.RepInsw : MOp.RepOutsw);
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.Lidt:
                Emit(MOp.Lidt, RL(i.Operands[0]));
                return;
            case Corsac.Lang.X86.MachineIntrinsics.Lgdt:
                Emit(MOp.Lgdt, RL(i.Operands[0]));
                return;
            case Corsac.Lang.X86.MachineIntrinsics.Invlpg:
                Emit(MOp.Invlpg, RL(i.Operands[0]));
                return;
            case Corsac.Lang.X86.MachineIntrinsics.CpuidPresent:
                // Every processor that runs long mode has CPUID.
                if (i.Dest is not null) EmitW(MOp.Mov, 4, V(i.Dest), Imm(1));
                return;
            // THE STREAMING ROUTINES' INSTRUCTIONS (Runtime.StreamCopy,
            // StreamFill): a cache line stored around the caches by eight
            // MOVNTI of a general register -- as Linux's uncached copy for
            // x86-64 stores it -- where i386 goes through MM0 by MOVNTQ.
            // No packed register, so nothing to leave at the end but the
            // fence that orders the stores before what follows. (The
            // prefetch ahead is the family's: Select.Intrinsics.cs.)
            case Corsac.Lang.X86.MachineIntrinsics.StreamCopy64:
            case Corsac.Lang.X86.MachineIntrinsics.StreamFill64:
            {
                MReg to = RL(i.Operands[0]), from = RL(i.Operands[1]);
                bool fill = name == Corsac.Lang.X86.MachineIntrinsics.StreamFill64;
                MReg word = Temp();
                if (fill) MovL(word, new MMem(from, 0), 8);
                for (int k = 0; k < 64; k += 8)
                {
                    if (!fill) MovL(word, new MMem(from, k), 8);
                    Emit(new MInstr(MOp.Movnti, new MMem(to, k), word) { Width = 8, Probed = true });
                }
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.StreamEnd:
                Emit(new MInstr(MOp.Sfence) { Probed = true });
                return;
            // THE RUNTIME'S SCANS (Runtime.PackedStringFind and its kin), which
            // i386 makes on the MMX registers: long mode's on XMM registers
            // the allocator gives, by SSE2, with no state to leave after.
            // FOUR UNITS against the four at the pattern: PCMPEQW on the
            // low halves, the words packed to bytes, the four bytes out by
            // MOVD -- a byte 0xFF for each unit that is equal, as i386's.
            // EIGHT: sixteen bytes of the string against the pattern
            // doubled (PUNPCKLQDQ), the words packed to bytes, a bit of
            // each by PMOVMSKB -- the low eight are the units'.
            case Corsac.Lang.X86.MachineIntrinsics.PackedFindChar4:
            case Corsac.Lang.X86.MachineIntrinsics.PackedFindChar8:
            {
                if (i.Dest is null) return;
                bool eight = name == Corsac.Lang.X86.MachineIntrinsics.PackedFindChar8;
                MReg units = XTemp(), pattern = XTemp();
                MovF(units, new MMem(RL(i.Operands[0]), 0), eight ? 16 : 8);
                MovF(pattern, new MMem(RL(i.Operands[1]), 0), 8);
                if (eight) Emit(new MInstr(MOp.XmmOpV, Imm(0x66), Imm(0x6C), pattern, pattern) { Probed = true });
                Emit(new MInstr(MOp.XmmOpV, Imm(0x66), Imm(0x75), units, pattern) { Probed = true });
                Emit(new MInstr(MOp.XmmOpV, Imm(0x66), Imm(0x63), units, units) { Probed = true });
                if (eight)
                {
                    Emit(new MInstr(MOp.XmmMaskV, V(i.Dest), units) { Probed = true });
                    EmitW(MOp.And, 4, V(i.Dest), Imm(0xFF));
                }
                else EmitW(MOp.MovGx, 4, V(i.Dest), units);
                return;
            }
            // EIGHT BYTES ALIKE (-1) OR NOT (0): a word of each, no packed
            // register at all. The words' difference negated carries where
            // they differ; SBB spreads the carry, NOT turns it round. All in
            // a register of its own, the result moved out last: the
            // destination may be one of the addresses' registers.
            case Corsac.Lang.X86.MachineIntrinsics.PackedEqual8:
            {
                if (i.Dest is null) return;
                MReg a = RL(i.Operands[0]), b = RL(i.Operands[1]), word = Temp();
                MovL(word, new MMem(a, 0), 8);
                EmitW(MOp.Xor, 8, word, new MMem(b, 0));
                EmitW(MOp.Neg, 8, word);
                EmitW(MOp.Sbb, 4, word, word);
                EmitW(MOp.Not, 4, word);
                MovL(V(i.Dest), word, 4);
                return;
            }
            // (The MMX registers' leave, EMMS or FEMMS: nothing to leave here.)
            case Corsac.Lang.X86.MachineIntrinsics.PackedEnd:
            case Corsac.Lang.X86.MachineIntrinsics.PackedEndFast:
                return;
            case Corsac.Lang.X86.MachineIntrinsics.ReadCr:
            {
                long number = ControlRegisterL(i.Operands[0]);
                MReg into = i.Dest is not null ? V(i.Dest) : Temp();
                Emit(MOp.MovFromCr, into, Imm(number));
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.WriteCr:
            {
                long number = ControlRegisterL(i.Operands[0]);
                Emit(MOp.MovToCr, Imm(number), RL(i.Operands[1]));
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.LoadSegments:
                MovL(Rax, RL(i.Operands[0]), 4);
                Emit(MOp.LoadSegments);
                return;
            case Corsac.Lang.X86.MachineIntrinsics.ReadMsr:
            case Corsac.Lang.X86.MachineIntrinsics.ReadTsc:
            {
                if (name == Corsac.Lang.X86.MachineIntrinsics.ReadMsr)
                {
                    MovL(Rcx, RL(i.Operands[0]), 4);
                    Emit(MOp.Rdmsr);
                }
                else
                {
                    Emit(MOp.Rdtsc);
                }
                if (i.Dest is not null)
                {
                    // EDX:EAX into one register.
                    MReg high = Temp();
                    MReg low = Temp();
                    MovL(high, Rdx, 4);
                    MovL(low, Rax, 4);
                    EmitW(MOp.Shl, 8, high, Imm(32));
                    EmitW(MOp.Or, 8, high, low);
                    MovL(V(i.Dest), high, 8);
                }
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.WriteMsr:
            {
                MReg msr = RL(i.Operands[0]);
                MReg value = RL(i.Operands[1]);
                MReg high = Temp();
                MovL(high, value, 8);
                EmitW(MOp.Shr, 8, high, Imm(32));
                MovL(Rcx, msr, 4);
                MovL(Rax, value, 4);
                MovL(Rdx, high, 4);
                Emit(MOp.Wrmsr);
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.Cpuid:
            {
                MReg leaf = RL(i.Operands[0]);
                MReg subleaf = RL(i.Operands[1]);
                MReg into = RL(i.Operands[2]);
                MReg keep = Temp();
                MovL(keep, into, 8);
                MovL(Rax, leaf, 4);
                MovL(Rcx, subleaf, 4);
                Emit(MOp.Cpuid);
                MReg a = Temp(), b = Temp(), c = Temp(), d = Temp();
                MovL(a, Rax, 4);
                MovL(b, MReg.Of(Gpr.Rbx), 4);
                MovL(c, Rcx, 4);
                MovL(d, Rdx, 4);
                MovL(new MMem(keep, 0), a, 4);
                MovL(new MMem(keep, 4), b, 4);
                MovL(new MMem(keep, 8), c, 4);
                MovL(new MMem(keep, 12), d, 4);
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.SwapGs:
                Emit(MOp.Swapgs);
                return;
            default:
                Error($"the machine intrinsic {name} has no x86-64 form");
                return;
        }
    }
}
