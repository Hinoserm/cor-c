#nullable enable
namespace Corsac.Lang;

public sealed class IndexExpr : Expr
{
    public required Expr Target { get; init; }
    public List<Expr> Args { get; } = new();
}
