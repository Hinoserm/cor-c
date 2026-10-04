#nullable enable
namespace Corsac.Lang;

public sealed class LambdaExpr : Expr
{
    /// <summary>Set when this lambda stands for a method group: the identity of the method, so every conversion of the same method shares one closure class and compares equal.</summary>
    public string? GroupIdentity { get; set; }

    /// <summary>
    /// Set when this lambda stands for a LOCAL FUNCTION converted to a
    /// delegate of another type: the identity of the local function, so that
    /// two conversions of it over one environment compare equal, as .NET's
    /// delegates of the same method and target do (TypeSymbol.DelegateGroup).
    /// </summary>
    public string? LocalGroup { get; set; }

    public List<Param> Params { get; } = new();
    public Expr? Body { get; init; }
    public Block? BlockBody { get; init; }
    public bool Async { get; init; }
}
