#nullable enable
namespace Corsac.Lang;

public sealed class EnumMember : Node
{
    public required string Name { get; init; }
    public Expr? Value { get; init; }
}
