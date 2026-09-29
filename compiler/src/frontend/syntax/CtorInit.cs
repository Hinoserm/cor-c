#nullable enable
namespace Corsac.Lang;

/// <summary>A constructor's chained call to a base or sibling constructor.</summary>
public sealed class CtorInit : Node
{
    /// <summary>True for <c>this(...)</c>, false for a base call.</summary>
    public bool IsThis { get; init; }
    public List<Expr> Args { get; } = new();

    /// <summary>As <see cref="CallExpr.ArgNames"/>: `: base(message: m)`.</summary>
    public List<string?> ArgNames { get; } = new();

    /// <summary>Parameter slots in source evaluation order, once named arguments are put in order.</summary>
    public List<int> ArgumentOrder { get; } = new();

    /// <summary>As <see cref="CallExpr.Spans"/>, with no receiver.</summary>
    public int[]? Spans { get; set; }

    /// <summary>The text <see cref="Spans"/> index.</summary>
    public string? Source { get; set; }
}
