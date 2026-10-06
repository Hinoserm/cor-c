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
    // EACH LIST MADE ON ITS FIRST WRITE: the plain property reads one shared
    // empty list until then, never written through, and every writer goes by
    // its Writable twin. Every `new` carries one of these, and nearly every
    // `new` written has no braces after it: three empty lists each, for
    // nothing, in every body and every copy the monomorphiser made of one.
    public List<InitAssign> Inits => _inits ?? NoInits;
    public List<InitAssign> WritableInits => _inits ??= new();
    private List<InitAssign>? _inits;
    private static readonly List<InitAssign> NoInits = new();

    /// <summary>The `a, b, c` elements, each of which calls Add.</summary>
    public List<InitAdd> Adds => _adds ?? NoAdds;
    public List<InitAdd> WritableAdds => _adds ??= new();
    private List<InitAdd>? _adds;
    private static readonly List<InitAdd> NoAdds = new();

    /// <summary>The `[key] = value` elements, which call the indexer.</summary>
    public List<InitIndex> Indexes => _indexes ?? NoIndexes;
    public List<InitIndex> WritableIndexes => _indexes ??= new();
    private List<InitIndex>? _indexes;
    private static readonly List<InitIndex> NoIndexes = new();

    public bool IsEmpty => Inits.Count == 0 && Adds.Count == 0 && Indexes.Count == 0;
}
