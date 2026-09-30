#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Escape analysis: an object whose reference never leaves the function
/// that made it does not need the heap.
///
/// The collector is the last resort, not the first (see the design's
/// "Memory" section). Most objects have lifetimes the compiler can prove:
/// the buffer a number is formatted into, an enumerator, a closure that is
/// called and dropped. After inlining, each of those is an allocation and
/// its every use in one function, and this pass decides whether the
/// reference ESCAPES -- is stored somewhere that outlives the function,
/// returned, handed to a callee that keeps it, or captured by anything the
/// analysis cannot see. One that does not escape becomes a frame slot,
/// zeroed as the allocator would have zeroed it, and the call is gone.
///
/// The rules are conservative in every direction the analysis cannot see:
/// a store of the pointer as a VALUE escapes, an indirect call escapes, an
/// atomic escapes, and a callee's parameter escapes unless the callee was
/// analysed and proved otherwise. What does NOT escape: using the pointer
/// as the base of a load or store (that is reading or writing the object),
/// copying it, adding a constant to it (a field address), passing it to a
/// system call (the kernel reads the memory during the call and keeps no
/// pointer -- a rule the runtime keeps by design), and passing it to a
/// callee whose summary says the parameter stays put.
///
/// An allocation inside a loop may be promoted only if nothing derived from
/// the previous iteration's object is still live when the allocation runs
/// again, since the slot is reused: that is the liveness check.
///
/// After promotion, if no allocation is reachable from the entry, the
/// program needs no collector, and Module.NeedsHeap says so.
/// </summary>
public sealed partial class Escape : IModulePass
{
    public string Name => "escape";

    /// <summary>The allocator every `new` calls. Its label is the runtime contract.</summary>
    public const string Allocator = Corsac.Lang.Lto.RuntimeAbi.Alloc;

    /// <summary>The allocator for memory that holds no references (strings, byte arrays).</summary>
    public const string LeafAllocator = Corsac.Lang.Lto.RuntimeAbi.AllocLeaf;

    /// <summary>Runtime.AllocObject: an object the caller gives its vtable next, scanned by its descriptor.</summary>
    public const string ObjectAllocator = Corsac.Lang.Lto.RuntimeAbi.AllocObject;

    /// <summary>Any of the collecting allocators: every pass that follows an allocation follows all three.</summary>
    public static bool IsAllocator(string? callee) => callee == Allocator || callee == LeafAllocator || callee == ObjectAllocator;

    /// <summary>The write barrier compiled code calls with the slot being overwritten.</summary>
    public const string Barrier = Corsac.Lang.Lto.RuntimeAbi.WriteBarrier;

    /// <summary>The same told the overwritten reference itself (Runtime.WriteBarrierValues).</summary>
    public const string ValueBarrier = Corsac.Lang.Lto.RuntimeAbi.WriteBarrierValues;

    /// <summary>At most this many bytes of a frame go to promoted objects.</summary>
    public int FrameBudget { get; init; } = 4096;

    /// <summary>A promoted object may be at most this large.</summary>
    public int ObjectLimit { get; init; } = 1024;

    public int Promoted { get; private set; }

    public void Run(Module m)
    {
        // The frees this run inserted are known to the analyses through a
        // thread's static (_inserted); left set, it kept the last unit's IR
        // alive for as long as the thread lived.
        try { RunCore(m); }
        finally { _inserted = null; _indirect = null; }
    }

    private void RunCore(Module m)
    {
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
        {
            byName[f.Name] = f;
        }
        _defined.UnionWith(byName.Keys);
        _hinting = m.LeavesLinkHints;
        _inserted = _bookkeeping;
        _unresolvedWhy = Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 0 } ? new() : null;
        _module = m;
        _items = null;
        _indirect = IndirectTargets(m, byName);
        _reachGraphs = null;

        // Which parameters of which functions escape: pessimistic until a
        // function has been analysed, bottom-up over the call graph so a
        // callee's answer is known before its callers ask. Cycles keep the
        // pessimistic answer.
        Dictionary<string, bool[]> summaries = new(StringComparer.Ordinal);
        _summaries = summaries;
        foreach (List<Function> cycle in CallCycles(m, byName))
        {
            SummariseCycle(cycle, summaries);
            foreach (Function f in cycle)
            {
            // Whether what it returns is a fresh object it hands over: made
            // here (or by a callee that hands it over in turn), never stored
            // anywhere and never let go of any other way. Its caller then
            // owns the object as if it had made it (Fresh, PromoteIn).
            // As a condition on other units (EscapeHints); fresh here and
            // now when the condition is already true.
            LifetimeCondition? freshHint = FreshHint(f, summaries, out List<Instr>? origins, out HashSet<VReg>? chain);
            _freshHints[f.Name] = freshHint;
            if (freshHint is not null && (freshHint.IsTrue || _hinting))
            {
                LifetimeFields? returned = _hinting ? new() : null;
                FieldSummary left = FreshFields(f, summaries, origins!, chain!, returned);
                if (freshHint.IsTrue)
                {
                    _fresh.Add(f.Name);
                    _freshOrigins[f.Name] = new HashSet<Instr>(origins!, ReferenceEqualityComparer.Instance);
                    _freshFields[f.Name] = left;
                }
                if (returned is not null) _freshFieldHints[f.Name] = returned;
            }
            // What it does to each field of every object it is handed
            // (EscapeFields): the reference fields that hold only objects
            // made for them may be freed with an owner that dies.
            _paramFields[f.Name] = ParameterFields(f, summaries);
            }
        }

        // What is thrown that was not just made, now that the functions
        // handing back fresh objects are known.
        _foreignTypes = !m.PreserveExports && m.Entry is not null ? ForeignThrows(m, summaries) : new HashSet<string>(StringComparer.Ordinal) { "*" };
        bool Provided(string helper) => byName.ContainsKey(helper) || m.RuntimeHelpers.Contains(helper);
        bool canFree = Provided(Freer);
        bool canFreeFields = canFree && Provided(FieldFreer);
        _fieldSites = canFreeFields && Provided(FieldKeeper);
        _unitKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(m.Name)))[..16];
        OwnedFieldEscape fields = new(byName, summaries);
        foreach (Function f in m.Functions)
        {
            PromoteIn(f, summaries, canFree, fields);
            if (canFree) OwnCaught(f, summaries);
            ThrownIn(f, summaries);
            if (canFree && Provided(ReplacedFreer)) OwnVariables(f, summaries);
            if (canFreeFields) OwnFields(f, summaries);
        }

        m.LifetimeHints = _hinting ? Hints(m, Provided) : null;
        PermanentStatics(m);
        if (canFree) OwnedFields(m, byName, summaries);
        // A unit of a larger program: its side of the same judgement, as
        // hints, or the link's answer applied (EscapeOwnedUnits).
        if (canFree) OwnedFieldsInUnit(m, byName, summaries);
        m.NeedsHeap = AnyAllocationReachable(m, byName);
        // A PROGRAM THAT SAID IT RUNS WITHOUT A COLLECTOR gets none: what is
        // left is named, one note each, and is never given back.
        if (m.NeedsHeap && m.NoCollector && !m.PreserveExports && m.Entry is not null && byName.TryGetValue(m.Entry, out Function? main))
        {
            foreach (string site in CollectorSites(m, byName, main, paths: false))
                Console.Error.WriteLine($"note: --no-collector: an allocation in {site} is never given back");
            m.NeedsHeap = false;
        }
        if (Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 0 }) AllocationReport(m, byName);
        LastRun = (Promoted, Owned, OwnedReturns, _fresh.Count, FieldsOwned, VariablesOwned);

        // THE PROGRAM'S ANSWER TO Runtime.CollectorLinked(), now that it is
        // known: a constant, which the passes after this fold, taking what
        // only a collector would do or say out with the branch it was under.
        if (!m.PreserveExports && m.Entry is not null)
        {
            foreach (Function f in m.Functions)
                foreach (Block b in f.Blocks)
                    for (int k = 0; k < b.Instrs.Count; k++)
                        if (b.Instrs[k] is { Op: Opcode.Call, Callee: CollectorQuery, Dest: { } answer } asked)
                            b.Instrs[k] = new Instr
                            {
                                Op = Opcode.Copy, Dest = answer, Line = asked.Line,
                                Operands = { new ImmOperand(m.NeedsHeap ? 1 : 0, answer.Type) },
                            };
        }

        // A PROGRAM THAT NEEDS NO COLLECTOR STILL ALLOCATES AND FREES: what
        // the compiler owns is given back where it dies, and what lives to
        // the end or is made once is never given back. That is malloc and
        // free, which the runtime provides beside the collector and sharing
        // nothing with it (Runtime.AllocManual). Every allocation goes there,
        // and the collector's free and its liveness test -- all the runtime's
        // own frees reach -- become the manual heap's; then nothing calls
        // into the collector, and it goes the way of every unreachable
        // function, its stack maps with it (the driver asks NeedsHeap).
        if (!m.NeedsHeap && byName.ContainsKey(ManualAllocator) && byName.ContainsKey(ManualObjectAllocator)
            && byName.ContainsKey(ManualFreer) && byName.ContainsKey(ManualLive))
        {
            foreach (Function f in m.Functions)
            {
                foreach (Block b in f.Blocks)
                {
                    for (int k = 0; k < b.Instrs.Count; k++)
                    {
                        Instr i = b.Instrs[k];
                        if (i.Op != Opcode.Call)
                        {
                            continue;
                        }
                        // Bookkeeping for a collector there will not be: a
                        // barrier, a card mark, a mark, a thread's word that
                        // it is blocking or at a safepoint. None has any other
                        // effect, and each would keep the collector linked.
                        if (i.Dest is null && (IsCollectorNote(i.Callee) || i.Callee is ThreadBlocking or ThreadUnblocking or ThreadSafePoint or ThreadRegister))
                        {
                            b.Instrs.RemoveAt(k);
                            k--;
                            continue;
                        }
                        string? to = i.Callee switch
                        {
                            Allocator or LeafAllocator => ManualAllocator,
                            ObjectAllocator => ManualObjectAllocator,
                            CollectorFreer => ManualFreer,
                            CollectorLive => ManualLive,
                            _ => null,
                        };
                        if (to is null)
                        {
                            continue;
                        }

                        Instr retargeted = new() { Op = Opcode.Call, Dest = i.Dest, Callee = to, Line = i.Line };
                        retargeted.Operands.AddRange(i.Operands);
                        b.Instrs[k] = retargeted;
                    }
                }
            }
        }
    }

    /// <summary>The heap of a program that needs no collector: malloc, for a block and for an object.</summary>
    public const string ManualAllocator = Corsac.Lang.Lto.RuntimeAbi.AllocManual;
    public const string ManualObjectAllocator = Corsac.Lang.Lto.RuntimeAbi.AllocManualObject;

    /// <summary>Its free, and whether a pointer is a live object of it (what Gc.Free and Gc.LiveObject become).</summary>
    public const string ManualFreer = "m_Runtime_FreeManual_1_V$I64";
    public const string ManualLive = "m_Runtime_ManualObject_1_V$I64";

    /// <summary>The collector's own free and liveness test, which the runtime's frees call.</summary>
    public const string CollectorFreer = "m_Gc_Free_1_V$I64";
    public const string CollectorLive = "m_Gc_LiveObject_1_V$I64";

    /// <summary>A thread telling the collector it blocks, is back, or is at a safepoint.</summary>
    public const string ThreadBlocking = "m_GcThreads_BeginBlocking_0";
    public const string ThreadUnblocking = "m_GcThreads_EndBlocking_0";
    public const string ThreadSafePoint = "m_GcThreads_SafePoint_1_V$I64";
    /// <summary>The runtime asking whether the program has a collector (answered here, per program).</summary>
    public const string CollectorQuery = "m_Runtime_CollectorLinked_0";

    /// <summary>A thread made known to the collector as it starts.</summary>
    public const string ThreadRegister = "m_GcThreads_Register_1_V$I64";

    /// <summary>
    /// Giving a block back by hand. The compiler calls this only for an
    /// object whose whole life it has proved, which is why the runtime may
    /// keep it as cheap as it likes; it ignores anything that is not the
    /// payload of a live block, so freeing a zero is a no-op.
    /// </summary>
    public const string Freer = Corsac.Lang.Lto.RuntimeAbi.Free;

    /// <summary>How many allocations this pass gave an explicit free rather than a frame slot.</summary>
    public int Owned { get; private set; }

    /// <summary>
    /// Allocation calls this pass took ownership of. They keep calling the
    /// real allocator -- a bump region cannot give memory back -- but they
    /// are not a reason to link a collector, because their memory comes
    /// back by a free the compiler placed, not by a collection.
    /// </summary>
    private readonly HashSet<Instr> _owned = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The instructions that make a promoted object: the copy of its frame
    /// slot's address that stands where its allocation was. Such an object
    /// is this function's own, proved never to escape it, and zeroed where it
    /// is made, as the allocator's memory is.
    /// </summary>
    private readonly HashSet<Instr> _promotedMade = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether `made` makes an object no other thread can see: a promoted
    /// object, or an allocation of this function that never escapes it.
    /// Replacing an owned field's value frees the old one only in such an
    /// object (OwnedFields, ApplyOwnedFields).
    /// </summary>
    private bool PrivateOwner(Function f, Instr made, Dictionary<string, bool[]> summaries, Dictionary<Instr, bool> known)
    {
        if (_promotedMade.Contains(made)) return true;
        if (made is not { Op: Opcode.Call, Dest: { } ownerReg } || !IsAllocator(made.Callee)) return false;
        if (!known.TryGetValue(made, out bool isPrivate))
            known[made] = isPrivate = !Analyse(f, new[] { ownerReg }, summaries, made).Escapes;
        return isPrivate;
    }

    // ---- fresh returns ----------------------------------------------------------------
    //
    // AN OBJECT HANDED OVER IS OWNED BY WHOEVER TAKES IT. A helper that makes
    // an object and returns it -- the value array a blit is built in, a list
    // of clip rectangles, a string made of pieces -- lets it escape by
    // returning it, and on that ground alone every such object used to be the
    // collector's, however short its life in the caller. But a return is not
    // a store: the helper keeps nothing, and the caller receives the only
    // reference there is. So a function whose every return is an object it
    // made (or received from a callee that hands its object over in turn, or
    // null), and which lets that object go no other way, is summarised as
    // RETURNING FRESH; and at a call to such a function the result is an
    // owned allocation of the caller, with the caller's escape and liveness
    // proofs, freed at its last use like any other (Own). Chains of helpers
    // compose, because a function that returns a fresh call's result
    // unchanged is itself fresh.

    /// <summary>Functions whose every return hands over a fresh object.</summary>
    private HashSet<string> _fresh = new(StringComparer.Ordinal);

    /// <summary>Calls to fresh-returning functions whose result this pass took ownership of.</summary>
    private readonly HashSet<Instr> _ownedCalls = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// For each function that returns a fresh object, what makes the object it
    /// returns: its allocations and the calls to fresh callees it hands on.
    /// Those are the caller's to free wherever the call is owned.
    /// </summary>
    private readonly Dictionary<string, HashSet<Instr>> _freshOrigins = new(StringComparer.Ordinal);

    /// <summary>The runtime's routine a catch body's end calls (Lowering.EndCatch).</summary>
    /// <summary>The runtime's end of a catch body's hold on its exception (Runtime.CatchEnd).</summary>
    public const string CatchEnder = "m_Runtime_CatchEnd_1_V$Any";

    public static bool IsCatchEnd(string? callee) => callee is not null && callee.StartsWith("m_Runtime_CatchEnd_1_", StringComparison.Ordinal);

    /// <summary>The runtime's routine an exception nothing catches goes to.</summary>
    public const string Unhandled = "m_Runtime_Unhandled_1_V$Any";

    /// <summary>
    /// The types of the catches that keep what they caught (OwnCaught), null
    /// for one that takes everything: a thrown object any of them can take is
    /// the collector's.
    /// </summary>
    private readonly HashSet<string> _keptCatches = new(StringComparer.Ordinal);
    private bool _keptCatchAll;
    private void KeepCatch(Instr end) { if (end.DispatchType is string t) _keptCatches.Add(t); else _keptCatchAll = true; }

    /// <summary>The exact type of each thrown allocation (ThrownIn): the descriptor its header is stamped with.</summary>
    private readonly Dictionary<Instr, string> _thrownType = new(ReferenceEqualityComparer.Instance);

    /// <summary>Allocations whose only way out is being thrown (ThrownIn).</summary>
    private readonly HashSet<Instr> _thrown = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A CAUGHT EXCEPTION IS THE CATCH'S TO FREE. Lowering ends each catch
    /// body's hold on its exception at every way out but handing it on
    /// (Runtime.CatchEnd). The exception is in the slot the landing pad kept
    /// it in; if nothing the body does with it lets it go anywhere -- stored,
    /// captured, handed to a callee that keeps it -- each end becomes a free.
    /// Rethrowing hands it on and is not an escape. Otherwise the ends stay
    /// calls that do nothing, and some catch is known to keep what it caught.
    /// </summary>
    /// <summary>
    /// What the program throws that it did not just make: `throw error` of one
    /// held elsewhere, a stored exception thrown again (a type initializer's
    /// failure, ExceptionDispatchInfo). A catch frees what it caught only when
    /// nothing held elsewhere can arrive there, so a catch that can take one
    /// of these types keeps its exception. The types come from the static a
    /// thrown value is read from and the objects every store puts there;
    /// where they cannot be told, "*": every catch keeps.
    /// </summary>
    private HashSet<string> _foreignTypes = new(StringComparer.Ordinal);
    private Module? _module;
    private Dictionary<string, DataItem>? _items;

    private HashSet<string> ForeignThrows(Module m, Dictionary<string, bool[]> summaries)
    {
        HashSet<string> types = new(StringComparer.Ordinal);
        Dictionary<string, List<(Function F, Instr Store)>> staticStores = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Operands.Count == 2 && i.Operands[0] is SymOperand { Name: var into })
                    {
                        if (!staticStores.TryGetValue(into, out var list)) staticStores[into] = list = new();
                        list.Add((f, i));
                    }
        // Only what the program can reach throws anything.
        HashSet<string> reached = Reached(m);
        foreach (Function f in m.Functions.Where(g => reached.Contains(g.Name)))
        {
            Dictionary<VReg, List<Instr>> writes = Writes(f);
            // Registers holding what a landing pad received: `throw;` hands it on.
            HashSet<FrameSlot> caughtSlots = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Operands.Count == 2 && i.Operands[0] is SlotOperand { Slot: var slot }
                        && i.Operands[1] is RegOperand { Reg: var v } && writes.TryGetValue(v, out List<Instr>? vw)
                        && vw.Count == 1 && vw[0] is { Op: Opcode.Call, Callee: "__exception" })
                        caughtSlots.Add(slot);
            // WHAT A CATCH HERE KEEPS, handed on by `throw;`, is still held by
            // whatever kept it: a catch further out may not free it. Kept =
            // anything received from a landing pad escapes this function.
            bool? caughtKept = null;
            bool CaughtKept()
            {
                if (caughtKept is bool known) return known;
                HashSet<VReg> roots = new();
                foreach (Block cb in f.Blocks)
                    foreach (Instr ci in cb.Instrs)
                        if (ci.Dest is not null && (ci.Op == Opcode.Call && ci.Callee == "__exception"
                            || ci.Op == Opcode.Load && ci.Operands is [SlotOperand { Slot: var s }] && caughtSlots.Contains(s)))
                            roots.Add(ci.Dest);
                return (caughtKept = roots.Count > 0 && Analyse(f, roots, summaries, null, handOff: true).Escapes).Value;
            }
            // Fresh, handed on, or read from a static (whose name is answered).
            // FRESH AND STILL ONLY THE THROW'S: an object made here but kept
            // somewhere before it was thrown (a log, a static) is not the
            // catch's to free -- its type is foreign.
            bool Fresh(Operand o, out string? fromStatic)
            {
                fromStatic = null;
                for (int hop = 0; hop < 8 && o is RegOperand { Reg: var r }; hop++)
                {
                    if (!writes.TryGetValue(r, out List<Instr>? ws) || ws.Count != 1) return false;
                    Instr d = ws[0];
                    if (d.Op == Opcode.Call && (IsAllocator(d.Callee) || d.Callee is not null && _fresh.Contains(d.Callee)))
                    {
                        if (d.Dest is not null && Analyse(f, new[] { d.Dest }, summaries, d, handOff: true).Escapes)
                            types.Add(StampedType(f, d.Dest) ?? "*");
                        return true;
                    }
                    if (d.Op == Opcode.Call && d.Callee == "__exception" || d.Op == Opcode.Load && d.Operands is [SlotOperand { Slot: var from }] && caughtSlots.Contains(from))
                    {
                        if (CaughtKept()) types.Add("*");
                        return true;
                    }
                    if (d.Op == Opcode.Load && d.Operands is [SymOperand { Name: var named }]) { fromStatic = named; return false; }
                    if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands.Count != 1) return false;
                    o = d.Operands[0];
                }
                return false;
            }
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    Operand? thrown = i.Op == Opcode.Unwind && i.Operands.Count >= 2 ? i.Operands[1]
                        : i.Op == Opcode.Call && i.Callee == Unhandled && i.Operands.Count == 1 ? i.Operands[0] : null;
                    if (thrown is null || Fresh(thrown, out string? stat)) continue;
                    bool known = stat is not null && staticStores.TryGetValue(stat, out var into) && into.All(w =>
                    {
                        if (w.Store.Operands[1] is ImmOperand { Value: 0 }) return true;
                        if (w.Store.Operands[1] is not RegOperand { Reg: var put }) return false;
                        string? type = StampedType(w.F, put);
                        if (type is null) return false;
                        types.Add(type);
                        return true;
                    });
                    if (!known) types.Add("*");
                    if (Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 0 })
                        Console.Error.WriteLine($"alloc report: {f.Name} throws what it did not make ({(known ? "from " + stat : "unknown")}); catches that can take it keep it");
                }
        }
        return types;
    }

    /// <summary>
    /// ForeignThrows for a unit that is not the whole program: each throw of
    /// something not made here, said so the link can judge it with every
    /// unit's (LifetimeHints.Throws): unknown, read from a static, or the
    /// result of a function only the whole program can call fresh. And every
    /// static store's stamped type, and the statics stored outside a static
    /// initialiser, for the same judgement and for PermanentStatics.
    /// </summary>
    private void ThrowHints(Module m, LifetimeHints hints)
    {
        foreach (Function f in m.Functions)
        {
            bool initialiser = f.Name.Contains("_StaticInit$", StringComparison.Ordinal);
            Dictionary<VReg, List<Instr>> writes = Writes(f);
            HashSet<FrameSlot> caughtSlots = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Store && i.Operands.Count == 2 && i.Operands[0] is SlotOperand { Slot: var slot }
                        && i.Operands[1] is RegOperand { Reg: var v } && writes.TryGetValue(v, out List<Instr>? vw)
                        && vw.Count == 1 && vw[0] is { Op: Opcode.Call, Callee: "__exception" })
                        caughtSlots.Add(slot);
                    if (i.Op == Opcode.Store && i.Operands.Count == 2 && i.Operands[0] is SymOperand { Name: var into })
                    {
                        if (!initialiser) hints.StaticWrites.Add(into);
                        if (i.Operands[1] is ImmOperand { Value: 0 }) continue;
                        string? type = i.Operands[1] is RegOperand { Reg: var put } ? StampedType(f, put) : null;
                        hints.StaticStores.Add((into, type ?? "*"));
                    }
                }
            // Made here, handed on from a landing pad, or else what the link must judge.
            string? Verdict(Operand o)
            {
                for (int hop = 0; hop < 8 && o is RegOperand { Reg: var r }; hop++)
                {
                    if (!writes.TryGetValue(r, out List<Instr>? ws) || ws.Count != 1) return "*";
                    Instr d = ws[0];
                    if (d.Op == Opcode.Call && (IsAllocator(d.Callee) || d.Callee is not null && _fresh.Contains(d.Callee))) return null;
                    if (d.Op == Opcode.Call && d.Callee == "__exception") return null;
                    if (d.Op == Opcode.Call && d.Callee is not null && !IsIntrinsic(d.Callee)) return "f:" + d.Callee;
                    if (d.Op == Opcode.Load && d.Operands is [SlotOperand { Slot: var from }] && caughtSlots.Contains(from)) return null;
                    if (d.Op == Opcode.Load && d.Operands is [SymOperand { Name: var named }]) return "s:" + named;
                    if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands.Count != 1) return "*";
                    o = d.Operands[0];
                }
                return "*";
            }
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    Operand? thrown = i.Op == Opcode.Unwind && i.Operands.Count >= 2 ? i.Operands[1]
                        : i.Op == Opcode.Call && i.Callee == Unhandled && i.Operands.Count == 1 ? i.Operands[0] : null;
                    if (thrown is not null && Verdict(thrown) is string verdict) hints.Throws.Add(verdict);
                }
        }
    }

    /// <summary>What the entry and static initialisers reach over calls, named addresses and data.</summary>
    private static HashSet<string> Reached(Module m)
    {
        // A descriptor's methods are reached by the calls that reach them when
        // every virtual call is resolved (IndirectTargets), as CollectorSites
        // walks; otherwise by the descriptor itself.
        bool everyMethod = _indirect is null || m.Functions.Any(f => f.Blocks.Any(b => b.Instrs.Any(i =>
            i.Op == Opcode.CallIndirect && i.DispatchType is not null && !_indirect.ContainsKey(i))));
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions) byName[f.Name] = f;
        Dictionary<string, DataItem> data = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) data[d.Name] = d;
        HashSet<string> reached = new(StringComparer.Ordinal);
        Stack<string> pending = new();
        void Reach(string name) { if (reached.Add(name)) pending.Push(name); }
        if (m.Entry is not null) Reach(m.Entry);
        foreach (Function f in m.Functions) if (f.Name.Contains("StaticInit$", StringComparison.Ordinal)) Reach(f.Name);
        while (pending.TryPop(out string? name))
        {
            if (data.TryGetValue(name, out DataItem? item))
            {
                bool descriptor = IsDescriptor(name);
                foreach (DataReloc r in item.Relocs)
                    if (everyMethod || !descriptor || !byName.ContainsKey(r.Symbol)) Reach(r.Symbol);
                continue;
            }
            if (!byName.TryGetValue(name, out Function? g)) continue;
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Callee is not null) Reach(i.Callee);
                    foreach (Operand o in i.Operands) if (o is SymOperand sym) Reach(sym.Name);
                    if (i.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(i, out string[]? targets))
                        foreach (string t in targets) Reach(t);
                }
        }
        return reached;
    }

    /// <summary>The descriptor stamped into the object an allocation made and `r` holds, or null.</summary>
    private static string? StampedType(Function f, VReg r)
    {
        Dictionary<VReg, List<Instr>> writes = Writes(f);
        VReg? made = null;
        VReg at = r;
        for (int hop = 0; hop < 8; hop++)
        {
            if (!writes.TryGetValue(at, out List<Instr>? ws) || ws.Count != 1) return null;
            Instr d = ws[0];
            if (d.Op == Opcode.Call && IsAllocator(d.Callee)) { made = d.Dest; break; }
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands is not [RegOperand next]) return null;
            at = next.Reg;
        }
        if (made is null) return null;
        foreach (Block b in f.Blocks)
            foreach (Instr st in b.Instrs)
                if (st.Op == Opcode.Store && st.Offset == 0 && st.Operands.Count >= 2 && st.Operands[1] is SymOperand { Name: var t }
                    && t.StartsWith("t_", StringComparison.Ordinal) && st.Operands[0] is RegOperand { Reg: var into }
                    && (into == made || Stamped(f, into, made)))
                    return t;
        return null;
    }

    /// <summary>Whether a catch can take something thrown that was not just made (_foreignTypes).</summary>
    private bool CatchesForeign(Instr end)
    {
        // The link's answer for a unit of a closed image (Module.ForeignCatchable).
        if (_module is { PreserveExports: true, ForeignCatchable: { } catchable })
            return catchable.Count > 0 && (end.DispatchType is not string taken || catchable.Contains(taken));
        if (_foreignTypes.Count == 0) return false;
        if (_foreignTypes.Contains("*") || end.DispatchType is not string caught) return true;
        Dictionary<string, DataItem> items = _items ??= _module!.Data.ToDictionary(d => d.Name, StringComparer.Ordinal);
        return _foreignTypes.Any(t => Ancestry(items, t).Contains(caught));
    }

    private void OwnCaught(Function f, Dictionary<string, bool[]> summaries)
    {
        if (f.Async is not null)
        {
            foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (i.Op == Opcode.Call && IsCatchEnd(i.Callee)) KeepCatch(i);
            return;
        }
        Dictionary<VReg, Instr> defs = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is not null) defs[i.Dest] = i;
        Dictionary<FrameSlot, List<(Block Block, Instr End)>> ends = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && IsCatchEnd(i.Callee) && i.Operands.Count == 1 && i.Operands[0] is RegOperand held
                    && defs.TryGetValue(held.Reg, out Instr? load) && load.Op == Opcode.Load && load.Operands.Count >= 1
                    && load.Operands[0] is SlotOperand { Slot: var keep })
                {
                    if (!ends.TryGetValue(keep, out var list)) ends[keep] = list = new();
                    list.Add((b, i));
                }
                else if (i.Op == Opcode.Call && IsCatchEnd(i.Callee)) KeepCatch(i);
        foreach ((FrameSlot keep, var list) in ends)
        {
            HashSet<VReg> roots = new();
            HashSet<Instr> stores = new(ReferenceEqualityComparer.Instance);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Load && i.Dest is not null && i.Operands.Count >= 1 && i.Operands[0] is SlotOperand { Slot: var from } && from == keep)
                        roots.Add(i.Dest);
                    if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[0] is SlotOperand { Slot: var into } && into == keep)
                    {
                        stores.Add(i);
                        if (i.Operands[1] is RegOperand value) roots.Add(value.Reg);
                    }
                }
            Flow flow = Analyse(f, roots, summaries, null, stores, handOff: true);
            if (flow.Escapes || list.Any(e => CatchesForeign(e.End)) || UsedAfterEnd(f, keep, flow, list))
            {
                foreach ((_, Instr end) in list) KeepCatch(end);
                continue;
            }
            foreach ((Block b, Instr end) in list)
            {
                // Runtime.Free takes a long on every target (AppendFree).
                List<Instr> made = new();
                Instr free = AppendFree(f, made, ((RegOperand)end.Operands[0]).Reg, end.Line);
                int at = b.Instrs.IndexOf(end);
                b.Instrs.RemoveAt(at);
                b.Instrs.InsertRange(at, made);
                _bookkeeping.UnionWith(made);
                Owned++;
            }
        }
    }

    /// <summary>
    /// Whether what a catch holds is still used after one of its ends: a
    /// register derived from it live past the end or into a handler, or the
    /// catch's slot read again where the end reaches. The end is where the
    /// free would go, and lowering does not always put it last -- `throw x;`
    /// of an alias of the caught object, and a finally inside the catch that
    /// a return leaves through, both end the catch first.
    /// </summary>
    private static bool UsedAfterEnd(Function f, FrameSlot keep, Flow flow, List<(Block Block, Instr End)> ends)
    {
        Liveness liveness = new(f);
        HashSet<VReg> pads = PadLive(liveness);
        if (flow.Derived.Any(r => !liveness.Tracks(r) || pads.Contains(r))) return true;
        bool ReadsKept(Instr i) => i.Operands.Any(o => o is RegOperand r && flow.Derived.Contains(r.Reg))
            || i.Op == Opcode.Load && i.Operands.Count >= 1 && i.Operands[0] is SlotOperand { Slot: var s } && s == keep;
        foreach ((Block b, Instr end) in ends)
        {
            int at = b.Instrs.IndexOf(end);
            for (int k = at + 1; k < b.Instrs.Count; k++) if (ReadsKept(b.Instrs[k])) return true;
            if (b.Terminator is Instr term && ReadsKept(term)) return true;
            if (flow.Derived.Any(r => liveness.IsLiveOut(b, r))) return true;
            foreach (Block x in f.Blocks)
                if (Reaches(f, b, x) && (x.Instrs.Any(ReadsKept) || x.Terminator is Instr t && ReadsKept(t))) return true;
        }
        return false;
    }

    /// <summary>
    /// A thrown allocation is covered if no catch that keeps what it caught can
    /// take its type: a catch of T takes X when T is X or an ancestor of X.
    /// </summary>
    private bool ThrownCovered(Instr i, Dictionary<string, DataItem> items)
    {
        if (!_thrown.Contains(i) || _keptCatchAll || !_thrownType.TryGetValue(i, out string? type)) return false;
        if (_keptCatches.Count == 0) return true;
        HashSet<string> ancestors = Ancestry(items, type);
        return !_keptCatches.Any(ancestors.Contains);
    }

    /// <summary>Whether register `into` is `made` narrowed or copied (the stamping store's address).</summary>
    private static bool Stamped(Function f, VReg into, VReg made)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr d in b.Instrs)
                if (d.Dest == into && d.Op is Opcode.Copy or Opcode.Trunc64 && d.Operands.Count == 1 && d.Operands[0] is RegOperand { Reg: var from } && from == made)
                    return true;
        return false;
    }

    /// <summary>
    /// An allocation whose only way out of its function is being thrown: what
    /// catches it frees it (OwnCaught), and nothing catching it is the program
    /// ending. Covered only where no catch keeps what it caught.
    /// </summary>
    private void ThrownIn(Function f, Dictionary<string, bool[]> summaries)
    {
        if (f.Async is not null) return;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.Call || !IsAllocator(i.Callee) || i.Dest is null || _owned.Contains(i)) continue;
                if (Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 1 } which && f.Name.Contains(which, StringComparison.Ordinal))
                {
                    Flow why = Analyse(f, new[] { i.Dest }, summaries, i, handOff: true);
                    Console.Error.WriteLine($"alloc report: {f.Name}:{i.Line} escapes={why.Escapes} via {why.Why?.Op} {why.Why?.Callee} {string.Join(" ", why.Why?.Operands.Select(o => o.ToString()) ?? Array.Empty<string>())}");
                }
                if (Analyse(f, new[] { i.Dest }, summaries, i).Escapes && !Analyse(f, new[] { i.Dest }, summaries, i, handOff: true).Escapes)
                {
                    _thrown.Add(i);
                    // Its exact type: the descriptor stamped into its first word.
                    foreach (Block s2 in f.Blocks)
                        foreach (Instr st in s2.Instrs)
                            if (st.Op == Opcode.Store && st.Offset == 0 && st.Operands.Count >= 2 && st.Operands[1] is SymOperand { Name: var t }
                                && t.StartsWith("t_", StringComparison.Ordinal) && st.Operands[0] is RegOperand { Reg: var into }
                                && (into == i.Dest || Stamped(f, into, i.Dest)))
                                _thrownType[i] = t;
                }
            }
    }

    /// <summary>The runtime's free of what an owned field held before a store replaces it.</summary>
    public const string OwnedReplacedFreer = Corsac.Lang.Lto.RuntimeAbi.FreeOwnedReplaced;

    /// <summary>The owned-field decisions, for the allocation report.</summary>
    private readonly List<string> _fieldReport = new();

    /// <summary>Allocations an owned field holds (OwnedFields): freed with the object that owns the field.</summary>
    private readonly HashSet<Instr> _fieldOwned = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A FIELD THAT OWNS WHAT IT HOLDS, over the whole program: every store
    /// into it, anywhere, stores an object made for it -- a fresh allocation or
    /// a fresh function's result that nothing else keeps -- or null or static
    /// data; and every read of it keeps the value within the reading block,
    /// letting it go nowhere and dead before anything that could free it (a
    /// free, a catch's end, a call that may store into the field, a call this
    /// pass cannot see). Then the field's objects are the owner's: a store
    /// that may replace one frees it first (Runtime.FreeOwnedReplaced), and
    /// freeing the owner frees them, by the owned-field map its type's
    /// descriptor carries (word 11, read by Runtime.FreeOwnedFields) -- the
    /// dynamic type's, so a subclass's own fields go too. Instance fields of
    /// classes only (lowering tags no other): a struct is copied byte for byte.
    /// </summary>
    private void OwnedFields(Module m, Dictionary<string, Function> byName, Dictionary<string, bool[]> summaries)
    {
        if (m.PreserveExports || m.Entry is null || !byName.ContainsKey(OwnedReplacedFreer) && !m.RuntimeHelpers.Contains(OwnedReplacedFreer)) return;

        List<(Function F, Block B, Instr I)> stores = new(), loads = new(), fieldAddresses = new();
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Field is null || i.Operands.Count < 1 || i.Operands[0] is SymOperand) continue;
                    if (i.Op == Opcode.Store) stores.Add((f, b, i));
                    else if (i.Op == Opcode.Load) loads.Add((f, b, i));
                    // A field's address taken (ref, out, Interlocked on it):
                    // what is read and written through it is none of the
                    // loads and stores above, and proves nothing.
                    else fieldAddresses.Add((f, b, i));
                }
        HashSet<string> candidates = new(stores.Select(s => s.I.Field!).Concat(loads.Select(l => l.I.Field!)), StringComparer.Ordinal);
        if (candidates.Count == 0) return;

        // Who may store into each field: the functions that do, and every
        // function that may call one of them.
        Dictionary<string, List<Function>> callers = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    IEnumerable<string> callees = i.Op == Opcode.Call && i.Callee is not null ? new[] { i.Callee }
                        : i.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(i, out string[]? t) ? t : Array.Empty<string>();
                    foreach (string c in callees)
                    {
                        if (!callers.TryGetValue(c, out List<Function>? list)) callers[c] = list = new();
                        list.Add(f);
                    }
                }
        Dictionary<string, HashSet<string>> mayWrite = new(StringComparer.Ordinal);
        // Whether a store fills a field of an object made for it (assigned
        // below, once the call sites are known): it cannot touch an object
        // anyone else is reading.
        Func<Function, Instr, bool> initializing = (_, _) => false;
        HashSet<string> MayWrite(string field)
        {
            if (mayWrite.TryGetValue(field, out HashSet<string>? known)) return known;
            HashSet<string> set = new(StringComparer.Ordinal);
            Stack<string> work = new(stores.Where(s => s.I.Field == field && !initializing(s.F, s.I)).Select(s => s.F.Name));
            while (work.Count > 0)
            {
                string name = work.Pop();
                if (!set.Add(name)) continue;
                if (callers.TryGetValue(name, out List<Function>? up)) foreach (Function c in up) work.Push(c.Name);
            }
            return mayWrite[field] = set;
        }

        // Functions whose address is taken -- named by code or by a descriptor
        // -- may be called where their callers cannot be seen.
        HashSet<string> addressed = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) foreach (DataReloc r in d.Relocs) addressed.Add(r.Symbol);
        foreach (Function f in m.Functions) foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs)
            foreach (Operand o in i.Operands) if (o is SymOperand sym) addressed.Add(sym.Name);
        // Functions a descriptor names are called through their slots, and
        // those calls are resolved (IndirectTargets); any other taken
        // address may be called from where nothing can be seen.
        HashSet<string> slotted = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) if (IsDescriptor(d.Name)) foreach (DataReloc r in d.Relocs) slotted.Add(r.Symbol);
        HashSet<string> codeNamed = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions) foreach (Block bb in g.Blocks) foreach (Instr i in bb.Instrs)
            foreach (Operand o in i.Operands) if (o is SymOperand sym) codeNamed.Add(sym.Name);
        bool unresolvedVirtual = m.Functions.Any(g => g.Blocks.Any(bb => bb.Instrs.Any(i => i.Op == Opcode.CallIndirect && i.DispatchType is not null
            && (_indirect is null || !_indirect.ContainsKey(i)))));
        bool IsVirtualTarget(string name) => slotted.Contains(name) && !codeNamed.Contains(name) && !unresolvedVirtual;
        // Each function's direct call sites, found once.
        Dictionary<string, List<(Function G, Block B, int At)>> sites = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions)
            foreach (Block b in g.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                    if (b.Instrs[k] is { Op: Opcode.Call, Callee: string callee })
                    {
                        if (!sites.TryGetValue(callee, out var list)) sites[callee] = list = new();
                        list.Add((g, b, k));
                    }
        Dictionary<Function, Dictionary<VReg, List<Instr>>> writesOf = new();
        Dictionary<(Function, int), List<Instr>?> sinks = new();
        Dictionary<Function, Liveness> sinkLiveness = new();
        // WHAT EVERY CALLER HANDS OVER FOR PARAMETER `index`: the fresh objects
        // taken over, or null when some caller does not hand one over.
        List<Instr>? Sink(Function f, int index, int depth)
        {
            if (sinks.TryGetValue((f, index), out List<Instr>? known)) return known;
            sinks[(f, index)] = null;       // in progress: not a sink, until shown one
            if (depth > 6 || addressed.Contains(f.Name) || f == byName.GetValueOrDefault(m.Entry!)) return null;
            List<Instr> handed = new();
            bool any = false;
            foreach ((Function g, Block b, int k) in sites.GetValueOrDefault(f.Name) ?? new())
                    {
                        Instr c = b.Instrs[k];
                        any = true;
                        if (index >= c.Operands.Count) return null;
                        Operand arg = c.Operands[index];
                        if (arg is ImmOperand { Value: 0 } or SymOperand) continue;
                        if (arg is not RegOperand a || Sources(g, a.Reg) is not { } sources) return null;
                        foreach (Source src in sources)
                        {
                            if (src.Kind is SourceKind.Null or SourceKind.Static) continue;
                            if (src.Kind == SourceKind.Parameter)
                            {
                                if (Analyse(g, new[] { g.Params[src.Index] }, summaries, null, consumers: new(ReferenceEqualityComparer.Instance) { c }).Escapes
                                    || Sink(g, src.Index, depth + 1) is not List<Instr> up) return null;
                                handed.AddRange(up);
                                continue;
                            }
                            if (src.Kind != SourceKind.Fresh || src.Made!.Dest is null) return null;
                            // Freed by the caller already (another rule owns it):
                            // it cannot be handed on as well.
                            if (_owned.Contains(src.Made) || _ownedCalls.Contains(src.Made)) return null;
                            Flow flow = Analyse(g, new[] { src.Made.Dest }, summaries, src.Made, consumers: new(ReferenceEqualityComparer.Instance) { c });
                            if (flow.Escapes) return null;
                            // Nothing of it read after the call that took it.
                            if (!sinkLiveness.TryGetValue(g, out Liveness? live)) sinkLiveness[g] = live = new Liveness(g);
                            if (flow.Derived.Any(r => !live.Tracks(r) || live.IsLiveOut(b, r))) return null;
                            for (int after = k + 1; after < b.Instrs.Count; after++)
                                if (b.Instrs[after].Operands.Any(o => o is RegOperand r && flow.Derived.Contains(r.Reg))) return null;
                            handed.Add(src.Made);
                        }
                    }
            return sinks[(f, index)] = any ? handed : null;
        }

        // HANDED OVER AT THE STORE: once the field holds the object, nothing
        // here reads it any other way -- no register of it read after the
        // store in its block, none live out of it. A field that owns an
        // object its maker still uses would free it under the maker.
        bool HandedOver(Function f, Block b, Instr st, HashSet<VReg> derived)
        {
            if (!sinkLiveness.TryGetValue(f, out Liveness? live)) sinkLiveness[f] = live = new Liveness(f);
            if (derived.Any(r => !live.Tracks(r) || live.IsLiveOut(b, r))) return false;
            for (int after = b.Instrs.IndexOf(st) + 1; after < b.Instrs.Count; after++)
            {
                Instr i = b.Instrs[after];
                if (_bookkeeping.Contains(i)) continue;
                if (i.Operands.Any(o => o is RegOperand r && derived.Contains(r.Reg))) return false;
            }
            return true;
        }

        // Where a value comes from, through copies and joins.
        Dictionary<(Function, VReg), List<Source>?> sourcesOf = new();
        List<Source>? Sources(Function f, VReg r)
        {
            if (sourcesOf.TryGetValue((f, r), out List<Source>? known)) return known;
            List<Source> found = new();
            HashSet<VReg> seen = new();
            Stack<VReg> work = new();
            work.Push(r);
            if (!writesOf.TryGetValue(f, out Dictionary<VReg, List<Instr>>? writes)) writesOf[f] = writes = Writes(f);
            while (work.Count > 0 && found.Count < 16)
            {
                VReg at = work.Pop();
                if (!seen.Add(at)) continue;
                int param = f.Params.IndexOf(at);
                if (param >= 0) { found.Add(new Source(SourceKind.Parameter, param, null)); continue; }
                if (!writes.TryGetValue(at, out List<Instr>? ws)) { found.Add(new Source(SourceKind.Unknown, 0, null)); continue; }
                foreach (Instr w in ws)
                {
                    if (w.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                    {
                        foreach (Operand o in w.Operands)
                        {
                            // Null and static data are one kind each however
                            // often they come: a table of messages is many
                            // literals, and only the other sources count.
                            if (o is ImmOperand { Value: 0 }) { if (!found.Any(x => x.Kind == SourceKind.Null)) found.Add(new Source(SourceKind.Null, 0, null)); }
                            else if (o is SymOperand) { if (!found.Any(x => x.Kind == SourceKind.Static)) found.Add(new Source(SourceKind.Static, 0, null)); }
                            else if (o is RegOperand from) work.Push(from.Reg);
                            else found.Add(new Source(SourceKind.Unknown, 0, null));
                        }
                        continue;
                    }
                    found.Add(Fresh(w) ? new Source(SourceKind.Fresh, 0, w) : new Source(SourceKind.Unknown, 0, w));
                }
            }
            if (work.Count > 0) found.Add(new Source(SourceKind.Unknown, 0, null));
            return sourcesOf[(f, r)] = found;
        }

        static Instr? Origin(Dictionary<VReg, Instr> defs, VReg r)
        {
            for (int hop = 0; hop < 8 && defs.TryGetValue(r, out Instr? d); hop++)
            {
                if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands.Count == 1 && d.Operands[0] is RegOperand from) { r = from.Reg; continue; }
                return d;
            }
            return null;
        }
        Dictionary<Function, Dictionary<VReg, Instr>> defsOf = new();
        Dictionary<VReg, Instr> Defs(Function f)
        {
            if (defsOf.TryGetValue(f, out var d)) return d;
            d = new();
            HashSet<VReg> many = new();
            foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (i.Dest is not null && !d.TryAdd(i.Dest, i)) many.Add(i.Dest);
            foreach (VReg r in many) d.Remove(r);
            return defsOf[f] = d;
        }
        bool Fresh(Instr? d) => d is { Op: Opcode.Call } && (IsAllocator(d.Callee) || IsFreshCall(d));

        // CONSTRUCTORS' OWN STORES. A function whose every call passes, as its
        // first argument, an object the caller has just made -- or the
        // caller's own first argument, the caller being such a function (a
        // base constructor) -- stores into a new object when it stores into
        // its first argument. Found outward from `new`, so the set is a
        // least one.
        HashSet<string> freshThis = new(StringComparer.Ordinal);
        // The call must come straight after the object is made, as a
        // constructor's does: in the same block, nothing between reading it.
        bool FreshFirst(Function g, Block at, int index, Operand arg)
        {
            if (arg is not RegOperand r) return false;
            if (g.Params.Count > 0 && r.Reg == g.Params[0]) return freshThis.Contains(g.Name);
            if (Origin(Defs(g), r.Reg) is not { Op: Opcode.Call } made || !IsAllocator(made.Callee)) return false;
            int from = at.Instrs.IndexOf(made);
            if (from < 0 || from > index) return false;
            HashSet<VReg> names = new() { made.Dest! };
            for (int k = from + 1; k < index; k++)
            {
                Instr i = at.Instrs[k];
                bool uses = i.Operands.Any(o => o is RegOperand u && names.Contains(u.Reg));
                if (!uses) continue;
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null) { names.Add(i.Dest); continue; }
                // Stamping its descriptor is making it.
                if (i.Op == Opcode.Store && i.Operands[1] is SymOperand) continue;
                return false;
            }
            return true;
        }
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Function g in m.Functions)
            {
                if (freshThis.Contains(g.Name) || g.Params.Count == 0 || addressed.Contains(g.Name)
                    || !sites.TryGetValue(g.Name, out var calls) || calls.Count == 0) continue;
                if (calls.All(c => c.B.Instrs[c.At] is { Operands.Count: > 0 } call && FreshFirst(c.G, c.B, c.At, call.Operands[0])))
                {
                    freshThis.Add(g.Name);
                    grew = true;
                }
            }
        }
        // A store in a function into an object that function made is into
        // something no reader outside it had before the function ran.
        initializing = (g, st) => st.Operands[0] is RegOperand baseReg
            && (g.Params.Count > 0 && baseReg.Reg == g.Params[0] && freshThis.Contains(g.Name)
                || Origin(Defs(g), baseReg.Reg) is { Op: Opcode.Call } made && IsAllocator(made.Callee));

        HashSet<string> refused = new(StringComparer.Ordinal);
        bool reporting = Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 0 };
        void Refuse(string field, string why, Function f, Instr at)
        {
            if (refused.Add(field) && reporting) _fieldReport.Add($"{field} refused: {why} in {f.Name}:{at.Line}");
        }
        foreach ((Function f, _, Instr at) in fieldAddresses) Refuse(at.Field!, "its address is taken (ref, out)", f, at);
        List<(Instr Origin, Instr Store)> held = new();
        foreach ((Function f, Block b, Instr st) in stores)
        {
            if (f.Async is not null || st.Operands.Count < 2) { Refuse(st.Field!, "async or odd store", f, st); continue; }
            switch (st.Operands[1])
            {
                case ImmOperand { Value: 0 }:
                case SymOperand:
                    continue;
                case RegOperand v when Origin(Defs(f), v.Reg) is Instr made && Fresh(made) && made.Dest is not null
                    && Analyse(f, new[] { made.Dest }, summaries, made, new HashSet<Instr>(ReferenceEqualityComparer.Instance) { st }) is { Escapes: false } alone:
                    if (!HandedOver(f, b, st, alone.Derived)) { Refuse(st.Field!, $"stores {st.Operands[1]}, which is used after it is stored", f, st); continue; }
                    held.Add((made, st));
                    continue;
                case RegOperand v when Sources(f, v.Reg) is { } sources && sources.All(src => src.Kind != SourceKind.Unknown):
                {
                    // A value from a parameter or a join: every source must be
                    // null, static data, a fresh object this store keeps, or a
                    // parameter every caller hands over (a sink).
                    bool ok = true;
                    HashSet<Instr> only = new(ReferenceEqualityComparer.Instance) { st };
                    // STORES TO THE SAME FIELD ON PATHS THAT EXCLUDE EACH OTHER
                    // -- a different exception made in each branch, the one
                    // message stored into whichever it is -- are one store: no
                    // run passes two of them, so the object goes to one owner.
                    foreach ((Function g, Block other, Instr sibling) in stores)
                        if (ReferenceEquals(g, f) && !ReferenceEquals(sibling, st) && sibling.Field == st.Field
                            && !Reaches(f, b, other) && !Reaches(f, other, b))
                            only.Add(sibling);
                    // THE FRESH SOURCES TOGETHER: two objects joined on their
                    // way to the store are one value there, and each alone
                    // would see the join also holding the other.
                    List<Instr> made = sources.Where(x => x.Kind == SourceKind.Fresh).Select(x => x.Made!).Distinct().ToList();
                    Flow? together = made.Count == 0 || made.Any(x => x.Dest is null) ? null
                        : Analyse(f, made.Select(x => x.Dest!).ToList(), summaries, made.Count == 1 ? made[0] : null, only);
                    if (made.Any(x => x.Dest is null) || together is { Escapes: true }
                        || together is not null && !HandedOver(f, b, st, together.Derived))
                    {
                        ok = false;
                    }
                    foreach (Source src in ok ? sources : new List<Source>())
                    {
                        if (src.Kind == SourceKind.Fresh)
                        {
                            held.Add((src.Made!, st));
                        }
                        else if (src.Kind == SourceKind.Parameter)
                        {
                            if (Analyse(f, new[] { f.Params[src.Index] }, summaries, null, only).Escapes
                                || Sink(f, src.Index, 0) is not List<Instr> handed) { ok = false; break; }
                            foreach (Instr h in handed) held.Add((h, st));
                        }
                    }
                    if (ok) continue;
                    string which = string.Join(",", sources.Where(x => x.Kind == SourceKind.Parameter).Select(x => f.Params[x.Index].ToString()));
                    string why = together is { Escapes: true, Why: { } via } ? $" (its {made.Count} fresh source(s) escape via {via.Op} {via.Callee} {string.Join(" ", via.Operands)})" : "";
                    Refuse(st.Field!, $"stores {st.Operands[1]}, which a caller does not hand over{(which.Length > 0 ? " (" + which + ")" : "")}{why}", f, st);
                    continue;
                }
                default:
                {
                    // Name what made the value, where one writer is known.
                    string from = st.Operands[1] is RegOperand stored && Sources(f, stored.Reg) is { } seen
                        && seen.FirstOrDefault(x => x.Kind == SourceKind.Unknown).Made is Instr maker
                        ? $" (from {maker.Op} {maker.Callee})" : "";
                    Refuse(st.Field!, $"stores {st.Operands[1]}, not a fresh object{from}", f, st);
                    continue;
                }
            }
        }

        // One liveness per function, however many loads it has.
        Dictionary<Function, Liveness> livenessOf = new();
        Dictionary<Function, HashSet<VReg>> padsOf = new();
        // A BORROWED RETURN: a getter handing back what its object's owned
        // field holds. The value is the object's still, so every call to the
        // getter is a read of the field, judged as one where it is made (the
        // calls are added to the reads below).
        List<(Function F, Block B, Instr I, string Field)> reads = loads.Select(l => (l.F, l.B, l.I, l.I.Field!)).ToList();
        HashSet<(string Function, string Field)> borrowing = new();
        Dictionary<Function, HashSet<VReg>> returnedOf = new();
        HashSet<VReg> Returned(Function f)
        {
            if (returnedOf.TryGetValue(f, out HashSet<VReg>? known)) return known;
            HashSet<VReg> chain = new();
            Dictionary<VReg, List<Instr>> writes = Writes(f);
            Stack<VReg> work = new();
            foreach (Block rb in f.Blocks)
                if (rb.Terminator is { Op: Opcode.Ret, Operands: [RegOperand back] }) work.Push(back.Reg);
            while (work.TryPop(out VReg? r))
            {
                if (!chain.Add(r) || !writes.TryGetValue(r, out List<Instr>? ws)) continue;
                foreach (Instr w in ws)
                    if (w.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                        foreach (Operand o in w.Operands) if (o is RegOperand from) work.Push(from.Reg);
            }
            return returnedOf[f] = chain;
        }
        for (int n = 0; n < reads.Count; n++)
        {
            (Function f, Block b, Instr ld, string field) = reads[n];
            // A free the compiler placed for a LEAF this function made -- a
            // temporary string, an array of numbers -- gives back something
            // that holds no object, so nothing it frees can be the value read
            // or anything owning it. (Any other object freed here could own
            // what was read: made here, its field read, then freed.)
            bool FreesOwnMaking(Block x, int k)
            {
                Instr free = x.Instrs[k];
                if (free.Operands.Count == 0 || free.Operands[0] is not RegOperand freed) return false;
                Instr? made = Origin(Defs(f), freed.Reg);
                if (made is not { Op: Opcode.Call, Callee: { } callee }) return false;
                if (callee == LeafAllocator) return true;
                return IsFreshCall(made) && _freshOrigins.TryGetValue(callee, out HashSet<Instr>? origins)
                    && origins.Count > 0 && origins.All(o => o.Callee == LeafAllocator);
            }
            if (refused.Contains(field)) continue;
            if (f.Async is not null || ld.Dest is null) { Refuse(field, "async load", f, ld); continue; }
            // Handed back again by a caller is followed too, once per function
            // (borrowing), so the chain of getters ends.
            HashSet<VReg>? back = f.Name == m.Entry ? null : Returned(f);
            Flow flow = Analyse(f, new[] { ld.Dest }, summaries, ld, returnable: back is { Count: > 0 } ? back : null);
            if (flow.Escapes) { Refuse(field, $"read escapes via {flow.Why?.Op} {flow.Why?.Callee}", f, ld); continue; }
            if (back is not null && flow.Derived.Overlaps(back))
            {
                // Handed back: every call of this function reads the field.
                if (addressed.Contains(f.Name) && !IsVirtualTarget(f.Name)) { Refuse(field, $"read handed back by {f.Name}, whose address is taken", f, ld); continue; }
                if (borrowing.Add((f.Name, field)))
                    foreach (Function g in m.Functions)
                        foreach (Block cb in g.Blocks)
                            foreach (Instr call in cb.Instrs)
                                if (call.Dest is not null && (call.Op == Opcode.Call && call.Callee == f.Name
                                    || call.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(call, out string[]? targets) && targets.Contains(f.Name)))
                                    reads.Add((g, cb, call, field));
            }
            // DEAD BEFORE ANYTHING THAT COULD FREE IT: wherever what was read
            // is live -- in this block after the read, and in every block it
            // is live into or out of, around a loop too -- nothing may store
            // to the field, free, or call what might. Not into a handler,
            // where liveness does not follow it.
            if (!livenessOf.TryGetValue(f, out Liveness? liveness)) livenessOf[f] = liveness = new Liveness(f);
            if (!padsOf.TryGetValue(f, out HashSet<VReg>? pads)) padsOf[f] = pads = PadLive(liveness);
            if (flow.Derived.Any(r => !liveness.Tracks(r) || pads.Contains(r))) { Refuse(field, "read lives into a handler", f, ld); continue; }
            HashSet<string> writers = MayWrite(field);
            Instr? unsafeAt = null;
            bool Uses(Instr i) => i.Operands.Any(o => o is RegOperand r && flow.Derived.Contains(r.Reg));
            foreach (Block x in f.Blocks)
            {
                bool liveIn = flow.Derived.Any(r => liveness.IsLiveIn(x, r));
                bool liveOut = flow.Derived.Any(r => liveness.IsLiveOut(x, r));
                int from = liveIn ? 0 : ReferenceEquals(x, b) ? b.Instrs.IndexOf(ld) + 1 : -1;
                if (from < 0)
                {
                    // Made here by a copy of what was read, in a block the
                    // read is not live into: from that copy on.
                    int made = x.Instrs.FindIndex(i => i.Dest is not null && flow.Derived.Contains(i.Dest));
                    if (made < 0) continue;
                    from = made + 1;
                }
                int to = x.Instrs.Count;
                if (!liveOut)
                {
                    to = from;
                    for (int k = from; k < x.Instrs.Count; k++) if (Uses(x.Instrs[k])) to = k;
                }
                for (int k = from; k < to && unsafeAt is null; k++)
                {
                    Instr i = x.Instrs[k];
                    bool danger = i.Op == Opcode.CallIndirect && (_indirect is null || !_indirect.TryGetValue(i, out _))
                        || i.Op == Opcode.CallIndirect && _indirect!.TryGetValue(i, out string[]? t) && t.Any(writers.Contains)
                        || i.Op == Opcode.Call && (IsFreeCall(i.Callee) && !FreesOwnMaking(x, k)
                                                   || IsCatchEnd(i.Callee) || i.Callee == OwnedReplacedFreer
                                                   || i.Callee is not null && writers.Contains(i.Callee))
                        // ANY owned field's store, not only this one's: the
                        // free a replacement gets (below) frees the old value's
                        // own owned fields too (Runtime.FreeOwnedFields), so
                        // replacing o.A frees o.A.B -- the value read here.
                        || i.Op == Opcode.Store && i.Field is not null && candidates.Contains(i.Field);
                    if (danger) unsafeAt = i;
                }
                if (unsafeAt is not null) break;
            }
            if (unsafeAt is not null) Refuse(field, $"read live across {unsafeAt.Op} {unsafeAt.Callee}", f, unsafeAt);
        }

        HashSet<string> owned = new(candidates.Where(c => !refused.Contains(c)), StringComparer.Ordinal);
        if (owned.Count == 0) return;

        // Replacing a value frees it -- ONLY IN AN OBJECT NO OTHER THREAD CAN
        // SEE: one this function made and that never escapes it, on the heap
        // or promoted to its frame (zeroed where it is made). The proof
        // above is of one thread: a field of a shared object replaced under a
        // lock on one processor freed the value another had just read under
        // the same lock (and an interrupt handler's the same). Anywhere else
        // the old value is the collector's. The object's first store finds
        // nothing there, which the freer ignores.
        Dictionary<Instr, bool> privateOwner = new(ReferenceEqualityComparer.Instance);
        foreach ((Function f, Block b, Instr st) in stores)
        {
            if (!owned.Contains(st.Field!)) continue;
            if (st.Operands[0] is not RegOperand baseReg || Origin(Defs(f), baseReg.Reg) is not Instr madeOwner
                || !PrivateOwner(f, madeOwner, summaries, privateOwner)) continue;
            int at = b.Instrs.IndexOf(st);
            List<Instr> made = new();
            VReg old = f.NewReg(IrTypes.Word);
            Instr load = new() { Op = Opcode.Load, Dest = old, Offset = st.Offset, Size = st.Size, Line = st.Line };
            load.Operands.Add(st.Operands[0]);
            made.Add(load);
            // Both machine words, as the runtime's frees take on every target.
            Instr free = new() { Op = Opcode.Call, Callee = OwnedReplacedFreer, Line = st.Line };
            free.Operands.Add(Word(f, made, new RegOperand(old), st.Line));
            free.Operands.Add(Word(f, made, st.Operands[1], st.Line));
            made.Add(free);
            b.Instrs.InsertRange(at, made);
            _bookkeeping.UnionWith(made);
        }

        foreach ((Instr made, Instr st) in held)
        {
            if (!owned.Contains(st.Field!)) continue;
            if (IsAllocator(made.Callee)) _fieldOwned.Add(made);
            else _ownedCalls.Add(made);
        }

        // Each class's owned-field map, its ancestors' fields included.
        Dictionary<string, List<long>> offsets = new(StringComparer.Ordinal);
        foreach ((_, _, Instr st) in stores)
            if (owned.Contains(st.Field!))
            {
                string owner = "t_" + st.Field![..st.Field!.IndexOf("::", StringComparison.Ordinal)];
                if (!offsets.TryGetValue(owner, out List<long>? list)) offsets[owner] = list = new();
                if (!list.Contains(st.Offset)) list.Add(st.Offset);
            }
        int w = Target.Current.WordSize;
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) items[d.Name] = d;
        foreach (DataItem d in m.Data.Where(d => d.Name.StartsWith("t_", StringComparison.Ordinal)).ToList())
        {
            List<long> mine = Ancestry(items, d.Name).Where(offsets.ContainsKey).SelectMany(a => offsets[a]).Distinct().ToList();
            if (mine.Count == 0 || d.Bytes.Length < 12 * w || mine.Any(o => o % w != 0)) continue;
            int words = (int)(mine.Max() / w) + 1;
            uint[] bits = new uint[(words + 31) / 32];
            foreach (long o in mine) bits[(int)(o / w) / 32] |= 1u << (int)(o / w % 32);
            byte[] block = new byte[(1 + bits.Length) * w];
            for (int k = 0; k < w; k++) block[k] = (byte)(words >> (8 * k));
            for (int i = 0; i < bits.Length; i++) for (int k = 0; k < 4; k++) block[(1 + i) * w + k] = (byte)(bits[i] >> (8 * k));
            string sym = "om_" + d.Name[2..];
            m.Data.Add(new DataItem(sym, block) { ReadOnly = true, Exported = false, Align = w });
            d.Relocs.Add(new DataReloc(11 * w, sym, 0));
        }
    }

    /// <summary>Whether control can go from block `from` to block `to` (not counting staying in `from`), unwinds included.</summary>
    private static bool Reaches(Function f, Block from, Block to)
    {
        // One graph per function for the run: a function with many stores
        // asks this for every pair.
        _reachGraphs ??= new(ReferenceEqualityComparer.Instance);
        if (!_reachGraphs.TryGetValue(f, out Cfg? cfg) || cfg.Function != f) _reachGraphs[f] = cfg = new Cfg(f);
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new();
        void Next(Block x)
        {
            foreach (Block n in cfg.Succs(x)) work.Push(n);
            foreach (Instr i in x.Instrs) if (i.Op == Opcode.LabelAddr) foreach (Block t in i.Targets) work.Push(t);
        }
        Next(from);
        while (work.TryPop(out Block? x))
        {
            if (ReferenceEquals(x, to)) return true;
            if (seen.Add(x)) Next(x);
        }
        return false;
    }

    private enum SourceKind { Null, Static, Parameter, Fresh, Unknown }
    private readonly record struct Source(SourceKind Kind, int Index, Instr? Made);

    /// <summary>
    /// A descriptor and every descriptor its ancestry tables name,
    /// transitively: the classes of its display (word 3, what `is` walks) and
    /// the interfaces of its interface list (word 4, the closure over its
    /// whole chain). By position, not name: a class's tables, a box's and an
    /// array's are named each their own way.
    /// </summary>
    private const int DescriptorDisplay = 3, DescriptorInterfaces = 4;

    /// <summary>An object's descriptor: a class's (t_), an array's or a string's (q_), a boxed primitive's (v_) or struct's (b_).</summary>
    private static bool IsDescriptor(string name) => name.Length > 2 && name[1] == '_' && name[0] is 't' or 'q' or 'v' or 'b';

    private static HashSet<string> Ancestry(Dictionary<string, DataItem> items, string type)
    {
        int w = Target.Current.WordSize;
        HashSet<string> found = new(StringComparer.Ordinal) { type };
        Stack<string> pending = new();
        pending.Push(type);
        while (pending.Count > 0)
        {
            string at = pending.Pop();
            if (!items.TryGetValue(at, out DataItem? t)) continue;
            foreach (DataReloc r in t.Relocs)
                if ((r.Offset == DescriptorDisplay * w || r.Offset == DescriptorInterfaces * w)
                    && items.TryGetValue(r.Symbol, out DataItem? table))
                    foreach (DataReloc up in table.Relocs)
                        if ((up.Symbol.StartsWith("t_", StringComparison.Ordinal) || up.Symbol.StartsWith("i_", StringComparison.Ordinal))
                            && found.Add(up.Symbol))
                            pending.Push(up.Symbol);
        }
        return found;
    }

    /// <summary>The run's parameter summaries, for the allocation report only.</summary>
    private Dictionary<string, bool[]>? _summaries;

    /// <summary>Allocations that live until the program ends (PermanentStatics): never garbage, so no collector's.</summary>
    private readonly HashSet<Instr> _permanent = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A STATIC FIELD SET ONLY BY A STATIC INITIALISER holds what it is given
    /// until the program ends -- C#'s `static readonly`, and any static nothing
    /// else assigns. An allocation a static initialiser makes once (not in a
    /// loop) and stores into such a field is therefore never garbage: it needs
    /// no collector and is never freed. Judged over the whole program only,
    /// since another unit could assign the field; the object may be read and
    /// handed anywhere, which changes nothing, since it is never freed.
    /// </summary>
    private void PermanentStatics(Module m)
    {
        if (m.PreserveExports || m.Entry is null) return;
        static bool IsStaticInit(Function f) => f.Name.Contains("_StaticInit$", StringComparison.Ordinal);
        Dictionary<string, bool> onlyInitialisers = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[0] is SymOperand { Name: var field } && field.StartsWith("s_", StringComparison.Ordinal))
                        onlyInitialisers[field] = (!onlyInitialisers.TryGetValue(field, out bool was) || was) && IsStaticInit(f);
        foreach (Function f in m.Functions)
        {
            if (!IsStaticInit(f)) continue;
            HashSet<Block>? repeating = null;
            Dictionary<VReg, Instr> made = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && IsAllocator(i.Callee) && i.Dest is not null) made[i.Dest] = i;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.Store || i.Operands.Count < 2 || i.Operands[0] is not SymOperand { Name: var field }
                        || !onlyInitialisers.TryGetValue(field, out bool only) || !only
                        || i.Operands[1] is not RegOperand { Reg: var value } || !made.TryGetValue(value, out Instr? alloc)) continue;
                    repeating ??= Repeating(f);
                    if (repeating.Contains(BlockOf(f, alloc))) continue;
                    _permanent.Add(alloc);
                }
        }

        static Block BlockOf(Function f, Instr i) => f.Blocks.First(b => b.Instrs.Contains(i));
    }

    /// <summary>How many of the owned allocations were a fresh function's result.</summary>
    public int OwnedReturns { get; private set; }

    /// <summary>The last run's counts, for --stats: frame slots, owned allocations, of those fresh results, fresh functions.</summary>
    public static (int Promoted, int Owned, int OwnedReturns, int Fresh, int Fields, int Variables) LastRun { get; private set; }

    /// <summary>How many functions were found to return a fresh object.</summary>
    public int FreshFunctions => _fresh.Count;

    /// <summary>Whether a call's result is an object its callee hands over.</summary>
    public bool IsFreshCall(Instr i) => i.Op == Opcode.Call && i.Callee is not null && _fresh.Contains(i.Callee);

    /// <summary>
    /// Whether every return of `f` hands over a fresh object. The returned
    /// register is followed back through copies, width changes and phis to
    /// its origins, each of which must be an allocation or a call to a
    /// function already found fresh (callees are summarised first); a null
    /// on some path is allowed. Each origin must then not escape in any way
    /// but the return itself -- and only the object's base goes back, never
    /// a field address.
    /// </summary>
    private static bool ReturnsFresh(Function f, Dictionary<string, bool[]> summaries, HashSet<string> fresh,
        out List<Instr>? found, out HashSet<VReg>? returned, Needs? needs = null)
    {
        found = null;
        returned = null;
        if (f.Async is not null || f.Returns is not (IrType.I32 or IrType.I64))
        {
            return false;
        }

        // Every definition of every register, since the returned one may be
        // written on more than one path.
        Dictionary<VReg, List<Instr>> defs = new();
        List<Instr> returns = new();
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null)
                {
                    if (!defs.TryGetValue(i.Dest, out List<Instr>? list)) defs[i.Dest] = list = new();
                    list.Add(i);
                }
                if (i.Op == Opcode.Ret)
                {
                    returns.Add(i);
                }
            }
        }
        if (returns.Count == 0)
        {
            return false;
        }

        HashSet<VReg> chain = new();
        List<Instr> origins = new();
        Stack<VReg> work = new();
        bool any = false;
        foreach (Instr ret in returns)
        {
            if (ret.Operands.Count != 1) return false;
            switch (ret.Operands[0])
            {
                case ImmOperand { Value: 0 }:
                    continue;
                // A string literal is handed back as null is: freeing it
                // does nothing, and a string owns no fields.
                case SymOperand { Name: var literal } when literal.StartsWith("str_", StringComparison.Ordinal):
                    continue;
                case RegOperand r:
                    work.Push(r.Reg);
                    any = true;
                    break;
                default:
                    return false;
            }
        }
        if (!any)
        {
            return false;
        }

        while (work.TryPop(out VReg? reg))
        {
            if (!chain.Add(reg)) continue;
            if (chain.Count > 64) return false;
            if (f.Params.Contains(reg) || !defs.TryGetValue(reg, out List<Instr>? writers)) return false;
            foreach (Instr d in writers)
            {
                if (d.Op == Opcode.Call && (IsAllocator(d.Callee) || (d.Callee is not null && fresh.Contains(d.Callee))))
                {
                    origins.Add(d);
                    continue;
                }
                // Made by a function whose answer waits on another unit.
                if (d.Op == Opcode.Call && d.Callee is not null && needs is not null && needs.AllowFresh(d.Callee))
                {
                    origins.Add(d);
                    continue;
                }
                if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Phi)
                {
                    if (d.Operands.Count == 0 || (d.Op != Opcode.Phi && d.Operands.Count != 1)) return false;
                    foreach (Operand o in d.Operands)
                    {
                        if (o is ImmOperand { Value: 0 }) continue;
                        if (o is SymOperand { Name: var literal } && literal.StartsWith("str_", StringComparison.Ordinal)) continue;
                        if (o is not RegOperand from) return false;
                        work.Push(from.Reg);
                    }
                    continue;
                }
                return false;
            }
        }
        if (origins.Count == 0)
        {
            return false;
        }

        foreach (Instr origin in origins)
        {
            Flow flow = Analyse(f, new[] { origin.Dest! }, summaries, origin, returnable: chain, needs: needs);
            if (flow.Escapes) return false;
        }
        found = origins;
        returned = chain;
        return true;
    }

    // ---- the escape question ------------------------------------------------------

    /// <summary>
    /// Everything derived from a set of root registers -- copies, width
    /// changes, constant offsets -- and whether any of it escapes. The
    /// derived set grows to a fixed point because a derivation may be
    /// written before the register it derives from is known to matter.
    /// </summary>
    internal sealed class Flow
    {
        public HashSet<VReg> Derived { get; } = new();
        public bool Escapes { get; set; }
        public Instr? Source { get; init; }
        /// <summary>The instruction the object escaped through, for a diagnostic.</summary>
        public Instr? Why { get; set; }
    }

    internal static Flow Analyse(Function f, IEnumerable<VReg> roots, Dictionary<string, bool[]> summaries, Instr? source,
        HashSet<Instr>? ownedStores = null, HashSet<VReg>? returnable = null, HashSet<VReg>? joinable = null,
        Needs? needs = null, bool handOff = false, HashSet<Instr>? consumers = null)
    {
        Flow flow = new() { Source = source };
        foreach (VReg r in roots)
        {
            flow.Derived.Add(r);
        }

        // A register with more than one definition may hold something else
        // at another time; anything derived through it is unknowable. Count
        // definitions once.
        Dictionary<VReg, int> defs = new();
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null)
                {
                    defs[i.Dest] = defs.GetValueOrDefault(i.Dest) + 1;
                }
            }
        }
        foreach (VReg p in f.Params)
        {
            defs[p] = defs.GetValueOrDefault(p) + 1;
        }

        HashSet<VReg>? pending = null;
        Dictionary<VReg, List<Instr>>? writes = null;
        bool changed = true;
        while (changed && !flow.Escapes)
        {
            changed = false;
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (ReferenceEquals(i, source))
                    {
                        continue;
                    }

                    // The operands read directly: IrInfo.Uses is an iterator, and
                    // this is every instruction of the function, for every
                    // allocation, until the derived set stops growing.
                    bool touches = false;
                    foreach (Operand o in i.Operands)
                    {
                        if (o is RegOperand r && flow.Derived.Contains(r.Reg))
                        {
                            touches = true;
                            break;
                        }
                    }
                    if (!touches)
                    {
                        continue;
                    }

                    switch (i.Op)
                    {
                        case Opcode.Copy:
                        case Opcode.Trunc64:
                        case Opcode.ZExt32:
                        case Opcode.SExt32:
                            Derive(i.Dest);
                            break;

                        case Opcode.Add:
                        case Opcode.Sub:
                            // A field or element address: the pointer plus a
                            // constant, or plus a register that is not itself
                            // derived (an index). Two derived pointers added
                            // together is not an address anyone means.
                            if (i.Operands[1] is RegOperand r1 && flow.Derived.Contains(r1.Reg)
                                && i.Operands[0] is RegOperand r0 && flow.Derived.Contains(r0.Reg))
                            {
                                flow.Escapes = true;
                            }
                            Derive(i.Dest);
                            break;

                        case Opcode.Load:
                        case Opcode.ArrayLength:
                            // Reading the object, or reading through a field
                            // address. The loaded value is not the pointer.
                            break;

                        case Opcode.Store:
                        case Opcode.InitArrayLength:
                            // Writing INTO the object is fine; writing the
                            // pointer itself somewhere is the escape.
                            if (i.Operands[1] is RegOperand v && flow.Derived.Contains(v.Reg)
                                && (ownedStores is null || !ownedStores.Contains(i)))
                            {
                                flow.Escapes = true;
                            }
                            break;

                        case Opcode.MemCopy:
                        case Opcode.MemSet:
                            // Bytes move; pointers do not.
                            break;

                        case Opcode.Eq:
                        case Opcode.Ne:
                        case Opcode.LtS:
                        case Opcode.LeS:
                        case Opcode.GtS:
                        case Opcode.GeS:
                        case Opcode.LtU:
                        case Opcode.LeU:
                        case Opcode.GtU:
                        case Opcode.GeU:
                        case Opcode.Branch:
                        case Opcode.Switch:
                            break;

                        case Opcode.Syscall:
                            // The kernel reads during the call and keeps no
                            // pointer: the runtime's contract with this pass.
                            break;

                        case Opcode.Ret when returnable is not null:
                            // Handing the object back: not an escape when it
                            // is the object's base, by a register of the
                            // returned chain (ReturnsFresh).
                            if (i.Operands.Count != 1 || i.Operands[0] is not RegOperand back || !returnable.Contains(back.Reg))
                            {
                                flow.Escapes = true;
                            }
                            break;

                        case Opcode.Phi when returnable is not null && i.Dest is not null && returnable.Contains(i.Dest):
                            // Joining it with another of the returned origins,
                            // or with null, on the way to the return.
                            Derive(i.Dest);
                            break;

                        case Opcode.Call when IsCatchEnd(i.Callee):
                            // A catch body's hold on its exception ending: a
                            // use, and the free the pass may make it.
                            break;

                        case Opcode.Unwind when handOff:
                        case Opcode.Call when handOff && i.Callee == Unhandled:
                            // Thrown: handed to whatever catches it, or to
                            // the program's end. Not kept here.
                            break;

                        case Opcode.Call when i.Callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive:
                            // A use and nothing more: the object stays live
                            // to here (an address taken from it, GC.KeepAlive)
                            // and nothing gets a pointer it could keep. On a
                            // promoted object it keeps a frame slot, which is
                            // there until the function returns anyway.
                            break;

                        case Opcode.Call when IsCollectorNote(i.Callee):
                            // The collector told of a reference (a write
                            // barrier): it keeps no pointer the program can
                            // use, and refuses to mark a block given back.
                            break;

                        case Opcode.Call:
                        {
                            if (i.Callee is null)
                            {
                                flow.Escapes = true;
                                break;
                            }
                            // A free that was there before this run: the object
                            // is already owned, and owning it again would free
                            // it twice (the link reruns this pass over frees
                            // the unit compile put in).
                            if (IsFreeCall(i.Callee) && _inserted?.Contains(i) != true)
                            {
                                foreach (Operand o in i.Operands)
                                    if (o is RegOperand r && flow.Derived.Contains(r.Reg)) { flow.Escapes = true; flow.Why ??= i; }
                                break;
                            }
                            // A call that takes the object over (a sink
                            // parameter, OwnedFields): handed on, not lost.
                            if (consumers is not null && consumers.Contains(i)) break;
                            summaries.TryGetValue(i.Callee, out bool[]? summary);
                            for (int a = 0; a < i.Operands.Count; a++)
                            {
                                if (i.Operands[a] is not RegOperand arg || !flow.Derived.Contains(arg.Reg)) continue;
                                if (summary is not null && a < summary.Length && !summary[a]) continue;
                                // Asked for the link (EscapeHints): another
                                // unit's function, or one of this unit's whose
                                // own answer waits on another unit, is a
                                // condition rather than an escape.
                                if (needs is not null && needs.Allow(i.Callee, a)) continue;
                                flow.Escapes = true;
                            }
                            // The result of a call that took the pointer is
                            // not assumed to be the pointer: a callee that
                            // returns its argument is summarised as escaping
                            // that argument.
                            break;
                        }

                        case Opcode.CallIndirect when _indirect is not null && _indirect.TryGetValue(i, out string[]? overrides):
                        {
                            // Every override it can reach, together: an
                            // argument stays put only if it does in each.
                            // Operand 0 is the method; the arguments follow.
                            for (int a = 1; a < i.Operands.Count; a++)
                            {
                                if (i.Operands[a] is not RegOperand arg || !flow.Derived.Contains(arg.Reg)) continue;
                                foreach (string o in overrides)
                                {
                                    if (summaries.TryGetValue(o, out bool[]? summary) && a - 1 < summary.Length && !summary[a - 1]) continue;
                                    if (needs is not null && needs.Allow(o, a - 1)) continue;
                                    flow.Escapes = true;
                                    break;
                                }
                                if (flow.Escapes) break;
                            }
                            break;
                        }

                        default:
                            // Returned, unwound, atomically exchanged, passed
                            // indirectly, or anything else: gone.
                            flow.Escapes = true;
                            break;
                    }

                    if (flow.Escapes)
                    {
                        flow.Why ??= i;
                        break;
                    }
                }
                if (flow.Escapes)
                {
                    break;
                }
            }

            // A REGISTER WRITTEN MORE THAN ONCE -- a variable, a joined value
            // -- is the object's too if EVERY write puts this object in it (a
            // copy of what is derived) or null: then it can hold nothing else.
            // Anything else written to it and it is unknowable, which is an
            // escape. Judged once the rest has settled, since what is derived
            // grows as the analysis goes.
            if (!changed && !flow.Escapes && pending is { Count: > 0 })
            {
                writes ??= Writes(f);
                bool resolved = false;
                foreach (VReg d in pending.ToList())
                {
                    bool mine = f.Params.Contains(d) is false && writes.TryGetValue(d, out List<Instr>? all) && all.All(w =>
                        w.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 && w.Operands.Count == 1
                        && (w.Operands[0] is ImmOperand { Value: 0 } || Literal(w.Operands[0], writes)
                            || w.Operands[0] is RegOperand { Reg: var from } && flow.Derived.Contains(from)));
                    if (!mine) continue;
                    pending.Remove(d);
                    // Resolved only when it adds something: one already known
                    // to be the object's, pended again by a later read of it,
                    // must not stand in for progress while another pending
                    // register -- one that holds something else -- waits.
                    if (flow.Derived.Add(d))
                    {
                        resolved = true;
                        changed = true;
                    }
                }
                // Still some that can hold another value, and nothing more to
                // learn: those are the escape.
                if (!resolved && pending.Count > 0)
                {
                    flow.Escapes = true;    // shared with another value; unknowable
                    flow.Why ??= writes.TryGetValue(pending.First(), out List<Instr>? w0) ? w0[0] : null;
                }
            }
        }

        return flow;

        void Derive(VReg? d)
        {
            if (d is null)
            {
                return;
            }
            if (flow.Derived.Contains(d))
            {
                return;
            }
            if (defs.GetValueOrDefault(d) > 1 && (returnable is null || !returnable.Contains(d))
                && (joinable is null || !joinable.Contains(d)))
            {
                (pending ??= new()).Add(d);
                return;
            }
            if (flow.Derived.Add(d))
            {
                changed = true;
            }
        }
    }

    /// <summary>
    /// A string literal, named or through the one copy that loaded it: what
    /// `s ?? ""` joins with. It is static data, not a heap block, and its
    /// type owns no fields, so the frees an owner makes pass it by -- a
    /// register that holds either the object or a literal is as much the
    /// object's as one that holds either the object or null.
    /// </summary>
    private static bool Literal(Operand o, Dictionary<VReg, List<Instr>> writes)
    {
        if (o is RegOperand { Reg: var r } && writes.TryGetValue(r, out List<Instr>? ws) && ws.Count == 1
            && ws[0] is { Op: Opcode.Copy, Operands.Count: 1 } load)
            o = load.Operands[0];
        return o is SymOperand { Name: var name } && name.StartsWith("str_", StringComparison.Ordinal);
    }

    /// <summary>
    /// The allocations a register written more than once joins, when every
    /// write is one of them (through copies) or null: each made once by a
    /// constant-sized allocation with a register of its own. Null otherwise.
    /// </summary>
    private List<Instr>? JoinedAllocations(Function f, VReg joined, long budget, List<VReg>? promoted = null)
    {
        Dictionary<VReg, List<Instr>> writes = Writes(f);
        if (!writes.TryGetValue(joined, out List<Instr>? into) || into.Count < 2 || f.Params.Contains(joined)) return null;
        List<Instr> group = new();
        foreach (Instr w in into)
        {
            if (w.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || w.Operands.Count != 1) return null;
            if (w.Operands[0] is ImmOperand { Value: 0 }) continue;
            if (w.Operands[0] is not RegOperand { Reg: var from }) return null;
            Instr? made = null;
            for (int hop = 0; hop < 8; hop++)
            {
                if (!writes.TryGetValue(from, out List<Instr>? ws) || ws.Count != 1 || f.Params.Contains(from)) return null;
                Instr d = ws[0];
                if (d.Op == Opcode.Call && IsAllocator(d.Callee)) { made = d; break; }
                // A member already given a frame slot (an earlier turn of
                // this): still one of the group, judged with it.
                if (d.Op == Opcode.Copy && d.Operands is [SlotOperand { Slot.Name: "obj" }] && promoted is not null)
                {
                    promoted.Add(from);
                    break;
                }
                if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands is not [RegOperand next]) return null;
                from = next.Reg;
            }
            if (made is null)
            {
                if (promoted is not null && promoted.Count > 0) continue;
                return null;
            }
            if (!ConstantSize(f, made.Operands[0], out long size) || size <= 0 || size > ObjectLimit || size > budget) return null;
            if (!group.Contains(made)) group.Add(made);
        }
        return group.Count + (promoted?.Count ?? 0) >= 2 && group.Count >= 1 ? group : null;
    }

    /// <summary>Every instruction that writes each register.</summary>
    private static Dictionary<VReg, List<Instr>> Writes(Function f)
    {
        Dictionary<VReg, List<Instr>> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is not null)
                {
                    if (!writes.TryGetValue(i.Dest, out List<Instr>? list)) writes[i.Dest] = list = new();
                    list.Add(i);
                }
        return writes;
    }

    // ---- recursion ---------------------------------------------------------------------
    //
    // A FUNCTION IN A CYCLE OF CALLS used to be summarised before its callees
    // in the cycle were, and a callee with no summary lets everything escape:
    // every parameter anywhere in a recursive family escaped, whatever the
    // functions did with it. win32k's dispatcher is one such family -- a
    // call can make a callback, whose calls come back into it -- and every
    // object it handed a handler went to the collector on that ground alone.
    //
    // Whether a parameter escapes is a monotone question: a callee's
    // parameter escaping can only make more of the caller's escape. So a
    // cycle is solved as such questions are, to its least fixed point:
    // every parameter of the cycle starts as staying put, each function is
    // summarised again with what the others were found to do, until nothing
    // changes. A parameter that escapes somewhere on the cycle is found to,
    // however far round the cycle that is; one that only travels round it
    // does not. If a cycle does not settle within the bound (it always does:
    // each round can only turn answers from no to yes), it goes back to the
    // old answer, everything escaping.

    private const int CycleRounds = 64;

    private static bool CallsItself(Function f)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Callee == f.Name) return true;
        return false;
    }

    /// <summary>
    /// The module's functions as the cycles of its call graph (Tarjan's
    /// strongly connected components), callees' cycles before their
    /// callers'; a function in no cycle is a cycle of one. Iterative, since
    /// a call chain can be deeper than a thread's stack.
    /// </summary>
    /// <summary>
    /// Each virtual call's possible callees (IndirectTargets), for the analyses
    /// that are static: set for the length of a run.
    /// </summary>
    [ThreadStatic] private static Dictionary<Instr, string[]>? _indirect;
    /// <summary>Flow graphs Reaches has built this run, by function.</summary>
    [ThreadStatic] private static Dictionary<Function, Cfg>? _reachGraphs;
    /// <summary>CORSAC_ALLOC_REPORT: why each virtual call it could not resolve was left.</summary>
    [ThreadStatic] private static List<string>? _unresolvedWhy;

    /// <summary>
    /// A VIRTUAL CALL IS EVERY OVERRIDE IT CAN REACH. The call loads the
    /// object's descriptor from its first word, the method from a slot at a
    /// fixed offset in it, and calls that with the object first. Over a whole
    /// program every descriptor is here, so the methods a call can reach are
    /// the ones every descriptor holds at that slot, and its summary is theirs
    /// together: an argument escapes if it escapes in any of them. Only for a
    /// whole program -- a library's descriptors are not all its consumers' --
    /// and only for the call shape just described; any other indirect call is
    /// left unresolved, and an escape, as before.
    /// </summary>
    /// <summary>
    /// A VIRTUAL CALL IN A UNIT THAT IS NOT THE WHOLE PROGRAM names every
    /// override it can reach by one symbol no unit defines: its declaring type
    /// and slot. A condition on it (EscapeHints) is a condition on each of
    /// them together, which the link resolves from every descriptor in the
    /// image (Lto.VirtualTargets) and answers as for any function.
    /// </summary>
    public const string VirtualPrefix = "__virtual:";

    public static string VirtualCallee(string declaring, long slot) => VirtualPrefix + declaring + "+" + slot;

    /// <summary>
    /// The call's declaring type and slot, when it has the shape of a virtual
    /// call: the method loaded from the object's own descriptor, the object
    /// passed first. <paramref name="single"/> maps each register written once
    /// to its instruction; <paramref name="many"/> holds those written more.
    /// </summary>
    private static (string Declaring, long Slot)? VirtualSlot(Instr i, Dictionary<VReg, Instr> single, HashSet<VReg> many)
    {
        if (i.Op != Opcode.CallIndirect || i.Operands.Count < 2 || i.DispatchType is not string declaring
            || i.Operands[0] is not RegOperand { Reg: var target } || i.Operands[1] is not RegOperand { Reg: var self }
            || many.Contains(target) || !single.TryGetValue(target, out Instr? method)
            || method.Op != Opcode.Load || method.Operands.Count < 1 || method.Operands[0] is not RegOperand { Reg: var table }
            || many.Contains(table) || !single.TryGetValue(table, out Instr? header)
            || header.Op != Opcode.Load || header.Offset != 0 || header.Operands.Count < 1
            || header.Operands[0] is not RegOperand { Reg: var from } || from != self) return null;
        return (declaring, method.Offset);
    }

    /// <summary>
    /// Every virtual call of a unit's functions, each to its one symbol for
    /// all its overrides (VirtualCallee); only those <paramref name="known"/>
    /// admits, when it is given -- the ones the link has an answer for.
    /// </summary>
    internal static Dictionary<Instr, string[]> VirtualCallees(IEnumerable<Function> functions, Func<string, bool>? known = null)
    {
        Dictionary<Instr, string[]> result = new(ReferenceEqualityComparer.Instance);
        foreach (Function f in functions)
        {
            Dictionary<VReg, Instr> single = new();
            HashSet<VReg> many = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is not null && !single.TryAdd(i.Dest, i)) many.Add(i.Dest);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.CallIndirect && VirtualSlot(i, single, many) is var (declaring, slot))
                    {
                        string name = VirtualCallee(declaring, slot);
                        if (known is null || known(name)) result[i] = new[] { name };
                    }
        }
        return result;
    }

    private static Dictionary<Instr, string[]>? IndirectTargets(Module m, Dictionary<string, Function> byName)
    {
        // Not the whole program: each virtual call stands for its overrides
        // under one name, for the link to answer (VirtualCallees).
        if (m.PreserveExports || m.Entry is null) return m.PreserveExports ? VirtualCallees(m.Functions) : null;

        // Where in a descriptor its methods begin: the offsets objects are
        // stamped with (`store @t_Type+48` into the new object's first word).
        HashSet<long> bases = new();
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Offset == 0 && i.Operands.Count >= 2
                        && i.Operands[1] is SymOperand { Name: var t, Offset: var at } && IsDescriptor(t))
                        bases.Add(at);
        if (bases.Count == 0) return null;
        // Every kind of object's descriptor: a class's, an array's or a
        // string's, a boxed primitive's, a boxed struct's. An interface call
        // lands on any of them that implements it.
        List<DataItem> descriptors = m.Data.Where(d => IsDescriptor(d.Name)).ToList();
        // A type's descriptor names its ancestors (what `is` tests read), so
        // the descriptors of a type and its subclasses are the ones that are
        // it or name it. Slot offsets are each hierarchy's own: the same
        // offset is another method entirely in an unrelated class.
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) items[d.Name] = d;
        // A type's ancestry: the classes and interfaces its tables name.
        // The whole chain, not only what one table lists: a missed ancestor
        // would drop an override from a call's targets, and the summary would
        // then be wrong rather than cautious.
        Dictionary<string, HashSet<string>> ancestry = new(StringComparer.Ordinal);
        HashSet<string> Ancestors(string type) =>
            ancestry.TryGetValue(type, out HashSet<string>? known) ? known : ancestry[type] = Ancestry(items, type);
        Dictionary<(string, long), string[]> bySlot = new();
        string[] Slot(string declaring, long slot)
        {
            if (bySlot.TryGetValue((declaring, slot), out string[]? known)) return known;
            HashSet<string> found = new(StringComparer.Ordinal);
            // Every object is an object: a call declared on it reaches the
            // slot of every descriptor, whatever its ancestry lists.
            bool everyType = declaring == "t_object";
            foreach (DataItem d in descriptors)
            {
                if (!everyType && !Ancestors(d.Name).Contains(declaring)) continue;
                foreach (DataReloc r in d.Relocs)
                    if (r.Addend == 0 && bases.Contains(r.Offset - slot)) found.Add(r.Symbol);
            }
            return bySlot[(declaring, slot)] = found.ToArray();
        }

        Dictionary<Instr, string[]> result = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, bool> instantiated = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
        {
            Dictionary<VReg, Instr> single = new();
            HashSet<VReg> many = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is not null && !single.TryAdd(i.Dest, i)) many.Add(i.Dest);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.CallIndirect || i.Operands.Count < 2
                        || i.Operands[0] is not RegOperand { Reg: var target } || i.Operands[1] is not RegOperand { Reg: var self }
                        || many.Contains(target) || !single.TryGetValue(target, out Instr? method)
                        || method.Op != Opcode.Load || method.Operands.Count < 1 || method.Operands[0] is not RegOperand { Reg: var table }
                        || many.Contains(table) || !single.TryGetValue(table, out Instr? header)
                        || header.Op != Opcode.Load || header.Offset != 0 || header.Operands.Count < 1
                        || header.Operands[0] is not RegOperand { Reg: var from } || from != self) continue;
                    if (i.DispatchType is not string declaring) continue;
                    // NO OBJECT OF THE TYPE EXISTS: no descriptor in the
                    // whole program is it or derives from it -- an interface
                    // the library tests for and nothing implements -- so the
                    // call is never made, and calls nothing.
                    if (!instantiated.TryGetValue(declaring, out bool made))
                        instantiated[declaring] = made = declaring == "t_object" || descriptors.Any(d => Ancestors(d.Name).Contains(declaring));
                    if (!made)
                    {
                        result[i] = Array.Empty<string>();
                        continue;
                    }
                    string[] targets = Slot(declaring, method.Offset);
                    // A slot no descriptor fills is not a call this knows.
                    if (targets.Length == 0 || targets.Any(t => !byName.ContainsKey(t)))
                    {
                        _unresolvedWhy?.Add($"{f.Name} {declaring}+{method.Offset}: "
                            + (targets.Length == 0 ? "no descriptor fills the slot" : "not code: " + string.Join(",", targets.Where(t => !byName.ContainsKey(t)).Take(4))));
                        continue;
                    }
                    result[i] = targets;
                }
        }
        return result;
    }

    private static List<List<Function>> CallCycles(Module m, Dictionary<string, Function> byName)
    {
        List<List<Function>> result = new();
        Dictionary<Function, int> index = new(), low = new();
        HashSet<Function> onStack = new();
        Stack<Function> stack = new();
        int next = 0;
        Dictionary<Function, List<Function>> callees = new();
        foreach (Function f in m.Functions)
        {
            List<Function> list = new();
            HashSet<Function> seen = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is not null && byName.TryGetValue(i.Callee, out Function? c) && seen.Add(c))
                        list.Add(c);
                    // A virtual call's overrides are its callees too, so they
                    // are summarised before it is.
                    else if (i.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(i, out string[]? targets))
                        foreach (string t in targets)
                            if (byName.TryGetValue(t, out Function? o) && seen.Add(o)) list.Add(o);
                }
            callees[f] = list;
        }
        foreach (Function root in m.Functions)
        {
            if (index.ContainsKey(root)) continue;
            Stack<(Function F, int Next)> work = new();
            work.Push((root, 0));
            index[root] = low[root] = next++;
            stack.Push(root); onStack.Add(root);
            while (work.Count > 0)
            {
                (Function f, int at) = work.Pop();
                List<Function> outs = callees[f];
                if (at < outs.Count)
                {
                    work.Push((f, at + 1));
                    Function c = outs[at];
                    if (!index.ContainsKey(c))
                    {
                        index[c] = low[c] = next++;
                        stack.Push(c); onStack.Add(c);
                        work.Push((c, 0));
                    }
                    else if (onStack.Contains(c))
                    {
                        low[f] = Math.Min(low[f], index[c]);
                    }
                    continue;
                }
                // f is finished: its low link goes to whoever called it.
                if (work.Count > 0)
                {
                    Function parent = work.Peek().F;
                    low[parent] = Math.Min(low[parent], low[f]);
                }
                if (low[f] == index[f])
                {
                    List<Function> cycle = new();
                    Function w;
                    do { w = stack.Pop(); onStack.Remove(w); cycle.Add(w); } while (w != f);
                    result.Add(cycle);
                }
            }
        }
        return result;
    }

    /// <summary>For each parameter: whether the function lets it escape. Returning it counts.</summary>
    // ---- promotion -----------------------------------------------------------------

    private static List<Block> PromotionOrder(Function f)
    {
        // Spend a bounded frame budget on repeatedly executed allocations
        // before one-time setup. This is only a selection heuristic: no IR
        // moves, and every escape, renewal and liveness proof still runs.
        Cfg cfg = new(f);
        if (cfg.Roots.Count != 1) return f.Blocks.ToList();
        Dictionary<Block, int> depth = new();
        foreach (Block header in f.Blocks)
        {
            Block[] latches = cfg.Preds(header).Where(p => cfg.Dominates(header, p)).ToArray();
            if (latches.Length == 0) continue;
            HashSet<Block> loop = new() { header };
            Stack<Block> work = new(latches);
            while (work.TryPop(out Block? block))
                if (loop.Add(block)) foreach (Block pred in cfg.Preds(block)) work.Push(pred);
            if (loop.Any(b => !cfg.Dominates(header, b))) continue;
            foreach (Block block in loop) depth[block] = depth.GetValueOrDefault(block) + 1;
        }
        // Stable ties preserve the existing owner-before-child opportunity.
        return f.Blocks.OrderByDescending(b => depth.GetValueOrDefault(b)).ToList();
    }

    private void PromoteIn(Function f, Dictionary<string, bool[]> summaries, bool canFree, OwnedFieldEscape fields)
    {
        // An async body's frame does not outlive a suspension, and an object
        // held across one would be gone when it resumed.
        if (f.Async is not null)
        {
            return;
        }

        if (canFree) OwnFreshResults(f, summaries);

        int budget = FrameBudget;
        // ONE ANALYSIS FOR THE WHOLE FUNCTION. Promoting an allocation or
        // owning one adds registers, slots and frees of its own and takes
        // away the allocator call's read of its size; no block is added, no
        // older register gains a definition, and the only older register
        // gaining a read is the owned object's own, just after it is made,
        // which no other allocation derives from. So what liveness
        // and the definition counts said before stays right, or errs toward
        // live, for every register a later candidate is judged on. Made again
        // after each promotion, a whole kernel in one module spent its build
        // solving liveness once per object.
        Liveness? liveness = null;
        HashSet<VReg>? pads = null;
        Defs? defs = null;
        UseIndex? uses = null;
        HashSet<Block>? repeating = null;
        List<OwnedFieldEscape.Owner> owners = new();

        foreach (Block b in PromotionOrder(f))
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Call || !IsAllocator(i.Callee) || i.Dest is null || i.Operands.Count != 1
                    || _owned.Contains(i))
                {
                    continue;
                }

                bool sized = ConstantSize(f, i.Operands[0], out long size)
                             && size > 0 && size <= ObjectLimit && size <= budget;
                if (!sized && !canFree)
                {
                    continue;
                }

                Flow flow = Analyse(f, new[] { i.Dest }, summaries, i);
                OwnedFieldEscape.Owner promotedOwner = new() { Block = b, Root = i.Dest, Bytes = size };
                promotedOwner.Aliases.Add(i.Dest);
                bool canAnchor = true;
                defs ??= flow.Escapes && sized && owners.Count != 0 ? new Defs(f) : null;
                if (flow.Escapes && sized && owners.Count != 0 && defs!.IsSingle(i.Dest))
                {
                    HashSet<VReg> roots = new() { i.Dest };
                    HashSet<Instr> stores = new();
                    Defs fieldDefs = defs;
                    for (int attempt = 0; attempt < 16 && flow.Escapes; attempt++)
                    {
                        bool added = false;
                        foreach (var owner in owners)
                        {
                            // The owner must be freshly made on every execution
                            // of this child allocation, including loop iterations.
                            // Across blocks, every cycle back to the child must
                            // pass through the owner creation again. An owner
                            // outside an inner allocation loop does not qualify.
                            if (!fieldDefs.IsSingle(owner.Root)
                                || !OwnedFieldEscape.OwnerRenews(fieldDefs.Cfg, owner.Block, b)) continue;
                            var addresses = OwnedFieldEscape.Addresses(f, owner.Aliases);
                            foreach (var targetBlock in f.Blocks)
                            foreach (Instr store in targetBlock.Instrs)
                            {
                                if (store.Op != Opcode.Store || stores.Contains(store) || store.Size != IrTypes.Word.Bytes()
                                    || !fieldDefs.Cfg.Dominates(b, targetBlock)
                                    || (targetBlock == b && targetBlock.Instrs.IndexOf(store) <= k)
                                    || store.Operands[1] is not RegOperand value || !flow.Derived.Contains(value.Reg)
                                    || store.Operands[0] is not RegOperand address || !addresses.TryGetValue(address.Reg, out long offset)
                                    || offset < 0 || store.Offset < 0 || offset > owner.Bytes - store.Offset) continue;
                                long field = offset + store.Offset;
                                if (field > owner.Bytes - store.Size) continue;
                                HashSet<VReg> loaded = new();
                                OwnedFieldEscape.Field referenceField = new(field, store.Size);
                                if (!fields.ReadsOwner(f, owner, new[] { referenceField }, loaded)) continue;
                                var childAddresses = OwnedFieldEscape.Addresses(f, promotedOwner.Aliases);
                                if (!childAddresses.TryGetValue(value.Reg, out long childOffset) || childOffset != 0)
                                    canAnchor = false;
                                promotedOwner.Aliases.UnionWith(loaded);
                                promotedOwner.Stores.Add(store);
                                promotedOwner.Parents.Add((owner, referenceField));
                                stores.Add(store); roots.UnionWith(loaded); added = true;
                            }
                        }
                        if (!added) break;
                        flow = Analyse(f, roots, summaries, i, stores);
                    }
                }
                // OBJECTS JOINED ONLY WITH EACH OTHER -- `c ? new A() : new A()`,
                // a span of one literal or another -- are one object as far as
                // anything after the join can tell, and are judged together:
                // if none of them escapes, each gets a slot of its own.
                List<VReg> promotedMembers = new();
                if (flow.Escapes && sized && flow.Why is { Op: Opcode.Copy, Dest: { } joined }
                    && JoinedAllocations(f, joined, budget, promotedMembers) is { } group && group.Contains(i))
                {
                    Flow together = Analyse(f, group.Select(g => g.Dest!).Concat(promotedMembers).ToList(), summaries, i);
                    if (!together.Escapes) flow = together;
                }
                if (flow.Escapes)
                {
                    // Left to the collector: say why, for the link (EscapeHints).
                    if (_hinting)
                    {
                        liveness ??= new Liveness(f);
                        pads ??= PadLive(liveness);
                        Pending(f, b, i, summaries, liveness, pads, null);
                    }
                    continue;
                }

                // Inside a loop the slot -- or, for an owned allocation, the
                // one pointer the function remembers -- is reused each time
                // round, so the previous object must be dead by the time this
                // runs again.
                liveness ??= new Liveness(f);
                pads ??= PadLive(liveness);
                // A group's members already promoted this pass hold their own
                // slots, reached through registers made after the liveness was
                // solved: not this slot's previous object.
                HashSet<VReg> selfDerived = promotedMembers.Count == 0 ? flow.Derived
                    : flow.Derived.Where(r => liveness.Tracks(r)).ToHashSet();
                if (LiveAtSelf(liveness, pads, b, i, selfDerived))
                {
                    continue;
                }

                if (!sized)
                {
                    // Tier 1 all the same: the size is only known at run time
                    // (`new byte[n]`, a string of a computed length), so there
                    // is no frame slot to put it in, but the lifetime is just
                    // as proved. Keep the allocation and free it on the way
                    // out -- and, in a loop, free last time's before making
                    // this one, so the function holds at most one at once.
                    long ownedBytes = ConstantSize(f, i.Operands[0], out long constant) ? constant : -1;
                    // Dead within its own block: one free at its last use.
                    uses ??= new UseIndex(f);
                    if (FreeAtLastUse(f, b, i, flow.Derived, liveness, pads, uses, ownedBytes))
                    {
                        _owned.Add(i);
                        Owned++;
                        continue;
                    }
                    repeating ??= Repeating(f);
                    if (Own(f, b, i, ownedBytes, repeating.Contains(b)))
                    {
                        _owned.Add(i);
                        Owned++;
                        // The allocation moved: instructions went in before it,
                        // and two after it that must not be scanned again.
                        k = b.Instrs.IndexOf(i) + 2;
                    }
                    continue;
                }

                int bytes = (int)((size + 7) & ~7L);
                FrameSlot slot = f.NewSlot(bytes, 8, "obj");
                VReg addr = f.NewReg(IrTypes.Word, "stackobj");

                // The allocator answers zeroed memory; so does this -- the
                // object's own bytes, which its constructor may write over
                // entirely (Dse then drops this), not the slot's rounding.
                List<Instr> replacement = new()
                {
                    new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = i.Line },
                    new Instr { Op = Opcode.MemSet, Operands = { new RegOperand(addr), new ImmOperand(0, IrType.I32), new ImmOperand(size, IrTypes.Word) }, Line = i.Line },
                };
                if (i.Dest.Type == IrTypes.Word)
                {
                    replacement.Add(new Instr { Op = Opcode.Copy, Dest = i.Dest, Operands = { new RegOperand(addr) }, Line = i.Line });
                }
                else
                {
                    replacement.Add(new Instr { Op = Opcode.ZExt32, Dest = i.Dest, Operands = { new RegOperand(addr) }, Line = i.Line });
                }

                b.Instrs.RemoveAt(k);
                b.Instrs.InsertRange(k, replacement);
                _promotedMade.Add(replacement[0]);
                Record(f, new OwnedRecord { Origin = replacement[1], Root = i.Dest, SlotAddress = addr, Slot = slot, Renew = replacement[1], Bytes = bytes });
                k += replacement.Count - 1;
                budget -= bytes;
                Promoted++;
                // Only base references may anchor another generation: an
                // interior reference would need a translated descendant path.
                if (canAnchor) owners.Add(promotedOwner);
            }
        }
    }

    /// <summary>
    /// Take ownership of an allocation whose size is not a constant: the
    /// function keeps its pointer in one frame slot, frees whatever the slot
    /// held before allocating again, and frees the slot on every return.
    ///
    /// The slot, not the register, is what the frees read, because the
    /// allocation need not run on the path that reaches a given return: a
    /// slot zeroed on entry says "nothing to free", and the runtime's free
    /// ignores a zero. It is also what makes a loop safe -- the pointer the
    /// slot holds is last time's object, which the liveness check has already
    /// proved dead here.
    ///
    /// Conservative on two counts. An Unwind out of the function does not
    /// free (the same terminator also jumps to a landing pad inside the
    /// function, where the object may still be live, and the two are not
    /// distinguished here), so an escaping exception leaks one object per
    /// site. And nothing is freed at a call that never returns; the process
    /// is ending there anyway.
    /// </summary>
    private bool Own(Function f, Block block, Instr alloc, long bytes = -1, bool repeats = true)
    {
        // Only a site that can run again has last time's object to give
        // back; one on no cycle of the graph finds its slot empty.
        OwnedRecord record = new() { Origin = alloc, Root = alloc.Dest!, Renew = repeats ? alloc : null, Bytes = bytes };
        List<(Block Block, int Index)> exits = new();
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is { Op: Opcode.Ret })
            {
                exits.Add((b, b.Instrs.Count - 1));
            }
        }
        // A function with no return -- the entry, which exits by a system
        // call -- still gains: the free before the next allocation is what
        // keeps a loop flat, and what the last iteration holds dies with the
        // process.
        int word = IrTypes.Word.Bytes();
        FrameSlot slot = f.NewSlot(word, word, "owned");

        // Entry: nothing owned yet. (Inserted code takes its neighbour's
        // line, so the line table's runs are not split by it.)
        VReg entryAddr = f.NewReg(IrTypes.Word, "ownedp");
        int entryLine = EntryLine(f, alloc.Line);
        f.Entry.Instrs.InsertRange(0, new List<Instr>
        {
            new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(slot) }, Line = entryLine },
            new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = entryLine },
        });

        int at = block.Instrs.IndexOf(alloc);
        if (at < 0)
        {
            return false;
        }

        // Before the allocation: give back what the last one made.
        VReg addr = f.NewReg(IrTypes.Word, "ownedp");
        List<Instr> releasePrevious = new()
        {
            new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = alloc.Line },
        };
        if (repeats)
        {
            VReg prev = f.NewReg(IrTypes.Word, "owned");
            releasePrevious.Add(new Instr { Op = Opcode.Load, Size = word, Dest = prev, Operands = { new RegOperand(addr) }, Line = alloc.Line });
            record.Frees.Add((block, AppendFree(f, releasePrevious, prev, alloc.Line), prev));
        }
        block.Instrs.InsertRange(at, releasePrevious);
        _bookkeeping.UnionWith(releasePrevious);

        // After it: remember the new one.
        VReg made = f.NewReg(IrTypes.Word, "owned");
        Instr resize = alloc.Dest!.Type == IrTypes.Word
            ? new Instr { Op = Opcode.Copy, Dest = made, Operands = { new RegOperand(alloc.Dest) }, Line = alloc.Line }
            : new Instr { Op = IrTypes.Word == IrType.I32 ? Opcode.Trunc64 : Opcode.ZExt32,
                Dest = made, Operands = { new RegOperand(alloc.Dest) }, Line = alloc.Line };
        List<Instr> remember = new()
        {
            resize,
            new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(addr), new RegOperand(made) }, Line = alloc.Line },
        };
        block.Instrs.InsertRange(block.Instrs.IndexOf(alloc) + 1, remember);
        _bookkeeping.UnionWith(remember);

        foreach ((Block b, int _) in exits)
        {
            int r = b.Instrs.Count - 1;
            VReg a = f.NewReg(IrTypes.Word, "ownedp");
            VReg p = f.NewReg(IrTypes.Word, "owned");
            int exitLine = b.Instrs[r].Line;
            List<Instr> releaseExit = new()
            {
                new Instr { Op = Opcode.Copy, Dest = a, Operands = { new SlotOperand(slot) }, Line = exitLine },
                new Instr { Op = Opcode.Load, Size = word, Dest = p, Operands = { new RegOperand(a) }, Line = exitLine },
            };
            record.Frees.Add((b, AppendFree(f, releaseExit, p, exitLine), p));
            b.Instrs.InsertRange(r, releaseExit);
            _bookkeeping.UnionWith(releaseExit);
        }

        Record(f, record);
        return true;
    }

    /// <summary>
    /// A fresh call's result owned by `f` if it does not escape here either
    /// and no earlier result from the same call is still in use when it runs
    /// again. True if ownership was taken (five instructions follow the call).
    /// </summary>
    /// <summary>
    /// Every fresh function's result in `f` the caller can own: all of them
    /// judged against ONE analysis of the function as it stands, then owned.
    /// Owning one inserts only bookkeeping of its own -- a slot, frees, a
    /// store of that result -- which touches no register another result's
    /// judgement rests on; judged one at a time with the analysis made again
    /// after each, a function with thousands of such calls cost thousands of
    /// liveness solutions.
    /// </summary>
    private void OwnFreshResults(Function f, Dictionary<string, bool[]> summaries)
    {
        if (f.Async is not null) return;
        List<(Block Block, Instr Call)> calls = new();
        List<(Block Block, Instr Call)> waiting = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (IsFreshCall(i) && i.Dest is not null && !_ownedCalls.Contains(i)) calls.Add((b, i));
                else if (_hinting && i.Op == Opcode.Call && i.Callee is not null && i.Dest is { Type: IrType.I32 or IrType.I64 }
                         && !IsAllocator(i.Callee) && !IsCollectorNote(i.Callee) && !_bookkeeping.Contains(i))
                    waiting.Add((b, i));
            }
        if (calls.Count == 0 && waiting.Count == 0) return;
        Defs defs = new(f, buildCfg: false);
        HashSet<VReg>? addresses = waiting.Count == 0 ? null : AddressRegisters(f);
        waiting.RemoveAll(w => !defs.IsSingle(w.Call.Dest!) || !addresses!.Contains(w.Call.Dest!));
        Liveness? liveness = null;
        HashSet<VReg>? pads = null;
        List<(Block Block, Instr Call, bool ReadsPrevious, HashSet<VReg> Derived)> chosen = new();
        foreach ((Block b, Instr call) in calls)
        {
            if (!defs.IsSingle(call.Dest!)) continue;
            Flow flow = Analyse(f, new[] { call.Dest! }, summaries, call);
            if (flow.Escapes) continue;
            liveness ??= new Liveness(f);
            pads ??= PadLive(liveness);
            if (LiveAtSelf(liveness, pads, b, call, flow.Derived)) continue;
            // BEFORE THE CALL WHEN THE CALL CANNOT BE READING IT (see below).
            bool readsPrevious = false;
            foreach (Operand o in call.Operands)
                if (o is RegOperand arg && flow.Derived.Contains(arg.Reg)) readsPrevious = true;
            chosen.Add((b, call, readsPrevious, flow.Derived));
        }
        UseIndex? uses = chosen.Count == 0 ? null : new UseIndex(f);
        HashSet<Block>? repeating = null;
        foreach ((Block b, Instr call, bool readsPrevious, HashSet<VReg> derived) in chosen)
        {
            // Dead within its own block: one free at its last use.
            if (FreeAtLastUse(f, b, call, derived, liveness!, pads!, uses!, -1))
            {
                _records[f][^1].FreshCallee = call.Callee;
                _ownedCalls.Add(call);
                Owned++;
                OwnedReturns++;
                continue;
            }
            repeating ??= Repeating(f);
            OwnFreshResult(f, b, call, readsPrevious, repeating.Contains(b));
        }

        // A result from a function whose answer waits on another unit: owned
        // here if the link finds it fresh (EscapeHints). Judged against the
        // same analysis, before anything above changed the function.
        if (!_hinting) return;
        foreach ((Block b, Instr call) in waiting)
        {
            liveness ??= new Liveness(f);
            pads ??= PadLive(liveness);
            Pending(f, b, call, summaries, liveness, pads, call.Callee);
        }
    }

    /// <summary>
    /// Every register used as the address of the memory it points at -- read
    /// through, written through, its length taken -- directly or through the
    /// copies and width changes that lead to such a use: what tells a call's
    /// object result from a number, which the IR types do not. One pass over
    /// the function, then back along the copies, for all of its calls at once.
    /// </summary>
    private static HashSet<VReg> AddressRegisters(Function f)
    {
        HashSet<VReg> used = new();
        Dictionary<VReg, List<VReg>> copiedFrom = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op is Opcode.Load or Opcode.Store or Opcode.ArrayLength or Opcode.InitArrayLength
                    && i.Operands.Count > 0 && i.Operands[0] is RegOperand address)
                    used.Add(address.Reg);
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 && i.Dest is not null
                    && i.Operands.Count == 1 && i.Operands[0] is RegOperand from)
                {
                    if (!copiedFrom.TryGetValue(i.Dest, out List<VReg>? sources)) copiedFrom[i.Dest] = sources = new();
                    sources.Add(from.Reg);
                }
            }
        Stack<VReg> work = new(used);
        while (work.TryPop(out VReg? r))
            if (copiedFrom.TryGetValue(r, out List<VReg>? sources))
                foreach (VReg source in sources)
                    if (used.Add(source)) work.Push(source);
        return used;
    }

    private bool OwnFreshResult(Function f, Block b, Instr call, bool readsPrevious, bool repeats = true)
    {
        // BEFORE THE CALL WHEN THE CALL CANNOT BE READING IT. The previous
        // result is reachable only through registers derived from it (it
        // does not escape), so a call that is handed none of them cannot
        // see it: give it back first, as Own does for an allocation, and the
        // callee's own allocation lands on the same bytes through the
        // collector's lock-free top-of-buffer path. A call handed the old
        // object -- x = Grow(x) -- gives it back after instead.
        if (readsPrevious ? !OwnAfter(f, b, call, repeats) : !Own(f, b, call, -1, repeats)) return false;
        // Own/OwnAfter recorded the object's frees; the callee is what filled its fields.
        _records[f][^1].FreshCallee = call.Callee;
        _ownedCalls.Add(call);
        Owned++;
        OwnedReturns++;
        return true;
    }

    /// <summary>
    /// Own's shape for an object that arrives from a call rather than an
    /// allocation: the slot's previous object is given back AFTER the call,
    /// not before it, since the call may be reading that object -- `x =
    /// Grow(x)` hands the old one in to make the new one -- and the liveness
    /// proof has already shown that nothing uses the old one once the call
    /// is made. Frees at every return as Own does.
    /// </summary>
    private bool OwnAfter(Function f, Block block, Instr call, bool repeats = true)
    {
        int at = block.Instrs.IndexOf(call);
        if (at < 0) return false;
        OwnedRecord record = new() { Origin = call, Root = call.Dest!, Renew = repeats ? call : null, RenewAfter = true };
        int word = IrTypes.Word.Bytes();
        FrameSlot slot = f.NewSlot(word, word, "owned");

        VReg entryAddr = f.NewReg(IrTypes.Word, "ownedp");
        int entryLine = EntryLine(f, call.Line);
        f.Entry.Instrs.InsertRange(0, new List<Instr>
        {
            new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(slot) }, Line = entryLine },
            new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = entryLine },
        });

        at = block.Instrs.IndexOf(call);
        VReg addr = f.NewReg(IrTypes.Word, "ownedp");
        VReg prev = f.NewReg(IrTypes.Word, "owned");
        VReg made = f.NewReg(IrTypes.Word, "owned");
        List<Instr> after = new()
        {
            new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = call.Line },
        };
        if (repeats)
        {
            after.Add(new Instr { Op = Opcode.Load, Size = word, Dest = prev, Operands = { new RegOperand(addr) }, Line = call.Line });
            record.Frees.Add((block, AppendFree(f, after, prev, call.Line), prev));
        }
        after.Add(call.Dest!.Type == IrTypes.Word
            ? new Instr { Op = Opcode.Copy, Dest = made, Operands = { new RegOperand(call.Dest) }, Line = call.Line }
            : new Instr { Op = IrTypes.Word == IrType.I32 ? Opcode.Trunc64 : Opcode.ZExt32,
                Dest = made, Operands = { new RegOperand(call.Dest) }, Line = call.Line });
        after.Add(new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(addr), new RegOperand(made) }, Line = call.Line });
        block.Instrs.InsertRange(at + 1, after);
        _bookkeeping.UnionWith(after);

        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is not { Op: Opcode.Ret }) continue;
            int r = b.Instrs.Count - 1;
            VReg a = f.NewReg(IrTypes.Word, "ownedp");
            VReg p = f.NewReg(IrTypes.Word, "owned");
            int exitLine = b.Instrs[r].Line;
            List<Instr> releaseExit = new()
            {
                new Instr { Op = Opcode.Copy, Dest = a, Operands = { new SlotOperand(slot) }, Line = exitLine },
                new Instr { Op = Opcode.Load, Size = word, Dest = p, Operands = { new RegOperand(a) }, Line = exitLine },
            };
            record.Frees.Add((b, AppendFree(f, releaseExit, p, exitLine), p));
            b.Instrs.InsertRange(r, releaseExit);
            _bookkeeping.UnionWith(releaseExit);
        }
        Record(f, record);
        return true;
    }

    /// <summary>
    /// Where each register is read, and in which block, as the function
    /// stood when asked: made once per function. The instructions the
    /// lifetime passes add read only registers of their own, so it stays
    /// right for every register a later candidate is judged on.
    /// </summary>
    private sealed class UseIndex
    {
        public readonly Dictionary<VReg, List<Instr>> Readers = new();
        public readonly Dictionary<Instr, Block> Home = new(ReferenceEqualityComparer.Instance);
        public UseIndex(Function f)
        {
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    Home[i] = b;
                    foreach (Operand o in i.Operands)
                    {
                        if (o is not RegOperand use) continue;
                        if (!Readers.TryGetValue(use.Reg, out List<Instr>? list)) Readers[use.Reg] = list = new();
                        list.Add(i);
                    }
                }
        }
    }

    /// <summary>
    /// FREED AT ITS LAST USE: an object whose every use, and every use of
    /// what is derived from it, lies in the block that made it, after it, with
    /// none of it live out of the block or read by a handler, is given back
    /// right after the last of those uses. No slot, no frees at the returns,
    /// no giving back of last time's before the next: one call. What most
    /// temporaries are -- a string made and handed to the next concatenation
    /// -- and, owned the slot way, each cost a slot and two frees.
    /// </summary>
    private bool FreeAtLastUse(Function f, Block b, Instr made, HashSet<VReg> derived, Liveness liveness,
        HashSet<VReg> pads, UseIndex uses, long bytes)
    {
        if (made.Dest is null) return false;
        foreach (VReg r in derived)
            if (pads.Contains(r) || !liveness.Tracks(r) || liveness.IsLiveOut(b, r)) return false;
        Instr? last = null;
        int lastAt = b.Instrs.IndexOf(made);
        if (lastAt < 0) return false;
        int madeAt = lastAt;
        foreach (VReg r in derived)
        {
            if (!uses.Readers.TryGetValue(r, out List<Instr>? readers)) continue;
            foreach (Instr reader in readers)
            {
                if (ReferenceEquals(reader, made) || _bookkeeping.Contains(reader)) continue;
                if (!uses.Home.TryGetValue(reader, out Block? home) || !ReferenceEquals(home, b)) return false;
                int at = b.Instrs.IndexOf(reader);
                if (at < madeAt) return false;
                if (at > lastAt) { lastAt = at; last = reader; }
            }
        }
        // A branch on it: there is no after in this block.
        if (last is not null && ReferenceEquals(last, b.Terminator)) return false;
        List<Instr> free = new();
        Instr call = AppendFree(f, free, made.Dest, b.Instrs[lastAt].Line);
        b.Instrs.InsertRange(lastAt + 1, free);
        _bookkeeping.UnionWith(free);
        // The owned field rules see it as dying at that free.
        OwnedRecord record = new() { Origin = made, Root = made.Dest, Renew = call, Bytes = bytes };
        record.Frees.Add((b, call, made.Dest));
        Record(f, record);
        return true;
    }

    /// <summary>The blocks on some cycle of the graph: the only places an allocation site runs twice in one call.</summary>
    private static HashSet<Block> Repeating(Function f)
    {
        Cfg cfg = new(f);
        // A LANDING PAD IS ENTERED FROM WHERE ITS HANDLER WAS INSTALLED (the
        // LabelAddr naming it), by an unwind no edge of the graph shows. With
        // that edge added, a pad that leads back into the loop that installed
        // it is on the loop's cycle, and one that leaves the loop is not --
        // it runs at most once however often its handler went in.
        Dictionary<Block, List<Block>> into = new(ReferenceEqualityComparer.Instance), from = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.LabelAddr)
                    foreach (Block pad in i.Targets)
                    {
                        if (!into.TryGetValue(b, out List<Block>? outs)) into[b] = outs = new();
                        outs.Add(pad);
                        if (!from.TryGetValue(pad, out List<Block>? ins)) from[pad] = ins = new();
                        ins.Add(b);
                    }
        IEnumerable<Block> Succs(Block b) => into.TryGetValue(b, out List<Block>? extra) ? cfg.Succs(b).Concat(extra) : cfg.Succs(b);
        IEnumerable<Block> Preds(Block b) => from.TryGetValue(b, out List<Block>? extra) ? cfg.Preds(b).Concat(extra) : cfg.Preds(b);
        // Kosaraju: finishing order on the graph, then components on its
        // reverse; a block is on a cycle if its component has two blocks or
        // it is its own successor.
        List<Block> order = new();
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        foreach (Block root in f.Blocks)
        {
            if (!seen.Add(root)) continue;
            Stack<(Block Block, IEnumerator<Block> Next)> stack = new();
            stack.Push((root, Succs(root).GetEnumerator()));
            while (stack.Count > 0)
            {
                (Block block, IEnumerator<Block> next) = stack.Peek();
                if (next.MoveNext())
                {
                    if (seen.Add(next.Current)) stack.Push((next.Current, Succs(next.Current).GetEnumerator()));
                }
                else { stack.Pop(); order.Add(block); }
            }
        }
        HashSet<Block> repeating = new(ReferenceEqualityComparer.Instance);
        HashSet<Block> assigned = new(ReferenceEqualityComparer.Instance);
        for (int k = order.Count - 1; k >= 0; k--)
        {
            if (!assigned.Add(order[k])) continue;
            List<Block> component = new() { order[k] };
            Stack<Block> work = new();
            work.Push(order[k]);
            while (work.TryPop(out Block? block))
                foreach (Block pred in Preds(block))
                    if (assigned.Add(pred)) { component.Add(pred); work.Push(pred); }
            if (component.Count > 1 || Succs(order[k]).Contains(order[k])) repeating.UnionWith(component);
        }
        return repeating;
    }

    /// <summary>The line the function's first instruction has: what code put before it is given.</summary>
    internal static int EntryLine(Function f, int otherwise) => f.Entry.Instrs.Count > 0 ? f.Entry.Instrs[0].Line : otherwise;

    /// <summary>An operand as a machine word, for a runtime routine that takes an nint (a null, a symbol, a register).</summary>
    internal static Operand Word(Function f, List<Instr> output, Operand value, int line)
    {
        switch (value)
        {
            case ImmOperand imm:
                return new ImmOperand(imm.Value, IrTypes.Word);
            case RegOperand { Reg: var r } when r.Type == IrTypes.Word:
                return value;
            case RegOperand { Reg.Type: IrType.I32 or IrType.I64 } reg:
                return new RegOperand(Word(f, output, reg.Reg, line));
            default:
            {
                VReg word = f.NewReg(IrTypes.Word);
                output.Add(new Instr { Op = Opcode.Copy, Dest = word, Operands = { value }, Line = line });
                return new RegOperand(word);
            }
        }
    }

    /// <summary>A register as a machine word: itself, or widened or narrowed to one.</summary>
    internal static VReg Word(Function f, List<Instr> output, VReg r, int line, string? name = null)
    {
        if (r.Type == IrTypes.Word || r.Type is not (IrType.I32 or IrType.I64)) return r;
        VReg word = f.NewReg(IrTypes.Word, name);
        output.Add(new Instr { Op = IrTypes.Word == IrType.I64 ? Opcode.ZExt32 : Opcode.Trunc64, Dest = word,
            Operands = { new RegOperand(r) }, Line = line });
        return word;
    }

    private static Instr AppendFree(Function f, List<Instr> output, VReg pointer, int line)
    {
        // Ownership slots are machine words, and so is Runtime.Free's parameter.
        VReg argument = Word(f, output, pointer, line, "freeAddress");
        Instr free = new Instr { Op = Opcode.Call, Callee = Freer,
            Operands = { new RegOperand(argument) }, Line = line };
        output.Add(free);
        return free;
    }

    /// <summary>Whether any register derived from the allocation is live just before it: a loop carrying last time's object.</summary>
    // LANDING PADS HAVE NO PREDECESSORS in the control-flow graph: an
    // exception arrives at a catch from anywhere in its try, and nothing
    // the handler reads is live anywhere else as the graph sees it. For a
    // lifetime that is exactly wrong -- an object freed inside a try and
    // read in its catch was read after it was given back. So a register a
    // handler reads before writing is taken to be live at every point in
    // the function, wherever the try that reaches it is.
    internal static HashSet<VReg> PadLive(Liveness liveness)
    {
        HashSet<VReg> live = new();
        foreach (Block b in liveness.Cfg.Function.Blocks)
            if (b.IsLandingPad) live.UnionWith(liveness.LiveIn(b));
        return live;
    }

    internal static bool LiveAtSelf(Liveness liveness, Block b, Instr alloc, HashSet<VReg> derived)
        => LiveAtSelf(liveness, PadLive(liveness), b, alloc, derived);

    /// <summary>
    /// The same, with the pads' live registers given: a caller asking about
    /// many allocations works them out once. The analysis may be older than
    /// the function (see <see cref="Liveness.WalkBackwards"/>); a derived
    /// register it does not know is taken to be live.
    /// </summary>
    internal static bool LiveAtSelf(Liveness liveness, HashSet<VReg> pads, Block b, Instr alloc, HashSet<VReg> derived)
    {
        foreach (VReg r in derived)
        {
            if (pads.Contains(r) || !liveness.Tracks(r)) return true;
        }
        foreach ((Instr i, ulong[] liveAfter) in liveness.WalkBackwards(b, skipNewer: true))
        {
            if (!ReferenceEquals(i, alloc))
            {
                continue;
            }
            // Live after the allocation minus what it defines is live before
            // it, as far as derived registers go: none of them is defined
            // by the allocation except its own result.
            foreach (VReg r in derived)
            {
                if (!ReferenceEquals(r, alloc.Dest) && Liveness.Test(liveAfter, r.Id))
                {
                    return true;
                }
            }
            return false;
        }
        return true;
    }

    private static bool ConstantSize(Function f, Operand o, out long size)
    {
        size = 0;
        for (int hops = 0; hops < 8; hops++)
        {
            switch (o)
            {
                case ImmOperand imm:
                    size = imm.Value;
                    return true;
                case RegOperand r:
                {
                    Instr? def = SingleDefinition(f, r.Reg);
                    if (def is null || def.Operands.Count != 1)
                    {
                        return false;
                    }
                    if (def.Op is not (Opcode.Copy or Opcode.SExt32 or Opcode.ZExt32 or Opcode.Trunc64))
                    {
                        return false;
                    }
                    o = def.Operands[0];
                    break;
                }
                default:
                    return false;
            }
        }
        return false;
    }

    private static Instr? SingleDefinition(Function f, VReg r)
    {
        Instr? found = null;
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (ReferenceEquals(i.Dest, r))
                {
                    if (found is not null)
                    {
                        return null;
                    }
                    found = i;
                }
            }
        }
        return found;
    }

    // ---- the module's verdict ----------------------------------------------------------

    private bool AnyAllocationReachable(Module m, Dictionary<string, Function> byName)
    {
        if (m.PreserveExports || m.Entry is null || !byName.TryGetValue(m.Entry, out Function? entry))
        {
            return true;        // a library: its consumers decide
        }
        // A PROGRAM THAT TALKS TO THE COLLECTOR ITSELF has one (Lowering).
        if (m.CallsCollector) return true;
        return CollectorSites(m, byName, entry, paths: false).Count > 0;
    }



    /// <summary>
    /// THE ALLOCATIONS A COLLECTOR IS STILL NEEDED FOR, reachable from the
    /// entry over code and data. A function reaches what it calls and every
    /// symbol it names; a data item -- a vtable, a descriptor -- every symbol
    /// its relocations name. A block that ends in a trap is the program dying,
    /// and what it allocates on the way decides nothing.
    ///
    /// An allocation this pass owns is freed on every path out of its
    /// function. So is the object a fresh function RETURNS, wherever the call
    /// to it is owned: the caller frees it. Such an allocation counts only if
    /// its function is reached through a call that does not own the result --
    /// through any path at all -- and a function's other allocations count
    /// wherever it is reached. Judging the returned object by the function
    /// alone made every program that called a fresh helper need a collector.
    /// </summary>
    private List<string> CollectorSites(Module m, Dictionary<string, Function> byName, Function entry, bool paths)
    {
        Dictionary<string, DataItem> data = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) data[d.Name] = d;

        // A DESCRIPTOR'S METHODS ARE REACHED BY THE CALLS THAT REACH THEM. A
        // virtual call the pass resolved (IndirectTargets) reaches exactly its
        // overrides, so a live type's other methods -- an exception's
        // ToString, its trace -- are not reached just because the type is.
        // Any virtual call left unresolved could reach any of them, and then
        // every method of every live descriptor is reached, as before. An
        // indirect call that is not virtual calls through an address some
        // code or data named, which the walk follows anyway.
        Dictionary<string, DataItem> items = data;
        bool everyMethod = m.Functions.Any(f => f.Blocks.Any(b => b.Instrs.Any(i =>
            i.Op == Opcode.CallIndirect && i.DispatchType is not null && (_indirect is null || !_indirect.ContainsKey(i)))));

        // Whether each symbol has been reached, and whether through a use
        // that does not own a fresh result (true is the stronger state).
        Dictionary<string, bool> reached = new(StringComparer.Ordinal);
        Dictionary<string, string> reachedFrom = new(StringComparer.Ordinal);
        Stack<(string Name, bool Unowned)> work = new();
        void Reach(string name, bool unowned, string? from)
        {
            if (reached.TryGetValue(name, out bool was) && (was || !unowned)) return;
            reached[name] = unowned;
            if (from is not null) reachedFrom[name] = from;
            work.Push((name, unowned));
        }
        Reach(entry.Name, true, null);

        List<string> sites = new();
        HashSet<Instr> counted = new(ReferenceEqualityComparer.Instance);
        OnceRun once = new(m, byName, entry);
        while (work.Count > 0)
        {
            (string name, bool unowned) = work.Pop();
            if (reached[name] != unowned) continue;     // superseded by a stronger visit
            if (data.TryGetValue(name, out DataItem? item))
            {
                bool descriptor = IsDescriptor(name);
                foreach (DataReloc r in item.Relocs)
                    if (everyMethod || !descriptor || !byName.ContainsKey(r.Symbol)) Reach(r.Symbol, true, name);
                continue;
            }
            if (!byName.TryGetValue(name, out Function? f)) continue;
            _freshOrigins.TryGetValue(f.Name, out HashSet<Instr>? origins);
            foreach (Block b in f.Blocks)
            {
                if (b.Terminator is { Op: Opcode.Unreachable }) continue;
                foreach (Instr i in b.Instrs)
                {
                    bool origin = origins is not null && origins.Contains(i);
                    if (i.Callee is not null && IsAllocator(i.Callee) && !_owned.Contains(i) && !_permanent.Contains(i) && !_fieldOwned.Contains(i)
                        && !ThrownCovered(i, items) && !once.RunsOnce(f, b)
                        && (!origin || unowned) && counted.Add(i))
                    {
                        sites.Add(paths ? $"{f.Name}:{i.Line} {i.Callee}\n      reached: {Path(f.Name)}" : f.Name);
                    }
                    if (i.Callee is not null && !_owned.Contains(i))
                    {
                        // The callee's returned object is freed here when the
                        // call is owned; handed on as this function's own
                        // result, it is owned exactly as far as this one is.
                        bool callUnowned = _ownedCalls.Contains(i) ? false : origin ? unowned : true;
                        Reach(i.Callee, callUnowned, f.Name);
                    }
                    foreach (Operand o in i.Operands) if (o is SymOperand sym) Reach(sym.Name, true, f.Name);
                    if (i.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(i, out string[]? targets))
                        foreach (string t in targets) Reach(t, true, f.Name);
                }
            }
        }
        return sites;

        string Path(string name)
        {
            List<string> chain = new() { name };
            while (reachedFrom.TryGetValue(chain[^1], out string? up) && chain.Count < 8 && !chain.Contains(up)) chain.Add(up);
            return string.Join(" <- ", chain);
        }
    }

    /// <summary>
    /// WHAT RUNS AT MOST ONCE IN A RUN. An allocation made at most once needs
    /// no collector: were it never freed, it would cost one object for the
    /// life of the program, as a C program's tables made at start-up do --
    /// the tables a probe makes, the message a fatal path builds. The entry
    /// and every static initialiser run once; so does a function every direct
    /// call to which is in a block that runs once of a function that does,
    /// and whose address nothing takes. Found from that start outward, so a
    /// recursive function is never among them. A block runs once when it is
    /// on no cycle, a landing pad's entry from its handler's install counted
    /// (Repeating).
    /// </summary>
    private sealed class OnceRun
    {
        private readonly HashSet<string> _once = new(StringComparer.Ordinal);
        private readonly Dictionary<Function, HashSet<Block>> _blocks = new();

        public OnceRun(Module m, Dictionary<string, Function> byName, Function entry)
        {
            HashSet<string> addressed = new(StringComparer.Ordinal);
            foreach (DataItem d in m.Data) foreach (DataReloc r in d.Relocs) addressed.Add(r.Symbol);
            // Only calls the program can make count against a function: a
            // caller nothing reaches runs never, not many times. Reached over
            // calls, named addresses and data, as widely as anything is.
            Dictionary<string, DataItem> data = new(StringComparer.Ordinal);
            foreach (DataItem d in m.Data) data[d.Name] = d;
            HashSet<string> reached = new(StringComparer.Ordinal) { entry.Name };
            Stack<string> pending = new();
            pending.Push(entry.Name);
            foreach (Function f in m.Functions)
                if (f.Name.Contains("StaticInit$", StringComparison.Ordinal) && reached.Add(f.Name)) pending.Push(f.Name);
            while (pending.TryPop(out string? name))
            {
                if (data.TryGetValue(name, out DataItem? item))
                {
                    foreach (DataReloc r in item.Relocs) if (reached.Add(r.Symbol)) pending.Push(r.Symbol);
                    continue;
                }
                if (!byName.TryGetValue(name, out Function? g)) continue;
                foreach (Block b in g.Blocks)
                    foreach (Instr i in b.Instrs)
                    {
                        if (i.Callee is not null && reached.Add(i.Callee)) pending.Push(i.Callee);
                        foreach (Operand o in i.Operands) if (o is SymOperand sym && reached.Add(sym.Name)) pending.Push(sym.Name);
                    }
            }
            Dictionary<string, List<(Function F, Block B)>> calls = new(StringComparer.Ordinal);
            foreach (Function f in m.Functions.Where(f => reached.Contains(f.Name)))
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                    {
                        foreach (Operand o in i.Operands) if (o is SymOperand sym) addressed.Add(sym.Name);
                        if (i.Op == Opcode.Call && i.Callee is not null)
                        {
                            if (!calls.TryGetValue(i.Callee, out var list)) calls[i.Callee] = list = new();
                            list.Add((f, b));
                        }
                    }
            _once.Add(entry.Name);
            foreach (Function f in m.Functions)
                if (f.Name.Contains("StaticInit$", StringComparison.Ordinal) && f.Async is null) _once.Add(f.Name);

            // A LATCHED FUNCTION RUNS ONCE HOWEVER OFTEN IT IS CALLED: it
            // begins `if (done) return; done = true;` on a static nothing
            // anywhere sets to anything but true, and whose address nothing
            // takes. Every call after the first returns at the test.
            Dictionary<string, bool> latchOnly = new(StringComparer.Ordinal);
            foreach (Function f in m.Functions.Where(f => reached.Contains(f.Name)))
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                    {
                        if (i.Op == Opcode.Store && i.Operands.Count == 2 && i.Operands[0] is SymOperand { Name: var flag })
                        {
                            latchOnly[flag] = latchOnly.GetValueOrDefault(flag, true) && i.Operands[1] is ImmOperand { Value: not 0 };
                            if (i.Operands[1] is SymOperand { Name: var stored }) latchOnly[stored] = false;
                        }
                        else
                            for (int k = 0; k < i.Operands.Count; k++)
                                if (i.Operands[k] is SymOperand { Name: var named } && !(i.Op == Opcode.Load && k == 0))
                                    latchOnly[named] = false;
                    }
            _latchOnly = latchOnly;
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (Function f in m.Functions)
                {
                    if (_once.Contains(f.Name) || f.Async is not null || addressed.Contains(f.Name)
                        || !calls.TryGetValue(f.Name, out var sites) || sites.Count == 0) continue;
                    if (sites.All(site => _once.Contains(site.F.Name) && BlockOnce(site.F, site.B)))
                    {
                        _once.Add(f.Name);
                        changed = true;
                    }
                }
            }
        }

        public bool RunsOnce(Function f, Block b) => _once.Contains(f.Name) && BlockOnce(f, b) || LatchedBlocks(f).Contains(b);

        private Dictionary<string, bool> _latchOnly = new(StringComparer.Ordinal);
        private readonly Dictionary<Function, HashSet<Block>> _latched = new();

        /// <summary>
        /// THE BLOCKS A LATCH LETS THROUGH ONCE: `if (done) ...; done = true;`
        /// on a static nothing sets but to true and whose address nothing
        /// takes. The block that sets it is entered only from the test, only
        /// while the flag is clear, and sets it before any call could come
        /// back round -- so it runs at most once in the whole run, whoever
        /// calls its function and however often (or from a loop the function
        /// was inlined into). So does every block it dominates that cannot
        /// come round to itself again without passing through it.
        /// </summary>
        private HashSet<Block> LatchedBlocks(Function f)
        {
            if (_latched.TryGetValue(f, out HashSet<Block>? known)) return known;
            HashSet<Block> found = new(ReferenceEqualityComparer.Instance);
            _latched[f] = found;
            if (f.Async is not null) return found;
            Dictionary<VReg, Instr> defs = new();
            foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (i.Dest is not null) defs[i.Dest] = i;
            Cfg? cfg = null;
            foreach (Block test in f.Blocks)
            {
                if (test.Terminator is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var cond }] } branch || branch.Targets.Count != 2
                    || !defs.TryGetValue(cond, out Instr? read)) continue;
                Block clear = branch.Targets[1];
                if (read is { Op: Opcode.Eq, Operands: [RegOperand { Reg: var inner }, ImmOperand { Value: 0 }] })
                {
                    clear = branch.Targets[0];
                    if (!defs.TryGetValue(inner, out read)) continue;
                }
                if (read is not { Op: Opcode.Load, Operands: [SymOperand { Name: var flag } at] } || !_latchOnly.GetValueOrDefault(flag)
                    || ReferenceEquals(clear, test)) continue;
                cfg ??= new Cfg(f);
                if (cfg.Preds(clear).Count != 1 || cfg.IsRoot(clear)) continue;
                bool sets = false;
                foreach (Instr i in clear.Instrs)
                {
                    if (i.Op is Opcode.Call or Opcode.CallIndirect) break;
                    if (i.Op == Opcode.Store && i.Operands[0] is SymOperand { Name: var set } where && set == flag && where.Offset == at.Offset
                        && i.Operands[1] is ImmOperand { Value: not 0 }) { sets = true; break; }
                }
                if (!sets) continue;
                foreach (Block b in f.Blocks)
                {
                    if (!cfg.Dominates(clear, b) || found.Contains(b)) continue;
                    if (ReferenceEquals(b, clear) || !ComesRound(cfg, b, clear)) found.Add(b);
                }
            }
            return found;
        }

        /// <summary>Whether `b` can reach itself again without passing through `latch`.</summary>
        private static bool ComesRound(Cfg cfg, Block b, Block latch)
        {
            // The unwind into a landing pad goes from where its handler was
            // installed (the LabelAddr), as Repeating counts it.
            static IEnumerable<Block> Next(Cfg cfg, Block x) =>
                cfg.Succs(x).Concat(x.Instrs.Where(i => i.Op == Opcode.LabelAddr).SelectMany(i => i.Targets));
            HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
            Stack<Block> work = new(Next(cfg, b));
            while (work.TryPop(out Block? x))
            {
                if (ReferenceEquals(x, b)) return true;
                if (ReferenceEquals(x, latch) || !seen.Add(x)) continue;
                foreach (Block next in Next(cfg, x)) work.Push(next);
            }
            return false;
        }

        private bool BlockOnce(Function f, Block b)
        {
            if (!_blocks.TryGetValue(f, out var known))
            {
                _blocks[f] = known = Repeating(f);
            }
            return !known.Contains(b);
        }
    }

    /// <summary>
    /// CORSAC_ALLOC_REPORT: every allocation a collector would still be needed
    /// for -- reachable from the entry over code and data as
    /// AnyAllocationReachable walks it, not promoted to the frame and not
    /// owned -- one line each, function and source line, on stderr. What is
    /// left between a program and needing no collector at all.
    /// </summary>
    private void AllocationReport(Module m, Dictionary<string, Function> byName)
    {
        if (m.Entry is null || !byName.TryGetValue(m.Entry, out Function? entry))
        {
            Console.Error.WriteLine("alloc report: " + m.Name + " has no entry; compile the whole program to see it");
            return;
        }
        List<string> sites = CollectorSites(m, byName, entry, paths: true);
        foreach (string line in _fieldReport.Where(l => Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is not { Length: > 1 } which || l.Contains(which, StringComparison.Ordinal)))
            Console.Error.WriteLine("alloc report: field " + line);
        Console.Error.WriteLine($"alloc report: thrown {_thrown.Count} ({_thrownType.Count} typed), catches keeping: {(_keptCatchAll ? "everything; " : "")}{string.Join(", ", _keptCatches.Take(12))}");
        if (Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 1 } which)
            foreach (Function f in m.Functions.Where(f => f.Name.Contains(which, StringComparison.Ordinal)))
                for (int p2 = 0; p2 < f.Params.Count; p2++)
                {
                    Flow why = Analyse(f, new[] { f.Params[p2] }, _summaries ?? new(), null);
                    Console.Error.WriteLine($"alloc report: {f.Name} param {p2} escapes={why.Escapes} via {why.Why?.Op} {why.Why?.Callee} {string.Join(" ", why.Why?.Operands.Select(o => o.ToString()) ?? Array.Empty<string>())}");
                }
        int indirect = m.Functions.Sum(f => f.Blocks.Sum(b => b.Instrs.Count(i => i.Op == Opcode.CallIndirect)));
        Console.Error.WriteLine($"alloc report: virtual calls resolved {_indirect?.Count ?? 0} of {indirect}"
            + string.Concat((_indirect ?? new()).Values.Where(t => Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is not "1" && t.Any(x => x.Contains(Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT")!, StringComparison.Ordinal))).Take(3).Select(t => "; [" + string.Join(", ", t) + "]"))
            + string.Concat(m.Functions.SelectMany(f => f.Blocks.SelectMany(b => b.Instrs)).Where(i => i.Op == Opcode.CallIndirect && i.DispatchType is not null && (_indirect is null || !_indirect.ContainsKey(i))).Select(i => i.DispatchType).Distinct().Take(12).Select(t => "; unresolved " + t)));
        foreach (string why in _unresolvedWhy ?? new()) Console.Error.WriteLine("alloc report: unresolved " + why);
        Console.Error.WriteLine($"alloc report: {sites.Count} allocation(s) still need a collector");
        foreach (string site in sites.OrderBy(x => x, StringComparer.Ordinal)) Console.Error.WriteLine("  " + site);
    }
}
