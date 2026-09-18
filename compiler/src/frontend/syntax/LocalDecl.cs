#nullable enable
namespace Corsac.Lang;

public sealed class LocalDecl : Stmt
{
    /// <summary>Null when written as <c>var</c>; inference fills it in later.</summary>
    public TypeRef? Type { get; init; }
    public required string Name { get; init; }
    public Expr? Init { get; init; }

    /// <summary>
    /// This declaration came from a local-function declaration. Local
    /// functions are in scope throughout their containing block, unlike an
    /// ordinary local whose scope begins at its declaration.
    /// </summary>
    public bool LocalFunction { get; init; }

    /// <summary>
    /// `const int Bx = 3;` -- a NAME FOR A VALUE, not storage, exactly as a
    /// const field is. C# requires the initialiser to be a constant expression
    /// and lets the name stand wherever one is wanted: `r is Bx or Bp` is a
    /// constant pattern and is a line in this compiler's own assembler.
    /// </summary>
    public bool IsConst { get; init; }

    /// <summary>
    /// The others in the same declaration: `int line = _line, col = _col;`.
    ///
    /// One statement declaring several is C#, and they share the type and the
    /// SCOPE -- which is why they hang off the first one rather than becoming a
    /// block of their own, since a block would hide them from everything after
    /// it.
    /// </summary>
    public List<LocalDecl> Also { get; } = new();
}
