#nullable enable
namespace Corsac.Lang;

public sealed class IsExpr : Expr, ICanonSlot
{
    /// <summary>The descriptor entry a shared copy reads for its type argument (ICanonSlot), or -1.</summary>
    public int CanonSlot { get; set; } = -1;
    /// <summary>The `this` that entry is read through (ICanonSlot).</summary>
    public Expr? CanonSelf { get; set; }

    public required Expr Operand { get; init; }
    public required TypeRef Type { get; init; }
    /// <summary>Set for <c>x is Foo f</c>.</summary>
    public string? Binding { get; init; }
}
