#nullable enable
namespace Corsac.Lang;

public sealed class ThisExpr : Expr
{
    /// <summary>
    /// A `this` the compiler wrote for a shared class copy's type context
    /// (CanonSelf): in instance code the object, in a static member of a
    /// canonical class copy the context it is handed (Binder.ContextStatic,
    /// Lowering.HasThis). A `this` written in source never is one, and a
    /// static method still refuses it.
    /// </summary>
    public bool Context { get; init; }
}
