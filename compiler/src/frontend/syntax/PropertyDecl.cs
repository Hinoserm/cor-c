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
    public Expr? Init { get; init; }

    /// <summary>
    /// The parameters of an INDEXER, and empty for an ordinary property.
    ///
    /// An indexer is a property called Item that takes arguments, which is what
    /// C# compiles `this[int i]` to. Keeping it as a property rather than
    /// inventing a third kind of member means the accessors are synthesised by
    /// the code that already synthesises them -- get_Item and set_Item come out
    /// of the same path as get_Name and set_Name, with these in front.
    /// </summary>
    public List<Param> Params { get; } = new();
}
