#nullable enable
namespace Corsac.Lang;

public sealed class TryStmt : Stmt
{
    public required Block Body { get; init; }
    public List<CatchClause> Catches { get; } = new();
    public Block? Finally { get; init; }
}
