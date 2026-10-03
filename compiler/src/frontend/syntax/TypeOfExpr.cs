#nullable enable
namespace Corsac.Lang;

/// `typeof(T)` -- the type itself, as a value.
///
/// REFLECTION TIER 1. What it produces is the address of T's descriptor, which
/// the code generator puts immediately in front of T's vtable -- so this is a
/// constant, and comparing two of them is comparing two addresses.
///
/// See docs/image-format.md.
public sealed class TypeOfExpr : Expr, ICanonSlot
{
    /// <summary>The descriptor entry a shared copy reads for its type argument (ICanonSlot), or -1.</summary>
    public int CanonSlot { get; set; } = -1;
    /// <summary>The `this` that entry is read through (ICanonSlot).</summary>
    public Expr? CanonSelf { get; set; }

    public required TypeRef Type { get; init; }
}
