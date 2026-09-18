#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A RANGE: `1..4`, `2..`, `..3`, `..`. Either end may be left out, and what it
/// means then is the start or the end of whatever is being sliced.
/// </summary>
public sealed class RangeExpr : Expr
{
    public Expr? From { get; init; }
    public Expr? To { get; init; }
}
