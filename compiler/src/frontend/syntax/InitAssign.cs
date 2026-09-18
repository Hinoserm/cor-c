#nullable enable
namespace Corsac.Lang;

/// One `Name = value` inside an object initialiser.
public sealed class InitAssign : Node
{
    public required string Name { get; init; }

    /// <summary>What the member is set to, and null when <see cref="Nested"/> is not.</summary>
    public Expr? Value { get; init; }

    /// <summary>
    /// The `{ … }` of <c>Name = { … }</c>, which C# does NOT define as an
    /// assignment: the member is READ and what it already holds is initialised
    /// in place. `Operands = { x, y }` calls Add twice on the list the property
    /// hands back, which is why a get-only collection property can be filled in
    /// this way and why the member needs a getter rather than a setter.
    /// </summary>
    public InitBody? Nested { get; init; }
}
