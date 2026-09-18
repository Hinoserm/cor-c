#nullable enable
namespace Corsac.Lang;

/// <summary>Every node carries a position, because every diagnostic needs one.</summary>
public abstract class Node
{
    public int Line { get; init; }
    public int Col { get; init; }

    /// <summary>
    /// Which file this was written in.
    ///
    /// A program is compiled from several sources at once -- a heap, a task
    /// library, a driver, the program itself -- and they become ONE unit before
    /// anything is checked. Without this every diagnostic was stamped with the
    /// name of the first file on the command line, so an error in the last one
    /// was reported at a line number that belonged to a different file
    /// entirely, and pointed at code that was perfectly correct.
    /// </summary>
    public string File { get; set; } = "";
}

// ---- types ------------------------------------------------------------

/// <summary>
/// A type as WRITTEN, not as resolved. Binding happens later; the parser's job
/// is to record exactly what the author typed so a diagnostic can quote it.
/// </summary>
public sealed class TypeRef : Node
{
    /// <summary>
    /// What a tuple type is called before the checker writes the class for it.
    ///
    /// C# has a library type of this name and this compiler does not: a tuple
    /// becomes a class the checker synthesises, one per shape, exactly as it
    /// does for an array being used as a sequence. The SPELLING in source is
    /// C#'s -- `(int At, string Label)` -- and that is the part that matters.
    /// </summary>
    public const string Tuple = "ValueTuple";

    /// <summary>
    /// Stands for THE SUBJECT'S OWN TYPE, made non-null.
    ///
    /// `x is { } y` tests that x is not null and names it. There is no type
    /// written, and the parser cannot invent one -- only the checker knows what
    /// x is. So the parser writes this and the binder resolves it against the
    /// operand, which is the one place that knows.
    /// </summary>
    public const string Same = "__same";

    /// <summary>
    /// Stands for THE SUBJECT'S OWN TYPE, exactly as it is.
    ///
    /// The `var` of a var pattern: `p is { Index: var i }` asks nothing at all
    /// and names what it found. It differs from <see cref="Same"/> in the one
    /// way C# says it does -- a var pattern matches null too, so the name keeps
    /// the nullability the subject had.
    /// </summary>
    public const string Anything = "__anything";

    public required string Name { get; init; }
    public List<TypeRef> Args { get; init; } = new();
    // Use-site arguments retained after Args is folded into a specialization
    // name. These annotations do not request another runtime specialization.
    public List<TypeRef>? UseArgs { get; init; }
    /// <summary>Array rank, 0 when not an array.</summary>
    public int ArrayRank { get; init; }
    public bool Nullable { get; init; }

    /// <summary>
    /// Whether the ELEMENT is nullable, when this is an array.
    ///
    /// `string?[]` and `string[]?` are different types -- an array of things
    /// that may be null, and a reference to an array that may itself be null --
    /// and one flag could only say one of them. Written source never needed the
    /// distinction because the spelling puts the '?' where it belongs;
    /// SUBSTITUTION does, because `T[]` with T bound to `string?` is an array of
    /// nullable strings, and the monomorphiser had nowhere to record that so it
    /// put the '?' on the array instead. List&lt;string?&gt; then declared its
    /// backing store as a nullable array, and every use of it in std.cor was
    /// reported as a possible null dereference -- 29 errors in a library file
    /// nobody had edited, blocking every compiler source that holds strings in
    /// a list.
    ///
    /// The BOUND type has always modelled this correctly: Type carries a nested
    /// Element with its own Nullable. Only the syntactic side was flat.
    /// </summary>
    public bool ElementNullable { get; init; }

    /// <summary>How many stars follow the name: <c>byte*</c> is one.</summary>
    public int PointerDepth { get; init; }

    /// <summary>
    /// What each element of a TUPLE type was called, or null for every other
    /// type.
    ///
    /// `(int At, string Label)` is a type whose elements have names, and the
    /// names are part of it: `f.At` has to mean the first one. Parallel to
    /// Args, with an empty string where an element was not named.
    /// </summary>
    public List<string>? TupleNames { get; set; }

    public override string ToString()
    {
        string s = Name;

        if (Args.Count > 0)
        {
            s += "<" + string.Join(", ", Args) + ">";
        }
        if (Nullable)
        {
            s += "?";
        }
        for (int i = 0; i < ArrayRank; i++)
        {
            s += "[]";
        }
        return s;
    }
}

[Flags]
public enum Mods
{
    None      = 0,
    Public    = 1 << 0,
    Private   = 1 << 1,
    Protected = 1 << 2,
    Internal  = 1 << 3,
    Static    = 1 << 4,
    Abstract  = 1 << 5,
    Virtual   = 1 << 6,
    Override  = 1 << 7,
    Sealed    = 1 << 8,
    Readonly  = 1 << 9,
    Const     = 1 << 10,
    Async     = 1 << 11,

    /// <summary>
    /// This member must be set by whoever builds the object.
    ///
    /// A contract about the CALL SITE rather than about the member, which is
    /// what makes it worth having at all: a field that must be filled in is one
    /// the type can stop checking for itself, and the check moves to the one
    /// place that knows whether it was.
    /// </summary>
    Required  = 1 << 12,

    /// <summary>
    /// <c>unsafe</c>. Carried so that C# source compiles, and not enforced.
    ///
    /// In C# it gates pointers behind a project switch, which is a policy about
    /// projects rather than a fact about a machine. This machine's standard
    /// library touches raw memory throughout -- that is what a standard library
    /// on a real computer does -- so a gate would mean the word appearing in
    /// every file for no benefit.
    /// </summary>
    Unsafe    = 1 << 13,
    Volatile  = 1 << 14,
    Extern    = 1 << 15,

    /// <summary>
    /// One part of a type whose members may be written in another source
    /// declaration. The compilation-unit merge consumes this distinction
    /// before binding so a legal C# partial type is not a duplicate type.
    /// </summary>
    Partial   = 1 << 16,
}

// ---- declarations -----------------------------------------------------

public sealed class CompilationUnit : Node
{
    public List<string> Usings { get; } = new();
    public List<TypeDecl> Types { get; } = new();

    /// <summary>
    /// Every TUPLE TYPE WITH NAMED ELEMENTS that was written anywhere, kept
    /// because a specialisation carries its arguments in its NAME and the
    /// argument list does not survive: `List&lt;(int A, int B)&gt;` becomes
    /// `List$ValueTuple_int_int`, and what the elements were called went with
    /// the brackets.
    ///
    /// The checker reads these before it checks anything, so the class it
    /// writes for a shape knows every naming that shape was ever given.
    /// </summary>
    public List<TypeRef> TupleNamings { get; } = new();
}

/// <summary>
/// What a file said before its types: the namespaces it imports and the names
/// it gives them, each remembered with the namespace declaration it was
/// written INSIDE -- which is what decides when it is consulted, since C#
/// looks at a namespace's own members before the directives written in it.
///
/// Shared by every type of a file rather than copied into each, because C#
/// scopes a using directive to the file it is written in and every file here
/// merges into one unit, which must not merge those: `using Block =
/// Corsac.Lang.Ir.Block` is written in thirty-six files of this compiler and
/// means nothing in the rest.
/// </summary>
public sealed class FileScope
{
    /// <summary>`using Corsac.Lang.Ir;`, and the namespace it was written in.</summary>
    public List<(string In, string Namespace)> Imports { get; } = new();

    /// <summary>`using Block = Corsac.Lang.Ir.Block;`, and where it was written.</summary>
    public List<(string In, string Alias, string Target)> Aliases { get; } = new();
}

public enum TypeKind : byte { Class, Interface, Struct, Enum }

/// <summary>How an interface's type parameter may vary, as C# declares it.</summary>
public enum Variance : byte
{
    /// <summary>Neither: the argument must be exactly what was asked for.</summary>
    None = 0,

    /// <summary>
    /// `out T`. The parameter is only ever handed OUT, so an
    /// `IEnumerable&lt;FieldDecl&gt;` is an `IEnumerable&lt;MemberDecl&gt;`:
    /// everything read from it is a MemberDecl, which is true of every
    /// FieldDecl.
    /// </summary>
    Out = 1,

    /// <summary>
    /// `in T`. The parameter is only ever taken IN, so an
    /// `IComparer&lt;MemberDecl&gt;` is an `IComparer&lt;FieldDecl&gt;`: it
    /// can compare anything a MemberDecl can be, FieldDecls among them.
    /// </summary>
    In = 2,
}

public sealed class TypeParam : Node
{
    public required string Name { get; init; }

    /// <summary>
    /// `out` or `in` before the name, and None where neither was written.
    ///
    /// C# allows it on an interface's parameters only, and it is what makes
    /// `List&lt;FieldDecl&gt;` usable where an `IEnumerable&lt;MemberDecl&gt;`
    /// is wanted -- a line in this compiler's own parser.
    /// </summary>
    public Variance Variance { get; init; }

    /// <summary>Constraints as written: <c>where T : Component</c>.</summary>
    public List<TypeRef> Constraints { get; } = new();
}

public sealed class TypeDecl : Node
{
    public required TypeKind Kind { get; init; }
    public required string Name { get; init; }
    public Mods Mods { get; init; }

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

public sealed class EnumMember : Node
{
    public required string Name { get; init; }
    public Expr? Value { get; init; }
}

public abstract class MemberDecl : Node
{
    public Mods Mods { get; init; }

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

public sealed class FieldDecl : MemberDecl
{
    public required TypeRef Type { get; init; }

    /// <summary>
    /// What it starts as, until the binder moves it.
    ///
    /// Settable rather than init-only because moving it is exactly what
    /// happens: an instance initialiser goes into every constructor and a
    /// static one into the type's StaticInit$, and the field is then cleared
    /// so nothing runs it twice.
    /// </summary>
    public Expr? Init { get; set; }

    /// <summary>
    /// The other names of a declaration that wrote several: the B and C of
    /// `const int A = 0, B = 1, C = 2;`. Each is a field in its own right with
    /// this one's type and modifiers, and whoever declares the members takes
    /// them along with it.
    /// </summary>
    public List<FieldDecl> More { get; } = new();
}

public sealed class Param : Node
{
    public required string Name { get; init; }
    public required TypeRef Type { get; init; }
    public bool IsRef { get; init; }
    public bool IsOut { get; init; }

    /// <summary>
    /// `in T x`: the address is passed, as it is for a ref, and the callee may
    /// not write through it. What `in` is written for is the address -- a
    /// struct too big to copy at every call -- so it is a ref that cannot be
    /// assigned rather than a value with a rule about it.
    /// </summary>
    public bool IsReadOnlyRef { get; init; }
    public bool IsParams { get; init; }

    /// <summary>
    /// `this` on the first parameter of a static method: an EXTENSION METHOD.
    ///
    /// `list.Where(f)` means `Enumerable.Where(list, f)`, and this is what
    /// says so. The whole of LINQ is written this way in C#, and writing it
    /// any other way here would mean the compiler's own source could not be
    /// compiled by it.
    /// </summary>
    public bool IsThis { get; init; }

    /// <summary>
    /// What a particular answer from this method PROVES about this argument.
    ///
    /// .NET writes it `[NotNullWhen(false)] string? value` on
    /// string.IsNullOrEmpty: a false answer means the argument was not null,
    /// so `if (!string.IsNullOrEmpty(dir)) { Use(dir); }` reads dir as a
    /// string. Null where the attribute was not written.
    /// </summary>
    public bool? NotNullWhen { get; init; }

    /// <summary>
    /// The value a caller that leaves this argument out passes.
    ///
    /// Settable because the checker WRITES THE NAMES IN IT OUT IN FULL the
    /// first time a call leaves the argument out: C# evaluates a default
    /// where it was declared, and the expression is copied into call sites in
    /// other classes, other namespaces and other files, where a bare name
    /// means something else or nothing at all.
    /// </summary>
    public Expr? Default { get; set; }
}

public sealed class MethodDecl : MemberDecl
{
    /// <summary>Null for a constructor.</summary>
    public TypeRef? Returns { get; init; }
    public List<TypeParam> TypeParams { get; } = new();
    public List<Param> Params { get; } = new();
    public Block? Body { get; init; }
    public bool IsCtor { get; init; }
    /// <summary>The <c>: base(...)</c> or <c>: this(...)</c> a constructor chains to.</summary>
    public CtorInit? Init { get; init; }

    /// <summary>
    /// The parameter this method's result is null only for, named by
    /// <c>[return: NotNullIfNotNull(nameof(path))]</c>.
    ///
    /// .NET writes it on Path.ChangeExtension and its fellows: the result is
    /// declared `string?` because a null in gives a null back, and a caller who
    /// passed a string gets one. Null where the attribute was not written.
    /// </summary>
    public string? NotNullIfNotNull { get; init; }
}

/// <summary>A constructor's chained call to a base or sibling constructor.</summary>
public sealed class CtorInit : Node
{
    /// <summary>True for <c>this(...)</c>, false for a base call.</summary>
    public bool IsThis { get; init; }
    public List<Expr> Args { get; } = new();
}

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

// ---- statements -------------------------------------------------------

public abstract class Stmt : Node { }

public sealed class Block : Stmt
{
    public List<Stmt> Statements { get; } = new();
}

public sealed class LocalDecl : Stmt
{
    /// <summary>Null when written as <c>var</c>; inference fills it in later.</summary>
    public TypeRef? Type { get; init; }
    public required string Name { get; init; }
    public Expr? Init { get; init; }

    /// <summary>
    /// This declaration came from a local-function declaration. Local
    /// functions are in scope throughout their containing block, unlike an
    /// ordinary local whose scope begins at its declaration.
    /// </summary>
    public bool LocalFunction { get; init; }

    /// <summary>
    /// `const int Bx = 3;` -- a NAME FOR A VALUE, not storage, exactly as a
    /// const field is. C# requires the initialiser to be a constant expression
    /// and lets the name stand wherever one is wanted: `r is Bx or Bp` is a
    /// constant pattern and is a line in this compiler's own assembler.
    /// </summary>
    public bool IsConst { get; init; }

    /// <summary>
    /// The others in the same declaration: `int line = _line, col = _col;`.
    ///
    /// One statement declaring several is C#, and they share the type and the
    /// SCOPE -- which is why they hang off the first one rather than becoming a
    /// block of their own, since a block would hide them from everything after
    /// it.
    /// </summary>
    public List<LocalDecl> Also { get; } = new();
}

/// <summary>Parser-only marker lowered to a declaration and try/finally.</summary>
public sealed class UsingDeclStmt : Stmt
{
    public required LocalDecl Declaration { get; init; }
}

public sealed class ExprStmt : Stmt
{
    public required Expr Expr { get; init; }
}

public sealed class IfStmt : Stmt
{
    public required Expr Cond { get; init; }
    public required Stmt Then { get; init; }
    public Stmt? Else { get; init; }
}

public sealed class WhileStmt : Stmt
{
    public required Expr Cond { get; init; }
    public required Stmt Body { get; init; }
}

public sealed class DoStmt : Stmt
{
    public required Expr Cond { get; init; }
    public required Stmt Body { get; init; }
}

public sealed class ForStmt : Stmt
{
    public Stmt? Init { get; init; }
    public Expr? Cond { get; init; }
    public List<Expr> Step { get; } = new();
    public required Stmt Body { get; init; }
}

public sealed class ForeachStmt : Stmt
{
    public TypeRef? Type { get; init; }
    public required string Name { get; init; }
    public required Expr Sequence { get; init; }
    public required Stmt Body { get; init; }

    /// <summary>
    /// The names a DECONSTRUCTING foreach binds, when it was written that way:
    /// `foreach ((Op op, Fmt f) in table)`.
    ///
    /// Null for the ordinary form. When it is set, Name is not used -- each of
    /// these is bound instead, out of the element, which is what makes walking
    /// a dictionary read the way it does in C#.
    /// </summary>
    public List<Binding>? Bindings { get; set; }
}

/// <summary>
/// `(string file, int at) = _origins[i];` -- taking a value apart into several
/// names, written as a statement.
///
/// The same two rules a deconstructing foreach follows: a tuple comes apart by
/// position, and anything else through its Deconstruct.
/// </summary>
public sealed class DeconstructStmt : Stmt
{
    public List<Binding> Names { get; } = new();
    public required Expr Value { get; init; }
}

/// One name a deconstruction binds, and the type written in front of it if
/// there was one.
public sealed class Binding : Node
{
    public TypeRef? Type { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// What to ASSIGN to, when the target is something that already exists
    /// rather than a name being declared: the `a` and `b` of `(a, b) = (b, a)`,
    /// and the `keys[0]` of `(keys[0], keys[1]) = (keys[1], keys[0])`.
    ///
    /// C# calls it a deconstructing assignment and allows any assignable
    /// expression in each position. Name is a hidden one in that case, so
    /// everything that counts positions still counts the same.
    /// </summary>
    public Expr? Target { get; init; }

    /// <summary>
    /// The names INSIDE this one, when a target was written as a list of its
    /// own: the `(Block block, Instr at)` of
    /// <c>foreach ((long index, (Block block, Instr at)) in suspends)</c>.
    ///
    /// C# takes a value apart into targets, and a target may be another list --
    /// which comes apart the same way, out of the element this one binds. Name
    /// is then a hidden local holding that element, so everything below reads
    /// one name per position as it always did.
    /// </summary>
    public List<Binding>? Nested { get; init; }
}

public sealed class ReturnStmt : Stmt
{
    public Expr? Value { get; init; }
}

public sealed class BreakStmt : Stmt { }
public sealed class ContinueStmt : Stmt { }

public sealed class GotoCaseStmt : Stmt
{
    public Expr? Value { get; init; }
    public bool IsDefault { get; init; }
}

public sealed class ThrowStmt : Stmt
{
    public required Expr Value { get; init; }
}

/// <summary>
/// `throw` written where a VALUE belongs: `x ?? throw new ArgumentNullException()`.
///
/// C# calls it a throw expression and it has no type of its own -- control
/// never reaches whatever was waiting for the value, so it fits wherever it is
/// written. This compiler's own Lexer.cs opens with one.
/// </summary>
public sealed class ThrowExpr : Expr
{
    public required Expr Value { get; init; }
}

public sealed class SwitchCase : Node
{
    /// <summary>
    /// The whole label, as a condition over the switch subject. Null for
    /// <c>default</c>, which is the only label that asks nothing.
    ///
    /// ONE FIELD, BECAUSE A CASE LABEL IS A PATTERN AND NOTHING ELSE. This was
    /// five fields -- Value, Type, Binding, Property and Accepts -- plus a
    /// guard, plus three side tables in the binder, plus eighty lines of its
    /// own code generation. All of it was a SECOND, SMALLER pattern language
    /// that had been reimplemented here: one member only, constants only, no
    /// nesting, no `and`, no relational patterns, no paths. The comment
    /// admitting that said "the rest of the pattern language can follow without
    /// changing any of this", and the rest of the pattern language could not,
    /// because this was not the pattern language.
    ///
    /// So `case MemberExpr { Target: NameExpr type } m:` -- an ordinary line in
    /// this compiler's own Binder.cs -- was a syntax error, while the identical
    /// test written as `is` was fine.
    ///
    /// Now the label is parsed by the same function `is` uses and checked and
    /// emitted as the ordinary boolean expression it is, so every shape of
    /// pattern works here the moment it works anywhere. The `when` guard folds
    /// in as an `&amp;&amp;`, which is exactly its meaning: it is tested after
    /// the pattern matched, and failing it falls through to the next label
    /// rather than out of the switch.
    /// </summary>
    public Expr? Pattern { get; init; }

    public List<Stmt> Body { get; } = new();
}

/// One arm of a switch EXPRESSION: a pattern, an optional guard, and a value.
///
/// Deliberately not the same node as SwitchCase. A case holds STATEMENTS and
/// falls out through break; an arm holds one expression and is the value of the
/// whole switch. Sharing a node would mean every consumer asking which kind it
/// was really looking at.
public sealed class SwitchArm : Node
{
    /// <summary>
    /// A constant to compare against, or null for a type pattern or for `_`.
    ///
    /// Settable because a bare name is ambiguous until something knows what
    /// names mean: `RegZero => "zero"` reads as a type pattern and IS a
    /// constant pattern, and only the checker can tell. See Binder.
    /// </summary>
    public Expr? Value { get; set; }

    /// The type in `Foo f => ...`, or null.
    public TypeRef? Type { get; set; }

    /// The name it binds, or null.
    public string? Binding { get; init; }

    /// `when` guard, evaluated only if the pattern matched.
    public Expr? When { get; init; }

    /// True for `_`, which matches anything and binds nothing.
    public bool Discard { get; init; }

    public required Expr Result { get; init; }
}

/// `subject switch { ... }` -- an expression, not a statement.
///
/// The compiler's own source is full of these and almost all of them are type
/// patterns over a node kind, which is the shape this exists to serve.
public sealed class SwitchExpr : Expr
{
    public required Expr Subject { get; init; }
    public List<SwitchArm> Arms { get; } = new();
}

public sealed class SwitchStmt : Stmt
{
    public required Expr Subject { get; init; }
    public List<SwitchCase> Cases { get; } = new();
}

public sealed class CatchClause : Node
{
    public TypeRef? Type { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// `catch (E e) when (cond)` -- an EXCEPTION FILTER.
    ///
    /// A filter that answers false does not catch, and the search carries on to
    /// the next clause. That is why it cannot be an `if` at the top of the body
    /// that rethrows: a body has already caught, and rethrowing starts a second
    /// search from here rather than continuing the first.
    /// </summary>
    public Expr? When { get; init; }

    public required Block Body { get; init; }
}

public sealed class TryStmt : Stmt
{
    public required Block Body { get; init; }
    public List<CatchClause> Catches { get; } = new();
    public Block? Finally { get; init; }
}

// ---- expressions ------------------------------------------------------

public abstract class Expr : Node { }

public enum Lit : byte { Int, Real, Str, Char, Bool, Null }

public sealed class LiteralExpr : Expr
{
    public required Lit Kind { get; init; }
    public required string Text { get; init; }
    public long IntValue { get; init; }
    public double RealValue { get; init; }
}

public sealed class NameExpr : Expr
{
    /// <summary>Settable for the same reason MemberExpr.Name is.</summary>
    public required string Name { get; set; }
    /// <summary>Explicit type arguments, as in <c>Foo&lt;int&gt;()</c>.</summary>
    public List<TypeRef> TypeArgs { get; } = new();
}

/// <summary>
/// A tuple, written out: <c>(a, b)</c> or <c>(text: "x", kind: k)</c>.
///
/// Its TYPE is not written anywhere and cannot be: it is whatever its elements
/// turn out to be, which only the checker knows. So this stays a shape of its
/// own until then, and the checker turns it into the construction of a class it
/// writes -- see Binder.TupleType.
/// </summary>
public sealed class TupleExpr : Expr
{
    public List<Expr> Items { get; } = new();

    /// <summary>The name written before each element, or "" where none was.</summary>
    public List<string> Names { get; } = new();
}

/// <summary>
/// A RANGE: `1..4`, `2..`, `..3`, `..`. Either end may be left out, and what it
/// means then is the start or the end of whatever is being sliced.
/// </summary>
public sealed class RangeExpr : Expr
{
    public Expr? From { get; init; }
    public Expr? To { get; init; }
}

/// <summary>
/// A pattern whose SUBJECT is evaluated once: `Peek() is 'x' or 'X'`.
///
/// A value pattern is a test repeated per alternative, and repeating a call
/// would call it twice -- which C# does not do and which is not a detail when
/// the call is a lexer's Peek. So the subject is put in a place of its own and
/// the test refers to it through Subject below.
/// </summary>
public sealed class PatternExpr : Expr
{
    public required Expr Subject { get; init; }
    public required Expr Test { get; init; }
}

/// <summary>
/// `^1` -- an index counted from the END, which C# calls an Index.
///
/// It means nothing without knowing what is being indexed, so it stays as
/// written until the checker has the target and can turn it into the
/// subtraction it is: `s[^1]` is `s[s.Length - 1]`.
/// </summary>
public sealed class FromEndExpr : Expr
{
    public required Expr Offset { get; init; }
}

/// <summary>Stands for the subject inside a PatternExpr's test.</summary>
public sealed class SubjectExpr : Expr { }

/// <summary>
/// `x!` -- the null-forgiving operator.
///
/// It says the author knows something the checker does not, and it generates NO
/// CODE: the value is the value. C# has it, so this has it; what it costs is
/// that the promise is the author's rather than the compiler's, which is
/// exactly what writing it means.
/// </summary>
public sealed class SuppressExpr : Expr
{
    public required Expr Operand { get; init; }
}

public sealed class ThisExpr : Expr { }
public sealed class BaseExpr : Expr { }

public sealed class MemberExpr : Expr
{
    /// <summary>
    /// The null test that guards this read has already been made, right here,
    /// by whatever generated it.
    ///
    /// `x is { Kind: A }` lowers to `x != null && x.Kind == A`, and the second
    /// half is only ever reached when the first was true -- so requiring it to
    /// be proved again means proving something about a MEMBER ACCESS, which is
    /// a wider change than this pattern is worth. The lowering knows what it
    /// wrote; this is it saying so.
    /// </summary>
    public bool Guarded { get; init; }

    public required Expr Target { get; init; }

    /// <summary>
    /// Settable because a call to a generic method is REPOINTED at the copy
    /// compiled for its type arguments: `Where` becomes `Where$Node`. The
    /// receiver and the arguments are untouched; only which member is being
    /// asked for changes.
    /// </summary>
    public required string Name { get; set; }
    public List<TypeRef> TypeArgs { get; } = new();
    /// <summary>True for <c>?.</c>, which short-circuits on null.</summary>
    public bool NullConditional { get; init; }
}

public sealed class CallExpr : Expr
{
    public required Expr Target { get; init; }
    // Source-level tuple naming survives renaming a generic call to its shared
    // machine-code specialization. Names are not part of that code's identity.
    public List<string>? ResultTupleNames { get; set; }
    public TypeRef? ResultTypeUse { get; set; }
    public Dictionary<int, TypeRef>? ArgumentTypeUses { get; set; }
    public List<Expr> Args { get; } = new();
    // Parameter indices in source evaluation order for named local calls.
    // Retained across generic rewriting after ArgNames has been consumed.
    public List<int> LocalArgumentOrder { get; } = new();

    /// <summary>
    /// The name written before each argument, or null where none was.
    ///
    /// Parallel to <see cref="Args"/> and the same length once anything has
    /// been named -- `With(nullable: true)`. The binder puts the arguments
    /// into parameter order and clears this, so nothing below it ever sees a
    /// call whose arguments are out of order.
    /// </summary>
    public List<string?> ArgNames { get; } = new();

    /// <summary>
    /// Whether the receiver has already been moved into the argument list.
    ///
    /// A member call on a type whose methods are static -- a string's, an
    /// extension method's -- is rewritten so the receiver becomes the first
    /// argument, which makes everything below an ordinary static call. That
    /// rewrite MUTATES the call, and the checker now runs more than once
    /// (generic methods are specialised between rounds), so without a mark on
    /// the call itself the receiver goes in twice and the method is told it
    /// was given one argument too many.
    /// </summary>
    public bool ReceiverAdded { get; set; }

}

public sealed class IndexExpr : Expr
{
    public required Expr Target { get; init; }
    public List<Expr> Args { get; } = new();
}

/// `sizeof(T)` -- how many bytes one of them takes.
///
/// A constant the compiler knows, exactly as in C#, and what makes pointer
/// arithmetic mean elements rather than bytes: `p + 1` is `p` plus sizeof(T).
public sealed class SizeOfExpr : Expr
{
    public required TypeRef Type { get; init; }
}

/// `typeof(T)` -- the type itself, as a value.
///
/// REFLECTION TIER 1. What it produces is the address of T's descriptor, which
/// the code generator puts immediately in front of T's vtable -- so this is a
/// constant, and comparing two of them is comparing two addresses.
///
/// See docs/image-format.md.
public sealed class TypeOfExpr : Expr
{
    public required TypeRef Type { get; init; }
}

/// `default(T)` -- the zero of a type.
///
/// Needed by generic code above all, which is where it earns its keep: a
/// Dictionary answering "nothing was there" has to produce a V without knowing
/// what V is. On this machine every value is a word, so the zero of a type is
/// the zero word -- null for a reference, false for a bool, 0 for a number --
/// and the type is carried only so the checker knows what came out.
public sealed class DefaultExpr : Expr
{
    public required TypeRef Type { get; init; }

    /// <summary>
    /// The type written was a TYPE PARAMETER, before anything was substituted
    /// into it.
    ///
    /// `default(T)` on an unconstrained T is oblivious in C#: it may be null
    /// when T turns out to be a reference type and it is still a T, which is
    /// what lets `out T value; value = default(T);` compile without a
    /// complaint -- .NET says the same thing on the outside with
    /// [MaybeNullWhen(false)]. Substitution turns the T into whatever it was
    /// bound to and the checker, meeting `default(string)`, would say the zero
    /// of a reference type is null and refuse the assignment -- in a library
    /// file whose author wrote nothing of the kind. So the fact that it WAS a
    /// parameter is recorded before the name is replaced.
    /// </summary>
    public bool OfTypeParameter { get; init; }
}

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

/// <summary>
/// <c>source with { A = 1, B = 2 }</c> -- a COPY of source with those members
/// changed, leaving source alone.
///
/// The copy is the whole point and is why this is not sugar for a sequence of
/// assignments: `was with { Kind = Gt }` in the compiler's own Parser.cs makes
/// a second token from the first, and mutating the first instead would change
/// what the lexer had already handed out.
/// </summary>
public sealed class WithExpr : Expr
{
    public required Expr Source { get; init; }
    public InitBody Body { get; } = new();
    public List<InitAssign> Inits => Body.Inits;
}

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
}

/// <summary>
/// One `[key] = value` inside an initialiser, which C# defines as a call to the
/// INDEXER rather than to Add -- the difference showing on a duplicate key,
/// where Add refuses and the indexer overwrites.
///
/// `new Dictionary&lt;string, Tok&gt;(StringComparer.Ordinal) { ["class"] = Tok.KwClass, … }`
/// is how this compiler's own lexer declares its keyword table.
/// </summary>
public sealed class InitIndex : Node
{
    public List<Expr> Args { get; } = new();
    public required Expr Value { get; init; }
}

public sealed class NewExpr : Expr
{
    public required TypeRef Type { get; init; }
    public List<Expr> Args { get; } = new();
    public List<string?> ArgNames { get; } = new();
    /// <summary>Parameter slots in source evaluation order after named argument binding.</summary>
    public List<int> ArgumentOrder { get; } = new();
    /// <summary>Set for <c>new int[n]</c>.</summary>
    public Expr? ArraySize { get; init; }

    /// <summary>
    /// The elements of <c>new[] { a, b, c }</c>, and null when there was no
    /// initialiser list.
    ///
    /// An implicitly-typed one leaves Type empty and takes its element from
    /// the first element written, exactly as C# does.
    /// </summary>
    public List<Expr>? Elements { get; set; }

    /// <summary>The `{ … }` after the constructor, if there was one.</summary>
    public InitBody Body { get; } = new();

    public List<InitAssign> Inits => Body.Inits;
    public List<InitAdd> Adds => Body.Adds;
    public List<InitIndex> Indexes => Body.Indexes;
}

public enum UnOp : byte
{
    Neg, Not, BitNot, PreInc, PreDec, PostInc, PostDec,

    /// <summary><c>*p</c> -- the thing at an address.</summary>
    Deref,

    /// <summary><c>&amp;x</c> -- the address of a thing.</summary>
    AddressOf,

    /// <summary>C# checked and unchecked arithmetic contexts.</summary>
    Checked,
    Unchecked,
}

public sealed class UnaryExpr : Expr
{
    public required UnOp Op { get; init; }
    public required Expr Operand { get; init; }
}

public enum BinOp : byte
{
    Add, Sub, Mul, Div, Rem,
    And, Or, Xor, Shl, Shr,
    Eq, Ne, Lt, Gt, Le, Ge,
    AndAlso, OrElse,
    Coalesce,
}

public sealed class BinaryExpr : Expr
{
    public required BinOp Op { get; init; }
    public required Expr Left { get; init; }
    public required Expr Right { get; init; }

    /// <summary>
    /// This test for null was written by a PATTERN, not by the author.
    ///
    /// A property pattern reads its subject's members, so it asks first
    /// whether there is a subject to read them off -- and where the subject is
    /// a value type there is nothing to ask. C# does not write the test at all
    /// there, and `def is { Role: Role.Def }` over a struct was refused with
    /// "a value type can never be null", which is true of the comparison and
    /// not of the pattern.
    /// </summary>
    public bool PatternNullTest { get; init; }
}

public sealed class AssignExpr : Expr
{
    /// <summary>Null for plain <c>=</c>; otherwise the compound operation.</summary>
    public BinOp? Op { get; init; }
    public required Expr Target { get; init; }
    public required Expr Value { get; init; }
}

public sealed class ConditionalExpr : Expr
{
    public required Expr Cond { get; init; }

    /// <summary>
    /// Settable because a lifted `x?.Member` is written here in two steps: the
    /// arms are built, the type of the member is learnt by checking them, and
    /// only then can the value-typed arm say which cell it means.
    /// </summary>
    public required Expr Then { get; set; }
    public required Expr Else { get; init; }
}

public sealed class CastExpr : Expr
{
    public required TypeRef Type { get; init; }
    public required Expr Operand { get; init; }
}

/// An argument passed BY REFERENCE: `f(out x)`, `f(ref x)`, `f(out int x)`.
///
/// A node of its own rather than a flag on the call, because what it produces
/// is not the same KIND of thing an ordinary argument produces. Every other
/// argument evaluates to a value; this one evaluates to the ADDRESS of somewhere
/// a value can be put. Making it an expression means the whole argument
/// machinery -- spilling across a suspension, passing on the stack once the
/// registers run out -- goes on working without knowing about it.
public sealed class RefArgExpr : Expr
{
    /// What the address is taken of. For a declaration form this is the
    /// NameExpr the parser synthesised for the new variable.
    public required Expr Target { get; init; }

    /// `out` rather than `ref`. The difference is entirely about definite
    /// assignment -- who has to have written to it before the call -- which is
    /// a check this compiler does not make yet.
    public bool IsOut { get; init; }

    /// The type written in a declaration form, or null for `out var x` and for
    /// an argument that names something already declared.
    public TypeRef? Declare { get; init; }

    /// The name being declared, or null when nothing is.
    public string? Name { get; init; }
}

public sealed class IsExpr : Expr
{
    public required Expr Operand { get; init; }
    public required TypeRef Type { get; init; }
    /// <summary>Set for <c>x is Foo f</c>.</summary>
    public string? Binding { get; init; }
}

public sealed class AsExpr : Expr
{
    public required Expr Operand { get; init; }
    public required TypeRef Type { get; init; }
}

public sealed class AwaitExpr : Expr
{
    public required Expr Operand { get; init; }
}

public sealed class LambdaExpr : Expr
{
    public List<Param> Params { get; } = new();
    public Expr? Body { get; init; }
    public Block? BlockBody { get; init; }
    public bool Async { get; init; }
}
