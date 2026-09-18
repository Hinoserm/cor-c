#nullable enable
namespace Corsac.Lang;

public sealed class CastExpr : Expr
{
    public required TypeRef Type { get; init; }
    public required Expr Operand { get; init; }
}
