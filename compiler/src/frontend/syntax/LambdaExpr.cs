#nullable enable
namespace Corsac.Lang;

public sealed class LambdaExpr : Expr
{
    public List<Param> Params { get; } = new();
    public Expr? Body { get; init; }
    public Block? BlockBody { get; init; }
    public bool Async { get; init; }
}
