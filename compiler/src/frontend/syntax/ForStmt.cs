#nullable enable
namespace Corsac.Lang;

public sealed class ForStmt : Stmt
{
    public Stmt? Init { get; init; }
    public Expr? Cond { get; init; }
    public List<Expr> Step { get; } = new();
    public required Stmt Body { get; init; }
}
