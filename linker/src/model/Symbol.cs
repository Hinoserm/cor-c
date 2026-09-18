#nullable enable

namespace Corsac.Lang.Ir;

public sealed class Symbol
{
    public required string Name { get; init; }
    /// <summary>Null for an undefined symbol the linker must supply.</summary>
    public Section? Section { get; init; }
    public long Offset { get; init; }
    public long Size { get; init; }
    public bool IsFunction { get; init; }
    public bool Global { get; init; } = true;
    public bool IsDefined => Section is not null;
}
