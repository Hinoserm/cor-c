#nullable enable
namespace Corsac.Lang;

public sealed class BinaryExpr : Expr
{
    public required BinOp Op { get; init; }
    public required Expr Left { get; init; }
    public required Expr Right { get; init; }

    /// <summary>
    /// This test for null was written by a PATTERN, not by the author.
    ///
    /// A property pattern reads its subject's members, so it asks first
    /// whether there is a subject to read them off -- and where the subject is
    /// a value type there is nothing to ask. C# does not write the test at all
    /// there, and `def is { Role: Role.Def }` over a struct was refused with
    /// "a value type can never be null", which is true of the comparison and
    /// not of the pattern.
    /// </summary>
    public bool PatternNullTest { get; init; }

    /// <summary>
    /// This equality is a CONSTANT PATTERN as the parser read it -- `x is A.B`
    /// with nothing after the name. A dotted name there may be a type rather
    /// than a constant, and only binding can say which: C# takes it as a type
    /// when it names one, so `o is System.Text.StringBuilder` is a type test.
    /// </summary>
    public bool PatternConstant { get; init; }
}
