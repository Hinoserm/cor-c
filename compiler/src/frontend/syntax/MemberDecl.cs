#nullable enable
namespace Corsac.Lang;

public abstract class MemberDecl : Node
{
    public Mods Mods { get; init; }

    /// <summary>
    /// The attributes written in front of this member, with their arguments.
    /// Empty for the overwhelming majority of members, which is why it is a
    /// plain list rather than anything cleverer.
    /// </summary>
    public List<AttributeRef> Attributes { get; } = new();

    /// <summary>
    /// The using directives of the file THIS MEMBER was written in, which is
    /// not always the file its type was.
    ///
    /// A partial class is written across several files and merged into one
    /// declaration here, and each part brought its own directives: this
    /// compiler's own Lowering is eleven files, of which six say `using Block =
    /// Corsac.Lang.Ir.Block` and the rest do not. C# resolves each part's names
    /// with its own file's usings, which is what this remembers.
    /// </summary>
    public FileScope? Scope { get; set; }

    /// <summary>The namespace this member was written in.</summary>
    public string Namespace { get; set; } = "";

    /// <summary>
    /// The interface an explicit implementation is written for -- `IEnumerable`
    /// in `IEnumerator<T> IEnumerable<T>.GetEnumerator()` -- or null. Such a
    /// member fills that interface's slot and is not on the type's own surface.
    /// </summary>
    public string? ExplicitInterface { get; set; }

    /// <summary>
    /// A specialised copy THIS compilation made for its own use, which is its
    /// code to compile whatever its owner is.
    ///
    /// The owner decides externality for every ordinary member: a method of a
    /// library's class is an import. A consumer's own instantiation of that
    /// class's generic method is the one exception -- the library never
    /// compiled it, so an import of it is a name nothing anywhere provides,
    /// and the program is refused at load time by the machine's own linker.
    /// `Array.AsSpan&lt;byte&gt;` in the first program to slice bytes is how
    /// this was found.
    /// </summary>
    public bool LocalCopy { get; set; }
    /// <summary>Indexed unit ownership; null retains the enclosing type's legacy ownership.</summary>
    public bool? OwnedImplementation { get; set; }

    /// <summary>
    /// Settable because a specialised copy is RENAMED: one generic method
    /// becomes `Where$Node` and `Where$Token`, and the call site names the one
    /// it wants. Everything else sets it once, at construction.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Which member of the generic template this was cloned from, or -1.
    ///
    /// This is how a specialisation finds its own code in the canonical copy.
    /// <c>List&lt;long&gt;.Add</c> and <c>List&lt;__canon&gt;.Add</c> are the
    /// same member of the same template, so they are the same INDEX -- which
    /// matching by name cannot say, because a template may overload and the
    /// overloads mangle differently once their type arguments are substituted.
    /// </summary>
    public int TemplateIndex { get; set; } = -1;

    /// <summary>
    /// The vtable slot chosen by the library that owns an imported generic
    /// template, or -1 for a source member whose layout is ours to choose.
    ///
    /// This is transient compiler state rather than part of the GIR member
    /// record: the enclosing GIR template already carries the authoritative
    /// member-index-to-slot table. The loader attaches that decision to the
    /// declaration before monomorphisation, and every specialised clone keeps
    /// it so the binder lays the consumer out to the library's ABI.
    /// </summary>
    public int VtableSlotHint { get; set; } = -1;
}
