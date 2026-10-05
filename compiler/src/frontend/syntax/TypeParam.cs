#nullable enable
namespace Corsac.Lang;

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

    /// <summary>
    /// `where T : struct`: T is a non-nullable value type, and so `T?` over it
    /// is Nullable&lt;T&gt;, a cell with a HasValue -- where on an unconstrained
    /// T the '?' is only an annotation and a value-type T? is still T (C# 9).
    /// </summary>
    public bool Struct { get; set; }

    /// <summary>
    /// `where T : new()`: every type argument has a public parameterless
    /// constructor, which is what lets `new T()` be written over T (C# 15.2.5).
    /// Checked where a type argument is given (Binder, CS0310); and a class
    /// with such a parameter gets a copy of its own per type argument
    /// (Monomorphiser.Shareable).
    /// </summary>
    public bool New { get; set; }

    /// <summary>`where T : unmanaged`, which says `new()` as `struct` does.</summary>
    public bool Unmanaged { get; set; }

    /// <summary>
    /// The declaration constructs it, `Activator.CreateInstance<T>()`, with no
    /// constraint saying it can (Parser): a class with such a parameter gets
    /// a copy of its own per type argument, as one with `new()` does, so the
    /// construction is of a type the copy knows (Monomorphiser.Shareable).
    /// </summary>
    public bool Made { get; set; }

    /// <summary>Whether `new T()` may be written: `new()`, `struct` and `unmanaged` each say so.</summary>
    public bool Constructible => New || Struct || Unmanaged;

    /// <summary>Constraints as written: <c>where T : Component</c>.</summary>
    // To read: one shared empty list until written (WritableConstraints), never written through.
    public List<TypeRef> Constraints => _constraints ?? NoConstraints;
    /// <summary>To write: made on first use; almost every node has none.</summary>
    public List<TypeRef> WritableConstraints => _constraints ??= new();
    private List<TypeRef>? _constraints;
    private static readonly List<TypeRef> NoConstraints = new();
}
