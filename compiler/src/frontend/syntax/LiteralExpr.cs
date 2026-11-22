#nullable enable
namespace Corsac.Lang;

public sealed class LiteralExpr : Expr
{
    public required Lit Kind { get; init; }
    public required string Text { get => _text; init => _text = Interned.Name(value); }
    private readonly string _text = "";
    public long IntValue { get; init; }
    public double RealValue { get; init; }

    /// <summary>
    /// An interpolated string with no holes (`$"abc"`, `$"{{}}"`): its text as
    /// a composite format, braces doubled, for a FormattableString wanted in
    /// its place, as C# converts one. Null for every other literal.
    /// </summary>
    public string? InterpolatedFormat { get; init; }
}
