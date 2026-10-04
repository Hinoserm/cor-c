#nullable enable
namespace Corsac.Lang;

public sealed class CastExpr : Expr, ICanonSlot
{
    /// <summary>The descriptor entry a shared copy reads for the constructed type cast to (ICanonSlot), or -1.</summary>
    public int CanonSlot { get; set; } = -1;
    /// <summary>The `this` that entry is read through (ICanonSlot).</summary>
    public Expr? CanonSelf { get; set; }

    public required TypeRef Type { get; init; }
    public required Expr Operand { get; init; }
}
