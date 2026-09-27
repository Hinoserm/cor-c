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
}
