#nullable enable
namespace Corsac.Lang;

/// <summary>
/// GENERIC LOCAL FUNCTIONS THAT CAPTURE, as C# compiles them: the function
/// is a method of the type (Parser.ParseGenericLocalFunction), and what it
/// captured is handed to it at every call. Roslyn passes a struct of the
/// captured variables by reference; here each captured variable is one
/// parameter, by reference, at the front, named as the variable is, so the
/// body -- checked as a member of the type, a copy for each set of type
/// arguments -- reads and writes the enclosing method's own variable through
/// it, and every call puts `ref x` in front of what it was written with
/// (PassCaptures).
///
/// ONE VARIABLE, HOWEVER MANY SEE IT. A variable a generic local function
/// captures lives in a cell, as one a lambda captures does (Lookup), and what
/// is handed over is that cell (ParamSym.Cell; Lowering.CellOf), a struct's
/// too: a lambda inside the generic function holds the cell itself, so the
/// method, its lambdas, the generic function and the lambdas inside that all
/// read and write the one variable, and a write on any side is seen on every
/// other. A call names the variable the function saw where it was declared,
/// not one a lambda at the call calls the same (LookupCaptured).
///
/// THE TYPE PARAMETERS AROUND IT GO WITH IT. A generic local function written
/// in a generic method, or in another generic local function, is carried with
/// every copy of that one, its type arguments put in (Frontend.RehostLocals),
/// so a variable it captured whose type is the outer T is the copy's own.
///
/// WHAT IS CAPTURED IS FOUND ONCE, by reading the body where it was written
/// with the enclosing method's scopes open (ProbeGenericLocal): every name
/// that resolves to a variable outside it is one, as for a lambda, and so is
/// every variable a generic local function it calls captured
/// (SettleGenericCaptures). The parameters are then written into the
/// declaration, before any copy of it is made, and the count kept
/// (MethodDecl.Captures); the copies, and every later round's calls, take
/// them from there.
/// </summary>
public sealed partial class Binder
{
    /// <summary>What one generic local function was found to capture, until the member it is in is checked.</summary>
    private sealed class GenericCaptures
    {
        public GenericCaptures(MethodDecl template) { Template = template; }
        public MethodDecl Template { get; }
        /// <summary>The variables, by name, and their types.</summary>
        public Dictionary<string, Type> Variables { get; } = new(StringComparer.Ordinal);
        /// <summary>Those a generic local function it calls captured, as that one's parameters spell them.</summary>
        public Dictionary<string, TypeRef> Spelt { get; } = new(StringComparer.Ordinal);
        /// <summary>The enclosing method's constants it reads, declared again in its body.</summary>
        public Dictionary<string, ConstSym> Constants { get; } = new(StringComparer.Ordinal);
        /// <summary>The generic local functions it calls, whose captures are its too.</summary>
        public HashSet<MethodDecl> Calls { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>Every name declared inside it: never one it captures through a call.</summary>
        public HashSet<string> OwnNames { get; } = new(StringComparer.Ordinal);
        /// <summary>What C# refuses it to capture: a ref local, a ref, out or in parameter.</summary>
        public List<string> Refused { get; } = new();
        /// <summary>
        /// The type parameters of what is around it that its captures' types
        /// name -- found in its own variables, and taken on with a callee's
        /// captures -- which become its own (WriteCaptures).
        /// </summary>
        public HashSet<string> Open { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The names a generic local function is called by, wherever they are declared.</summary>
    private readonly HashSet<Sym> _genericLocalSyms = new(ReferenceEqualityComparer.Instance);

    /// <summary>Found in the member being checked, written in when it is done.</summary>
    private readonly List<GenericCaptures> _pendingCaptures = new();

    /// <summary>The probes running, innermost last: each is told the names declared inside it.</summary>
    private readonly List<GenericCaptures> _probeOwners = new();

    /// <summary>While probing, the generic local functions the body calls (PassCaptures).</summary>
    private HashSet<MethodDecl>? _probeCalls;

    /// <summary>Probes running: a lambda in one makes no class (CheckLambda).</summary>
    private int _probing;

    /// <summary>
    /// The type the generic local functions being named are methods of: the
    /// one being checked, or, in a lambda's closure, the one the lambda was
    /// written in.
    /// </summary>
    private TypeSymbol? HostType()
        => _thisType is not null && IsClosure(_thisType) ? _capturedThisType ?? _lexicalType : _thisType;

    /// <summary>
    /// A generic local function's written name, as its method group -- and,
    /// inside a closure, through the `this` the closure holds when the
    /// function is an instance method.
    /// </summary>
    private void DeclareGenericLocal(Node at, string name, string method)
    {
        // IN A COPY OF A GENERIC METHOD the name means the function carried
        // with the copy (Frontend.RehostLocals), typed as the copy is.
        if (_member is MethodDecl { Rehosted.Count: > 0 } copy && copy.Rehosted.TryGetValue(method, out string? carried))
        {
            method = carried;
        }
        List<MethodSymbol>? methods = HostType()?.FindMethods(method);
        if (methods is not { Count: > 0 }) return;
        Sym group = _thisType is not null && IsClosure(_thisType) && _capturedThisField is not null && methods.Any(m => !m.Static)
            ? new CapturedMethodGroupSym(_capturedThisField, methods)
            : new MethodGroupSym(methods);
        _genericLocalSyms.Add(group);
        Declare(at, name, group);
    }

    /// <summary>The hoisted declaration a generic local function's name stands for.</summary>
    private static MethodDecl? GenericLocalTemplate(Sym named)
    {
        List<MethodSymbol> methods = named switch
        {
            MethodGroupSym group => group.Methods,
            CapturedMethodGroupSym held => held.Methods,
            _ => new List<MethodSymbol>(),
        };
        return methods.Select(m => m.Decl).OfType<MethodDecl>().FirstOrDefault(d => d.HoistedName is not null);
    }

    /// <summary>
    /// A CALL OF A GENERIC LOCAL FUNCTION hands it the variables it captured:
    /// `ref x` for each, in front of what was written, once (CallExpr.
    /// CapturesPassed). Each is a cell from then on, here, so the address the
    /// function is given is one a lambda of its may keep.
    /// </summary>
    private void PassCaptures(CallExpr c, MethodDecl template, string written)
    {
        if (_probeCalls is not null)
        {
            _probeCalls.Add(template);
            return;
        }
        if (template.Captures <= 0) return;

        if (!c.CapturesPassed)
        {
            for (int i = 0; i < template.Captures; i++)
            {
                c.Args.Insert(i, new RefArgExpr
                {
                    // THE VARIABLE THE FUNCTION SAW, by where it was written,
                    // not by whatever is called the same here (LookupCaptured).
                    Target = new NameExpr { Name = template.Params[i].Name, CaptureOf = written, Line = c.Line, Col = c.Col },
                    Line = c.Line, Col = c.Col,
                });
            }
            if (c.ArgNames.Count > 0) c.WritableArgNames.InsertRange(0, Enumerable.Repeat<string?>(null, template.Captures));
            c.CapturesPassed = true;
        }

        for (int i = 0; i < template.Captures; i++)
        {
            switch (LookupCaptured(template.Params[i].Name, written))
            {
                case LocalSym { IsRef: false } local:
                    local.Boxed = true;
                    if (_declOf.TryGetValue(local, out LocalDecl? where)) _r.BoxedLocals.Add(where);
                    break;
                case ParamSym { ByRef: false } parameter:
                    parameter.ForcedCell = true;
                    break;
            }
        }
    }

    /// <summary>
    /// A VARIABLE A GENERIC LOCAL FUNCTION CAPTURED, at a call of it: the one
    /// in scope where the function's own name was declared, looked for from
    /// that scope outwards. A lambda at the call may have a parameter or a
    /// local of the same name (C# 8 lets it), and that is not the variable;
    /// from inside a closure, the variable is the closure's field (CheckName
    /// finds it when this does not).
    /// </summary>
    private Sym? LookupCaptured(string name, string function)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(function, out Sym? named) && _genericLocalSyms.Contains(named))
            {
                return LookupFrom(name, i);
            }
        }
        return Lookup(name);
    }

    /// <summary>The block's generic local functions whose captures are not known yet, probed.</summary>
    private void DiscoverGenericCaptures(Block b)
    {
        foreach ((string name, string _) in b.GenericLocals)
        {
            if (Lookup(name) is not Sym named || !_genericLocalSyms.Contains(named)
                || GenericLocalTemplate(named) is not MethodDecl template)
            {
                continue;
            }
            if (template.Captures >= 0 || _pendingCaptures.Any(p => ReferenceEquals(p.Template, template))) continue;
            ProbeGenericLocal(template);
        }
    }

    /// <summary>
    /// Reads a generic local function's body where it was written, as a
    /// lambda's first pass does: a copy of it, its own type parameters still
    /// open, with the enclosing scopes below a floor, so every name it reads
    /// from outside is written down. Quiet, and leaving nothing behind: no
    /// copy it would want, no class for a lambda in it.
    /// </summary>
    private void ProbeGenericLocal(MethodDecl template)
    {
        List<TypeRef> own = template.TypeParams
            .Select(tp => new TypeRef { Name = tp.Name, Line = template.Line, Col = template.Col })
            .ToList();
        MethodDecl probe = Monomorphiser.Specialise(template, own, template.Name + "$probe");
        GenericCaptures found = new(template);

        Dictionary<string, Type>? wasCaptured = _captured;
        Dictionary<string, ConstSym>? wasConstants = _capturedConstants;
        int wasFloor = _lambdaFloor;
        HashSet<MethodDecl>? wasCalls = _probeCalls;
        MethodSymbol? wasMethod = _method;
        int wasSlot = _nextSlot, wasMax = _maxSlot;
        List<Type>? wasInferred = _inferredReturns;
        Type? wasWanted = _wanted;
        HashSet<Sym> notNull = new(_notNull);
        HashSet<string> paths = new(_notNullPaths, StringComparer.Ordinal);
        int wanted = _r.Wanted.Count, overrides = _r.WantedOverrides.Count, wanting = _r.Wanting.Count;

        _captured = found.Variables;
        _capturedConstants = found.Constants;
        _probeCalls = found.Calls;
        _probing++;
        _quiet++;
        _inferredReturns = null;
        _wanted = null;
        _probeOwners.Add(found);
        // Its own type parameters are open in here (Resolve).
        if (_r.Methods.TryGetValue(template, out MethodSymbol? symbol)) _method = symbol;
        PushScope(functionBoundary: true);
        _lambdaFloor = _scopes.Count - 1;
        try
        {
            for (int i = 0; i < probe.Params.Count; i++)
            {
                Param p = probe.Params[i];
                Declare(p, p.Name, new ParamSym(i, Resolve(p.Type, HostType()), p.Name, p.IsRef || p.IsOut, p.IsReadOnlyRef));
            }
            if (probe.Body is not null) CheckBlock(probe.Body);
        }
        finally
        {
            PopScope();
            _probeOwners.RemoveAt(_probeOwners.Count - 1);
            _probing--;
            _quiet--;
            _captured = wasCaptured;
            _capturedConstants = wasConstants;
            _lambdaFloor = wasFloor;
            _probeCalls = wasCalls;
            _method = wasMethod;
            _nextSlot = wasSlot;
            _maxSlot = wasMax;
            _inferredReturns = wasInferred;
            _wanted = wasWanted;
            _notNull.Clear();
            _notNull.UnionWith(notNull);
            _notNullPaths.Clear();
            _notNullPaths.UnionWith(paths);
            // What the copy asked for is no copy anyone needs.
            _r.Wanted.RemoveRange(wanted, _r.Wanted.Count - wanted);
            _r.WantedOverrides.RemoveRange(overrides, _r.WantedOverrides.Count - overrides);
            _r.Wanting.RemoveRange(wanting, _r.Wanting.Count - wanting);
        }

        foreach ((string name, Type held) in found.Variables)
        {
            // WHAT C# REFUSES: a ref local or a by-reference parameter of
            // the enclosing method (CS8175, CS1628), whose variable lives in
            // a frame the function may outlast.
            Sym? named = Lookup(name);
            if (named is LocalSym { IsRef: true } || named is ParamSym { ByRef: true, CapturedVariable: false })
            {
                found.Refused.Add(name);
            }

            // AND WHAT IT CAPTURED, WHATEVER IT IS INSIDE CAPTURES TOO: a
            // lambda, or another generic local function being probed, around
            // this one reads the variable through it (Lookup writes it down
            // when it is below that one's floor).
            if (named is null && _captured is not null && _thisType is not null && IsClosure(_thisType)
                && (_thisType.FindField(name) ?? _thisType.FindField("<" + name + ">")) is not null)
            {
                _captured[name] = held;
            }
        }
        if (_capturedConstants is not null)
        {
            foreach ((string name, ConstSym constant) in found.Constants) _capturedConstants.TryAdd(name, constant);
        }
        foreach (GenericCaptures outer in _probeOwners) outer.Calls.UnionWith(found.Calls);

        _pendingCaptures.Add(found);
    }

    /// <summary>
    /// THE MEMBER IS CHECKED, so every generic local function in it has been
    /// read: each takes on what the ones it calls captured, until nothing
    /// more is added, and its parameters and constants are written into its
    /// declaration.
    /// </summary>
    private void SettleGenericCaptures()
    {
        if (_pendingCaptures.Count == 0) return;

        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (GenericCaptures p in _pendingCaptures)
            {
                int opened = p.Open.Count;
                foreach (MethodDecl callee in p.Calls)
                {
                    if (ReferenceEquals(callee, p.Template)) continue;
                    if (_pendingCaptures.FirstOrDefault(other => ReferenceEquals(other.Template, callee)) is GenericCaptures q)
                    {
                        foreach ((string name, Type held) in q.Variables)
                        {
                            if (!p.OwnNames.Contains(name) && !p.Spelt.ContainsKey(name) && p.Variables.TryAdd(name, held)) changed = true;
                        }
                        foreach ((string name, TypeRef spelt) in q.Spelt)
                        {
                            if (!p.OwnNames.Contains(name) && !p.Variables.ContainsKey(name) && p.Spelt.TryAdd(name, spelt)) changed = true;
                        }
                        HashSet<string> theirs = OpenOf(q);
                        foreach ((string name, Type held) in q.Variables)
                            if (p.Variables.TryGetValue(name, out Type? mine) && ReferenceEquals(mine, held)) Collect(held, theirs, p.Open);
                        foreach ((string name, TypeRef spelt) in q.Spelt)
                            if (p.Spelt.TryGetValue(name, out TypeRef? mine) && ReferenceEquals(mine, spelt)) Collect(spelt, theirs, p.Open);
                        foreach ((string name, ConstSym constant) in q.Constants)
                        {
                            if (!p.OwnNames.Contains(name) && p.Constants.TryAdd(name, constant)) changed = true;
                        }
                    }
                    else
                    {
                        HashSet<string> carried = new(callee.CarriedTypeParams, StringComparer.Ordinal);
                        for (int i = 0; i < callee.Captures; i++)
                        {
                            string name = callee.Params[i].Name;
                            if (!p.OwnNames.Contains(name) && !p.Variables.ContainsKey(name) && p.Spelt.TryAdd(name, callee.Params[i].Type)) changed = true;
                            if (p.Spelt.TryGetValue(name, out TypeRef? mine) && ReferenceEquals(mine, callee.Params[i].Type)) Collect(mine, carried, p.Open);
                        }
                    }
                }
                if (p.Open.Count != opened) changed = true;
            }
        }

        foreach (GenericCaptures p in _pendingCaptures)
        {
            WriteCaptures(p);
        }
        _pendingCaptures.Clear();
    }

    /// <summary>The captured variables as parameters at the front, and the constants as `const` locals at the top.</summary>
    private void WriteCaptures(GenericCaptures p)
    {
        MethodDecl template = p.Template;
        string written = template.HoistedName ?? template.Name;
        foreach (string name in p.Refused)
        {
            Error(template, $"cannot use '{name}' inside the local function '{written}': "
                          + "a ref local or a ref, out or in parameter cannot be captured");
        }

        List<Param> hidden = new();
        foreach ((string name, Type held) in p.Variables)
        {
            if (SpellOpen(held) is not TypeRef spelt)
            {
                Error(template, $"the local function '{written}' captures '{name}', whose type '{held}' cannot be written");
                continue;
            }
            hidden.Add(new Param { Name = name, Type = spelt, IsRef = true, Line = template.Line, Col = template.Col });
        }
        foreach ((string name, TypeRef spelt) in p.Spelt)
        {
            hidden.Add(new Param { Name = name, Type = spelt, IsRef = true, Line = template.Line, Col = template.Col });
        }
        template.WritableParams.InsertRange(0, hidden);
        template.Captures = hidden.Count;

        // THE TYPE PARAMETERS ITS CAPTURES ARE OF, made its own, as Roslyn
        // makes them: `T again` of the generic local function around it, or
        // `U kept` of the generic method, is no type in the hoisted method
        // unless it is one of its parameters ("'T' is not a known type"). Each
        // is inferred at every call from the variable handed to it there.
        HashSet<string> open = OpenOf(p);
        foreach (TypeParam tp in template.TypeParams) open.Remove(tp.Name);
        if (_thisType?.Decl is TypeDecl host) foreach (TypeParam tp in host.TypeParams) open.Remove(tp.Name);
        List<string> carried = open.OrderBy(n => n, StringComparer.Ordinal).ToList();
        template.CarriedTypeParams = carried;
        for (int k = carried.Count - 1; k >= 0; k--)
            template.WritableTypeParams.Insert(0, new TypeParam { Name = carried[k], Line = template.Line, Col = template.Col });

        if (template.Body is null) return;
        int at = 0;
        foreach ((string name, ConstSym constant) in p.Constants)
        {
            if (SpellConstant(constant, template) is not Expr value || RefOf(constant.Type) is not TypeRef type) continue;
            template.Body.WritableStatements.Insert(at++, new LocalDecl
            {
                Name = name, Type = type, Init = value, IsConst = true, Line = template.Line, Col = template.Col,
            });
        }
    }

    // A record's open type parameters: what it took on, and those its own variables' types name.
    private static HashSet<string> OpenOf(GenericCaptures p)
    {
        HashSet<string> open = new(p.Open, StringComparer.Ordinal);
        foreach ((_, Type held) in p.Variables) Params(held, open);
        return open;

        static void Params(Type t, HashSet<string> into)
        {
            if (t.ParamName is string name) into.Add(name);
            if (t.Element is Type element) Params(element, into);
            foreach (Type argument in t.Args) Params(argument, into);
        }
    }

    // Of `among`, the type parameters a type names, into `into`.
    private static void Collect(Type t, HashSet<string> among, HashSet<string> into)
    {
        if (t.ParamName is string name && among.Contains(name)) into.Add(name);
        if (t.Element is Type element) Collect(element, among, into);
        foreach (Type argument in t.Args) Collect(argument, among, into);
    }

    private static void Collect(TypeRef r, HashSet<string> among, HashSet<string> into)
    {
        if (r.Args.Count == 0 && among.Contains(r.Name)) into.Add(r.Name);
        foreach (TypeRef argument in r.Args) Collect(argument, among, into);
    }

    /// <summary>
    /// A type spelt back into source, OPEN ones too: a variable of a generic
    /// local function nested in another, or in a generic method, may be a T
    /// of the one around it -- spelt `T`, and put in for when that one is
    /// copied (Frontend.RehostLocals).
    /// </summary>
    private static TypeRef? SpellOpen(Type t)
    {
        if (RefOf(t) is TypeRef plain) return plain;
        int rank = 0;
        Type bare = t;
        while (bare.IsArray && bare.Element is Type of)
        {
            bare = of;
            rank++;
        }
        bool nullable = rank == 0 ? bare.Nullable : t.Nullable;
        if (bare.ParamName is string name)
        {
            return new TypeRef { Name = name, ArrayRank = rank, Nullable = nullable, ElementNullable = rank > 0 && bare.Nullable };
        }
        if (bare.Symbol?.Decl is { TypeParams.Count: > 0 } template && bare.Args.Count > 0)
        {
            TypeRef made = new()
            {
                Name = template.Outer is null ? template.Name : template.Outer + "." + template.Name,
                ArrayRank = rank, Nullable = nullable, ElementNullable = rank > 0 && bare.Nullable,
            };
            foreach (Type argument in bare.Args)
            {
                if (SpellOpen(argument) is not TypeRef spelt) return null;
                made.Arguments.Add(spelt);
            }
            return made;
        }
        return null;
    }

    /// <summary>A constant's value, written as source a `const` declaration accepts.</summary>
    private static Expr? SpellConstant(ConstSym constant, Node at)
    {
        if (constant.Text is not null)
        {
            return new LiteralExpr { Kind = Lit.Str, Text = constant.Text, Line = at.Line, Col = at.Col };
        }
        if (constant.Type.Prim == Prim.Bool)
        {
            return new LiteralExpr
            {
                Kind = Lit.Bool, Text = constant.Value == 0 ? "false" : "true", IntValue = constant.Value,
                Line = at.Line, Col = at.Col,
            };
        }
        if (RefOf(constant.Type) is not TypeRef type) return null;
        Expr literal;
        if (constant.Type.Prim is Prim.F32 or Prim.F64)
        {
            double real = BitConverter.Int64BitsToDouble(constant.Value);
            literal = new LiteralExpr
            {
                Kind = Lit.Real, Text = Math.Abs(real).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                RealValue = Math.Abs(real), Line = at.Line, Col = at.Col,
            };
            if (real < 0 || real == 0 && double.IsNegative(real))
            {
                literal = new UnaryExpr { Op = UnOp.Neg, Operand = literal, Line = at.Line, Col = at.Col };
            }
        }
        else
        {
            literal = new LiteralExpr { Kind = Lit.Int, Text = constant.Value.ToString(), IntValue = constant.Value, Line = at.Line, Col = at.Col };
        }
        return new CastExpr { Type = type, Operand = literal, Line = at.Line, Col = at.Col };
    }
}
