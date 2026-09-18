#nullable enable
namespace Corsac.Lang;

public sealed class ForeachStmt : Stmt
{
    public TypeRef? Type { get; init; }
    public required string Name { get; init; }
    public required Expr Sequence { get; init; }
    public required Stmt Body { get; init; }

    /// <summary>
    /// The names a DECONSTRUCTING foreach binds, when it was written that way:
    /// `foreach ((Op op, Fmt f) in table)`.
    ///
    /// Null for the ordinary form. When it is set, Name is not used -- each of
    /// these is bound instead, out of the element, which is what makes walking
    /// a dictionary read the way it does in C#.
    /// </summary>
    public List<Binding>? Bindings { get; set; }
}
