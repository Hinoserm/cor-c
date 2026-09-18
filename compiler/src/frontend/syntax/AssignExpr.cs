#nullable enable
namespace Corsac.Lang;

public sealed class AssignExpr : Expr
{
    /// <summary>Null for plain <c>=</c>; otherwise the compound operation.</summary>
    public BinOp? Op { get; init; }
    public required Expr Target { get; init; }
    public required Expr Value { get; init; }
}
