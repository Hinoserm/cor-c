#nullable enable
namespace Corsac.Lang;

public sealed class MemberExpr : Expr
{
    /// <summary>
    /// The null test that guards this read has already been made, right here,
    /// by whatever generated it.
    ///
    /// `x is { Kind: A }` lowers to `x != null && x.Kind == A`, and the second
    /// half is only ever reached when the first was true -- so requiring it to
    /// be proved again means proving something about a MEMBER ACCESS, which is
    /// a wider change than this pattern is worth. The lowering knows what it
    /// wrote; this is it saying so.
    /// </summary>
    public bool Guarded { get; init; }

    public required Expr Target { get; init; }

    /// <summary>
    /// Settable because a call to a generic method is REPOINTED at the copy
    /// compiled for its type arguments: `Where` becomes `Where$Node`. The
    /// receiver and the arguments are untouched; only which member is being
    /// asked for changes.
    /// </summary>
    public required string Name { get; set; }
    public List<TypeRef> TypeArgs { get; } = new();
    /// <summary>True for <c>?.</c>, which short-circuits on null.</summary>
    public bool NullConditional { get; init; }
}
