#nullable enable
namespace Corsac.Lang;

/// <summary>
/// One attribute as written, kept rather than dropped.
///
/// The parser has always read attribute lists and thrown most of them away,
/// because for a long time `[Flags]` was the only one that meant anything and
/// a name was all it needed. `[Registry("CORSAC.Paint")]`, `[Label("...")]`
/// and `[Description("...")]` all carry their argument, so the argument is
/// what had to start surviving.
/// </summary>
public sealed class AttributeRef
{
    /// <summary>The name as written, without the "Attribute" suffix C# allows.</summary>
    public required string Name { get; init; }

    /// <summary>What it was aimed at: "return" for `[return: X]`, otherwise empty.</summary>
    public string Target { get; init; } = "";

    /// <summary>
    /// Its first argument reduced to the one word it amounts to, or null
    /// where it has none. A string literal's text, an identifier's spelling,
    /// "true" or "false".
    /// </summary>
    public string? Argument { get; init; }

    /// <summary>Whether this is `name`, allowing for C#'s "Attribute" suffix.</summary>
    public bool Is(string name)
        => string.Equals(Name, name, StringComparison.Ordinal)
        || string.Equals(Name, name + "Attribute", StringComparison.Ordinal);
}
