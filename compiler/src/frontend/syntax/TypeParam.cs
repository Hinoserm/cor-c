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

    /// <summary>Whether `new T()` may be written: `new()`, `struct` and `unmanaged` each say so.</summary>
    public bool Constructible => New || Struct || Unmanaged;

    /// <summary>Constraints as written: <c>where T : Component</c>.</summary>
    public List<TypeRef> Constraints { get; } = new();
}
