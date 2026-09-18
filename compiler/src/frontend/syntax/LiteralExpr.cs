#nullable enable
namespace Corsac.Lang;

public sealed class LiteralExpr : Expr
{
    public required Lit Kind { get; init; }
    public required string Text { get; init; }
    public long IntValue { get; init; }
    public double RealValue { get; init; }
}
