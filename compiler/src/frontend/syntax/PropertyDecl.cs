#nullable enable
namespace Corsac.Lang;

public sealed class PropertyDecl : MemberDecl
{
    public required TypeRef Type { get; init; }

    /// <summary>
    /// This property came from a record's POSITIONAL LIST rather than from
    /// anybody writing it.
    ///
    /// C# synthesizes one per parameter unless the type already inherits a
    /// readable property of that name, which is how `record RegPlace(VReg Reg,
    /// Type Type) : Place(Type)` has one Type and not two. Only the checker
    /// knows what a base declares, so the parser writes the property and marks
    /// it; a written property is never dropped.
    /// </summary>
    public bool FromRecord { get; init; }
    public Block? Getter { get; init; }
    public Block? Setter { get; init; }
    /// <summary>True for <c>{ get; set; }</c> with no bodies.</summary>
    public bool Auto { get; init; }
    public bool HasSetter { get; init; }
    public Expr? Init { get; set; }

    /// <summary>
    /// The parameters of an INDEXER, and empty for an ordinary property.
    ///
    /// An indexer is a property called Item that takes arguments, which is what
    /// C# compiles `this[int i]` to. Keeping it as a property rather than
    /// inventing a third kind of member means the accessors are synthesised by
    /// the code that already synthesises them -- get_Item and set_Item come out
    /// of the same path as get_Name and set_Name, with these in front.
    /// </summary>
    /// <summary>To read: one shared empty list until a parameter is added.</summary>
    public List<Param> Params => _params ?? NoParams;
    /// <summary>To write: made on first use.</summary>
    public List<Param> WritableParams => _params ??= new();
    private List<Param>? _params;
    /// <summary>Takes a list the parser read; none for `()`.</summary>
    internal void AdoptParams(List<Param>? read) { if (read is { Count: > 0 }) { if (_params is null) _params = read; else _params.AddRange(read); } }
    // Never written through: every writer goes by WritableParams.
    private static readonly List<Param> NoParams = new();
}
