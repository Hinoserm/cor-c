#nullable enable
namespace Corsac.Lang;

/// <summary>
/// What is inside one pair of initialiser braces, whatever they are attached to
/// -- a `new`, a `with`, or a member of either.
///
/// The three lists are C#'s three kinds of element, kept apart because each
/// becomes something different: a store or a setter call, a call to Add, a call
/// to the indexer.
/// </summary>
public sealed class InitBody : Node
{
    /// <summary>
    /// The `A = 1, B = 2` elements.
    ///
    /// Kept as part of the expression rather than desugared into statements,
    /// because there is nowhere to put the statements: an object initialiser
    /// appears in the middle of an expression and the object it is filling in
    /// has no name to refer to it by. The code generator has the object in a
    /// register at exactly the right moment and writes the fields there.
    /// </summary>
    public List<InitAssign> Inits { get; } = new();

    /// <summary>The `a, b, c` elements, each of which calls Add.</summary>
    public List<InitAdd> Adds { get; } = new();

    /// <summary>The `[key] = value` elements, which call the indexer.</summary>
    public List<InitIndex> Indexes { get; } = new();

    public bool IsEmpty => Inits.Count == 0 && Adds.Count == 0 && Indexes.Count == 0;
}
