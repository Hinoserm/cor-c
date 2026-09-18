#nullable enable
namespace Corsac.Lang;

public sealed class ReturnStmt : Stmt
{
    public Expr? Value { get; init; }
}
