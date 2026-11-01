using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

public sealed partial class Lowering
{
    /// <summary>
    /// WHAT THIS RING UNIT OFFERS OTHER IMAGES (PublicApi, `.corsac.public`):
    /// of each method and static this unit defines, whether C# lets another
    /// assembly name it -- shown (Binder.ShownOutside) in a type public all
    /// the way out -- and so whether a kernel linked with --exports=public
    /// keeps it out of line and in its export table. A public type's startup
    /// work (StaticInit$ and its ready word) is offered with it: a module
    /// touching the type's statics runs it; a packed method's copy that runs
    /// inside its caller's state ($inside) with the method. Of the program's
    /// own types every other one is withheld: `internal` is the kernel's
    /// assembly's alone. Of the class library's, none is: what a module's
    /// compiled code calls of the runtime and the library is not all public,
    /// and is exported where the link keeps it, as it was. Every generic
    /// instantiation is left as it was.
    /// </summary>
    private void RecordPublicApi()
    {
        foreach (TypeSymbol t in _b.Types.Values.ToList())
        {
            if (t.MembersPending || t.Decl is not TypeDecl d || d.LocalOnly || d.Specialised || d.Canon is not null || d.TypeParams.Count > 0)
                continue;
            bool library = IsLibrary(t) || SystemCode(t);
            bool open = Offered(d);
            foreach (MethodSymbol m in t.Methods)
            {
                // Defined here: a part of a type whose first part is another
                // unit's is this unit's code (Elsewhere, OwnedImplementation).
                if (!ReferenceEquals(m.Owner, t) || !_required.Contains(m) || !Emits(m) || m.Decl is not MethodDecl method || method.LocalCopy)
                    continue;
                bool offered = open && (method.Name == "StaticInit$" || Binder.ShownOutside(d, method));
                if (library && !offered) continue;
                List<string> into = offered ? _m.PublicSymbols : _m.WithheldSymbols;
                into.Add(CallLabel(m));
                if (CopiesInside(m)) into.Add((TakesContext(m) ? BodyLabel(m) : Label(m)) + "$inside");
            }
            foreach (FieldSymbol f in t.Fields)
            {
                // A [ThreadStatic] is reached through its number (ThreadStaticIndex), never its own symbol.
                if (!f.Static || !_statics.Contains(f) || FieldDeclOf(f) is FieldDecl declared && Binder.IsThreadStatic(declared)) continue;
                bool offered = open && (f.Name == BindResult.ReadyField || FieldDeclOf(f) is MemberDecl field && Binder.ShownOutside(d, field));
                if (library && !offered) continue;
                (offered ? _m.PublicSymbols : _m.WithheldSymbols).Add(StaticSymbol(f));
            }
        }
    }

    /// <summary>
    /// A type another assembly can name: public, and every type it is
    /// declared in public too. One written in a namespace is at the top: its
    /// Outer is the namespace (Binder.HiddenFromProgram).
    /// </summary>
    private bool Offered(TypeDecl d)
    {
        for (TypeDecl? at = d; at is not null; at = _b.Types.TryGetValue(at.Outer!, out TypeSymbol? holder) ? holder.Decl : null)
        {
            if ((at.Mods & Mods.Public) == 0) return false;
            if (at.Outer is null || at.Outer == at.Namespace) return true;
        }
        return false;
    }
}
