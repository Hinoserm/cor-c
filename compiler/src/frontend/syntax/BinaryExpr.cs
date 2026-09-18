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
}
