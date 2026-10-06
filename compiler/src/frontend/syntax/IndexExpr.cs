#nullable enable
namespace Corsac.Lang;

public sealed class IndexExpr : Expr
{
    public required Expr Target { get; init; }
    public List<Expr> Args { get; } = new();

    /// <summary>`a?[i]`: null when a is, and the element otherwise.</summary>
    public bool NullConditional { get; init; }

    // The get_Item and set_Item a user type's `x[i]` calls (BindResult.Indexers
    // and IndexSetters), and which binding found them (NodeBinding). One
    // generation for the pair: a binding writing either starts both afresh.
    internal MethodSymbol? BoundGetter;
    internal MethodSymbol? BoundSetter;
    internal int BoundIndexBy;
}
