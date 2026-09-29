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

    /// <summary>
    /// Written by the binder's rewrite of `x?.M()`, whose receiver is the
    /// value `x` was found to hold: for a nullable value type that is the
    /// value inside the cell, which a plain `x!` -- still a `T?` in C# -- is
    /// not.
    /// </summary>
    public bool OpensCell { get; init; }
}
