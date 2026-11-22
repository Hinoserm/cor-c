#nullable enable
namespace Corsac.Lang;

public sealed class BinaryExpr : Expr
{
    public required BinOp Op { get; init; }
    public required Expr Left { get; init; }
    public required Expr Right { get; init; }

    /// <summary>
    /// AN INTERPOLATED STRING'S, where this is the join its pieces were made
    /// (Parser.Interpolate): its composite format and where its holes are,
    /// for a FormattableString or an IFormattable wanted in its place (C#'s
    /// interpolated string conversion: FormattableStringFactory.Create).
    /// Plain data, carried as it is by every copy of the tree.
    /// </summary>
    public InterpolatedParts? Interpolation { get; set; }

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

/// <summary>
/// An interpolated string's composite format (`{0,5:x8}` for each hole,
/// braces doubled) and its holes, by WHERE they are in the join and never
/// as nodes of their own. The join's pieces, left to right, are its literal
/// runs and its holes, with an empty literal first where a hole would be,
/// so no hole is ever piece 0. Each hole is as written or wrapped: its format
/// a ToString call on it (Parser.Formatted), its alignment a pad of that
/// (Parser.Aligned). The binder takes the holes out of the join it has
/// checked, so each is checked once (Binder.InterpolatedHoles), and a copy of
/// the tree has its own holes in its own join.
/// </summary>
public sealed record InterpolatedParts(string Format, int Pieces, int[] Holes)
{
    /// <summary>A hole's entry in Holes: its piece, and the wrappings on it.</summary>
    public static int Hole(int piece, bool formatted, bool aligned) => piece * 4 + (formatted ? 1 : 0) + (aligned ? 2 : 0);
}
