#nullable enable
namespace Corsac.Lang;

/// <summary>
/// `^1` -- an index counted from the END, which C# calls an Index.
///
/// It means nothing without knowing what is being indexed, so it stays as
/// written until the checker has the target and can turn it into the
/// subtraction it is: `s[^1]` is `s[s.Length - 1]`.
/// </summary>
public sealed class FromEndExpr : Expr
{
    public required Expr Offset { get; init; }
}
