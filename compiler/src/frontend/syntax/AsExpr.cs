#nullable enable
namespace Corsac.Lang;

public sealed class AsExpr : Expr
{
    public required Expr Operand { get; init; }
    public required TypeRef Type { get; init; }
}
