#nullable enable
namespace Corsac.Lang;

public sealed class AssignExpr : Expr
{
    /// <summary>Null for plain <c>=</c>; otherwise the compound operation.</summary>
    public BinOp? Op { get; init; }
    public required Expr Target { get; init; }
    public required Expr Value { get; init; }

    /// <summary>
    /// Written `x ??= y`, held as `x = x ?? y`: lowered as C# has it, y
    /// evaluated and x stored only where x is empty (Lowering's
    /// EmitCoalescingAssign). Said, not inferred from the shape, which a
    /// written `r = a ?? b` shares once a body is rewritten or read back.
    /// </summary>
    public bool Coalescing { get; init; }
}
