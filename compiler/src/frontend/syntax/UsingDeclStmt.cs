#nullable enable
namespace Corsac.Lang;

/// <summary>Parser-only marker lowered to a declaration and try/finally.</summary>
public sealed class UsingDeclStmt : Stmt
{
    public required LocalDecl Declaration { get; init; }

    /// <summary>`await using`: the resource is given back by awaiting DisposeAsync, not by Dispose.</summary>
    public bool Async { get; init; }

    /// <summary>
    /// The resources declared after the first in one declaration --
    /// `using MemoryStream input = new(bytes), log = new();` -- each given
    /// back before the ones declared ahead of it (Parser.LowerUsings).
    /// </summary>
    public List<LocalDecl>? More { get; init; }
}
