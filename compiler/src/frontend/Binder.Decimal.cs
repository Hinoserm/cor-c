#nullable enable
using System.Globalization;

namespace Corsac.Lang;

/// <summary>
/// C#'S DECIMAL: a literal `1.50m`, a `const decimal`, and an expression of
/// decimal constants, folded.
///
/// The type is the class library's struct System.Decimal (Resolve), and
/// everything a decimal does at run time is its operators and conversions.
/// What the language adds is the constant: C# folds `Rate * 12m` when both
/// are constants (C# 12.23), refuses one that overflows (CS0463) or divides
/// by a constant zero (CS0020), and lets a const, a default parameter value
/// and a case label be one. Folded here with the compiler's own System.Decimal,
/// which is .NET's arithmetic exactly, so the answer is the one the program
/// would have computed.
///
/// A decimal constant is evaluated as a construction of the struct from its
/// four words -- `new decimal(lo, mid, hi, negative, scale)`, the constructor
/// .NET's compilers emit for the same thing -- which the binding records
/// beside the expression (BindResult.DecimalValues). A const's value goes
/// back into source as the literal that means it ("-0.0m" keeps a negative
/// zero's sign), where a generic copy or a default argument needs it spelt.
/// </summary>
public sealed partial class Binder
{
    /// <summary>The value of each decimal const field, by owner and name (EvaluateConstant).</summary>
    private readonly Dictionary<(TypeSymbol Owner, string Name), decimal> _decimalConstants = new();

    /// <summary>The constant expressions already refused, so a second evaluation says nothing more.</summary>
    private readonly HashSet<Expr> _decimalRefused = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether a real literal is written with the decimal suffix.</summary>
    internal static bool IsDecimalLiteral(LiteralExpr l)
        => l.Kind == Lit.Real && l.Text.Length > 0 && l.Text[^1] is 'm' or 'M';

    /// <summary>
    /// A decimal literal's value, as C# reads it: the digits rounded to the
    /// nearest decimal, ties to even, every written trailing zero kept as
    /// scale. Null when it is outside decimal's range (CS0594).
    /// </summary>
    internal static decimal? DecimalLiteralValue(LiteralExpr l)
    {
        string text = l.Text[..^1].Replace("_", "");
        bool negative = text.StartsWith('-');
        if (negative) text = text[1..];
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out decimal value))
        {
            return null;
        }
        return negative ? decimal.Negate(value) : value;
    }

    /// <summary>A decimal as the literal that means it, sign and scale kept.</summary>
    private static LiteralExpr DecimalLiteral(decimal value, Node at) => new()
    {
        Kind = Lit.Real,
        Text = (decimal.IsNegative(value) ? "-" : "") + Math.Abs(value).ToString(CultureInfo.InvariantCulture) + "m",
        Line = at.Line, Col = at.Col,
    };

    /// <summary>
    /// The class library's System.Decimal, which `decimal` is: asked for as
    /// any type a program names is (TypeCandidate), so a unit compiled
    /// against the index demands its declaration where it has not got it.
    /// </summary>
    private TypeSymbol? LibraryDecimal()
        => TypeCandidate("Decimal", out _) && LibraryGlobal("Decimal", out TypeSymbol? money) ? money : null;

    /// <summary>Whether a type is the class library's System.Decimal, as it is (not nullable, not an array).</summary>
    private bool IsDecimalType(Type t)
        => !t.Nullable && !t.IsArray && !t.IsPointer && t.Symbol is { } named
           && LibraryGlobal("Decimal", out TypeSymbol? money) && ReferenceEquals(named, money);

    /// <summary>
    /// The expression `at` as the decimal constant `value`: the struct made
    /// from its words, which is what is evaluated for it.
    /// </summary>
    private Type DecimalAs(Expr at, decimal value)
    {
        int[] bits = decimal.GetBits(value);
        NewExpr made = new() { Type = new TypeRef { Name = "decimal", Line = at.Line, Col = at.Col }, Line = at.Line, Col = at.Col, File = at.File };
        for (int i = 0; i < 3; i++)
        {
            made.Args.Add(new LiteralExpr { Kind = Lit.Int, Text = bits[i].ToString(CultureInfo.InvariantCulture), IntValue = bits[i], Line = at.Line, Col = at.Col });
        }
        made.Args.Add(new LiteralExpr { Kind = Lit.Bool, Text = bits[3] < 0 ? "true" : "false", IntValue = bits[3] < 0 ? 1 : 0, Line = at.Line, Col = at.Col });
        int scale = (bits[3] >> 16) & 0xFF;
        made.Args.Add(new LiteralExpr { Kind = Lit.Int, Text = scale.ToString(CultureInfo.InvariantCulture), IntValue = scale, Line = at.Line, Col = at.Col });
        _r.DecimalValues[at] = made;
        return CheckExpr(made);
    }

    /// <summary>`1.50m`: the struct made from its words -- `new decimal(150, 0, 0, false, 2)`.</summary>
    private Type CheckDecimalLiteral(LiteralExpr l)
    {
        if (DecimalLiteralValue(l) is not decimal value)
        {
            Error(l, "Floating-point constant is outside the range of type 'decimal'");
            return Type.Error;
        }
        return DecimalAs(l, value);
    }

    /// <summary>
    /// An operator or a cast over decimal constants, checked already: its
    /// value in its place, when it is a constant expression C# folds. Null
    /// when it is not one, or could not be folded (and was refused).
    /// </summary>
    private Type? FoldDecimal(Expr e)
    {
        TypeSymbol? owner = _thisType ?? _scope;
        // Only over operands that are constants already, which a decimal one
        // is once checked: a chain of a thousand non-constant sums is not
        // walked a thousand times.
        bool Constant(Expr x) => _r.DecimalValues.ContainsKey(x) || ConstantValue(x, owner) is not null;
        bool operands = e switch
        {
            BinaryExpr { Op: BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Rem } b => Constant(b.Left) && Constant(b.Right),
            UnaryExpr { Op: UnOp.Neg or UnOp.Plus or UnOp.Checked or UnOp.Unchecked } u => Constant(u.Operand),
            CastExpr c => Constant(c.Operand) || RealConstant(c.Operand, owner) is not null,
            _ => false,
        };
        return operands && DecimalValue(e, owner, out decimal value, out bool money) && money ? DecimalAs(e, value) : null;
    }

    /// <summary>A decimal const on a type or its bases, evaluated when first asked for.</summary>
    private decimal? FindDecimal(TypeSymbol? owner, string name)
    {
        for (TypeSymbol? t = owner; t != null; t = t.Base)
        {
            EvaluateConstant(t, name);
            if (_decimalConstants.TryGetValue((t, name), out decimal value)) return value;
            if (_constantDeclarations.ContainsKey((t, name))) return null;
        }
        return null;
    }

    /// <summary>A decimal const by the name an initialiser writes, outwards as FindConstantOutward.</summary>
    private decimal? FindDecimalOutward(TypeSymbol owner, string name)
    {
        for (TypeSymbol? t = owner; t != null; t = Outer(t))
        {
            if (FindDecimal(t, name) is decimal found) return found;
        }
        return null;
    }

    /// <summary>A read of a decimal const, by name or as a member: the constant it names.</summary>
    private Type ReadDecimalConstant(Expr at, decimal value)
    {
        Type type = DecimalAs(at, value);
        _r.Resolved[at] = new ConstSym(0, type, Number: value);
        return type;
    }

    /// <summary>
    /// What a decimal constant expression is worth: `money` when its type is
    /// decimal, and otherwise an integer constant's value, which C# converts
    /// to decimal implicitly. Decimal literals and consts, integer constants,
    /// negation, the five operators and casts to decimal -- of an integer
    /// constant, a decimal one, or a floating-point one, as .NET's
    /// conversion rounds it. False when the expression is no constant; one
    /// that overflows or divides by zero is refused once and is none.
    /// </summary>
    private bool DecimalValue(Expr? e, TypeSymbol? owner, out decimal value, out bool money)
    {
        value = 0;
        money = false;
        switch (e)
        {
            case null:
                return false;

            case LiteralExpr l when IsDecimalLiteral(l):
                if (DecimalLiteralValue(l) is not decimal literal) return false;
                value = literal;
                money = true;
                return true;

            case NameExpr local when Lookup(local.Name) is ConstSym { Number: decimal named }:
                value = named;
                money = true;
                return true;

            case NameExpr local when Lookup(local.Name) is not null:
                return Integral(e, owner, out value);

            case NameExpr n when owner is not null && FindDecimalOutward(owner, n.Name) is decimal field:
                value = field;
                money = true;
                return true;

            case MemberExpr m when ConstantOwner(m.Target) is { } named && FindDecimal(named, m.Name) is decimal member:
                value = member;
                money = true;
                return true;

            case UnaryExpr { Op: UnOp.Neg or UnOp.Plus or UnOp.Checked or UnOp.Unchecked } u:
                if (!DecimalValue(u.Operand, owner, out decimal inner, out money)) return false;
                if (!money) return Integral(e, owner, out value);
                value = u.Op == UnOp.Neg ? decimal.Negate(inner) : inner;
                return true;

            case BinaryExpr { Op: BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Rem } b:
            {
                if (!DecimalValue(b.Left, owner, out decimal left, out bool leftMoney)
                    || !DecimalValue(b.Right, owner, out decimal right, out bool rightMoney))
                {
                    return false;
                }
                if (!leftMoney && !rightMoney) return Integral(e, owner, out value);
                money = true;
                try
                {
                    value = b.Op switch
                    {
                        BinOp.Add => left + right,
                        BinOp.Sub => left - right,
                        BinOp.Mul => left * right,
                        BinOp.Div => left / right,
                        _ => left % right,
                    };
                    return true;
                }
                catch (OverflowException)
                {
                    if (_decimalRefused.Add(b)) Error(b, "Evaluation of the decimal constant expression failed");
                }
                catch (DivideByZeroException)
                {
                    if (_decimalRefused.Add(b)) Error(b, "Division by constant zero");
                }
                return false;
            }

            case CastExpr cast when IsDecimalType(Resolve(cast.Type, owner)):
            {
                money = true;
                if (DecimalValue(cast.Operand, owner, out value, out _)) return true;
                if (RealConstant(cast.Operand, owner) is not double real) return false;
                try
                {
                    value = Single(cast.Operand) ? new decimal((float)real) : new decimal(real);
                    return true;
                }
                catch (OverflowException)
                {
                    if (_decimalRefused.Add(cast)) Error(cast, $"Constant value '{real.ToString("R", CultureInfo.InvariantCulture)}' cannot be converted to a 'decimal'");
                }
                return false;
            }

            default:
                return Integral(e, owner, out value);
        }

        // A float constant is a float literal, or one made so.
        static bool Single(Expr operand) => operand switch
        {
            LiteralExpr { Kind: Lit.Real } real => real.Text[^1] is 'f' or 'F',
            UnaryExpr { Op: UnOp.Neg or UnOp.Plus } u => Single(u.Operand),
            _ => false,
        };
    }

    /// <summary>
    /// `(int)1.9m` and its kin: a decimal constant truncated to a whole type,
    /// which is a constant in C# -- and refused where it does not fit
    /// (CS0031). Null when the operand is no decimal constant.
    /// </summary>
    private long? DecimalCast(CastExpr cast, Prim whole, TypeSymbol? owner)
    {
        if (!DecimalValue(cast.Operand, owner, out decimal value, out bool money) || !money) return null;
        decimal truncated = decimal.Truncate(value);
        (decimal Least, decimal Most) range = whole switch
        {
            Prim.I8 => (sbyte.MinValue, sbyte.MaxValue),
            Prim.U8 => (byte.MinValue, byte.MaxValue),
            Prim.I16 => (short.MinValue, short.MaxValue),
            Prim.U16 or Prim.Char => (ushort.MinValue, ushort.MaxValue),
            Prim.I32 => (int.MinValue, int.MaxValue),
            Prim.U32 => (uint.MinValue, uint.MaxValue),
            Prim.I64 => (long.MinValue, long.MaxValue),
            Prim.U64 => (ulong.MinValue, ulong.MaxValue),
            Prim.NInt => Target.Current.WordSize == 4 ? (int.MinValue, int.MaxValue) : (long.MinValue, long.MaxValue),
            _ => Target.Current.WordSize == 4 ? (uint.MinValue, uint.MaxValue) : (ulong.MinValue, ulong.MaxValue),
        };
        if (truncated < range.Least || truncated > range.Most)
        {
            if (_decimalRefused.Add(cast))
            {
                Error(cast, $"Constant value '{value.ToString(CultureInfo.InvariantCulture)}M' cannot be converted to a '{cast.Type.Name}'");
            }
            return null;
        }
        return whole == Prim.U64 || whole == Prim.NUInt ? (long)(ulong)truncated : (long)truncated;
    }

    /// <summary>An integer or character constant, as the decimal it converts to.</summary>
    private bool Integral(Expr e, TypeSymbol? owner, out decimal value)
    {
        value = 0;
        if (!Fold.TryConst(e, out long whole, out Prim type, x => Named(x, owner))) return false;
        switch (type)
        {
            case Prim.U64:
                value = (ulong)whole;
                return true;
            case Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64 or Prim.U8 or Prim.U16 or Prim.U32 or Prim.Char:
                value = whole;
                return true;
            default:
                return false;
        }
    }
}
