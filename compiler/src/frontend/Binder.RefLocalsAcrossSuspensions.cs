#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A REF LOCAL IN AN ASYNC METHOD OR AN ITERATOR, as C# 13 allows one: used
/// anywhere its binding has not crossed an `await` or a `yield return`, and
/// refused (CS9217) at each use a suspension since its binding reaches. Its
/// address names a variable in this frame or in a heap object the frame
/// alone may hold, and a suspension keeps neither: the frame is gone, and the
/// machine saves no address into the code's own data (AsyncTransform saves
/// only what is live, and a ref local after this check never is).
///
/// A walk of the body's paths, as the lock walk's (Binder.AwaitChecks): what
/// is crossed is a set of the body's ref locals, every one of them added at a
/// suspension -- an await, an `await using`'s dispose or an `await foreach`'s
/// step, as the binder wrote them out, and a yield after its value -- and one
/// taken out where it is bound again: its declaration, each time round a loop
/// it is declared in, and `r = ref other;`. A use of one in the set is the
/// error. Paths join by union, loops go round to a fixed point, a finally is
/// walked on the way out of what it guards, and `&&`, `||`, `??`, `?:`, a
/// switch expression's arms and a `?.` chain each take their own paths, so an
/// await a path never reaches crosses nothing on it.
///
/// AND AS A VARIABLE ACROSS AN AWAIT LATER IN ITS OWN EXPRESSION, as Roslyn
/// refuses it (probes ~/tmp/dn/asyncref4, asyncref6): an `await` after a ref
/// local in one expression keeps what was taken before it, and where that is
/// the variable -- the local, or a struct's field reached through it -- the
/// reference is what crosses (SpanningUse): the local assigned
/// (`r = await F()`), a compound assignment's target (`r += await F()`,
/// `s.X += await F()`), the receiver a value type's method is called on
/// (`r.CompareTo(await F())`, `s.Inner.Get(await F())`), an argument passed
/// by reference to a method or a constructor (`N(ref s.X, await F())`). A
/// value read (`r + await F()`, `M(r, await F())`), a field of it assigned
/// (`s.X = await F()`), a class's method called through it, and an await
/// inside a lambda the expression only makes, cross nothing.
///
/// A lambda and a local function are bodies of their own, walked when they
/// are bound (an async or iterator one); a ref local is never captured
/// (RefuseCapturedRefLocals), so no use reaches across.
/// </summary>
public sealed partial class Binder
{
    /// <summary>Whether the body being bound declared a ref local: the walk is made only then.</summary>
    private bool _sawRefLocal;

    /// <summary>The uses already refused, so a body bound twice (a generic copy) says each once.</summary>
    private readonly HashSet<Node> _refAcrossReported = new(ReferenceEqualityComparer.Instance);

    /// <summary>The ref locals whose binding a suspension has crossed, on the paths to a point; Dead where none reach.</summary>
    private readonly struct Crossed
    {
        public readonly bool Dead;
        public readonly ulong[]? Bits;
        public Crossed(bool dead, ulong[]? bits) { Dead = dead; Bits = bits; }
    }

    private static readonly Crossed DeadCrossed = new(true, null);

    private sealed class CrossWalk
    {
        public readonly Dictionary<LocalSym, int> Index = new(ReferenceEqualityComparer.Instance);
        public int Words;
        // What reaches each label by a goto, gathered round after round
        // until no label gains (a goto back over a suspension).
        public readonly Dictionary<string, Crossed> Labels = new(StringComparer.Ordinal);
        public readonly List<CrossLoop> Loops = new();
        public readonly List<Block> Finallys = new();
        public readonly List<Crossed> Tries = new();
    }

    private sealed class CrossLoop
    {
        public Crossed Break = DeadCrossed;
        public Crossed Continue = DeadCrossed;
        // A switch's: what `goto case` and `goto default` bring to its cases.
        public Crossed CaseGotos = DeadCrossed;
        public int Finallys;
        public bool Switch;
    }

    private static Crossed JoinCrossed(Crossed a, Crossed b)
    {
        if (a.Dead) return b;
        if (b.Dead) return a;
        if (a.Bits is null) return b;
        if (b.Bits is null) return a;
        ulong[] made = new ulong[a.Bits.Length];
        for (int i = 0; i < made.Length; i++) made[i] = a.Bits[i] | b.Bits[i];
        return new Crossed(false, made);
    }

    private static bool SameCrossed(Crossed a, Crossed b)
    {
        if (a.Dead || b.Dead) return a.Dead == b.Dead;
        for (int i = 0; i < Math.Max(a.Bits?.Length ?? 0, b.Bits?.Length ?? 0); i++)
        {
            ulong x = a.Bits is { } ab && i < ab.Length ? ab[i] : 0;
            ulong y = b.Bits is { } bb && i < bb.Length ? bb[i] : 0;
            if (x != y) return false;
        }
        return true;
    }

    /// <summary>
    /// Every ref local crossed: a suspension. Seen by every try around it at
    /// once: what the awaited task throws comes out after the resumption, in
    /// the middle of a statement whose end may never come (`return await F();`).
    /// </summary>
    private static Crossed Suspended(Crossed c, CrossWalk w)
    {
        if (c.Dead) return c;
        ulong[] all = new ulong[w.Words];
        for (int i = 0; i < w.Index.Count; i++) all[i >> 6] |= 1UL << (i & 63);
        Crossed crossed = new(false, all);
        for (int open = 0; open < w.Tries.Count; open++) w.Tries[open] = JoinCrossed(w.Tries[open], crossed);
        return crossed;
    }

    /// <summary>One ref local bound afresh.</summary>
    private static Crossed Rebound(Crossed c, int index)
    {
        if (c.Dead || c.Bits is null || (c.Bits[index >> 6] & (1UL << (index & 63))) == 0) return c;
        ulong[] made = (ulong[])c.Bits.Clone();
        made[index >> 6] &= ~(1UL << (index & 63));
        return new Crossed(false, made);
    }

    private static bool IsCrossed(Crossed c, int index)
        => !c.Dead && c.Bits is { } bits && (bits[index >> 6] & (1UL << (index & 63))) != 0;

    /// <summary>
    /// The check of one async or iterator body, once it is bound: a method's
    /// (EndMethodBody) or a lambda's or local function's (CheckLambda).
    /// </summary>
    private void CheckRefLocalsAcrossSuspensions(Node body)
    {
        CrossWalk w = new();
        foreach (Node n in SafetyNodes(body))
        {
            if (n is LocalDecl { IsRef: true } d && _r.LocalSymbols.TryGetValue(d, out LocalSym? made) && !w.Index.ContainsKey(made))
            {
                w.Index[made] = w.Index.Count;
            }
        }
        if (w.Index.Count == 0) return;
        w.Words = (w.Index.Count + 63) >> 6;
        // Round after round while a label gains from a goto later in the
        // text; each round only adds, so the rounds end.
        for (int round = 0; round <= w.Index.Count + 2; round++)
        {
            Dictionary<string, Crossed> before = new(w.Labels, StringComparer.Ordinal);
            Crossed start = new(false, new ulong[w.Words]);
            if (body is Stmt s) WalkCrossStmt(s, start, w);
            else if (body is Expr e) WalkCrossExpr(e, start, w);
            bool same = before.Count == w.Labels.Count;
            foreach ((string label, Crossed now) in w.Labels)
            {
                if (!same) break;
                same = before.TryGetValue(label, out Crossed was) && SameCrossed(was, now);
            }
            if (same) break;
        }
    }

    private static Crossed LabelIn(CrossWalk w, string label) => w.Labels.TryGetValue(label, out Crossed c) ? c : DeadCrossed;

    private void ReportCrossed(NameExpr use)
    {
        // Not while quiet (a lambda's first, trial binding): said, and kept
        // from being said again, only where it is heard.
        if (_quiet > 0) return;
        if (_refAcrossReported.Add(use))
        {
            Error(use, $"CS9217: '{use.Name}' is a ref local, and an 'await' or 'yield' since it was bound would keep it across a suspension: "
                     + "a 'ref' local cannot be preserved across 'await' or 'yield' boundary");
        }
    }

    private Crossed WalkCrossStmt(Stmt s, Crossed c, CrossWalk w)
    {
        if (c.Dead)
        {
            if (s is LabeledStmt label && !LabelIn(w, label.Label).Dead) return WalkCrossStmt(label.Body, LabelIn(w, label.Label), w);
            if (s is Block dead)
            {
                foreach (Stmt one in dead.Statements) c = WalkCrossStmt(one, c, w);
            }
            return c;
        }
        for (int open = 0; open < w.Tries.Count; open++) w.Tries[open] = JoinCrossed(w.Tries[open], c);

        switch (s)
        {
            case Block b:
                foreach (Stmt one in b.Statements) c = WalkCrossStmt(one, c, w);
                return c;

            case LocalDecl d:
            {
                // A local function is a body of its own.
                if (!(d.LocalFunction && d.Init is LambdaExpr) && d.Init is not null)
                {
                    c = WalkCrossExpr(d.Init, c, w);
                }
                if (d.IsRef && _r.LocalSymbols.TryGetValue(d, out LocalSym? bound) && w.Index.TryGetValue(bound, out int index))
                {
                    c = Rebound(c, index);
                }
                foreach (LocalDecl also in d.Also) c = WalkCrossStmt(also, c, w);
                return c;
            }

            case UsingDeclStmt u:
                return WalkCrossStmt(u.Declaration, c, w);

            case ExprStmt e:
                return WalkCrossExpr(e.Expr, c, w);

            case IfStmt i:
            {
                WalkCrossCondition(i.Cond, c, w, out Crossed yes, out Crossed no);
                Crossed then = WalkCrossStmt(i.Then, yes, w);
                Crossed otherwise = i.Else is not null ? WalkCrossStmt(i.Else, no, w) : no;
                return JoinCrossed(then, otherwise);
            }

            case WhileStmt loop:
                return WalkCrossLoop(c, w, loop.Cond, loop.Body, null, false);

            case DoStmt loop:
                return WalkCrossLoop(c, w, loop.Cond, loop.Body, null, true);

            case ForStmt loop:
                if (loop.Init is not null) c = WalkCrossStmt(loop.Init, c, w);
                return WalkCrossLoop(c, w, loop.Cond, loop.Body, loop.Step, false);

            case ForeachStmt fe:
                if (_r.Lowered.TryGetValue(fe, out Stmt? lowered)) return WalkCrossStmt(lowered, c, w);
                c = WalkCrossExpr(fe.Sequence, c, w);
                return WalkCrossLoop(c, w, null, fe.Body, null, false);

            case DeconstructStmt ds:
                return _r.Lowered.TryGetValue(ds, out Stmt? taken) ? WalkCrossStmt(taken, c, w) : c;

            case ReturnStmt r:
                if (r.Value is not null) c = WalkCrossExpr(r.Value, c, w);
                CrossThroughFinallys(c, w, 0);
                return DeadCrossed;

            case YieldStmt y:
                if (y.Value is null)
                {
                    CrossThroughFinallys(c, w, 0);
                    return DeadCrossed;
                }
                // The value is read before the iterator hands it out.
                return Suspended(WalkCrossExpr(y.Value, c, w), w);

            case ThrowStmt t:
                if (t.Value is not null) WalkCrossExpr(t.Value, c, w);
                return DeadCrossed;

            case SwitchStmt sw:
            {
                c = WalkCrossExpr(sw.Subject, c, w);
                // Round again while a `goto case` brings a case more than it had.
                Crossed caseGotos = DeadCrossed;
                Crossed ends = DeadCrossed;
                CrossLoop frame = new() { Switch = true, Finallys = w.Finallys.Count };
                bool hasDefault = false;
                for (int round = 0; round <= w.Index.Count + 1; round++)
                {
                    frame = new CrossLoop { Switch = true, Finallys = w.Finallys.Count };
                    w.Loops.Add(frame);
                    ends = DeadCrossed;
                    hasDefault = false;
                    foreach (SwitchCase one in sw.Cases)
                    {
                        Crossed at = c;
                        if (one.Pattern is null) hasDefault = true;
                        else at = WalkCrossExpr(one.Pattern, at, w);
                        at = JoinCrossed(at, caseGotos);
                        foreach (Stmt inner in one.Body) at = WalkCrossStmt(inner, at, w);
                        ends = JoinCrossed(ends, at);
                    }
                    w.Loops.RemoveAt(w.Loops.Count - 1);
                    Crossed again = JoinCrossed(caseGotos, frame.CaseGotos);
                    if (SameCrossed(again, caseGotos)) break;
                    caseGotos = again;
                }
                if (!hasDefault) ends = JoinCrossed(ends, c);
                return JoinCrossed(ends, frame.Break);
            }

            case TryStmt ts:
            {
                w.Tries.Add(c);
                if (ts.Finally is not null) w.Finallys.Add(ts.Finally);
                int tryAt = w.Tries.Count - 1;
                Crossed body = WalkCrossStmt(ts.Body, c, w);
                // What a catch may begin with: any point of the body, not
                // another catch's (no catch takes what its sibling throws).
                Crossed thrown = JoinCrossed(w.Tries[tryAt], body);
                w.Tries[tryAt] = DeadCrossed;
                Crossed ends = body;
                foreach (CatchClause cc in ts.Catches)
                {
                    Crossed at = thrown;
                    if (cc.When is not null) at = WalkCrossExpr(cc.When, at, w);
                    ends = JoinCrossed(ends, WalkCrossStmt(cc.Body, at, w));
                }
                // And what the finally may begin with on the way out of an
                // exception: the body's, or a catch's (gathered in the slot).
                Crossed failing = JoinCrossed(thrown, w.Tries[tryAt]);
                w.Tries.RemoveAt(tryAt);
                if (ts.Finally is null) return ends;
                w.Finallys.RemoveAt(w.Finallys.Count - 1);
                // On the way out of an exception (it goes on after), and on the normal way out.
                WalkCrossStmt(ts.Finally, JoinCrossed(failing, ends), w);
                return WalkCrossStmt(ts.Finally, ends, w);
            }

            case BreakStmt:
                if (w.Loops.Count > 0)
                {
                    CrossLoop frame = w.Loops[^1];
                    frame.Break = JoinCrossed(frame.Break, CrossThroughFinallys(c, w, frame.Finallys));
                }
                return DeadCrossed;

            case ContinueStmt:
                for (int i = w.Loops.Count - 1; i >= 0; i--)
                {
                    if (!w.Loops[i].Switch)
                    {
                        w.Loops[i].Continue = JoinCrossed(w.Loops[i].Continue, CrossThroughFinallys(c, w, w.Loops[i].Finallys));
                        break;
                    }
                }
                return DeadCrossed;

            // To a case of the switch it is in: its cases begin with it too.
            case GotoCaseStmt gc:
                if (gc.Value is not null) c = WalkCrossExpr(gc.Value, c, w);
                for (int i = w.Loops.Count - 1; i >= 0; i--)
                {
                    if (w.Loops[i].Switch)
                    {
                        w.Loops[i].CaseGotos = JoinCrossed(w.Loops[i].CaseGotos, CrossThroughFinallys(c, w, w.Loops[i].Finallys));
                        break;
                    }
                }
                return DeadCrossed;

            // Out through every finally open, as a jump out of them: sound
            // wherever the label is.
            case GotoStmt g:
                w.Labels[g.Label] = JoinCrossed(LabelIn(w, g.Label), CrossThroughFinallys(c, w, 0));
                return DeadCrossed;

            case LabeledStmt l:
                return WalkCrossStmt(l.Body, JoinCrossed(c, LabelIn(w, l.Label)), w);

            default:
            {
                int from = _safety.Count;
                Expr? marked = AddSafetyChildren(s, _safety);
                int to = _safety.Count;
                try
                {
                    for (int i = from; i < to; i++)
                    {
                        Node child = _safety[i];
                        if (child is Stmt inner) c = WalkCrossStmt(inner, c, w);
                        else if (child is Expr e) c = WalkCrossExpr(e, c, w);
                    }
                }
                finally { Unwalked(from, marked); }
                return c;
            }
        }
    }

    /// <summary>A loop to a fixed point: the condition, the body, the steps, round again.</summary>
    private Crossed WalkCrossLoop(Crossed c, CrossWalk w, Expr? cond, Stmt body, List<Expr>? step, bool bodyFirst)
    {
        Crossed entry = c;
        Crossed leave = DeadCrossed;
        // Each round only adds to what is crossed: at most one round a ref local, and one more to see it settle.
        for (int round = 0; round <= w.Index.Count + 1; round++)
        {
            CrossLoop frame = new() { Finallys = w.Finallys.Count };
            Crossed yes, no, ran;
            if (bodyFirst)
            {
                w.Loops.Add(frame);
                ran = WalkCrossStmt(body, entry, w);
                w.Loops.RemoveAt(w.Loops.Count - 1);
                ran = JoinCrossed(ran, frame.Continue);
                if (cond is not null) WalkCrossCondition(cond, ran, w, out yes, out no);
                else { yes = ran; no = DeadCrossed; }
                leave = JoinCrossed(no, frame.Break);
                Crossed again = JoinCrossed(c, yes);
                if (SameCrossed(again, entry)) break;
                entry = again;
            }
            else
            {
                if (cond is not null) WalkCrossCondition(cond, entry, w, out yes, out no);
                else { yes = entry; no = DeadCrossed; }
                w.Loops.Add(frame);
                ran = WalkCrossStmt(body, yes, w);
                w.Loops.RemoveAt(w.Loops.Count - 1);
                ran = JoinCrossed(ran, frame.Continue);
                if (step is not null)
                {
                    foreach (Expr one in step) ran = WalkCrossExpr(one, ran, w);
                }
                leave = JoinCrossed(no, frame.Break);
                Crossed again = JoinCrossed(c, ran);
                if (SameCrossed(again, entry)) break;
                entry = again;
            }
        }
        return leave;
    }

    /// <summary>A jump out through the finally blocks opened since `depth`, innermost first.</summary>
    private Crossed CrossThroughFinallys(Crossed c, CrossWalk w, int depth)
    {
        if (w.Finallys.Count <= depth || c.Dead) return c;
        List<Block> open = new(w.Finallys);
        try
        {
            for (int i = open.Count - 1; i >= depth && !c.Dead; i--)
            {
                w.Finallys.Clear();
                for (int k = 0; k < i; k++) w.Finallys.Add(open[k]);
                c = WalkCrossStmt(open[i], c, w);
            }
        }
        finally
        {
            w.Finallys.Clear();
            w.Finallys.AddRange(open);
        }
        return c;
    }

    /// <summary>A condition, and what is crossed where it says yes and where it says no.</summary>
    private void WalkCrossCondition(Expr e, Crossed c, CrossWalk w, out Crossed yes, out Crossed no)
    {
        if (_r.Rewrites.TryGetValue(e, out Expr? instead) && !ReferenceEquals(instead, e) && _inOwnRewrite.Add(e))
        {
            try { WalkCrossCondition(instead, c, w, out yes, out no); }
            finally { _inOwnRewrite.Remove(e); }
            return;
        }
        switch (e)
        {
            case UnaryExpr { Op: UnOp.Not } negated:
                WalkCrossCondition(negated.Operand, c, w, out no, out yes);
                return;
            case BinaryExpr { Op: BinOp.AndAlso } both:
            {
                WalkCrossCondition(both.Left, c, w, out Crossed leftYes, out Crossed leftNo);
                WalkCrossCondition(both.Right, leftYes, w, out Crossed rightYes, out Crossed rightNo);
                yes = rightYes;
                no = JoinCrossed(leftNo, rightNo);
                return;
            }
            case BinaryExpr { Op: BinOp.OrElse } either:
            {
                WalkCrossCondition(either.Left, c, w, out Crossed leftYes, out Crossed leftNo);
                WalkCrossCondition(either.Right, leftNo, w, out Crossed rightYes, out Crossed rightNo);
                yes = JoinCrossed(leftYes, rightYes);
                no = rightNo;
                return;
            }
            default:
                yes = no = WalkCrossExpr(e, c, w);
                return;
        }
    }

    /// <summary>Whether an expression is a link of a `?.` chain: a member, call or index read of what is before it.</summary>
    private static bool ChainLink(Expr e) => e is MemberExpr or CallExpr or IndexExpr;

    /// <summary>The ref local of this body a name is, or -1.</summary>
    private int RefLocalOf(Expr e, CrossWalk w)
        => e is NameExpr n && _r.Resolved.TryGetValue(n, out Sym? named) && named is LocalSym { IsRef: true } refLocal
           && w.Index.TryGetValue(refLocal, out int index) ? index : -1;

    /// <summary>Whether a value of this type is no reference: a call on one is made on the variable itself.</summary>
    private static bool ValueTyped(Type t)
        => !t.IsArray && !t.IsPointer && !t.IsReference && t.Prim is not (Prim.Any or Prim.String or Prim.Type)
           && t.Symbol is not { Kind: TypeKind.Class or TypeKind.Interface };

    /// <summary>
    /// The ref local a variable is, or holds as a struct's field (`s`,
    /// `s.X`, `s.Inner.Y`): the name it is reached through, or null.
    /// </summary>
    private NameExpr? RefLocalRoot(Expr e, CrossWalk w)
    {
        while (e is MemberExpr { Target: { } inner } && _r.TypeOf(inner) is { } held && ValueTyped(held)) e = inner;
        return e is NameExpr n && RefLocalOf(n, w) >= 0 ? n : null;
    }

    /// <summary>Whether an expression awaits in this body: an await inside a lambda it only makes is the lambda's.</summary>
    private bool AwaitsHere(Expr e)
    {
        foreach (Node n in SafetyNodes(e))
        {
            if (n is AwaitExpr) return true;
        }
        return false;
    }

    /// <summary>
    /// A ref local used as the variable it is, by an expression whose own
    /// later part awaits: the reference is held across the suspension.
    /// </summary>
    private void SpanningUse(Expr e, CrossWalk w)
    {
        switch (e)
        {
            // The local assigned, or any compound assignment's target through it.
            case AssignExpr assign when assign.Value is not RefArgExpr && AwaitsHere(assign.Value)
                && (assign.Op is null ? (assign.Target is NameExpr own && RefLocalOf(own, w) >= 0 ? own : null)
                                      : RefLocalRoot(assign.Target, w)) is { } target:
                ReportCrossed(target);
                return;
            case CallExpr call:
            {
                // A value type's method, called on the variable itself.
                if (call.Target is MemberExpr { Target: { } receiver } && _r.TypeOf(receiver) is { } receiverType && ValueTyped(receiverType)
                    && RefLocalRoot(receiver, w) is { } root && call.Args.Any(AwaitsHere))
                {
                    ReportCrossed(root);
                }
                ByReferenceAcross(call.Args, _r.Calls.TryGetValue(call, out MethodSymbol? named) ? named : null, w);
                return;
            }
            case NewExpr made:
                ByReferenceAcross(made.Args, _r.NewConstructors.TryGetValue(made, out MethodSymbol? ctor) ? ctor : null, w);
                return;
        }
    }

    /// <summary>An argument passed by reference -- `ref`, `out`, `in`, or to an `in` parameter -- before one that awaits.</summary>
    private void ByReferenceAcross(IReadOnlyList<Expr> args, MethodSymbol? callee, CrossWalk w)
    {
        for (int i = 0; i < args.Count; i++)
        {
            Expr arg = args[i];
            Expr? passed = arg is RefArgExpr { Target: { } written } ? written
                : callee is not null && i < callee.Params.Count && callee.Params[i].ReadOnly ? arg : null;
            if (passed is null || RefLocalRoot(passed, w) is not { } root) continue;
            for (int later = i + 1; later < args.Count; later++)
            {
                if (AwaitsHere(args[later])) { ReportCrossed(root); break; }
            }
        }
    }

    private Crossed WalkCrossExpr(Expr e, Crossed c, CrossWalk w)
    {
        if (c.Dead) return c;
        SpanningUse(e, w);
        if (_r.Rewrites.TryGetValue(e, out Expr? instead) && !ReferenceEquals(instead, e) && _inOwnRewrite.Add(e))
        {
            try { return WalkCrossExpr(instead, c, w); }
            finally { _inOwnRewrite.Remove(e); }
        }
        switch (e)
        {
            case LambdaExpr:
                // A body of its own, walked when it is bound.
                return c;

            case AwaitExpr a:
                return Suspended(WalkCrossExpr(a.Operand, c, w), w);

            case NameExpr n:
                if (_r.Resolved.TryGetValue(n, out Sym? named) && named is LocalSym { IsRef: true } refLocal
                    && w.Index.TryGetValue(refLocal, out int used) && IsCrossed(c, used))
                {
                    ReportCrossed(n);
                }
                return c;

            // `r = ref other;`: bound afresh once the variable is found, and
            // the name on the left is no use of what it named before.
            case AssignExpr { Op: null, Value: RefArgExpr { IsOut: false, Name: null } referred, Target: NameExpr pointed }
                when _r.Resolved.TryGetValue(pointed, out Sym? target) && target is LocalSym { IsRef: true } repointed
                     && w.Index.TryGetValue(repointed, out int index):
                return Rebound(WalkCrossExpr(referred, c, w), index);

            case ThrowExpr t:
                WalkCrossExpr(t.Value, c, w);
                return DeadCrossed;

            case BinaryExpr { Op: BinOp.AndAlso or BinOp.OrElse }:
            {
                WalkCrossCondition(e, c, w, out Crossed yes, out Crossed no);
                return JoinCrossed(yes, no);
            }

            // The right side only where the left is null.
            case BinaryExpr { Op: BinOp.Coalesce } coalesce:
            {
                Crossed left = WalkCrossExpr(coalesce.Left, c, w);
                return JoinCrossed(left, WalkCrossExpr(coalesce.Right, left, w));
            }

            case ConditionalExpr ce:
            {
                WalkCrossCondition(ce.Cond, c, w, out Crossed yes, out Crossed no);
                return JoinCrossed(WalkCrossExpr(ce.Then, yes, w), WalkCrossExpr(ce.Else, no, w));
            }

            case SwitchExpr sx:
            {
                c = WalkCrossExpr(sx.Subject, c, w);
                Crossed ends = DeadCrossed;
                foreach (SwitchArm arm in sx.Arms)
                {
                    Crossed at = c;
                    if (arm.Value is not null) at = WalkCrossExpr(arm.Value, at, w);
                    if (arm.When is not null) at = WalkCrossExpr(arm.When, at, w);
                    ends = JoinCrossed(ends, WalkCrossExpr(arm.Result, at, w));
                }
                return ends.Dead ? c : ends;
            }

            // A `?.` CHAIN: everything after a null-conditional link is skipped
            // where what it reads is null, to the chain's end.
            case MemberExpr or CallExpr or IndexExpr:
            {
                List<Crossed>? skipped = null;
                Crossed ran = WalkCrossChain(e, c, w, ref skipped);
                if (skipped is not null)
                {
                    foreach (Crossed one in skipped) ran = JoinCrossed(ran, one);
                }
                return ran;
            }

            default:
            {
                int from = _safety.Count;
                Expr? marked = AddSafetyChildren(e, _safety);
                int to = _safety.Count;
                try
                {
                    for (int i = from; i < to; i++)
                    {
                        Node child = _safety[i];
                        if (child is Expr inner) c = WalkCrossExpr(inner, c, w);
                        else if (child is Stmt s) c = WalkCrossStmt(s, c, w);
                    }
                }
                finally { Unwalked(from, marked); }
                return c;
            }
        }
    }

    /// <summary>One link of a `?.` chain and those before it, noting the state where each null-conditional link skips the rest.</summary>
    private Crossed WalkCrossChain(Expr e, Crossed c, CrossWalk w, ref List<Crossed>? skipped)
    {
        if (c.Dead) return c;
        // A rewritten link is walked as itself: its chain is the rewrite's.
        if (_r.Rewrites.TryGetValue(e, out Expr? instead) && !ReferenceEquals(instead, e))
        {
            return WalkCrossExpr(e, c, w);
        }
        SpanningUse(e, w);
        Crossed Before(Expr target, ref List<Crossed>? skips)
            => ChainLink(target) ? WalkCrossChain(target, c, w, ref skips) : WalkCrossExpr(target, c, w);
        switch (e)
        {
            case MemberExpr m:
            {
                Crossed at = Before(m.Target, ref skipped);
                if (m.NullConditional) (skipped ??= new()).Add(at);
                return at;
            }
            case IndexExpr ix:
            {
                Crossed at = Before(ix.Target, ref skipped);
                if (ix.NullConditional) (skipped ??= new()).Add(at);
                foreach (Expr arg in ix.Args) at = WalkCrossExpr(arg, at, w);
                return at;
            }
            case CallExpr call:
            {
                Crossed at = Before(call.Target, ref skipped);
                foreach (Expr arg in call.Args) at = WalkCrossExpr(arg, at, w);
                return at;
            }
            default:
                return WalkCrossExpr(e, c, w);
        }
    }
}
