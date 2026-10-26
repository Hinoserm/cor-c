#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A statement run for its effect, then a value: what a positional pattern
/// needs in the middle of its test. `s is Rect(var w, var h)` takes the Rect
/// apart -- one call of its Deconstruct, or its items when it is a tuple --
/// into hidden locals, and the element patterns test those. C# has no
/// spelling for this; the compiler writes it where a pattern needs one.
/// </summary>
public sealed class SequenceExpr : Expr
{
    public required Stmt Effect { get; init; }
    public required Expr Value { get; init; }

    /// <summary>
    /// `(a, b) = pair` written where an expression goes -- a lambda's body,
    /// an expression-bodied member -- rather than as a statement: Effect is
    /// the DeconstructStmt and Value the places read back, which is what C#
    /// gives such an assignment. As a statement only the Effect is kept
    /// (Parser.Statement, Binder.Wrap, Lowering's ExprStmt), so its places
    /// are not read again.
    /// </summary>
    public bool Assignment { get; init; }

    /// <summary>
    /// An Assignment whose places can be read again with nothing done twice
    /// (Parser.Rereadable): only such a one may be used as a value.
    /// </summary>
    public bool ReadBackPure { get; init; }
}
