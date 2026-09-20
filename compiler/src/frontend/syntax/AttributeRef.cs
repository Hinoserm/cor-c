#nullable enable
namespace Corsac.Lang;

/// <summary>One argument of an attribute: `Closed = true` is named, `"X"` is not.</summary>
public sealed class AttributeArgument
{
    public string? Name { get; init; }
    public required string Value { get; init; }
}

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
    public string? Argument => Arguments.Count > 0 ? Arguments[0].Value : null;

    /// <summary>
    /// Every argument, in order, each reduced to the one word it amounts to
    /// and carrying its name where it was written with one.
    ///
    /// `[Registry("CORSAC.Paint", Closed = true)]` is two: an unnamed
    /// "CORSAC.Paint" and a "true" called Closed. Keeping only the first,
    /// which is all anything needed while [Flags] was the only attribute
    /// that meant something, cannot express that.
    /// </summary>
    public List<AttributeArgument> Arguments { get; } = new();

    /// <summary>The value written for `name`, or null where it was not.</summary>
    public string? Named(string name)
    {
        foreach (AttributeArgument argument in Arguments)
        {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal))
            {
                return argument.Value;
            }
        }
        return null;
    }

    /// <summary>Whether `name` was written and says true.</summary>
    public bool Says(string name) => Named(name) == "true";

    /// <summary>Whether this is `name`, allowing for C#'s "Attribute" suffix.</summary>
    public bool Is(string name)
        => string.Equals(Name, name, StringComparison.Ordinal)
        || string.Equals(Name, name + "Attribute", StringComparison.Ordinal);
}
