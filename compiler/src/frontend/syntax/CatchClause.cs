#nullable enable
namespace Corsac.Lang;

public sealed class CatchClause : Node, ICanonSlot, ICanonShape
{
    /// <summary>
    /// A CATCH OF A CONSTRUCTED TYPE OVER A SHARED COPY'S PARAMETERS --
    /// `catch (GenEx<T>)` -- marked as `is GenEx<T>` is (Monomorphiser.
    /// CanonTested): the entry its `this`'s type context holds for that
    /// type, or -1.
    /// </summary>
    public int CanonSlot { get; set; } = -1;
    /// <summary>The `this` that entry is read through (ICanonSlot).</summary>
    public Expr? CanonSelf { get; set; }
    /// <summary>The caught type's arguments as written, in a shared method copy (ICanonShape), or null.</summary>
    public List<TypeRef>? ShapeArgs { get; set; }

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
