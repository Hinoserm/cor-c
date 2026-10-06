#nullable enable
namespace Corsac.Lang;

public abstract class MemberDecl : Node
{
    public Mods Mods { get; init; }

    /// <summary>
    /// What few members carry, kept apart (MemberRare): null until one of its
    /// fields is set to something other than its default.
    /// </summary>
    internal MemberRare? _rare;
    /// <summary>The side object, made by the first writer that needs it.</summary>
    internal MemberRare Rare => _rare ??= new();

    /// <summary>
    /// The attributes written in front of this member, with their arguments.
    /// Empty for the overwhelming majority of members, which is why it is a
    /// plain list rather than anything cleverer.
    /// </summary>
    // Made only when written: most have none, and a list each was the collector's.
    private static readonly List<AttributeRef> NoAttributes = new();
    private List<AttributeRef>? _attributes;
    /// <summary>To read: one shared empty list when there are none, never written through.</summary>
    public List<AttributeRef> Attributes => _attributes ?? NoAttributes;
    /// <summary>To write: made on first use.</summary>
    public List<AttributeRef> WritableAttributes => _attributes ??= new();

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
    public string Namespace { get => _namespace; set => _namespace = Interned.Name(value); }
    private string _namespace = "";

    /// <summary>
    /// The interface an explicit implementation is written for -- `IEnumerable`
    /// in `IEnumerator<T> IEnumerable<T>.GetEnumerator()` -- or null. Such a
    /// member fills that interface's slot and is not on the type's own surface.
    /// </summary>
    public string? ExplicitInterface
    {
        get => _rare?.ExplicitInterface;
        set { if (value is not null) Rare.ExplicitInterface = Interned.Name(value); else if (_rare is not null) _rare.ExplicitInterface = null; }
    }

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

    /// <summary>
    /// Whether a shared generic copy's code in this member reads its type
    /// arguments through `this` (ICanonSlot): its initialiser's helper is then
    /// an instance method (InitializerMethods), which the constructor calls.
    /// </summary>
    // Declared beside the other flags so the bytes pack into one word.
    public bool ReadsTypeArguments { get; set; }

    /// <summary>
    /// Made since the last binding: a generic method's new copy, or a method
    /// of a specialised type that did not exist before. Between the rounds
    /// that make copies, only these bodies are checked (Binder.BindFresh),
    /// because only they can want a copy nothing has made yet.
    /// </summary>
    public bool Fresh { get; set; }
    /// <summary>Indexed unit ownership; null retains the enclosing type's legacy ownership.</summary>
    public bool? OwnedImplementation { get; set; }

    /// <summary>
    /// Settable because a specialised copy is RENAMED: one generic method
    /// becomes `Where$Node` and `Where$Token`, and the call site names the one
    /// it wants. Everything else sets it once, at construction.
    /// </summary>
    public required string Name { get => _name; set => _name = Interned.Name(value); }
    private string _name = "";

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
    public int VtableSlotHint
    {
        get => _rare?.VtableSlotHint ?? -1;
        set { if (value != -1) Rare.VtableSlotHint = value; else if (_rare is not null) _rare.VtableSlotHint = -1; }
    }
}

/// <summary>
/// THE FIELDS OF A MEMBER THAT ALMOST NO MEMBER SETS, in one object made only
/// for those that do.
///
/// A large unit binds a hundred thousand method declarations, and each held
/// a word for a constructor chain, an explicit interface, a vtable hint and
/// six more that only a hoisted generic local function or an annotated
/// return ever fills: the declarations were 128 bytes apiece, a large share
/// of the front end's heap. Each property of MemberDecl and MethodDecl over
/// these reads its default while this is null and makes it only to store
/// something other than that default, so a reader sees exactly what it saw
/// when the fields were the declaration's own.
/// </summary>
internal sealed class MemberRare
{
    public string? ExplicitInterface;
    public int VtableSlotHint = -1;
    // MethodDecl's, below.
    public CtorInit? Init;
    public string? NotNullIfNotNull;
    public string? HoistedName;
    public string? HoistedIn;
    public List<string>? CarriedTypeParams;
    public List<(string Name, string Method)>? LocalGenerics;
    public Dictionary<string, string>? Rehosted;
    public int Captures = -1;
}
