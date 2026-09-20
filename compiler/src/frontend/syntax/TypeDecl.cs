#nullable enable
namespace Corsac.Lang;

public sealed class TypeDecl : Node
{
    public required TypeKind Kind { get; init; }
    public required string Name { get; init; }
    public Mods Mods { get; set; }
    /// <summary>Canonical source identity, separate from diagnostic spelling.</summary>
    public string? SourcePath { get; set; }
    /// <summary>An implementation-private generated type, such as a closure.</summary>
    public bool LocalOnly { get; set; }
    public bool IsDelegate { get; set; }

    /// <summary>
    /// Its field initialisers are already in its constructors. A declaration
    /// is bound more than once -- every round of specialisation binds the
    /// whole unit again -- and each of them put the initialisers in again, so
    /// a class with four properties made its one list four times.
    /// </summary>
    public bool InitialisersPlaced { get; set; }
    public List<TypeParam> TypeParams { get; } = new();
    /// <summary>The attributes written on it, by name: `[Flags]` is "Flags".</summary>
    public List<string> Attributes { get; } = new();

    /// <summary>
    /// The same attributes with their arguments, for the ones whose argument
    /// is the whole point -- `[Registry("CORSAC.Paint")]` names a domain, and
    /// the name in it is the source of truth for where those settings live.
    /// </summary>
    public List<AttributeRef> AttributeParts { get; } = new();
    /// <summary>Base class and interfaces, undistinguished until binding.</summary>
    public List<TypeRef> Bases { get; } = new();

    /// <summary>
    /// The arguments a positional record hands its base: the `Type` of
    /// <c>record RegPlace(VReg Reg, Type Type) : Place(Type)</c>.
    ///
    /// They belong to the constructor the record generates, which is where they
    /// end up; kept here because they are written with the base and are read
    /// before that constructor exists.
    /// </summary>
    public List<Expr> BaseArgs { get; } = new();
    public List<MemberDecl> Members { get; } = new();
    /// <summary>Enum members, when this is an enum.</summary>
    public List<EnumMember> EnumMembers { get; } = new();

    /// <summary>
    /// The type this one was written INSIDE, spelled in full -- `Outer.Middle`
    /// for a type nested two deep -- or null for a type written at the top
    /// level.
    ///
    /// A nested type is hoisted out to sit beside the one that held it, and
    /// this is what remembers where it came from -- so `Outer.Inner` resolves
    /// and `Sys.Io` does not become the unrelated top-level class called Io.
    /// That second case is not hypothetical: it is what broke the machine's own
    /// I/O library the first time this existed without it.
    ///
    /// It is also the type's IDENTITY. Two types may share a simple name if
    /// they were written inside different types -- C# says so, and this
    /// compiler's own Assembler.Section and ImageFile.Section are the proof --
    /// so the flat table of types is keyed by this path joined to the name.
    /// </summary>
    public string? Outer { get; set; }

    /// <summary>
    /// The namespace this was written in, or the empty string for the global
    /// one. Part of <see cref="Outer"/> as well -- which is what keeps
    /// `Corsac.Lang.Expr` and `Corsac.Asm.Expr` apart in the flat table -- and
    /// kept apart from it here because a nested type's Outer names the type
    /// that holds it and the namespace cannot be read back out of that.
    /// </summary>
    public string Namespace { get; set; } = "";

    /// <summary>The using directives of the file this was written in.</summary>
    public FileScope? Scope { get; set; }

    /// <summary>
    /// Whether this came from the CLASS LIBRARY rather than from the program.
    ///
    /// .NET puts its own List and Stack in System.Collections.Generic, so a
    /// program that declares a Stack of its own in the global namespace gets
    /// its own -- the one it wrote wins over the one it merely referenced.
    /// Namespaces are flattened here, so the same answer is reached by asking
    /// where a declaration came from: a source the driver links by default is
    /// the library, and anything the programmer named is the program.
    /// </summary>
    public bool FromLibrary { get; set; }

    /// <summary>
    /// Where this declaration began and ended in its file.
    ///
    /// Recorded so a GENERIC type can go into a library's header with its
    /// bodies intact. A template stripped to signatures cannot be instantiated
    /// by whoever links the library, which is exactly why `List` was not a
    /// known type through a compiled library.
    ///
    /// Sliced from the source rather than printed back out of the tree,
    /// because an un-printer is a second implementation of the grammar and
    /// would drift from the parser. A slice is what the parser read.
    /// </summary>
    public int SourceFrom { get; set; }

    public int SourceTo { get; set; }

    /// <summary>
    /// Declared here, implemented somewhere else: this came from a library's
    /// header rather than from source being compiled.
    ///
    /// Everything about binding is the same -- the types are real, the calls
    /// are checked -- and everything about emission is different. No body is
    /// emitted for its methods, a call to one becomes an IMPORT for the loader
    /// to point at whatever provides it, and its statics live in the library's
    /// slot of the block every process gets rather than in this image's data.
    /// </summary>
    public bool External { get; set; }

    /// <summary>
    /// Bound here, emitted somewhere else: the source was given to the
    /// compiler for its declarations only, because the code it describes is
    /// in a shared object this one links (`corc compile --ref file.cor`).
    ///
    /// NOT the same as External, which is the old compiled-header
    /// arrangement and also moves the type's statics into a library slot.
    /// Everything about binding here is exactly as if the source were being
    /// compiled -- the statics are ordinary symbols, the generated
    /// StaticInit$ exists, and a consumer's first touch of the type calls
    /// it, which is what makes initialisation happen once across a
    /// library boundary. Only the emission is skipped.
    /// </summary>
    public bool Elsewhere { get; set; }
    /// <summary>Indexed signatures, not executable bodies. Never lower their declaration markers.</summary>
    public bool SignatureOnly { get; set; }

    /// <summary>Which library it came from, so its statics can be found.</summary>
    public int LibSlot { get; set; }

    /// <summary>
    /// The specialisation whose CODE this one shares, or null when it has code
    /// of its own.
    ///
    /// THE WHOLE POINT OF A SHARED LIBRARY IS NOT DUPLICATING CODE, and a
    /// generic defeats that unless something is done: monomorphisation clones
    /// the body per set of type arguments, so every program using
    /// <c>List&lt;long&gt;</c> carried its own copy of List even though the
    /// library it linked already had one.
    ///
    /// Every reference -- class, interface, string, array -- is one machine word
    /// in the integer bank, and so are long, ulong and a pointer. The compiled
    /// code for <c>List&lt;long&gt;</c>, <c>List&lt;string&gt;</c> and
    /// <c>List&lt;Node&gt;</c> is therefore the same instructions: same strides,
    /// same widths, same layout. One copy serves all of them, and it lives in
    /// the library. .NET calls it <c>__canon</c> and the name is worth keeping.
    ///
    /// The specialisation still exists as a TYPE here, with long everywhere a T
    /// was, because that is what checks the caller: <c>List&lt;long&gt;.Add</c>
    /// takes a long and nothing else. Only the CODE is shared -- which is
    /// exactly the split .NET makes between what the verifier sees and what the
    /// JIT emits.
    ///
    /// See docs/image-format.md.
    /// </summary>
    public string? Canon { get; set; }

    /// <summary>
    /// Made by monomorphisation rather than written down: this is
    /// <c>List$long</c>, not <c>List</c>.
    ///
    /// Kept out of a library's generated header, where it would be both wrong
    /// and unparseable -- the mangled name contains a '$', which is not a
    /// character the grammar allows in an identifier. A consumer does not need
    /// to be told about it either: it holds the TEMPLATE, and makes whichever
    /// specialisations its own source asks for.
    /// </summary>
    public bool Specialised { get; set; }

    /// <summary>
    /// The template this was made from, and with what.
    ///
    /// `List$Node` remembers that it is `List` applied to `Node`. Without it a
    /// specialisation is a type with a curious name and no way back: the
    /// checker cannot tell that a `List$Node` argument satisfies a `List&lt;T&gt;`
    /// parameter, and nothing can work out that T is Node -- which is the whole
    /// of generic-method inference.
    /// </summary>
    public string? Template { get; set; }

    public List<TypeRef> TemplateArgs { get; } = new();
}
