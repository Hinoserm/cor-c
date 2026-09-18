#nullable enable
namespace Corsac.Lang;

/// <summary>A constructor's chained call to a base or sibling constructor.</summary>
public sealed class CtorInit : Node
{
    /// <summary>True for <c>this(...)</c>, false for a base call.</summary>
    public bool IsThis { get; init; }
    public List<Expr> Args { get; } = new();
}
