#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

// ---- a frame object's frees where each lap ends -----------------------------------------
//
// A FRAME SLOT MADE AGAIN ROUND A LOOP frees what its last occupant held just
// before it is made again (its renewal): the elements of a collection, its
// storage, its owned fields. Where the link gave that loop a region, the
// region of the last lap is given back at the top of the loop's header
// (Gc.RegionLoop, RegionPointsTo.OpenLoops) -- before the renewal, which then
// read the last occupant's fields out of memory the lap had given back and
// the next lap had already made something else in. Runtime.FreeHeld takes a
// block in its own thread's region for one that is still alive, and gave
// back from the region's top the object just made there.
//
// So the renewal's frees are placed again where each lap ends, while the
// lap's region still holds everything: on every edge back to the header and
// on every edge out of the loop from its body, the slot's first word cleared
// after them so the renewal and the returns that follow find no object. Only
// where nothing that holds the slot's object, or anything read from it, is
// live across those edges -- otherwise the renewal keeps its frees, which
// then read what the link's facts say outlives the lap.

public sealed partial class Escape
{
    /// <summary>What a renewal frees of a slot, recorded where it is placed, for the lap's end.</summary>
    internal sealed class LapFree
    {
        public required FrameSlot Slot;
        public required Instr Renew;
        public required HashSet<VReg> Roots;
        /// <summary>The freer of a collection's or an array's elements (AppendElementFree), or null.</summary>
        public string? ElementFreer;
        /// <summary>Whether the storage a collection made in the frame grows is freed (AppendStorageFree).</summary>
        public bool Storage;
        /// <summary>The owned fields freed (SlotFieldFrees), by offset and freer.</summary>
        public List<(long Offset, string Callee)> FieldFrees = new();
    }

    /// <summary>
    /// Each function's renewal frees, from the escape pass that places them to
    /// the region pass that opens the laps (or the link's per-function rules,
    /// which do both): taken there, and gone with the function.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Function, List<LapFree>> _lapFrees = new();

    /// <summary>Whether a loop of `f` has a lap region: marked by the link (MarkLoops), or opened (RegionLoop).</summary>
    internal static bool LapRegions(Function f)
        => f.Blocks.Any(b => b.RegionLoop || b.Instrs.Any(i => i.Op == Opcode.Call && i.Callee == RegionPointsTo.LoopTop));

    private static void NoteLapFree(Function f, LapFree free)
    {
        lock (_lapFrees) _lapFrees.GetOrCreateValue(f).Add(free);
    }

    /// <summary>
    /// The renewal frees noted for `f`, placed where each lap of a loop with a
    /// region (a RegionLoop call in its header) ends: on every edge back to the
    /// header and on every edge out of the loop from its body. `placed` takes
    /// what went in. Answers how many edges were given frees.
    /// </summary>
    internal static int PlaceLapFrees(Function f, HashSet<Instr>? placed = null)
    {
        List<LapFree>? noted;
        lock (_lapFrees)
        {
            if (!_lapFrees.TryGetValue(f, out noted) || noted.Count == 0) return 0;
            _lapFrees.Remove(f);
        }
        Cfg cfg = new(f);
        HashSet<Block> headers = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            if (b.Instrs.Any(i => i.Op == Opcode.Call && i.Callee == RegionPointsTo.LoopTop)) headers.Add(b);
        if (headers.Count == 0) return 0;
        var loops = RegionPointsTo.NaturalLoops(f, cfg).Where(l => headers.Contains(l.Header)).ToList();
        if (loops.Count == 0) return 0;
        Liveness liveness = new(cfg);
        List<Block> returning = f.Blocks.Where(b => b.Terminator is { Op: Opcode.Ret }).ToList();
        bool Returns(Block at) => returning.Any(r => ReferenceEquals(r, at) || cfg.Reaches(at, r));
        // An edge split for one slot's frees takes the next slot's there too.
        Dictionary<(Block, Block), Block> splits = new();
        int count = 0;
        foreach (IGrouping<FrameSlot, LapFree> bySlot in noted.GroupBy(r => r.Slot))
        {
            List<LapFree> mine = bySlot.ToList();
            Instr renew = mine[0].Renew;
            if (f.Blocks.FirstOrDefault(b => b.Instrs.Contains(renew)) is not Block renewBlock) continue;
            FrameSlot slot = bySlot.Key;
            // What holds the object, and what was read of it by reference. A
            // slot whose frees are its owned fields' alone: only what is read
            // of those fields (or through an address inside the object, whose
            // field is not known) -- a clean owned field's object is reached
            // only through it, and a name or a long total carried round the
            // loop from another field holds nothing these frees give back.
            HashSet<VReg> names = Derivations(f, mine.SelectMany(r => r.Roots));
            bool typed = mine.Any(r => r.ElementFreer is not null || r.Storage);
            HashSet<long> freedAt = mine.SelectMany(r => r.FieldFrees).Select(p => p.Offset).ToHashSet();
            HashSet<VReg> exact = new(mine.SelectMany(r => r.Roots));
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                        if (i.Dest is { } d && !exact.Contains(d) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                            && i.Operands is [RegOperand { Reg: var from }] && exact.Contains(from))
                        { exact.Add(d); grew = true; }
            }
            List<VReg> values = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Load && i.Dest is not null && !i.Number
                        && (i.Operands[0] is RegOperand { Reg: var a } && names.Contains(a) && (typed || !exact.Contains(a) || freedAt.Contains(i.Offset))
                            || i.Operands[0] is SlotOperand { Slot: var s } && s == slot && (typed || freedAt.Contains(i.Offset))))
                        values.Add(i.Dest);
            HashSet<VReg> held = Derivations(f, names.Concat(values));
            bool Dead(Block at) => !held.Any(r => !liveness.Tracks(r) || liveness.IsLiveIn(at, r));
            foreach ((Block header, HashSet<Block> body, List<Block> latches) in loops)
            {
                if (!body.Contains(renewBlock)) continue;
                // EACH EDGE ON ITS OWN: back to the header where nothing holding
                // the object is live into it, and out of the loop where nothing
                // is live into where it goes. Where something is, the renewal's
                // frees stay -- what the link's facts count as outliving the
                // lap is never in its region. A way out that never returns (a
                // trap's) needs none.
                int back = 0, outward = 0;
                if (Dead(header))
                    foreach (Block latch in latches) { PutOnEdge(f, latch, header, LapFrees(f, mine, slot, renew.Line), placed, splits); back++; }
                // From the header too where the slot is made again there: this
                // lap's object is alive where it leaves, the back edge's free
                // not yet run.
                foreach (Block x in body.ToList())
                    if (!ReferenceEquals(x, header) || ReferenceEquals(renewBlock, header))
                        foreach (Block to in cfg.Succs(x).ToArray())
                            if (!body.Contains(to) && Dead(to) && Returns(to))
                            { PutOnEdge(f, x, to, LapFrees(f, mine, slot, renew.Line), placed, splits); outward++; }
                count += back + outward;
                OwnedElements.Say(f, back == 0 && outward == 0 ? $"frame {slot.Name}: live where a lap ends, its frees left at its renewal"
                    : $"frame {slot.Name}: freed where each lap ends, {back} back, {outward} out");
            }
        }
        if (count > 0) f.Edited();
        return count;
    }

    /// <summary>The slot's frees as its renewal has them, then its first word cleared: the renewal and the returns after find no object.</summary>
    private static List<Instr> LapFrees(Function f, List<LapFree> frees, FrameSlot slot, int line)
    {
        List<Instr> made = new();
        VReg addr = f.NewReg(IrTypes.Word, "lapFreed");
        made.Add(new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = line });
        bool typed = false;
        // Each field once: the clean fields' frees and the owned fields' may both name it.
        HashSet<long> fieldsFreed = new();
        foreach (LapFree r in frees)
        {
            if (r.ElementFreer is { } freer)
            {
                VReg argument = Word(f, made, addr, line, "elementsOf");
                made.Add(new Instr { Op = Opcode.Call, Callee = freer, Operands = { RegOperand.Of(argument) }, Line = line });
                typed = true;
            }
            if (r.Storage) { AppendStorageFree(f, made, addr, line); typed = true; }
            foreach ((long o, string callee) in r.FieldFrees) if (fieldsFreed.Add(o)) AppendFieldFree(f, made, addr, o, line, callee);
        }
        if (typed)
            made.Add(new Instr { Op = Opcode.Store, Size = IrTypes.Word.Bytes(), Operands = { RegOperand.Of(addr), new ImmOperand(0, IrTypes.Word) }, Line = line });
        return made;
    }

    /// <summary>
    /// `code` on the edge from `from` to `to`: at the end of `from` where it
    /// goes nowhere else, else in a block of its own between them -- the one
    /// made for an earlier slot's, where there is one (`splits`).
    /// </summary>
    private static void PutOnEdge(Function f, Block from, Block to, List<Instr> code, HashSet<Instr>? placed, Dictionary<(Block, Block), Block> splits)
    {
        placed?.UnionWith(code);
        if (splits.TryGetValue((from, to), out Block? between))
        {
            between.Instrs.InsertRange(between.Instrs.Count - 1, code);
            return;
        }
        Instr t = from.Terminator!;
        if (t.Op == Opcode.Jump && t.Targets.Count == 1 && ReferenceEquals(t.Targets[0], to))
        {
            from.Instrs.InsertRange(from.Instrs.Count - 1, code);
            return;
        }
        Block split = f.NewBlock("lapFreed");
        splits[(from, to)] = split;
        split.Instrs.AddRange(code);
        split.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { to }, Line = t.Line });
        for (int k = 0; k < t.Targets.Count; k++)
            if (ReferenceEquals(t.Targets[k], to)) t.SetTarget(k, split);
        if (ReferenceEquals(t.Default, to)) t.Default = split;
        foreach (Instr phi in to.Instrs)
        {
            if (phi.Op != Opcode.Phi) break;
            for (int k = 0; k < phi.Targets.Count; k++)
                if (ReferenceEquals(phi.Targets[k], from)) phi.SetTarget(k, split);
        }
    }
}
