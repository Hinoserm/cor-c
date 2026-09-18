#nullable enable
namespace Corsac.Lang;

public sealed class NameExpr : Expr
{
    /// <summary>Settable for the same reason MemberExpr.Name is.</summary>
    public required string Name { get; set; }
    /// <summary>Explicit type arguments, as in <c>Foo&lt;int&gt;()</c>.</summary>
    public List<TypeRef> TypeArgs { get; } = new();
}
