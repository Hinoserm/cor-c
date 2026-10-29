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

    /// <summary>
    /// This assignment into another target -- an element at an Index's
    /// offset, through a ref-returning call, past a `?.` -- its `??=`
    /// (Coalescing) kept, and its `x ?? y` read from that target: kept as it
    /// was, the store's x read the old one, its receiver and index again.
    /// </summary>
    public AssignExpr Retargeted(Expr target) => new()
    {
        Target = target, Op = Op, Coalescing = Coalescing,
        Value = Coalescing && Value is BinaryExpr { Op: BinOp.Coalesce } held
            ? new BinaryExpr { Op = BinOp.Coalesce, Left = target, Right = held.Right, Line = held.Line, Col = held.Col }
            : Value,
        Line = Line, Col = Col,
    };
}
