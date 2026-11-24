#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A FIELD A LOOP READS AND NEVER WRITES, READ ONCE BEFORE IT; an array's
/// length with it. `for (k = 0; k < _bits.Length; k++) _bits[k] |= o._bits[k]`
/// read `this._bits`, its length and `o._bits` every lap, and checked
/// `o._bits`'s bounds against a length read again each time: a store into a
/// ulong[] element cannot write an object's field, and an array's length
/// never changes once it is made.
///
/// WHAT A LOOP WRITES, BY KIND (Summary), as RyuJIT's loop side effects keep
/// the fields and array element kinds a loop writes apart
/// (optComputeLoopSideEffects) and HotSpot C2 keeps a memory slice a field:
/// a class's field written at an offset kills a read of a field over the
/// same bytes; an array's element (its base an array whose length this
/// function asks, LoadReuse.ArrayBased) kills no field; a static's own cell
/// none either. Anything else the loop does that is not pure -- a call, a
/// block copy, a fence (a volatile access's too), an atomic, a store this
/// cannot place -- may write anything, and nothing is read before the loop.
///
/// READ WHERE THE LOOP WOULD HAVE READ IT FIRST, or not at all: a field read
/// through a register faults on a null one, and that fault is the
/// program's. So a read moves only from the start of the loop's first
/// block, with nothing before it there but computing and other reads --
/// nothing the program sees happens between where it was and where it goes,
/// and an earlier read that would have faulted faults as a null one
/// too (RyuJIT hoists what may throw only from the loop's first block,
/// before any other effect; LLVM's isGuaranteedToExecute). Where the first
/// block is the loop's test alone and the read is in the block the test
/// enters, the test is asked once before the loop as well (Guard): the
/// block entered then gets a way in of its own, run only when the first lap
/// is -- the guarded preheader a rotated loop has (LLVM's loop-rotate before
/// LICM, RyuJIT's optInvertWhileLoop) -- and the read moves there.
///
/// NOT ACROSS A HANDLER: a fault moved out of a try would be caught by
/// another one, so a function with a landing pad is left as it is, as
/// LoopUnswitch leaves it; nor an async body, nor a loop the link made a
/// region of. C# lets a read of a field no volatile names be made once
/// (ECMA-335 I.12.6.4); a volatile one is fenced (CompilerFence), and the
/// fence is the loop's writing anything.
/// </summary>
public sealed class LoopLoads : IPass
{
    public string Name => "loop-loads";

    /// <summary>The null fault covers the first page (NullCheckFold.Covered): a field read past it is not moved.</summary>
    private const long Covered = 4096;

    /// <summary>Values a loop holds in registers for its reads: few on 32-bit x86, where a register held round the loop is one its body lacks.</summary>
    private static int Budget => Target.Current.WordSize == 8 ? 8 : 4;

    /// <summary>
    /// THE READS EACH LOOP'S WAY IN HOLDS ALREADY, by the block they were
    /// moved to, over every round of this pass and every run of it the
    /// pipeline makes: the budget is a loop's, not a round's (a header of
    /// twelve reads was moved four at a time, three rounds).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Block, System.Runtime.CompilerServices.StrongBox<int>> Held = new();

    private static System.Runtime.CompilerServices.StrongBox<int> HeldBy(Block way) => Held.GetValue(way, _ => new(0));

    public void Run(Function function)
    {
        if (function.Async is not null || function.Blocks.Count < 2) return;
        // NO LOOP, NO TABLES: a block jumping back to one before it in
        // the function's order is the cheap sign of one (a loop the order
        // does not show is found by the Cfg the next time round).
        bool back = false;
        for (int k = 0; k < function.Blocks.Count; k++) function.Blocks[k].Order = k;
        foreach (Block b in function.Blocks)
        {
            if (b.IsLandingPad || b.RegionLoop) return;
            foreach (Instr i in b.Instrs)
            {
                if (i.Op is Opcode.Phi or Opcode.LabelAddr) return;
                if (i.IsTerminator) for (int t = 0; t < i.TargetCount; t++) back |= i.TargetAt(t).Order <= b.Order;
            }
        }
        if (!back) return;
        for (int round = 0; round < 8; round++)
            if (!Once(function)) break;
    }

    /// <summary>One loop's reads moved, or one guard made: true when the function changed.</summary>
    private static bool Once(Function function)
    {
        Cfg cfg = PipelineAnalyses.CfgOf(function);
        if (cfg.Roots.Count != 1) return false;
        Defs defs = PipelineAnalyses.DefsOf(function);
        Block?[] sites = new Block?[function.RegCount];
        foreach (Block block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
                if (instruction.Dest is { } dest && dest.Id < sites.Length) sites[dest.Id] = block;
        HashSet<VReg> arrays = new();
        foreach (Block b in function.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.ArrayLength && i.Operands is [RegOperand { Reg: var array }]) arrays.Add(array);

        // Innermost first: a read moved out of an inner loop lands in its
        // way in, which the outer loop's next round may move again.
        List<(Block Header, HashSet<Block> Loop)> loops = new();
        foreach (Block header in function.Blocks)
            if (LoopOf(cfg, header) is { } loop) loops.Add((header, loop));
        loops.Sort((a, b) => a.Loop.Count.CompareTo(b.Loop.Count));

        foreach ((Block header, HashSet<Block> loop) in loops)
        {
            Block? preheader = null;
            int outside = 0;
            foreach (Block p in cfg.Preds(header)) if (!loop.Contains(p) && outside++ == 0) preheader = p;
            if (outside != 1 || preheader!.Terminator is not { Op: Opcode.Jump } || cfg.Succs(preheader).Count != 1) continue;
            Summary? summary = Summarise(loop, defs, arrays);
            if (summary is null) continue;

            // Written once, before the loop, where its way in is reached after it.
            bool Invariant(VReg r) => (uint)r.Id >= (uint)sites.Length
                || defs.IsSingle(r) && (sites[r.Id] is not { } site || !loop.Contains(site) && cfg.Dominates(site, preheader));

            if (Hoist(header, preheader, summary, Invariant, defs, sites)) return true;
            if (Guard(function, cfg, loop, header, preheader, summary, Invariant, defs)) return true;
        }
        return false;
    }

    /// <summary>
    /// The reads at the start of `first` that `summary` leaves alone, their
    /// address read before the loop, moved to the end of `into`: true when
    /// one was.
    /// </summary>
    private static bool Hoist(Block first, Block into, Summary summary, Func<VReg, bool> invariant, Defs defs, Block?[] sites)
    {
        System.Runtime.CompilerServices.StrongBox<int> held = HeldBy(into);
        bool moved = false;
        for (int k = 0; k < first.Instrs.Count && held.Value < Budget; k++)
        {
            Instr i = first.Instrs[k];
            if (ReferenceEquals(i, first.Terminator)) break;
            if (Movable(i, summary, invariant, defs))
            {
                first.Instrs.RemoveAt(k--);
                into.Instrs.Insert(into.Instrs.Count - 1, i);
                if (i.Dest!.Id < sites.Length) sites[i.Dest.Id] = into;
                held.Value++;
                moved = true;
                continue;
            }
            if (!Quiet(i)) break;
        }
        return moved;
    }

    /// <summary>A read of a class's field or an array's length, its address one the loop never changes, what it reads one the loop never writes.</summary>
    private static bool Movable(Instr i, Summary summary, Func<VReg, bool> invariant, Defs defs)
    {
        if (i.Dest is not { } dest || !defs.IsSingle(dest) || i.Operands is not [RegOperand { Reg: var address }] || !invariant(address)) return false;
        if (i.Op == Opcode.ArrayLength) return true;
        return i.Op == Opcode.Load && !i.Device && i.Family is not null && i.Offset is >= 0 && i.Offset + i.Size <= Covered
            && dest.Type.IsInt() && !Interior(address, defs) && !summary.Writes(i.Offset, i.Size);
    }

    /// <summary>
    /// AN ADDRESS INSIDE AN OBJECT, made by adding to another: a struct's
    /// field reached through it carries a Family too (Lowering's field
    /// access), at the struct's own offset, which a field's offset from the
    /// object's start is never compared with. Neither such a read is moved
    /// nor such a write placed (Summarise): an object's own address is
    /// never a sum.
    /// </summary>
    private static bool Interior(VReg address, Defs defs)
        => !defs.IsSingle(address) || defs.Site(address) is { } site && site.Block.Instrs[site.Index].Op is Opcode.Add or Opcode.Sub;

    /// <summary>Nothing the program sees, and no fault but a read's: computing, and reads of memory -- never a device's, whose every read is seen.</summary>
    private static bool Quiet(Instr i)
        => !i.Device && (IrInfo.IsPure(i) && i.Op is not (Opcode.Phi or Opcode.LabelAddr) || i.Op is Opcode.ArrayLength || i.Op == Opcode.Load);

    /// <summary>
    /// THE LOOP'S TEST ASKED ONCE BEFORE IT TOO, where the header is that test
    /// alone -- computing, no read of memory at all, a branch out of the loop one way and into a
    /// block only it enters the other -- and that block starts with a read
    /// Hoist would move: the way in tests and goes out, or into a new block
    /// of its own that jumps to that block, which is then the loop's first.
    /// Not SSA, so the test's copy writes the registers the header writes,
    /// the values the header would have given them on the first lap.
    /// </summary>
    private static bool Guard(Function function, Cfg cfg, HashSet<Block> loop, Block header, Block preheader, Summary summary, Func<VReg, bool> invariant, Defs defs)
    {
        if (header.Terminator is not { Op: Opcode.Branch } branch || branch.TargetCount != 2 || header.Instrs.Count > 9) return false;
        int stay = loop.Contains(branch.TargetAt(0)) ? 0 : 1;
        Block body = branch.TargetAt(stay), exit = branch.TargetAt(1 - stay);
        if (loop.Contains(exit) || !loop.Contains(body) || ReferenceEquals(body, header) || cfg.Preds(body).Count != 1) return false;
        foreach (Instr i in header.Instrs)
            if (!ReferenceEquals(i, branch) && !(IrInfo.IsPure(i) && !i.Device && i.Op is not (Opcode.Phi or Opcode.LabelAddr or Opcode.Load))) return false;
        bool any = false;
        foreach (Instr i in body.Instrs)
        {
            if (ReferenceEquals(i, body.Terminator)) break;
            if (Movable(i, summary, invariant, defs)) { any = true; break; }
            if (!Quiet(i)) break;
        }
        if (!any) return false;
        Block way = function.NewBlock(body.Label + ".in");
        // One budget for the loop: what its first way in holds already counts.
        HeldBy(way).Value = HeldBy(preheader).Value;
        Instr jump = new() { Op = Opcode.Jump, Line = branch.Line };
        jump.SetTargets(new[] { body });
        way.Instrs.Add(jump);
        preheader.Instrs.RemoveAt(preheader.Instrs.Count - 1);
        foreach (Instr i in header.Instrs)
        {
            Instr copy = i.Clone();
            if (ReferenceEquals(i, branch)) copy.SetTarget(stay, way);
            preheader.Instrs.Add(copy);
        }
        return true;
    }

    /// <summary>
    /// What a loop writes: the bytes of class fields stored into, and whether
    /// it may write anything at all (null).
    /// </summary>
    private static Summary? Summarise(HashSet<Block> loop, Defs defs, HashSet<VReg> arrays)
    {
        Summary summary = new();
        foreach (Block b in loop)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Store)
                {
                    if (i.Device || i.Operands.Count == 0) return null;
                    switch (i.Operands[0])
                    {
                        case SymOperand { Name: var cell } when cell.StartsWith("s_", StringComparison.Ordinal) || cell.StartsWith("ts_", StringComparison.Ordinal):
                            break;
                        case RegOperand { Reg: var at } when i.Family is not null && !Interior(at, defs):
                            summary.Fields.Add((i.Offset, i.Size));
                            break;
                        case RegOperand { Reg: var r } when ArrayBased(r, defs, arrays, 6):
                            break;
                        default:
                            return null;
                    }
                    continue;
                }
                if (i.Op is Opcode.Load or Opcode.ArrayLength or Opcode.Jump or Opcode.Branch or Opcode.Switch) continue;
                if (i.Op is Opcode.Phi or Opcode.LabelAddr || !IrInfo.IsPure(i)) return null;
            }
        return summary;
    }

    /// <summary>An address within an array this function asks the length of, through adds and copies (LoadReuse's rule).</summary>
    private static bool ArrayBased(VReg r, Defs defs, HashSet<VReg> arrays, int depth)
    {
        if (arrays.Contains(r)) return true;
        if (depth == 0 || !defs.IsSingle(r) || defs.Site(r) is not { } site) return false;
        Instr d = site.Block.Instrs[site.Index];
        if (d.Op is not (Opcode.Add or Opcode.Copy)) return false;
        foreach (Operand o in d.Operands)
            if (o is RegOperand { Reg: var from } && ArrayBased(from, defs, arrays, depth - 1)) return true;
        return false;
    }

    private sealed class Summary
    {
        public readonly List<(long Offset, int Size)> Fields = new();

        /// <summary>Whether a field over these bytes is stored into.</summary>
        public bool Writes(long offset, int size)
        {
            foreach ((long at, int bytes) in Fields)
                if (at < offset + size && offset < at + bytes) return true;
            return false;
        }
    }

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
}
