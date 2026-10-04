#nullable enable
namespace Corsac.Lang;

public sealed class CastExpr : Expr, ICanonSlot, ICanonShape
{
    /// <summary>The interface's arguments as written, in a shared method copy (ICanonShape), or null.</summary>
    public List<TypeRef>? ShapeArgs { get; set; }

    /// <summary>The descriptor entry a shared copy reads for the constructed type cast to (ICanonSlot), or -1.</summary>
    public int CanonSlot { get; set; } = -1;
    /// <summary>The `this` that entry is read through (ICanonSlot).</summary>
    public Expr? CanonSelf { get; set; }

    public required TypeRef Type { get; init; }
    public required Expr Operand { get; init; }
}
