#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

// ---- whether the program needs a collector, for the link -------------------------------
//
// A WHOLE PROGRAM SAYS IT ITSELF (CollectorSites): from the entry, over every
// call and every address its code and data name, the allocations nothing
// frees, that run more than once and hold no table for the life of the
// program. A unit sees only itself, so it writes down what that walk reads
// of each of its functions once its own lifetimes are decided -- its calls,
// with whether it frees what each hands back; the addresses it names; the
// allocations it left to the collector, with the flags, fields and thrown
// types that could still excuse them -- and the link walks every unit's
// records at once (Lto.HeapDecision). Where they say no collector is needed,
// every unit is made again without one (Module.LinkHeap).

public sealed partial class Escape
{
    /// <summary>The types each function's catches keep what they caught of, "*" for all (KeepCaughtIn).</summary>
    private readonly Dictionary<string, HashSet<string>> _keptCatchesIn = new(StringComparer.Ordinal);

    // ---- the throw budget, and the fields that leak -----------------------------------
    //
    // AN UNWIND LEAVES THINGS BEHIND: the object thrown, the frees placed in
    // the frames it leaves, the frames a throw records into what it throws
    // again. Where every catch the program reaches runs at most once, and it
    // reaches no async body (whose catch-all takes what its awaits throw, as
    // often as they do), every unwind ends at a catch that runs once or ends
    // the program -- boundedly many in a run -- and all it leaves behind is
    // bounded too: no collector is needed for any of it. Where a catch can
    // run more than once, it is not (CollectorSites, HeapDecision). An
    // iterator's own handler, run as often as its iterators are disposed,
    // ends only unwinds that leave nothing behind (RepeatingCatches).
    //
    // A FIELD A STORE CAN REPLACE WITHOUT FREEING, round and round, holds
    // nothing the collector can be spared for: a fresh object held by it
    // (OwnedFields' held pairs) is its owner's to free only where every
    // reached store into it that may replace a value nobody frees runs at
    // most once, or is into what is being thrown (bounded with the unwinds).

    /// <summary>Every block of a reached function that ends a catch, with its end, noted before OwnCaught makes freeing catches into frees.</summary>
    private readonly List<(Function F, Block B, Instr End)> _catchEnds = new();

    /// <summary>The stores into an owned field that may replace a value nobody frees (StoreMayReplace), and whether into what is thrown.</summary>
    private readonly List<(Function F, Block B, string Field, bool AtThrow)> _replacing = new();

    /// <summary>The field each held object is held by (OwnedFields' held pairs); and the held calls not owned for another reason.</summary>
    private readonly Dictionary<Instr, string> _heldBy = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Instr> _heldCalls = new(ReferenceEqualityComparer.Instance);

    private void NoteCatchEnds(Module m)
    {
        _catchEnds.Clear();
        foreach (Function f in m.Functions)
        {
            if (_reachedFunctions is not null && !_reachedFunctions.Contains(f.Name)) continue;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && IsCatchEnd(i.Callee)) _catchEnds.Add((f, b, i));
        }
    }

    private readonly Dictionary<Function, HashSet<Block>> _returning = new();

    /// <summary>
    /// WHETHER WHAT A BLOCK MAKES IS MADE ON THE WAY TO AN UNWIND: on no cycle,
    /// and no return reachable from it -- every run through it ends in a
    /// throw, a trap or the program's end. An exception made every lap and
    /// thrown on some (`b = new Boom(i); if (i == n) throw b;`) is thrown,
    /// and dropped on every other lap: only where the block is doomed is it
    /// one of boundedly many unwinds' (the throw budget).
    /// </summary>
    private bool Doomed(Function f, Block b)
    {
        if (Repeating(f).Contains(b)) return false;
        if (!_returning.TryGetValue(f, out HashSet<Block>? returning))
        {
            returning = new(ReferenceEqualityComparer.Instance);
            Cfg cfg = new(f);
            Stack<Block> work = new(f.Blocks.Where(x => x.Terminator is { Op: Opcode.Ret }));
            while (work.TryPop(out Block? x))
                if (returning.Add(x)) foreach (Block p in cfg.Preds(x)) work.Push(p);
            _returning[f] = returning;
        }
        return !returning.Contains(b);
    }

    /// <summary>
    /// Whether an object made in `f` can be made again before it reaches the
    /// store that hands it to a field -- `x = null; for (..) if (..) x = new
    /// Item(); h.F = x;` -- dropping the earlier one, which no owner frees.
    /// </summary>
    private static bool MadeAgainBeforeStored(Function f, Instr made, Instr st)
    {
        Block? madeIn = null, storedIn = null;
        foreach (Block x in f.Blocks)
        {
            if (madeIn is null && x.Instrs.Contains(made)) madeIn = x;
            if (storedIn is null && x.Instrs.Contains(st)) storedIn = x;
        }
        if (madeIn is null || storedIn is null) return true;
        if (ReferenceEquals(madeIn, storedIn)) return madeIn.Instrs.IndexOf(made) > madeIn.Instrs.IndexOf(st);
        return ReachesAvoiding(new Cfg(f), madeIn, madeIn, storedIn);
    }

    /// <summary>
    /// Why unwinds are unbounded: each reached catch that can run more than
    /// once, and each reached async body. Empty: bounded.
    ///
    /// AN ITERATOR'S OWN HANDLER IS NOT ONE (CoroutineKind): it ends only the
    /// stop signal Dispose resumes it with, and that unwind leaves nothing
    /// behind. The signal is one object, made once and thrown each time, and
    /// Capture records no trace into it. It is thrown by IteratorStop, a leaf
    /// with nothing to free, at the resumption it stops, in MoveNext's own
    /// frame: nothing made since the resumption is live there, what was held
    /// across the suspension is in a slot of its own that the handler's
    /// return frees as every return does (Own), and the finally blocks the
    /// signal runs on its way are that frame's own code. Every other
    /// exception the handler hands on, untouched, to an outer catch, judged
    /// here as its own, or to the program's end; so is a catch the iterator's
    /// body writes itself.
    /// </summary>
    private List<string> RepeatingCatches(Module m, OnceRun once)
    {
        List<string> found = new();
        foreach ((Function f, Block b, Instr end) in _catchEnds)
            if (!once.RunsOnce(f, b)) found.Add($"{f.Name}:{end.Line} a catch of {end.DispatchType ?? "everything"} that can run more than once");
        foreach (Function f in m.Functions)
            if (f.Async is { Kind: CoroutineKind.Async } && (_reachedFunctions is null || _reachedFunctions.Contains(f.Name)))
                found.Add($"{f.Name}: an async body, whose catch takes what its awaits throw");
        return found;
    }

    /// <summary>The fields a reached store may replace without freeing, more than once in a run, not into what is thrown.</summary>
    private HashSet<string> LeakyFields(OnceRun once)
    {
        HashSet<string> leaky = new(StringComparer.Ordinal);
        foreach ((Function f, Block b, string field, bool atThrow) in _replacing)
            if (!atThrow && (_reachedFunctions is null || _reachedFunctions.Contains(f.Name)) && !once.RunsOnce(f, b)) leaky.Add(field);
        return leaky;
    }

    /// <summary>
    /// WHETHER A STORE INTO A FIELD MAY REPLACE A VALUE NOBODY FREES: not a
    /// `??=` (FillStoreShape, whose window the owned-field rules hold clear
    /// of other stores), the first store into an object just made
    /// (FirstStoreIntoMade, FirstStores), a store the function's own free of
    /// the field's value covers (EscapeSelfFrees), a fill behind a test that
    /// found the field empty (GuardedFill), nor a store into an object only
    /// this function sees, whose old value the owned-field rules free where
    /// it is replaced (PrivateOwner).
    /// </summary>
    private bool StoreMayReplace(Function f, Block b, Instr st, SelfFrees self, FirstStores first, Dictionary<Instr, bool> firstFill,
        Dictionary<Function, Cfg> cfgOf, Dictionary<string, bool[]> summaries, Dictionary<Instr, bool> privateOwner)
    {
        if (st.Field is not { } field || st.Operands.Count < 2 || st.Operands[0] is not RegOperand baseReg) return false;
        if (FillStoreShape(f, new Defs(f), new RegisterWrites(f), st) is not null) return false;
        if (FirstStoreIntoMade(f, st, firstFill, cfgOf) || first.Fills(b, b.Instrs.IndexOf(st)) || self.FreedFields.Contains(field)
            || ConstructorFirstStore(f, b, st, cfgOf)) return false;
        Dictionary<VReg, Instr> defs = SingleDefs(f);
        if (GuardedFill(f, b, st, defs)) return false;
        return !(OriginOf(defs, baseReg.Reg) is Instr madeOwner && PrivateOwner(f, madeOwner, summaries, privateOwner));
    }

    /// <summary>
    /// THE FIRST STORE A CONSTRUCTOR MAKES INTO A FIELD OF ITS OWN OBJECT --
    /// a field initialiser, List's `items = null!` -- replaces nothing. A
    /// class's constructor runs on an object just made, zero where it was
    /// made, or is chained to by another constructor of the same object
    /// (`: this(...)`, `: base(...)`) before that one's body, whose field
    /// initialisers are its own class's fields; and before the store, on
    /// every way from the constructor's entry, nothing but allocations and
    /// the collector's notes is called, nothing else is stored into the
    /// field, and no memory is copied or filled, so nothing can have put
    /// anything there. Only a class's instance fields are tagged
    /// (Instr.Field), so `this` is an object, never a struct's storage.
    /// </summary>
    private static bool ConstructorFirstStore(Function f, Block b, Instr st, Dictionary<Function, Cfg> cfgOf)
    {
        if (f.Display is not { } shown || !shown.Contains("..ctor(", StringComparison.Ordinal) || f.Async is not null
            || f.Params.Count == 0 || f.Params[0].Name != "this" || st.Operands[0] is not RegOperand { Reg: var into }) return false;
        Dictionary<VReg, Instr> defs = SingleDefs(f);
        HashSet<VReg> written = new();
        foreach (Block x in f.Blocks) foreach (Instr i in x.Instrs) if (i.Dest is not null) written.Add(i.Dest);
        if (written.Contains(f.Params[0])) return false;
        VReg root = into;
        for (int hop = 0; hop < 8 && root != f.Params[0]; hop++)
        {
            if (!defs.TryGetValue(root, out Instr? d) || d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands is not [RegOperand from]) return false;
            root = from.Reg;
        }
        if (root != f.Params[0]) return false;
        bool Harmless(Instr i) => i.Op switch
        {
            Opcode.Call => IsAllocator(i.Callee) || i.Callee is not null && IsCollectorNote(i.Callee),
            Opcode.Store => i.Field != st.Field,
            Opcode.CallIndirect or Opcode.Syscall or Opcode.MemCopy or Opcode.MemSet or Opcode.AtomicSwap or Opcode.AtomicAdd
                or Opcode.AtomicAnd or Opcode.AtomicOr or Opcode.AtomicXor or Opcode.AtomicCas => false,
            _ => true,
        };
        // Back from the store to the entry: a landing pad, or the store's own
        // block round a loop -- the store again -- is a way that fails.
        if (!cfgOf.TryGetValue(f, out Cfg? cfg)) cfgOf[f] = cfg = new Cfg(f);
        for (int k = b.Instrs.IndexOf(st) - 1; k >= 0; k--) if (!Harmless(b.Instrs[k])) return false;
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new();
        if (!ReferenceEquals(b, f.Entry)) foreach (Block p in cfg.Preds(b)) work.Push(p);
        else if (cfg.Preds(b).Count > 0) return false;
        while (work.TryPop(out Block? x))
        {
            if (!seen.Add(x)) continue;
            if (ReferenceEquals(x, b) || x.IsLandingPad) return false;
            foreach (Instr i in x.Instrs) if (!Harmless(i)) return false;
            if (ReferenceEquals(x, f.Entry)) { if (cfg.Preds(x).Count > 0) return false; continue; }
            if (cfg.Preds(x).Count == 0) return false;
            foreach (Block p in cfg.Preds(x)) work.Push(p);
        }
        return true;
    }

    /// <summary>
    /// THE FIRST STORE INTO AN OBJECT JUST MADE -- a record's `with` filling
    /// its copy -- replaces nothing: the object's memory is zero where it is
    /// made, and this store is the field's only one there, not round a loop.
    /// </summary>
    private bool FirstStoreIntoMade(Function f, Instr st, Dictionary<Instr, bool> firstFill, Dictionary<Function, Cfg> cfgOf)
    {
        if (firstFill.TryGetValue(st, out bool known)) return known;
        firstFill[st] = false;
        Dictionary<VReg, Instr> defs = SingleDefs(f);
        if (st.Operands[0] is not RegOperand { Reg: var baseReg } || OriginOf(defs, baseReg) is not { Op: Opcode.Call } made
            || !IsAllocator(made.Callee) || made.Dest is null) return false;
        HashSet<VReg> names = new() { made.Dest };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block x in f.Blocks)
                foreach (Instr i in x.Instrs)
                    if (i.Dest is not null && !names.Contains(i.Dest) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                        && i.Operands[0] is RegOperand r && names.Contains(r.Reg) && defs.ContainsKey(i.Dest))
                    { names.Add(i.Dest); grew = true; }
        }
        int count = 0;
        Block? at = null, madeIn = null;
        foreach (Block x in f.Blocks)
            foreach (Instr i in x.Instrs)
            {
                if (ReferenceEquals(i, made)) madeIn = x;
                if (ReferenceEquals(i, st)) at = x;
                if (i.Op == Opcode.Store && i.Field == st.Field && i.Operands[0] is RegOperand { Reg: var q } && names.Contains(q)) count++;
            }
        if (count != 1 || at is null || madeIn is null) return false;
        if (!cfgOf.TryGetValue(f, out Cfg? cfg)) cfgOf[f] = cfg = new Cfg(f);
        if (ReachesAvoiding(cfg, at, at, madeIn)) return false;
        return firstFill[st] = true;
    }

    /// <summary>
    /// A FILL BEHIND ITS TEST -- `if (o.f == null) o.f = new T();` -- replaces
    /// nothing: the store's block is entered only by the null edge of a test
    /// of the same field of the same object, nothing between the test's read
    /// and the store but allocations, the collector's notes and stores into
    /// other fields, and the store is not round a loop back to itself without
    /// the test.
    /// </summary>
    private static bool GuardedFill(Function f, Block b, Instr st, Dictionary<VReg, Instr> defs)
    {
        if (st.Operands[0] is not RegOperand { Reg: var baseReg }) return false;
        VReg Root(VReg r)
        {
            for (int hop = 0; hop < 8 && defs.TryGetValue(r, out Instr? d) && d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands is [RegOperand from]; hop++) r = from.Reg;
            return r;
        }
        Cfg cfg = new(f);
        bool Harmless(Instr i) => i.Op is not (Opcode.Call or Opcode.CallIndirect or Opcode.Store)
            || i.Op == Opcode.Call && (IsAllocator(i.Callee) || IsCollectorNote(i.Callee))
            || i.Op == Opcode.Store && i.Field != st.Field && !(i.Operands[0] is RegOperand { Reg: var into } && Root(into) == Root(baseReg) && i.Offset == st.Offset);
        foreach (Block p in f.Blocks)
        {
            if (p.Terminator is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var cond }] } br || br.Targets.Count != 2
                || ReferenceEquals(br.Targets[0], br.Targets[1]) || !defs.TryGetValue(cond, out Instr? test)) continue;
            Block nullEdge;
            VReg read;
            if (test is { Op: Opcode.Eq, Operands: [RegOperand { Reg: var eq }, ImmOperand { Value: 0 }] }) { nullEdge = br.Targets[0]; read = eq; }
            else if (test is { Op: Opcode.Ne, Operands: [RegOperand { Reg: var ne }, ImmOperand { Value: 0 }] }) { nullEdge = br.Targets[1]; read = ne; }
            else { nullEdge = br.Targets[1]; read = cond; }
            read = Root(read);
            if (!defs.TryGetValue(read, out Instr? load) || load.Op != Opcode.Load || load.Offset != st.Offset
                || load.Operands is not [RegOperand { Reg: var loadBase }] || Root(loadBase) != Root(baseReg)) continue;
            if (cfg.Preds(nullEdge).Count != 1 || !cfg.Dominates(nullEdge, b)) continue;
            int from = p.Instrs.IndexOf(load);
            if (from < 0) continue;
            // Every block between the null edge and the store, not round again through the test.
            HashSet<Block> forward = new(ReferenceEqualityComparer.Instance);
            Stack<Block> work = new();
            work.Push(nullEdge);
            while (work.TryPop(out Block? x))
            {
                if (ReferenceEquals(x, p) || !forward.Add(x) || ReferenceEquals(x, b)) continue;
                foreach (Block n in cfg.Succs(x)) work.Push(n);
            }
            if (!forward.Contains(b)) continue;
            HashSet<Block> backward = new(ReferenceEqualityComparer.Instance);
            work.Push(b);
            while (work.TryPop(out Block? x))
            {
                if (!forward.Contains(x) || !backward.Add(x) || ReferenceEquals(x, nullEdge)) continue;
                foreach (Block n in cfg.Preds(x)) work.Push(n);
            }
            bool loops = false;
            foreach (Block n in cfg.Succs(b)) if (forward.Contains(n) && backward.Contains(n) || ReferenceEquals(n, b)) loops = true;
            // Nor round a loop the store is in, back to it without the test.
            if (loops || ReachesAvoiding(cfg, b, b, p)) continue;
            bool ok = true;
            for (int k = from + 1; k < p.Instrs.Count && ok; k++) ok = Harmless(p.Instrs[k]);
            foreach (Block x in backward)
            {
                int end = ReferenceEquals(x, b) ? x.Instrs.IndexOf(st) : x.Instrs.Count;
                for (int k = 0; k < end && ok; k++) ok = Harmless(x.Instrs[k]);
            }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>
    /// WHAT THE THROW WRITES INTO WHAT IS THROWN (Exception.Thrown$ and
    /// Placed$, called by the runtime's Capture at the throw; Reserve$, the
    /// making of the exception the heap throws out of memory, made in
    /// advance and thrown with the trace it then writes in place): a store
    /// into the first parameter, through copies and through a join of it
    /// and null (an `is Exception e` binding).
    /// </summary>
    private static bool IntoThrown(Function g, VReg r)
    {
        if (g.Params.Count == 0 || !g.Name.StartsWith("m_Exception_Thrown$_", StringComparison.Ordinal)
            && !g.Name.StartsWith("m_Runtime_Capture_3_", StringComparison.Ordinal) && !g.Name.StartsWith("m_Exception_Placed$_", StringComparison.Ordinal)
            && !g.Name.StartsWith("m_Exception_Reserve$_", StringComparison.Ordinal)) return false;
        RegisterWrites writes = new(g);
        HashSet<VReg> seen = new();
        Stack<VReg> work = new();
        work.Push(r);
        while (work.TryPop(out VReg? at))
        {
            if (at == g.Params[0] || !seen.Add(at)) continue;
            if (!writes.TryGetValue(at, out WriteList ws) || ws.Count == 0) return false;
            foreach (Instr d in ws)
            {
                if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands is [ImmOperand { Value: 0 }]) continue;
                if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands is [RegOperand from]) { work.Push(from.Reg); continue; }
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The unit's records for the link's walk (Lto.HeapRecords), from the
    /// module as this run leaves its lifetimes: every allocation it owns,
    /// every call whose result it frees, decided.
    /// </summary>
    private HeapRecords HeapRecordsOf(Module m, Dictionary<string, bool[]> summaries)
    {
        HeapRecords records = new() { CallsCollector = m.CallsCollector, NoCollector = m.NoCollector };
        foreach (Function f in m.Functions)
        {
            if (IsStaticInitialiser(f.Name)) continue;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[0] is SymOperand { Name: var field } && field.StartsWith("s_", StringComparison.Ordinal))
                        records.StaticWrites.Add(field);
        }
        foreach (Function f in m.Functions) records.Functions.Add(HeapFunctionOf(f, summaries));
        return records;
    }

    private HeapFunction HeapFunctionOf(Function f, Dictionary<string, bool[]> summaries)
    {
        HeapFunction record = new(f.Name) { Async = f.Async is not null, KeepsAwaited = f.Async is { Kind: CoroutineKind.Async } };

        // The latch flags, as OnceRun reads them over the whole program.
        SortedSet<string> sets = new(StringComparer.Ordinal), breaks = new(StringComparer.Ordinal);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Store && i.Operands.Count == 2 && i.Operands[0] is SymOperand { Name: var flag })
                {
                    if (i.Operands[1] is ImmOperand { Value: not 0 }) sets.Add(flag); else breaks.Add(flag);
                    if (i.Operands[1] is SymOperand { Name: var stored }) breaks.Add(stored);
                }
                else
                    for (int k = 0; k < i.Operands.Count; k++)
                        if (i.Operands[k] is SymOperand { Name: var named } && !(i.Op == Opcode.Load && k == 0)) breaks.Add(named);
            }
        record.LatchSets.AddRange(sets); record.LatchBreaks.AddRange(breaks);

        HashSet<Block> repeating = Repeating(f);
        Dictionary<Block, HashSet<string>> latched = OnceRun.LatchedBy(f);
        Dictionary<Instr, string> permanent = PermanentCandidates(f, repeating);
        Dictionary<Instr, string> held = HeldByFields(f, summaries);
        _freshOrigins.TryGetValue(f.Name, out HashSet<Instr>? origins);
        Dictionary<Instr, string[]>? virtuals = null;
        // Its catches, and its stores that may replace what nobody frees (the
        // throw budget and the fields that leak, which the link decides with
        // the program's reach): of a field the unit has not refused, whose
        // owner the link may yet find frees what it holds.
        HashSet<Block> catches = new(_catchEnds.Where(c => ReferenceEquals(c.F, f)).Select(c => c.B), ReferenceEqualityComparer.Instance);
        SelfFrees? self = null;
        FirstStores? first = null;
        Dictionary<Instr, bool> firstFill = new(ReferenceEqualityComparer.Instance), privateOwner = new(ReferenceEqualityComparer.Instance);
        Dictionary<Function, Cfg> cfgOf = new();
        Corsac.Lang.Lto.OwnedFieldHints? hints = _hinting ? _module?.LifetimeHints?.Owned : null;
        bool Replaces(Block b, Instr st)
        {
            if (st is not { Op: Opcode.Store, Field: { } field } || st.Operands.Count < 2 || st.Operands[0] is not RegOperand) return false;
            if (hints is not null && hints.Fields.TryGetValue(field, out Corsac.Lang.Lto.OwnedFieldRecord? known) && known.Refused) return false;
            self ??= SelfFreesOf(f, _inserted);
            first ??= new FirstStores(f, SingleDefs(f), _promotedMade, _promotedZeroing);
            return StoreMayReplace(f, b, st, self, first, firstFill, cfgOf, summaries, privateOwner);
        }
        foreach (Block b in f.Blocks)
        {
            HeapFlags block = (repeating.Contains(b) ? HeapFlags.None : HeapFlags.Once)
                | (b.Terminator is { Op: Opcode.Unreachable } ? HeapFlags.Trapping : HeapFlags.None);
            string[] latches = latched.TryGetValue(b, out HashSet<string>? flags) ? flags.Order(StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            if (catches.Contains(b))
                foreach ((Function _, Block _, Instr end) in _catchEnds.Where(c => ReferenceEquals(c.B, b)))
                    record.Events.Add(new(HeapEventKind.Catch, end.DispatchType ?? "*", block, latches, null, null));
            int dying = DyingFrom(b);
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr i = b.Instrs[at];
                if (at == dying) block |= HeapFlags.Dying;
                if (Replaces(b, i))
                    record.Events.Add(new(HeapEventKind.Store, i.Field!, block | (IntoThrown(f, ((RegOperand)i.Operands[0]).Reg) ? HeapFlags.AtThrow : HeapFlags.None),
                        latches, null, null));
                if (i.Callee is not null)
                {
                    HeapFlags call = block | (i.Op == Opcode.Call ? HeapFlags.Direct : HeapFlags.None)
                        | (origins is not null && origins.Contains(i) ? HeapFlags.Origin : HeapFlags.None)
                        | (_ownedCalls.Contains(i) ? HeapFlags.OwnedResult : HeapFlags.None)
                        | (_owned.Contains(i) ? HeapFlags.OwnedAllocation : HeapFlags.None);
                    bool site = IsAllocator(i.Callee) && !_owned.Contains(i) && !_permanent.Contains(i) && !_fieldOwned.Contains(i);
                    if (site) call |= HeapFlags.Site;
                    record.Events.Add(new(HeapEventKind.Call, i.Callee, call, latches,
                        site ? permanent.GetValueOrDefault(i) : null,
                        site && _thrown.Contains(i) && Doomed(f, b) ? _thrownType.GetValueOrDefault(i) ?? "*" : null) { Held = held.GetValueOrDefault(i) });
                }
                foreach (Operand o in i.Operands)
                    if (o is SymOperand sym) record.Events.Add(new(HeapEventKind.Named, sym.Name, block, latches, null, null));
                if (i.Op != Opcode.CallIndirect) continue;
                // A target the unit names by its virtual slot (VirtualCallees) is the link's to resolve.
                if (_indirect is not null && _indirect.TryGetValue(i, out string[]? targets))
                    foreach (string target in targets)
                        record.Events.Add(new(target.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal) ? HeapEventKind.Virtual : HeapEventKind.Target,
                            target, block, latches, null, null));
                else if (i.DispatchType is not null)
                {
                    virtuals ??= VirtualCallees(new[] { f });
                    foreach (string name in virtuals.TryGetValue(i, out string[]? named) ? named : new[] { "*" })
                        record.Events.Add(new(HeapEventKind.Virtual, name, block, latches, null, null));
                }
            }
        }
        return record;
    }

    /// <summary>
    /// The allocations of a static initialiser stored straight into a static
    /// field, on no cycle (PermanentStatics): each permanent where nothing
    /// but an initialiser stores to that field, which the whole program says.
    /// </summary>
    private static Dictionary<Instr, string> PermanentCandidates(Function f, HashSet<Block> repeating)
    {
        Dictionary<Instr, string> found = new(ReferenceEqualityComparer.Instance);
        if (!IsStaticInitialiser(f.Name)) return found;
        Dictionary<VReg, (Instr Alloc, Block Block)> made = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && IsAllocator(i.Callee) && i.Dest is not null) made[i.Dest] = (i, b);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[0] is SymOperand { Name: var field } && field.StartsWith("s_", StringComparison.Ordinal)
                    && i.Operands[1] is RegOperand { Reg: var value } && made.TryGetValue(value, out var alloc) && !repeating.Contains(alloc.Block))
                    found.TryAdd(alloc.Alloc, field);
        return found;
    }

    /// <summary>
    /// THE FRESH OBJECTS `f` STORES INTO A FIELD AND LETS GO OF THERE, as
    /// OwnedFields finds them (its held pairs): an allocation, or a call
    /// handing back a fresh object, whose value goes nowhere but that one
    /// store. Where the whole program finds the field owns what it holds,
    /// its owner frees the object: neither an allocation the collector is
    /// needed for, nor a call that leaves what it hands back to it.
    /// </summary>
    private Dictionary<Instr, string> HeldByFields(Function f, Dictionary<string, bool[]> summaries)
    {
        Dictionary<Instr, string> held = new(ReferenceEqualityComparer.Instance);
        RegisterWrites? writes = null;
        foreach (Block b in f.Blocks)
            foreach (Instr st in b.Instrs)
            {
                if (st is not { Op: Opcode.Store, Field: { } field } || st.Operands.Count < 2 || st.Operands[1] is not RegOperand { Reg: var value }) continue;
                writes ??= new RegisterWrites(f);
                // THROUGH COPIES AND JOINS with null or static data -- `site ??
                // ""`, the trace a null fault places -- to the one fresh object
                // the value may be, as OwnedFields' Sources goes: what the field
                // then holds is that object or nothing the owner's free touches.
                Instr? made = null;
                HashSet<VReg> joins = new(), seen = new();
                Stack<VReg> work = new();
                work.Push(value);
                bool other = false;
                while (!other && work.TryPop(out VReg? at))
                {
                    if (!seen.Add(at)) continue;
                    if (seen.Count > 16 || f.Params.Contains(at) || !writes.TryGetValue(at, out WriteList ws)) { other = true; break; }
                    if (ws.Count > 1) joins.Add(at);
                    foreach (Instr w in ws)
                    {
                        if (w.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                        {
                            foreach (Operand o in w.Operands)
                                if (o is RegOperand from) work.Push(from.Reg);
                                else if (o is not (ImmOperand { Value: 0 } or SymOperand)) other = true;
                        }
                        else if (w is { Op: Opcode.Call, Dest: not null } && (IsAllocator(w.Callee) || IsFreshCall(w)) && (made is null || ReferenceEquals(made, w))) made = w;
                        else other = true;
                    }
                }
                if (other || made is not { Dest: { } dest } || held.ContainsKey(made)) continue;
                if (Analyse(f, new[] { dest }, summaries, made, new HashSet<Instr>(ReferenceEqualityComparer.Instance) { st },
                    joinable: joins.Count > 0 ? joins : null).Escapes) continue;
                if (MadeAgainBeforeStored(f, made, st)) continue;
                held[made] = field;
            }
        return held;
    }

    /// <summary>
    /// The names `f` refers to that it would not without a collector: its
    /// code under Runtime.CollectorLinked() answered no, the bookkeeping the
    /// manual heap drops, and the collecting allocators and frees, which
    /// become the manual heap's -- as the end of Run makes it, where a
    /// program needs no collector.
    /// </summary>
    /// <summary>
    /// Every function's names gone without a collector (GoneWithoutCollector),
    /// as the module stands: taken where the IR archive is, before the late
    /// passes, so they are the edges of the archive's references (HeapRecords.Gone).
    /// </summary>
    public static Dictionary<string, string[]> GoneOf(Module m)
    {
        Dictionary<string, string[]> gone = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            if (GoneWithoutCollector(f) is { Count: > 0 } names) gone[f.Name] = names.ToArray();
        return gone;
    }

    private static List<string> GoneWithoutCollector(Function f)
    {
        HashSet<string> all = new(StringComparer.Ordinal), kept = new(StringComparer.Ordinal);
        Dictionary<VReg, int> defs = new();
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Callee is not null) all.Add(i.Callee);
                foreach (Operand o in i.Operands) if (o is SymOperand sym) all.Add(sym.Name);
                if (i.Dest is not null) defs[i.Dest] = defs.GetValueOrDefault(i.Dest) + 1;
            }
            if (b.Terminator is Instr t) foreach (Operand o in t.Operands) if (o is SymOperand sym) all.Add(sym.Name);
        }
        if (all.Count == 0) return new List<string>();
        bool asks = all.Contains(CollectorQuery);
        // What runs with the question answered no: a branch on its answer,
        // or on that answer compared with zero, goes one way only.
        Dictionary<VReg, long> known = new();
        if (asks)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i is { Op: Opcode.Call, Callee: CollectorQuery, Dest: { } answer } && defs[answer] == 1) known[answer] = 0;
        bool grew = asks;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is null || known.ContainsKey(i.Dest)) continue;
                    long? value = i switch
                    {
                        { Op: Opcode.Copy or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Trunc64, Operands: [RegOperand { Reg: var from }] }
                            when known.TryGetValue(from, out long v) => v,
                        { Op: Opcode.Eq, Operands: [RegOperand { Reg: var x }, ImmOperand { Value: var c }] } when known.TryGetValue(x, out long v) => v == c ? 1 : 0,
                        { Op: Opcode.Ne, Operands: [RegOperand { Reg: var x }, ImmOperand { Value: var c }] } when known.TryGetValue(x, out long v) => v != c ? 1 : 0,
                        _ => null,
                    };
                    if (value is long found && defs[i.Dest] == 1) { known[i.Dest] = found; grew = true; }
                }
        }
        HashSet<Block> live = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new();
        if (f.Blocks.Count > 0) { live.Add(f.Blocks[0]); work.Push(f.Blocks[0]); }
        while (work.TryPop(out Block? b))
        {
            IEnumerable<Block> next = b.Terminator is { Op: Opcode.Branch, Operands: [RegOperand { Reg: var cond }] } branch && branch.Targets.Count == 2
                && known.TryGetValue(cond, out long taken)
                ? new[] { taken != 0 ? branch.Targets[0] : branch.Targets[1] }
                : b.Terminator?.Targets ?? (IEnumerable<Block>)Array.Empty<Block>();
            foreach (Instr i in b.Instrs) if (i.Op == Opcode.LabelAddr) next = next.Concat(i.Targets);
            foreach (Block n in next) if (live.Add(n)) work.Push(n);
        }
        foreach (Block b in f.Blocks)
        {
            if (!live.Contains(b)) continue;
            foreach (Instr i in b.Instrs)
            {
                if (i.Callee is string callee)
                {
                    bool dropped = callee == CollectorQuery
                        || i.Dest is null && (IsCollectorNote(callee) || callee is ThreadBlocking or ThreadUnblocking or ThreadSafePoint or ThreadRegister);
                    if (!dropped)
                        kept.Add(callee switch
                        {
                            Allocator or LeafAllocator => ManualAllocator,
                            ObjectAllocator => ManualObjectAllocator,
                            CollectorFreer => ManualFreer,
                            CollectorLive => ManualLive,
                            _ => callee,
                        });
                }
                foreach (Operand o in i.Operands) if (o is SymOperand sym) kept.Add(sym.Name);
            }
            if (b.Terminator is Instr t) foreach (Operand o in t.Operands) if (o is SymOperand sym) kept.Add(sym.Name);
        }
        return all.Where(name => !kept.Contains(name)).Order(StringComparer.Ordinal).ToList();
    }
}
