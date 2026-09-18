#nullable enable
namespace Corsac.Lang;

public sealed class ThrowStmt : Stmt
{
    public required Expr Value { get; init; }
}
