#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A CHAIN OF NAMES AND MEMBER ACCESSES, `Box&lt;int&gt;.G.List&lt;string&gt;`: its
/// links leftmost first, and each link's name and type arguments. What the
/// parser reads to give a nested type its outer parameters (Parser.FinishFamily)
/// and the monomorphiser to name a type nested in a generic one
/// (Monomorphiser.NestedGenericPath), read the same way in both.
/// </summary>
public static class Chains
{
    /// <summary>The links, leftmost name first; null for anything but names and plain dots.</summary>
    public static List<Expr>? Links(Expr e)
    {
        List<Expr> links = new();
        for (Expr at = e; ; )
        {
            if (at is NameExpr) { links.Add(at); links.Reverse(); return links; }
            if (at is not MemberExpr { NullConditional: false } m) return null;
            links.Add(m);
            at = m.Target;
        }
    }

    /// <summary>A link's type arguments.</summary>
    public static List<TypeRef> Args(Expr e) => e is NameExpr n ? n.TypeArgs : ((MemberExpr)e).TypeArgs;

    /// <summary>A link's type arguments, to write.</summary>
    public static List<TypeRef> WritableArgs(Expr e) => e is NameExpr n ? n.WritableTypeArgs : ((MemberExpr)e).WritableTypeArgs;

    /// <summary>A link's name.</summary>
    public static string Name(Expr e) => e is NameExpr n ? n.Name : ((MemberExpr)e).Name;
}
