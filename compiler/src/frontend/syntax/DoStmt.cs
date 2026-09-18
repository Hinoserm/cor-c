#nullable enable
namespace Corsac.Lang;

public sealed class DoStmt : Stmt
{
    public required Expr Cond { get; init; }
    public required Stmt Body { get; init; }
}
