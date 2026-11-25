#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A LOOP'S TEST OF SOMETHING THE LOOP NEVER CHANGES, TAKEN OUT OF IT: the
/// branch decided once before the loop, and the loop made again for each way
/// it goes -- `for (...) { if (flag) A(); else B(); }` as `if (flag) for (...)
/// A(); else for (...) B();` -- each copy's branch now a jump, its dead arm
/// gone (BranchSimplify, DeadCodeElimination), and each lap one test and one
/// jump less. LLVM's SimpleLoopUnswitch, GCC's tree-ssa-loop-unswitch,
/// HotSpot's PhaseIdealLoop::do_unswitching.
///
/// TRIVIAL FIRST, with no copy at all (LLVM's unswitchTrivialBranch): where
/// one way of the test leaves the loop and the test is reached on the first
/// lap before anything the loop does that is seen -- the header and the
/// blocks it jumps straight on to, every instruction pure (IrInfo.IsPure) --
/// the test moves to the preheader: the way out goes straight out, the loop
/// is entered only the other way, and its branch is a jump. Nothing is made
/// twice; what the skipped instructions compute is read by nothing out there
/// (Liveness), or the move is refused.
///
/// A COPY WHERE THE LOOP IS SMALL ENOUGH for the processor's code cache: the
/// whole loop twice is code the cache holds twice, and on a 486's 8 KB a
/// loop that no longer fits beside its callers costs more than its tests
/// saved. So the budget is the instruction cache's (X86Cpu.InstructionCacheKb)
/// -- the loop's instructions at most a cache-sized share of it, none on the
/// 386 (no cache: only the trivial kind) or under --opt-size -- as GCC weighs
/// a candidate's growth against param_max_unswitch_insns and LLVM's cost
/// against its unswitch threshold. One copy a loop and at most two a
/// function a run: never the exponential growth of a loop unswitched on
/// every invariant it has (LLVM's cost multiplier, GCC's max-unswitch-depth).
///
/// LEGAL WHERE IT IS PLAIN: a natural loop with one block outside it that
/// enters it -- by a jump, or a for loop's guard, whose edge in gets a block
/// of its own for the test (Entry) -- its test a register written once, outside the loop, by
/// an instruction that dominates the preheader (the test reads the same value
/// every lap and costs nothing to ask before it); no landing pad, no async
/// body, no block the link gave a region of its own, no phi -- the IR here is
/// not SSA: a register read or written outside the loop is the one register
/// in both copies, whichever ran the one read after (Instr.Clone); one the
/// loop alone writes and reads is a fresh register in the copy (Copy).
/// </summary>
public sealed class LoopUnswitch : IPass
{
    public string Name => "loop-unswitch";

    /// <summary>Whether only the trivial kind is made (--opt-size).</summary>
    public bool OptimizeSize { get; init; }

    public void Run(Function function)
    {
        if (function.Async is not null || function.Blocks.Count < 3) return;
        foreach (Block b in function.Blocks)
        {
            if (b.IsLandingPad || b.RegionLoop) return;
            foreach (Instr i in b.Instrs) if (i.Op is Opcode.Phi or Opcode.LabelAddr) return;
        }
        int copies = 0;
        for (int round = 0; round < 4; round++)
            if (!Once(function, ref copies)) break;
    }

    // THE LOOPS ALREADY COPIED, the copy's header and the loop's own, in
    // any run of this pass over the function (the pipeline runs it each
    // round), are marked on the header (Block.Unswitched), not in a table
    // the function workers share: one copy a loop, never a loop's copy
    // copied again -- a loop with three invariant tests would otherwise be
    // eight.

    /// <summary>The copy budget in instructions: a share of the instruction cache, as LoopUnswitch's summary says, in either mode (the long-mode profile has its cache too).</summary>
    private int CopyBudget()
    {
        if (OptimizeSize) return 0;
        int kb = Target.Current.X86Profile.InstructionCacheKb;
        // About three bytes an IR instruction once selected; a loop copied is
        // allowed one part in 64 of the cache -- 8 KB: 42 instructions; 16 KB
        // and up: the cap.
        return kb == 0 ? 0 : Math.Min(64, kb * 1024 / 64 / 3);
    }

    /// <summary>One loop unswitched, if any is: true when the function changed.</summary>
    private bool Once(Function function, ref int copies)
    {
        Cfg cfg = PipelineAnalyses.CfgOf(function);
        if (cfg.Roots.Count != 1) return false;
        Defs defs = PipelineAnalyses.DefsOf(function);
        Block?[] sites = new Block?[function.RegCount];
        foreach (Block block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
                if (instruction.Dest is { } dest && dest.Id < sites.Length) sites[dest.Id] = block;
        foreach (Block header in function.Blocks.ToArray())
        {
            HashSet<Block>? loop = LoopOf(cfg, header);
            if (loop is null) continue;
            Block? preheader = null;
            int outside = 0;
            foreach (Block p in cfg.Preds(header)) if (!loop.Contains(p) && outside++ == 0) preheader = p;
            if (outside != 1 || preheader!.Terminator is not { Op: Opcode.Jump or Opcode.Branch }) continue;
            // The way in, a block of the loop's own where the one before it
            // also goes elsewhere -- a for loop's guard, `i < n` before the
            // first lap -- made when a test is found to move there (Entry).
            Block before = preheader;
            // A register written once outside the loop where the way in is
            // reached after it, or a parameter the function never writes.
            bool Invariant(Operand o) => o is RegOperand { Reg: var r } && (uint)r.Id < (uint)sites.Length
                && (sites[r.Id] is { } site ? defs.IsSingle(r) && !loop.Contains(site) && cfg.Dominates(site, before)
                                            : function.Params.Contains(r));
            // THE TESTS OF WHAT THE LOOP NEVER CHANGES, in the loop's blocks.
            foreach (Block b in function.Blocks)
            {
                if (!loop.Contains(b) || b.Terminator is not { Op: Opcode.Branch } branch || branch.Targets.Count != 2) continue;
                if (!Invariant(branch.Operands[0]) || ReferenceEquals(branch.Targets[0], branch.Targets[1])) continue;
                bool trivial = TrivialWay(cfg, loop, header, b, branch);
                int size = 0;
                foreach (Block inLoop in loop) size += inLoop.Instrs.Count;
                if (!trivial && (copies >= 2 || size > CopyBudget() || header.Unswitched)) continue;
                Block entry = Entry(function, preheader, header);
                if (trivial) Trivial(loop, header, entry, b, branch);
                else { Copy(function, loop, header, entry, b, branch); copies++; }
                return true;
            }
        }
        return false;
    }

    /// <summary>The natural loop headed by `header` -- the blocks of its back edges and all that reach them inside it -- or null where it is none.</summary>
    private static HashSet<Block>? LoopOf(Cfg cfg, Block header)
    {
        Stack<Block>? todo = null;
        foreach (Block p in cfg.Preds(header))
            if (cfg.Dominates(header, p)) (todo ??= new()).Push(p);
        if (todo is null) return null;
        HashSet<Block> loop = new() { header };
        while (todo.TryPop(out Block? block))
            if (loop.Add(block)) foreach (Block pred in cfg.Preds(block)) todo.Push(pred);
        foreach (Block b in loop) if (!cfg.Dominates(header, b)) return null;
        return loop;
    }

    /// <summary>
    /// THE WAY INTO THE LOOP AS A BLOCK OF ITS OWN: the preheader where it
    /// ends in a jump to the header; else a new block on the edge from it to
    /// the header (it is a guard's branch), which the test then ends.
    /// </summary>
    private static Block Entry(Function function, Block preheader, Block header)
    {
        if (preheader.Terminator is { Op: Opcode.Jump }) return preheader;
        Block entry = function.NewBlock(header.Label + ".pre");
        Instr jump = new() { Op = Opcode.Jump, Line = preheader.Terminator!.Line };
        jump.SetTargets(new[] { header });
        entry.Instrs.Add(jump);
        Instr guard = preheader.Terminator!;
        for (int k = 0; k < guard.TargetCount; k++)
            if (ReferenceEquals(guard.TargetAt(k), header)) guard.SetTarget(k, entry);
        return entry;
    }

    /// <summary>
    /// THE TRIVIAL KIND: `b` reached from the header along jumps alone, every
    /// instruction on the way pure, and one of the branch's ways out of the
    /// loop, where nothing the way skips is read.
    /// </summary>
    private static bool TrivialWay(Cfg cfg, HashSet<Block> loop, Block header, Block b, Instr branch)
    {
        int exitWay = !loop.Contains(branch.Targets[0]) ? 0 : !loop.Contains(branch.Targets[1]) ? 1 : -1;
        if (exitWay < 0) return false;
        Block exit = branch.Targets[exitWay];
        HashSet<VReg> written = new();
        Block at = header;
        for (int steps = 0; ; steps++)
        {
            if (steps > 8) return false;
            foreach (Instr i in at.Instrs)
            {
                if (ReferenceEquals(i, at.Terminator)) break;
                if (!IrInfo.IsPure(i)) return false;
                if (i.Dest is { } d) written.Add(d);
            }
            if (ReferenceEquals(at, b)) break;
            if (at.Terminator is not { Op: Opcode.Jump } jump || !loop.Contains(jump.Targets[0])) return false;
            at = jump.Targets[0];
        }
        if (written.Count == 0) return true;
        Liveness live = new(cfg);
        foreach (VReg r in written) if (live.IsLiveIn(exit, r)) return false;
        return true;
    }

    /// <summary>The trivial kind made: the entry tests -- out, or into the loop as it was -- and the loop's branch is a jump the way it stays.</summary>
    private static void Trivial(HashSet<Block> loop, Block header, Block entry, Block b, Instr branch)
    {
        int exitWay = !loop.Contains(branch.Targets[0]) ? 0 : 1;
        Block exit = branch.Targets[exitWay], stay = branch.Targets[1 - exitWay];
        Instr test = new() { Op = Opcode.Branch, Line = branch.Line };
        test.Operands.Add(branch.Operands[0]);
        test.SetTargets(exitWay == 0 ? new[] { exit, header } : new[] { header, exit });
        entry.Instrs[entry.Instrs.Count - 1] = test;
        Instr stays = new() { Op = Opcode.Jump, Line = branch.Line };
        stays.SetTargets(new[] { stay });
        b.Instrs[b.Instrs.Count - 1] = stays;
    }

    /// <summary>
    /// THE COPY: the loop made again, the preheader's jump a test of the
    /// branch's register -- the copy where it is true, the loop where false --
    /// and each one's branch a jump the way its test went; the loop's own
    /// registers fresh in the copy (below).
    /// </summary>
    private static void Copy(Function function, HashSet<Block> loop, Block header, Block entry, Block b, Instr branch)
    {
        // What is live into the loop, asked before the copy's blocks exist.
        Liveness live = new(function);
        Dictionary<Block, Block> copy = new(ReferenceEqualityComparer.Instance);
        // In the function's order, so the copy's layout is the loop's.
        List<Block> blocks = function.Blocks.Where(loop.Contains).ToList();
        foreach (Block original in blocks) copy[original] = function.NewBlock(original.Label + ".u");
        // THE LOOP'S OWN REGISTERS, A FRESH SET IN THE COPY (loop versioning's
        // new values, the IR here having no phis): each register every write
        // and read of which is inside the loop, and that holds nothing on the
        // way into it (not live into the header: no value carried in from an
        // earlier run of the loop, inside an outer one). Shared, an object
        // made in the loop was a register with two writes, one a copy, and
        // the lifetime rules, which take such a register for one that may
        // hold anything, freed nothing made in either (1200, 1529). What is
        // read or written outside the loop -- a counter set before it, a
        // total read after -- or carried into it stays the one register,
        // whichever copy ran.
        HashSet<VReg> parameters = new(function.Params);
        HashSet<VReg> outside = new();
        foreach (Block other in function.Blocks)
        {
            if (loop.Contains(other)) continue;
            foreach (Instr i in other.Instrs)
            {
                if (i.Dest is { } d) outside.Add(d);
                foreach (Operand o in i.Operands) if (o is RegOperand { Reg: var r }) outside.Add(r);
            }
        }
        Dictionary<VReg, VReg> fresh = new();
        foreach (Block original in blocks)
            foreach (Instr i in original.Instrs)
                if (i.Dest is { } d && !outside.Contains(d) && !parameters.Contains(d) && !fresh.ContainsKey(d) && !live.IsLiveIn(header, d))
                    fresh[d] = function.NewReg(d.Type, d.Name);
        foreach (Block original in blocks)
        {
            Block made = copy[original];
            foreach (Instr i in original.Instrs)
            {
                Instr c = i.Clone();
                if (c.Dest is { } written && fresh.TryGetValue(written, out VReg? renamed)) c.Dest = renamed;
                for (int k = 0; k < c.Operands.Count; k++)
                    if (c.Operands[k] is RegOperand { Reg: var read } && fresh.TryGetValue(read, out VReg? renamedRead)) c.Operands[k] = RegOperand.Of(renamedRead);
                for (int k = 0; k < c.TargetCount; k++)
                    if (copy.TryGetValue(c.TargetAt(k), out Block? inCopy)) c.SetTarget(k, inCopy);
                // A switch's default is an edge of its own (Block.Successors), not one of its targets.
                if (c.Default is { } fallback && copy.TryGetValue(fallback, out Block? defaultInCopy)) c.Default = defaultInCopy;
                made.Instrs.Add(c);
            }
        }
        Block whenTrue = branch.Targets[0], whenFalse = branch.Targets[1];
        // The copy runs where the test is true: its branch goes the true way.
        Block copied = copy[b];
        Instr toTrue = new() { Op = Opcode.Jump, Line = branch.Line };
        toTrue.SetTargets(new[] { copy.TryGetValue(whenTrue, out Block? t) ? t : whenTrue });
        copied.Instrs[copied.Instrs.Count - 1] = toTrue;
        // The loop runs where it is false.
        Instr toFalse = new() { Op = Opcode.Jump, Line = branch.Line };
        toFalse.SetTargets(new[] { whenFalse });
        b.Instrs[b.Instrs.Count - 1] = toFalse;
        header.Unswitched = true;
        copy[header].Unswitched = true;
        // The way in decides once.
        Instr test = new() { Op = Opcode.Branch, Line = branch.Line };
        test.Operands.Add(branch.Operands[0]);
        test.SetTargets(new[] { copy[header], header });
        entry.Instrs[entry.Instrs.Count - 1] = test;
    }
}
