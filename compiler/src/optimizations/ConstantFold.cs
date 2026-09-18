#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Evaluates integer instructions whose operands are all immediates, and
/// decides branches and switches whose condition is one. Purely local:
/// it never looks at where an operand came from, so it only fires after
/// <see cref="ConstantAndCopyPropagation"/> has put the immediates in
/// place, and the pipeline runs them in turn.
///
/// Arithmetic is done in the IR type's width -- I32 wraps at 32 bits, I64
/// at 64 -- with shift counts masked as C# masks them, which is also what
/// the x86 does. Division by zero is left alone so the target's trap
/// still happens where the program wrote it. Floating point is not folded
/// at all: there are no float immediates in this IR, and folding through
/// the host's FPU would bake the host's rounding into the output.
/// </summary>
public sealed class ConstantFold : IPass
{
    public string Name => "fold";

    public void Run(Function f)
    {
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                Instr? folded = Fold(i);
                if (folded is not null)
                {
                    b.Instrs[k] = folded;
                }
            }
        }
    }

    /// <summary>The instruction an all-immediate instruction becomes, or null if it stays.</summary>
    public static Instr? Fold(Instr i)
    {
        switch (i.Op)
        {
            case Opcode.Branch:
                if (IrInfo.IsImm(i.Operands[0], out long cond))
                {
                    // Branch tests the I32 for nonzero, so the width matters:
                    // 0x1_0000_0000 as an I32 is zero.
                    Block taken = IrInfo.Normalise(cond, IrType.I32) != 0 ? i.Targets[0] : i.Targets[1];
                    return new Instr { Op = Opcode.Jump, Targets = { taken }, Line = i.Line };
                }
                return null;

            case Opcode.Switch:
                if (IrInfo.IsImm(i.Operands[0], out long index) && i.Default is not null)
                {
                    long n = IrInfo.Normalise(index, IrType.I32);
                    Block taken = n >= 0 && n < i.Targets.Count ? i.Targets[(int)n] : i.Default;
                    return new Instr { Op = Opcode.Jump, Targets = { taken }, Line = i.Line };
                }
                return null;
        }

        if (i.Dest is null || !i.Dest.Type.IsInt())
        {
            return null;
        }
        foreach (Operand o in i.Operands)
        {
            if (o is not ImmOperand)
            {
                return null;
            }
        }

        long? result = i.Operands.Count switch
        {
            1 => Unary(i.Op, ((ImmOperand)i.Operands[0]).Value, i.Operands[0].Type),
            2 => Binary(i.Op, ((ImmOperand)i.Operands[0]).Value, ((ImmOperand)i.Operands[1]).Value, i.Operands[0].Type),
            _ => null,
        };
        if (result is null)
        {
            return null;
        }
        return IrInfo.CopyOf(i, new ImmOperand(IrInfo.Normalise(result.Value, i.Dest.Type), i.Dest.Type));
    }

    private static long? Unary(Opcode op, long a, IrType t)
    {
        a = IrInfo.Normalise(a, t);
        return op switch
        {
            Opcode.Neg => t == IrType.I32 ? unchecked(-(int)a) : unchecked(-a),
            Opcode.Not => ~a,
            Opcode.ByteSwap => Swap(a, t),
            Opcode.SExt8 => (sbyte)a,
            Opcode.SExt16 => (short)a,
            Opcode.ZExt8 => (byte)a,
            Opcode.ZExt16 => (ushort)a,
            Opcode.Trunc64 => (int)a,
            Opcode.SExt32 => (int)a,
            Opcode.ZExt32 => (uint)a,
            _ => null,
        };
    }

    private static long Swap(long value, IrType type)
    {
        ulong input = unchecked((ulong)value), output = 0;
        for (int b = 0; b < type.Bytes(); b++) { output = (output << 8) | (input & 255); input >>= 8; }
        return unchecked((long)output);
    }

    private static long? Binary(Opcode op, long a, long b, IrType t)
    {
        a = IrInfo.Normalise(a, t);
        b = IrInfo.Normalise(b, t);
        bool wide = t == IrType.I64;

        // The shift count is I32 and masked by the width of the shifted
        // operand, per the Ir.cs comment and C#'s own rule.
        int shift = (int)b & (wide ? 63 : 31);

        switch (op)
        {
            case Opcode.Add:
                return unchecked(a + b);
            case Opcode.Sub:
                return unchecked(a - b);
            case Opcode.Mul:
                return wide ? unchecked(a * b) : unchecked((int)a * (int)b);
            case Opcode.And:
                return a & b;
            case Opcode.Or:
                return a | b;
            case Opcode.Xor:
                return a ^ b;
            case Opcode.Shl:
                return wide ? a << shift : (int)a << shift;
            case Opcode.ShrS:
                return wide ? a >> shift : (int)a >> shift;
            case Opcode.ShrU:
                return wide ? (long)((ulong)a >> shift) : (int)((uint)a >> shift);

            case Opcode.DivS:
            case Opcode.RemS:
            case Opcode.DivU:
            case Opcode.RemU:
                if (b == 0)
                {
                    return null;    // the program traps here; leave it to
                }
                if (op == Opcode.DivS)
                {
                    // int.MinValue / -1 overflows and the x86 traps on it;
                    // folding it would silently give the wrong answer.
                    if (wide ? a == long.MinValue && b == -1 : (int)a == int.MinValue && (int)b == -1)
                    {
                        return null;
                    }
                    return wide ? a / b : (int)a / (int)b;
                }
                if (op == Opcode.RemS)
                {
                    if (wide ? a == long.MinValue && b == -1 : (int)a == int.MinValue && (int)b == -1)
                    {
                        return null;
                    }
                    return wide ? a % b : (int)a % (int)b;
                }
                if (op == Opcode.DivU)
                {
                    return wide ? (long)((ulong)a / (ulong)b) : (long)((uint)a / (uint)b);
                }
                return wide ? (long)((ulong)a % (ulong)b) : (long)((uint)a % (uint)b);

            case Opcode.Eq:
                return a == b ? 1 : 0;
            case Opcode.Ne:
                return a != b ? 1 : 0;
            case Opcode.LtS:
                return a < b ? 1 : 0;
            case Opcode.LeS:
                return a <= b ? 1 : 0;
            case Opcode.GtS:
                return a > b ? 1 : 0;
            case Opcode.GeS:
                return a >= b ? 1 : 0;
            case Opcode.LtU:
                return Unsigned(a, wide) < Unsigned(b, wide) ? 1 : 0;
            case Opcode.LeU:
                return Unsigned(a, wide) <= Unsigned(b, wide) ? 1 : 0;
            case Opcode.GtU:
                return Unsigned(a, wide) > Unsigned(b, wide) ? 1 : 0;
            case Opcode.GeU:
                return Unsigned(a, wide) >= Unsigned(b, wide) ? 1 : 0;

            default:
                return null;
        }
    }

    private static ulong Unsigned(long v, bool wide) => wide ? (ulong)v : (uint)v;
}
