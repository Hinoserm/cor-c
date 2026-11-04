#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    private const Mods AccessBits = Mods.Public | Mods.Private | Mods.Protected | Mods.Internal;

    /// <summary>
    /// Inside a default spelt out at a call from the type that declared it
    /// (MemberExpr.DefaultIn): its access is judged from that type, as C#
    /// evaluates a default where it was declared; nothing else of the binding
    /// changes, as it would under _made.
    /// </summary>
    private TypeSymbol? _accessFrom;

    /// <summary>The places being bound that are only written, `x.N = v` and `out x.N` (CheckExpr).</summary>
    private readonly HashSet<Expr> _onlyWritten = new(ReferenceEqualityComparer.Instance);

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
        if (_accessFrom is not null) return _accessFrom;
        // A DECLARATION BEING READ is its own type's, whatever body asked
        // for it: a constant's initialiser evaluated on demand (_scope, as
        // EvaluateConstantOf sets it) names its own type's private ones.
        TypeSymbol? here = _scope ?? (_thisType is not null && IsClosure(_thisType) ? _lexicalType ?? _capturedThisType : _thisType);
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
        // A place only written is bound as a read first: what it reads by
        // is no matter (CheckExpr's _onlyWritten).
        if (used is FieldSym or PropertyGetSym && _onlyWritten.Contains(site)) return;
        switch (used)
        {
            case FieldSym { Field: { Declared: true } f }:
                Refuse(site, f.Owner, Spelt(f), f.Mods, f.Static, Receiver(site), made: false, Sibling(f, write: false), write: false);
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

    /// <summary>
    /// A constant this code may not reach, passed over by the lookups
    /// (FindConstant, FindText, FindDecimal) as C#'s member lookup passes
    /// over what it may not reach: a base's accessible one of the name, an
    /// outer type's, a later `using static`'s, or a field, property or method
    /// of the name is found instead. Noted (_passedConstant), and said as
    /// CS0122 only where the name then finds nothing (PassedConstant).
    /// </summary>
    private bool ConstantOutOfReach(TypeSymbol t, string name)
    {
        if (!_constantDeclarations.TryGetValue((t, name), out var declared) || !ChecksAccess()) return false;
        if (Inaccessible(t, declared.Field.Mods, isStatic: true, null, made: false) is null) return false;
        _passedConstant = (t, name);
        return true;
    }

    /// <summary>The constant last passed over by the lookup under way (CheckName, CheckMember clear it).</summary>
    private (TypeSymbol Owner, string Name)? _passedConstant;

    /// <summary>
    /// CS0122 for a name that found nothing but a constant out of reach, or null.
    /// For a member, `on` is the type asked: a constant passed over while
    /// its target was bound is not the one meant.
    /// </summary>
    private string? PassedConstant(string name, TypeSymbol? on = null)
        => _passedConstant is { } passed && passed.Name == name && (on is null || DerivesFromType(on, passed.Owner))
            ? $"CS0122: '{passed.Owner.Name}.{name}' is inaccessible due to its protection level"
            : null;

    /// <summary>A field as its code names it: an auto property's `<N>` is N.</summary>
    private static string Spelt(FieldSymbol f) => f.Name.Length > 2 && f.Name[0] == '<' && f.Name[^1] == '>' ? f.Name[1..^1] : f.Name;

    /// <summary>
    /// A place written -- assigned, incremented, passed by ref or out --
    /// checked by its writer's access: a field's own, and an auto property's
    /// field, read in place of get_N, written in place of set_N, so that
    /// `private set` makes it private. A place only written was not checked
    /// as a read (_onlyWritten), so this is its one check.
    /// </summary>
    private void CheckWriteAccess(Expr target)
    {
        if (!ChecksAccess() || !_r.Resolved.TryGetValue(target, out Sym? sym)) return;
        if (sym is FieldSym { Field: { Declared: true } f })
        {
            Refuse(target, f.Owner, Spelt(f), f.WriteMods, f.Static, Receiver(target), made: false, f.Mods, write: true);
        }
    }

    /// <summary>
    /// An object initialiser's or a `with`'s member, `new C { N = 1 }`: a
    /// field or property set (written, by its setter's access) or, `N = { ...
    /// }`, read and filled. The new object is the receiver, of its own type.
    /// </summary>
    private void CheckInitAccess(Node init, string name, FieldSymbol? field, MethodSymbol? method, bool write)
    {
        if (!ChecksAccess()) return;
        if (field is { Declared: true })
            Refuse(init, field.Owner, name, write ? field.WriteMods : field.Mods, field.Static, null, made: false, Sibling(field, write), write);
        else if (method is { Decl: { Made: false } d, ExplicitInterface: null })
            Refuse(init, method.Owner, Accessor(method, out Mods? other, out bool writes) ?? name, d.Mods, method.Static, null, made: false, other, writes);
    }

    /// <summary>
    /// A method's access checked: a constructor by `new` (`byNew`, a `new`
    /// node's by default) only where its type's code is, protected or not --
    /// a `: base(...)` reaches a protected one.
    /// </summary>
    private void CheckMethod(Expr site, MethodSymbol m, bool? byNew = null)
    {
        if (!ChecksAccess() || m.Decl is not { Made: false } d || m.ExplicitInterface is not null) return;
        Mods? other = null;
        bool writes = false;
        string name = m.IsCtor ? m.Owner.Name : Accessor(m, out other, out writes) ?? m.Name;
        Refuse(site, m.Owner, name, d.Mods, m.Static, Receiver(site), made: m.IsCtor && (byNew ?? site is NewExpr), other, writes);
    }

    /// <summary>
    /// An auto property's field's other accessor's access, where the two
    /// differ: what says whether the property or only one accessor is out
    /// of reach (Refuse's CS0271 and CS0272).
    /// </summary>
    private static Mods? Sibling(FieldSymbol f, bool write) => f.Mods == f.WriteMods ? null : write ? f.Mods : f.WriteMods;

    /// <summary>
    /// A property's accessor, get_N or set_N, as the property's name, its
    /// other accessor's access in `other` (none if it has no other) and
    /// whether it is the setter in `writes`; null for any other method.
    /// </summary>
    private static string? Accessor(MethodSymbol m, out Mods? other, out bool writes)
    {
        other = null;
        writes = false;
        bool getter = m.Name.StartsWith("get_", StringComparison.Ordinal), setter = m.Name.StartsWith("set_", StringComparison.Ordinal);
        int indices = m.Params.Count - (setter ? 1 : 0);
        if (!getter && !setter || m.Owner.Decl?.Members.Find(member => member is PropertyDecl { ExplicitInterface: null } p
                && (p.Params.Count > 0 ? "Item" : p.Name) == m.Name[4..] && p.Params.Count == indices) is not PropertyDecl property)
            return null;
        writes = setter;
        if (getter ? property.HasSetter : property.Getter is not null || property.Auto)
            other = AccessorMods(property.Mods, getter ? property.SetMods : property.GetMods);
        return property.Params.Count > 0 ? "this[]" : property.Name;
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

    /// <summary>
    /// Refused as C# says it: CS1540 through the wrong receiver; for a
    /// property one of whose accessors is out of reach while the other
    /// (`other`) is not, CS0272 for its setter (`write`) or CS0271 for its
    /// getter; otherwise CS0122. Said once a place.
    /// </summary>
    private void Refuse(Node site, TypeSymbol owner, string name, Mods mods, bool isStatic, Expr? receiver, bool made,
                        Mods? other = null, bool write = false)
    {
        string? why = Inaccessible(owner, mods, isStatic, receiver, made);
        if (why is null) return;
        string message = why.Length != 0 ? why.Replace("{member}", $"{owner.Name}.{name}")
            : other is { } otherMods && Inaccessible(owner, otherMods, isStatic, receiver, made) is null
                ? $"CS{(write ? "0272" : "0271")}: the property or indexer '{owner.Name}.{name}' cannot be used in this context because the {(write ? "set" : "get")} accessor is inaccessible"
                : $"CS0122: '{owner.Name}.{name}' is inaccessible due to its protection level";
        // ONCE A PLACE, however often it is bound again: by what the error
        // list holds, which a trial binding's discarded errors (Peek) leave.
        string file = Where(site);
        if (_r.Errors.Exists(said => said.Line == site.Line && said.Col == site.Col && said.Message == message && said.File == file)) return;
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
        for (TypeSymbol? at = here; at is not null; at = at.Decl is { } atDecl ? OuterOf(atDecl) : null)
            if (ReferenceEquals(at, owner)) return true;
        string? ownerPath = owner.Decl is { } ownerDecl ? TypePath(ownerDecl) : null;
        for (TypeSymbol? at = here; at is not null; at = at.Decl is { } atDecl ? OuterOf(atDecl) : null)
        {
            // A NESTED TYPE'S COPY has no Outer, only its template's path
            // (List.Enumerator): the types it is inside are that path's.
            if (ownerPath is null || at.Decl is not { } d) continue;
            for (string? path = TypePath(d); path is not null; path = path.Contains('.') ? path[..path.LastIndexOf('.')] : null)
                if (path == ownerPath) return true;
        }
        return false;
    }
}
