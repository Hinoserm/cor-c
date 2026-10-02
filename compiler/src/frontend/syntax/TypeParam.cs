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

    /// <summary>Constraints as written: <c>where T : Component</c>.</summary>
    public List<TypeRef> Constraints { get; } = new();
}
