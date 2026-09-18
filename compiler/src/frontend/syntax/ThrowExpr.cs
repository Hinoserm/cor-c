#nullable enable
namespace Corsac.Lang;

/// <summary>
/// `throw` written where a VALUE belongs: `x ?? throw new ArgumentNullException()`.
///
/// C# calls it a throw expression and it has no type of its own -- control
/// never reaches whatever was waiting for the value, so it fits wherever it is
/// written. This compiler's own Lexer.cs opens with one.
/// </summary>
public sealed class ThrowExpr : Expr
{
    public required Expr Value { get; init; }
}
