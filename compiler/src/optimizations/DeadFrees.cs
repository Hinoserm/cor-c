#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A FREE OF NOTHING IS NO CALL. The lifetime rules free what a function made
/// at every way out of it (Escape), and leave the region it entered at every
/// way out too: an owned slot or variable is set to null, or the region to
/// none (-1), where the function starts, and a way out taken before anything
/// was made frees the null and leaves no region -- a full call each, to
/// return at once. The GUI kernel's win32k made thousands a frame on its
/// fast paths (GuiKernel.Reap: one compare and a return, then two Frees and a
/// RegionLeave).
///
/// So each value is followed along every path, across branches: a register
/// copied from a constant, a frame slot whose address goes nowhere but its
/// own loads and stores, and what is loaded from one. A free whose argument
/// is null on every path to it, and a region left that is none, is removed;
/// the loads it read become dead for DeadCodeElimination after this.
/// Each callee named below returns at once for that argument
/// (runtime.cor's Free, gc.cor's Free, Runtime.RegionLeave).
/// </summary>
public sealed class DeadFrees : IPass
{
    public string Name => "dead-frees";

    private static readonly HashSet<string> FreesOfNull = new(StringComparer.Ordinal)
    {
        Escape.Freer, Escape.CollectorFreer, Escape.StorageFreer,
    };

    public void Run(Function f)
    {
        if (!Worth(f)) return;
        // THE WAY OUT A FAST PATH SHARES: the frees sit in one exit block
        // that the paths which made something and the paths which made
        // nothing all reach. Each path in on which a free of it would be of
        // nothing gets its own copy of the (small) block, and loses the
        // call there.
        var (cfg, followed, atEnd) = Analyse(f);
        bool split = false;
        foreach (Block b in f.Blocks.ToArray())
        {
            if (b == f.Blocks[0] || b.IsLandingPad || b.Instrs.Count > SplitLimit || !b.Instrs.Any(IsCandidate)
                || b.Instrs.Any(i => i.Op == Opcode.Phi)) continue;
            Block[] preds = cfg.Preds(b).ToArray().Distinct().ToArray();
            if (preds.Length < 2) continue;
            foreach (Block from in preds)
            {
                if (from == b || atEnd[from] is not { } there) continue;
                Instr last = from.Instrs[^1];
                if (last.Default == b || !Saves(b, new(there), followed)) continue;
                Block copy = f.NewBlock(b.Label + "$nofree");
                foreach (Instr i in b.Instrs) copy.Instrs.Add(i.Clone());
                for (int t = 0; t < last.Targets.Count; t++) if (last.Targets[t] == b) last.SetTarget(t, copy);
                split = true;
            }
        }
        if (split) (cfg, followed, atEnd) = Analyse(f);

        foreach (Block b in f.Blocks)
        {
            Dictionary<object, long>? state = Entry(cfg, b, f, atEnd);
            if (state is null) continue;
            List<Instr> instrs = b.Instrs;
            int kept = 0;
            for (int k = 0; k < instrs.Count; k++)
            {
                Instr i = instrs[k];
                if (!Removable(i, state)) instrs[kept++] = i;
                Step(i, state, followed);
            }
            if (kept < instrs.Count) instrs.RemoveRange(kept, instrs.Count - kept);
        }
        GuardFrees(f, cfg);
    }

    /// <summary>
    /// A FREE OF WHAT MAY BE NULL, TESTED WHERE IT IS: what is left of the
    /// frees above may still be of nothing on the path taken -- a loop's,
    /// a merge's -- and a null is told by one test, where the call was the
    /// call and the runtime's own tests. Each becomes `branch x -> free ->
    /// on`, as the inliner splits a block at a call. Not where a successor
    /// has a phi, which names the block it is entered from.
    /// </summary>
    private static void GuardFrees(Function f, Cfg cfg)
    {
        foreach (Block start in f.Blocks.ToArray())
        {
            if (cfg.Succs(start).ToArray().Any(s => s.Instrs.Any(i => i.Op == Opcode.Phi))) continue;
            Block b = start;
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr call = b.Instrs[k];
                if (call.Op != Opcode.Call || call.Callee is not string callee || !FreesOfNull.Contains(callee)
                    || call.Dest is not null || call.Operands.Count != 1 || call.Operands[0] is not RegOperand freed) continue;
                // A branch tests a word: a reference, an nint. Gc.Free's long
                // on a 32-bit target -- the runtime's own call, in Free -- is not one.
                if (freed.Type != IrTypes.Word) continue;
                // Tested already: the block's one way in is a branch on it.
                if (k == 0 && Guarded(cfg, b, freed)) continue;
                Block freeing = f.NewBlock(b.Label + "$free");
                Block on = f.NewBlock(b.Label + "$freed");
                on.Instrs.AddRange(b.Instrs.GetRange(k + 1, b.Instrs.Count - k - 1));
                b.Instrs.RemoveRange(k, b.Instrs.Count - k);
                b.Instrs.Add(new Instr { Op = Opcode.Branch, Operands = { freed }, InitialTargets = new[] { freeing, on }, Line = call.Line });
                freeing.Instrs.Add(call);
                freeing.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { on }, Line = call.Line });
                // The rest of the block, any free after this one with it, is `on` now.
                b = on;
                k = -1;
            }
        }
    }

    /// Whether `b`'s one way in is a branch that enters it when `value` is not null.
    private static bool Guarded(Cfg cfg, Block b, RegOperand value)
    {
        Block[] preds = cfg.Preds(b).ToArray();
        if (preds.Length != 1 || preds[0].Instrs.Count == 0) return false;
        Instr last = preds[0].Instrs[^1];
        return last.Op == Opcode.Branch && last.Operands.Count == 1 && last.Operands[0] is RegOperand tested
            && tested.Reg == value.Reg && last.Targets.Count == 2 && last.Targets[0] == b && last.Targets[1] != b;
    }

    /// How large a shared way out is copied for a path: frees, a region's
    /// leave and their loads, nothing like a body.
    private const int SplitLimit = 24;

    private static bool IsCandidate(Instr i) => i.Op == Opcode.Call && i.Callee is string c && (FreesOfNull.Contains(c) || c == Lto.RuntimeAbi.RegionLeave);

    /// Whether a block entered with `state` would lose a call.
    private static bool Saves(Block b, Dictionary<object, long> state, HashSet<FrameSlot> followed)
    {
        foreach (Instr i in b.Instrs)
        {
            if (Removable(i, state)) return true;
            Step(i, state, followed);
        }
        return false;
    }

    /// What is known at each block's end, followed to a fixed point.
    private static (Cfg, HashSet<FrameSlot>, Dictionary<Block, Dictionary<object, long>?>) Analyse(Function f)
    {
        // The slots followed: a slot whose address is anything but the first
        // operand of a load or a store at its start may be written through it.
        HashSet<FrameSlot> followed = new(f.Slots);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is SlotOperand { Slot: var slot }
                        && !(k == 0 && (i.Op == Opcode.Load || i.Op == Opcode.Store) && i.Offset == 0))
                        followed.Remove(slot);

        Cfg cfg = new(f);
        // Null for a block no path has reached yet.
        Dictionary<Block, Dictionary<object, long>?> atEnd = new();
        foreach (Block b in f.Blocks) atEnd[b] = null;
        Queue<Block> work = new();
        HashSet<Block> queued = new();
        foreach (Block b in f.Blocks) { work.Enqueue(b); queued.Add(b); }
        while (work.Count > 0)
        {
            Block b = work.Dequeue();
            queued.Remove(b);
            Dictionary<object, long>? state = Entry(cfg, b, f, atEnd);
            if (state is null) continue;
            foreach (Instr i in b.Instrs) Step(i, state, followed);
            if (atEnd[b] is { } was && Same(was, state)) continue;
            atEnd[b] = state;
            foreach (Block next in cfg.Succs(b)) if (queued.Add(next)) work.Enqueue(next);
        }
        return (cfg, followed, atEnd);
    }

    /// Only a function that calls one of them is worth following.
    private static bool Worth(Function f)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (IsCandidate(i)) return true;
        return false;
    }

    private static bool Removable(Instr i, Dictionary<object, long> state)
    {
        if (i.Op != Opcode.Call || i.Callee is not string callee || i.Dest is not null || i.Operands.Count != 1) return false;
        if (!Known(i.Operands[0], state, out long value)) return false;
        if (FreesOfNull.Contains(callee)) return value == 0;
        if (callee == Lto.RuntimeAbi.RegionLeave) return value == -1;
        return false;
    }

    /// What is known where a block starts: what every path into it agrees on.
    private static Dictionary<object, long>? Entry(Cfg cfg, Block b, Function f, Dictionary<Block, Dictionary<object, long>?> atEnd)
    {
        if (b == f.Blocks[0]) return new();
        Dictionary<object, long>? met = null;
        foreach (Block from in cfg.Preds(b))
        {
            Dictionary<object, long>? there = atEnd[from];
            if (there is null) continue;
            if (met is null) { met = new(there); continue; }
            List<object>? gone = null;
            foreach ((object key, long value) in met)
                if (!there.TryGetValue(key, out long other) || other != value) (gone ??= new()).Add(key);
            if (gone is not null) foreach (object key in gone) met.Remove(key);
        }
        // A landing pad is reached by an unwind from anywhere in its try.
        if (b.IsLandingPad) return new();
        return met;
    }

    private static void Step(Instr i, Dictionary<object, long> state, HashSet<FrameSlot> followed)
    {
        if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[0] is SlotOperand { Slot: var into } && followed.Contains(into))
        {
            if (i.Offset == 0 && i.Size == into.Bytes && Known(i.StoredValue, state, out long stored)) state[into] = stored;
            else state.Remove(into);
        }
        if (i.Dest is not VReg dest) return;
        if (i.Op == Opcode.Copy && i.Operands.Count == 1 && Known(i.Operands[0], state, out long copied)) { state[dest] = copied; return; }
        // A load of a followed slot carries a null only: the one value that
        // reads the same at every width, whatever the load's sign.
        if (i.Op == Opcode.Load && i.Operands.Count == 1 && i.Offset == 0 && i.Operands[0] is SlotOperand { Slot: var from }
            && followed.Contains(from) && state.TryGetValue(from, out long held) && held == 0)
        {
            state[dest] = 0;
            return;
        }
        state.Remove(dest);
    }

    private static bool Known(Operand o, Dictionary<object, long> state, out long value)
    {
        if (o is ImmOperand imm) { value = imm.Value; return true; }
        if (o is RegOperand r && state.TryGetValue(r.Reg, out value)) return true;
        value = 0;
        return false;
    }

    private static bool Same(Dictionary<object, long> a, Dictionary<object, long> b)
    {
        if (a.Count != b.Count) return false;
        foreach ((object key, long value) in a) if (!b.TryGetValue(key, out long other) || other != value) return false;
        return true;
    }
}
