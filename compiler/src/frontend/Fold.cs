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
///   * It computes every step at the type C# gives it, wrapped to that width
///     and signed or unsigned as that type is. Folding `a * b` for two ints and
///     keeping a 64-bit result would make a constant expression produce a
///     different answer from the same expression with a variable in it, which
///     is the one thing an optimisation may never do.
/// </summary>
public static class Fold
{
    /// <summary>
    /// The value of an expression, if it has one that is known now.
    ///
    /// Recursive, so `2 * 3 + 4` folds whole rather than one operator at a
    /// time -- which matters because the tree is walked from the top and a
    /// partial answer would emit the outer operation anyway.
    ///
    /// <paramref name="named"/> answers what a NAME is worth, and its type,
    /// for the caller that has a table of them, and null for anything it does
    /// not know.
    ///
    /// The folder itself knows nothing about scopes, and should not: it is
    /// asked from the code generator, where a name has already become a
    /// constant, and from the checker while it is BUILDING that table --
    /// `const int ImmMin = -(1 &lt;&lt; (ImmBits - 1));` needs the value of
    /// ImmBits and only the checker knows it.
    /// </summary>
    public static bool TryConst(Expr e, out long value, Func<Expr, (long Value, Prim Type)?>? named = null)
        => TryConst(e, out value, out _, named);

    /// <summary>
    /// The value of a constant expression and the type C# gives it.
    ///
    /// EVERY STEP AT ITS OWN TYPE, as C# works a constant out: the operands
    /// promoted the way the operator's own overload takes them, the answer
    /// wrapped to that type's width, and division, remainder, right shifts and
    /// comparisons signed or unsigned as that type is. `ulong.MaxValue / 2`
    /// is not 0, `unchecked(int.MaxValue + 1) &lt; 0` is true, and
    /// `1 &lt;&lt; 33` is 2 because an int's shift count is taken mod 32.
    /// The value comes back in the IR's spelling: sign-extended for a signed
    /// type, zero-extended for uint, and the bits for a ulong.
    /// </summary>
    public static bool TryConst(Expr e, out long value, out Prim type, Func<Expr, (long Value, Prim Type)?>? named = null)
    {
        value = 0;
        type = Prim.Void;

        if (named?.Invoke(e) is { } known)
        {
            type = known.Type;
            value = Wrap(known.Value, known.Type);
            return true;
        }

        switch (e)
        {
            case LiteralExpr { Kind: Lit.Int } l:
                type = Binder.IntLiteralType(l).Prim;
                value = Wrap(l.IntValue, type);
                return true;

            case LiteralExpr { Kind: Lit.Char } l:
                type = Prim.Char;
                value = Wrap(l.IntValue, type);
                return true;

            case LiteralExpr { Kind: Lit.Bool } l:
                type = Prim.Bool;
                value = l.IntValue != 0 ? 1 : 0;
                return true;

            case UnaryExpr u:
            {
                if (!TryConst(u.Operand, out long v, out Prim operand, named))
                {
                    return false;
                }

                if (u.Op is UnOp.Checked or UnOp.Unchecked)
                {
                    value = v;
                    type = operand;
                    return true;
                }

                if (u.Op == UnOp.Not)
                {
                    if (operand != Prim.Bool) { return false; }
                    value = v == 0 ? 1 : 0;
                    type = Prim.Bool;
                    return true;
                }

                if (!IsWhole(operand))
                {
                    return false;
                }

                type = Promoted(operand);
                switch (u.Op)
                {
                    // A uint negated is a long, as `-x` is for a uint x: C#
                    // has no unsigned negation, and none at all for a ulong.
                    case UnOp.Neg:
                        if (type == Prim.U32) { type = Prim.I64; }
                        if (type is Prim.U64 or Prim.NUInt) { return false; }
                        value = Wrap(unchecked(0 - v), type);
                        return true;
                    case UnOp.Plus: value = v; return true;
                    case UnOp.BitNot: value = Wrap(~v, type); return true;
                    default: return false;
                }
            }

            // Integral and enum casts remain constant expressions in C#.
            // Apply the target width here rather than merely dropping the
            // cast: narrowing a constant is observable, especially in the
            // unchecked masks used throughout the ISA tables.
            // A cast to a pointer, an array or a nullable makes no constant.
            case CastExpr c:
            {
                if (c.Type.PointerDepth > 0 || c.Type.ArrayRank > 0 || c.Type.Nullable
                    || !TryConst(c.Operand, out long v, out Prim operand, named))
                {
                    return false;
                }

                // Enums and native whole-word types are represented in a
                // machine word; their cast changes the static type, not these
                // bits.
                type = Keyword(c.Type.Name) ?? operand;
                if (type != operand && (!IsWhole(operand) || !IsWhole(type)))
                {
                    return false;
                }
                value = Wrap(v, type);
                return true;
            }

            case BinaryExpr b:
            {
                // Short-circuit operators are control flow. Folding them is
                // legal only because both sides are constant, which means
                // neither has a side effect to skip -- and both sides ARE
                // required to be constant to get here.
                if (!TryConst(b.Left, out long x, out Prim left, named) || !TryConst(b.Right, out long y, out Prim right, named))
                {
                    return false;
                }
                return Apply(b.Op, x, left, y, right, out value, out type);
            }

            default:
                return false;
        }
    }

    private static bool Apply(BinOp op, long x, Prim left, long y, Prim right, out long value, out Prim type)
    {
        value = 0;
        type = Prim.Void;

        if (left == Prim.Bool || right == Prim.Bool)
        {
            if (left != right) { return false; }
            type = Prim.Bool;
            switch (op)
            {
                case BinOp.And or BinOp.AndAlso: value = x & y; return true;
                case BinOp.Or or BinOp.OrElse: value = x | y; return true;
                case BinOp.Xor or BinOp.Ne: value = x ^ y; return true;
                case BinOp.Eq: value = x ^ y ^ 1; return true;
                default: return false;
            }
        }

        if (!IsWhole(left) || !IsWhole(right))
        {
            return false;
        }

        // A SHIFT IS ITS LEFT SIDE'S TYPE, and the count is taken modulo that
        // type's width, as C# defines it and as the machine's own shifts do.
        if (op is BinOp.Shl or BinOp.Shr or BinOp.UShr)
        {
            type = Promoted(left);
            int count = (int)(y & (Wide(type) ? 63 : 31));
            value = op switch
            {
                BinOp.Shl => Wrap(unchecked(x << count), type),
                BinOp.Shr when Unsigned(type) => Wrap((long)((ulong)x >> count), type),
                BinOp.Shr => x >> count,
                _ => Wrap(Wide(type) ? (long)((ulong)x >> count) : (long)((uint)x >> count), type),
            };
            return true;
        }

        if (Common(left, x, right, y) is not Prim common)
        {
            return false;
        }
        type = common;
        bool unsigned = Unsigned(common);

        switch (op)
        {
            case BinOp.Add: value = Wrap(unchecked(x + y), type); return true;
            case BinOp.Sub: value = Wrap(unchecked(x - y), type); return true;
            case BinOp.Mul: value = Wrap(unchecked(x * y), type); return true;

            // LEFT TO THE MACHINE. Dividing by zero traps on this processor,
            // and that trap is the program being told what it did. A folded
            // answer would be a silent number instead, and the fault would move
            // from where the mistake is to wherever the number ended up.
            // A DIVISOR OF -1 IS NOT LEFT TO IT. A constant divided by -1 is
            // its negation, and MinValue's wraps back to itself, which is C#'s
            // unchecked answer; the remainder is 0. Only a division made at
            // run time throws.
            case BinOp.Div:
                if (y == 0) { return false; }
                value = unsigned ? Wrap((long)((ulong)x / (ulong)y), type)
                      : y == -1 ? Wrap(unchecked(0 - x), type)
                      : x / y;
                return true;

            case BinOp.Rem:
                if (y == 0) { return false; }
                value = unsigned ? Wrap((long)((ulong)x % (ulong)y), type)
                      : y == -1 ? 0
                      : x % y;
                return true;

            case BinOp.And: value = x & y; return true;
            case BinOp.Or:  value = x | y; return true;
            case BinOp.Xor: value = x ^ y; return true;
        }

        // A comparison is a bool, made at the operands' common type.
        int order = unsigned ? ((ulong)x).CompareTo((ulong)y) : x.CompareTo(y);
        type = Prim.Bool;
        switch (op)
        {
            case BinOp.Eq: value = order == 0 ? 1 : 0; return true;
            case BinOp.Ne: value = order != 0 ? 1 : 0; return true;
            case BinOp.Lt: value = order <  0 ? 1 : 0; return true;
            case BinOp.Le: value = order <= 0 ? 1 : 0; return true;
            case BinOp.Gt: value = order >  0 ? 1 : 0; return true;
            case BinOp.Ge: value = order >= 0 ? 1 : 0; return true;
            default: return false;
        }
    }

    /// <summary>
    /// The type a keyword names, for the whole types a constant can be: a
    /// native integer is as wide as the word, so a cast to one keeps what the
    /// word keeps.
    /// </summary>
    public static Prim? Keyword(string name) => name switch
    {
        "bool" => Prim.Bool,
        "sbyte" => Prim.I8,
        "byte" => Prim.U8,
        "short" => Prim.I16,
        "ushort" => Prim.U16,
        "char" => Prim.Char,
        "int" => Prim.I32,
        "uint" => Prim.U32,
        "long" => Prim.I64,
        "ulong" => Prim.U64,
        "nint" => Prim.NInt,
        "nuint" => Prim.NUInt,
        _ => null,
    };

    /// <summary>The type a constant of this type folds at: an enum's is the integer it is stored as.</summary>
    public static Prim Whole(Type t) => t.IsEnumValue && t.Symbol is { } e ? e.EnumUnderlying : t.Prim;

    private static bool IsWhole(Prim p) => p is Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64
        or Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64 or Prim.Char or Prim.NInt or Prim.NUInt;

    /// <summary>The type an operator computes in for an operand of this one: the narrow types are ints.</summary>
    private static Prim Promoted(Prim p) => p is Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16 or Prim.Char ? Prim.I32 : p;

    private static bool Unsigned(Prim p) => p is Prim.U32 or Prim.U64 or Prim.NUInt;

    private static bool Wide(Prim p) => p is Prim.I64 or Prim.U64 || (p is Prim.NInt or Prim.NUInt && Target.Current.WordSize == 8);

    /// <summary>A 64-bit answer cut to a type's width, in the IR's spelling of that type.</summary>
    private static long Wrap(long v, Prim p) => p switch
    {
        Prim.I8 => unchecked((sbyte)v),
        Prim.U8 => unchecked((byte)v),
        Prim.I16 => unchecked((short)v),
        Prim.U16 or Prim.Char => unchecked((ushort)v),
        Prim.I32 => unchecked((int)v),
        Prim.U32 => unchecked((uint)v),
        Prim.NInt => Target.Current.WordSize == 4 ? unchecked((int)v) : v,
        Prim.NUInt => Target.Current.WordSize == 4 ? unchecked((uint)v) : v,
        Prim.Bool => v != 0 ? 1 : 0,
        _ => v,
    };

    /// <summary>
    /// The type both sides of an arithmetic, bitwise or comparison operator
    /// meet at: the predefined overload C#'s overload resolution picks for
    /// two operands of these types, or null where it would pick none.
    ///
    /// Both sides are constants here, so C#'s constant conversions take part:
    /// an int constant that is not negative converts to uint, ulong and nuint,
    /// and a long one to ulong. `x + 1` for a uint x is a uint, where
    /// `x + -1` is a long. The narrow unsigned types and char convert to the
    /// unsigned ones of their own accord; sbyte and short do not.
    /// </summary>
    private static Prim? Common(Prim a, long x, Prim b, long y)
    {
        if (Promoted(a) == Promoted(b))
        {
            return Promoted(a);
        }

        // One rank for the side that is the wider or the unsigned type, so the
        // rules below are written once.
        if (Rank(Promoted(a)) < Rank(Promoted(b)))
        {
            (a, x, b, y) = (b, y, a, x);
        }

        // `b` meets `a`, the higher of the two. Whether b converts to an
        // unsigned a: the unsigned narrow types and char do, as does a
        // constant int or long that is not negative.
        bool fits = b is Prim.U8 or Prim.U16 or Prim.Char or Prim.U32 || (b is Prim.I32 or Prim.I64 && y >= 0);
        switch (Promoted(a))
        {
            case Prim.U32: return fits ? Prim.U32 : Prim.I64;
            case Prim.I64: return Promoted(b) == Prim.NUInt ? (x >= 0 ? Prim.U64 : null) : Prim.I64;
            case Prim.U64: return fits || Promoted(b) == Prim.NUInt ? Prim.U64 : null;
            case Prim.NInt: return Promoted(b) == Prim.U32 ? Prim.I64 : Prim.NInt;
            case Prim.NUInt: return Promoted(b) == Prim.NInt ? null : fits ? Prim.NUInt : null;
            default: return null;
        }
    }

    /// <summary>The order Common takes two operand types in: int lowest, then uint, nint, nuint, long, ulong.</summary>
    private static int Rank(Prim p) => p switch
    {
        Prim.I32 => 0,
        Prim.U32 => 1,
        Prim.NInt => 2,
        Prim.NUInt => 3,
        Prim.I64 => 4,
        _ => 5,
    };

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
