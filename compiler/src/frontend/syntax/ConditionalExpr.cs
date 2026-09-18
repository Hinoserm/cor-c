#nullable enable
namespace Corsac.Lang;

public sealed class ConditionalExpr : Expr
{
    public required Expr Cond { get; init; }

    /// <summary>
    /// Settable because a lifted `x?.Member` is written here in two steps: the
    /// arms are built, the type of the member is learnt by checking them, and
    /// only then can the value-typed arm say which cell it means.
    /// </summary>
    public required Expr Then { get; set; }
    public required Expr Else { get; init; }
}
