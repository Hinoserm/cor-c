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
        if (ReferenceEquals(receiver.Symbol, owner) && !HasTupleUse(receiver)) return fallback;

        TypeSymbol? savedScope = _scope;
        MemberDecl? savedMember = _member;
        try
        {
            Type? declaringUse = DeclaringTypeUse(receiver, owner, 0);
            if (declaringUse is null) return fallback;
            IReadOnlyList<Type> arguments = declaringUse.UseArgs ?? declaringUse.Args;
            if (arguments.Count == 0 || !HasTupleUse(declaringUse)) return fallback;
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
            List<MethodSymbol> candidates = template.FindMethods(name)
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

    private bool ContextualSignatureMatches(MethodSymbol source, MethodSymbol member,
        Dictionary<string, Type> bindings)
    {
        if (source.Static != member.Static || source.TypeParams.Count != member.TypeParams.Count
            || source.Params.Count != member.Params.Count) return false;
        for (int i = 0; i < source.Params.Count; i++)
            if (source.Params[i].ByRef != member.Params[i].ByRef
                || !Close(source.Params[i].Type, bindings).Equals(member.Params[i].Type))
                return false;
        return true;
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
