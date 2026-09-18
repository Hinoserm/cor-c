#nullable enable
namespace Corsac.Lang;

/// <summary>
/// Works out at COMPILE TIME what does not need working out at run time.
///
/// The oldest optimisation there is, and on this machine the most valuable one
/// per line of compiler: every processor is interpreted, so an instruction not
/// emitted is worth far more here than the same instruction on real hardware.
/// `new long[16]` was three instructions and a multiply to discover that
/// sixteen eights are a hundred and twenty-eight.
///
/// WHAT IT WILL NOT DO, which matters more than what it will:
///
///   * It answers only for INTEGERS. Folding floating point means this compiler
///     and the machine's own arithmetic unit have to agree bit for bit about
///     rounding, and if they ever disagree a program computes one answer when a
///     value is a constant and a different one when it is not. That is the kind
///     of bug nobody finds.
///
///   * It refuses anything the machine would refuse. Division by zero is a trap
///     on this processor, and a compiler that quietly folded it would turn a
///     program that stops with a diagnostic into one that carries on with a
///     number nobody chose. If it cannot be folded honestly it is left alone
///     and the machine deals with it at run time, exactly as before.
///
///   * It computes in 64 bits and then checks the answer still fits what the
///     expression is typed as. Folding `a * b` for two ints and keeping a
///     64-bit result would make a constant expression produce a different
///     answer from the same expression with a variable in it, which is the one
///     thing an optimisation may never do.
/// </summary>
public static class Fold
{
    /// <summary>
    /// The value of an expression, if it has one that is known now.
    ///
    /// Recursive, so `2 * 3 + 4` folds whole rather than one operator at a
    /// time -- which matters because the tree is walked from the top and a
    /// partial answer would emit the outer operation anyway.
    /// </summary>
    /// <summary>
    /// <paramref name="named"/> answers what a NAME is worth, for the caller
    /// that has a table of them, and null for anything it does not know.
    ///
    /// The folder itself knows nothing about scopes, and should not: it is
    /// asked from the code generator, where a name has already become a
    /// constant, and from the checker while it is BUILDING that table --
    /// `const int ImmMin = -(1 &lt;&lt; (ImmBits - 1));` needs the value of
    /// ImmBits and only the checker knows it.
    /// </summary>
    public static bool TryConst(Expr e, out long value, Func<Expr, long?>? named = null)
    {
        value = 0;

        if (named?.Invoke(e) is long known)
        {
            value = known;
            return true;
        }

        switch (e)
        {
            case LiteralExpr { Kind: Lit.Int or Lit.Char or Lit.Bool } l:
                value = l.IntValue;
                return true;

            case UnaryExpr u:
            {
                if (!TryConst(u.Operand, out long v, named))
                {
                    return false;
                }

                switch (u.Op)
                {
                    case UnOp.Checked:
                    case UnOp.Unchecked:
                        value = v;
                        return true;
                    case UnOp.Neg: value = -v; return true;
                    case UnOp.Not: value = v == 0 ? 1 : 0; return true;
                    case UnOp.BitNot: value = ~v; return true;
                    default: return false;
                }
            }

            // Integral and enum casts remain constant expressions in C#.
            // Apply the target width here rather than merely dropping the
            // cast: narrowing a constant is observable, especially in the
            // unchecked masks used throughout the ISA tables.
            case CastExpr c:
            {
                if (!TryConst(c.Operand, out long v, named))
                {
                    return false;
                }

                value = c.Type.Name switch
                {
                    "sbyte" => unchecked((sbyte)v),
                    "byte" => unchecked((byte)v),
                    "short" => unchecked((short)v),
                    "ushort" or "char" => unchecked((ushort)v),
                    "int" => unchecked((int)v),
                    "uint" => unchecked((uint)v),
                    // A native integer is as wide as the word: a cast to one
                    // keeps what the word keeps.
                    "nint" => Target.Current.WordSize == 4 ? unchecked((int)v) : v,
                    "nuint" => Target.Current.WordSize == 4 ? unchecked((uint)v) : v,
                    // Enums and native whole-word types are represented in a
                    // machine word; their cast changes the static type, not
                    // these bits.
                    _ => v,
                };
                return true;
            }

            case BinaryExpr b:
            {
                // Short-circuit operators are control flow. Folding them is
                // legal only because both sides are constant, which means
                // neither has a side effect to skip -- and both sides ARE
                // required to be constant to get here.
                if (!TryConst(b.Left, out long x, named) || !TryConst(b.Right, out long y, named))
                {
                    return false;
                }
                return Apply(b.Op, x, y, out value);
            }

            default:
                return false;
        }
    }

    private static bool Apply(BinOp op, long x, long y, out long value)
    {
        value = 0;

        switch (op)
        {
            case BinOp.Add: value = unchecked(x + y); return true;
            case BinOp.Sub: value = unchecked(x - y); return true;
            case BinOp.Mul: value = unchecked(x * y); return true;

            // LEFT TO THE MACHINE. Dividing by zero traps on this processor,
            // and that trap is the program being told what it did. A folded
            // answer would be a silent number instead, and the fault would move
            // from where the mistake is to wherever the number ended up.
            case BinOp.Div:
                if (y == 0) { return false; }
                if (x == long.MinValue && y == -1) { return false; }   // and this overflows
                value = x / y;
                return true;

            case BinOp.Rem:
                if (y == 0) { return false; }
                if (x == long.MinValue && y == -1) { return false; }
                value = x % y;
                return true;

            // A shift by more than the width of the register is undefined on
            // most machines and defined differently on the rest. Whatever this
            // one does, the compiler must not have an opinion of its own.
            case BinOp.Shl:
                if (y is < 0 or > 63) { return false; }
                value = unchecked(x << (int)y);
                return true;

            case BinOp.Shr:
                if (y is < 0 or > 63) { return false; }
                value = x >> (int)y;
                return true;

            case BinOp.And: value = x & y; return true;
            case BinOp.Or:  value = x | y; return true;
            case BinOp.Xor: value = x ^ y; return true;

            case BinOp.Eq:  value = x == y ? 1 : 0; return true;
            case BinOp.Ne:  value = x != y ? 1 : 0; return true;
            case BinOp.Lt:  value = x <  y ? 1 : 0; return true;
            case BinOp.Le:  value = x <= y ? 1 : 0; return true;
            case BinOp.Gt:  value = x >  y ? 1 : 0; return true;
            case BinOp.Ge:  value = x >= y ? 1 : 0; return true;

            case BinOp.AndAlso: value = x != 0 && y != 0 ? 1 : 0; return true;
            case BinOp.OrElse:  value = x != 0 || y != 0 ? 1 : 0; return true;

            default: return false;
        }
    }

    /// <summary>
    /// Whether a folded 64-bit answer is still the answer at the width the
    /// expression is typed as.
    ///
    /// This is the check that keeps folding HONEST. Two ints multiplied
    /// overflow to a 32-bit result on this machine; folding them in 64 bits and
    /// keeping the wide answer would make `a * b` mean one thing when the
    /// compiler can see the values and another when it cannot. An optimisation
    /// that changes an answer is not an optimisation.
    /// </summary>
    public static bool Fits(long value, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.Prim switch
        {
            Prim.I8 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
            Prim.I16 => value >= short.MinValue && value <= short.MaxValue,
            Prim.I32 => value >= int.MinValue && value <= int.MaxValue,
            Prim.I64 => true,
            Prim.U8 => value >= byte.MinValue && value <= byte.MaxValue,
            Prim.U16 or Prim.Char => value >= ushort.MinValue
                && value <= ushort.MaxValue,
            Prim.U32 => value >= uint.MinValue && value <= uint.MaxValue,
            Prim.U64 => true,
            Prim.NInt => Target.Current.WordSize == 8
                || (value >= int.MinValue && value <= int.MaxValue),
            Prim.NUInt => Target.Current.WordSize == 8
                ? value >= 0
                : value >= uint.MinValue && value <= uint.MaxValue,
            Prim.Bool => value is 0 or 1,
            _ => false,
        };
    }
}
