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
    public List<AttributeRef> Attributes { get; } = new();
}
