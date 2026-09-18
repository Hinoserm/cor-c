#nullable enable
namespace Corsac.Lang;

public sealed class CatchClause : Node
{
    public TypeRef? Type { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// `catch (E e) when (cond)` -- an EXCEPTION FILTER.
    ///
    /// A filter that answers false does not catch, and the search carries on to
    /// the next clause. That is why it cannot be an `if` at the top of the body
    /// that rethrows: a body has already caught, and rethrowing starts a second
    /// search from here rather than continuing the first.
    /// </summary>
    public Expr? When { get; init; }

    public required Block Body { get; init; }
}
