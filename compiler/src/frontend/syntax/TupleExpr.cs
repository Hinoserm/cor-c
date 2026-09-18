#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A tuple, written out: <c>(a, b)</c> or <c>(text: "x", kind: k)</c>.
///
/// Its TYPE is not written anywhere and cannot be: it is whatever its elements
/// turn out to be, which only the checker knows. So this stays a shape of its
/// own until then, and the checker turns it into the construction of a class it
/// writes -- see Binder.TupleType.
/// </summary>
public sealed class TupleExpr : Expr
{
    public List<Expr> Items { get; } = new();

    /// <summary>The name written before each element, or "" where none was.</summary>
    public List<string> Names { get; } = new();
}
