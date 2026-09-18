#nullable enable
namespace Corsac.Lang;

/// <summary>
/// `x!` -- the null-forgiving operator.
///
/// It says the author knows something the checker does not, and it generates NO
/// CODE: the value is the value. C# has it, so this has it; what it costs is
/// that the promise is the author's rather than the compiler's, which is
/// exactly what writing it means.
/// </summary>
public sealed class SuppressExpr : Expr
{
    public required Expr Operand { get; init; }
}
