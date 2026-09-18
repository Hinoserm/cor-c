#nullable enable
namespace Corsac.Lang;

/// <summary>
/// One `[key] = value` inside an initialiser, which C# defines as a call to the
/// INDEXER rather than to Add -- the difference showing on a duplicate key,
/// where Add refuses and the indexer overwrites.
///
/// `new Dictionary&lt;string, Tok&gt;(StringComparer.Ordinal) { ["class"] = Tok.KwClass, … }`
/// is how this compiler's own lexer declares its keyword table.
/// </summary>
public sealed class InitIndex : Node
{
    public List<Expr> Args { get; } = new();
    public required Expr Value { get; init; }
}
