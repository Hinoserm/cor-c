#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// NO SNAPSHOT BARRIER ON A STORE THAT FILLS A BLOCK JUST MADE. The barrier
/// before a reference store (Lowering.ReferenceBarrier: `Runtime.Marking`
/// tested, Runtime.WriteBarrier called while it is set) tells a concurrent
/// mark of the reference about to be overwritten; a slot of a block made
/// here and not yet written holds zero, and overwrites nothing -- lowering
/// writes a fresh object's own fields with no barrier for that reason. A
/// constructor cannot know its `this` is fresh, so it barriers every field
/// it fills; inlined after the allocation that made the object, its
/// barriers are this one's to drop. What HotSpot's C2 does for the stores
/// that initialise a just-allocated object (ReduceInitialCardMarks' sibling
/// for the pre-barrier), and Go's write-barrier pass for a store into the
/// result of runtime.newobject (IsNewObject): a barrier proved to find
/// nothing.
///
/// The card mark after the store stays: a block handed out while a
/// collection stops the threads comes out of it old (Gc.ScanStopBlocks),
/// and its card is how a later minor collection finds what it holds.
///
/// PROVED BY A FORWARD WALK, per allocation: the words of its block a
/// store may have written on some path since it was made (none at the
/// allocation, which every lap of a loop makes afresh), all of them once
/// the block's address goes anywhere this cannot follow -- a call that is
/// not a note to the collector, a store of the address, any use but a load
/// or a store through it. A barrier whose word no path has written is
/// dropped: its test of Marking becomes a jump to the store.
///
/// The inliner runs it on each caller it has expanded calls into, before
/// the caller is weighed as a callee: a factory that makes one of several
/// kinds of object (LINQ's Where and Select, each kind's constructor
/// inlined) is then the size of its stores, and small enough to be
/// inlined where its result is walked.
/// </summary>
internal static class FreshStoreBarriers
{
    /// <summary>Runtime.Marking, read before every barrier.</summary>
    private const string Marking = "s_Runtime_Marking_t$V$I32";

    /// <summary>Drops the barriers of `f` that a fresh block's unwritten word makes useless; whether any was.</summary>
    public static bool Drop(Function f)
    {
        // Barriers first: most functions have none worth the walk.
        List<(Block Head, Block Barrier, Block Stored, VReg Address)>? candidates = null;
        foreach (Block b in f.Blocks)
            if (BarrierShape(b) is var (barrier, stored, address)) (candidates ??= new()).Add((b, barrier, stored, address));
        if (candidates is null) return false;

        RegisterWrites writes = new(f);
        // Each register that is an allocation's block, at an offset into it:
        // the allocation's result, its single copies, and single additions
        // of a constant to one.
        Dictionary<VReg, (VReg Root, long Offset)> of = new();
        HashSet<VReg> roots = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i is { Op: Opcode.Call, Dest: { } made } && Escape.IsAllocator(i.Callee) && writes.TryGetValue(made, out WriteList ws) && ws.Count == 1)
                {
                    roots.Add(made);
                    of[made] = (made, 0);
                }
        if (roots.Count == 0) return false;
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { } d || of.ContainsKey(d) || !writes.TryGetValue(d, out WriteList ws) || ws.Count != 1) continue;
                    if (i.Op == Opcode.Copy && i.Operands is [RegOperand { Reg: var from }] && of.TryGetValue(from, out var at)) { of[d] = at; grew = true; }
                    else if (i.Op == Opcode.Add && i.Operands is [RegOperand { Reg: var bas }, ImmOperand { Value: var plus }] && of.TryGetValue(bas, out var on))
                    { of[d] = (on.Root, on.Offset + plus); grew = true; }
                }
        }

        // The walk. A block's state on entry: each root's words written on
        // some path (null: any may be); a root absent is one no path into
        // the block has made, or one it says nothing of -- not fresh.
        int word = IrTypes.Word.Bytes();
        Dictionary<Block, Dictionary<VReg, HashSet<long>?>> entry = new(ReferenceEqualityComparer.Instance);
        Queue<Block> work = new();
        HashSet<Block> queued = new(ReferenceEqualityComparer.Instance);
        Block first = f.Blocks[0];
        entry[first] = new();
        work.Enqueue(first);
        queued.Add(first);
        while (work.TryDequeue(out Block? b))
        {
            queued.Remove(b);
            Dictionary<VReg, HashSet<long>?> state = Copy(entry[b]);
            foreach (Instr i in b.Instrs) Step(i, state);
            foreach (Block s in b.Successors)
            {
                bool changed;
                if (!entry.TryGetValue(s, out Dictionary<VReg, HashSet<long>?>? into)) { entry[s] = Copy(state); changed = true; }
                else changed = Join(into, state);
                if (changed && queued.Add(s)) work.Enqueue(s);
            }
        }

        bool dropped = false;
        foreach ((Block head, Block barrier, Block stored, VReg address) in candidates)
        {
            if (!of.TryGetValue(address, out var at) || !entry.TryGetValue(barrier, out var before)
                || !before.TryGetValue(at.Root, out HashSet<long>? written) || written is null) continue;
            if (written.Contains(WordOf(at.Offset))) continue;
            Instr branch = head.Instrs[^1];
            head.Instrs[^1] = new Instr { Op = Opcode.Jump, InitialTargets = new[] { stored }, Line = branch.Line };
            // The test of Marking, read for this branch alone.
            if (head.Instrs.Count >= 2 && head.Instrs[^2] is { Op: Opcode.Load, Dest: { } flag } && branch.Operands is [RegOperand { Reg: var tested }] && tested == flag
                && writes.TryGetValue(flag, out WriteList fw) && fw.Count == 1 && !UsedElsewhere(f, flag, branch))
                head.Instrs.RemoveAt(head.Instrs.Count - 2);
            dropped = true;
        }
        if (dropped) Cfg.RemoveUnreachable(f);
        return dropped;

        long WordOf(long offset) => offset - ((offset % word) + word) % word;

        void Step(Instr i, Dictionary<VReg, HashSet<long>?> state)
        {
            // A NEW BLOCK, nothing of it written: every lap of a loop makes
            // its own.
            if (i is { Op: Opcode.Call, Dest: { } made } && roots.Contains(made))
            {
                state[made] = new HashSet<long>();
                foreach (Operand o in i.Operands) Escapes(o, state);
                return;
            }
            switch (i.Op)
            {
                case Opcode.Store when i.Operands.Count == 2:
                    if (i.Operands[0] is RegOperand { Reg: var into } && of.TryGetValue(into, out var at) && state.TryGetValue(at.Root, out HashSet<long>? words) && words is not null)
                        for (long w = WordOf(at.Offset + i.Offset); w < at.Offset + i.Offset + Math.Max(i.Size, 1); w += word) words.Add(w);
                    Escapes(i.Operands[1], state);
                    return;
                case Opcode.Load:
                    return;
                case Opcode.Copy or Opcode.Add when i.Dest is { } d && of.ContainsKey(d):
                    return;
                case Opcode.Call when Escape.IsCollectorNote(i.Callee):
                    return;
                case var _ when IrInfo.IsIntCompare(i.Op) || i.Op is Opcode.Branch or Opcode.Jump:
                    return;
                default:
                    foreach (Operand o in i.Operands) Escapes(o, state);
                    return;
            }
        }

        // Its address gone where the walk cannot follow: any word may be written.
        void Escapes(Operand o, Dictionary<VReg, HashSet<long>?> state)
        {
            if (o is RegOperand { Reg: var r } && of.TryGetValue(r, out var at) && state.ContainsKey(at.Root)) state[at.Root] = null;
        }
    }

    /// <summary>
    /// Lowering's barrier: a block ending `branch (load Runtime.Marking) ->B
    /// ->S`, B being `a = add x k; call Runtime.WriteBarrier a v; jump S` and
    /// reached from nowhere else. The barrier block, the store's, and `a`.
    /// </summary>
    private static (Block Barrier, Block Stored, VReg Address)? BarrierShape(Block head)
    {
        if (head.Instrs.Count < 2 || head.Instrs[^1] is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var tested }] } branch) return null;
        if (head.Instrs[^2] is not { Op: Opcode.Load, Dest: { } flag, Operands: [SymOperand { Name: Marking }] } || flag != tested) return null;
        Block barrier = branch.Targets[0], stored = branch.Targets[1];
        if (ReferenceEquals(barrier, stored) || barrier.Instrs.Count != 3) return null;
        if (barrier.Instrs[0] is not { Op: Opcode.Add, Dest: { } address, Operands: [RegOperand, ImmOperand] }
            || barrier.Instrs[1] is not { Op: Opcode.Call, Callee: Escape.Barrier, Operands: [RegOperand { Reg: var told }, _] } || told != address
            || barrier.Instrs[2] is not { Op: Opcode.Jump } jump || !ReferenceEquals(jump.Targets[0], stored)) return null;
        return (barrier, stored, address);
    }

    private static bool UsedElsewhere(Function f, VReg r, Instr except)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (ReferenceEquals(i, except)) continue;
                foreach (Operand o in i.Operands) if (o is RegOperand { Reg: var used } && used == r) return true;
            }
        return false;
    }

    private static Dictionary<VReg, HashSet<long>?> Copy(Dictionary<VReg, HashSet<long>?> state)
    {
        Dictionary<VReg, HashSet<long>?> copy = new(state.Count);
        foreach ((VReg r, HashSet<long>? words) in state) copy[r] = words is null ? null : new HashSet<long>(words);
        return copy;
    }

    /// <summary>Joins what reaches a block on another edge: a root some path says nothing of is not fresh; words written on any path are written.</summary>
    private static bool Join(Dictionary<VReg, HashSet<long>?> into, Dictionary<VReg, HashSet<long>?> from)
    {
        bool changed = false;
        foreach (VReg r in into.Keys.ToList())
        {
            HashSet<long>? mine = into[r];
            if (!from.TryGetValue(r, out HashSet<long>? theirs))
            {
                into.Remove(r);
                changed = true;
                continue;
            }
            if (mine is null) continue;
            if (theirs is null) { into[r] = null; changed = true; continue; }
            foreach (long w in theirs) if (mine.Add(w)) changed = true;
        }
        return changed;
    }
}
