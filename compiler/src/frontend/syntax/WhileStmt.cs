#nullable enable
namespace Corsac.Lang;

public sealed class WhileStmt : Stmt
{
    public required Expr Cond { get; init; }
    public required Stmt Body { get; init; }
}
