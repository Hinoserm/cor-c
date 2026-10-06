#nullable enable
namespace Corsac.Lang;

/// <summary>
/// THE SAFETY CHECKS AROUND `await` (docs/X86-BACKEND.md, "Async safety").
///
/// NO AWAIT WHILE A LOCK IS HELD. C# refuses `await` inside a `lock`
/// (CS1996): the continuation may run on another thread, which does not own
/// the monitor, and the thread that does goes on holding it. CORSAC's paired
/// Enter/Leave locks need the same rule and more: a spin lock held across a
/// suspension spins every other taker until the continuation runs, which on
/// one processor is never, and IoOwnership records a process that is no
/// longer the holder. So from an Enter to the Leave that ends it, on every
/// path -- try/finally, loops, branches, early returns -- an `await` is an
/// error.
///
/// What is a lock: IrqSpinLock, KernelGate, Ring1Lock, IoOwnership (and its
/// IFilesystemGuard), GcLock, Atom, System.Threading.Monitor (what `lock`
/// expands to) and SpinLock -- and any type, or a type deriving from or
/// implementing one, marked [NoAwaitWhileHeld]. Its Enter, EnterInterruptible,
/// EnterPair and TryEnter take it; Leave, Exit and LeavePair give it back.
/// TryEnter (and Enter on IoOwnership, and on a type marked
/// [NoAwaitWhileHeld(EnterMayFail = true)]) answers whether it took it, so
/// `if (g.TryEnter())` holds it in the branch that says so only.
///
/// A METHOD THAT TAKES A LOCK AND RETURNS HOLDING IT is a lock of its own to
/// its callers: `EnterSeat()` around `_seatGate.Enter()`. Every method body of
/// the unit is summarised -- what it leaves taken, or gives back, on its way
/// out -- and a call to it counts as that. Summaries are of what the unit
/// compiles from source; a library method compiled elsewhere is neither.
///
/// What the analysis does not see: a lock taken through a delegate or a
/// virtual call to an override other than the one the call names, a lock
/// handed from method to method in a field, and a `goto` backwards over an
/// Enter. A lock that is never left counts as held for the rest of the
/// method.
///
/// INTERRUPT HANDLERS. A method marked [InterruptHandler], or one that
/// implements an interface method so marked or IIrqHandler.OnIrq, runs with
/// interrupts off on the interrupt stack: it may not await, and it may not
/// allocate. What is refused, in the handler and in every method it calls
/// directly (a static or non-virtual call) whose body this unit compiles,
/// transitively: `new` of a class, an array or a delegate; joining strings;
/// boxing; a lambda or a method group made a delegate; and a call to an
/// async method (its state machine and task). Not seen: an allocation inside
/// a library method compiled elsewhere, behind a virtual, interface or
/// delegate call, or made by the runtime itself (an exception the runtime
/// throws, a collection's own growth inside a library body not compiled
/// here).
/// </summary>
public sealed partial class Binder
{
    /// <summary>Every method body checked in this unit, for the unit-wide checks after binding.</summary>
    private readonly List<(MethodSymbol Method, MethodDecl Decl)> _boundBodies = new();

    /// <summary>What each method body leaves taken (positive) or gives back (negative) on its way out.</summary>
    private readonly Dictionary<MethodSymbol, int> _lockSummary = new(ReferenceEqualityComparer.Instance);

    /// <summary>The await and allocation diagnostics already made, so a node is reported once.</summary>
    private readonly HashSet<Node> _asyncSafetyReported = new(ReferenceEqualityComparer.Instance);

    private const int LockDead = int.MinValue;
    private const int LockMost = 8, LockLeast = -4;

    /// <summary>The locks held at a point of a body: how many, and the latest taking, for the message.</summary>
    private struct HeldLocks
    {
        public int Count;
        public Node? Site;
        public string? Name;

        public bool Dead => Count == LockDead;
    }

    private static HeldLocks DeadLocks => new HeldLocks { Count = LockDead };

    private static HeldLocks JoinLocks(HeldLocks a, HeldLocks b)
    {
        if (a.Dead) return b;
        if (b.Dead) return a;
        return b.Count > a.Count ? b : a;
    }

    private static HeldLocks AddLocks(HeldLocks h, int by, Node? site, string? name)
    {
        if (h.Dead) return h;
        int count = h.Count + by;
        if (count > LockMost) count = LockMost;
        if (count < LockLeast) count = LockLeast;
        HeldLocks made = new HeldLocks { Count = count, Site = h.Site, Name = h.Name };
        if (by > 0)
        {
            made.Site = site;
            made.Name = name;
        }
        return made;
    }

    private static bool SameLocks(HeldLocks a, HeldLocks b) => a.Count == b.Count;

    private enum LockEffect { None, Take, TryTake, Give }

    /// <summary>One walk of one body: its exits, the loops and finally blocks open, and the lambdas met.</summary>
    private sealed class LockWalk
    {
        public bool Report;
        public HeldLocks Exit = DeadLocks;
        public HeldLocks Gotos = DeadLocks;
        public readonly List<LockLoop> Loops = new();
        public readonly List<Block> Finallys = new();
        public readonly List<HeldLocks> Tries = new();
        public readonly List<LambdaExpr> Lambdas = new();
        public readonly HashSet<string> TriedLocals = new(StringComparer.Ordinal);
    }

    private sealed class LockLoop
    {
        public HeldLocks Break = DeadLocks;
        public HeldLocks Continue = DeadLocks;
        public int Finallys;
        public bool Switch;
    }

    /// <summary>Called for every method body once it is bound.</summary>
    private void NoteBoundBody(MethodSymbol m, MethodDecl md)
    {
        if (md.Body is not null)
        {
            _boundBodies.Add((m, md));
        }
    }

    /// <summary>What one scan of a body found: whether it awaits (its lambdas too), and the methods it calls.</summary>
    private sealed class BodyFacts
    {
        public bool Awaits;
        public bool TakesLocks;
        public readonly List<MethodSymbol> Calls = new();
    }

    /// <summary>The unit-wide checks: locks held across an await, and interrupt handlers.</summary>
    /// <summary>Whether this unit bound an await or an async lambda (CheckExprCore): what CheckAsyncSafety has anything to find with.</summary>
    private bool _mayAwait;

    private void CheckAsyncSafety()
    {
        if (_boundBodies.Count == 0)
        {
            return;
        }
        // NO LOCK CAN BE HELD ACROSS AN AWAIT THAT IS NOT THERE: the scan of
        // every body for awaits, calls and locks, the locks' summaries and the
        // check across awaits only where this unit bound an await, an async
        // lambda or an async method. Walked anyway, every body of every unit
        // was a sixth of every byte compiling the compiler took.
        bool mayHoldAcross = _mayAwait || _boundBodies.Any(b => b.Item1.Async);
        string inWas = _in;
        MemberDecl? memberWas = _member;
        try
        {
            // ONE SCAN OF EVERY BODY: whether it awaits, what it calls, and
            // whether any of that is a lock's own method.
            Dictionary<MethodSymbol, BodyFacts> facts = new(ReferenceEqualityComparer.Instance);
            Dictionary<MethodSymbol, List<MethodSymbol>> callers = new(ReferenceEqualityComparer.Instance);
            foreach ((MethodSymbol m, MethodDecl d) in mayHoldAcross ? _boundBodies : new List<(MethodSymbol, MethodDecl)>())
            {
                BodyFacts found = new();
                foreach (Node n in SafetyNodes(d.Body!, intoLambdas: true))
                {
                    if (n is AwaitExpr || n is UsingDeclStmt { Async: true })
                    {
                        found.Awaits = true;
                    }
                    else if (n is CallExpr c && _r.Calls.TryGetValue(c, out MethodSymbol? callee))
                    {
                        found.Calls.Add(callee);
                        if (IsLockType(callee.Owner)) found.TakesLocks = true;
                        if (!callers.TryGetValue(callee, out List<MethodSymbol>? who))
                        {
                            who = new List<MethodSymbol>();
                            callers[callee] = who;
                        }
                        who.Add(m);
                    }
                }
                facts[m] = found;
            }
            Dictionary<MethodSymbol, MethodDecl> bodies = new(ReferenceEqualityComparer.Instance);
            foreach ((MethodSymbol m, MethodDecl d) in _boundBodies)
            {
                bodies[m] = d;
            }

            // WHAT EACH METHOD LEAVES TAKEN, to a fixed point: a wrapper of a
            // wrapper is a lock too. Only bodies that call a lock, or a method
            // whose summary is not zero, can have one; reported nothing -- the
            // walk that reports comes after, with every summary known.
            List<MethodSymbol> work = new();
            HashSet<MethodSymbol> queued = new(ReferenceEqualityComparer.Instance);
            foreach ((MethodSymbol m, MethodDecl unused) in _boundBodies)
            {
                if (facts.TryGetValue(m, out BodyFacts? taking) && taking.TakesLocks && Summarised(m) && queued.Add(m)) work.Add(m);
            }
            int steps = 0;
            while (work.Count > 0 && steps++ < 20000)
            {
                MethodSymbol m = work[^1];
                work.RemoveAt(work.Count - 1);
                queued.Remove(m);
                LockWalk w = new() { Report = false };
                HeldLocks end = WalkLockStmt(bodies[m].Body!, new HeldLocks(), w);
                HeldLocks exit = JoinLocks(w.Exit, end);
                int net = exit.Dead ? 0 : exit.Count;
                int had = _lockSummary.TryGetValue(m, out int known) ? known : 0;
                if (net == had)
                {
                    continue;
                }
                if (net == 0) _lockSummary.Remove(m);
                else _lockSummary[m] = net;
                if (callers.TryGetValue(m, out List<MethodSymbol>? who))
                {
                    foreach (MethodSymbol caller in who)
                    {
                        if (bodies.ContainsKey(caller) && Summarised(caller) && queued.Add(caller)) work.Add(caller);
                    }
                }
            }

            foreach ((MethodSymbol m, MethodDecl d) in _boundBodies)
            {
                if (!facts.TryGetValue(m, out BodyFacts? awaiting) || !awaiting.Awaits)
                {
                    continue;
                }
                _in = d.File is { Length: > 0 } own ? own : m.Owner.Decl?.File ?? _in;
                _member = d;
                CheckLocksAcrossAwait(d.Body!);
            }

            Dictionary<MethodSymbol, (Node Site, string What, MethodSymbol Where)?> allocates = new(ReferenceEqualityComparer.Instance);
            foreach ((MethodSymbol m, MethodDecl d) in _boundBodies)
            {
                if (!IsInterruptHandler(m))
                {
                    continue;
                }
                _in = d.File is { Length: > 0 } own ? own : m.Owner.Decl?.File ?? _in;
                _member = d;
                CheckInterruptHandler(m, d, bodies, allocates);
            }

            if (CollectInterruptFacts)
            {
                _r.InterruptFacts = InterruptFactsOf();
            }
        }
        finally
        {
            _in = inWas;
            _member = memberWas;
        }
    }

    /// <summary>
    /// Whether a method is summarised as a lock of its own: not a lock's own
    /// method (those are the locks), and not the compiler's own libraries,
    /// whose lock helpers -- the collector's, the scheduler's -- pair their
    /// takings in ways no caller awaits around, and whose every caller would
    /// otherwise pay for the analysis's imprecision in them.
    /// </summary>
    private bool Summarised(MethodSymbol m)
    {
        if (IsLockType(m.Owner))
        {
            return false;
        }
        string? path = m.Owner.Decl?.SourcePath;
        return path is null || LibrarySource is null || !LibrarySource(path);
    }

    // ---- no await while a lock is held ---------------------------------------------------

    /// <summary>A body, and every lambda in it, walked with nothing held on entry, reporting each await under a lock.</summary>
    private void CheckLocksAcrossAwait(Node body)
    {
        if (!HoldsAwait(body))
        {
            return;
        }
        List<Node> pending = new() { body };
        HashSet<Node> walked = new(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0)
        {
            Node next = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (!walked.Add(next) || !HoldsAwait(next))
            {
                continue;
            }
            LockWalk w = new() { Report = true };
            if (next is Stmt s)
            {
                WalkLockStmt(s, new HeldLocks(), w);
            }
            else if (next is Expr e)
            {
                WalkLockExpr(e, new HeldLocks(), w);
            }
            foreach (LambdaExpr l in w.Lambdas)
            {
                if (l.BlockBody is not null) pending.Add(l.BlockBody);
                else if (l.Body is not null) pending.Add(l.Body);
            }
        }
    }

    /// <summary>Whether `n` has an await of its own (not one inside a lambda it makes).</summary>
    private bool HoldsAwait(Node n)
    {
        if (n is AwaitExpr || n is UsingDeclStmt { Async: true })
        {
            return true;
        }
        int from = _safety.Count;
        Expr? marked = AddSafetyChildren(n, _safety);
        int to = _safety.Count;
        try
        {
            for (int i = from; i < to; i++)
            {
                Node c = _safety[i];
                if (c is LambdaExpr)
                {
                    // A lambda's own awaits are its own; but walking the body
                    // finds the lambda, which is walked by itself.
                    if (HoldsAwaitInLambda((LambdaExpr)c)) return true;
                    continue;
                }
                if (HoldsAwait(c)) return true;
            }
            return false;
        }
        finally { Unwalked(from, marked); }
    }

    /// <summary>
    /// The children of the nodes a recursive walk is inside, one slice per
    /// level (AddSafetyChildren): a level reads its own and gives them back.
    /// </summary>
    private readonly List<Node> _safety = new();

    private void Unwalked(int from, Expr? marked)
    {
        _safety.RemoveRange(from, _safety.Count - from);
        if (marked is not null) _inOwnRewrite.Remove(marked);
    }

    private bool HoldsAwaitInLambda(LambdaExpr l)
        => l.Async && (l.BlockBody is not null ? HoldsAwait(l.BlockBody) : l.Body is not null && HoldsAwait(l.Body));

    private void ReportAwaitUnderLock(AwaitExpr a, HeldLocks h, LockWalk w)
    {
        if (!w.Report || h.Dead || h.Count <= 0 || !_asyncSafetyReported.Add(a))
        {
            return;
        }
        if (h.Site is CallExpr { Args.Count: > 0 } c && c.Args[0] is NameExpr { Name: var held } && held.StartsWith("$lock$", StringComparison.Ordinal))
        {
            Error(a, "cannot await in the body of a lock statement: the continuation may run on another thread, which does not hold the lock (C# CS1996)");
            return;
        }
        string what = h.Name ?? "a lock";
        string taken = h.Site is null ? "" : $", taken at line {h.Site.Line},";
        Error(a, $"cannot await while {what} is held{taken} and not left on every path to here: a lock held across a suspension deadlocks, or names a holder that is no longer the one running; leave it first, or hold an awaitable lock (AsyncLock, SemaphoreSlim.WaitAsync) for a long hold");
    }

    private HeldLocks WalkLockStmt(Stmt s, HeldLocks h, LockWalk w)
    {
        if (h.Dead)
        {
            // Unreachable from the top -- but a label may still be reached.
            if (s is LabeledStmt label && !w.Gotos.Dead)
            {
                return WalkLockStmt(label.Body, w.Gotos, w);
            }
            if (s is Block dead)
            {
                foreach (Stmt one in dead.Statements)
                {
                    h = WalkLockStmt(one, h, w);
                }
            }
            return h;
        }
        for (int open = 0; open < w.Tries.Count; open++)
        {
            w.Tries[open] = JoinLocks(w.Tries[open], h);
        }

        switch (s)
        {
            case Block b:
                foreach (Stmt one in b.Statements)
                {
                    h = WalkLockStmt(one, h, w);
                }
                return h;

            case LocalDecl d:
            {
                if (d.LocalFunction && d.Init is LambdaExpr local)
                {
                    w.Lambdas.Add(local);
                }
                else if (d.Init is not null)
                {
                    if (d.Init is CallExpr c && LockEffectOf(c) == LockEffect.TryTake)
                    {
                        w.TriedLocals.Add(d.Name);
                    }
                    h = WalkLockExpr(d.Init, h, w);
                }
                foreach (LocalDecl also in d.Also)
                {
                    h = WalkLockStmt(also, h, w);
                }
                return h;
            }

            case UsingDeclStmt u:
                return WalkLockStmt(u.Declaration, h, w);

            case ExprStmt e:
                return WalkLockExpr(e.Expr, h, w);

            case IfStmt i:
            {
                WalkLockCondition(i.Cond, h, w, out HeldLocks yes, out HeldLocks no);
                HeldLocks then = WalkLockStmt(i.Then, yes, w);
                HeldLocks otherwise = i.Else is not null ? WalkLockStmt(i.Else, no, w) : no;
                return JoinLocks(then, otherwise);
            }

            case WhileStmt loop:
                return WalkLockLoop(h, w, loop.Cond, loop.Body, null, false);

            case DoStmt loop:
                return WalkLockLoop(h, w, loop.Cond, loop.Body, null, true);

            case ForStmt loop:
            {
                if (loop.Init is not null)
                {
                    h = WalkLockStmt(loop.Init, h, w);
                }
                return WalkLockLoop(h, w, loop.Cond, loop.Body, loop.Step, false);
            }

            case ForeachStmt fe:
            {
                if (_r.Lowered.TryGetValue(fe, out Stmt? lowered))
                {
                    return WalkLockStmt(lowered, h, w);
                }
                h = WalkLockExpr(fe.Sequence, h, w);
                return WalkLockLoop(h, w, null, fe.Body, null, false);
            }

            case DeconstructStmt ds:
                return _r.Lowered.TryGetValue(ds, out Stmt? taken) ? WalkLockStmt(taken, h, w) : h;

            case ReturnStmt r:
            {
                if (r.Value is not null)
                {
                    h = WalkLockExpr(r.Value, h, w);
                }
                h = ThroughFinallys(h, w, 0);
                w.Exit = JoinLocks(w.Exit, h);
                return DeadLocks;
            }

            case YieldStmt y:
                if (y.Value is null)
                {
                    h = ThroughFinallys(h, w, 0);
                    w.Exit = JoinLocks(w.Exit, h);
                    return DeadLocks;
                }
                return WalkLockExpr(y.Value, h, w);

            case ThrowStmt t:
                if (t.Value is not null)
                {
                    WalkLockExpr(t.Value, h, w);
                }
                return DeadLocks;

            case SwitchStmt sw:
            {
                h = WalkLockExpr(sw.Subject, h, w);
                LockLoop frame = new() { Switch = true, Finallys = w.Finallys.Count };
                w.Loops.Add(frame);
                HeldLocks ends = DeadLocks;
                bool hasDefault = false;
                foreach (SwitchCase c in sw.Cases)
                {
                    HeldLocks at = h;
                    if (c.Pattern is null)
                    {
                        hasDefault = true;
                    }
                    else
                    {
                        at = WalkLockExpr(c.Pattern, at, w);
                    }
                    foreach (Stmt one in c.Body)
                    {
                        at = WalkLockStmt(one, at, w);
                    }
                    ends = JoinLocks(ends, at);
                }
                w.Loops.RemoveAt(w.Loops.Count - 1);
                if (!hasDefault)
                {
                    ends = JoinLocks(ends, h);
                }
                return JoinLocks(ends, frame.Break);
            }

            case TryStmt ts:
            {
                w.Tries.Add(h);
                if (ts.Finally is not null)
                {
                    w.Finallys.Add(ts.Finally);
                }
                int tryAt = w.Tries.Count - 1;
                HeldLocks body = WalkLockStmt(ts.Body, h, w);
                HeldLocks observed = JoinLocks(w.Tries[tryAt], body);
                HeldLocks ends = body;
                foreach (CatchClause cc in ts.Catches)
                {
                    HeldLocks at = observed;
                    if (cc.When is not null)
                    {
                        at = WalkLockExpr(cc.When, at, w);
                    }
                    ends = JoinLocks(ends, WalkLockStmt(cc.Body, at, w));
                    observed = JoinLocks(observed, w.Tries[tryAt]);
                }
                w.Tries.RemoveAt(tryAt);
                if (ts.Finally is null)
                {
                    return ends;
                }
                w.Finallys.RemoveAt(w.Finallys.Count - 1);
                // The finally on the way out of an exception (it rethrows
                // after), and on the normal way out.
                WalkLockStmt(ts.Finally, JoinLocks(observed, ends), w);
                return WalkLockStmt(ts.Finally, ends, w);
            }

            case BreakStmt:
            {
                if (w.Loops.Count > 0)
                {
                    LockLoop frame = w.Loops[^1];
                    frame.Break = JoinLocks(frame.Break, ThroughFinallys(h, w, frame.Finallys));
                }
                return DeadLocks;
            }

            case ContinueStmt:
            {
                for (int i = w.Loops.Count - 1; i >= 0; i--)
                {
                    if (!w.Loops[i].Switch)
                    {
                        w.Loops[i].Continue = JoinLocks(w.Loops[i].Continue, ThroughFinallys(h, w, w.Loops[i].Finallys));
                        break;
                    }
                }
                return DeadLocks;
            }

            case GotoCaseStmt gc:
                if (gc.Value is not null)
                {
                    WalkLockExpr(gc.Value, h, w);
                }
                w.Gotos = JoinLocks(w.Gotos, h);
                return DeadLocks;

            case GotoStmt:
                w.Gotos = JoinLocks(w.Gotos, h);
                return DeadLocks;

            case LabeledStmt l:
                return WalkLockStmt(l.Body, JoinLocks(h, w.Gotos), w);

            default:
                {
                    int from = _safety.Count;
                    Expr? marked = AddSafetyChildren(s, _safety);
                    int to = _safety.Count;
                    try
                    {
                        for (int i = from; i < to; i++)
                        {
                            Node c = _safety[i];
                            if (c is Stmt inner) h = WalkLockStmt(inner, h, w);
                            else if (c is Expr e) h = WalkLockExpr(e, h, w);
                        }
                    }
                    finally { Unwalked(from, marked); }
                    return h;
                }
        }
    }

    /// <summary>A loop to a fixed point: the condition, the body, the steps, round again.</summary>
    private HeldLocks WalkLockLoop(HeldLocks h, LockWalk w, Expr? cond, Stmt body, List<Expr>? step, bool bodyFirst)
    {
        HeldLocks entry = h;
        HeldLocks leave = DeadLocks;
        LockLoop frame = new();
        for (int round = 0; round < 12; round++)
        {
            frame = new LockLoop { Finallys = w.Finallys.Count };
            HeldLocks yes, no;
            if (bodyFirst)
            {
                w.Loops.Add(frame);
                HeldLocks ran = WalkLockStmt(body, entry, w);
                w.Loops.RemoveAt(w.Loops.Count - 1);
                ran = JoinLocks(ran, frame.Continue);
                if (cond is not null) WalkLockCondition(cond, ran, w, out yes, out no);
                else { yes = ran; no = DeadLocks; }
                leave = JoinLocks(no, frame.Break);
                HeldLocks again = JoinLocks(h, yes);
                if (SameLocks(again, entry) || again.Dead) break;
                entry = again;
            }
            else
            {
                if (cond is not null) WalkLockCondition(cond, entry, w, out yes, out no);
                else { yes = entry; no = step is null && cond is null && body is not null ? entry : DeadLocks; }
                w.Loops.Add(frame);
                HeldLocks ran = WalkLockStmt(body, yes, w);
                w.Loops.RemoveAt(w.Loops.Count - 1);
                ran = JoinLocks(ran, frame.Continue);
                if (step is not null)
                {
                    foreach (Expr one in step) ran = WalkLockExpr(one, ran, w);
                }
                leave = JoinLocks(no, frame.Break);
                HeldLocks again = JoinLocks(h, ran);
                if (SameLocks(again, entry) || again.Dead) break;
                entry = again;
            }
        }
        return leave;
    }

    /// <summary>A jump out through the finally blocks opened since `depth`, innermost first.</summary>
    private HeldLocks ThroughFinallys(HeldLocks h, LockWalk w, int depth)
    {
        if (w.Finallys.Count <= depth || h.Dead)
        {
            return h;
        }
        List<Block> open = new(w.Finallys);
        try
        {
            for (int i = open.Count - 1; i >= depth && !h.Dead; i--)
            {
                w.Finallys.Clear();
                for (int k = 0; k < i; k++) w.Finallys.Add(open[k]);
                h = WalkLockStmt(open[i], h, w);
            }
        }
        finally
        {
            w.Finallys.Clear();
            w.Finallys.AddRange(open);
        }
        return h;
    }

    /// <summary>A condition, and what is held where it says yes and where it says no.</summary>
    private void WalkLockCondition(Expr c, HeldLocks h, LockWalk w, out HeldLocks yes, out HeldLocks no)
    {
        if (_r.Rewrites.TryGetValue(c, out Expr? instead) && !ReferenceEquals(instead, c) && _inOwnRewrite.Add(c))
        {
            try { WalkLockCondition(instead, h, w, out yes, out no); }
            finally { _inOwnRewrite.Remove(c); }
            return;
        }
        switch (c)
        {
            case UnaryExpr { Op: UnOp.Not } negated:
                WalkLockCondition(negated.Operand, h, w, out no, out yes);
                return;
            case BinaryExpr { Op: BinOp.AndAlso } both:
            {
                WalkLockCondition(both.Left, h, w, out HeldLocks leftYes, out HeldLocks leftNo);
                WalkLockCondition(both.Right, leftYes, w, out HeldLocks rightYes, out HeldLocks rightNo);
                yes = rightYes;
                no = JoinLocks(leftNo, rightNo);
                return;
            }
            case BinaryExpr { Op: BinOp.OrElse } either:
            {
                WalkLockCondition(either.Left, h, w, out HeldLocks leftYes, out HeldLocks leftNo);
                WalkLockCondition(either.Right, leftNo, w, out HeldLocks rightYes, out HeldLocks rightNo);
                yes = JoinLocks(leftYes, rightYes);
                no = rightNo;
                return;
            }
            case CallExpr call when LockEffectOf(call) == LockEffect.TryTake:
            {
                // Taken where it answers true; given back -- never taken -- where false.
                yes = WalkLockExpr(call, h, w);
                no = AddLocks(yes, -1, null, null);
                if (!no.Dead && no.Count == h.Count) no = h;
                return;
            }
            case NameExpr name when w.TriedLocals.Contains(name.Name):
                yes = h;
                no = AddLocks(h, -1, null, null);
                return;
            default:
                yes = no = WalkLockExpr(c, h, w);
                return;
        }
    }

    private HeldLocks WalkLockExpr(Expr e, HeldLocks h, LockWalk w)
    {
        if (h.Dead)
        {
            return h;
        }
        if (_r.Rewrites.TryGetValue(e, out Expr? instead) && !ReferenceEquals(instead, e) && _inOwnRewrite.Add(e))
        {
            try { return WalkLockExpr(instead, h, w); }
            finally { _inOwnRewrite.Remove(e); }
        }
        switch (e)
        {
            case LambdaExpr l:
                w.Lambdas.Add(l);
                return h;

            case AwaitExpr a:
                h = WalkLockExpr(a.Operand, h, w);
                ReportAwaitUnderLock(a, h, w);
                return h;

            case CallExpr c:
            {
                h = WalkLockExpr(c.Target, h, w);
                foreach (Expr arg in c.Args)
                {
                    h = WalkLockExpr(arg, h, w);
                }
                switch (LockEffectOf(c))
                {
                    case LockEffect.Take:
                    case LockEffect.TryTake:
                        return AddLocks(h, Math.Max(1, TakenBy(c)), c, LockName(c));
                    case LockEffect.Give:
                        return AddLocks(h, Math.Min(-1, TakenBy(c)), null, null);
                    default:
                        return h;
                }
            }

            case BinaryExpr { Op: BinOp.AndAlso or BinOp.OrElse }:
            {
                WalkLockCondition(e, h, w, out HeldLocks yes, out HeldLocks no);
                return JoinLocks(yes, no);
            }

            case ConditionalExpr c:
            {
                WalkLockCondition(c.Cond, h, w, out HeldLocks yes, out HeldLocks no);
                return JoinLocks(WalkLockExpr(c.Then, yes, w), WalkLockExpr(c.Else, no, w));
            }

            case SwitchExpr sx:
            {
                h = WalkLockExpr(sx.Subject, h, w);
                HeldLocks ends = DeadLocks;
                foreach (SwitchArm arm in sx.Arms)
                {
                    HeldLocks at = h;
                    if (arm.Value is not null) at = WalkLockExpr(arm.Value, at, w);
                    if (arm.When is not null) at = WalkLockExpr(arm.When, at, w);
                    ends = JoinLocks(ends, WalkLockExpr(arm.Result, at, w));
                }
                return ends.Dead ? h : ends;
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
                            Node c = _safety[i];
                            if (c is Expr inner) h = WalkLockExpr(inner, h, w);
                            else if (c is Stmt s) h = WalkLockStmt(s, h, w);
                        }
                    }
                    finally { Unwalked(from, marked); }
                    return h;
                }
        }
    }

    /// <summary>How many locks a call takes (positive) or gives back (negative): one for a lock's own method, the summary for a wrapper.</summary>
    private int TakenBy(CallExpr c)
    {
        if (!_r.Calls.TryGetValue(c, out MethodSymbol? m) || IsLockType(m.Owner))
        {
            return 1 * (LockEffectOf(c) == LockEffect.Give ? -1 : 1);
        }
        return _lockSummary.TryGetValue(m, out int net) ? net : 0;
    }

    private string LockName(CallExpr c)
    {
        if (!_r.Calls.TryGetValue(c, out MethodSymbol? m))
        {
            return "a lock";
        }
        return IsLockType(m.Owner) ? $"'{m.Owner.Name}' (its {m.Name})" : $"the lock '{m.Owner.Name}.{m.Name}' takes";
    }

    private LockEffect LockEffectOf(CallExpr c)
    {
        if (!_r.Calls.TryGetValue(c, out MethodSymbol? m))
        {
            return LockEffect.None;
        }
        if (IsLockType(m.Owner))
        {
            string name = m.Name;
            if (name is "Leave" or "Exit" or "LeavePair" or "ExitPair")
            {
                return LockEffect.Give;
            }
            if (name.StartsWith("TryEnter", StringComparison.Ordinal))
            {
                return LockEffect.TryTake;
            }
            if (name is "Enter" or "EnterInterruptible" or "EnterPair" or "ReliableEnter" or "EnterExclusive")
            {
                return m.Returns.Prim == Prim.Bool && EnterMayFail(m.Owner) ? LockEffect.TryTake : LockEffect.Take;
            }
            return LockEffect.None;
        }
        if (_lockSummary.TryGetValue(m, out int net))
        {
            if (net < 0)
            {
                return LockEffect.Give;
            }
            return m.Name.StartsWith("Try", StringComparison.Ordinal) && m.Returns.Prim == Prim.Bool ? LockEffect.TryTake : LockEffect.Take;
        }
        return LockEffect.None;
    }

    private static readonly HashSet<string> KnownLocks = new(StringComparer.Ordinal)
    {
        "IrqSpinLock", "KernelGate", "Ring1Lock", "IoOwnership", "IFilesystemGuard",
        "GcLock", "Atom", "Monitor", "SpinLock",
    };

    private readonly Dictionary<TypeSymbol, bool> _lockTypes = new(ReferenceEqualityComparer.Instance);

    /// <summary>A lock that may not be held across an await: a known one, or one marked [NoAwaitWhileHeld], or deriving from or implementing one.</summary>
    private bool IsLockType(TypeSymbol? t)
    {
        if (t is null)
        {
            return false;
        }
        if (_lockTypes.TryGetValue(t, out bool known))
        {
            return known;
        }
        _lockTypes[t] = false;                          // a cycle answers no
        bool lockType = KnownLocks.Contains(ShortName(t.Name)) || Marked(t, "NoAwaitWhileHeld") is not null
            || IsLockType(t.Base);
        if (!lockType)
        {
            foreach (TypeSymbol i in t.Interfaces)
            {
                if (IsLockType(i)) { lockType = true; break; }
            }
        }
        _lockTypes[t] = lockType;
        return lockType;
    }

    /// <summary>Whether a lock type's Enter answers whether it took the lock (IoOwnership's), rather than something else (IrqSpinLock's interrupt flag).</summary>
    private bool EnterMayFail(TypeSymbol t)
    {
        for (TypeSymbol? at = t; at is not null; at = at.Base)
        {
            string name = ShortName(at.Name);
            if (name is "IoOwnership" or "IFilesystemGuard") return true;
            if (Marked(at, "NoAwaitWhileHeld") is AttributeRef marked) return marked.Says("EnterMayFail");
        }
        foreach (TypeSymbol i in t.Interfaces)
        {
            if (EnterMayFail(i)) return true;
        }
        return false;
    }

    private static string ShortName(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(dot + 1);
    }

    private static AttributeRef? Marked(TypeSymbol t, string attribute)
    {
        TypeDecl? d = t.Decl;
        if (d is null)
        {
            return null;
        }
        foreach (AttributeRef a in d.AttributeParts)
        {
            if (a.Is(attribute)) return a;
        }
        foreach (string a in d.Attributes)
        {
            if (a == attribute || a == attribute + "Attribute") return new AttributeRef { Name = attribute };
        }
        return null;
    }

    // ---- interrupt handlers ----------------------------------------------------------------

    /// <summary>[InterruptHandler], or the implementation of an interface method that is one (IIrqHandler.OnIrq).</summary>
    private bool IsInterruptHandler(MethodSymbol m)
    {
        if (m.Decl is MethodDecl d && d.Attributes.Any(a => a.Is("InterruptHandler")))
        {
            return true;
        }
        if (m.Static || m.IsCtor)
        {
            return false;
        }
        HashSet<TypeSymbol> seen = new(ReferenceEqualityComparer.Instance);
        for (TypeSymbol? t = m.Owner; t is not null; t = t.Base)
        {
            foreach (TypeSymbol i in t.Interfaces)
            {
                if (HandlerInterface(i, m, seen)) return true;
            }
        }
        return false;
    }

    private bool HandlerInterface(TypeSymbol i, MethodSymbol m, HashSet<TypeSymbol> seen)
    {
        if (!seen.Add(i))
        {
            return false;
        }
        foreach (MethodSymbol im in i.Methods)
        {
            string implemented = m.ExplicitMember ?? m.Name;
            if (im.Name != implemented || im.Params.Count != m.Params.Count) continue;
            if (ShortName(i.Name) == "IIrqHandler" && im.Name == "OnIrq") return true;
            if (im.Decl is MethodDecl d && d.Attributes.Any(a => a.Is("InterruptHandler"))) return true;
        }
        foreach (TypeSymbol up in i.Interfaces)
        {
            if (HandlerInterface(up, m, seen)) return true;
        }
        return false;
    }

    private void CheckInterruptHandler(MethodSymbol m, MethodDecl d,
        Dictionary<MethodSymbol, MethodDecl> bodies, Dictionary<MethodSymbol, (Node Site, string What, MethodSymbol Where)?> allocates)
    {
        string name = $"{m.Owner.Name}.{m.Name}";
        if (m.Async && _asyncSafetyReported.Add(d))
        {
            Error(d, $"'{name}' is an interrupt handler, and an interrupt handler cannot be async: it runs with interrupts off on the interrupt stack, and may neither suspend nor allocate a state machine");
        }
        foreach ((Node site, string what) in AllocationsIn(d.Body!))
        {
            if (!_asyncSafetyReported.Add(site)) continue;
            Error(site, site is AwaitExpr
                ? $"'{name}' is an interrupt handler and cannot await: it runs with interrupts off on the interrupt stack (complete a task and let its continuation run, instead)"
                : $"'{name}' is an interrupt handler and must not allocate: here it {what}");
        }
        foreach (CallExpr call in DirectCalls(d.Body!))
        {
            if (!_r.Calls.TryGetValue(call, out MethodSymbol? callee) || !bodies.ContainsKey(callee) || ReferenceEquals(callee, m))
            {
                continue;
            }
            HashSet<MethodSymbol> visiting = new(ReferenceEqualityComparer.Instance) { m };
            (Node Site, string What, MethodSymbol Where)? found = AllocatesThrough(callee, bodies, allocates, visiting);
            if (found.HasValue && _asyncSafetyReported.Add(call))
            {
                (Node site, string what, MethodSymbol reached) = found.Value;
                string inner = ReferenceEquals(reached, callee) ? "" : $" (in '{reached.Owner.Name}.{reached.Name}', which it reaches)";
                Error(call, $"'{name}' is an interrupt handler and must not allocate: '{callee.Owner.Name}.{callee.Name}' {what} at line {site.Line}{inner}");
            }
        }
    }

    /// <summary>
    /// Set by the driver for a unit compiled on its own (--obj, a library):
    /// its methods' interrupt facts are kept for the link (InterruptFactsOf).
    /// </summary>
    public static bool CollectInterruptFacts { get; set; }

    /// <summary>
    /// ACROSS UNITS, THE LINK CHECKS WHAT THIS CANNOT (Lto.InterruptNotes):
    /// for every method this unit binds, its symbol, whether it is a handler,
    /// the first allocation its own body makes, and the symbols it calls
    /// directly -- the calls CheckInterruptHandler follows within the unit.
    /// </summary>
    private List<Corsac.Lang.Lto.InterruptNotes.Fact> InterruptFactsOf()
    {
        List<Corsac.Lang.Lto.InterruptNotes.Fact> made = new();
        // EACH DEFINITION'S NAME SPELT ONCE: a callee is named again from
        // every body that calls it -- List's Add from thousands -- and each
        // spelling was a builder and its strings, for the same answer.
        Dictionary<MethodSymbol, string> names = new(ReferenceEqualityComparer.Instance);
        string NameOf(MethodSymbol m)
        {
            if (!names.TryGetValue(m, out string? name)) names[m] = name = DefinitionName(m);
            return name;
        }
        foreach ((MethodSymbol m, MethodDecl d) in _boundBodies)
        {
            string file = d.File is { Length: > 0 } own ? own : m.Owner.Decl?.File ?? "";
            string? allocates = null;
            if (m.Async)
            {
                allocates = "is async, and makes a state machine and a task";
            }
            else
            {
                foreach ((Node site, string what) in AllocationsIn(d.Body!))
                {
                    if (site is AwaitExpr) continue;
                    allocates = $"{what} at {file}:{site.Line}";
                    break;
                }
            }
            List<string> calls = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (CallExpr call in DirectCalls(d.Body!))
            {
                if (_r.Calls.TryGetValue(call, out MethodSymbol? callee) && !ReferenceEquals(callee, m))
                {
                    string label = NameOf(callee);
                    if (seen.Add(label)) calls.Add(label);
                }
            }
            made.Add(new Corsac.Lang.Lto.InterruptNotes.Fact(NameOf(m), $"{m.Owner.Name}.{m.Name}",
                IsInterruptHandler(m), allocates, calls.ToArray()));
        }
        return made;
    }

    /// <summary>
    /// A method's name as its DEFINITION is known in every unit, whatever
    /// copy of it a unit binds: the owner's key with any type arguments taken
    /// out and its arity written instead, the method's name and arity, and
    /// its parameters as the declaration writes them. The emitted label
    /// spells a specialisation's arguments out, so a call to `List<long>.Add`
    /// in one unit and the shared code of `List<T>.Add` in another would not
    /// meet by it; by this they do. A method no declaration wrote is known
    /// by its label.
    /// </summary>
    private static string DefinitionName(MethodSymbol m)
    {
        if (m.Decl is not MethodDecl d) return Corsac.Lang.Lower.Lowering.Label(m);
        System.Text.StringBuilder owner = new();
        int depth = 0;
        foreach (char c in m.Owner.Key)
        {
            if (c == '<') { depth++; continue; }
            if (c == '>') { if (depth > 0) depth--; continue; }
            if (depth == 0) owner.Append(c);
        }
        int ownerArity = m.Owner.Decl?.TypeParams.Count ?? 0;
        return $"{owner}`{ownerArity}.{m.Name}`{d.TypeParams.Count}({string.Join(",", d.Params.Select(p => (p.IsRef || p.IsOut ? "ref " : "") + p.Type))})";
    }

    /// <summary>The first allocation a method makes, itself or in what it calls directly with a body in this unit.</summary>
    private (Node Site, string What, MethodSymbol Where)? AllocatesThrough(MethodSymbol m, Dictionary<MethodSymbol, MethodDecl> bodies,
        Dictionary<MethodSymbol, (Node Site, string What, MethodSymbol Where)?> allocates, HashSet<MethodSymbol> visiting)
    {
        if (allocates.TryGetValue(m, out var known))
        {
            return known;
        }
        if (!bodies.TryGetValue(m, out MethodDecl? d) || !visiting.Add(m))
        {
            return null;
        }
        (Node Site, string What, MethodSymbol Where)? found = null;
        if (m.Async)
        {
            found = (d, "is async, and makes a state machine and a task", m);
        }
        if (found is null)
        {
            foreach ((Node site, string what) in AllocationsIn(d.Body!))
            {
                if (site is AwaitExpr) continue;
                found = (site, what, m);
                break;
            }
        }
        if (found is null)
        {
            foreach (CallExpr call in DirectCalls(d.Body!))
            {
                if (_r.Calls.TryGetValue(call, out MethodSymbol? callee) && bodies.ContainsKey(callee))
                {
                    (Node Site, string What, MethodSymbol Where)? deeper = AllocatesThrough(callee, bodies, allocates, visiting);
                    if (deeper.HasValue)
                    {
                        found = deeper;
                        break;
                    }
                }
            }
        }
        visiting.Remove(m);
        allocates[m] = found;
        return found;
    }

    /// <summary>Every call in a body made to a method the call names exactly: static, or not virtual.</summary>
    private IEnumerable<CallExpr> DirectCalls(Node body)
    {
        foreach (Node n in SafetyNodes(body))
        {
            if (n is CallExpr c && _r.Calls.TryGetValue(c, out MethodSymbol? m)
                && (m.Static || (!m.Virtual && !m.Abstract && !m.Override && m.Owner.Kind != TypeKind.Interface))
                && !_r.Invocations.ContainsKey(c))
            {
                yield return c;
            }
        }
    }

    /// <summary>The awaits and allocations a body makes itself, in order, with what each does.</summary>
    private IEnumerable<(Node Site, string What)> AllocationsIn(Node body)
    {
        foreach (Node n in SafetyNodes(body))
        {
            switch (n)
            {
                case AwaitExpr:
                    yield return (n, "awaits");
                    break;
                case UsingDeclStmt { Async: true }:
                    yield return (n, "awaits");
                    break;
                case LambdaExpr l:
                    yield return (n, l.GroupIdentity is not null ? "makes a delegate of a method" : "makes a lambda");
                    break;
                case NewExpr nw when nw.Utf8Bytes is null:
                {
                    Type made = _r.TypeOf(nw);
                    if (made.IsArray) yield return (n, $"makes an array ('{made}')");
                    else if (made.IsReference) yield return (n, $"makes a new '{made}'");
                    break;
                }
                case BinaryExpr { Op: BinOp.Add } add when _r.TypeOf(add).Prim == Prim.String
                    && !(add.Left is LiteralExpr && add.Right is LiteralExpr):
                    yield return (n, "joins strings");
                    break;
                case AssignExpr { Op: BinOp.Add } compound when _r.TypeOf(compound.Target).Prim == Prim.String:
                    yield return (n, "joins strings");
                    break;
                case CallExpr call when _r.Calls.TryGetValue(call, out MethodSymbol? callee) && callee.Async:
                    yield return (n, $"calls async '{callee.Owner.Name}.{callee.Name}', which makes a state machine");
                    break;
            }
            if (n is Expr boxed && _r.Boxes.Contains(boxed))
            {
                yield return (n, "boxes a value");
            }
        }
    }

    /// <summary>Every node of a body, not descending into the bodies of the lambdas it makes.</summary>
    private IEnumerable<Node> SafetyNodes(Node root, bool intoLambdas = false)
    {
        // A rewrite may hold the expression it replaces (a conversion around
        // it): each node's rewrite is followed once, and the node met again
        // inside it is walked by its own children, or the walk never ends.
        List<Node> stack = new() { root };
        HashSet<Node> rewritten = new(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            Node n = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            yield return n;
            if (n is LambdaExpr && !intoLambdas)
            {
                continue;
            }
            // One buffer for every node's children, reversed onto the stack:
            // a list of its own for each node was most of what the walk cost.
            int below = stack.Count;
            if (AddSafetyChildren(n, stack, rewritten) is { } marked) _inOwnRewrite.Remove(marked);
            stack.Reverse(below, stack.Count - below);
        }
    }

    /// <summary>The expressions whose rewrite a recursive walk is inside now: met again there, they are walked by their own children.</summary>
    private readonly HashSet<Node> _inOwnRewrite = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A node's children as the code runs them, appended to <paramref name="into"/>:
    /// a rewritten expression's rewrite, a lowered statement's lowering. An
    /// enumerator per node was a quarter of a gigabyte of garbage in one unit
    /// of this compiler, every node of every body walked several times.
    ///
    /// For a recursive walker (no <paramref name="rewritten"/>), an expression
    /// whose rewrite is its child is marked as inside its own rewrite, and
    /// returned: the walker unmarks it once it has walked the children, as
    /// long as the enumerator's mark used to last.
    /// </summary>
    private Expr? AddSafetyChildren(Node n, List<Node> into, HashSet<Node>? rewritten = null)
    {
        if (n is Expr e && _r.Rewrites.TryGetValue(e, out Expr? instead) && !ReferenceEquals(instead, e))
        {
            if (rewritten is not null)
            {
                if (rewritten.Add(e))
                {
                    into.Add(instead);
                    return null;
                }
            }
            else if (_inOwnRewrite.Add(e))
            {
                into.Add(instead);
                return e;
            }
        }
        switch (n)
        {
            case Block b: foreach (Stmt s in b.Statements) into.Add(s); break;
            case LocalDecl d:
                if (d.Init is not null) into.Add(d.Init);
                foreach (LocalDecl also in d.Also) into.Add(also);
                break;
            case UsingDeclStmt u: into.Add(u.Declaration); break;
            case ExprStmt es: into.Add(es.Expr); break;
            case IfStmt i:
                into.Add(i.Cond); into.Add(i.Then);
                if (i.Else is not null) into.Add(i.Else);
                break;
            case WhileStmt w: into.Add(w.Cond); into.Add(w.Body); break;
            case LabeledStmt l: into.Add(l.Body); break;
            case DoStmt d: into.Add(d.Body); into.Add(d.Cond); break;
            case ForStmt f:
                if (f.Init is not null) into.Add(f.Init);
                if (f.Cond is not null) into.Add(f.Cond);
                foreach (Expr s in f.Step) into.Add(s);
                into.Add(f.Body);
                break;
            case ForeachStmt fe:
                if (_r.Lowered.TryGetValue(fe, out Stmt? low)) into.Add(low);
                else { into.Add(fe.Sequence); into.Add(fe.Body); }
                break;
            case DeconstructStmt ds:
                if (_r.Lowered.TryGetValue(ds, out Stmt? low2)) into.Add(low2);
                break;
            case ReturnStmt r: if (r.Value is not null) into.Add(r.Value); break;
            case YieldStmt y: if (y.Value is not null) into.Add(y.Value); break;
            case ThrowStmt t: if (t.Value is not null) into.Add(t.Value); break;
            case SwitchStmt sw:
                into.Add(sw.Subject);
                foreach (SwitchCase c in sw.Cases)
                {
                    if (c.Pattern is not null) into.Add(c.Pattern);
                    foreach (Stmt s in c.Body) into.Add(s);
                }
                break;
            case TryStmt ts:
                into.Add(ts.Body);
                foreach (CatchClause c in ts.Catches)
                {
                    if (c.When is not null) into.Add(c.When);
                    into.Add(c.Body);
                }
                if (ts.Finally is not null) into.Add(ts.Finally);
                break;
            case ThrowExpr te: into.Add(te.Value); break;
            case GotoCaseStmt gc: if (gc.Value is not null) into.Add(gc.Value); break;
            case TupleExpr tu: foreach (Expr x in tu.Items) into.Add(x); break;
            case RangeExpr rg:
                if (rg.From is not null) into.Add(rg.From);
                if (rg.To is not null) into.Add(rg.To);
                break;
            case FromEndExpr fe2: into.Add(fe2.Offset); break;
            case PatternExpr p: into.Add(p.Subject); into.Add(p.Test); break;
            case SequenceExpr q: into.Add(q.Effect); into.Add(q.Value); break;
            case SuppressExpr s: into.Add(s.Operand); break;
            case MemberExpr m: into.Add(m.Target); break;
            case CallExpr c: into.Add(c.Target); foreach (Expr a in c.Args) into.Add(a); break;
            case IndexExpr ix: into.Add(ix.Target); foreach (Expr a in ix.Args) into.Add(a); break;
            case WithExpr w:
                into.Add(w.Source);
                AddSafetyInitChildren(w.Body, into);
                break;
            case NewExpr nw:
                foreach (Expr a in nw.Args) into.Add(a);
                if (nw.ArraySize is not null) into.Add(nw.ArraySize);
                if (nw.Elements is not null) foreach (Expr x in nw.Elements) into.Add(x);
                AddSafetyInitChildren(nw.Body, into);
                break;
            case UnaryExpr u: into.Add(u.Operand); break;
            case BinaryExpr b: into.Add(b.Left); into.Add(b.Right); break;
            case AssignExpr a: into.Add(a.Target); into.Add(a.Value); break;
            case ConditionalExpr c: into.Add(c.Cond); into.Add(c.Then); into.Add(c.Else); break;
            case CastExpr c: into.Add(c.Operand); break;
            case RefArgExpr r: into.Add(r.Target); break;
            case IsExpr i: into.Add(i.Operand); break;
            case AsExpr a: into.Add(a.Operand); break;
            case AwaitExpr a: into.Add(a.Operand); break;
            case SwitchExpr sx:
                into.Add(sx.Subject);
                foreach (SwitchArm arm in sx.Arms)
                {
                    if (arm.Value is not null) into.Add(arm.Value);
                    if (arm.When is not null) into.Add(arm.When);
                    into.Add(arm.Result);
                }
                break;
            case LambdaExpr l:
                if (l.Body is not null) into.Add(l.Body);
                if (l.BlockBody is not null) into.Add(l.BlockBody);
                break;
        }
        return null;
    }

    private static void AddSafetyInitChildren(InitBody body, List<Node> into)
    {
        foreach (InitAssign init in body.Inits)
        {
            if (init.Nested is InitBody nested)
            {
                AddSafetyInitChildren(nested, into);
            }
            else if (init.Value is Expr given)
            {
                into.Add(given);
            }
        }
        foreach (InitAdd add in body.Adds)
        {
            foreach (Expr e in add.Args) into.Add(e);
        }
        foreach (InitIndex one in body.Indexes)
        {
            foreach (Expr e in one.Args) into.Add(e);
            into.Add(one.Value);
        }
    }
}
