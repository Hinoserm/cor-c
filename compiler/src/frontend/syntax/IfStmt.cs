#nullable enable
namespace Corsac.Lang;

public sealed class IfStmt : Stmt
{
    public required Expr Cond { get; init; }
    public required Stmt Then { get; init; }
    public Stmt? Else { get; init; }
}
