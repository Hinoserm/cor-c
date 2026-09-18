#nullable enable
namespace Corsac.Lang;

public sealed class GotoCaseStmt : Stmt
{
    public Expr? Value { get; init; }
    public bool IsDefault { get; init; }
}
