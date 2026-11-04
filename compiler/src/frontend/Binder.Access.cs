#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    private const Mods AccessBits = Mods.Public | Mods.Private | Mods.Protected | Mods.Internal;

    /// <summary>
    /// An accessor's modifiers: the property's, with the accessor's own access
    /// in place of the property's when it writes one -- `public int N { get;
    /// private set; }` has a public get_N and a private set_N (C# 15.7.6).
    /// </summary>
    internal static Mods AccessorMods(Mods property, Mods accessor)
        => (accessor & AccessBits) == 0 ? property : (property & ~AccessBits) | (accessor & AccessBits);

    /// <summary>
    /// Whether the access of what is being bound is checked: code someone
    /// wrote. Code the compiler wrote (_made, a Made or LocalCopy member), and
    /// the prelude, name what they need.
    /// </summary>
    private bool ChecksAccess()
    {
        if (_made > 0) return false;
        TypeDecl? written = (_scope ?? _thisType)?.Decl;
        return written is not null && written.File != "<prelude>" && _member is not ({ LocalCopy: true } or { Made: true });
    }

    /// <summary>
    /// The type whose text the code being bound is in: a closure's is the
    /// type its lambda was written in; none known, nothing is refused.
    /// A lambda inside a lambda is the outermost's lexical type
    /// (_lexicalType), never the closure it captured (_capturedThisType).
    /// </summary>
    private TypeSymbol? AccessHere()
    {
        TypeSymbol? here = _thisType is not null && IsClosure(_thisType) ? _lexicalType ?? _capturedThisType : _thisType ?? _scope;
        return here is not null && IsClosure(here) ? null : here;
    }

    /// <summary>
    /// Told of every name, call, `new`, indexer and setter a node is bound to
    /// (NodeBinding.Checked): refused with C#'s CS0122 or CS1540 where the
    /// code being bound may not reach it.
    /// </summary>
    private void CheckAccess(Expr site, object used)
    {
        if (!ChecksAccess()) return;
        switch (used)
        {
            case FieldSym { Field: { Declared: true } f }:
                Refuse(site, f.Owner, Spelt(f), f.Mods, f.Static, Receiver(site), made: false);
                break;
            case PropertyGetSym { Getter: var getter }:
                CheckMethod(site, getter);
                break;
            case PropertySetSym { Setter: var setter }:
                CheckMethod(site, setter);
                break;
            case MethodSymbol method:
                CheckMethod(site, method);
                break;
        }
    }

    /// <summary>Whether the code being bound may reach method `m` through `receiver` (an overload's candidacy).</summary>
    private bool Reachable(MethodSymbol m, Expr? receiver, bool made)
        => m.Decl is not { Made: false } d || m.ExplicitInterface is not null
            || Inaccessible(m.Owner, d.Mods, m.Static, receiver, made) is null;

    /// <summary>A field as its code names it: an auto property's `<N>` is N.</summary>
    private static string Spelt(FieldSymbol f) => f.Name.Length > 2 && f.Name[0] == '<' && f.Name[^1] == '>' ? f.Name[1..^1] : f.Name;

    /// <summary>
    /// A place written -- assigned, incremented, passed by ref or out --
    /// checked by its writer's access where it differs from its reader's: an
    /// auto property's field read in place of get_N is written in place of
    /// set_N, and `private set` makes that private.
    /// </summary>
    private void CheckWriteAccess(Expr target)
    {
        if (!ChecksAccess() || !_r.Resolved.TryGetValue(target, out Sym? sym)) return;
        if (sym is FieldSym { Field: { Declared: true } f } && f.WriteMods != f.Mods)
        {
            Refuse(target, f.Owner, Spelt(f), f.WriteMods, f.Static, Receiver(target), made: false);
        }
    }

    /// <summary>
    /// A method's access checked: a constructor by `new` (`byNew`, a `new`
    /// node's by default) only where its type's code is, protected or not --
    /// a `: base(...)` reaches a protected one.
    /// </summary>
    /// <summary>
    /// An object initialiser's or a `with`'s member, `new C { N = 1 }`: a
    /// field or property set (written, by its setter's access) or, `N = { ...
    /// }`, read and filled. The new object is the receiver, of its own type.
    /// </summary>
    private void CheckInitAccess(Node init, string name, FieldSymbol? field, MethodSymbol? method, bool write)
    {
        if (!ChecksAccess()) return;
        if (field is { Declared: true })
            Refuse(init, field.Owner, name, write ? field.WriteMods : field.Mods, field.Static, null, made: false);
        else if (method is { Decl: { Made: false } d, ExplicitInterface: null })
            Refuse(init, method.Owner, name, d.Mods, method.Static, null, made: false);
    }

    private void CheckMethod(Expr site, MethodSymbol m, bool? byNew = null)
    {
        if (!ChecksAccess() || m.Decl is not { Made: false } d || m.ExplicitInterface is not null) return;
        Refuse(site, m.Owner, m.IsCtor ? m.Owner.Name : m.Name, d.Mods, m.Static, Receiver(site), made: m.IsCtor && (byNew ?? site is NewExpr));
    }

    /// <summary>
    /// The expression a member is reached through, or null where it is `this`
    /// or `base`, implicit or written, or a type: what C#'s protected rule asks
    /// the type of (CS1540).
    /// </summary>
    private static Expr? Receiver(Expr site)
    {
        Expr? through = site switch
        {
            MemberExpr m => m.Target,
            CallExpr { Target: MemberExpr m } => m.Target,
            IndexExpr ix => ix.Target,
            _ => null,
        };
        return through is ThisExpr or BaseExpr ? null : through;
    }

    private void Refuse(Node site, TypeSymbol owner, string name, Mods mods, bool isStatic, Expr? receiver, bool made)
    {
        string? why = Inaccessible(owner, mods, isStatic, receiver, made);
        if (why is null) return;
        string message = why.Length == 0
            ? $"CS0122: '{owner.Name}.{name}' is inaccessible due to its protection level"
            : why.Replace("{member}", $"{owner.Name}.{name}");
        Error(site, message);
    }

    /// <summary>
    /// Null where the code being bound may reach a member of `owner` declared
    /// with `mods`, through `receiver`; otherwise "" (CS0122) or CS1540's
    /// message. C# 7.5.3 as Roslyn's AccessCheck has it:
    ///  - public: anywhere; an interface's or enum's member written without
    ///    access is public;
    ///  - internal: the same assembly -- the class library, or the program
    ///    (the kernel and its modules one);
    ///  - private, written so or a class's or struct's by default: inside the
    ///    type, or a type nested in it, a template's copies one type;
    ///  - protected: there, or in a type derived from it (or nested in one)
    ///    through a receiver of that type or one derived from it; a
    ///    constructor by `new` only there;
    ///  - protected internal is either; private protected both.
    /// </summary>
    private string? Inaccessible(TypeSymbol owner, Mods mods, bool isStatic, Expr? receiver, bool made)
    {
        if (owner.Decl is not { } ownerDecl || ownerDecl.File == "<prelude>") return null;
        Mods access = mods & AccessBits;
        if (access == Mods.None && owner.Kind is TypeKind.Interface or TypeKind.Enum) return null;
        if (access == Mods.Public) return null;
        TypeSymbol? here = AccessHere();
        if (here?.Decl is not { } hereDecl) return null;
        bool sameAssembly = ClassLibrarySide(ownerDecl) == ClassLibrarySide(hereDecl);
        if (access == Mods.Internal) return sameAssembly ? null : "";
        if (Within(here, owner)) return null;
        if (access is Mods.None or Mods.Private) return "";
        if (access == (Mods.Protected | Mods.Internal) && sameAssembly) return null;
        if (access == (Mods.Private | Mods.Protected) && !sameAssembly) return "";
        if (made) return "";
        // PROTECTED: from a type derived from the owner, or nested in one,
        // and an instance member only through that type (C#'s CS1540): a
        // Derived may not read another class's Base part.
        TypeSymbol? qualifier = isStatic || receiver is null || _r.Resolved.TryGetValue(receiver, out Sym? named) && named is TypeNameSym
            ? null : _r.TypeOf(receiver).Symbol;
        bool derived = false;
        for (TypeSymbol? at = here; at is not null; at = at.Decl is { } atDecl ? OuterOf(atDecl) : null)
        {
            if (!DerivesFromType(at, owner)) continue;
            derived = true;
            if (qualifier is null || DerivesFromType(qualifier, at)) return null;
        }
        return derived && qualifier is not null
            ? $"CS1540: cannot reach the protected member '{{member}}' through a '{qualifier.Name}'; it must be through '{here.Name}' or a type derived from it"
            : "";
    }

    /// <summary>A type derived from another, a template's copy counting as the template.</summary>
    private static bool DerivesFromType(TypeSymbol type, TypeSymbol of)
    {
        string ofPath = of.Decl is { } d ? TypePath(d) : of.Name;
        for (TypeSymbol? t = type; t is not null; t = t.Base)
            if (ReferenceEquals(t, of) || t.Decl is { } td && TypePath(td) == ofPath) return true;
        return false;
    }

    /// <summary>
    /// Whether `here` is `owner` or written inside it, however deep: a
    /// template's copies are the template's (Box$int reaches Box$string's
    /// private members, as Box&lt;int&gt; does Box&lt;string&gt;'s), and a
    /// copy's nested type is inside the copy (List$int's Enumerator).
    /// </summary>
    private bool Within(TypeSymbol here, TypeSymbol owner)
    {
        string? ownerPath = owner.Decl is { } ownerDecl ? TypePath(ownerDecl) : null;
        for (TypeSymbol? at = here; at is not null; at = at.Decl is { } atDecl ? OuterOf(atDecl) : null)
        {
            if (ReferenceEquals(at, owner)) return true;
            // A NESTED TYPE'S COPY has no Outer, only its template's path
            // (List.Enumerator): the types it is inside are that path's.
            if (ownerPath is null || at.Decl is not { } d) continue;
            for (string? path = TypePath(d); path is not null; path = path.Contains('.') ? path[..path.LastIndexOf('.')] : null)
                if (path == ownerPath) return true;
        }
        return false;
    }
}
