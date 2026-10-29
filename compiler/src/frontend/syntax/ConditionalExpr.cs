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
    /// A null-conditional assignment's test (Binder, NullConditionalAssignment):
    /// its assignment may be void -- `c?.E += h`, an event's add, a declared
    /// compound operator's -- and then the whole is void, done only where the
    /// receiver is not null, as `() => c?.E += h` for an Action is in C#.
    /// </summary>
    public bool Effect { get; init; }
}
