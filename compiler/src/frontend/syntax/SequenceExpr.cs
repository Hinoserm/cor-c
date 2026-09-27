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
}
