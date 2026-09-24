#nullable enable
namespace Corsac.Lang;

/// <summary>
/// One element of a COLLECTION initialiser: the `x` in <c>new List&lt;T&gt; { x }</c>.
///
/// C# defines it as a call to Add, and that is exactly what it becomes here --
/// a list of arguments, because `{ key, value }` inside a dictionary's
/// initialiser is one element that calls Add with two.
/// </summary>
public sealed class InitAdd : Node
{
    public List<Expr> Args { get; } = new();

    /// <summary>`..items` in a collection expression: every element of it, not it.</summary>
    public bool Spread { get; init; }
}
