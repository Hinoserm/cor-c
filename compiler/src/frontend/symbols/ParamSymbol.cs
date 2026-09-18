#nullable enable
namespace Corsac.Lang;

public sealed class ParamSymbol
{
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public bool ByRef { get; init; }

    /// <summary>
    /// `in`: a ref the callee may not write, and one the CALLER does not name.
    /// C# makes the word optional at the call site precisely because nothing
    /// the caller can see changes, which is why this is kept apart from ByRef
    /// rather than folded into it.
    /// </summary>
    public bool ReadOnly { get; init; }
    public bool IsParams { get; init; }
}
