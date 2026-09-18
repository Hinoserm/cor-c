#nullable enable
namespace Corsac.Lang;

public sealed class AwaitExpr : Expr
{
    public required Expr Operand { get; init; }
}
