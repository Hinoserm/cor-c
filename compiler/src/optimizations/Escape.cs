#nullable enable
using Corsac.Lang.Ir;

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
    public const string Allocator = "m_Runtime_Alloc_1_V$I64";

    /// <summary>The allocator for memory that holds no references (strings, byte arrays).</summary>
    public const string LeafAllocator = "m_Runtime_AllocLeaf_1_V$I64";

    /// <summary>Either of the collecting allocators: every pass that follows an allocation follows both.</summary>
    public static bool IsAllocator(string? callee) => callee == Allocator || callee == LeafAllocator;

    /// <summary>At most this many bytes of a frame go to promoted objects.</summary>
    public int FrameBudget { get; init; } = 4096;

    /// <summary>A promoted object may be at most this large.</summary>
    public int ObjectLimit { get; init; } = 1024;

    public int Promoted { get; private set; }

    public void Run(Module m)
    {
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
        {
            byName[f.Name] = f;
        }

        // Which parameters of which functions escape: pessimistic until a
        // function has been analysed, bottom-up over the call graph so a
        // callee's answer is known before its callers ask. Cycles keep the
        // pessimistic answer.
        Dictionary<string, bool[]> summaries = new(StringComparer.Ordinal);
        foreach (List<Function> cycle in CallCycles(m, byName))
        {
            SummariseCycle(cycle, summaries);
            foreach (Function f in cycle)
            {
            // Whether what it returns is a fresh object it hands over: made
            // here (or by a callee that hands it over in turn), never stored
            // anywhere and never let go of any other way. Its caller then
            // owns the object as if it had made it (Fresh, PromoteIn).
            if (ReturnsFresh(f, summaries, _fresh, out List<Instr>? origins, out HashSet<VReg>? chain))
            {
                _fresh.Add(f.Name);
                _freshFields[f.Name] = FreshFields(f, summaries, origins!, chain!);
            }
            // What it does to each field of every object it is handed
            // (EscapeFields): the reference fields that hold only objects
            // made for them may be freed with an owner that dies.
            _paramFields[f.Name] = ParameterFields(f, summaries);
            }
        }

        bool canFree = byName.ContainsKey(Freer);
        bool canFreeFields = canFree && byName.ContainsKey(FieldFreer);
        OwnedFieldEscape fields = new(byName, summaries);
        foreach (Function f in m.Functions)
        {
            PromoteIn(f, summaries, canFree, fields);
            if (canFree && byName.ContainsKey(ReplacedFreer)) OwnVariables(f, summaries);
            if (canFreeFields) OwnFields(f, summaries);
        }

        m.NeedsHeap = AnyAllocationReachable(m, byName);
        LastRun = (Promoted, Owned, OwnedReturns, _fresh.Count, FieldsOwned, VariablesOwned);

        // A program that needs no collector still allocates on its way to
        // dying -- the exception object, the message it prints -- and those
        // go to a bump allocator that never frees, which the runtime provides
        // under this name. Then nothing calls the collecting allocator, and
        // the collector goes the way of every unreachable function.
        if (!m.NeedsHeap && byName.ContainsKey(BumpAllocator))
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

                        // An allocation this pass owns keeps the real
                        // allocator: a bump region cannot take a block back,
                        // and giving the block back is the whole point. Its
                        // frees therefore keep the real free as well, which
                        // is why nothing is retargeted when the module has
                        // owned an allocation anywhere.
                        string? to = null;
                        if (IsAllocator(i.Callee) && !_owned.Contains(i))
                        {
                            to = BumpAllocator;
                        }
                        else if (i.Callee == Freer && Owned == 0)
                        {
                            to = BumpFreer;
                        }
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

    /// <summary>The allocator for a program that never collects: bump and forget.</summary>
    public const string BumpAllocator = "m_Runtime_AllocBump_1_V$I64";

    /// <summary>
    /// Giving a block back by hand. The compiler calls this only for an
    /// object whose whole life it has proved, which is why the runtime may
    /// keep it as cheap as it likes; it ignores anything that is not the
    /// payload of a live block, so freeing a zero is a no-op.
    /// </summary>
    public const string Freer = "m_Runtime_Free_1_V$I64";

    /// <summary>Free for a program with no heap to free into: nothing to do.</summary>
    public const string BumpFreer = "m_Runtime_FreeBump_1_V$I64";

    /// <summary>How many allocations this pass gave an explicit free rather than a frame slot.</summary>
    public int Owned { get; private set; }

    /// <summary>
    /// Allocation calls this pass took ownership of. They keep calling the
    /// real allocator -- a bump region cannot give memory back -- but they
    /// are not a reason to link a collector, because their memory comes
    /// back by a free the compiler placed, not by a collection.
    /// </summary>
    private readonly HashSet<Instr> _owned = new(ReferenceEqualityComparer.Instance);

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
    private readonly HashSet<string> _fresh = new(StringComparer.Ordinal);

    /// <summary>Calls to fresh-returning functions whose result this pass took ownership of.</summary>
    private readonly HashSet<Instr> _ownedCalls = new(ReferenceEqualityComparer.Instance);

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
        out List<Instr>? found, out HashSet<VReg>? returned)
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
                if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Phi)
                {
                    if (d.Operands.Count == 0 || (d.Op != Opcode.Phi && d.Operands.Count != 1)) return false;
                    foreach (Operand o in d.Operands)
                    {
                        if (o is ImmOperand { Value: 0 }) continue;
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
            Flow flow = Analyse(f, new[] { origin.Dest! }, summaries, origin, returnable: chain);
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
    }

    internal static Flow Analyse(Function f, IEnumerable<VReg> roots, Dictionary<string, bool[]> summaries, Instr? source,
        HashSet<Instr>? ownedStores = null, HashSet<VReg>? returnable = null, HashSet<VReg>? joinable = null)
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

                    bool touches = false;
                    foreach (VReg r in IrInfo.Uses(i))
                    {
                        if (flow.Derived.Contains(r))
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

                        case Opcode.Call when IsCollectorNote(i.Callee):
                            // The collector told of a reference (a write
                            // barrier): it keeps no pointer the program can
                            // use, and refuses to mark a block given back.
                            break;

                        case Opcode.Call:
                        {
                            if (i.Callee is null || !summaries.TryGetValue(i.Callee, out bool[]? summary))
                            {
                                flow.Escapes = true;
                                break;
                            }
                            for (int a = 0; a < i.Operands.Count; a++)
                            {
                                if (i.Operands[a] is RegOperand arg && flow.Derived.Contains(arg.Reg)
                                    && (a >= summary.Length || summary[a]))
                                {
                                    flow.Escapes = true;
                                }
                            }
                            // The result of a call that took the pointer is
                            // not assumed to be the pointer: a callee that
                            // returns its argument is summarised as escaping
                            // that argument.
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
                        break;
                    }
                }
                if (flow.Escapes)
                {
                    break;
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
            if (defs.GetValueOrDefault(d) > 1 && (returnable is null || !returnable.Contains(d))
                && (joinable is null || !joinable.Contains(d)))
            {
                flow.Escapes = true;    // shared with another value; unknowable
                return;
            }
            if (flow.Derived.Add(d))
            {
                changed = true;
            }
        }
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

    private static void SummariseCycle(List<Function> cycle, Dictionary<string, bool[]> summaries)
    {
        if (cycle.Count == 1 && !CallsItself(cycle[0]))
        {
            summaries[cycle[0].Name] = ParameterSummary(cycle[0], summaries);
            return;
        }
        foreach (Function f in cycle) summaries[f.Name] = new bool[f.Params.Count];
        for (int round = 0; round < CycleRounds; round++)
        {
            bool changed = false;
            foreach (Function f in cycle)
            {
                bool[] again = ParameterSummary(f, summaries);
                if (!again.AsSpan().SequenceEqual(summaries[f.Name])) { summaries[f.Name] = again; changed = true; }
            }
            if (!changed) return;
        }
        foreach (Function f in cycle) summaries[f.Name] = Enumerable.Repeat(true, f.Params.Count).ToArray();
    }

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
                    if (i.Op == Opcode.Call && i.Callee is not null && byName.TryGetValue(i.Callee, out Function? c) && seen.Add(c))
                        list.Add(c);
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
    private static bool[] ParameterSummary(Function f, Dictionary<string, bool[]> summaries)
    {
        bool[] result = new bool[f.Params.Count];
        for (int p = 0; p < f.Params.Count; p++)
        {
            VReg param = f.Params[p];
            if (param.Type is not (IrType.I32 or IrType.I64))
            {
                continue;
            }
            Flow flow = Analyse(f, new[] { param }, summaries, null);
            result[p] = flow.Escapes;
        }
        return result;
    }

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

        int budget = FrameBudget;
        Liveness? liveness = null;
        List<OwnedFieldEscape.Owner> owners = new();

        foreach (Block b in PromotionOrder(f))
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (canFree && IsFreshCall(i) && i.Dest is not null && !_ownedCalls.Contains(i))
                {
                    // A fresh function's result: the caller owns it. Never a
                    // frame slot -- the callee made it on the heap -- but
                    // freed at its last use here, as a dynamic allocation is.
                    if (OwnFreshResult(f, b, i, summaries, ref liveness))
                    {
                        // Instructions went in before the call; carry on
                        // just after it (what went in after is bookkeeping,
                        // no allocation or call to own).
                        k = b.Instrs.IndexOf(i);
                    }
                    continue;
                }
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
                if (flow.Escapes && sized && owners.Count != 0 && new Defs(f).IsSingle(i.Dest))
                {
                    HashSet<VReg> roots = new() { i.Dest };
                    HashSet<Instr> stores = new();
                    Defs fieldDefs = new(f);
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
                if (flow.Escapes)
                {
                    continue;
                }

                // Inside a loop the slot -- or, for an owned allocation, the
                // one pointer the function remembers -- is reused each time
                // round, so the previous object must be dead by the time this
                // runs again.
                liveness ??= new Liveness(f);
                if (LiveAtSelf(liveness, b, i, flow.Derived))
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
                    if (Own(f, b, i, ConstantSize(f, i.Operands[0], out long ownedBytes) ? ownedBytes : -1))
                    {
                        _owned.Add(i);
                        Owned++;
                        liveness = null;
                        // The allocation moved: instructions went in before it,
                        // and two after it that must not be scanned again.
                        k = b.Instrs.IndexOf(i) + 2;
                    }
                    continue;
                }

                int bytes = (int)((size + 7) & ~7L);
                FrameSlot slot = f.NewSlot(bytes, 8, "obj");
                VReg addr = f.NewReg(IrTypes.Word, "stackobj");

                // The allocator answers zeroed memory; so does this.
                List<Instr> replacement = new()
                {
                    new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = i.Line },
                    new Instr { Op = Opcode.MemSet, Operands = { new RegOperand(addr), new ImmOperand(0, IrType.I32), new ImmOperand(bytes, IrTypes.Word) }, Line = i.Line },
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
                Record(f, new OwnedRecord { Origin = replacement[1], Root = i.Dest, SlotAddress = addr, Slot = slot, Renew = replacement[1], Bytes = bytes });
                k += replacement.Count - 1;
                budget -= bytes;
                Promoted++;
                // Only base references may anchor another generation: an
                // interior reference would need a translated descendant path.
                if (canAnchor) owners.Add(promotedOwner);
                liveness = null;        // the block changed; recompute if asked again
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
    private bool Own(Function f, Block block, Instr alloc, long bytes = -1)
    {
        OwnedRecord record = new() { Origin = alloc, Root = alloc.Dest!, Renew = alloc, Bytes = bytes };
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

        // Entry: nothing owned yet.
        VReg entryAddr = f.NewReg(IrTypes.Word, "ownedp");
        f.Entry.Instrs.InsertRange(0, new List<Instr>
        {
            new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(slot) }, Line = alloc.Line },
            new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = alloc.Line },
        });

        int at = block.Instrs.IndexOf(alloc);
        if (at < 0)
        {
            return false;
        }

        // Before the allocation: give back what the last one made.
        VReg addr = f.NewReg(IrTypes.Word, "ownedp");
        VReg prev = f.NewReg(IrTypes.Word, "owned");
        List<Instr> releasePrevious = new()
        {
            new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = alloc.Line },
            new Instr { Op = Opcode.Load, Size = word, Dest = prev, Operands = { new RegOperand(addr) }, Line = alloc.Line },
        };
        record.Frees.Add((block, AppendFree(f, releasePrevious, prev, alloc.Line), prev));
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
            List<Instr> releaseExit = new()
            {
                new Instr { Op = Opcode.Copy, Dest = a, Operands = { new SlotOperand(slot) }, Line = alloc.Line },
                new Instr { Op = Opcode.Load, Size = word, Dest = p, Operands = { new RegOperand(a) }, Line = alloc.Line },
            };
            record.Frees.Add((b, AppendFree(f, releaseExit, p, alloc.Line), p));
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
    private bool OwnFreshResult(Function f, Block b, Instr call, Dictionary<string, bool[]> summaries, ref Liveness? liveness)
    {
        if (f.Async is not null) return false;
        Defs defs = new(f, buildCfg: false);
        if (!defs.IsSingle(call.Dest!)) return false;
        Flow flow = Analyse(f, new[] { call.Dest! }, summaries, call);
        if (flow.Escapes) return false;
        liveness ??= new Liveness(f);
        if (LiveAtSelf(liveness, b, call, flow.Derived)) return false;
        // BEFORE THE CALL WHEN THE CALL CANNOT BE READING IT. The previous
        // result is reachable only through registers derived from it (it
        // does not escape), so a call that is handed none of them cannot
        // see it: give it back first, as Own does for an allocation, and the
        // callee's own allocation lands on the same bytes through the
        // collector's lock-free top-of-buffer path. A call handed the old
        // object -- x = Grow(x) -- gives it back after instead.
        bool readsPrevious = false;
        foreach (Operand o in call.Operands)
            if (o is RegOperand arg && flow.Derived.Contains(arg.Reg)) readsPrevious = true;
        if (readsPrevious ? !OwnAfter(f, b, call) : !Own(f, b, call)) return false;
        // Own/OwnAfter recorded the object's frees; the callee is what filled its fields.
        _records[f][^1].FreshCallee = call.Callee;
        _ownedCalls.Add(call);
        Owned++;
        OwnedReturns++;
        liveness = null;
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
    private bool OwnAfter(Function f, Block block, Instr call)
    {
        int at = block.Instrs.IndexOf(call);
        if (at < 0) return false;
        OwnedRecord record = new() { Origin = call, Root = call.Dest!, Renew = call, RenewAfter = true };
        int word = IrTypes.Word.Bytes();
        FrameSlot slot = f.NewSlot(word, word, "owned");

        VReg entryAddr = f.NewReg(IrTypes.Word, "ownedp");
        f.Entry.Instrs.InsertRange(0, new List<Instr>
        {
            new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(slot) }, Line = call.Line },
            new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = call.Line },
        });

        at = block.Instrs.IndexOf(call);
        VReg addr = f.NewReg(IrTypes.Word, "ownedp");
        VReg prev = f.NewReg(IrTypes.Word, "owned");
        VReg made = f.NewReg(IrTypes.Word, "owned");
        List<Instr> after = new()
        {
            new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = call.Line },
            new Instr { Op = Opcode.Load, Size = word, Dest = prev, Operands = { new RegOperand(addr) }, Line = call.Line },
        };
        record.Frees.Add((block, AppendFree(f, after, prev, call.Line), prev));
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
            List<Instr> releaseExit = new()
            {
                new Instr { Op = Opcode.Copy, Dest = a, Operands = { new SlotOperand(slot) }, Line = call.Line },
                new Instr { Op = Opcode.Load, Size = word, Dest = p, Operands = { new RegOperand(a) }, Line = call.Line },
            };
            record.Frees.Add((b, AppendFree(f, releaseExit, p, call.Line), p));
            b.Instrs.InsertRange(r, releaseExit);
            _bookkeeping.UnionWith(releaseExit);
        }
        Record(f, record);
        return true;
    }

    private static Instr AppendFree(Function f, List<Instr> output, VReg pointer, int line)
    {
        // Ownership slots are machine words, but Runtime.Free(long) has a
        // language-level 64-bit ABI on every target. Never omit the high word.
        VReg argument = pointer;
        if (pointer.Type == IrType.I32)
        {
            argument = f.NewReg(IrType.I64, "freeAddress");
            output.Add(new Instr { Op = Opcode.ZExt32, Dest = argument,
                Operands = { new RegOperand(pointer) }, Line = line });
        }
        Instr free = new Instr { Op = Opcode.Call, Callee = Freer,
            Operands = { new RegOperand(argument) }, Line = line };
        output.Add(free);
        return free;
    }

    /// <summary>Whether any register derived from the allocation is live just before it: a loop carrying last time's object.</summary>
    internal static bool LiveAtSelf(Liveness liveness, Block b, Instr alloc, HashSet<VReg> derived)
    {
        foreach ((Instr i, ulong[] liveAfter) in liveness.WalkBackwards(b))
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

        // Reachability over code AND data from the entry. A function
        // reaches what it calls and every symbol it names; a data item --
        // a vtable, a descriptor -- reaches every symbol its relocations
        // name. A vtable nothing live names is not live, and neither are the
        // methods in it.
        Dictionary<string, DataItem> data = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            data[d.Name] = d;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> work = new();
        work.Push(entry.Name);

        while (work.Count > 0)
        {
            string name = work.Pop();
            if (!seen.Add(name))
            {
                continue;
            }
            if (IsAllocator(name))
            {
                return true;
            }

            if (data.TryGetValue(name, out DataItem? item))
            {
                foreach (DataReloc r in item.Relocs)
                {
                    work.Push(r.Symbol);
                }
                continue;
            }

            if (!byName.TryGetValue(name, out Function? f))
            {
                continue;
            }

            foreach (Block b in f.Blocks)
            {
                // A block that ends in a trap is the program dying: a failed
                // bounds check, an unhandled exception. What it allocates on
                // the way is never freed by anyone, collector or not, so
                // nothing it names decides whether a collector is needed.
                if (b.Terminator is { Op: Opcode.Unreachable })
                {
                    continue;
                }
                foreach (Instr i in b.Instrs)
                {
                    // An allocation the pass owns is freed on every path out
                    // of its function, so it is tier 1 and names no heap.
                    if (i.Callee is not null && !_owned.Contains(i))
                    {
                        work.Push(i.Callee);
                    }
                    foreach (Operand o in i.Operands)
                    {
                        if (o is SymOperand s)
                        {
                            work.Push(s.Name);
                        }
                    }
                }
            }
        }
        return false;
    }
}
