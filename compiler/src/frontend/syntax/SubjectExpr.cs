#nullable enable
namespace Corsac.Lang;

/// <summary>Stands for the subject inside a PatternExpr's test.</summary>
public sealed class SubjectExpr : Expr
{
    /// <summary>
    /// How many enclosing subjects out this one reads: 0 is the innermost
    /// PatternExpr's, 1 the one around it. A rewrite that must evaluate two
    /// things once each, in order, nests two patterns and reads both.
    /// </summary>
    public int Outer { get; init; }
}
