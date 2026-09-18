#nullable enable
namespace Corsac.Lang;

public sealed class SwitchStmt : Stmt
{
    public required Expr Subject { get; init; }
    public List<SwitchCase> Cases { get; } = new();
}
