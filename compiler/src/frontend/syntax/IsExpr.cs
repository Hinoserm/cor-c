#nullable enable
namespace Corsac.Lang;

public sealed class IsExpr : Expr, ICanonSlot, ICanonShape
{
    /// <summary>The interface's arguments as written, in a shared method copy (ICanonShape), or null.</summary>
    public List<TypeRef>? ShapeArgs { get; set; }

    /// <summary>The descriptor entry a shared copy reads for its type argument (ICanonSlot), or -1.</summary>
    public int CanonSlot { get; set; } = -1;
    /// <summary>The `this` that entry is read through (ICanonSlot).</summary>
    public Expr? CanonSelf { get; set; }

    /// <summary>
    /// In a shared method copy, the dictionary entry of the constructed type
    /// as written (NewExpr.NeedSlot): what a box of a struct over the copy's
    /// hidden arguments is tested or unboxed against (Lowering.BoxTest), or -1.
    /// </summary>
    public int NeedSlot { get; set; } = -1;

    public required Expr Operand { get; init; }
    public required TypeRef Type { get; init; }
    /// <summary>Set for <c>x is Foo f</c>.</summary>
    public string? Binding { get; init; }
}
