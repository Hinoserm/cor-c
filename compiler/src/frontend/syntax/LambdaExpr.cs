#nullable enable
namespace Corsac.Lang;

public sealed class LambdaExpr : Expr
{
    /// <summary>Set when this lambda stands for a method group: the identity of the method, so every conversion of the same method shares one closure class and compares equal.</summary>
    public string? GroupIdentity { get; set; }

    public List<Param> Params { get; } = new();
    public Expr? Body { get; init; }
    public Block? BlockBody { get; init; }
    public bool Async { get; init; }
}
