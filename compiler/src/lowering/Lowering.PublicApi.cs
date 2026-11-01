using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

public sealed partial class Lowering
{
    /// <summary>
    /// WHAT THIS RING UNIT OFFERS OTHER IMAGES (PublicApi, `.corsac.public`):
    /// of each method and static of the program's own types this unit
    /// defines, whether C# lets another assembly name it -- `public`, or
    /// `protected` for a module's class deriving from one, in a type public
    /// all the way out -- and so whether a kernel linked with --exports keeps
    /// it out of line and in its export table, or may inline and prune it and
    /// leaves it out. `internal` is the kernel's assembly's alone. A public
    /// type's startup work (StaticInit$ and its ready word) is offered with
    /// it: a module touching the type's statics runs it. The library's types
    /// and every generic instantiation are left as they were: what a module
    /// imports of those is not the kernel's API.
    /// </summary>
    private void RecordPublicApi()
    {
        foreach (TypeSymbol t in _b.Types.Values.ToList())
        {
            if (t.MembersPending || t.Decl is not TypeDecl d || d.Elsewhere || d.LocalOnly || d.Specialised || d.Canon is not null
                || d.TypeParams.Count > 0 || IsLibrary(t) || SystemCode(t))
                continue;
            bool open = Offered(d);
            foreach (MethodSymbol m in t.Methods)
            {
                if (!ReferenceEquals(m.Owner, t) || !_required.Contains(m) || m.Decl is not MethodDecl method || method.LocalCopy || method.TypeParams.Count > 0)
                    continue;
                bool offered = open && (t.Kind == TypeKind.Interface || method.Name == "StaticInit$" || (method.Mods & (Mods.Public | Mods.Protected)) != 0);
                (offered ? _m.PublicSymbols : _m.WithheldSymbols).Add(CallLabel(m));
            }
            foreach (FieldSymbol f in t.Fields)
            {
                // A [ThreadStatic] is reached through its number (ThreadStaticIndex), never its own symbol.
                if (!f.Static || !_statics.Contains(f) || FieldDeclOf(f) is null && f.Name != BindResult.ReadyField
                    || FieldDeclOf(f) is FieldDecl { } declared && Binder.IsThreadStatic(declared)) continue;
                bool offered = open && (f.Name == BindResult.ReadyField || FieldDeclOf(f) is MemberDecl { Mods: var mods } && (mods & (Mods.Public | Mods.Protected)) != 0);
                (offered ? _m.PublicSymbols : _m.WithheldSymbols).Add(StaticSymbol(f));
            }
        }
    }

    /// <summary>A type another assembly can name: public, and every type it is declared in public too.</summary>
    private bool Offered(TypeDecl d)
    {
        for (TypeDecl? at = d; at is not null; at = at.Outer is { } outer && _b.Types.TryGetValue(outer, out TypeSymbol? holder) ? holder.Decl : null)
        {
            if ((at.Mods & Mods.Public) == 0) return false;
            if (at.Outer is null) return true;
        }
        return false;
    }
}
