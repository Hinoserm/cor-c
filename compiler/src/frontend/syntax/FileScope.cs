#nullable enable
namespace Corsac.Lang;

/// <summary>
/// What a file said before its types: the namespaces it imports and the names
/// it gives them, each remembered with the namespace declaration it was
/// written INSIDE -- which is what decides when it is consulted, since C#
/// looks at a namespace's own members before the directives written in it.
///
/// Shared by every type of a file rather than copied into each, because C#
/// scopes a using directive to the file it is written in and every file here
/// merges into one unit, which must not merge those: `using Block =
/// Corsac.Lang.Ir.Block` is written in thirty-six files of this compiler and
/// means nothing in the rest.
/// </summary>
public sealed class FileScope
{
    /// <summary>`using Corsac.Lang.Ir;`, and the namespace it was written in.</summary>
    public List<(string In, string Namespace)> Imports { get; } = new();

    /// <summary>`using Block = Corsac.Lang.Ir.Block;`, and where it was written.</summary>
    public List<(string In, string Alias, string Target)> Aliases { get; } = new();
}
