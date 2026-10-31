#nullable enable
namespace Corsac.Lang;

// ---- methods that never return ---------------------------------------------
//
// WORKED OUT, NEVER TAKEN ON TRUST. A method never returns when every path of
// its body ends in a throw, in a loop with no way out, or in a call to a
// method that never returns either. The code generator ends the block after a
// call to one (Lowering.CallDirect), so what is live across a throw helper
// need not survive it -- and it asks here, of every method, marked or not.
//
// [DoesNotReturn] (System.Diagnostics.CodeAnalysis) proves nothing of the
// kind. In C# it informs null analysis and nothing else: a call to a method
// marked with it ends the path as far as null state goes (NeverReturns), the
// method itself is warned about where its own body can come back (CS8763,
// CheckDoesNotReturn), and at run time the call returns as any call does when
// the method does. It never changes the code.
//
// TWO ANSWERS, ONE FOR EACH KIND OF CODE. Code only this compile emits is
// judged on everything this compile bound: the greatest fixpoint over the
// call chains of every body it has. Code another unit may compile as well --
// a generic copy, of which the link keeps one and refuses two that differ
// (DefinitionCoalescer) -- must come out the same in both, and the units see
// different bodies: the library proves its throw helpers from their bodies,
// and a program compiled against it sees them as declarations. So that code
// is judged on what every unit reads alike, each method's own text
// (MethodDecl.NeverReturnsByText), which the declaration index carries for
// the bodies it leaves out (SourceIndexBuilder). The two agree by
// construction: the same tokens, the same rule, wherever they are read.

public sealed partial class Binder
{
    /// <summary>Whether a declaration is marked [DoesNotReturn].</summary>
    internal static bool MarkedDoesNotReturn(MethodDecl? d)
        => d is not null && d.Attributes.Any(a => a.Target.Length == 0 && a.Name is "DoesNotReturn" or "DoesNotReturnAttribute");

    /// <summary>What was proved of each method asked about, and of every method its proof went through: true where it never returns.</summary>
    private readonly Dictionary<MethodDecl, bool> _neverReturns = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// C#'S CS8763, for a [DoesNotReturn] method whose body was just checked:
    /// at each `return` its body can reach, and at its end when that can be
    /// reached, counting a call to any method marked [DoesNotReturn] as the
    /// end of its path and a finally as no part of it, as Roslyn's null
    /// analysis does.
    /// </summary>
    private void CheckDoesNotReturn(MethodDecl md)
    {
        if (!MarkedDoesNotReturn(md) || md.Body is null) return;
        // C# reports it for a local function or lambda marked so as well;
        // this parser reads no attributes on those, so only members get here.
        Flow flow = new(callee => MarkedDoesNotReturn(callee.Decl), roslyn: true);
        bool ends = Completes(md.Body, flow);
        if (flow.Unsure) return;
        foreach (Node back in flow.Returns) Warning(back, "CS8763", "A method marked [DoesNotReturn] should not return.");
        if (ends) Warning(md.End == (0, 0) ? md : new ExprStmt { Expr = new ThisExpr(), Line = md.End.Line, Col = md.End.Col },
            "CS8763", "A method marked [DoesNotReturn] should not return.");
    }

    /// <summary>
    /// Whether a call to `m` never returns, for code this compile alone emits
    /// (`shared` false) or for code other units may compile too (`shared`
    /// true, which only its text answers).
    ///
    /// For code of its own, the GREATEST FIXPOINT over m and every method
    /// its body can end a path calling: all are first taken never to return,
    /// and any whose body can then reach a return or its end is dropped,
    /// until none is -- so a chain that ends in a method that returns returns
    /// all the way, and methods that only call one another, a recursion that
    /// only recurses, never do. A method whose body this compile did not
    /// bind -- another unit's, a generic template -- is taken at its text.
    /// Asked by the code generator, and before a lowered body is let go
    /// (Lowering.ReleaseBody), so no proof reads a body emptied under it.
    /// </summary>
    internal bool CallNeverReturns(MethodSymbol m, bool shared)
    {
        if (m.Decl is not MethodDecl d || m.IsCtor || m.Async) return false;
        if (shared || !Provable(m)) return d.NeverReturnsByText;
        if (_neverReturns.TryGetValue(d, out bool known)) return known;

        // Every method m's proof goes through: those its body can end a path
        // calling, and theirs, as far as bodies this compile bound go -- and
        // for each, the ones whose paths can end calling it.
        Dictionary<MethodDecl, MethodSymbol> closure = new(ReferenceEqualityComparer.Instance);
        Dictionary<MethodDecl, List<MethodDecl>> callers = new(ReferenceEqualityComparer.Instance);
        Stack<(MethodSymbol Callee, MethodDecl? Caller)> pending = new();
        pending.Push((m, null));
        while (pending.TryPop(out var next))
        {
            MethodDecl ad = (MethodDecl)next.Callee.Decl!;
            if (_neverReturns.ContainsKey(ad)) continue;
            if (next.Caller is not null)
            {
                if (!callers.TryGetValue(ad, out List<MethodDecl>? those)) callers[ad] = those = new();
                those.Add(next.Caller);
            }
            if (closure.ContainsKey(ad)) continue;
            closure[ad] = next.Callee;
            // Every call that can end a path, with none of them trusted: the
            // most code that can be reached, whatever is proved.
            Flow all = new(callee =>
            {
                if (DirectlyCalled(callee) && Provable(callee)) pending.Push((callee, ad));
                return false;
            }, roslyn: false);
            Completes(ad.Body!, all);
        }

        HashSet<MethodDecl> holds = new(closure.Keys, ReferenceEqualityComparer.Instance);
        bool Ends(MethodSymbol callee)
        {
            if (!DirectlyCalled(callee) || callee.Decl is not MethodDecl cd || callee.IsCtor || callee.Async) return false;
            if (holds.Contains(cd)) return true;
            return _neverReturns.TryGetValue(cd, out bool proved) ? proved : cd.NeverReturnsByText;
        }
        // Each walked once, and again only when a method it can end a path
        // calling is dropped: nothing else can change what its walk finds.
        Queue<MethodDecl> asked = new(closure.Keys);
        HashSet<MethodDecl> queued = new(closure.Keys, ReferenceEqualityComparer.Instance);
        while (asked.TryDequeue(out MethodDecl? c))
        {
            queued.Remove(c);
            // What its text says holds whatever its body's walk finds.
            if (!holds.Contains(c) || c.NeverReturnsByText) continue;
            Flow flow = new(Ends, roslyn: false);
            bool ends = Completes(c.Body!, flow);
            if (!ends && flow.Returns.Count == 0 && !flow.Unsure) continue;
            holds.Remove(c);
            if (!callers.TryGetValue(c, out List<MethodDecl>? those)) continue;
            foreach (MethodDecl caller in those)
                if (holds.Contains(caller) && queued.Add(caller)) asked.Enqueue(caller);
        }
        foreach (MethodDecl c in closure.Keys) _neverReturns[c] = holds.Contains(c);
        return _neverReturns[d];
    }

    /// <summary>
    /// Whether a call to this method runs its own body: not one dispatched
    /// through a slot, which an override, or an interface's implementation,
    /// answers with a body of its own.
    /// </summary>
    private static bool DirectlyCalled(MethodSymbol m)
        => !m.Virtual && !m.Override && !m.Abstract && m.Owner.Kind != TypeKind.Interface;

    /// <summary>
    /// Whether this compile bound the method's body, and so can walk it. A
    /// body left until used (WantBody) is not bound here: asked in the middle
    /// of lowering a caller, it would be checked there, and a call some
    /// bound body makes was checked with that body. Not an iterator's: its
    /// call hands back an enumerator however the body ends.
    /// </summary>
    private bool Provable(MethodSymbol m)
        => m.Decl is MethodDecl { Body: { Iterator: false } } d && !m.IsCtor && !m.Async && _r.FrameSize.ContainsKey(d);

    /// <summary>One walk of a body's paths.</summary>
    private sealed class Flow
    {
        public Flow(Func<MethodSymbol, bool> ends, bool roslyn) { Ends = ends; Roslyn = roslyn; }

        /// <summary>Whether a call to this method ends its path.</summary>
        public readonly Func<MethodSymbol, bool> Ends;

        /// <summary>
        /// Roslyn's null analysis, for its warning: a try whose finally cannot
        /// end still ends where its body or a catch does. Otherwise the truth:
        /// it does not end at all.
        /// </summary>
        public readonly bool Roslyn;

        /// <summary>The `return`s that can be reached.</summary>
        public readonly List<Node> Returns = new();

        /// <summary>For each loop or switch round the walk, innermost last: whether a break leaves it, and whether a continue reaches its test.</summary>
        public readonly List<(bool Breaks, bool Continues, bool Loop)> Targets = new();

        /// <summary>A goto, whose target this walk does not follow, or a statement it does not know: nothing is proved.</summary>
        public bool Unsure;
    }

    /// <summary>
    /// A condition's value when it is a constant: literals, and the consts
    /// the checker resolved names to (BindResult.Resolved, as lowering's
    /// NamedConstant reads them) -- `while (Forever)` never ends either.
    /// </summary>
    private bool? Constant(Expr? e)
        => e is not null && Fold.TryConst(e, out long v, x => x is MemberExpr or NameExpr && _r.Resolved.TryGetValue(x, out Sym? sym)
               && x is not MemberExpr { Target: NameExpr { Name: "nint" or "nuint" } }
               && sym is ConstSym { Text: null } k ? (k.Value, Fold.Whole(k.Type)) : null) ? v != 0 : null;

    /// <summary>
    /// Whether an expression, run as a statement, ends its path: a throw, or
    /// a call that never returns -- but not one behind a `?.`, which a null
    /// receiver skips.
    /// </summary>
    private bool EndsPath(Expr? e, Flow flow)
    {
        if (e is null) return false;
        if (e is ThrowExpr) return true;
        // As written and as the binder rewrote it, as Called follows it.
        Expr at = e;
        for (int hops = 0; ; hops++)
        {
            if (ShortCircuits(at)) return false;
            if (hops == 8 || !_r.Rewrites.TryGetValue(at, out Expr? to)) break;
            at = to;
        }
        return Called(e) is { } m && flow.Ends(m);
    }

    /// <summary>
    /// Whether an expression's call or assignment is behind a `?.` or `?[`
    /// anywhere along the chain of receivers it is made on: a null there
    /// skips the rest of the chain, the call with it.
    /// </summary>
    private static bool ShortCircuits(Expr e)
    {
        for (Expr? at = e; at is not null; )
        {
            switch (at)
            {
                case MemberExpr { NullConditional: true } or IndexExpr { NullConditional: true }: return true;
                case MemberExpr member: at = member.Target; break;
                case IndexExpr index: at = index.Target; break;
                case CallExpr call: at = call.Target; break;
                // A `!` ends nothing (NullConditionalAssignment.Linked).
                case SuppressExpr suppressed: at = suppressed.Operand; break;
                case AssignExpr assign: at = assign.Target; break;
                default: return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the end of `s` can be reached when its start can, as C#'s
    /// reachability has it, recording the returns and breaks reached.
    /// </summary>
    private bool Completes(Stmt s, Flow flow)
    {
        switch (s)
        {
            case Block b:
            {
                foreach (Stmt each in b.Statements)
                {
                    if (!Completes(each, flow)) return false;
                }
                return true;
            }
            case ExprStmt e:
                return !EndsPath(e.Expr, flow);
            case LocalDecl l:
                return l.LocalFunction || !EndsPath(l.Init, flow);
            case ThrowStmt:
                return false;
            case ReturnStmt r:
                if (!EndsPath(r.Value, flow)) flow.Returns.Add(r);
                return false;
            case YieldStmt y:
                if (y.Value is null) flow.Returns.Add(y);
                return y.Value is not null;
            case BreakStmt:
                if (flow.Targets.Count > 0) flow.Targets[^1] = flow.Targets[^1] with { Breaks = true };
                return false;
            case ContinueStmt:
                for (int k = flow.Targets.Count - 1; k >= 0; k--)
                {
                    if (!flow.Targets[k].Loop) continue;
                    flow.Targets[k] = flow.Targets[k] with { Continues = true };
                    break;
                }
                return false;
            case GotoCaseStmt:
                return false;
            case GotoStmt:
                flow.Unsure = true;
                return false;
            case LabeledStmt labeled:
                return Completes(labeled.Body, flow);
            case IfStmt i:
            {
                bool? known = Constant(i.Cond);
                bool thenEnds = known != false && Completes(i.Then, flow);
                bool elseEnds = known != true && (i.Else is null || Completes(i.Else, flow));
                return thenEnds || elseEnds;
            }
            case WhileStmt w:
            {
                bool? known = Constant(w.Cond);
                if (known == false) return true;
                (bool breaks, _, _) = LoopBody(w.Body, flow);
                return breaks || known != true;
            }
            case DoStmt d:
            {
                (bool breaks, bool continues, bool bodyEnds) = LoopBody(d.Body, flow);
                return breaks || Constant(d.Cond) != true && (bodyEnds || continues);
            }
            case ForStmt f:
            {
                if (f.Init is not null && !Completes(f.Init, flow)) return false;
                bool? known = f.Cond is null ? true : Constant(f.Cond);
                if (known == false) return true;
                (bool breaks, _, _) = LoopBody(f.Body, flow);
                return breaks || known != true;
            }
            case ForeachStmt fe:
                LoopBody(fe.Body, flow);
                return true;
            case SwitchStmt sw:
            {
                flow.Targets.Add((false, false, false));
                bool fallsOut = false;
                bool hasDefault = false;
                bool sawTrue = false, sawFalse = false;
                foreach (SwitchCase c in sw.Cases)
                {
                    if (c.Pattern is null) hasDefault = true;
                    // `case true:` with `case false:` is every bool, as a
                    // default is, when neither has a guard.
                    if (c.Pattern is BinaryExpr { Op: BinOp.Eq, Left: SubjectExpr { Outer: 0 }, Right: LiteralExpr { Kind: Lit.Bool } truth }
                        && _r.TypeOf(sw.Subject) is { Prim: Prim.Bool, Nullable: false })
                    {
                        sawTrue |= truth.IntValue != 0;
                        sawFalse |= truth.IntValue == 0;
                    }
                    bool ends = true;
                    foreach (Stmt each in c.Body)
                    {
                        if (!Completes(each, flow)) { ends = false; break; }
                    }
                    // An empty section's labels share the next one's
                    // statements; only a section with statements of its own
                    // can run off its end.
                    if (c.Body.Count > 0) fallsOut |= ends;
                }
                bool breaks = flow.Targets[^1].Breaks;
                flow.Targets.RemoveAt(flow.Targets.Count - 1);
                return breaks || fallsOut || !(hasDefault || sawTrue && sawFalse);
            }
            case TryStmt t:
            {
                bool ends = Completes(t.Body, flow);
                foreach (CatchClause c in t.Catches) ends |= Completes(c.Body, flow);
                if (t.Finally is not null && !Completes(t.Finally, flow) && !flow.Roslyn) return false;
                return ends;
            }
            case DeconstructStmt or UsingDeclStmt or AsmStmt:
                // On to the next statement: none holds one of its own (an asm
                // block may not leave its method: InlineAsm).
                return true;
            default:
                // A statement this walk does not know may hold a return.
                flow.Unsure = true;
                return true;
        }
    }

    /// <summary>A loop's body walked as a break and continue target: whether a break leaves it, whether a continue was reached, and whether its end can be.</summary>
    private (bool Breaks, bool Continues, bool BodyEnds) LoopBody(Stmt body, Flow flow)
    {
        flow.Targets.Add((false, false, true));
        bool ends = Completes(body, flow);
        (bool breaks, bool continues, _) = flow.Targets[^1];
        flow.Targets.RemoveAt(flow.Targets.Count - 1);
        return (breaks, continues, ends);
    }
}
