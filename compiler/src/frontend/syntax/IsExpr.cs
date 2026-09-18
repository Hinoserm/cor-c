#nullable enable
namespace Corsac.Lang;

public sealed class IsExpr : Expr
{
    public required Expr Operand { get; init; }
    public required TypeRef Type { get; init; }
    /// <summary>Set for <c>x is Foo f</c>.</summary>
    public string? Binding { get; init; }
}
