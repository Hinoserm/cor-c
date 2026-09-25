#nullable enable
namespace Corsac.Lang;

public sealed class NameExpr : Expr
{
    /// <summary>Settable for the same reason MemberExpr.Name is.</summary>
    public required string Name { get; set; }
    /// <summary>Written after <c>global::</c>: a type or a namespace, never a
    /// local, a parameter or a member of the enclosing types.</summary>
    public bool Global { get; set; }
    /// <summary>Explicit type arguments, as in <c>Foo&lt;int&gt;()</c>.</summary>
    public List<TypeRef> TypeArgs { get; } = new();
}
