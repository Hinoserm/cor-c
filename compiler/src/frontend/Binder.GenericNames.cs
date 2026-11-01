#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    /// <summary>
    /// The unit is for an image that exports (Lowering.ExportingImage): what
    /// its generic code names is found (GenericNames), for the image to
    /// export what another image's copy of that code calls.
    /// </summary>
    public static bool NamesGenericCode { get; set; }

    /// <summary>
    /// WHAT THIS UNIT'S GENERIC CODE NAMES, AS WRITTEN: of each generic type
    /// and generic method its own sources declare, the members of other
    /// types its bodies name -- `ThrowHelper.KeyNotFound(key)`, a static of
    /// the type it is declared in, `new Node(...)` -- found in the scope the
    /// code was written in (BindResult.GenericNamed). Another image compiles
    /// its own copy of the code for its own type arguments, and the copy
    /// calls what the code names, the library's code as it is, which C# lets
    /// it whatever their accessibility; the image this unit is linked into
    /// exports what of it is withheld (Lowering.RecordReached, PublicApi).
    ///
    /// THE CODE AS WRITTEN, NOT AS A COPY OF IT BINDS: a template no copy of
    /// this unit's ever made is named here all the same -- an Enumerable
    /// method the library never calls, whose copy a program's struct makes.
    /// What a copy this unit made names exactly, lowered, is added to it
    /// there. A type's name looked up here only, never demanded, never
    /// reported (_namingOnly, _quiet); a type found whose members wait is
    /// declared to be asked, as binding declares one (DeclareMembersNow), and
    /// what its declaring wants of the index is wanted as binding's is -- a
    /// pass more, where the code names a type nothing else here used. A member
    /// of what a value holds by its written type: a field's, a property's, a
    /// parameter's, a local's; a `var`'s, or an expression's, is a copy's to
    /// name, bound.
    /// </summary>
    private void GenericNames()
    {
        if (!NamesGenericCode) return;
        TypeSymbol? wasScope = _scope, wasThis = _thisType;
        string wasIn = _in;
        bool wasNaming = _namingOnly;
        _quiet++;
        _namingOnly = true;
        try
        {
            foreach (TypeSymbol t in _r.Types.Values.ToList())
            {
                if (t.Decl is not TypeDecl d || d.Elsewhere || d.Specialised || d.Canon is not null || d.LocalOnly || d.SignatureOnly) continue;
                bool template = d.TypeParams.Count > 0;
                if (!template && !d.Members.Any(m => m is MethodDecl { TypeParams.Count: > 0 })) continue;
                _scope = t;
                _thisType = t;
                _in = d.File;
                Func<MemberDecl, bool>? generic = template ? null : member => member is MethodDecl { TypeParams.Count: > 0 };
                // The names the code declares with their types, the template's
                // fields and properties among them, then what it names.
                Dictionary<string, List<TypeRef>> declared = new(StringComparer.Ordinal);
                Metadata.BodyTypeNames.Walk(d, found: _ => { }, members: generic, declared: (name, type) =>
                {
                    // Not `var`'s, nor a lambda's parameter given no type.
                    if (type.Name is "var" or "") return;
                    if (!declared.TryGetValue(name, out List<TypeRef>? types)) declared[name] = types = new();
                    types.Add(type);
                });
                Metadata.BodyTypeNames.Walk(d,
                    found: reference => NamedType(reference, t, made: false),
                    access: member => NamedMember(member, t, declared),
                    bare: name => { if (name.TypeArgs.Count == 0) NamedAround(name.Name, t); },
                    made: reference => NamedType(reference, t, made: true),
                    members: generic);
            }
        }
        finally
        {
            _quiet--;
            _namingOnly = wasNaming;
            _scope = wasScope;
            _thisType = wasThis;
            _in = wasIn;
        }
    }

    /// <summary>A type an ordinary one, nobody's copy: what a copy of generic code names and does not copy.</summary>
    private static bool Ordinary(TypeSymbol? t)
        => t?.Decl is { TypeParams.Count: 0, Specialised: false, LocalOnly: false } && t.Decl.Canon is null;

    /// <summary>A type written in generic code: its descriptor, and its constructors where `new` makes one.</summary>
    private void NamedType(TypeRef reference, TypeSymbol scope, bool made)
    {
        // Not a generic one, which is a copy, and asking for it could ask for one.
        if (OrdinaryNamed(reference, scope) is not TypeSymbol type) return;
        _r.GenericNamedTypes.Add(type);
        if (made)
            foreach (MethodSymbol ctor in type.FindMethods(type.Name))
                if (ctor.IsCtor) _r.GenericNamedMethods.Add(ctor);
    }

    /// <summary>
    /// `Q.N` in generic code: N of the type Q names, where it names one;
    /// `base.N` and `this.N` as N alone. A member of what a value holds is
    /// left to the copies (Lowering.RecordReached).
    /// </summary>
    private void NamedMember(MemberExpr member, TypeSymbol scope, Dictionary<string, List<TypeRef>> declared)
    {
        if (member.TypeArgs.Count != 0) return;
        if (member.Target is BaseExpr or ThisExpr)
        {
            NamedAround(member.Name, scope);
            return;
        }
        // WHAT A VALUE HOLDS, by the type it was declared with: `sb.Append`
        // of a `StringBuilder sb`, `_root.Size` of a field, `this._root.Size`.
        string? held = member.Target switch
        {
            NameExpr { TypeArgs.Count: 0 } name => name.Name,
            MemberExpr { Target: ThisExpr, TypeArgs.Count: 0 } field => field.Name,
            _ => null,
        };
        if (held is not null && declared.TryGetValue(held, out List<TypeRef>? types))
        {
            foreach (TypeRef type in types)
                if (OrdinaryNamed(type, scope) is TypeSymbol holder) NamedOf(holder, member.Name);
            return;
        }
        // A TYPE'S NAME, by its spelling, as BodyTypeNames.Path reads one: a
        // capital first, or the compiler's own two underscores. As written
        // first, as the binder looks one up, and then by the type a keyword
        // or `Type` stands for (Alias: `string.Concat`, `Type.Missing`).
        if (NamesPath(member.Target) is not string qualifier) return;
        string? alias = Alias(qualifier);
        if (alias is null && !(char.IsUpper(qualifier[0]) || qualifier.StartsWith("__", StringComparison.Ordinal))) return;
        TypeSymbol? named = char.IsUpper(qualifier[0]) || qualifier.StartsWith("__", StringComparison.Ordinal)
            ? OrdinaryNamed(new TypeRef { Name = qualifier, Line = member.Line, Col = member.Col }, scope) : null;
        if (named is null && alias is not null) named = OrdinaryNamed(new TypeRef { Name = alias, Line = member.Line, Col = member.Col }, scope);
        if (named is not null) NamedOf(named, member.Name);
    }

    /// <summary>The ordinary type a written type names in the code's scope, or null: never a generic one, which a copy has of its own.</summary>
    private TypeSymbol? OrdinaryNamed(TypeRef reference, TypeSymbol scope)
    {
        if (reference.Args.Count != 0 || reference.UseArgs is { Count: > 0 }) return null;
        Type resolved = Resolve(reference, scope);
        return !Unresolved(resolved) && resolved.Symbol is TypeSymbol type && Ordinary(type) ? type : null;
    }

    /// <summary>The dotted name an expression spells, `A.B.C`, when it is nothing but names: a type, if anything.</summary>
    private static string? NamesPath(Expr expression) => expression switch
    {
        NameExpr { TypeArgs.Count: 0 } name => name.Name,
        MemberExpr { TypeArgs.Count: 0, NullConditional: false } member when NamesPath(member.Target) is string left => left + "." + member.Name,
        _ => null,
    };

    /// <summary>
    /// A bare name in generic code: N of each ordinary type around the code --
    /// the type it is written in, those that hold it -- and of their bases.
    /// The generic type's own members are the copy's own.
    /// </summary>
    private void NamedAround(string name, TypeSymbol scope)
    {
        for (TypeSymbol? at = scope; at is not null; at = at.Decl?.Outer is string outer && _r.Types.TryGetValue(outer, out TypeSymbol? holder) ? holder : null)
        {
            for (TypeSymbol? up = at; up is not null; up = up.Base)
                if (Ordinary(up)) NamedOf(up, name);
            if (at.Decl is not { } d || d.Outer is null || d.Outer == d.Namespace) break;
        }
    }

    /// <summary>The members of `type` called `name`: its methods of the name, its static field or the backing field of its property, its accessors.</summary>
    private void NamedOf(TypeSymbol type, string name)
    {
        foreach (MethodSymbol m in type.FindMethods(name))
            if (m.TypeParams.Count == 0 && !m.IsCtor) _r.GenericNamedMethods.Add(m);
        foreach (MethodSymbol m in type.FindMethods(NameTable.Accessor("get_", name))) _r.GenericNamedMethods.Add(m);
        foreach (MethodSymbol m in type.FindMethods(NameTable.Accessor("set_", name))) _r.GenericNamedMethods.Add(m);
        if ((type.FindField(name) ?? type.FindBackingField(name)) is { Static: true } field) _r.GenericNamedFields.Add(field);
    }
}
