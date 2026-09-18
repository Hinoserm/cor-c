#nullable enable
namespace Corsac.Lang;

/// `sizeof(T)` -- how many bytes one of them takes.
///
/// A constant the compiler knows, exactly as in C#, and what makes pointer
/// arithmetic mean elements rather than bytes: `p + 1` is `p` plus sizeof(T).
public sealed class SizeOfExpr : Expr
{
    public required TypeRef Type { get; init; }
}
