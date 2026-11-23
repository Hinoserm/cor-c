#nullable enable
namespace Corsac.Lang;

/// <summary>
/// HOW FAR A REF STRUCT'S VALUE MAY GO, as C# 11 rules it (the "safe-context"
/// of the ref-safety rules, Roslyn's RefSafetyAnalysis): a span over the
/// heap may go anywhere, one over this method's stack (stackalloc) no
/// further than the method, one a call made only as far as what it was made
/// from. A context is a number, the caller's 0, the method's 1, each block
/// within it one more: a value may go where the number is its own or more.
///
/// What is checked, once a body is bound, in Roslyn's words and places:
/// - a value returned (CS8352 for a variable, CS8353 for a stackalloc, CS8347
///   for a call's result, with the argument that made it so);
/// - a value assigned to a variable, a field of a ref struct, an `out` or a
///   `ref` parameter, that may live longer than it (CS8352 and the rest);
/// - a call whose `ref` argument of a ref struct, or whose receiver -- a ref
///   struct's own method, which may store into it -- would be given a value
///   narrower than its own (CS8350, the method-arguments-must-match rule).
///
/// Here a span is an array, an offset and a count (Core.cor's Span), so what
/// C# refuses would run safely all the same: the rules are kept because C#
/// keeps them, and a program they refuse is not a C# program.
/// </summary>
public sealed partial class Binder
{
    /// <summary>Each ref-struct local's context, and the block depth it is declared at (how far a reference to it may go).</summary>
    private readonly Dictionary<LocalSym, (int Safe, int Declared)> _refSafety = new(ReferenceEqualityComparer.Instance);

    /// <summary>The method whose body is checked: its parameters' `scoped`.</summary>
    private MethodDecl? _refSafetyMethod;

    /// <summary>Whether a body needs the check: a ref struct it returns, takes or declares.</summary>
    private bool NeedsRefSafety(MethodDecl md, Type returns)
        => RefLike(returns) || _sawRefLocal
           || _method?.Params.Any(p => RefLike(p.Type)) == true;

    /// <summary>The check of one method's body, once it is bound (EndMethodBody).</summary>
    private void CheckRefSafety(MethodDecl md, Type returns)
    {
        if (md.Body is null) return;
        _refSafety.Clear();
        _refSafetyMethod = md;
        try { SafetyVisit(md.Body, 1, RefLike(returns)); }
        finally { _refSafetyMethod = null; _refSafety.Clear(); }
    }

    private void SafetyVisit(Node n, int depth, bool returnsRefLike)
    {
        switch (n)
        {
            case LambdaExpr:
                // A body of its own: what it may do with the method's values
                // is the capture rules' (CS8175).
                return;
            case Block block:
                foreach (Stmt s in block.Statements) SafetyVisit(s, depth + 1, returnsRefLike);
                return;
            case LocalDecl d:
            {
                if (d.Init is not null) SafetyVisit(d.Init, depth, returnsRefLike);
                if (_r.LocalSymbols.TryGetValue(d, out LocalSym? local) && RefLike(local.Type))
                {
                    // Its initialiser's context -- a stackalloc's the method's,
                    // wherever in it -- or its own block's where it is scoped;
                    // none at all is the caller's (default).
                    int safe = d.IsScoped ? depth : d.Init is null ? 0 : SafeContext(d.Init);
                    _refSafety[local] = (safe, depth);
                }
                foreach (LocalDecl also in d.Also) SafetyVisit(also, depth, returnsRefLike);
                return;
            }
            // Whatever is returned, converted to the ref struct returned: a
            // stackalloc is typed as the block it makes.
            case ReturnStmt { Value: { } value } when returnsRefLike:
                SafetyVisit(value, depth, returnsRefLike);
                if (SafeContext(value) > 0)
                {
                    _escapeInto = _method?.Returns;
                    try { EscapeRefused(value, 0, returning: true); }
                    finally { _escapeInto = null; }
                }
                return;
            case AssignExpr { Op: null, Value: not RefArgExpr } assign when VariableType(assign.Target) is { } assigned && RefLike(assigned):
            {
                SafetyVisit(assign.Value, depth, returnsRefLike);
                SafetyVisit(assign.Target, depth, returnsRefLike);
                int target = TargetContext(assign.Target);
                if (SafeContext(assign.Value) > target)
                {
                    _escapeInto = assigned;
                    try { EscapeRefused(assign.Value, target, returning: false); }
                    finally { _escapeInto = null; }
                }
                return;
            }
            case CallExpr call:
                ArgumentsMustMatch(call, Receiver(call), CallArguments(call), _r.Calls.TryGetValue(call, out MethodSymbol? called) ? called : null);
                break;
            case NewExpr made when _r.NewConstructors.TryGetValue(made, out MethodSymbol? ctor):
                ArgumentsMustMatch(made, null, made.Args, ctor);
                break;
        }
        List<Node> children = new();
        Expr? marked = AddSafetyChildren(n, children);
        try
        {
        foreach (Node child in children)
        {
            if (child is Block) SafetyVisit(child, depth, returnsRefLike);
            else SafetyVisit(child, child is Stmt and not Block && n is ForStmt or ForeachStmt ? depth + 1 : depth, returnsRefLike);
        }
        }
        finally { if (marked is not null) _inOwnRewrite.Remove(marked); }
    }

    // ---- the contexts ----------------------------------------------------------

    /// <summary>
    /// A variable's type, as its symbol says it: an assignment's target is
    /// never read as a value, and the binder keeps only a placeholder for it.
    /// </summary>
    private Type? VariableType(Expr target) => target switch
    {
        NameExpr name when _r.Resolved.TryGetValue(name, out Sym? sym) && sym is LocalSym or ParamSym => sym switch
        {
            LocalSym local => local.Type,
            ParamSym parameter => parameter.Type,
            _ => null,
        },
        MemberExpr member when _r.Resolved.TryGetValue(member, out Sym? field) && field is FieldSym { Field: { } declared } => declared.Type,
        _ => _r.TypeOf(target),
    };

    /// <summary>How far a ref struct's value may go: 0 anywhere, 1 this method, more a block of it.</summary>
    private int SafeContext(Expr e)
    {
        // A stackalloc first: typed as the block it makes, the span is what
        // it converts to.
        if (e is CallExpr { Target: NameExpr { Name: StackAllocMarker } }) return 1;
        // A variable by its symbol: a target has no type of its own here.
        if (e is NameExpr variable && _r.Resolved.TryGetValue(variable, out Sym? named) && VariableType(e) is { } its && RefLike(its))
        {
            return named switch
            {
                LocalSym local => _refSafety.TryGetValue(local, out var known) ? known.Safe : 0,
                ParamSym parameter => ParamScoped(parameter) ? 1 : 0,
                _ => 0,
            };
        }
        if (_r.TypeOf(e) is not { } type || !RefLike(type))
        {
            // A conversion to a span the binder put in place of a written
            // expression: the span is that expression's -- once: the
            // conversion holds the expression, met again inside it as itself.
            if (!_r.Rewrites.TryGetValue(e, out Expr? converted) || ReferenceEquals(converted, e) || _r.TypeOf(converted) is not { } made
                || !RefLike(made) || !_safetyRewriting.Add(e))
            {
                return 0;
            }
            try { return SafeContext(converted); }
            finally { _safetyRewriting.Remove(e); }
        }
        switch (e)
        {
            case NameExpr name when _r.Resolved.TryGetValue(name, out Sym? sym):
                return sym switch
                {
                    LocalSym local => _refSafety.TryGetValue(local, out var known) ? known.Safe : 0,
                    ParamSym parameter => ParamScoped(parameter) ? 1 : 0,
                    _ => 0,
                };
            case ConditionalExpr conditional:
                return Math.Max(SafeContext(conditional.Then), SafeContext(conditional.Else));
            case MemberExpr { Target: { } holder } member when VariableType(holder) is { } held && RefLike(held)
                                                              && !(_r.Resolved.TryGetValue(member, out Sym? field) && field is FieldSym { Field.Static: true }):
                return SafeContext(holder);
            case CallExpr call when _r.Calls.TryGetValue(call, out MethodSymbol? called):
                return ResultContext(Receiver(call), CallArguments(call), called).Context;
            case NewExpr made when _r.NewConstructors.TryGetValue(made, out MethodSymbol? ctor):
                return ResultContext(null, made.Args, ctor).Context;
            case CastExpr cast:
                return SafeContext(cast.Operand);
        }
        if (_r.Rewrites.TryGetValue(e, out Expr? instead) && !ReferenceEquals(instead, e) && _safetyRewriting.Add(e))
        {
            try { return SafeContext(instead); }
            finally { _safetyRewriting.Remove(e); }
        }
        return 0;
    }

    /// <summary>The expressions whose rewrite the contexts are inside now: met again there, each is itself.</summary>
    private readonly HashSet<Expr> _safetyRewriting = new(ReferenceEqualityComparer.Instance);

    /// <summary>How far a reference to a variable may go: a local its block, a value parameter this method, a ref parameter, a field of a class, an element, anywhere.</summary>
    private int RefContext(Expr e)
    {
        switch (e)
        {
            case NameExpr name when _r.Resolved.TryGetValue(name, out Sym? sym):
                return sym switch
                {
                    LocalSym { IsRef: true } => 1,
                    LocalSym local => _refSafety.TryGetValue(local, out var known) ? known.Declared : 1,
                    ParamSym { ByRef: true } => 0,
                    ParamSym => 1,
                    ThisSym => 1,
                    _ => 0,
                };
            case MemberExpr { Target: { } holder } when _r.TypeOf(holder) is { } held && !held.IsReference && held.Prim != Prim.Any:
                return RefContext(holder);
            case IndexExpr { Target: { } indexed } when _r.TypeOf(indexed) is { } of && RefLike(of):
                return SafeContext(indexed);
        }
        return 0;
    }

    /// <summary>How far what is assigned to a variable may go: its own context.</summary>
    private int TargetContext(Expr target) => target switch
    {
        MemberExpr { Target: { } holder } when VariableType(holder) is { } held && RefLike(held) => TargetContext(holder),
        // `this.S = ...` in a ref struct's own method: the caller's.
        MemberExpr { Target: ThisExpr } => 0,
        _ => SafeContext(target),
    };

    /// <summary>Whether a parameter of the method checked is `scoped`.</summary>
    private bool ParamScoped(ParamSym parameter)
        => _refSafetyMethod is { } md && parameter.Index < md.Params.Count && md.Params[parameter.Index].IsScoped;

    /// <summary>A call's receiver as an argument: an instance method's object, where it is a ref struct's.</summary>
    private Expr? Receiver(CallExpr call)
        => call.Target is MemberExpr { Target: { } receiver } && _r.Calls.TryGetValue(call, out MethodSymbol? called) && !called.Static
           && _r.TypeOf(receiver) is { } held && RefLike(held) ? receiver : null;

    /// <summary>A call's arguments as the method's parameters take them: an extension's receiver first.</summary>
    private IReadOnlyList<Expr> CallArguments(CallExpr call)
    {
        if (call.Target is MemberExpr { Target: { } receiver } && _r.Calls.TryGetValue(call, out MethodSymbol? called) && called.Static
            && called.Params.Count == call.Args.Count + 1 && called.Decl is { Params.Count: > 0 } d && d.Params[0].IsThis)
        {
            List<Expr> all = new(call.Args.Count + 1) { receiver };
            all.AddRange(call.Args);
            return all;
        }
        return call.Args;
    }

    /// <summary>
    /// A call's result's context: the narrowest of what it was made from --
    /// each ref struct argument's value, each argument passed by reference's
    /// variable, the receiver -- but a `scoped` parameter's. Which one made it
    /// so, for the message: its place (-1 the receiver) and the parameter.
    /// </summary>
    private (int Context, int Culprit, string? Parameter) ResultContext(Expr? receiver, IReadOnlyList<Expr> args, MethodSymbol m)
    {
        int context = receiver is null ? 0 : SafeContext(receiver);
        int culprit = context > 0 ? -1 : -2;
        string? parameter = null;
        for (int i = 0; i < args.Count && i < m.Params.Count; i++)
        {
            if (m.Decl is { } d && i < d.Params.Count && d.Params[i].IsScoped) continue;
            Expr arg = args[i] is RefArgExpr { Target: { } passed } ? passed : args[i];
            int one = args[i] is RefArgExpr || m.Params[i].ByRef ? RefContext(arg) : SafeContext(arg);
            if (one > context) { context = one; culprit = i; parameter = m.Params[i].Name; }
        }
        return (context, culprit, parameter);
    }

    /// <summary>
    /// The method-arguments-must-match rule (C# 11): a call that could store
    /// into a ref struct it is handed by reference -- a `ref` or `out`
    /// argument of one, or the receiver of a ref struct's own method -- may be
    /// given no ref struct value narrower than that one (CS8350).
    /// </summary>
    private void ArgumentsMustMatch(Expr call, Expr? receiver, IReadOnlyList<Expr> args, MethodSymbol? m)
    {
        if (m is null) return;
        int into = int.MaxValue;
        if (receiver is not null && !(m.Decl is { } readOnly && readOnly.Mods.HasFlag(Mods.Readonly))) into = SafeContext(receiver);
        for (int i = 0; i < args.Count && i < m.Params.Count; i++)
            if (args[i] is RefArgExpr { Target: { } written } && _r.TypeOf(written) is { } held && RefLike(held)
                && !(m.Decl is { } d && i < d.Params.Count && d.Params[i].IsScoped))
                into = Math.Min(into, SafeContext(written));
        if (into == int.MaxValue) return;
        for (int i = 0; i < args.Count && i < m.Params.Count; i++)
        {
            if (args[i] is RefArgExpr || m.Decl is { } d && i < d.Params.Count && d.Params[i].IsScoped) continue;
            if (_r.TypeOf(args[i]) is not { } given || !RefLike(given) || SafeContext(args[i]) <= into) continue;
            Node at = call is CallExpr { Target: { } target } ? Start(target) : call;
            Error(at, $"CS8350: This combination of arguments to '{MethodShown(m)}' is disallowed because it may expose variables referenced by parameter '{m.Params[i].Name}' outside of their declaration scope");
            EscapeRefused(args[i], into, returning: false);
            return;
        }
    }

    // ---- the messages ----------------------------------------------------------

    /// <summary>
    /// A value that may not go where it goes, said at what made it so: a
    /// variable (CS8352), a stackalloc (CS8353), a call's result and then its
    /// argument (CS8347), a reference to a local returned (CS8168).
    /// </summary>
    /// <summary>
    /// The ref struct a refused value goes into, where a return or an
    /// assignment says it: a stackalloc is named as the span it becomes.
    /// </summary>
    private Type? _escapeInto;

    private void EscapeRefused(Expr e, int allowed, bool returning)
    {
        switch (e)
        {
            case CallExpr { Target: NameExpr { Name: StackAllocMarker } }:
                Error(e, $"CS8353: A result of a stackalloc expression of type '{(_escapeInto ?? _r.TypeOf(e))!.Shown()}' cannot be used in this context because it may be exposed outside of the containing method");
                return;
            case NameExpr name when _r.Resolved.TryGetValue(name, out Sym? sym):
            {
                string shown = sym is ParamSym parameter && ParamScoped(parameter) ? "scoped " + parameter.Type.Shown() + " " + name.Name : name.Name;
                Error(e, $"CS8352: Cannot use variable '{shown}' in this context because it may expose referenced variables outside of their declaration scope");
                return;
            }
            case ConditionalExpr conditional:
                if (SafeContext(conditional.Then) > allowed) EscapeRefused(conditional.Then, allowed, returning);
                if (SafeContext(conditional.Else) > allowed) EscapeRefused(conditional.Else, allowed, returning);
                return;
            case MemberExpr { Target: { } holder } when _r.TypeOf(holder) is { } held && RefLike(held):
                EscapeRefused(holder, allowed, returning);
                return;
            case CastExpr cast:
                EscapeRefused(cast.Operand, allowed, returning);
                return;
            case CallExpr call when _r.Calls.TryGetValue(call, out MethodSymbol? called):
                CallRefused(call, Start(call.Target), Receiver(call), CallArguments(call), called, allowed, returning);
                return;
            case NewExpr made when _r.NewConstructors.TryGetValue(made, out MethodSymbol? ctor):
                CallRefused(made, made, null, made.Args, ctor, allowed, returning);
                return;
        }
        if (_r.Rewrites.TryGetValue(e, out Expr? instead) && !ReferenceEquals(instead, e) && _safetyRewriting.Add(e))
        {
            try { EscapeRefused(instead, allowed, returning); }
            finally { _safetyRewriting.Remove(e); }
        }
    }

    private void CallRefused(Expr call, Node at, Expr? receiver, IReadOnlyList<Expr> args, MethodSymbol m, int allowed, bool returning)
    {
        (_, int culprit, string? parameter) = ResultContext(receiver, args, m);
        // The receiver's own narrowness is said at the receiver alone, as
        // Roslyn says it (`stackSpan.Slice(1)` returned).
        if (culprit == -1 && receiver is not null) { EscapeRefused(receiver, allowed, returning); return; }
        if (culprit < 0) return;
        Error(at, $"CS8347: Cannot use a result of '{MethodShown(m)}' in this context because it may expose variables referenced by parameter '{parameter}' outside of their declaration scope");
        if (args[culprit] is RefArgExpr { Target: { } passed } || m.Params[culprit].ByRef)
        {
            Expr variable = args[culprit] is RefArgExpr { Target: { } written } ? written : args[culprit];
            if (returning && variable is NameExpr local && _r.Resolved.TryGetValue(local, out Sym? sym))
            {
                if (sym is LocalSym) Error(local, $"CS8168: Cannot return local '{local.Name}' by reference because it is not a ref local");
                else if (sym is ParamSym) Error(local, $"CS8166: Cannot return a parameter by reference '{local.Name}' because it is not a ref parameter");
            }
            return;
        }
        EscapeRefused(args[culprit], allowed, returning);
    }

    /// <summary>A method as Roslyn names it in a message: `P.Pass(Span<int>)`, `Span<int>.Span(ref int)`.</summary>
    private static string MethodShown(MethodSymbol m)
    {
        string owner = Type.Plain(m.Owner, Prim.Void).Shown();
        string name = m.IsCtor ? owner[..(owner.IndexOf('<') is int lt and >= 0 ? lt : owner.Length)] : m.Name;
        IEnumerable<string> ps = m.Params.Select((p, i) =>
            (m.Decl is { } d && i < d.Params.Count ? d.Params[i].IsOut ? "out " : d.Params[i].IsReadOnlyRef ? "in " : d.Params[i].IsRef ? "ref " : "" : p.ByRef ? "ref " : "")
            + p.Type.Shown());
        return owner + "." + name + "(" + string.Join(", ", ps) + ")";
    }

    /// <summary>Where an expression starts, as Roslyn places a call's message: the leftmost of a member chain.</summary>
    private static Node Start(Node e)
    {
        while (e is MemberExpr { Target: { } inner }) e = inner;
        return e;
    }
}
