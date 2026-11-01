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
    /// assembly's alone. Of the class library's the public surface is offered
    /// as a shared library's is -- a module built later may call what the
    /// kernel never did -- and nothing withheld: what a module's compiled code
    /// calls of the runtime and the library is not all public, and is exported
    /// where the link keeps it. A static, and all of the library's, is offered
    /// where the link keeps it (OptionalSymbols): the link may lay one down or
    /// fold it. Every generic instantiation is left as it was.
    /// </summary>
    private void RecordPublicApi()
    {
        foreach (TypeSymbol t in _b.Types.Values.ToList())
        {
            if (t.MembersPending || t.Decl is not TypeDecl d || d.LocalOnly || d.Specialised || d.Canon is not null || d.TypeParams.Count > 0)
                continue;
            bool library = IsLibrary(t) || SystemCode(t);
            bool open = Offered(d);
            // ITS MEMBERS READ IN ITS OWN SCOPE (MemberWatch): every type of the
            // unit is walked, and an imported one -- the kernel's Arch, in
            // every unit -- emits nothing here but what the unit copied, on
            // which the unit depends whole already. Read whole for the unit,
            // 139 kernel units were compiled again for an Arch method none of
            // them called.
            using MemberWatch.Scope own = MemberWatch.Within(d.Watch);
            foreach (MethodSymbol m in t.Methods)
            {
                // Defined here: a part of a type whose first part is another
                // unit's is this unit's code (Elsewhere, OwnedImplementation).
                if (!ReferenceEquals(m.Owner, t) || !_required.Contains(m) || !Emits(m) || m.Decl is not MethodDecl method || method.LocalCopy)
                    continue;
                bool offered = open && (method.Name == "StaticInit$" || Binder.ShownOutside(d, method));
                if (library && !offered) continue;
                List<string> into = !offered ? _m.WithheldSymbols : library ? _m.OptionalSymbols : _m.PublicSymbols;
                into.Add(CallLabel(m));
                if (CopiesInside(m)) into.Add((TakesContext(m) ? BodyLabel(m) : Label(m)) + "$inside");
            }
            foreach (FieldSymbol f in t.Fields)
            {
                // Laid down here, as the statics' rounds do (Run): another
                // unit's type's is that unit's. A [ThreadStatic] is reached
                // through its number (ThreadStaticIndex), never its own symbol.
                if (!f.Static || !_statics.Contains(f) || d.Elsewhere || FieldDeclOf(f) is FieldDecl declared && Binder.IsThreadStatic(declared)) continue;
                bool offered = open && (f.Name == BindResult.ReadyField || FieldDeclOf(f) is MemberDecl field && Binder.ShownOutside(d, field));
                if (library && !offered) continue;
                (offered ? _m.OptionalSymbols : _m.WithheldSymbols).Add(StaticSymbol(f));
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
