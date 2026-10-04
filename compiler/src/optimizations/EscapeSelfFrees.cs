#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

// ---- storage a collection frees itself ------------------------------------------------
//
// A COLLECTION FREES THE STORAGE IT REPLACES (OutgrownStorage.Release): Grow
// reads the array a field holds, makes a bigger one, stores it, and gives the
// old one back; FreeStorage gives the array back and clears the field. Read
// as any other free that was there before the pass ran, that free was an
// escape of the value read, and every field a List, a Dictionary, a HashSet,
// a Queue or a StringBuilder keeps its storage in was refused: never freed on
// replacement by the compiler, never freed with its owner by the owned-field
// map, the arrays left behind by every growth the collector's.
//
// THE IDIOM, recognised here, a SELF-REPLACING FREE: `Runtime.Free(v)` that
// this run did not insert, where v is a read of `o.F` and a store `o.F = w`
// into the same object either
//   - comes before the free on every way from the read to it (the value
//     freed is the one the store replaced: Grow), or
//   - follows the free on every way on from it, with nothing between that
//     could reach the field (the field is cleared as its value goes:
//     FreeStorage).
// Between the read and the store nothing else stores into the field or calls
// what might; the free is reached once for each read; and no handler can
// reach it without the read.
//
// SUCH A FREE IS THE ONE FREE OF WHAT IT GIVES BACK, provided the compiler
// adds no other: OwnedFields and ApplyOwnedFields put no free at a store into
// the field in a function that frees it itself, and watch no such field
// around a call (FreeReplacedAcrossCalls). Then each value the field holds is
// freed once: by the store that replaces it -- the compiler's free where the
// store is not self-freeing, the collection's own where it is -- or where the
// owner dies, by the owned-field map, which finds a field FreeStorage cleared
// empty. Leaving the compiler's free out where a collection does not free
// (a lent List, a null array) leaks, which is the collector's again; it never
// frees twice. The read the free takes is judged as any read of an owned
// field -- the free a consumer of it, and nothing of it used after -- and
// for every other read the free is a danger, as any free is, unless the
// value read is the same object's other field: that field's value is its
// own, never inside what this free gives back.

public sealed partial class Escape
{
    /// <summary>The self-replacing frees of one function: each free, with the read it gives back and its store.</summary>
    private sealed class SelfFrees
    {
        /// <summary>Each free, by the free: the read of the field it gives back, and the store that replaces or clears it.</summary>
        public readonly Dictionary<Instr, (Instr Load, Instr Store)> Pairs = new(ReferenceEqualityComparer.Instance);
        /// <summary>The frees, as consumers of what they give back (Analyse).</summary>
        public readonly HashSet<Instr> Frees = new(ReferenceEqualityComparer.Instance);
        /// <summary>The stores, which free nothing of their own.</summary>
        public readonly HashSet<Instr> Stores = new(ReferenceEqualityComparer.Instance);
        /// <summary>
        /// Every field some free here gives back a read of, paired or not: a
        /// store into one of these here gets no free from the compiler.
        /// </summary>
        public readonly HashSet<string> FreedFields = new(StringComparer.Ordinal);

        public static readonly SelfFrees None = new();
    }

    /// <summary>Where a walk over the instructions stands (SelfFreeOf).</summary>
    private enum FreeWalk { BeforeStore, AfterStore, AfterFree, BeforeFree, Clearing }

    /// <summary>
    /// The self-replacing frees of <paramref name="f"/> (the idiom above);
    /// <paramref name="inserted"/> are this run's own frees, which are none.
    /// With <paramref name="pair"/> false, only the fields freed.
    /// </summary>
    private static SelfFrees SelfFreesOf(Function f, HashSet<Instr>? inserted, bool pair = true)
    {
        SelfFrees? found = null;
        Dictionary<VReg, Instr>? defs = null;
        HashSet<VReg>? written = null;
        // Where a block's address is taken, control may arrive from where
        // no edge says; such a function's frees are judged as before.
        bool addressed = f.Blocks.Any(x => x.Instrs.Any(i => i.Op == Opcode.LabelAddr));
        foreach (Block b in f.Blocks)
            foreach (Instr free in b.Instrs)
            {
                if (free.Op != Opcode.Call || free.Callee != Freer || inserted?.Contains(free) == true
                    || free.Operands.Count != 1 || free.Operands[0] is not RegOperand freed) continue;
                defs ??= SingleDefs(f);
                if (OriginOf(defs, freed.Reg) is not { Op: Opcode.Load, Field: string field, Dest: { } value } load
                    || load.Operands.Count < 1 || load.Operands[0] is not RegOperand { Reg: var baseReg } || !defs.ContainsKey(value)) continue;
                found ??= new SelfFrees();
                found.FreedFields.Add(field);
                if (addressed || !pair) continue;
                written ??= Written(f);
                if (StableRoot(f, defs, written, baseReg) is not VReg owner) continue;
                Instr? store = SelfFreeOf(f, defs, written, load, free, owner);
                if (store is null) continue;
                found.Pairs[free] = (load, store);
                found.Frees.Add(free);
                found.Stores.Add(store);
            }
        return found ?? SelfFrees.None;
    }

    /// <summary>Every register some instruction writes.</summary>
    private static HashSet<VReg> Written(Function f)
    {
        HashSet<VReg> written = new();
        foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (i.Dest is not null) written.Add(i.Dest);
        return written;
    }

    /// <summary>
    /// What a register holds the object of, through copies: a register
    /// written once, or a parameter never written; null for anything else.
    /// </summary>
    private static VReg? StableRoot(Function f, Dictionary<VReg, Instr> defs, HashSet<VReg> written, VReg r)
    {
        for (int hop = 0; hop < 8; hop++)
        {
            if (f.Params.Contains(r)) return written.Contains(r) ? null : r;
            if (!defs.TryGetValue(r, out Instr? d)) return null;
            if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands is [RegOperand from]) { r = from.Reg; continue; }
            return r;
        }
        return null;
    }

    /// <summary>
    /// The store that makes <paramref name="free"/> of what <paramref name="load"/>
    /// read a self-replacing free -- before it on every way there, or after it
    /// on every way on -- or null.
    /// </summary>
    private static Instr? SelfFreeOf(Function f, Dictionary<VReg, Instr> defs, HashSet<VReg> written, Instr load, Instr free, VReg owner)
    {
        string field = load.Field!;
        Instr? Into(Instr i) => i.Op == Opcode.Store && i.Field == field && i.Offset == load.Offset && i.Operands.Count >= 2
            && i.Operands[0] is RegOperand { Reg: var into } && StableRoot(f, defs, written, into) == owner ? i : null;
        List<Instr> stores = new();
        foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (Into(i) is Instr st) stores.Add(st);
        if (stores.Count == 0) return null;
        // ANOTHER FIELD OF THE SAME OBJECT GIVEN BACK BESIDE THIS ONE, as a
        // table frees its arrays together: never the object itself, nor this
        // field's value.
        bool Peer(Instr call) => call.Operands is [RegOperand { Reg: var other }]
            && OriginOf(defs, other) is { Op: Opcode.Load, Field: string of, Operands: [RegOperand { Reg: var from }, ..] }
            && of != field && StableRoot(f, defs, written, from) == owner;
        // No handler may reach the free without the read: an exception
        // thrown between the read and the store, caught, and run on to the
        // free would give back what the field still holds.
        foreach (Block root in Roots(f))
            if (!Walk(f, root, 0, FreeWalk.BeforeFree, (i, state, throws) => ReferenceEquals(i, load) ? Step.Stop : ReferenceEquals(i, free) ? Step.Fail : Step.Go))
                return null;
        (Block at, int index) = Where(f, load);
        foreach (Instr store in stores)
        {
            // REPLACED, THEN FREED: on every way from the read to the free the
            // store comes first, and nothing else may write the field before
            // it; once freed, nothing reaches the free again but by the read.
            bool replaced = Walk(f, at, index + 1, FreeWalk.BeforeStore, (i, state, throws) =>
            {
                if (ReferenceEquals(i, load)) return Step.Stop;
                switch (state)
                {
                    case FreeWalk.BeforeStore:
                        if (ReferenceEquals(i, store)) return Step.To(FreeWalk.AfterStore);
                        if (ReferenceEquals(i, free)) return Step.Fail;
                        return throws || Quiet(i, field, defs, null) ? Step.Go : Step.Fail;
                    case FreeWalk.AfterStore:
                        return ReferenceEquals(i, free) ? Step.To(FreeWalk.AfterFree) : Step.Go;
                    default:
                        return ReferenceEquals(i, free) ? Step.Fail : Step.Go;
                }
            });
            if (replaced) return store;
            // FREED, THEN CLEARED: nothing writes the field between the read
            // and the free; and from the free on, every way reaches the store
            // before anything reads the field, writes it, calls out, or leaves.
            // A way that clears the field without the free leaks, no worse --
            // or frees after it, the value it replaced. Either way the free
            // is not reached again but by the read.
            bool cleared = Walk(f, at, index + 1, FreeWalk.BeforeFree, (i, state, throws) =>
            {
                if (ReferenceEquals(i, load)) return state == FreeWalk.Clearing ? Step.Fail : Step.Stop;
                switch (state)
                {
                    case FreeWalk.BeforeFree:
                        if (ReferenceEquals(i, store)) return Step.To(FreeWalk.AfterStore);
                        if (ReferenceEquals(i, free)) return Step.To(FreeWalk.Clearing);
                        return throws || Quiet(i, field, defs, Peer) ? Step.Go : Step.Fail;
                    case FreeWalk.Clearing:
                        if (ReferenceEquals(i, store)) return Step.To(FreeWalk.AfterFree);
                        if (ReferenceEquals(i, free) || throws || i.Op is Opcode.Ret or Opcode.Unwind or Opcode.Unreachable or Opcode.Trap
                            || i.Op == Opcode.Load && i.Field == field) return Step.Fail;
                        return Quiet(i, field, defs, Peer) ? Step.Go : Step.Fail;
                    case FreeWalk.AfterStore:
                        return ReferenceEquals(i, free) ? Step.To(FreeWalk.AfterFree) : Step.Go;
                    default:
                        return ReferenceEquals(i, free) ? Step.Fail : Step.Go;
                }
            });
            if (cleared) return store;
        }
        return null;
    }

    /// <summary>
    /// Whether an instruction between a read of a field and the store that
    /// pairs with its free leaves the field alone: no store into it; no copy
    /// or fill of memory but into an object just made; nothing atomic; no
    /// call but the collector's notes, an allocation, or an explicit free
    /// <paramref name="peer"/> accepts -- a collection giving back its other
    /// arrays beside this one.
    /// </summary>
    private static bool Quiet(Instr i, string field, Dictionary<VReg, Instr> defs, Func<Instr, bool>? peer)
    {
        if (i.Op == Opcode.Store) return i.Field != field;
        if (i.Op is Opcode.MemCopy or Opcode.MemSet)
        {
            if (i.Operands.Count == 0 || i.Operands[0] is not RegOperand { Reg: var into }) return false;
            Instr? origin = OriginOf(defs, into);
            // Into its body, an offset from where it starts.
            if (origin is { Op: Opcode.Add, Operands: [RegOperand { Reg: var start }, _] }) origin = OriginOf(defs, start);
            return origin is { Op: Opcode.Call } made && IsAllocator(made.Callee);
        }
        if (i.Op is Opcode.CallIndirect or Opcode.Syscall or Opcode.AtomicSwap or Opcode.AtomicAdd or Opcode.AtomicAnd or Opcode.AtomicOr
            or Opcode.AtomicXor or Opcode.AtomicCas) return false;
        if (i.Op != Opcode.Call) return true;
        return i.Callee is not null && (IsCollectorNote(i.Callee) || NeverWritesFields(i.Callee) || peer is not null && i.Callee == Freer && peer(i));
    }

    /// <summary>What a walk does at an instruction: go on, stop this way here (fine), fail the whole walk, or go on in another state.</summary>
    private readonly record struct Step(int Kind, FreeWalk State)
    {
        public static readonly Step Go = new(0, default), Stop = new(1, default), Fail = new(2, default);
        public static Step To(FreeWalk state) => new(3, state);
    }

    /// <summary>
    /// Every way on from instruction <paramref name="index"/> of <paramref name="from"/>,
    /// each instruction judged in the state the way reached it with; false
    /// if any judges Fail. A way ends at a Stop, or where the function does.
    /// A call followed by a trap or `unreachable` -- a throw's helper, judged
    /// as one that throws -- ends its way too: what it throws to is a
    /// handler, walked from its own start.
    /// </summary>
    private static bool Walk(Function f, Block from, int index, FreeWalk state, Func<Instr, FreeWalk, bool, Step> judge)
    {
        HashSet<(Block, FreeWalk)> seen = new();
        Stack<(Block, int, FreeWalk)> work = new();
        work.Push((from, index, state));
        while (work.TryPop(out var at))
        {
            (Block b, int k, FreeWalk s) = at;
            bool ended = false;
            for (; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                bool throws = i.Op == Opcode.Call && k + 1 < b.Instrs.Count && b.Instrs[k + 1].Op is Opcode.Trap or Opcode.Unreachable;
                Step step = judge(i, s, throws);
                if (step.Kind == 2) return false;
                if (step.Kind == 1) { ended = true; break; }
                if (step.Kind == 3) s = step.State;
                if (throws || i.Op is Opcode.Ret or Opcode.Unwind or Opcode.Unreachable or Opcode.Trap) { ended = true; break; }
            }
            if (ended) continue;
            foreach (Block next in b.Successors)
                if (seen.Add((next, s))) work.Push((next, 0, s));
        }
        return true;
    }

    /// <summary>Where control can start: the entry and every landing pad.</summary>
    private static IEnumerable<Block> Roots(Function f)
    {
        yield return f.Entry;
        foreach (Block b in f.Blocks)
            if (b.IsLandingPad && !ReferenceEquals(b, f.Entry)) yield return b;
    }

    /// <summary>The block and position of an instruction.</summary>
    private static (Block, int) Where(Function f, Instr i)
    {
        foreach (Block b in f.Blocks)
        {
            int k = b.Instrs.IndexOf(i);
            if (k >= 0) return (b, k);
        }
        throw new InvalidOperationException("instruction not in its function");
    }

    /// <summary>
    /// A SELF-REPLACING FREE OF THE SAME OBJECT'S OTHER FIELD is no danger to
    /// a read: the field read owns its value alone, and nothing that value
    /// holds is inside the other field's. The read must be a load of a field
    /// of the very object the free's read was of, and of another field.
    /// </summary>
    private static bool FreesOtherField(Function f, Dictionary<VReg, Instr> defs, HashSet<VReg> written, SelfFrees mine, Instr free, Instr read)
    {
        if (!mine.Pairs.TryGetValue(free, out var pair) || read.Op != Opcode.Load || read.Field is null || read.Field == pair.Load.Field
            || read.Operands.Count < 1 || read.Operands[0] is not RegOperand { Reg: var readBase }
            || pair.Load.Operands[0] is not RegOperand { Reg: var freedBase }) return false;
        return StableRoot(f, defs, written, readBase) is VReg a && a == StableRoot(f, defs, written, freedBase);
    }

    /// <summary>
    /// A FREE OF AN ELEMENT OF THE VERY VALUE READ -- a List giving back the
    /// objects its array holds (OwnedElements.Release) -- is no danger to
    /// that read: what an element owns is never the array the field owns,
    /// which no element can hold, an owned field's value going nowhere.
    /// </summary>
    private static bool FreesElementOf(Dictionary<VReg, Instr> defs, Instr free, HashSet<VReg> inside)
    {
        if (inside.Count == 0 || free.Operands.Count == 0 || free.Operands[0] is not RegOperand freed
            || OriginOf(defs, freed.Reg) is not { Op: Opcode.Load, Operands: [RegOperand { Reg: var at }, ..] }) return false;
        if (inside.Contains(at)) return true;
        return defs.TryGetValue(at, out Instr? address) && address.Op == Opcode.Add && address.Operands.Count == 2
            && address.Operands[0] is RegOperand { Reg: var array } && inside.Contains(array);
    }
}
