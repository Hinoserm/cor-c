#nullable enable
namespace Corsac.Lang;

public sealed class EnumMember : Node
{
    public required string Name { get; init; }
    public Expr? Value { get; init; }

    /// <summary>
    /// C# allows attributes on an enum member, and a registry choice list
    /// needs them: a member carries the `[Label]` and `[Description]` a
    /// settings page shows instead of the bare member name.
    /// </summary>
    // To read: one shared empty list until written (WritableAttributes), never written through.
    public List<AttributeRef> Attributes => _attributes ?? NoAttributes;
    /// <summary>To write: made on first use; almost every node has none.</summary>
    public List<AttributeRef> WritableAttributes => _attributes ??= new();
    private List<AttributeRef>? _attributes;
    private static readonly List<AttributeRef> NoAttributes = new();
}
