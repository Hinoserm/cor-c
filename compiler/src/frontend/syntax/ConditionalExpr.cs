#nullable enable
namespace Corsac.Lang;

public sealed class ConditionalExpr : Expr
{
    public required Expr Cond { get; init; }

    /// <summary>
    /// Both arms settable because a lifted `x?.Member` or `x?[i]` is written
    /// here in two steps: the arms are built, the type of the member or
    /// element is learnt by checking them, and only then can the value-typed
    /// arm say which cell it means.
    /// </summary>
    public required Expr Then { get; set; }
    public required Expr Else { get; set; }

    /// <summary>
    /// A null-conditional's test, the binder's (`x?.M()`, `a?.B.C`,
    /// `x?[i]`, `c?.E += h`): its null arm is `null`, and its other may be
    /// void -- a void method's call, an event's add -- and then the whole is
    /// void, done only where the receiver is not null, as C# types
    /// `a?.M()` for a void M. A nullable value's empty cell the null arm
    /// makes is a zeroed slot of the frame (Lowering's ConditionalExpr).
    /// </summary>
    public bool Effect { get; init; }
}
