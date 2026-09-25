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

    /// <summary>Optional separate growth cap for exposing child allocations.
    /// Zero keeps the ordinary growth cap; size policies can preserve this
    /// opportunity without expanding unrelated arithmetic helpers.</summary>
    public int FreshOwnerGrowthLimit { get; init; }

    /// <summary>A function may not grow past this many instructions by inlining.</summary>
    public int GrowthLimit { get; init; } = 4000;

    /// <summary>
    /// Keep the free helper alive even though nothing calls it yet. Escape
    /// analysis has not run when the first inlining round strips dead code,
    /// and it is escape analysis that inserts the calls; without this the
    /// helper would be gone before the pass that needs it. The round after
    /// escape analysis leaves this off, so a program that gained no free
    /// still carries none of it.
    /// </summary>
    public bool KeepFreeHelper { get; init; }
    public int Workers { get; set; } = 1;

    public void Run(Module m)
    {
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
        if (KeepFreeHelper)
        {
            addressTaken.Add(Escape.Freer);
        }

        // Bottom-up over the call graph: callees before callers, so a leaf
        // reaches its caller's caller already folded in. Functions in a
        // cycle are left in place.
        foreach (Function caller in order)
        {
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("inline-begin " + caller.Name + " instructions=" + Size(caller));
#endif
            InlineInto(caller, byName, addressTaken, callers, recursive);
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("inline-end " + caller.Name + " instructions=" + Size(caller));
#endif
        }

        RemoveDeadFunctions(m, addressTaken);
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
                            HashSet<string> addressTaken, Dictionary<string, int> callers,
                            HashSet<Function> recursive)
    {
        int size = Size(caller);
        bool changed = true;
        // Many rejected call sites ask the same definition/CFG questions.
        // Reuse the analysis only until Expand mutates this caller.
        Defs? callerDefs = null;

        while (changed)
        {
            changed = false;

            foreach (Block b in caller.Blocks.ToList())
            {
                for (int i = 0; i < b.Instrs.Count; i++)
                {
                    Instr call = b.Instrs[i];
                    if (call.Op != Opcode.Call || call.Callee is null)
                    {
                        continue;
                    }
                    if (!byName.TryGetValue(call.Callee, out Function? callee) || ReferenceEquals(callee, caller))
                    {
                        continue;
                    }
                    if (!Inlineable(callee, addressTaken) || recursive.Contains(callee))
                    {
                        continue;
                    }

                    int calleeSize = Size(callee);
                    int argumentWords = ArgumentWordCredit == 0 ? 0
                        : callee.Params.Sum(p => (p.Type.Bytes() + IrTypes.Word.Bytes() - 1) / IrTypes.Word.Bytes());
                    int smallBody = SmallBody + Math.Min(24, argumentWords * ArgumentWordCredit);
                    int ordinaryCost = calleeSize;
                    if (ConditionalBranchCost != 0)
                        ordinaryCost += ConditionalBranchCost * callee.Blocks.Sum(block =>
                            block.Instrs.Count(instruction => instruction.Op is Opcode.Branch or Opcode.Switch));
                    bool single = callers.GetValueOrDefault(callee.Name) == 1;
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
                    if (ordinaryCost > smallBody && !single && !specializesBranch && !exposesChildren)
                    {
                        TraceDecision?.Invoke(caller, callee, $"keep: body={calleeSize} cost={ordinaryCost} small-limit={smallBody} caller={size} sites={callers.GetValueOrDefault(callee.Name)}");
                        continue;
                    }
                    if (size + calleeSize > GrowthLimit && !single && !exposesChildren)
                    {
                        TraceDecision?.Invoke(caller, callee, $"keep: growth caller={size} body={calleeSize} limit={GrowthLimit}");
                        continue;
                    }

                    TraceDecision?.Invoke(caller, callee, $"expand: body={calleeSize} small-limit={smallBody} caller={size} single={single} constant-branch={specializesBranch} fresh-owner={exposesChildren}");
                    Expand(caller, b, i, call, callee);
                    callerDefs = null;
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
    internal static bool Inlineable(Function callee, HashSet<string> addressTaken)
    {
        if (callee.Blocks.Count == 0 || addressTaken.Contains(callee.Name) || callee.Async is not null)
        {
            return false;
        }

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
            blocks[b] = caller.NewBlock(b.Label + "$");
        }

        // Arguments into the cloned parameters.
        for (int p = 0; p < callee.Params.Count; p++)
        {
            VReg param = Reg(callee.Params[p]);
            site.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = param, Operands = { call.Operands[p] } });
        }
        site.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { blocks[callee.Entry] } });

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
                    Line = i.Line,
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
    private static void RemoveDeadFunctions(Module m, HashSet<string> addressTaken)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            Dictionary<string, int> callers = CountCallers(m);
            for (int i = m.Functions.Count - 1; i >= 0; i--)
            {
                Function f = m.Functions[i];
                if (f.Name == m.Entry || addressTaken.Contains(f.Name) || callers.GetValueOrDefault(f.Name) > 0)
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
