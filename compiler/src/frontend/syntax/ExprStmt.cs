#nullable enable
namespace Corsac.Lang;

public sealed class ExprStmt : Stmt
{
    public required Expr Expr { get; init; }
}
