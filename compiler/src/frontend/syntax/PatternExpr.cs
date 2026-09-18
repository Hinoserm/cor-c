#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A pattern whose SUBJECT is evaluated once: `Peek() is 'x' or 'X'`.
///
/// A value pattern is a test repeated per alternative, and repeating a call
/// would call it twice -- which C# does not do and which is not a detail when
/// the call is a lexer's Peek. So the subject is put in a place of its own and
/// the test refers to it through Subject below.
/// </summary>
public sealed class PatternExpr : Expr
{
    public required Expr Subject { get; init; }
    public required Expr Test { get; init; }
}
