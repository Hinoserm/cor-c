#nullable enable
namespace Corsac.Lang;

public sealed class UnaryExpr : Expr
{
    public required UnOp Op { get; init; }
    public required Expr Operand { get; init; }
}
