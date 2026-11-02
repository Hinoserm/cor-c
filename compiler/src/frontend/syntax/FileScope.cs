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
///
/// AND ONE A NAMESPACE BLOCK, as C# scopes a directive written in one to that
/// block: `namespace A { using G = X; }` then `namespace A { G.Y }` in the
/// same file -- the second block does not see the first's G. Each block has a
/// copy of what encloses it (Inside), which its own directives join.
/// </summary>
public sealed class FileScope
{
    /// <summary>A namespace block's scope: everything this one has, and what the block will add, apart.</summary>
    public FileScope Inside()
    {
        FileScope inner = new() { Path = Path };
        inner.Imports.AddRange(Imports);
        inner.Aliases.AddRange(Aliases);
        inner.StaticImports.AddRange(StaticImports);
        return inner;
    }

    /// <summary>
    /// The full path of the file, where one is known (Frontend, the index):
    /// what a member says of where it was written. Its File is the bare name
    /// diagnostics print, which names no file outside the working directory,
    /// and a type merged of two declarations has one path for both.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>`using Corsac.Lang.Ir;`, and the namespace it was written in.</summary>
    public List<(string In, string Namespace)> Imports { get; } = new();

    /// <summary>`using Block = Corsac.Lang.Ir.Block;`, and where it was written.</summary>
    public List<(string In, string Alias, string Target)> Aliases { get; } = new();

    /// <summary>`using static System.Math;`, and where it was written: the type whose static members are named bare.</summary>
    public List<(string In, string Type)> StaticImports { get; } = new();
}
