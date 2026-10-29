#nullable enable

namespace Corsac.Lang.RegAlloc;

/// <summary>
/// INSTRUCTIONS REORDERED TO NEED FEWER REGISTERS AT ONCE, before the scan,
/// on any machine: each block, between its barriers (Effect.Barrier), list-
/// scheduled bottom-up, each place given to the ready instruction that
/// starts the fewest of the first class's values above it (net of those it
/// ends), the latest of the selector's order among equals (Chen's min-
/// register list scheduler, arXiv 2303.06855; LLVM's register-reduction
/// scheduler; Barany and Krall measured such a scheduler's spills against a
/// latency one's). Loads may pass loads: two that fault on a null reference
/// can swap which faults first, as the same exception.
/// Out-of-order machines hide the latency a shorter value exposes, and on an
/// in-order one the selector's order is kept unless the stretch needs more
/// registers than the machine has: a stretch is reordered only where its
/// own values outnumber the first class's registers somewhere and the new
/// order needs fewer at its worst (Goodman and Hsu's switch to freeing
/// registers when they run short).
///
/// The order kept: a value's definition before its reads, and its reads
/// before the next definition of its register (the registers are not yet
/// single definitions); a store after the loads and stores before it, and a
/// load after the store before it; and the flags: a writer whose flags are
/// read, with its readers, a stretch no other writer enters -- writers before
/// it stay before its writer, writers after it after its last reader, the
/// last writer of a stretch counting as read past its end. A writer whose
/// flags nothing reads moves freely between those stretches.
/// </summary>
public static class Schedule
{
    /// <summary>A stretch longer than this keeps its order: the scheduler's choice is quadratic in the stretch.</summary>
    private const int Longest = 1500;

    public static void Run(AFunction f)
    {
        RegisterFile rf = f.Registers;
        int k = rf.Classes[0].Preference.Length;
        // Which block each register is named in (-1: more than one), and the
        // registers a landing pad names -- entered by no edge, so no flow says
        // they outlive the stretches of its try: they are taken to.
        Dictionary<int, int> home = new();
        HashSet<int> padRead = new();
        for (int b = 0; b < f.Blocks.Count; b++)
            foreach (AInstr i in f.Blocks[b].Instrs)
                foreach (Named n in i.Regs)
                    if (n.Reg >= rf.FirstVirtual)
                    {
                        home[n.Reg] = home.TryGetValue(n.Reg, out int was) && was != b ? -1 : b;
                        if (f.Blocks[b].LandingPad) padRead.Add(n.Reg);
                    }
        // WHAT IS LIVE OUT OF EACH BLOCK, for the values named in more than
        // one block or in a loop's: a value carried round a back edge lives
        // to the end of the stretch that makes it, though nothing after it in
        // the block names it (division's lap: three loads and a store more
        // when its 64-bit sum was taken for dead at its definition). Worked
        // out once a stretch names more values than there are registers, as
        // any it reorders must.
        BitSet[]? liveOut = null;
        Dictionary<int, int>? index = null;
        for (int b = 0; b < f.Blocks.Count; b++)
        {
            List<AInstr> instrs = f.Blocks[b].Instrs;
            // Each register's last mention in the block, for what outlives a stretch.
            Dictionary<int, int> lastIn = new();
            for (int x = 0; x < instrs.Count; x++)
                foreach (Named r in instrs[x].Regs)
                    if (r.Reg >= rf.FirstVirtual) lastIn[r.Reg] = x;
            int start = 0;
            for (int at = 0; at <= instrs.Count; at++)
            {
                if (at < instrs.Count && (instrs[at].Effects & Effect.Barrier) == 0) continue;
                if (at - start >= 3 && at - start <= Longest && Names(f, instrs, start, at) > k)
                {
                    if (liveOut is null) liveOut = LiveOut(f, home, Cyclic(f), out index);
                    Stretch(f, instrs, start, at, liveOut[b], index!, padRead, lastIn, k);
                }
                start = at + 1;
            }
        }
    }

    /// <summary>
    /// Each block's live-out set (by `index`) of the first class's values
    /// named in more than one block or in a block on a cycle -- the others
    /// live and die in their one block, which its own instructions say --
    /// by the usual backward dataflow to a fixed point.
    /// </summary>
    private static BitSet[] LiveOut(AFunction f, Dictionary<int, int> home, bool[] cyclic, out Dictionary<int, int> index)
    {
        int nb = f.Blocks.Count;
        RegisterFile rf = f.Registers;
        index = new();
        for (int b = 0; b < nb; b++)
            foreach (AInstr i in f.Blocks[b].Instrs)
                foreach (Named r in i.Regs)
                    if (r.Reg >= rf.FirstVirtual && f.Target.ClassOf(r.Reg) == 0 && !index.ContainsKey(r.Reg)
                        && (home[r.Reg] < 0 || cyclic[b]))
                        index[r.Reg] = index.Count;
        int width = Math.Max(index.Count, 1);
        BitSet[] use = new BitSet[nb], def = new BitSet[nb], liveIn = new BitSet[nb], liveOut = new BitSet[nb];
        for (int b = 0; b < nb; b++)
        {
            use[b] = new(width); def[b] = new(width); liveIn[b] = new(width); liveOut[b] = new(width);
            foreach (AInstr i in f.Blocks[b].Instrs)
            {
                foreach (Named r in i.Regs)
                    if ((r.Role & Role.Use) != 0 && index.TryGetValue(r.Reg, out int at) && !def[b].Get(at)) use[b].Set(at);
                foreach (Named r in i.Regs)
                    if ((r.Role & Role.Def) != 0 && index.TryGetValue(r.Reg, out int at)) def[b].Set(at);
            }
        }
        if (index.Count == 0) return liveOut;
        List<ABlock> into = new();
        Dictionary<ABlock, int> blockAt = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++) blockAt[f.Blocks[b]] = b;
        int[][] succ = new int[nb][];
        List<int>[] pred = new List<int>[nb];
        for (int b = 0; b < nb; b++) pred[b] = new();
        for (int b = 0; b < nb; b++)
        {
            f.SuccessorsInto(b, into);
            succ[b] = new int[into.Count];
            for (int e = 0; e < into.Count; e++) { succ[b][e] = blockAt[into[e]]; pred[succ[b][e]].Add(b); }
        }
        // A worklist, not sweeps: a chain of blocks each jumping to one laid
        // out before it moves liveness one block a sweep (the allocator's
        // ComputeLiveness found 1077 sweeps on a 14,000-block Main).
        BitSet next = new(width);
        Stack<int> work = new();
        bool[] queued = new bool[nb];
        for (int b = 0; b < nb; b++) { work.Push(b); queued[b] = true; }
        while (work.Count > 0)
        {
            int b = work.Pop();
            queued[b] = false;
            liveOut[b].Clear();
            foreach (int s2 in succ[b]) liveOut[b].Or(liveIn[s2]);
            next.Clear();
            next.Or(liveOut[b]);
            next.AndNot(def[b]);
            next.Or(use[b]);
            if (next.Equals(liveIn[b])) continue;
            liveIn[b].CopyFrom(next);
            foreach (int p2 in pred[b]) if (!queued[p2]) { queued[p2] = true; work.Push(p2); }
        }
        return liveOut;
    }

    /// <summary>Which blocks lie on a cycle of the flow graph (Tarjan's strongly connected components, iteratively).</summary>
    private static bool[] Cyclic(AFunction f)
    {
        int nb = f.Blocks.Count;
        Dictionary<ABlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++) index[f.Blocks[b]] = b;
        List<int>[] succ = new List<int>[nb];
        List<ABlock> into = new();
        for (int b = 0; b < nb; b++)
        {
            f.SuccessorsInto(b, into);
            succ[b] = into.Select(x => index[x]).ToList();
        }
        bool[] cyclic = new bool[nb];
        int[] order = new int[nb], low = new int[nb];
        Array.Fill(order, -1);
        bool[] onStack = new bool[nb];
        Stack<int> stack = new();
        int counter = 0;
        for (int root = 0; root < nb; root++)
        {
            if (order[root] >= 0) continue;
            Stack<(int B, int Next)> walk = new();
            walk.Push((root, 0));
            order[root] = low[root] = counter++;
            stack.Push(root);
            onStack[root] = true;
            while (walk.Count > 0)
            {
                (int b, int next) = walk.Pop();
                if (next < succ[b].Count)
                {
                    walk.Push((b, next + 1));
                    int s = succ[b][next];
                    if (s == b) cyclic[b] = true;
                    if (order[s] < 0)
                    {
                        order[s] = low[s] = counter++;
                        stack.Push(s);
                        onStack[s] = true;
                        walk.Push((s, 0));
                    }
                    else if (onStack[s]) low[b] = Math.Min(low[b], order[s]);
                    continue;
                }
                if (walk.Count > 0) low[walk.Peek().B] = Math.Min(low[walk.Peek().B], low[b]);
                if (low[b] != order[b]) continue;
                List<int> component = new();
                int x;
                do { x = stack.Pop(); onStack[x] = false; component.Add(x); } while (x != b);
                if (component.Count > 1) foreach (int c in component) cyclic[c] = true;
            }
        }
        return cyclic;
    }

    /// <summary>The stretch [from, to) of a block reordered, where that needs fewer registers.</summary>
    /// <summary>How many of the first class's values the stretch [from, to) names: its peak counts no more.</summary>
    private static int Names(AFunction f, List<AInstr> instrs, int from, int to)
    {
        HashSet<int> named = new();
        for (int x = from; x < to; x++)
            foreach (Named r in instrs[x].Regs)
                if (r.Reg >= f.Registers.FirstVirtual && f.Target.ClassOf(r.Reg) == 0) named.Add(r.Reg);
        return named.Count;
    }

    private static void Stretch(AFunction f, List<AInstr> instrs, int from, int to, BitSet liveOut, Dictionary<int, int> index, HashSet<int> padRead, Dictionary<int, int> lastIn, int k)
    {
        int n = to - from;
        RegisterFile rf = f.Registers;
        ITarget t = f.Target;

        // The registers whose pressure is counted: the first class's values.
        // A value read before it is written here (a read-modify-write
        // included) is live from the stretch's start -- in any order the
        // dependences allow, as each later definition depends on every read
        // before it and on the definition before it (the register edges
        // below); one mentioned after the stretch, or live out of the block,
        // to its end.
        HashSet<int> liveIn = new(), outlives = new(), seen = new();
        for (int x = 0; x < n; x++)
        {
            Named[] named = instrs[from + x].Regs;
            foreach (Named r in named)
            {
                if (r.Reg < rf.FirstVirtual || t.ClassOf(r.Reg) != 0) continue;
                if ((r.Role & Role.Use) != 0 && !seen.Contains(r.Reg)) liveIn.Add(r.Reg);
                if (lastIn[r.Reg] >= to || index.TryGetValue(r.Reg, out int at) && liveOut.Get(at) || padRead.Contains(r.Reg)) outlives.Add(r.Reg);
            }
            foreach (Named r in named) if (r.Reg >= rf.FirstVirtual) seen.Add(r.Reg);
        }
        int before = Peak(instrs, from, Enumerable.Range(0, n).ToArray(), liveIn, outlives, rf, t);
        // Only a stretch whose own values outnumber the registers somewhere:
        // below that, the selector's order costs no spill (Goodman and Hsu).
        if (before <= k) return;

        List<int>[] succ = new List<int>[n];
        int[] preds = new int[n];
        for (int x = 0; x < n; x++) succ[x] = new();
        void Edge(int a, int c)
        {
            if (a == c) return;
            succ[a].Add(c);
            preds[c]++;
        }

        // Registers: definitions, reads and the next definition.
        Dictionary<int, (int Def, List<int> Uses)> regs = new();
        for (int x = 0; x < n; x++)
        {
            Named[] named = instrs[from + x].Regs;
            foreach (Named r in named)
            {
                if ((r.Role & Role.Use) == 0) continue;
                if (regs.TryGetValue(r.Reg, out var st))
                {
                    if (st.Def >= 0) Edge(st.Def, x);
                    st.Uses.Add(x);
                }
                else regs[r.Reg] = (-1, new List<int> { x });
            }
            foreach (Named r in named)
            {
                if ((r.Role & Role.Def) == 0) continue;
                if (regs.TryGetValue(r.Reg, out var st))
                {
                    if (st.Def >= 0) Edge(st.Def, x);
                    foreach (int u in st.Uses) Edge(u, x);
                }
                regs[r.Reg] = (x, new List<int>());
            }
        }

        // Memory: a store after every access before it, a load after the store before it.
        int lastStore = -1;
        List<int> loads = new();
        for (int x = 0; x < n; x++)
        {
            Effect e = instrs[from + x].Effects;
            if ((e & Effect.ReadsMemory) != 0 && lastStore >= 0) Edge(lastStore, x);
            if ((e & Effect.WritesMemory) != 0)
            {
                if (lastStore >= 0) Edge(lastStore, x);
                foreach (int l in loads) Edge(l, x);
                loads.Clear();
                lastStore = x;
            }
            if ((e & Effect.ReadsMemory) != 0 && (e & Effect.WritesMemory) == 0) loads.Add(x);
        }

        // Flags: each writer whose flags are read, with its readers, a
        // section; the last writer's read past the stretch's end, perhaps.
        // The writer before its readers, its readers before the last of them
        // (and so before any writer after), one section's last reader before
        // the next section's writer, and a writer whose flags nothing reads
        // kept between the sections it was between.
        List<(int Writer, List<int> Readers, bool Out)> sections = new();
        {
            int producer = -1;  // -1: what was before the stretch
            List<int> readers = new();
            for (int x = 0; x < n; x++)
            {
                Effect e = instrs[from + x].Effects;
                if ((e & Effect.ReadsFlags) != 0) readers.Add(x);
                if ((e & Effect.WritesFlags) != 0)
                {
                    if (readers.Count > 0) sections.Add((producer, readers, false));
                    producer = x;
                    readers = new();
                }
            }
            if (producer >= 0) sections.Add((producer, readers, true));
            else if (readers.Count > 0) sections.Add((-1, readers, false));
        }
        int Last((int Writer, List<int> Readers, bool Out) sec) => sec.Out || sec.Readers.Count == 0 ? n : sec.Readers[^1];
        for (int si = 0; si < sections.Count; si++)
        {
            (int w, List<int> readers, bool _) = sections[si];
            if (w >= 0) foreach (int r in readers) Edge(w, r);
            int last = Last(sections[si]);
            if (last < n) foreach (int r in readers) Edge(r, last);
            if (si > 0 && Last(sections[si - 1]) < n && w >= 0) Edge(Last(sections[si - 1]), w);
        }
        {
            HashSet<int> producers = new(sections.Where(sec => sec.Writer >= 0).Select(sec => sec.Writer));
            int next = 0;
            for (int x = 0; x < n; x++)
            {
                if ((instrs[from + x].Effects & Effect.WritesFlags) == 0 || producers.Contains(x)) continue;
                while (next < sections.Count && sections[next].Writer >= 0 && sections[next].Writer < x
                    || next < sections.Count && sections[next].Writer < 0 && Last(sections[next]) < x) next++;
                if (next > 0 && Last(sections[next - 1]) < n) Edge(Last(sections[next - 1]), x);
                if (next < sections.Count && sections[next].Writer > x) Edge(x, sections[next].Writer);
            }
        }

        // Bottom-up: a node is ready once everything after it is placed.
        int[] after = new int[n];
        for (int x = 0; x < n; x++) after[x] = succ[x].Count;
        List<int>[] pred = new List<int>[n];
        for (int x = 0; x < n; x++) pred[x] = new();
        for (int x = 0; x < n; x++) foreach (int c in succ[x]) pred[c].Add(x);
        List<int> ready = new();
        for (int x = 0; x < n; x++) if (after[x] == 0) ready.Add(x);
        HashSet<int> liveNow = new(outlives);
        int[] order = new int[n];
        for (int slot = n - 1; slot >= 0; slot--)
        {
            int best = -1, bestDelta = int.MaxValue;
            foreach (int x in ready)
            {
                int delta = Delta(instrs[from + x], liveNow, rf, t);
                // The fewest values started; the latest of the selector's order among equals.
                if (delta < bestDelta || delta == bestDelta && x > best) { best = x; bestDelta = delta; }
            }
            if (best < 0) return;  // a cycle: no order (cannot happen; kept as it was)
            ready.Remove(best);
            order[slot] = best;
            foreach (Named r in instrs[from + best].Regs)
                if (r.Reg >= rf.FirstVirtual && t.ClassOf(r.Reg) == 0 && (r.Role & Role.Def) != 0 && (r.Role & Role.Use) == 0) liveNow.Remove(r.Reg);
            foreach (Named r in instrs[from + best].Regs)
                if (r.Reg >= rf.FirstVirtual && t.ClassOf(r.Reg) == 0 && (r.Role & Role.Use) != 0) liveNow.Add(r.Reg);
            foreach (int p in pred[best]) if (--after[p] == 0) ready.Add(p);
        }
        int now = Peak(instrs, from, order, liveIn, outlives, rf, t);
        if (now >= before) return;
        AInstr[] copy = new AInstr[n];
        for (int x = 0; x < n; x++) copy[x] = instrs[from + order[x]];
        for (int x = 0; x < n; x++) instrs[from + x] = copy[x];
    }

    /// <summary>How many more of the first class's values are live above `i` than below it, placed bottom-up.</summary>
    private static int Delta(AInstr i, HashSet<int> live, RegisterFile rf, ITarget t)
    {
        // A register named twice (`add v, v`, `[v+v*2]`) once, by all it does.
        Span<(int Reg, Role Role)> seen = stackalloc (int, Role)[i.Regs.Length];
        int count = 0;
        foreach (Named r in i.Regs)
        {
            if (r.Reg < rf.FirstVirtual || t.ClassOf(r.Reg) != 0) continue;
            int at = 0;
            while (at < count && seen[at].Reg != r.Reg) at++;
            if (at == count) seen[count++] = (r.Reg, r.Role);
            else seen[at].Role |= r.Role;
        }
        int delta = 0;
        for (int at = 0; at < count; at++)
        {
            bool use = (seen[at].Role & Role.Use) != 0, def = (seen[at].Role & Role.Def) != 0;
            if (use && !live.Contains(seen[at].Reg)) delta++;
            else if (def && !use && live.Contains(seen[at].Reg)) delta--;
        }
        return delta;
    }

    /// <summary>
    /// The most of the first class's values live at once over the stretch in
    /// `order`: each from its definition (or the stretch's start, read before
    /// it is written: `liveIn`, the same in any order the dependences allow)
    /// to its last mention (or the stretch's end, where it outlives it).
    /// </summary>
    private static int Peak(List<AInstr> instrs, int from, int[] order, HashSet<int> liveIn, HashSet<int> outlives, RegisterFile rf, ITarget t)
    {
        // Each value's last mention: a read, or a definition nothing reads.
        Dictionary<int, int> last = new();
        for (int s = 0; s < order.Length; s++)
            foreach (Named r in instrs[from + order[s]].Regs)
                if (r.Reg >= rf.FirstVirtual && t.ClassOf(r.Reg) == 0) last[r.Reg] = s;
        HashSet<int> live = new(liveIn);
        int peak = live.Count;
        for (int s = 0; s < order.Length; s++)
        {
            foreach (Named r in instrs[from + order[s]].Regs)
                if (r.Reg >= rf.FirstVirtual && t.ClassOf(r.Reg) == 0 && (r.Role & Role.Def) != 0) live.Add(r.Reg);
            peak = Math.Max(peak, live.Count);
            foreach (Named r in instrs[from + order[s]].Regs)
                if (r.Reg >= rf.FirstVirtual && t.ClassOf(r.Reg) == 0
                    && last.TryGetValue(r.Reg, out int l) && l == s && !outlives.Contains(r.Reg))
                    live.Remove(r.Reg);
        }
        return peak;
    }
}
