#nullable enable
using Corsac.Lang.Ir;
using System.Threading.Tasks;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Inlining: a call to a small function, or to one called from exactly one
/// place, becomes the function's body at the call site.
///
/// On a 486 a call is not cheap -- the push of the arguments, the call, the
/// frame setup, the epilogue and the caller's stack adjustment are a dozen
/// instructions around a body that is often three -- and the lowering
/// produces a great many small functions, because the standard library is
/// written as small functions and the prelude's intrinsics route through
/// runtime routines. Inlining is what turns that shape into straight-line
/// code the rest of the optimiser can see through: constants flow into
/// the body, dead branches fold, and the register allocator sees one
/// function instead of a call it has to save everything around.
///
/// The IR makes this mechanical. A callee's blocks, registers and frame
/// slots are cloned into the caller with fresh identities, the arguments
/// are copied into the cloned parameters, the call site's block is split
/// so the instructions after the call become the continuation, and every
/// return in the clone becomes a copy into the call's destination and a
/// jump to the continuation. Nothing is SSA, so no phi nodes are needed.
///
/// What is refused: recursive functions (a cycle in the call graph),
/// functions containing exception landing pads or handler records (their
/// unwind state names a frame, and moving them is a job for later),
/// functions whose address is taken (something calls them indirectly, so
/// the body must stay), and anything over the size budget unless it has a
/// single caller, where inlining shrinks the program rather than growing
/// it. Callees are processed bottom-up so a leaf inlined into its caller
/// travels with that caller when it is inlined in turn.
/// </summary>
public sealed class Inline : IParallelModulePass
{
    public string Name => "inline";

    /// <summary>Optional profitability diagnostics; never changes decisions.</summary>
    public Action<Function, Function, string>? TraceDecision { get; set; }

    /// <summary>Bodies up to this many instructions are inlined wherever they are called.</summary>
    public int SmallBody { get; init; } = 40;

    /// <summary>Bounded allowance for argument staging, pushes and callee
    /// reloads avoided by inlining. This is a size/call-cost estimate, not
    /// a measured cycle model. Zero preserves the ordinary fixed budget.</summary>
    public int ArgumentWordCredit { get; init; }

    /// <summary>Extra duplication cost for conditional control flow. Kept
    /// separate from growth accounting and allocation-exposure budgets.</summary>
    public int ConditionalBranchCost { get; init; }

    /// <summary>Bounded extra budget when a constant argument controls a branch.
    /// Folding that branch can expose non-escaping allocation paths.</summary>
    public int ConstantBranchBody { get; init; } = 160;

    /// <summary>Expose child allocations made while initializing a fresh local owner.
    /// The normal growth, recursion and exception-region guards still apply.</summary>
    public int FreshOwnerBody { get; init; } = 320;

    /// <summary>
    /// A callee this size or smaller that makes a virtual call on an object
    /// its caller has just made and handed it -- a List built from an
    /// iterator Select made, a sequence walked by foreach -- comes into the
    /// caller, where the object's type is known: the call through it is then
    /// direct (Devirtualize) and the object the caller's to free.
    /// </summary>
    public int FreshArgumentBody { get; init; } = 320;

    /// <summary>
    /// Whether a callee with a try of its own may come in (Inlineable); never
    /// into an async body or iterator. Off: on, a LINQ test went from 59% of
    /// its blocks freed by the program to 48% -- whole consumers came in, and
    /// what they walked then reached calls through a vtable that nothing made
    /// direct. Kept for the pass that devirtualizes after it.
    /// </summary>
    public bool InlineHandlers { get; init; }

    /// <summary>
    /// A callee this size or smaller every return of which is an object it
    /// has just made -- an iterator method's machine, a factory -- comes into
    /// the caller, which then knows the object's type (FreshArgumentBody,
    /// Devirtualize).
    /// </summary>
    public int FreshResultBody { get; init; } = 120;

    /// <summary>Optional separate growth cap for exposing child allocations.
    /// Zero keeps the ordinary growth cap; size policies can preserve this
    /// opportunity without expanding unrelated arithmetic helpers.</summary>
    public int FreshOwnerGrowthLimit { get; init; }

    /// <summary>A function may not grow past this many instructions by inlining.</summary>
    public int GrowthLimit { get; init; } = 4000;

    /// <summary>
    /// Bodies up to this many instructions are inlined however large the
    /// caller has grown: no bigger than the call they replace -- arguments
    /// pushed, the call, the frame, the result moved -- they cannot grow it.
    /// Past the growth limit every getter, Math.Min and List indexer in the
    /// compiler's own large methods stayed a call.
    /// </summary>
    public int TinyBody { get; init; } = 12;

    /// <summary>
    /// Keep the free helper alive even though nothing calls it yet. Escape
    /// analysis has not run when the first inlining round strips dead code,
    /// and it is escape analysis that inserts the calls; without this the
    /// helper would be gone before the pass that needs it. The round after
    /// escape analysis leaves this off, so a program that gained no free
    /// still carries none of it.
    /// </summary>
    public bool KeepFreeHelper { get; init; }

    /// <summary>Functions never inlined in this run, by label (the link's first round keeps the allocators).</summary>
    public IReadOnlyCollection<string> Keep { get; init; } = Array.Empty<string>();

    public int Workers { get; set; } = 1;

    public void Run(Module m)
    {
        // A body's stores as of this run: a round that inlined into it since
        // may have given it one.
        lock (_storesField) _storesField.Clear();
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
        {
            byName[f.Name] = f;
        }

        HashSet<string> addressTaken;
        Dictionary<string, int> callers;
        HashSet<Function> recursive;
        List<Function> order;
        if (Workers > 1 && m.Functions.Count > 1)
        {
            Analyses analysis = AnalyzeParallel(m, byName, Workers);
            addressTaken = analysis.Addresses;
            callers = analysis.Callers;
            recursive = analysis.Recursive;
            order = analysis.Order;
        }
        else
        {
            addressTaken = AddressTaken(m);
            callers = CountCallers(m);
            recursive = RecursiveFunctions(m, byName);
            order = BottomUp(m, byName);
        }
        // TWO QUESTIONS, NOT ONE. Whether a body must stay -- something takes
        // its address: a vtable slot, a delegate -- and whether a call to it
        // may become its body. Every virtual and interface method is in a
        // vtable, so answering the second with the first kept every direct
        // call to List.Count, List[i], Add, TryGetValue, Equals and GetHashCode
        // a call. Only these are never inlined: what the later passes find by
        // name (below) and what the caller asked to keep.
        HashSet<string> pinned = new(Keep, StringComparer.Ordinal);
        if (KeepFreeHelper)
        {
            pinned.Add(Escape.Freer);
            pinned.Add(Escape.FieldFreer);
            pinned.Add(Escape.ReplacedFreer);
            pinned.Add(Escape.OwnedReplacedFreer);
            pinned.Add(OwnedElements.Freer);
            // What a program that needs no collector allocates and frees with.
            pinned.Add(Escape.ManualAllocator);
            pinned.Add(Escape.ManualObjectAllocator);
            pinned.Add(Escape.ManualFreer);
            pinned.Add(Escape.ManualLive);
            // And the collector's free and liveness test, which such a
            // program's frees are retargeted from: inlined first, the calls
            // to retarget would be gone and the collector with them kept.
            pinned.Add(Escape.CollectorFreer);
            pinned.Add(Escape.CollectorLive);
            // And what a thread tells the collector, which such a program drops.
            pinned.Add(Escape.ThreadBlocking);
            pinned.Add(Escape.ThreadUnblocking);
            pinned.Add(Escape.ThreadSafePoint);
            pinned.Add(Escape.ThreadRegister);
            // And the question whether there is one, answered only then.
            pinned.Add(Escape.CollectorQuery);
            // A catch body's end, which that pass makes a free: an empty
            // routine, and inlined first there would be nothing to make one.
            pinned.Add(Escape.CatchEnder);
            // What a barrier on a replaced object becomes (ScalarObjects).
            pinned.Add(Escape.ValueBarrier);
        }
        addressTaken.UnionWith(pinned);
        _keepCalls = m.KeepCalls;

        // Bottom-up over the call graph: callees before callers, so a leaf
        // reaches its caller's caller already folded in. Functions in a
        // cycle are left in place.
        foreach (Function caller in order)
        {
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("inline-begin " + caller.Name + " instructions=" + Size(caller));
#endif
            InlineInto(caller, byName, pinned, addressTaken, callers, recursive);
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("inline-end " + caller.Name + " instructions=" + Size(caller));
#endif
        }

        RemoveDeadFunctions(m, addressTaken);
    }

    private IReadOnlySet<Instr> _keepCalls = new HashSet<Instr>();

    private readonly Dictionary<Function, bool> _storesField = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether a body stores a register into an object's field.</summary>
    private bool StoresField(Function f)
    {
        lock (_storesField)
        {
            if (_storesField.TryGetValue(f, out bool known)) return known;
        }
        bool stores = f.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.Store && i.Field is not null
            && i.Operands.Count >= 2 && i.Operands[1] is RegOperand));
        lock (_storesField) _storesField[f] = stores;
        return stores;
    }

    private sealed class Analyses
    {
        public HashSet<string> Addresses = null!;
        public Dictionary<string, int> Callers = null!;
        public HashSet<Function> Recursive = null!;
        public List<Function> Order = null!;
    }

    private static Analyses AnalyzeParallel(Module module, Dictionary<string, Function> byName, int workers)
    {
        // Keep captured cells/task allocations entirely off the serial path.
        // Each analysis writes one private result before the join publishes it.
        Analyses result = new();
        int active = Math.Min(workers, 4);
        Task[] tasks = new Task[active];
        for (int worker = 0; worker < active; worker++)
        {
            int lane = worker;
            tasks[worker] = Task.Run(() =>
            {
                for (int analysis = lane; analysis < 4; analysis += active)
                {
                    if (analysis == 0) result.Addresses = AddressTaken(module);
                    else if (analysis == 1) result.Callers = CountCallers(module);
                    else if (analysis == 2) result.Recursive = RecursiveFunctions(module, byName);
                    else result.Order = BottomUp(module, byName);
                }
            });
        }
        Task.WhenAll(tasks).Wait();
        return result;
    }

    private void InlineInto(Function caller, Dictionary<string, Function> byName,
                            HashSet<string> pinned, HashSet<string> addressTaken, Dictionary<string, int> callers,
                            HashSet<Function> recursive)
    {
        int size = Size(caller);
        bool changed = true;
        // Many rejected call sites ask the same definition/CFG questions.
        // Reuse the analysis only until Expand mutates this caller.
        Defs? callerDefs = null;
        FreshValues? callerFresh = null;

        while (changed)
        {
            changed = false;

            foreach (Block b in caller.Blocks.ToList())
            {
                for (int i = 0; i < b.Instrs.Count; i++)
                {
                    Instr call = b.Instrs[i];
                    if (call.Op != Opcode.Call || call.Callee is null || _keepCalls.Contains(call))
                    {
                        continue;
                    }
                    if (!byName.TryGetValue(call.Callee, out Function? callee) || ReferenceEquals(callee, caller))
                    {
                        continue;
                    }
                    // The collector's own notes stay calls: the escape rules
                    // know them by name, and inlined their ring store reads
                    // as the reported object escaping.
                    // A callee with a try of its own comes in only to show its
                    // caller's fresh object to the calls it makes on it
                    // (FreshDispatched) -- a List built from an iterator, a
                    // Join over one -- or everywhere, with InlineHandlers.
                    bool handlers = caller.Async is null && (InlineHandlers
                        || !Inlineable(callee, pinned) && Size(callee) <= FreshArgumentBody && FreshDispatched(caller, call, callee, ref callerFresh));
                    if (!Inlineable(callee, pinned, handlers) || recursive.Contains(callee) || Escape.IsCollectorLeaf(callee.Name))
                    {
                        continue;
                    }
                    // NOT INTO AN ASYNC FUNCTION OR AN ITERATOR, a body that
                    // stores into an object's field: whose values the owned-
                    // field rules cannot follow across a suspension, so one
                    // such store refuses the field for every object of the
                    // type in the program. A List.Add spliced into one
                    // iterator's MoveNext left every List's array to the
                    // collector. Called, the store stays in the callee.
                    if (caller.Async is not null && StoresField(callee))
                    {
                        continue;
                    }

                    int calleeSize = Size(callee);
                    int argumentWords = ArgumentWordCredit == 0 ? 0
                        : callee.Params.Sum(p => (p.Type.Bytes() + IrTypes.Word.Bytes() - 1) / IrTypes.Word.Bytes());
                    int smallBody = SmallBody + Math.Min(24, argumentWords * ArgumentWordCredit);
                    // THE HOT PATH'S COST ONLY IN A LOOP, where the call is paid
                    // every time round; anywhere else the body's whole size, or
                    // the image grew a tenth for nothing measurable.
                    int ordinaryCost = calleeSize;
                    if (calleeSize > SmallBody && InLoop(caller, b))
                    {
                        ordinaryCost = HotSize(callee);
                    }
                    if (ConditionalBranchCost != 0)
                        ordinaryCost += ConditionalBranchCost * callee.Blocks.Sum(block =>
                            block.Instrs.Count(instruction => instruction.Op is Opcode.Branch or Opcode.Switch));
                    // One caller makes a body free to move only if it then goes:
                    // one whose address is taken stays, and would be twice.
                    bool single = callers.GetValueOrDefault(callee.Name) == 1 && !addressTaken.Contains(callee.Name);
                    bool specializesBranch = calleeSize <= ConstantBranchBody
                        && ConstantControlsBranch(callee, call);
                    // The context walk builds definition/CFG information. Do
                    // not pay for it when ordinary eligibility already decides
                    // the call, or the hard growth budget will reject it.
                    bool exposesChildren = ordinaryCost > smallBody && !single && !specializesBranch
                        && size + calleeSize <= Math.Max(GrowthLimit, FreshOwnerGrowthLimit)
                        && calleeSize <= FreshOwnerBody
                        && callee.Blocks.Any(x => x.Instrs.Any(y => y.Op == Opcode.Call && Escape.IsAllocator(y.Callee)))
                        && FreshOwner(caller, b, i, call, ref callerDefs);
                    bool dispatchesFresh = ordinaryCost > smallBody && !single && !specializesBranch && !exposesChildren
                        && size + calleeSize <= GrowthLimit
                        && (calleeSize <= FreshArgumentBody && FreshDispatched(caller, call, callee, ref callerFresh)
                            || calleeSize <= FreshResultBody && call.Dest is not null && MakesWhatItReturns(callee));
                    exposesChildren |= dispatchesFresh;
                    if (ordinaryCost > smallBody && !single && !specializesBranch && !exposesChildren)
                    {
                        TraceDecision?.Invoke(caller, callee, $"keep: body={calleeSize} cost={ordinaryCost} small-limit={smallBody} caller={size} sites={callers.GetValueOrDefault(callee.Name)}");
                        continue;
                    }
                    if (size + calleeSize > GrowthLimit && !single && !exposesChildren && calleeSize > TinyBody)
                    {
                        TraceDecision?.Invoke(caller, callee, $"keep: growth caller={size} body={calleeSize} limit={GrowthLimit}");
                        continue;
                    }

                    if (Environment.GetEnvironmentVariable("CORC_INLINE_DEBUG") is { } dbg && caller.Name.Contains(dbg, StringComparison.Ordinal))
                        Console.Error.WriteLine($"inline debug: {callee.Name} into {caller.Name} async={caller.Async is not null} stores={StoresField(callee)}");
                    TraceDecision?.Invoke(caller, callee, $"expand: body={calleeSize} small-limit={smallBody} caller={size} single={single} constant-branch={specializesBranch} fresh-owner={exposesChildren}");
                    Expand(caller, b, i, call, callee);
                    callerDefs = null;
                    callerFresh = null;
                    size += calleeSize;
                    callers[callee.Name] = callers.GetValueOrDefault(callee.Name) - 1;
                    foreach (Instr inner in callee.Blocks.SelectMany(x => x.Instrs))
                    {
                        if (inner.Op == Opcode.Call && inner.Callee is not null)
                        {
                            callers[inner.Callee] = callers.GetValueOrDefault(inner.Callee) + 1;
                        }
                    }
                    changed = true;
                    break;      // the block was split; start it over
                }
                if (changed)
                {
                    break;
                }
            }
        }
    }

    // This is a profitability hint, not a value substitution: Expand still
    // preserves the entire body, then the ordinary verified passes fold it.
    // Tracking dependencies conservatively can overestimate the benefit but
    // cannot change the program's meaning. Existing recursion, EH, address-
    // taken and total-growth guards remain in force.
    internal static bool ConstantControlsBranch(Function callee, Instr call)
    {
        HashSet<VReg>? depends = null;
        for (int p = 0; p < callee.Params.Count && p < call.Operands.Count; p++)
            if (call.Operands[p] is ImmOperand)
            {
                depends ??= new HashSet<VReg>();
                depends.Add(callee.Params[p]);
            }
        if (depends is null) return false;
        bool changed;
        do
        {
            changed = false;
            foreach (Instr i in callee.Blocks.SelectMany(b => b.Instrs))
            {
                if (!i.Operands.OfType<RegOperand>().Any(r => depends.Contains(r.Reg))) continue;
                if (i.Op is Opcode.Branch or Opcode.Switch) return true;
                if (i.Dest is not null && i.Op is Opcode.Copy or Opcode.SExt32 or Opcode.ZExt32
                    or Opcode.Trunc64 or Opcode.Eq or Opcode.Ne or Opcode.LtS or Opcode.LeS
                    or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU)
                    changed |= depends.Add(i.Dest);
            }
        } while (changed);
        return false;
    }

    /// <summary>Whether a body can be moved into a caller at all.</summary>
    internal static bool Inlineable(Function callee, HashSet<string> pinned, bool handlers = false)
    {
        if (callee.Blocks.Count == 0 || pinned.Contains(callee.Name) || callee.Async is not null || callee.NoInlining)
        {
            return false;
        }

        // A NOTE TO THE COLLECTOR STAYS A CALL: the write barrier's slow path
        // and what it reports with (Escape.IsCollectorNote). Escape analysis
        // knows such a call keeps no pointer the program can use; spliced in,
        // its body is a store of the reference into the collector's log, and
        // every object whose field a barrier guarded escaped. Whether that
        // happened was the inliner's size arithmetic of the day. The fast path
        // -- the Marking test -- is already inline at every store.
        if (Escape.IsCollectorNote(callee.Name))
        {
            return false;
        }

        // A TRY IN THE CALLEE comes along whole when the caller asks for it
        // (handlers): its record is a frame slot cloned with the others, its
        // pad a block cloned and named by the cloned labeladdr, the stack and
        // frame it saves the caller's -- where the pad then runs -- and the
        // record links to whatever handler the caller has open, as a call's
        // would. A foreach over a sequence has one, for Dispose, and a List
        // built from an iterator was a call that let the iterator go.
        if (handlers) return true;
        foreach (Block b in callee.Blocks)
        {
            if (b.IsLandingPad)
            {
                return false;
            }
            foreach (Instr i in b.Instrs)
            {
                switch (i.Op)
                {
                    case Opcode.Unwind:
                    case Opcode.LabelAddr:
                    case Opcode.StackPointer:
                    case Opcode.FramePointer:
                        return false;
                    case Opcode.Call when i.Callee == "__exception":
                        return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Whether an argument the call passes is an object the caller made (an
    /// allocator's result, through copies) on which the callee makes a
    /// virtual call: the call FreshArgumentBody brings into sight.
    /// </summary>
    private static bool FreshDispatched(Function caller, Instr call, Function callee, ref FreshValues? cached)
    {
        FreshValues fresh = cached ??= new FreshValues(caller);
        for (int k = 0; k < call.Operands.Count && k < callee.Params.Count; k++)
        {
            if (call.Operands[k] is not RegOperand value || !fresh.Made(value.Reg)) continue;
            // The parameter, through the copies the callee makes of it, as
            // the receiver of a call through a vtable.
            HashSet<VReg> param = new() { callee.Params[k] };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (Block b in callee.Blocks)
                    foreach (Instr i in b.Instrs)
                        if (i.Dest is { } dest && !param.Contains(dest) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                            && i.Operands[0] is RegOperand from && param.Contains(from.Reg))
                        { param.Add(dest); grew = true; }
            }
            foreach (Block b in callee.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.CallIndirect && i.Operands.Count > 1 && i.Operands[1] is RegOperand receiver && param.Contains(receiver.Reg))
                        return true;
        }
        return false;
    }

    /// <summary>
    /// Whether every return hands back an object the function made: an
    /// allocator's result, through copies -- and through a variable every
    /// write of which is such a copy (the result an inlined body joins).
    /// </summary>
    private static bool MakesWhatItReturns(Function f)
    {
        FreshValues fresh = new(f);
        bool any = false;
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is not { Op: Opcode.Ret } ret) continue;
            if (ret.Operands.Count != 1 || ret.Operands[0] is not RegOperand back || !fresh.Made(back.Reg)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// The registers of a function that hold only objects it made: an
    /// allocator's result, through copies, and a variable every write of
    /// which is such a copy (what an inlined factory's paths join).
    /// </summary>
    private sealed class FreshValues
    {
        private readonly Dictionary<VReg, List<Instr>> _writes = new();
        private readonly Dictionary<VReg, bool> _known = new();
        public FreshValues(Function f)
        {
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d)
                    {
                        if (!_writes.TryGetValue(d, out List<Instr>? list)) _writes[d] = list = new();
                        list.Add(i);
                    }
        }
        public bool Made(VReg r) => Made(r, 0);
        private bool Made(VReg r, int depth)
        {
            if (_known.TryGetValue(r, out bool answer)) return answer;
            if (depth > 8 || !_writes.TryGetValue(r, out List<Instr>? defs)) return false;
            _known[r] = false;   // a cycle of copies is no allocation
            foreach (Instr d in defs)
            {
                bool ok = d.Op == Opcode.Call && Escape.IsAllocator(d.Callee)
                    || d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands[0] is RegOperand next && Made(next.Reg, depth + 1);
                if (!ok) return false;
            }
            _known[r] = true;
            return true;
        }
    }

    private static bool FreshOwner(Function caller, Block block, int index, Instr call, ref Defs? cached)
    {
        if (call.Operands.Count == 0) return false;
        Operand value = call.Operands[0];
        // This proof only asks for unique definition sites in the same block.
        // Constructing predecessor/successor tables here adds no information.
        Defs defs = cached ??= new Defs(caller, buildCfg: false);
        for (int depth = 0; depth < 8 && value is RegOperand r; depth++)
        {
            if (defs.Site(r.Reg) is not { } site || !ReferenceEquals(site.Block, block)
                || site.Index >= index) return false;
            Instr definition = block.Instrs[site.Index];
            if (definition.Op == Opcode.Call && Escape.IsAllocator(definition.Callee)) return true;
            if (definition.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)
                || definition.Operands.Count != 1) return false;
            value = definition.Operands[0];
            index = site.Index;
        }
        return false;
    }

    /// <summary>
    /// What a body costs where it runs, for the small-body test: its size
    /// without the collector's bookkeeping -- a store's barrier, which is a
    /// test of the marking flag and a call under it, and its card mark --
    /// and without a block that only throws or dies. Inlined where the
    /// object is the caller's frame's, the bookkeeping goes; a cold path
    /// runs once if ever. List's enumerator's MoveNext was 57 by count and
    /// 37 by this, and every foreach over a list called it.
    /// </summary>
    /// <summary>Whether a block can reach itself: it is inside a loop.</summary>
    private static bool InLoop(Function f, Block from)
    {
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new();
        foreach (Block s in from.Successors) work.Push(s);
        while (work.Count > 0)
        {
            Block b = work.Pop();
            if (ReferenceEquals(b, from)) return true;
            if (!seen.Add(b)) continue;
            foreach (Block s in b.Successors) work.Push(s);
        }
        return false;
    }

    private static int HotSize(Function f)
    {
        int n = 0;
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is { Op: Opcode.Unreachable } || b.Instrs.Any(i => i.Op == Opcode.Call && i.Callee is { } c
                    && (c.StartsWith("m_ThrowHelper_", StringComparison.Ordinal) || c == "m_Runtime_IndexOutOfRange_0")))
            {
                continue;
            }
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op == Opcode.Call && i.Callee is Corsac.Lang.Lto.RuntimeAbi.WriteBarrier or Corsac.Lang.Lto.RuntimeAbi.WriteBarrierValues
                        or Corsac.Lang.Lto.RuntimeAbi.CardMark or Corsac.Lang.Lto.RuntimeAbi.CardMarkObject)
                {
                    continue;
                }
                // The flag's read and the branch on it, and the field address
                // that only the bookkeeping takes.
                if (i.Op == Opcode.Load && i.Operands.Count > 0 && i.Operands[0] is SymOperand { Name: "s_Runtime_Marking" }) continue;
                if (k + 1 < b.Instrs.Count && i.Op == Opcode.Add && b.Instrs[k + 1] is { Op: Opcode.Call, Callee: Corsac.Lang.Lto.RuntimeAbi.WriteBarrier or Corsac.Lang.Lto.RuntimeAbi.CardMark }) continue;
                if (i.Op == Opcode.Jump && b.Instrs.Count <= 3 && b.Instrs.Any(x => x.Op == Opcode.Call && x.Callee == Corsac.Lang.Lto.RuntimeAbi.WriteBarrier)) continue;
                n++;
            }
        }
        return n;
    }

    private static int Size(Function f)
    {
        int n = 0;
        foreach (Block b in f.Blocks)
        {
            n += b.Instrs.Count;
        }
        return n;
    }

    /// <summary>
    /// Replaces the call at <paramref name="index"/> of <paramref name="site"/>
    /// with a clone of the callee's body.
    /// </summary>
    internal static void Expand(Function caller, Block site, int index, Instr call, Function callee)
    {
        Dictionary<VReg, VReg> regs = new();
        Dictionary<FrameSlot, FrameSlot> slots = new();
        Dictionary<Block, Block> blocks = new();

        VReg Reg(VReg r)
        {
            if (!regs.TryGetValue(r, out VReg? made))
            {
                made = caller.NewReg(r.Type, r.Name);
                regs[r] = made;
            }
            return made;
        }

        FrameSlot Slot(FrameSlot s)
        {
            if (!slots.TryGetValue(s, out FrameSlot? made))
            {
                made = caller.NewSlot(s.Bytes, s.Align, s.Name);
                slots[s] = made;
            }
            return made;
        }

        Operand Op(Operand o) => o switch
        {
            RegOperand r => new RegOperand(Reg(r.Reg)),
            SlotOperand s => new SlotOperand(Slot(s.Slot)),
            _ => o,
        };

        // The continuation: everything after the call, in a block of its own.
        Block cont = caller.NewBlock("inl");
        cont.Instrs.AddRange(site.Instrs.Skip(index + 1));
        site.Instrs.RemoveRange(index, site.Instrs.Count - index);

        foreach (Block b in callee.Blocks)
        {
            Block made = caller.NewBlock(b.Label + "$");
            made.IsLandingPad = b.IsLandingPad;
            blocks[b] = made;
        }

        // Arguments into the cloned parameters.
        for (int p = 0; p < callee.Params.Count; p++)
        {
            VReg param = Reg(callee.Params[p]);
            site.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = param, Operands = { call.Operands[p] } });
        }
        site.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { blocks[callee.Entry] } });

        // WHERE AN INLINED INSTRUCTION IS, for a trace: at the call, as .NET
        // reports a method its JIT inlined -- the callee is not a frame of its
        // own, and the caller's frame is at the line that called it. A line
        // of the callee's would name a place in another file than the one
        // the caller's frame gives (FrameTable names one file a function),
        // and moving between the two lines cost the table bytes at each step.

        foreach (Block b in callee.Blocks)
        {
            Block into = blocks[b];
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Ret)
                {
                    if (call.Dest is not null && i.Operands.Count > 0)
                    {
                        into.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = call.Dest, Operands = { Op(i.Operands[0]) } });
                    }
                    into.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { cont } });
                    continue;
                }

                Instr made = new()
                {
                    Op = i.Op,
                    Dest = i.Dest is null ? null : Reg(i.Dest),
                    Size = i.Size,
                    Signed = i.Signed,
                    Offset = i.Offset,
                    Callee = i.Callee,
                    DispatchType = i.DispatchType,
                    Field = i.Field,
                    Line = call.Line,
                    Default = i.Default is null ? null : blocks[i.Default],
                };
                foreach (Operand o in i.Operands)
                {
                    made.Operands.Add(Op(o));
                }
                foreach (Block t in i.Targets)
                {
                    made.Targets.Add(blocks[t]);
                }
                into.Instrs.Add(made);
            }
        }
    }

    /// <summary>Functions named as data -- vtable entries, function pointers -- stay callable by address.</summary>
    internal static HashSet<string> AddressTaken(Module m)
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            foreach (DataReloc r in d.Relocs)
            {
                taken.Add(r.Symbol);
            }
        }
        foreach (Function f in m.Functions)
        {
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    foreach (Operand o in i.Operands)
                    {
                        if (o is SymOperand s)
                        {
                            taken.Add(s.Name);
                        }
                    }
                }
            }
        }
        if (m.Entry is not null)
        {
            taken.Add(m.Entry);
        }
        return taken;
    }

    private static Dictionary<string, int> CountCallers(Module m)
    {
        Dictionary<string, int> count = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
        {
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is not null)
                    {
                        count[i.Callee] = count.GetValueOrDefault(i.Callee) + 1;
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Every function that calls itself, directly or through some chain of
    /// other functions. `Inlineable` only ever refuses a call site whose
    /// callee is the literal caller object it is being expanded into --
    /// which stops a function from being spliced into itself, but a callee
    /// that merely calls itself keeps that self-call verbatim in its
    /// cloned body. Inlined into anyone else, the clone's self-call no
    /// longer names the caller, so nothing stops it from being inlined
    /// again inside its own clone, and again inside that one, growing
    /// without bound (until the growth budget finally chokes it off). This
    /// is the set that must stay out of <see cref="Inlineable"/> instead.
    /// </summary>
    internal static HashSet<Function> RecursiveFunctions(Module m, Dictionary<string, Function> byName)
    {
        HashSet<Function> cyclic = new();
        HashSet<Function> done = new();
        HashSet<Function> onPath = new();
        List<Function> path = new();

        void Visit(Function f)
        {
            if (done.Contains(f))
            {
                return;
            }
            if (onPath.Contains(f))
            {
                // A back edge to f: f and everything called on the way to
                // this point can reach f again, so all of it is recursive.
                int at = path.IndexOf(f);
                for (int j = at; j < path.Count; j++)
                {
                    cyclic.Add(path[j]);
                }
                return;
            }

            onPath.Add(f);
            path.Add(f);
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is not null && byName.TryGetValue(i.Callee, out Function? callee))
                    {
                        Visit(callee);
                    }
                }
            }
            path.RemoveAt(path.Count - 1);
            onPath.Remove(f);
            done.Add(f);
        }

        foreach (Function f in m.Functions)
        {
            Visit(f);
        }
        return cyclic;
    }

    /// <summary>Callees before callers; members of a cycle in arbitrary order among themselves.</summary>
    private static List<Function> BottomUp(Module m, Dictionary<string, Function> byName)
    {
        List<Function> order = new();
        HashSet<Function> done = new();
        HashSet<Function> onPath = new();

        void Visit(Function f)
        {
            if (!done.Add(f))
            {
                return;
            }
            onPath.Add(f);
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is not null
                        && byName.TryGetValue(i.Callee, out Function? callee) && !onPath.Contains(callee))
                    {
                        Visit(callee);
                    }
                }
            }
            onPath.Remove(f);
            order.Add(f);
        }

        foreach (Function f in m.Functions)
        {
            Visit(f);
        }
        return order;
    }

    /// <summary>
    /// A function nothing calls or names any more is gone: inlining copied
    /// it everywhere it was wanted. Exported symbols a library must keep
    /// are not touched.
    /// </summary>
    internal static void RemoveDeadFunctions(Module m, HashSet<string> addressTaken)
    {
        // A COROUTINE'S CARD MARK IS CALLED FROM CODE NOT YET WRITTEN: the
        // suspensions become saves and calls after the optimiser has finished
        // (AsyncTransform), so while any function is one, its helper stays.
        bool coroutines = m.Functions.Any(fn => fn.Async is not null);
        bool changed = true;
        while (changed)
        {
            changed = false;
            Dictionary<string, int> callers = CountCallers(m);
            for (int i = m.Functions.Count - 1; i >= 0; i--)
            {
                Function f = m.Functions[i];
                if (f.Name == m.Entry || addressTaken.Contains(f.Name) || callers.GetValueOrDefault(f.Name) > 0
                    || (coroutines && f.Name == AsyncTransform.CardMarkObject))
                {
                    continue;
                }
                if (m.Entry is null || (m.PreserveExports && f.Exported))
                {
                    continue;       // a library: everything is an export
                }
                m.Functions.RemoveAt(i);
                changed = true;
            }
        }
    }
}
