using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// FLOATS IN THE XMM REGISTERS, where --cpu has SSE (floats) and SSE2
/// (doubles) and the code owns that state (hosted code: Lowering.OwnsFpuState):
/// each float value a virtual register of the allocator's XMM class
/// (MFunction.XmmRegs), worked on by the scalar instructions -- ADDSS and its
/// kin -- in registers across instructions and blocks, where the x87 path
/// loads every operand from its home and stores every result back. What the
/// scalar set has no instruction for (a 64-bit integer's conversion, an
/// unsigned one, a call's result in ST0) goes through the x87 as before, the
/// value moved between the two through a slot of the frame (Fld, Fstp).
///
/// A function with a packed operation too (MachineIntrinsics.PackedOpPrefix),
/// whose packed registers the selector numbers itself, splits them: the
/// class XMM0 to XMM3, the packed values XMM4 to XMM7 (MFunction.XmmShared).
/// </summary>
internal sealed partial class Selector
{
    /// <summary>Whether F32 values are of the XMM class here, and F64 values.</summary>
    private bool _xmm32, _xmm64;

    /// <summary>A float type of the XMM class here.</summary>
    private bool InXmm(IrType t) => t == IrType.F32 ? _xmm32 : t == IrType.F64 && _xmm64;

    /// <summary>Whether a function names a packed operation, whose XMM registers the selector numbers itself (MFunction.XmmShared).</summary>
    private static bool UsesPackedRegisters(Function f)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal))
                    return true;
        return false;
    }

    /// <summary>The XMM-class register of a float value.</summary>
    private MReg X(VReg v)
    {
        MReg? r = _lo[v.Id];
        if (r is null)
        {
            r = _m.NewReg();
            _lo[v.Id] = r;
            _m.XmmRegs.Add(r.Id);
        }
        return r;
    }

    private MReg X(Operand o) => o is RegOperand r ? X(r.Reg) : throw new InvalidOperationException($"{o} is not a float register");

    /// <summary>A new XMM-class temporary.</summary>
    private MReg XTemp()
    {
        MReg r = _m.NewReg();
        _m.XmmRegs.Add(r.Id);
        return r;
    }

    private void MovF(MOperand to, MOperand from, int width)
    {
        if (to is MReg a && from is MReg b && a.Id == b.Id) return;
        EmitW(MOp.MovF, width, to, from);
    }

    /// <summary>
    /// Eight bytes of the frame a value crosses between the XMM registers and
    /// the x87 or the general ones by: stored and read back at once, so one
    /// serves the whole function.
    /// </summary>
    private MMem FloatScratch => MMem.Frame(_m.Frame.FloatScratch);

    /// <summary>
    /// A FLOAT CONSTANT, from the object's read-only pool (X86Backend.
    /// FloatPoolName): one load, where its bits stored into the frame and read
    /// back were two instructions and a store to forward from.
    /// </summary>
    private static MMem FloatConstant(long bits, int width) => SymbolMem(X86Backend.FloatPoolName(bits, width), 0);

    /// <summary>The bits of a float constant: an immediate, or a register a constant was copied into in this block.</summary>
    private bool KnownBits(Operand o, out long bits)
    {
        switch (o)
        {
            case ImmOperand imm:
                bits = imm.Value;
                return true;
            case RegOperand r when _constants.TryGetValue(r.Reg.Id, out long k):
                bits = k;
                return true;
            case RegOperand r when _definitions.TryGetValue(r.Reg, out Instr? def) && def is { Op: Opcode.Copy, Operands: [ImmOperand wide] }:
                bits = wide.Value;
                return true;
        }
        bits = 0;
        return false;
    }

    /// <summary>
    /// A FLOAT CONSTANT AS AN INSTRUCTION'S SOURCE: a value made once by Bits
    /// from bits known everywhere -- an immediate, or a register defined once
    /// by one -- read from the pool by the instruction itself (ADDSS xmm, m32),
    /// its own load left unused (Allocator.PruneUnusedValues). Anything else,
    /// its register.
    /// </summary>
    private MOperand XSource(Operand o)
    {
        if (o is RegOperand r && InXmm(r.Reg.Type) && _definitions.TryGetValue(r.Reg, out Instr? def)
            && def is { Op: Opcode.Bits, Operands: [var bitsOf] } && ConstantEverywhere(bitsOf, out long bits))
        {
            int w = FWidth(r.Reg.Type);
            return FloatConstant(w == 4 ? bits & 0xFFFFFFFFL : bits, w);
        }
        return X(o);
    }

    /// <summary>Bits known wherever they are read: an immediate, or a register an immediate is its one definition.</summary>
    private bool ConstantEverywhere(Operand o, out long bits)
    {
        switch (o)
        {
            case ImmOperand imm:
                bits = imm.Value;
                return true;
            case RegOperand r when _definitions.TryGetValue(r.Reg, out Instr? def) && def is { Op: Opcode.Copy, Operands: [ImmOperand known] }:
                bits = known.Value;
                return true;
        }
        bits = 0;
        return false;
    }

    /// <summary>
    /// A float operand's value in memory, for a path that reads its bits or
    /// hands it to the x87: its home, or an XMM value stored to the scratch.
    /// </summary>
    private MMem FloatInMemory(Operand o)
    {
        if (!InXmm(o.Type)) return FHome(o);
        MovF(FloatScratch, X(o), FWidth(o.Type));
        return FloatScratch;
    }

    // ---- the instructions ------------------------------------------------------

    private void SseCopy(VReg d, Operand src) => MovF(X(d), X(src), FWidth(d.Type));

    private void SseAlu(Instr i)
    {
        MOp op = i.Op switch { Opcode.FAdd => MOp.AddF, Opcode.FSub => MOp.SubF, Opcode.FMul => MOp.MulF, _ => MOp.DivF };
        int w = FWidth(i.Dest!.Type);
        Operand left = i.Operands[0], right = i.Operands[1];
        // A constant on the left of a sum or a product goes right, where it is read from the pool.
        if (op is MOp.AddF or MOp.MulF && XSource(left) is MMem && XSource(right) is not MMem) (left, right) = (right, left);
        MReg d = X(i.Dest!), a = X(left);
        MOperand b = XSource(right);
        if (b is MMem)
        {
            MovF(d, a, w);
            EmitW(op, w, d, b);
            return;
        }
        if (d.Id == ((MReg)b).Id && d.Id != a.Id)
        {
            // d = a - d: the two-address form would overwrite d before reading it.
            if (op is MOp.AddF or MOp.MulF)
            {
                EmitW(op, w, d, a);
                return;
            }
            MReg t = XTemp();
            MovF(t, a, w);
            EmitW(op, w, t, b);
            MovF(d, t, w);
            return;
        }
        MovF(d, a, w);
        EmitW(op, w, d, b);
    }

    /// <summary>The sign flipped by XORPS with the pool's mask: -0.0 from 0.0, a NaN's sign too, as .NET's negation has them.</summary>
    private void SseNeg(Instr i)
    {
        int w = FWidth(i.Dest!.Type);
        MReg mask = XTemp();
        MovF(mask, FloatConstant(w == 4 ? 0x80000000L : long.MinValue, w), w);
        MReg d = X(i.Dest!), a = X(i.Operands[0]);
        if (d.Id == a.Id)
        {
            Emit(MOp.XorPs, d, mask);
            return;
        }
        Emit(MOp.XorPs, mask, a);
        MovF(d, mask, w);
    }

    private void SseSqrt(Instr i) => EmitW(MOp.SqrtF, FWidth(i.Dest!.Type), X(i.Dest!), X(i.Operands[0]));

    /// <summary>
    /// UCOMISS/UCOMISD x, y: ZF, PF and CF as FUCOMIP leaves them for ST(0) ?
    /// ST(1), so x is whichever FloatCompare would have loaded second.
    /// </summary>
    private void SseCompare(Operand first, Operand second) => EmitW(MOp.UcomiF, FWidth(first.Type), X(first), XSource(second));

    /// <summary>Between a float and a double, both in the XMM class: CVTSS2SD, CVTSD2SS.</summary>
    private void SseConvert(Instr i) => EmitW(MOp.CvtFToF, FWidth(i.Dest!.Type), X(i.Dest!), X(i.Operands[0]));

    /// <summary>A signed 32-bit integer to a float or a double: CVTSI2SS, CVTSI2SD, from a register or memory.</summary>
    private void SseFromInt(Instr i) => EmitW(MOp.CvtIntToF, FWidth(i.Dest!.Type), X(i.Dest!), R(i.Operands[0]));

    /// <summary>
    /// A FLOAT TO A SIGNED 32-BIT INTEGER, the way .NET's JIT has it: CVTTSS2SI
    /// answers the truncation of every value in range; 0x80000000 -- the
    /// "integer indefinite" of a NaN or an overflow, and int.MinValue itself
    /// -- sends the value to the classification (SelectFloatToInt) for NaN to
    /// 0 and the limits, which answers int.MinValue again for itself.
    /// </summary>
    private void SseToInt(Instr i)
    {
        bool single = FWidth(i.Operands[0].Type) == 4;
        MReg d = Lo(i.Dest!);
        EmitW(single ? MOp.Cvttss2si : MOp.Cvttsd2si, 4, d, X(i.Operands[0]));
        Emit(MOp.Cmp, d, Imm(int.MinValue));
        MBlock exit = _m.NewBlock($"{_cur.Name}.{_splits++}", _cur);
        Jcc(Cond.Ne, exit);
        SelectFloatToInt(i);
        Jmp(exit);
        _cur = exit;
    }

    /// <summary>
    /// A FLOAT TO AN UNSIGNED 32-BIT INTEGER: CVTTSS2SI answers every value
    /// below 2^31 that truncates to no negative number -- (-1, 0) to 0, as
    /// .NET has it; a negative result -- 2^31 and up, -1 and below, NaN -- sends
    /// the value to the classification (SelectFloatToInt), whose limits are
    /// .NET's, and whose in-range path is SseUIntCore's.
    /// </summary>
    private void SseToUInt(Instr i)
    {
        bool single = FWidth(i.Operands[0].Type) == 4;
        MReg d = Lo(i.Dest!);
        EmitW(single ? MOp.Cvttss2si : MOp.Cvttsd2si, 4, d, X(i.Operands[0]));
        Emit(MOp.Test, d, d);
        MBlock exit = _m.NewBlock($"{_cur.Name}.{_splits++}", _cur);
        Jcc(Cond.Ns, exit);
        SelectFloatToInt(i);
        Jmp(exit);
        _cur = exit;
    }

    /// <summary>
    /// A value of [0, 2^32) truncated to an unsigned 32-bit integer: below
    /// 2^31 as it is; from 2^31 less 2^31 -- exact, a power of two the value's
    /// exponent covers -- and the top bit set again.
    /// </summary>
    private void SseUIntCore(Instr i)
    {
        int w = FWidth(i.Operands[0].Type);
        MOp truncate = w == 4 ? MOp.Cvttss2si : MOp.Cvttsd2si;
        MReg d = Lo(i.Dest!), x = X(i.Operands[0]);
        MMem half = FloatConstant(w == 4 ? 0x4F000000L : 0x41E0000000000000L, w);
        MBlock anchor = _cur;
        MBlock high = _m.NewBlock($"{_cur.Name}.{_splits++}", anchor);
        MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", high);
        EmitW(MOp.UcomiF, w, x, half);
        Jcc(Cond.Ae, high);
        Emit(truncate, d, x);
        Jmp(done);
        _cur = high;
        MReg t = XTemp();
        MovF(t, x, w);
        EmitW(MOp.SubF, w, t, half);
        Emit(truncate, d, t);
        Emit(MOp.Xor, d, Imm(int.MinValue));
        Jmp(done);
        _cur = done;
    }

    /// <summary>A float loaded from memory, as it is or widened or narrowed on the way.</summary>
    private void SseLoad(VReg d, MMem m, int size)
    {
        int w = FWidth(d.Type);
        if (size == w) MovF(X(d), m, w);
        // CVTSS2SD from a float in memory; CVTSD2SS from a double (SSE2's,
        // which a double of the XMM class implies, or a float would not be
        // narrowed from one here).
        else EmitW(MOp.CvtFToF, w, X(d), m);
    }

    /// <summary>A float stored, as it is or converted to the width stored.</summary>
    private void SseStore(MMem m, Operand v, int size)
    {
        int w = FWidth(v.Type);
        if (size == w)
        {
            MovF(m, X(v), w);
            return;
        }
        MReg t = XTemp();
        EmitW(MOp.CvtFToF, size, t, X(v));
        MovF(m, t, size);
    }

    /// <summary>
    /// The bits of an integer as a float of the XMM class: a constant from the
    /// pool; a word with MOVD where --cpu has SSE2, else through the scratch;
    /// two words through the scratch.
    /// </summary>
    private void SseFromBits(VReg d, Operand src)
    {
        int w = FWidth(d.Type);
        if (KnownBits(src, out long bits))
        {
            MovF(X(d), FloatConstant(w == 4 ? bits & 0xFFFFFFFFL : bits, w), w);
            return;
        }
        if (w == 4)
        {
            if (Target.Current.X86Profile.Has(X86Feature.Sse2))
            {
                Emit(MOp.MovdToX, X(d), RM(src) is MImm ? R(src) : RM(src));
                return;
            }
            Mov(FloatScratch, RM(src));
            MovF(X(d), FloatScratch, 4);
            return;
        }
        // MOVD each half and PUNPCKLDQ them together: two stores read back as
        // one eight-byte load forward from neither, on the P6, the K7 or the K8.
        (MOperand lo, MOperand hi) = PairRM(src);
        MReg high = XTemp();
        Emit(MOp.MovdToX, X(d), InRegister(lo));
        Emit(MOp.MovdToX, high, InRegister(hi));
        Emit(MOp.Punpckldq, X(d), high);
    }

    /// <summary>A half as MOVD takes it: a register, an immediate moved into one.</summary>
    private MOperand InRegister(MOperand o)
    {
        if (o is not MImm) return o;
        MReg t = Temp();
        Mov(t, o);
        return t;
    }

    /// <summary>A float of the XMM class as an integer's bits.</summary>
    private void SseToBits(VReg d, Operand src)
    {
        if (d.Type == IrType.I32)
        {
            if (Target.Current.X86Profile.Has(X86Feature.Sse2))
            {
                Emit(MOp.MovdFromX, Lo(d), X(src));
                return;
            }
            MovF(FloatScratch, X(src), 4);
            Mov(Lo(d), FloatScratch);
            return;
        }
        MovF(FloatScratch, X(src), 8);
        Mov(Lo(d), FloatScratch);
        Mov(Hi(d), Displaced(FloatScratch, 4));
    }

    /// <summary>A float argument of the XMM class onto the stack: ESP moved, the value stored where a push would have put it.</summary>
    private int SsePush(Operand o)
    {
        int w = FWidth(o.Type);
        Emit(MOp.Sub, Esp, Imm(w));
        MovF(new MMem(Esp, 0), X(o), w);
        return w;
    }
}
