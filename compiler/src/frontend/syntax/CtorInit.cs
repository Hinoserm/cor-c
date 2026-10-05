#nullable enable
namespace Corsac.Lang;

/// <summary>A constructor's chained call to a base or sibling constructor.</summary>
public sealed class CtorInit : Node
{
    /// <summary>True for <c>this(...)</c>, false for a base call.</summary>
    public bool IsThis { get; init; }
    public List<Expr> Args { get; } = new();

    /// <summary>As <see cref="CallExpr.ArgNames"/>: `: base(message: m)`.</summary>
    // To read: one shared empty list until written (WritableArgNames), never written through.
    public List<string?> ArgNames => _argNames ?? NoArgNames;
    /// <summary>To write: made on first use; almost every node has none.</summary>
    public List<string?> WritableArgNames => _argNames ??= new();
    private List<string?>? _argNames;
    private static readonly List<string?> NoArgNames = new();

    /// <summary>Parameter slots in source evaluation order, once named arguments are put in order.</summary>
    // To read: one shared empty list until written (WritableArgumentOrder), never written through.
    public List<int> ArgumentOrder => _argumentOrder ?? NoArgumentOrder;
    /// <summary>To write: made on first use; almost every node has none.</summary>
    public List<int> WritableArgumentOrder => _argumentOrder ??= new();
    private List<int>? _argumentOrder;
    private static readonly List<int> NoArgumentOrder = new();

    /// <summary>As <see cref="CallExpr.Spans"/>, with no receiver.</summary>
    public int[]? Spans { get; set; }

    /// <summary>The text <see cref="Spans"/> index.</summary>
    public string? Source { get; set; }
}
