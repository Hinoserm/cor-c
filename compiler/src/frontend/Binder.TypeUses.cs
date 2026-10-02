#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    private Type ContextualMemberResult(Type receiver, MethodSymbol member)
        => ContextualResult(receiver, member.Owner, member.Name, member.Returns, member);

    private Type ContextualFieldResult(Type receiver, FieldSymbol field)
        => ContextualResult(receiver, field.Owner, field.Name, field.Type, null);

    private Type ContextualParameterType(Type receiver, MethodSymbol member, int index)
        => ContextualResult(receiver, member.Owner, member.Name, member.Params[index].Type, member, index);

    private Type ContextualResult(Type receiver, TypeSymbol owner, string name, Type fallback,
        MethodSymbol? member, int parameter = -1)
    {
        if (owner.Decl?.Template is not string templateName)
            return fallback;
        if (ReferenceEquals(receiver.Symbol, owner) && !HasContextualUse(receiver)) return fallback;

        TypeSymbol? savedScope = _scope;
        MemberDecl? savedMember = _member;
        try
        {
            Type? declaringUse = DeclaringTypeUse(receiver, owner, 0);
            if (declaringUse is null) return fallback;
            IReadOnlyList<Type> arguments = declaringUse.UseArgs ?? declaringUse.Args;
            if (arguments.Count == 0 || !HasContextualUse(declaringUse)) return fallback;
            _scope = owner;
            _member = null;
            if (!FindType(Arity(templateName, arguments.Count), out TypeSymbol? template)
                || template is null || template.TypeParams.Count != arguments.Count)
                return fallback;
            Dictionary<string, Type> bindings = new(StringComparer.Ordinal);
            for (int i = 0; i < arguments.Count; i++)
                bindings[template.TypeParams[i]] = arguments[i];
            if (member is null)
            {
                FieldSymbol? original = template.FindField(name);
                return original is not null && ReferenceEquals(original.Owner, template)
                    ? Close(original.Type, bindings) : fallback;
            }
            // A constructor is named for its type, and the specialisation's
            // name is not the template's.
            List<MethodSymbol> candidates = (member is { IsCtor: true }
                    ? template.Methods.Where(m => m.IsCtor) : template.FindMethods(name))
                .Where(m => ReferenceEquals(m.Owner, template)
                    && ContextualSignatureMatches(m, member, bindings)).ToList();
            // An overloaded signature needs its exact declaration origin;
            // never infer that origin solely from matching machine shapes.
            if (candidates.Count == 0) return fallback;
            if (parameter >= 0)
                return candidates.Count == 1
                    ? Close(candidates[0].Params[parameter].Type, bindings) : fallback;
            if (candidates.Count > 1)
            {
                // Nongeneric overloads returning the same class parameter
                // have exactly the same contextual result, irrespective of
                // which parameter list selected the overload.
                Type first = candidates[0].Returns;
                if (first.ParamName is null || !candidates.All(m =>
                    m.TypeParams.Count == 0 && m.Returns.Equals(first)))
                    return fallback;
            }
            return Close(candidates[0].Returns, bindings);
        }
        finally
        {
            _scope = savedScope;
            _member = savedMember;
        }
    }

    // Follow the written base applications, substituting at every edge.
    // Derived<A,B> : Base<B> must not treat A as Base's first argument.
    // Symbols remain shared; only the compile-time use annotations travel.
    private Type? DeclaringTypeUse(Type receiver, TypeSymbol owner, int depth)
    {
        if (ReferenceEquals(receiver.Symbol, owner)) return receiver;
        if (depth >= 64 || receiver.Symbol is not TypeSymbol actual || actual.Decl is null)
            return null;
        TypeSymbol source = actual;
        Dictionary<string, Type>? bindings = null;
        IReadOnlyList<Type> arguments = receiver.UseArgs ?? receiver.Args;
        _scope = actual;
        _member = null;
        if (actual.Decl.Template is string templateName && arguments.Count > 0)
        {
            if (!FindType(Arity(templateName, arguments.Count), out TypeSymbol? template)
                || template?.Decl is null || template.TypeParams.Count != arguments.Count)
                return null;
            source = template;
            bindings = new(StringComparer.Ordinal);
            for (int i = 0; i < arguments.Count; i++)
                bindings[source.TypeParams[i]] = arguments[i];
        }
        foreach (TypeRef writtenBase in source.Decl!.Bases)
        {
            _scope = source;
            Type next = Close(Resolve(writtenBase, source), bindings);
            if (next.Symbol is null || ReferenceEquals(next.Symbol, actual)) continue;
            Type? found = DeclaringTypeUse(next, owner, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// The arguments a specialisation is NAMED by: a reference's `?` is the
    /// use's, so `List&lt;(int, Box?)&gt;` is the one List$ValueTuple_int_Box
    /// the Monomorphiser made (a value's `int?` is Nullable and stays).
    /// </summary>
    private static List<TypeRef> SpecialisationKeys(List<TypeRef> spelt, IReadOnlyList<Type> types)
    {
        List<TypeRef>? keys = null;
        for (int i = 0; i < spelt.Count; i++)
        {
            TypeRef key = SpecialisationKey(spelt[i], types[i]);
            if (!ReferenceEquals(key, spelt[i])) (keys ??= new List<TypeRef>(spelt))[i] = key;
        }
        return keys ?? spelt;
    }

    private static TypeRef SpecialisationKey(TypeRef spelt, Type type)
    {
        Type bare = type;
        while (bare.IsArray && bare.Element is Type of) bare = of;
        bool reference = spelt.Nullable && (spelt.ArrayRank > 0 || spelt.PointerDepth == 0 && type.IsReference);
        bool element = spelt.ElementNullable && bare.IsReference;
        List<TypeRef> args = spelt.Args;
        if (spelt.Name == TypeRef.Tuple && bare.Symbol is TypeSymbol tuple)
        {
            List<Type> items = tuple.Fields.Where(f => !f.Static).Select(f => f.Type).ToList();
            if (items.Count == spelt.Args.Count) args = SpecialisationKeys(spelt.Args, items);
        }
        else if (spelt.Args.Count > 0 && bare.Args.Count == spelt.Args.Count)
            args = SpecialisationKeys(spelt.Args, bare.Args);
        if (!reference && !element && ReferenceEquals(args, spelt.Args)) return spelt;
        return new TypeRef { Name = spelt.Name, Arguments = args, UseArgs = spelt.UseArgs, ArrayRank = spelt.ArrayRank,
            Nullable = spelt.Nullable && !reference, ElementNullable = spelt.ElementNullable && !element,
            InnerNullable = spelt.InnerNullable, PointerDepth = spelt.PointerDepth, TupleNames = spelt.TupleNames,
            Line = spelt.Line, Col = spelt.Col };
    }

    private bool ContextualSignatureMatches(MethodSymbol source, MethodSymbol member,
        Dictionary<string, Type> bindings)
    {
        if (source.Static != member.Static || source.TypeParams.Count != member.TypeParams.Count
            || source.Params.Count != member.Params.Count) return false;
        for (int i = 0; i < source.Params.Count; i++)
            if (source.Params[i].ByRef != member.Params[i].ByRef
                || !SameButReferenceMarks(Close(source.Params[i].Type, bindings), member.Params[i].Type))
                return false;
        return true;
    }

    /// <summary>
    /// One type, whatever `?` its references carry -- a template's member
    /// closed over a use's annotated arguments against the specialisation's,
    /// which has none (Monomorphiser: one specialisation per type).
    /// </summary>
    private static bool SameButReferenceMarks(Type a, Type b)
    {
        if (a.Nullable != b.Nullable && !(a.IsReference && b.IsReference)) return false;
        if (a.Prim != b.Prim || !ReferenceEquals(a.Symbol, b.Symbol) || a.ArrayRank != b.ArrayRank
            || a.PointerDepth != b.PointerDepth || a.ParamName != b.ParamName) return false;
        if ((a.Element is null) != (b.Element is null)) return false;
        if (a.Element is not null && !SameButReferenceMarks(a.Element, b.Element!)) return false;
        if (a.Args.Count != b.Args.Count) return false;
        for (int i = 0; i < a.Args.Count; i++)
            if (!SameButReferenceMarks(a.Args[i], b.Args[i])) return false;
        return true;
    }

    /// <summary>
    /// WHETHER A USE SAYS MORE THAN ITS SPECIALISATION: tuple element names,
    /// or a reference argument's `?` -- which names no specialisation of its
    /// own (one List serves List&lt;JsonNode&gt; and List&lt;JsonNode?&gt;), so the
    /// member types the checker sees at this use are worked out from it.
    /// </summary>
    private static bool HasContextualUse(Type type) => HasTupleUse(type) || HasReferenceMark(type, 0);

    private static bool HasReferenceMark(Type type, int depth)
    {
        if (depth > 8) return false;
        if (depth > 0 && type.Nullable && type.IsReference) return true;
        if (type.Element is Type element && HasReferenceMark(element, depth + 1)) return true;
        if (type.UseArgs is not null)
            foreach (Type argument in type.UseArgs)
                if (HasReferenceMark(argument, depth + 1)) return true;
        foreach (Type argument in type.Args)
            if (HasReferenceMark(argument, depth + 1)) return true;
        return false;
    }

    private static bool HasTupleUse(Type type)
    {
        if (type.Names is not null || type.Symbol?.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal) == true)
            return true;
        if (type.Element is Type element && HasTupleUse(element)) return true;
        if (type.UseArgs is not null)
            foreach (Type argument in type.UseArgs)
                if (HasTupleUse(argument)) return true;
        foreach (Type argument in type.Args)
            if (HasTupleUse(argument)) return true;
        return false;
    }
}
