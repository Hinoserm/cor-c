#nullable enable
namespace Corsac.Lang;

/// <summary>
/// <c>source with { A = 1, B = 2 }</c> -- a COPY of source with those members
/// changed, leaving source alone.
///
/// The copy is the whole point and is why this is not sugar for a sequence of
/// assignments: `was with { Kind = Gt }` in the compiler's own Parser.cs makes
/// a second token from the first, and mutating the first instead would change
/// what the lexer had already handed out.
/// </summary>
public sealed class WithExpr : Expr
{
    public required Expr Source { get; init; }
    public InitBody Body { get; } = new();
    public List<InitAssign> Inits => Body.Inits;
}
