using System.Text;
using System.IO;

namespace Corsac.Lang;

/// <summary>
/// WHAT A DYNAMIC RECEIVER FINDS ON A PROGRAM'S OWN CLASSES, written out as
/// source before anything is bound, as ComDeclarations writes a class's
/// IDispatch: each class and struct of a program that writes `dynamic` is
/// made System.Dynamic.IDynamicMembers, and given its public and internal
/// instance members by name --
///
///   __DynGet(name, out result)              a field or property read
///   __DynSet(name, value)                   a field or property written
///   __DynCall(name, args, out result)       a method called
///   __DynGetIndex(keys, out result), __DynSetIndex(keys, value)   the indexer
///   __DynOperator(op, left, right, out result)   its operators (op: .NET's ExpressionType)
///   __DynConvert(type, explicitly, out result)   its conversions from it
///
/// -- each answering 0 done, 1 no such member, 2 none that takes these
/// arguments, 3 a method where a value was asked for, 4 a member that cannot
/// be written. The library's binder (System.DynamicRuntime) asks them.
///
/// THE OVERLOAD, as C#'s run-time binder chooses it: by the arguments'
/// run-time types, every argument an object here. First an overload every
/// argument is exactly the parameter type of (DynamicValues.Exact), then
/// one each converts to implicitly (DynamicValues.Fits: a reference to
/// its base or interface, null to a reference or a Nullable, a number to a
/// wider one), the more derived class's first and, within one class, the
/// parameters that say more first. Optional parameters may be left out; a
/// `params` array is taken whole or in its expanded form.
///
/// What is left out says so here: a generic method, a method with a `ref`
/// or `out` parameter, an explicit interface implementation, a static
/// member (as C#'s binder leaves statics out of an instance's members), an
/// event.
/// </summary>
public static class DynamicDeclarations
{
    const string Face = "System.Dynamic.IDynamicMembers";
    const string Rt = "System.DynamicValues";

    sealed class Scope
    {
        public readonly Dictionary<string, List<TypeDecl>> ByName = new(StringComparer.Ordinal);

        public Scope(CompilationUnit unit)
        {
            foreach (TypeDecl t in unit.Types)
            {
                if (!ByName.TryGetValue(t.Name, out List<TypeDecl>? list)) ByName[t.Name] = list = new();
                list.Add(t);
            }
        }

        /// <summary>The declaration a written, non-generic type names, nearest `at` first.</summary>
        public TypeDecl? Resolve(TypeRef r, TypeDecl at)
        {
            if (r.Args.Count > 0 || r.IsFunctionPointer || r.ArrayRank > 0 || r.PointerDepth > 0) return null;
            string name = r.Name;
            int dot = name.LastIndexOf('.');
            string simple = dot < 0 ? name : name[(dot + 1)..];
            if (!ByName.TryGetValue(simple, out List<TypeDecl>? all)) return null;
            List<TypeDecl> found = all.Where(t => t.TypeParams.Count == 0).ToList();
            if (found.Count <= 1) return found.FirstOrDefault();
            TypeDecl? sibling = found.FirstOrDefault(t => t.Outer == at.Outer);
            if (sibling is not null) return sibling;
            return found.FirstOrDefault(t => t.Namespace == at.Namespace) ?? found[0];
        }
    }

    /// <summary>The classes and structs given their members; before binding, after ComDeclarations (Frontend).</summary>
    public static void Expand(CompilationUnit unit, IReadOnlyCollection<string>? symbols, List<CompileError> errors)
    {
        Scope scope = new(unit);
        foreach (TypeDecl d in unit.Types.Where(t => unit.UsesDynamic && LateDelegate(t)))
        {
            MethodDecl? invoke = d.Members.OfType<MethodDecl>().FirstOrDefault(m => m.Name == "Invoke");
            if (invoke is null || d.Members.Any(m => m is MethodDecl { Name: "__LateInvoke" })) continue;
            CompilationUnit late;
            try { late = Parser.ParseText(DelegateMembers(scope, d, invoke), "<dynamic:" + d.Name + ">", symbols); }
            catch (CompileError e) { errors.Add(e); continue; }
            d.Bases.Add(new TypeRef { Name = "System.Dynamic.ILateInvocable", Line = d.Line, Col = d.Col });
            foreach (MemberDecl m in late.Types[0].Members)
            {
                m.Scope = d.Scope;
                m.Namespace = d.Namespace;
                m.File = d.File;
                d.Members.Add(m);
            }
        }
        List<TypeDecl> made = unit.Types.Where(t => (unit.UsesDynamic && Eligible(t)) || (Marked(t) && Shaped(t) && (unit.UsesDynamic || t.TypeParams.Count == 0))).ToList();
        HashSet<TypeDecl> given = new(made);
        foreach (TypeDecl t in made)
        {
            List<TypeDecl> chain = new() { t };
            chain.AddRange(Ancestors(scope, t));
            bool inherits = t.Kind == TypeKind.Class
                && (chain.Skip(1).Any(a => given.Contains(a) || a.Members.Any(m => m is MethodDecl { Name: "__DynGet" }))
                    || t.Bases.Any(b => GenericMarkedBase(scope, b)));
            string modifier = t.Kind == TypeKind.Struct ? "" : inherits ? "override" : t.Mods.HasFlag(Mods.Sealed) ? "" : "virtual";
            if (!inherits) t.Bases.Add(new TypeRef { Name = Face, Line = t.Line, Col = t.Col });
            string text = Members(scope, t, chain, modifier, inherits);
            CompilationUnit parsed;
            try { parsed = Parser.ParseText(text, "<dynamic:" + t.Name + ">", symbols); }
            catch (CompileError e) { errors.Add(e); continue; }
            foreach (MemberDecl m in parsed.Types[0].Members)
            {
                m.Scope = t.Scope;
                m.Namespace = t.Namespace;
                m.File = t.File;
                if (t.Elsewhere) m.OwnedImplementation = false;
                t.Members.Add(m);
            }
        }
        if (unit.UsesDynamic) LateUnit(unit, scope, symbols, errors);
    }

    static bool Eligible(TypeDecl t) => !t.FromLibrary && Shaped(t);

    static bool Shaped(TypeDecl t)
        => t.Kind is TypeKind.Class or TypeKind.Struct && !t.SignatureOnly && !t.Elsewhere
        && !t.IsDelegate && !t.LocalOnly && !t.Mods.HasFlag(Mods.Static)
        && !t.Name.StartsWith("__", StringComparison.Ordinal) && !t.Name.Contains('<') && !t.Name.Contains('$');

    /// <summary>
    /// A class of the library's that a dynamic receiver finds the members of
    /// whatever program uses it: `[LateBound]` (the collections and
    /// StringBuilder), given them where the library is compiled -- a generic
    /// one only in a program that uses dynamic, which specializes its own.
    /// </summary>
    static bool Marked(TypeDecl t) => t.AttributeParts.Any(a => a.Target.Length == 0 && a.Is("LateBound"));

    /// <summary>Whether this unit has anything to write: it uses `dynamic`, or holds a [LateBound] class.</summary>
    public static bool Wanted(CompilationUnit unit) => unit.UsesDynamic || unit.Types.Any(t => Marked(t) && Shaped(t) && t.TypeParams.Count == 0);

    /// <summary>
    /// A delegate type given its __LateInvoke here: one of a program's own
    /// that uses dynamic, not a local function's. Never the library's: a
    /// base and a method added to a delegate type the declaration index and
    /// the other units see without them renumbered every interface slot
    /// above it. The library's delegate types, Func and Action among them,
    /// are invoked through their adapters (LateUnit).
    /// </summary>
    static bool LateDelegate(TypeDecl d)
        => d.IsDelegate && !d.FromLibrary && !d.SignatureOnly && !d.Elsewhere && !d.LocalOnly
        && !d.Name.Contains('$') && !d.Name.StartsWith("__", StringComparison.Ordinal);

    /// <summary>
    /// The library's Func and Action: interfaces with one Invoke, which is
    /// what a delegate type is here (Core.cor), given __LateInvoke only in a
    /// program that uses dynamic, so that no other program's closures pay for it.
    /// </summary>
    static bool FunctionShape(TypeDecl d)
        => d.Kind == TypeKind.Interface && d.FromLibrary && d.Name is "Func" or "Action" or "Predicate" or "Comparison"
        && d.Members.Count == 1 && d.Members[0] is MethodDecl { Name: "Invoke" };

    /// <summary>A generic base that has the members already: a [LateBound] template (List&lt;int&gt;).</summary>
    static bool GenericMarkedBase(Scope scope, TypeRef b)
    {
        if (b.Args.Count == 0) return false;
        int dot = b.Name.LastIndexOf('.');
        string simple = dot < 0 ? b.Name : b.Name[(dot + 1)..];
        return scope.ByName.TryGetValue(simple, out List<TypeDecl>? all) && all.Any(d => d.TypeParams.Count == b.Args.Count && Marked(d));
    }

    static IEnumerable<TypeDecl> Ancestors(Scope scope, TypeDecl c)
    {
        HashSet<TypeDecl> seen = new() { c };
        TypeDecl at = c;
        while (true)
        {
            TypeDecl? up = at.Bases.Select(b => scope.Resolve(b, at)).FirstOrDefault(d => d is { Kind: TypeKind.Class });
            if (up is null || !seen.Add(up)) yield break;
            yield return up;
            at = up;
        }
    }

    static bool Visible(MemberDecl m)
        => (m.Mods.HasFlag(Mods.Public) || m.Mods.HasFlag(Mods.Internal)) && !m.Mods.HasFlag(Mods.Static)
        && m.ExplicitInterface is null && !m.Name.StartsWith("__", StringComparison.Ordinal) && !m.Name.Contains('<');

    // ---- the members ----------------------------------------------------------------------

    sealed record Storage(string Name, TypeRef Type, bool Readable, bool Writable);

    static string Members(Scope scope, TypeDecl t, List<TypeDecl> chain, string modifier, bool inherits, string me = "this", Func<TypeRef, bool>? spellable = null)
    {
        spellable ??= _ => true;
        // Fields and properties by name, the most derived first; methods by
        // name with every overload, the most derived first; the indexers;
        // the operators and the conversions declared anywhere on the chain.
        List<Storage> storage = new();
        Dictionary<string, List<MethodDecl>> methods = new(StringComparer.Ordinal);
        List<string> methodOrder = new();
        List<PropertyDecl> indexers = new();
        List<MethodDecl> operators = new();
        List<(string Name, TypeRef Type)> events = new();
        HashSet<string> stored = new(StringComparer.Ordinal);
        foreach (TypeDecl at in chain)
        {
            // An interface's members are public without saying so: a delegate's Invoke.
            bool Visible(MemberDecl m) => DynamicDeclarations.Visible(m)
                || at.Kind == TypeKind.Interface && !m.Mods.HasFlag(Mods.Private) && !m.Mods.HasFlag(Mods.Static) && m.ExplicitInterface is null && !m.Name.StartsWith("__", StringComparison.Ordinal);
            foreach (MemberDecl m in at.Members)
            {
                switch (m)
                {
                    case FieldDecl e when Visible(e) && e.IsEvent && spellable(e.Type):
                        foreach (FieldDecl one in new[] { e }.Concat(e.More))
                        {
                            if (stored.Add(one.Name)) events.Add((one.Name, e.Type));
                        }
                        break;
                    case FieldDecl f when Visible(f) && !f.IsEvent && !f.Mods.HasFlag(Mods.Const) && spellable(f.Type):
                        foreach (FieldDecl one in new[] { f }.Concat(f.More))
                        {
                            if (stored.Add(one.Name)) storage.Add(new Storage(one.Name, f.Type, true, !f.Mods.HasFlag(Mods.Readonly)));
                        }
                        break;
                    case PropertyDecl p when Visible(p) && p.Params.Count == 0 && spellable(p.Type):
                        if (stored.Add(p.Name)) storage.Add(new Storage(p.Name, p.Type, p.Auto || p.Getter is not null, p.HasSetter));
                        break;
                    case PropertyDecl ix when (ix.Mods.HasFlag(Mods.Public) || ix.Mods.HasFlag(Mods.Internal)) && !ix.Mods.HasFlag(Mods.Static)
                                           && ix.ExplicitInterface is null && ix.Params.Count > 0
                                           && ix.Params.All(prm => !prm.IsRef && !prm.IsOut && !prm.IsParams && spellable(prm.Type)) && spellable(ix.Type):
                        indexers.Add(ix);
                        break;
                    case MethodDecl op when op.Mods.HasFlag(Mods.Static) && op.Mods.HasFlag(Mods.Public) && op.Name.StartsWith("op_", StringComparison.Ordinal)
                                         && op.Params.Count is 1 or 2 && op.TypeParams.Count == 0 && me == "this":
                        operators.Add(op);
                        break;
                    case MethodDecl md when Visible(md) && !md.IsCtor && md.TypeParams.Count == 0 && md.Name != at.Name
                                         && !md.Name.StartsWith("op_", StringComparison.Ordinal)
                                         && !md.Name.StartsWith("get_", StringComparison.Ordinal) && !md.Name.StartsWith("set_", StringComparison.Ordinal)
                                         && !md.Name.StartsWith("add_", StringComparison.Ordinal) && !md.Name.StartsWith("remove_", StringComparison.Ordinal)
                                         && md.Params.All(prm => spellable(prm.Type)):
                        if (!methods.TryGetValue(md.Name, out List<MethodDecl>? overloads)) { methods[md.Name] = overloads = new(); methodOrder.Add(md.Name); }
                        // An override is the method it overrides: called
                        // through `this`, it dispatches the same.
                        if (!overloads.Any(o => SameParameters(o, md))) overloads.Add(md);
                        break;
                }
            }
        }

        StringBuilder s = new();
        string Fallback(string call, string none) => inherits ? "        return base." + call + ";\n" : "        return " + none + ";\n";
        s.Append("class __Members\n{\n");

        // ---- a value read
        s.Append("    public ").Append(modifier).Append(" int __DynGet(string name, out object? result)\n    {\n");
        s.Append("        result = null;\n        switch (name)\n        {\n");
        foreach (Storage one in storage.Where(x => x.Readable))
            s.Append("            case \"").Append(one.Name).Append("\": result = ").Append(me).Append('.').Append(one.Name).Append("; return 0;\n");
        foreach (string name in methodOrder.Where(n => !stored.Contains(n)))
            s.Append("            case \"").Append(name).Append("\": return 3;\n");
        s.Append("        }\n").Append(Fallback("__DynGet(name, out result)", "1")).Append("    }\n");

        // ---- a value written
        s.Append("    public ").Append(modifier).Append(" int __DynSet(string name, object? value)\n    {\n");
        s.Append("        switch (name)\n        {\n");
        foreach (Storage one in storage)
        {
            s.Append("            case \"").Append(one.Name).Append("\":\n");
            if (!one.Writable) { s.Append("                return 4;\n"); continue; }
            s.Append("                if (!(").Append(Fits(scope, t, one.Type, "value")).Append(")) return 2;\n");
            s.Append("                ").Append(me).Append('.').Append(one.Name).Append(" = ").Append(Converted(scope, t, one.Type, "value")).Append(";\n");
            s.Append("                return 0;\n");
        }
        s.Append("        }\n").Append(Fallback("__DynSet(name, value)", "1")).Append("    }\n");

        // ---- a method called
        s.Append("    public ").Append(modifier).Append(" int __DynCall(string name, System.Dynamic.LateCall call, out object? result)\n    {\n");
        s.Append("        result = null;\n        switch (name)\n        {\n");
        foreach (string name in methodOrder)
        {
            List<MethodDecl> overloads = methods[name];
            overloads = overloads.OrderByDescending(o => o.Params.Sum(prm => Weight(scope, t, prm.Type))).ToList();
            s.Append("            case \"").Append(name).Append("\":\n            {\n");
            foreach (bool exact in new[] { true, false })
            {
                foreach (MethodDecl md in overloads) CallCases(scope, t, s, md, exact, callee: me + "." + md.Name);
            }
            s.Append("                return 2;\n            }\n");
        }
        s.Append("        }\n").Append(Fallback("__DynCall(name, call, out result)", "1")).Append("    }\n");

        // ---- the indexer
        s.Append("    public ").Append(modifier).Append(" int __DynGetIndex(System.Dynamic.LateCall keys, out object? result)\n    {\n");
        s.Append("        result = null;\n        object?[] __keys = keys.Args;\n");
        foreach (bool exact in new[] { true, false })
        {
            foreach (PropertyDecl ix in indexers.Where(x => x.Auto || x.Getter is not null))
            {
                s.Append("        if (__keys.Length == ").Append(ix.Params.Count);
                for (int k = 0; k < ix.Params.Count; k++) s.Append(" && ").Append(Test(scope, t, ix.Params[k].Type, "__keys[" + k + "]", exact));
                s.Append(") { result = ").Append(me).Append('[').Append(string.Join(", ", Enumerable.Range(0, ix.Params.Count).Select(k => Converted(scope, t, ix.Params[k].Type, "__keys[" + k + "]")))).Append("]; return 0; }\n");
            }
        }
        s.Append(indexers.Count > 0 ? "        return 2;\n" : inherits ? "        return base.__DynGetIndex(keys, out result);\n" : "        return 1;\n");
        s.Append("    }\n");
        s.Append("    public ").Append(modifier).Append(" int __DynSetIndex(System.Dynamic.LateCall keys, object? value)\n    {\n");
        s.Append("        object?[] __keys = keys.Args;\n");
        foreach (bool exact in new[] { true, false })
        {
            foreach (PropertyDecl ix in indexers.Where(x => x.HasSetter))
            {
                s.Append("        if (__keys.Length == ").Append(ix.Params.Count);
                for (int k = 0; k < ix.Params.Count; k++) s.Append(" && ").Append(Test(scope, t, ix.Params[k].Type, "__keys[" + k + "]", exact));
                s.Append(" && ").Append(Fits(scope, t, ix.Type, "value"));
                s.Append(") { ").Append(me).Append('[').Append(string.Join(", ", Enumerable.Range(0, ix.Params.Count).Select(k => Converted(scope, t, ix.Params[k].Type, "__keys[" + k + "]"))))
                 .Append("] = ").Append(Converted(scope, t, ix.Type, "value")).Append("; return 0; }\n");
            }
        }
        s.Append(indexers.Count > 0 ? (indexers.Any(x => x.HasSetter) ? "        return 2;\n" : "        return 4;\n")
                 : inherits ? "        return base.__DynSetIndex(keys, value);\n" : "        return 1;\n");
        s.Append("    }\n");

        // ---- the operators
        s.Append("    public ").Append(modifier).Append(" int __DynOperator(int op, object? left, object? right, out object? result)\n    {\n");
        s.Append("        result = null;\n        switch (op)\n        {\n");
        foreach (IGrouping<int, MethodDecl> group in operators.Where(o => OperatorCode(o) >= 0).GroupBy(OperatorCode))
        {
            s.Append("            case ").Append(group.Key).Append(":\n");
            foreach (MethodDecl op in group) OperatorCase(scope, t, s, op);
            s.Append("                break;\n");
        }
        s.Append("        }\n").Append(Fallback("__DynOperator(op, left, right, out result)", "1")).Append("    }\n");

        // ---- the conversions from it
        s.Append("    public ").Append(modifier).Append(" int __DynConvert(System.Type target, bool explicitly, out object? result)\n    {\n");
        s.Append("        result = null;\n");
        foreach (MethodDecl conversion in operators.Where(o => o.Name is "op_Implicit" or "op_Explicit" && o.Params.Count == 1 && o.Returns is not null
                                                               && Named(o.Params[0].Type, t)))
        {
            string to = Plain(conversion.Returns!);
            s.Append("        if (target == typeof(").Append(to).Append(")) { ");
            if (conversion.Name == "op_Explicit") s.Append("if (!explicitly) return 2; ");
            s.Append("result = (").Append(to).Append(')').Append(me).Append("; return 0; }\n");
        }
        s.Append(Fallback("__DynConvert(target, explicitly, out result)", "1")).Append("    }\n");

        // ---- the events: a handler added or taken away
        s.Append("    public ").Append(modifier).Append(" int __DynEvent(string name, bool add, object? handler)\n    {\n");
        s.Append("        switch (name)\n        {\n");
        foreach ((string name, TypeRef type) in events)
        {
            string p = Plain(type);
            s.Append("            case \"").Append(name).Append("\":\n");
            s.Append("                if (!(handler == null || handler is ").Append(p).Append(")) return 2;\n");
            s.Append("                if (add) ").Append(me).Append('.').Append(name).Append(" += (").Append(p).Append(")handler!; else ").Append(me).Append('.').Append(name).Append(" -= (").Append(p).Append(")handler!;\n");
            s.Append("                return 0;\n");
        }
        s.Append("        }\n").Append(Fallback("__DynEvent(name, add, handler)", "1")).Append("    }\n");

        s.Append("}\n");
        return s.ToString();
    }

    /// <summary>
    /// A DELEGATE CALLED BY A DYNAMIC BINDER (System.DynamicRuntime.Invoke):
    /// every delegate type, the library's and the program's, is made
    /// System.Dynamic.ILateInvocable with a default __LateInvoke, which a
    /// closure takes up as any class implementing the delegate's interface
    /// would (Binder.ImplementDefaults). Its arguments go to Invoke's
    /// parameters as a method's do (CallCases); no match is C#'s binder's
    /// error.
    /// </summary>
    static string DelegateMembers(Scope scope, TypeDecl t, MethodDecl invoke)
    {
        StringBuilder s = new();
        s.Append("class __Members\n{\n");
        s.Append("    object? System.Dynamic.ILateInvocable.__LateInvoke(System.Dynamic.LateCall call)\n    {\n");
        s.Append("        object? result = null;\n");
        foreach (bool exact in new[] { true, false }) CallCases(scope, t, s, invoke, exact, "return result;");
        s.Append("        throw new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException(\"Delegate '").Append(t.Name).Append("' has some invalid arguments\");\n");
        s.Append("    }\n}\n");
        return s.ToString();
    }

    static bool SameParameters(MethodDecl a, MethodDecl b)
    {
        if (a.Params.Count != b.Params.Count) return false;
        for (int i = 0; i < a.Params.Count; i++)
        {
            if (Plain(a.Params[i].Type) != Plain(b.Params[i].Type)) return false;
        }
        return true;
    }

    static bool Named(TypeRef r, TypeDecl t)
    {
        string name = r.Name;
        int dot = name.LastIndexOf('.');
        return (dot < 0 ? name : name[(dot + 1)..]) == t.Name && r.ArrayRank == 0 && r.PointerDepth == 0;
    }

    /// <summary>
    /// One overload's call, its arguments matched to its parameters by the
    /// binder's own rules (LateCall.Map: by position and by name, `ref`,
    /// `out` and `in` as the parameters take them, the optional ones left
    /// out), each present one tested exactly or by conversion; the call made
    /// with every present argument named, one form for each set of optional
    /// parameters given, so a left-out one takes the method's own default;
    /// `ref` and `out` through locals of the parameter's type, written back
    /// to the caller's arguments after (LateCall.Back). Then, for a `params`
    /// method, its expanded form (LateCall.MapExpanded).
    /// </summary>
    static void CallCases(Scope scope, TypeDecl t, StringBuilder s, MethodDecl md, bool exact, string ret = "return 0;", string? callee = null, string? self = null)
    {
        callee ??= "this." + md.Name;
        string head = self is null ? "" : self;
        const string pad = "                ";
        int total = md.Params.Count;
        int required = total;
        while (required > 0 && md.Params[required - 1].Default is not null) required--;
        bool expands = total > 0 && md.Params[^1].IsParams && md.Params[^1].Type.ArrayRank > 0;
        bool returns = md.Returns is not null && md.Returns.Name != "void";
        static int Kind(Param p) => p.IsOut ? 2 : p.IsReadOnlyRef ? 3 : p.IsRef ? 1 : 0;
        string names = "new string[] { " + string.Join(", ", md.Params.Select(p => "\"" + p.Name + "\"")) + " }";
        string kinds = "new int[] { " + string.Join(", ", md.Params.Select(Kind)) + " }";

        // Each argument in a local of its own: the tests' null checks follow
        // a local, not an element of an element.
        s.Append(pad).Append("{\n");
        s.Append(pad).Append("    int[]? __m = call.Map(").Append(names).Append(", ").Append(kinds).Append(", ").Append(required).Append(");\n");
        s.Append(pad).Append("    if (__m != null)\n").Append(pad).Append("    {\n");
        for (int k = 0; k < total; k++)
            s.Append(pad).Append("    object? __a").Append(k).Append(" = __m[").Append(k).Append("] < 0 ? null : call.Args[__m[").Append(k).Append("]];\n");
        s.Append(pad).Append("    if (true");
        for (int k = 0; k < total; k++)
        {
            if (md.Params[k].IsOut) continue;
            s.Append(" && (__m[").Append(k).Append("] < 0 || ").Append(Test(scope, t, md.Params[k].Type, "__a" + k, exact)).Append(')');
        }
        s.Append(")\n").Append(pad).Append("    {\n");
        // The locals a ref or out argument goes through.
        for (int k = 0; k < total; k++)
        {
            Param prm = md.Params[k];
            if (!prm.IsRef && !prm.IsOut) continue;
            string type = Plain(prm.Type) + (prm.Type.Nullable ? "?" : "");
            s.Append(pad).Append("        ").Append(type).Append(" __r").Append(k);
            if (prm.IsRef) s.Append(" = ").Append(Converted(scope, t, prm.Type, "__a" + k));
            else s.Append(" = default(").Append(type).Append(')');
            s.Append(";\n");
        }
        // One call for each set of the optional parameters given.
        List<int> optional = Enumerable.Range(required, total - required).ToList();
        int forms = optional.Count <= 4 ? 1 << optional.Count : 1;
        for (int mask = forms - 1; mask >= 0; mask--)
        {
            List<string> conditions = new();
            List<string> passed = new();
            for (int k = 0; k < total; k++)
            {
                int bit = optional.IndexOf(k);
                bool given = bit < 0 || optional.Count > 4 || (mask & (1 << bit)) != 0;
                // Past four optional parameters, the one form that takes them all.
                if (bit >= 0) conditions.Add("__m[" + k + "] " + (given ? ">= 0" : "< 0"));
                if (!given) continue;
                Param prm = md.Params[k];
                string value = prm.IsRef ? "ref __r" + k : prm.IsOut ? "out __r" + k : Converted(scope, t, prm.Type, "__a" + k);
                passed.Add(prm.Name + ": " + value);
            }
            string invoke = callee + "(" + string.Join(", ", (self is null ? passed : passed.Prepend(head))) + ")";
            s.Append(pad).Append("        ");
            if (conditions.Count > 0) s.Append("if (").Append(string.Join(" && ", conditions)).Append(") ");
            s.Append("{ ").Append(returns ? "result = " + invoke + ";" : invoke + ";");
            for (int k = 0; k < total; k++)
                if (md.Params[k].IsRef || md.Params[k].IsOut) s.Append(" call.Back(__m, ").Append(k).Append(", __r").Append(k).Append(");");
            s.Append(' ').Append(ret).Append(" }\n");
        }
        s.Append(pad).Append("    }\n").Append(pad).Append("    }\n").Append(pad).Append("}\n");
        if (!expands || md.Params.Any(p => p.IsRef || p.IsOut)) return;

        // THE EXPANDED FORM: the fixed arguments as Map has them, each of the
        // rest an element of the array.
        TypeRef array = md.Params[^1].Type;
        TypeRef element = new()
        {
            Name = array.Name, Arguments = array.Args, ArrayRank = array.ArrayRank - 1, Nullable = array.ElementNullable,
            Line = array.Line, Col = array.Col,
        };
        int fixedCount = total - 1;
        // Every fixed parameter given: the expanded form is C#'s only when
        // they are (12.6.4.2 takes the fixed ones as written).
        int fixedRequired = fixedCount;
        string fixedNames = "new string[] { " + string.Join(", ", md.Params.Take(fixedCount).Select(p => "\"" + p.Name + "\"")) + " }";
        string fixedKinds = "new int[] { " + string.Join(", ", md.Params.Take(fixedCount).Select(Kind)) + " }";
        s.Append(pad).Append("{\n");
        s.Append(pad).Append("    int[]? __x = call.MapExpanded(").Append(fixedNames).Append(", ").Append(fixedKinds).Append(", ").Append(fixedRequired).Append(");\n");
        s.Append(pad).Append("    if (__x != null)\n").Append(pad).Append("    {\n");
        for (int k = 0; k < fixedCount; k++)
            s.Append(pad).Append("    object? __f").Append(k).Append(" = call.Args[__x[").Append(k).Append("]];\n");
        s.Append(pad).Append("    if (true");
        for (int k = 0; k < fixedCount; k++)
            s.Append(" && ").Append(Test(scope, t, md.Params[k].Type, "__f" + k, exact));
        s.Append(" && " + Rt + ".All(call.Rest(").Append(fixedCount).Append("), (object? __e) => ").Append(Test(scope, t, element, "__e", exact)).Append("))\n");
        s.Append(pad).Append("    {\n");
        s.Append(pad).Append("        object?[] __given = call.Rest(").Append(fixedCount).Append(");\n");
        s.Append(pad).Append("        ").Append(Plain(element)).Append(element.Nullable ? "?" : "").Append("[] __rest = new ").Append(Plain(element)).Append(element.Nullable ? "?" : "")
         .Append("[__given.Length];\n");
        s.Append(pad).Append("        for (int __k = 0; __k < __rest.Length; __k++) __rest[__k] = ").Append(Converted(scope, t, element, "__given[__k]")).Append(";\n");
        List<string> fixedPassed = Enumerable.Range(0, fixedCount).Select(k => Converted(scope, t, md.Params[k].Type, "__f" + k)).ToList();
        fixedPassed.Add("__rest");
        if (self is not null) fixedPassed.Insert(0, head);
        string expanded = callee + "(" + string.Join(", ", fixedPassed) + ")";
        s.Append(pad).Append("        ").Append(returns ? "result = " + expanded + "; " + ret : expanded + "; " + ret).Append('\n');
        s.Append(pad).Append("    }\n").Append(pad).Append("    }\n").Append(pad).Append("}\n");
    }

    // ---- the primitives' own members ------------------------------------------------------

    /// <summary>
    /// What a primitive answers as its members: here a string's are the
    /// library's String statics that take it first (`s.ToUpper()` is
    /// String.ToUpper(s)), a number's or a char's the same of Int32, Char and
    /// the rest. Written into LateBuiltIns.PrimitiveCall (Dynamic.cor), whose
    /// own body says no such member, in a program that uses dynamic. The
    /// statics .NET has on these types (String.Join, int.Parse) are left out,
    /// as C#'s binder leaves statics out of an instance's members.
    /// </summary>
    static readonly (string Keyword, string Holder)[] Primitives =
    {
        ("string", "String"), ("char", "Char"), ("bool", "Boolean"), ("byte", "Byte"), ("short", "Int16"),
        ("int", "Int32"), ("uint", "UInt32"), ("long", "Int64"), ("ulong", "UInt64"), ("float", "Single"), ("double", "Double"),
    };

    static readonly HashSet<string> StringStatics = new(StringComparer.Ordinal)
    {
        "Join", "Format", "Concat", "IsNullOrEmpty", "IsNullOrWhiteSpace", "Compare", "CompareOrdinal",
        "Copy", "Intern", "IsInterned", "Create", "Empty",
    };

    /// <summary>A number's or a char's instance members, as .NET has them; the rest of its statics are static there.</summary>
    static readonly HashSet<string> ValueInstance = new(StringComparer.Ordinal)
    {
        "CompareTo", "Equals", "GetHashCode", "ToString", "TryFormat", "GetTypeCode",
    };

    /// <summary>The String, Int32 and the rest's dispatch: one static class in the library's scope (PrimitiveCall).</summary>
    static string? PrimitiveText(CompilationUnit unit, Scope scope)
    {
        StringBuilder s = new();
        s.Append("static class __LatePrimitives\n{\n");
        s.Append("    public static int Call(object self, string name, System.Dynamic.LateCall call, out object? result)\n    {\n");
        s.Append("        result = null;\n");
        bool any = false;
        foreach ((string keyword, string holder) in Primitives)
        {
            TypeDecl? statics = unit.Types.FirstOrDefault(t => t.Name == holder && t.FromLibrary && t.Outer is null && t.TypeParams.Count == 0
                                                             && t.Mods.HasFlag(Mods.Static));
            if (statics is null) continue;
            Dictionary<string, List<MethodDecl>> methods = new(StringComparer.Ordinal);
            foreach (MethodDecl md in statics.Members.OfType<MethodDecl>())
            {
                if (!md.Mods.HasFlag(Mods.Public) || md.Body is null && !md.Mods.HasFlag(Mods.Extern) || md.TypeParams.Count > 0 || md.Params.Count == 0) continue;
                if (md.Name.StartsWith("__", StringComparison.Ordinal) || md.Name.Contains('<') || md.Name.StartsWith("op_", StringComparison.Ordinal)) continue;
                if (keyword == "string" ? StringStatics.Contains(md.Name) : !ValueInstance.Contains(md.Name)) continue;
                Param first = md.Params[0];
                if (first.IsRef || first.IsOut || first.IsParams || first.Type.ArrayRank > 0 || first.Type.Args.Count > 0
                    || first.Type.Name != keyword && first.Type.Name != holder && first.Type.Name != "System." + holder) continue;
                if (md.Params.Skip(1).Any(p => p.Type.Name.Contains("Span", StringComparison.Ordinal) || p.Type.PointerDepth > 0)) continue;
                MethodDecl view = new() { Name = md.Name, Returns = md.Returns, Line = md.Line, Col = md.Col };
                view.WritableParams.AddRange(md.Params.Skip(1));
                if (!methods.TryGetValue(md.Name, out List<MethodDecl>? all)) methods[md.Name] = all = new();
                all.Add(view);
            }
            if (methods.Count == 0) continue;
            any = true;
            // Each its own name: an `is` pattern's variable is the enclosing block's.
            string own = "__self_" + keyword;
            s.Append("        if (self is ").Append(keyword).Append(' ').Append(own).Append(")\n        {\n            switch (name)\n            {\n");
            foreach ((string name, List<MethodDecl> overloads) in methods)
            {
                s.Append("            case \"").Append(name).Append("\":\n            {\n");
                List<MethodDecl> ordered = overloads.OrderByDescending(o => o.Params.Sum(prm => Weight(scope, statics, prm.Type))).ToList();
                foreach (bool exact in new[] { true, false })
                    foreach (MethodDecl md in ordered) CallCases(scope, statics, s, md, exact, callee: Qualified(statics) + "." + name, self: own);
                s.Append("                return 2;\n            }\n");
            }
            s.Append("            }\n            return 1;\n        }\n");
        }
        s.Append("        return 1;\n    }\n}\n");
        return any ? s.ToString() : null;
    }

    /// <summary>A library type's name as any file can write it.</summary>
    static string Qualified(TypeDecl t) => t.Namespace.Length == 0 ? t.Name : t.Namespace + "." + t.Name;

    // ---- the library's classes, through adapters --------------------------------------------

    /// <summary>
    /// A LIBRARY CLASS a program that uses dynamic names is reached through
    /// an adapter written here, never by changing the class: its layout is
    /// the same in every unit that sees it, the library's own included, so
    /// nothing a program does with dynamic reaches another's objects.
    /// `__Late_List&lt;T&gt;` holds a List&lt;T&gt; and answers
    /// IDynamicMembers through it (Members, with the held object as the
    /// receiver); generic like the class, it is made for each List the
    /// program names. One provider per unit (__LateUnit, System.Dynamic's
    /// ILateMembers) finds the adapter for a target -- the more derived
    /// class first -- and a primitive's members (__LatePrimitives), and is
    /// registered when Main's type is first touched, or by a module
    /// initializer in a unit without Main.
    ///
    /// Which classes: those the program's own sources name, a generic one
    /// with the arguments written there (a name a generic of the program's
    /// own takes as a parameter is not a type, and is left out). Members
    /// whose types are a class's nested types are left out: the adapter
    /// cannot spell them.
    /// </summary>
    static void LateUnit(CompilationUnit unit, Scope scope, IReadOnlyCollection<string>? symbols, List<CompileError> errors)
    {
        List<TypeDecl> own = unit.Types.Where(t => !t.FromLibrary && !t.Elsewhere).ToList();
        HashSet<string> ownNames = new(own.Select(t => t.Name), StringComparer.Ordinal);
        HashSet<string> typeParams = new(StringComparer.Ordinal);
        foreach (TypeDecl t in own)
        {
            foreach (TypeParam tp in t.TypeParams) typeParams.Add(tp.Name);
            foreach (MethodDecl md in t.Members.OfType<MethodDecl>()) foreach (TypeParam tp in md.TypeParams) typeParams.Add(tp.Name);
        }
        HashSet<string> nested = new(unit.Types.Where(t => t.Outer is not null).Select(t => t.Name), StringComparer.Ordinal);
        HashSet<string> topLevel = new(unit.Types.Where(t => t.Outer is null).Select(t => t.Name), StringComparer.Ordinal);
        bool Spellable(TypeRef r) => (!nested.Contains(r.Name) || topLevel.Contains(r.Name)) && r.Args.All(Spellable) && !r.IsFunctionPointer;

        Dictionary<string, List<TypeDecl>> library = new(StringComparer.Ordinal);
        foreach (TypeDecl t in unit.Types)
        {
            if (!t.FromLibrary || t.Outer is not null || t.LocalOnly || t.SignatureOnly || t.Mods.HasFlag(Mods.Static)) continue;
            if (t.Name.Contains('$') || t.Name.Contains('<') || t.Name.StartsWith("__", StringComparison.Ordinal) || ownNames.Contains(t.Name)) continue;
            if (t.Kind != TypeKind.Class && !(t.Kind == TypeKind.Interface && (FunctionShape(t) || t.IsDelegate))) continue;
            if (!library.TryGetValue(t.Name, out List<TypeDecl>? list)) library[t.Name] = list = new();
            list.Add(t);
        }

        // The names written: Name or Name<args>, in the program's own files.
        HashSet<string> files = new(own.Select(t => t.SourcePath).OfType<string>(), StringComparer.Ordinal);
        List<(TypeDecl Template, string Args)> wanted = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string path in files)
        {
            string text;
            try { text = File.ReadAllText(path); } catch (IOException) { continue; }
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsLetter(text[i]) && text[i] != '_' || i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_' || text[i - 1] == '.' && !QualifiedBefore(text, i))) continue;
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                string name = text[i..j];
                if (!library.TryGetValue(name, out List<TypeDecl>? decls)) { i = j - 1; continue; }
                int k = j;
                while (k < text.Length && text[k] == ' ') k++;
                string args = "";
                if (k < text.Length && text[k] == '<')
                {
                    int depth = 0, end = k;
                    for (; end < text.Length; end++)
                    {
                        if (text[end] == '<') depth++;
                        else if (text[end] == '>' && --depth == 0) break;
                        else if (text[end] is ';' or '{' or '}' or '=') { end = -1; break; }
                    }
                    if (end > k && end < text.Length) args = text[(k + 1)..end].Trim();
                }
                i = j - 1;
                int arity = args.Length == 0 ? 0 : Arity(args);
                TypeDecl? template = decls.FirstOrDefault(d => d.TypeParams.Count == arity);
                if (template is null) continue;
                if (arity > 0 && Words(args).Any(w => typeParams.Contains(w))) continue;
                string key = Qualified(template) + "<" + args + ">";
                if (seen.Add(key)) wanted.Add((template, args));
            }
        }

        // The adapters, one for each class named, in the library's scope.
        Dictionary<TypeDecl, string> adapters = new();
        foreach (TypeDecl t in wanted.Select(w => w.Template).Distinct())
        {
            List<TypeDecl> chain = new() { t };
            chain.AddRange(Ancestors(scope, t).TakeWhile(a => a.TypeParams.Count == 0));
            string self = Qualified(t) + (t.TypeParams.Count == 0 ? "" : "<" + string.Join(", ", t.TypeParams.Select(tp => tp.Name)) + ">");
            string name = "__Late_" + t.Name;
            string generic = t.TypeParams.Count == 0 ? "" : "<" + string.Join(", ", t.TypeParams.Select(tp => tp.Name)) + ">";
            string body = Members(scope, t, chain, "", false, me: "__t", spellable: Spellable);
            body = body[(body.IndexOf('{') + 1)..];
            string text = "sealed class " + name + generic + " : " + Face + "\n{\n    readonly " + self + " __t;\n    public " + name + "(" + self + " t) { __t = t; }\n" + body;
            if (Adopt(unit, text, "<dynamic:" + t.Name + ">", t.Namespace, t.Scope, t.File, symbols, errors) is not null)
                adapters[t] = (t.Namespace.Length == 0 ? "" : t.Namespace + ".") + name;
        }

        TypeDecl? stringHolder = unit.Types.FirstOrDefault(t => t.Name == "String" && t.FromLibrary && t.Mods.HasFlag(Mods.Static) && t.Outer is null);
        string? primitives = stringHolder is null ? null : PrimitiveText(unit, scope);
        string? primitiveName = null;
        if (primitives is not null && Adopt(unit, primitives, "<dynamic:primitives>", stringHolder!.Namespace, stringHolder.Scope, stringHolder.File, symbols, errors) is not null)
            primitiveName = (stringHolder.Namespace.Length == 0 ? "" : stringHolder.Namespace + ".") + "__LatePrimitives";

        // The provider, in the scope of the program's Main (or its first type).
        TypeDecl? main = own.FirstOrDefault(t => t.Members.OfType<MethodDecl>().Any(m => m.Name == "Main" && m.Mods.HasFlag(Mods.Static)));
        TypeDecl? home = main ?? own.FirstOrDefault(t => t.Outer is null);
        if (home is null) return;
        StringBuilder s = new();
        s.Append("sealed class __LateUnit : System.Dynamic.ILateMembers\n{\n");
        if (main is null) s.Append("    [System.Runtime.CompilerServices.ModuleInitializer]\n    internal static void __Register() { System.DynamicRuntime.Register(new __LateUnit()); }\n");
        s.Append("    static System.Dynamic.IDynamicMembers? Of(object? target)\n    {\n");
        int n = 0;
        // The more derived first: a class before any it derives from.
        foreach ((TypeDecl t, string args) in wanted.Where(w => adapters.ContainsKey(w.Template))
                     .OrderByDescending(w => Ancestors(scope, w.Template).Count()))
        {
            string closed = Qualified(t) + (args.Length == 0 ? "" : "<" + args + ">");
            string adapter = adapters[t] + (args.Length == 0 ? "" : "<" + args + ">");
            s.Append("        if (target is ").Append(closed).Append(" __a").Append(n).Append(") return new ").Append(adapter).Append("(__a").Append(n).Append(");\n");
            n++;
        }
        s.Append("        return null;\n    }\n");
        s.Append("    public int Get(object target, string name, out object? result) { result = null; System.Dynamic.IDynamicMembers? m = Of(target); return m == null ? 1 : m.__DynGet(name, out result); }\n");
        s.Append("    public int Set(object target, string name, object? value) { System.Dynamic.IDynamicMembers? m = Of(target); return m == null ? 1 : m.__DynSet(name, value); }\n");
        s.Append("    public int Call(object target, string name, System.Dynamic.LateCall call, out object? result)\n    {\n        result = null;\n");
        s.Append("        System.Dynamic.IDynamicMembers? m = Of(target);\n        if (m != null) return m.__DynCall(name, call, out result);\n");
        s.Append(primitiveName is null ? "        return 1;\n" : "        return " + primitiveName + ".Call(target, name, call, out result);\n");
        s.Append("    }\n");
        s.Append("    public int GetIndex(object target, System.Dynamic.LateCall keys, out object? result) { result = null; System.Dynamic.IDynamicMembers? m = Of(target); return m == null ? 1 : m.__DynGetIndex(keys, out result); }\n");
        s.Append("    public int SetIndex(object target, System.Dynamic.LateCall keys, object? value) { System.Dynamic.IDynamicMembers? m = Of(target); return m == null ? 1 : m.__DynSetIndex(keys, value); }\n");
        s.Append("    public int Operator(int op, object? left, object? right, out object? result) { result = null; return 1; }\n");
        s.Append("    public int Convert(object value, System.Type target, bool explicitly, out object? result) { result = null; return 1; }\n");
        s.Append("    public int Event(object target, string name, bool add, object? handler) { System.Dynamic.IDynamicMembers? m = Of(target); return m == null ? 1 : m.__DynEvent(name, add, handler); }\n");
        s.Append("    public string? Static(object target, string name) { return null; }\n");
        s.Append("}\n");
        if (Adopt(unit, s.ToString(), "<dynamic:unit>", home.Namespace, home.Scope, home.File, symbols, errors) is null || main is null) return;

        // Registered as Main's type is first touched: before Main runs.
        CompilationUnit hook;
        try { hook = Parser.ParseText("class __Members\n{\n    static readonly bool __lateUnit = System.DynamicRuntime.Registered(new " + (home.Namespace.Length == 0 ? "" : home.Namespace + ".") + "__LateUnit());\n}\n", "<dynamic:register>", symbols); }
        catch (CompileError e) { errors.Add(e); return; }
        foreach (MemberDecl m in hook.Types[0].Members)
        {
            m.Scope = main.Scope;
            m.Namespace = main.Namespace;
            m.File = main.File;
            main.Members.Add(m);
        }
    }

    /// <summary>Whether a `.` before position i ends a namespace written before a library name (System.Text.StringBuilder).</summary>
    static bool QualifiedBefore(string text, int i)
    {
        int j = i - 2;
        while (j >= 0 && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '.')) j--;
        string before = text[(j + 1)..(i - 1)];
        return before.Length > 0 && char.IsUpper(before[0]) && before.Split('.').All(part => part.Length > 0 && char.IsUpper(part[0]));
    }

    /// <summary>How many type arguments a written list holds, at its own depth.</summary>
    static int Arity(string args)
    {
        int depth = 0, count = 1;
        foreach (char c in args)
        {
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) count++;
        }
        return count;
    }

    static IEnumerable<string> Words(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i]) && text[i] != '_') continue;
            int j = i;
            while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
            yield return text[i..j];
            i = j;
        }
    }

    /// <summary>A written type made one of the unit's, in the scope given -- the library's for an adapter, the program's for its provider.</summary>
    static TypeDecl? Adopt(CompilationUnit unit, string text, string file, string ns, FileScope? fileScope, string? at,
                           IReadOnlyCollection<string>? symbols, List<CompileError> errors)
    {
        CompilationUnit late;
        try { late = Parser.ParseText(text, file, symbols); }
        catch (CompileError e) { errors.Add(e); return null; }
        TypeDecl made = late.Types[0];
        made.Namespace = ns;
        made.Scope = fileScope;
        made.File = at ?? file;
        foreach (MemberDecl m in made.Members)
        {
            m.Scope = fileScope;
            m.Namespace = ns;
            m.File = made.File;
        }
        unit.Types.Add(made);
        return made;
    }

    // ---- operators ---------------------------------------------------------------------

    static int OperatorCode(MethodDecl op) => op.Params.Count == 2
        ? op.Name switch
        {
            "op_Addition" => 0, "op_Subtraction" => 42, "op_Multiply" => 26, "op_Division" => 12, "op_Modulus" => 25,
            "op_BitwiseAnd" => 2, "op_BitwiseOr" => 36, "op_ExclusiveOr" => 14, "op_LeftShift" => 19, "op_RightShift" => 41,
            "op_Equality" => 13, "op_Inequality" => 35, "op_LessThan" => 20, "op_GreaterThan" => 15,
            "op_LessThanOrEqual" => 21, "op_GreaterThanOrEqual" => 16,
            _ => -1,
        }
        : op.Name switch
        {
            "op_UnaryNegation" => 28, "op_UnaryPlus" => 29, "op_LogicalNot" => 34, "op_OnesComplement" => 82,
            "op_Increment" => 54, "op_Decrement" => 49, "op_True" => 83,
            _ => -1,
        };

    static string Symbol(string name) => name switch
    {
        "op_Addition" => "+", "op_Subtraction" => "-", "op_Multiply" => "*", "op_Division" => "/", "op_Modulus" => "%",
        "op_BitwiseAnd" => "&", "op_BitwiseOr" => "|", "op_ExclusiveOr" => "^", "op_LeftShift" => "<<", "op_RightShift" => ">>",
        "op_Equality" => "==", "op_Inequality" => "!=", "op_LessThan" => "<", "op_GreaterThan" => ">",
        "op_LessThanOrEqual" => "<=", "op_GreaterThanOrEqual" => ">=",
        "op_UnaryNegation" => "-", "op_UnaryPlus" => "+", "op_LogicalNot" => "!", "op_OnesComplement" => "~",
        _ => "",
    };

    static void OperatorCase(Scope scope, TypeDecl t, StringBuilder s, MethodDecl op)
    {
        const string pad = "                ";
        string a = Converted(scope, t, op.Params[0].Type, "left");
        if (op.Params.Count == 2)
        {
            string b = Converted(scope, t, op.Params[1].Type, "right");
            s.Append(pad).Append("if (").Append(Fits(scope, t, op.Params[0].Type, "left")).Append(" && ").Append(Fits(scope, t, op.Params[1].Type, "right"))
             .Append(") { result = ").Append(a).Append(' ').Append(Symbol(op.Name)).Append(' ').Append(b).Append("; return 0; }\n");
            return;
        }
        s.Append(pad).Append("if (").Append(Fits(scope, t, op.Params[0].Type, "left")).Append(") { ");
        switch (op.Name)
        {
            case "op_Increment":
            case "op_Decrement":
                s.Append(Plain(op.Params[0].Type)).Append(" __v = ").Append(a).Append("; __v").Append(op.Name == "op_Increment" ? "++" : "--").Append("; result = __v; return 0; }\n");
                break;
            case "op_True":
                s.Append("result = ").Append(a).Append(" ? true : false; return 0; }\n");
                break;
            default:
                s.Append("result = ").Append(Symbol(op.Name)).Append('(').Append(a).Append("); return 0; }\n");
                break;
        }
    }

    // ---- the tests and the conversions, a parameter type at a time ------------------------

    static readonly HashSet<string> Numbers = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "char", "float", "double", "nint", "nuint",
        "Boolean", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Char", "Single", "Double", "IntPtr", "UIntPtr",
        "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64",
        "System.UInt64", "System.Char", "System.Single", "System.Double", "System.IntPtr", "System.UIntPtr",
    };

    enum Shape { Number, Value, Reference, Anything, Open }

    static Shape ShapeOf(Scope scope, TypeDecl t, TypeRef r)
    {
        if (r.ArrayRank > 0 || r.PointerDepth > 0) return Shape.Reference;
        if (r.Name is "object" or "Object" or "System.Object" or "dynamic") return Shape.Anything;
        if (r.Args.Count == 0 && Numbers.Contains(r.Name)) return Shape.Number;
        if (r.Args.Count == 0 && (t.TypeParams.Any(p => p.Name == r.Name))) return Shape.Open;
        if (r.Name is "string" or "String" or "System.String") return Shape.Reference;
        TypeDecl? d = scope.Resolve(r, t);
        if (d is { Kind: TypeKind.Struct or TypeKind.Enum }) return Shape.Value;
        if (r.Name is "Nullable" or "System.Nullable") return Shape.Value;
        return Shape.Reference;
    }

    static string Plain(TypeRef r)
    {
        StringBuilder s = new(r.Name);
        if (r.Args.Count > 0) s.Append('<').Append(string.Join(", ", r.Args.Select(a => Plain(a) + (a.Nullable ? "?" : "")))).Append('>');
        for (int i = 0; i < r.PointerDepth; i++) s.Append('*');
        for (int i = 0; i < r.ArrayRank; i++) s.Append(i == 0 && r.ElementNullable ? "?[]" : "[]");
        return s.ToString();
    }

    /// <summary>
    /// How much a parameter type says, for the order overloads are tried
    /// in: a narrower number more than a wider one (an int before a long
    /// before a double, as C#'s better conversion has it), a value or a
    /// reference less, a type parameter or an object nothing.
    /// </summary>
    static int Weight(Scope scope, TypeDecl t, TypeRef r) => ShapeOf(scope, t, r) switch
    {
        Shape.Anything => 0,
        Shape.Open => 1,
        Shape.Number => (r.Name.StartsWith("System.", StringComparison.Ordinal) ? r.Name[7..] : r.Name) switch
        {
            "sbyte" or "byte" or "SByte" or "Byte" => 16,
            "short" or "ushort" or "char" or "Int16" or "UInt16" or "Char" => 15,
            "int" or "uint" or "Int32" or "UInt32" => 14,
            "long" or "ulong" or "Int64" or "UInt64" or "nint" or "nuint" or "IntPtr" or "UIntPtr" => 13,
            "float" or "Single" => 12,
            "double" or "Double" => 11,
            _ => 10,
        },
        _ => 10,
    };

    static string Test(Scope scope, TypeDecl t, TypeRef r, string v, bool exact) => exact ? Exact(scope, t, r, v) : Fits(scope, t, r, v);

    static string Exact(Scope scope, TypeDecl t, TypeRef r, string v)
    {
        string p = Plain(r);
        return ShapeOf(scope, t, r) switch
        {
            Shape.Number => Rt + ".Exact(" + v + ", typeof(" + p + "))",
            Shape.Anything => "false",
            Shape.Open => "(" + v + " is " + p + ")",
            _ => "(" + v + " != null && " + v + ".GetType() == typeof(" + p + "))",
        };
    }

    static string Fits(Scope scope, TypeDecl t, TypeRef r, string v)
    {
        string p = Plain(r);
        switch (ShapeOf(scope, t, r))
        {
            case Shape.Number:
                return r.Nullable ? "(" + v + " == null || " + Rt + ".Fits(" + v + ", typeof(" + p + ")))" : Rt + ".Fits(" + v + ", typeof(" + p + "))";
            case Shape.Value:
                return r.Nullable ? "(" + v + " == null || " + v + " is " + p + ")" : "(" + v + " is " + p + ")";
            case Shape.Anything:
                return "true";
            case Shape.Open:
                return "(" + v + " is " + p + " || " + Rt + ".Fits(" + v + ", typeof(" + p + ")))";
            default:
                return "(" + v + " == null || " + v + " is " + p + ")";
        }
    }

    static string Converted(Scope scope, TypeDecl t, TypeRef r, string v)
    {
        string p = Plain(r);
        switch (ShapeOf(scope, t, r))
        {
            case Shape.Number:
                string number = "((" + p + ")" + Rt + ".Convert(" + v + ", typeof(" + p + "), false))";
                return r.Nullable ? "(" + v + " == null ? (" + p + "?)null : (" + p + "?)" + number + ")" : number;
            case Shape.Value:
                return r.Nullable ? "(" + v + " == null ? (" + p + "?)null : (" + p + "?)(" + p + ")" + v + "!)" : "((" + p + ")" + v + "!)";
            case Shape.Anything:
                return r.Nullable ? v : v + "!";
            case Shape.Open:
                return "(" + v + " is " + p + " ? (" + p + ")" + v + "! : (" + p + ")" + Rt + ".Convert(" + v + ", typeof(" + p + "), false))";
            default:
                return r.Nullable ? "((" + p + "?)" + v + ")" : "((" + p + ")" + v + "!)";
        }
    }
}
