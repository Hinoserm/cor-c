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
    // To read: one shared empty list until written (WritableTypeArgs), never written through.
    public List<TypeRef> TypeArgs => _typeArgs ?? NoTypeArgs;
    /// <summary>To write: made on first use; almost every node has none.</summary>
    public List<TypeRef> WritableTypeArgs => _typeArgs ??= new();
    private List<TypeRef>? _typeArgs;
    private static readonly List<TypeRef> NoTypeArgs = new();

    /// <summary>
    /// A variable handed to a generic local function as one it captured
    /// (Binder.PassCaptures): the function's written name. The variable is
    /// the one seen where the function was declared, not whatever a lambda
    /// at the call names the same (Binder.LookupCaptured).
    /// </summary>
    public string? CaptureOf { get; set; }
}
