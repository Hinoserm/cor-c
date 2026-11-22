#nullable enable
namespace Corsac.Lang;

public sealed class BinaryExpr : Expr
{
    public required BinOp Op { get; init; }
    public required Expr Left { get; init; }
    public required Expr Right { get; init; }

    /// <summary>
    /// AN INTERPOLATED STRING'S, where this is the join its pieces were made
    /// (Parser.Interpolate): its composite format and its holes as written,
    /// for a FormattableString or an IFormattable wanted in its place (C#'s
    /// interpolated string conversion: FormattableStringFactory.Create).
    /// </summary>
    public InterpolatedParts? Interpolation { get; set; }

    /// <summary>Every copy of a join carries this; see InterpolatedParts.Copied.</summary>

    /// <summary>
    /// This test for null was written by a PATTERN, not by the author.
    ///
    /// A property pattern reads its subject's members, so it asks first
    /// whether there is a subject to read them off -- and where the subject is
    /// a value type there is nothing to ask. C# does not write the test at all
    /// there, and `def is { Role: Role.Def }` over a struct was refused with
    /// "a value type can never be null", which is true of the comparison and
    /// not of the pattern. The test `using` writes before disposing is the
    /// same: `using (var e = set.GetEnumerator())` over a struct enumerator.
    /// </summary>
    public bool PatternNullTest { get; init; }

    /// <summary>
    /// This equality is a CONSTANT PATTERN as the parser read it -- `x is A.B`
    /// with nothing after the name. A dotted name there may be a type rather
    /// than a constant, and only binding can say which: C# takes it as a type
    /// when it names one, so `o is System.Text.StringBuilder` is a type test.
    /// </summary>
    public bool PatternConstant { get; init; }
}

/// <summary>An interpolated string's composite format (`{0,5:x8}` for each hole, braces doubled) and its holes.</summary>
public sealed record InterpolatedParts(string Format, List<Expr> Holes)
{
    /// <summary>
    /// THE SAME PARTS OVER COPIED HOLES, for whatever copies the join it hangs
    /// on (Monomorphiser.Rewrite, Binder.Spell, the templates' Gir): a copy
    /// made without them is an ordinary string where a FormattableString was
    /// asked for, refused as one.
    /// </summary>
    public InterpolatedParts Copied(System.Func<Expr, Expr> copy) => new(Format, Holes.ConvertAll(h => copy(h)));
}
