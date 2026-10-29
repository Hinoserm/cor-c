#nullable enable

namespace Corsac.Lang.RegAlloc;

/// <summary>
/// INSTRUCTIONS REORDERED TO NEED FEWER REGISTERS AT ONCE, before the scan,
/// on any machine: each block, between its barriers (Effect.Barrier), list-
/// scheduled bottom-up (Chen's min-register list scheduler, Intel IGC: an
/// instruction that ends as many values as it starts placed at once, the
/// rest by the fewest it starts; LLVM's register-reduction scheduler; Barany
/// and Krall measured such a scheduler's spills against a latency one's).
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
        // Which blocks each register is named in: a register named in one
        // block alone lives there only (its reads after a definition there);
        // any other is taken for live across the block's stretches.
        Dictionary<int, int> home = new();
        for (int b = 0; b < f.Blocks.Count; b++)
            foreach (AInstr i in f.Blocks[b].Instrs)
                foreach (Named n in i.Regs)
                    if (n.Reg >= rf.FirstVirtual)
                        home[n.Reg] = home.TryGetValue(n.Reg, out int was) && was != b ? -1 : b;
        bool[] cyclic = Cyclic(f);
        for (int b = 0; b < f.Blocks.Count; b++)
        {
            // NOT IN A LOOP: there values carried round the back edge, which
            // the stretch's own count does not see, are what the registers go
            // to; an order that needs fewer of the stretch's own took one from
            // them (division's lap: three loads and a store more).
            if (cyclic[b]) continue;
            List<AInstr> instrs = f.Blocks[b].Instrs;
            int start = 0;
            for (int at = 0; at <= instrs.Count; at++)
            {
                if (at < instrs.Count && (instrs[at].Effects & Effect.Barrier) == 0) continue;
                if (at - start >= 3 && at - start <= Longest) Stretch(f, instrs, start, at, b, home, k);
                start = at + 1;
            }
        }
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
    private static void Stretch(AFunction f, List<AInstr> instrs, int from, int to, int block, Dictionary<int, int> home, int k)
    {
        int n = to - from;
        RegisterFile rf = f.Registers;
        ITarget t = f.Target;
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

        // The registers whose pressure is counted: the first class's values.
        // Each value's last read in the stretch and its definition there; a
        // value read after the stretch, or named in another block, lives to
        // its end.
        Dictionary<int, int> lastUse = new();
        HashSet<int> defined = new(), outlives = new();
        for (int x = 0; x < n; x++)
            foreach (Named r in instrs[from + x].Regs)
            {
                if (r.Reg < rf.FirstVirtual || t.ClassOf(r.Reg) != 0) continue;
                if ((r.Role & Role.Use) != 0) lastUse[r.Reg] = x;
                if ((r.Role & Role.Def) != 0) defined.Add(r.Reg);
            }
        foreach (int v in defined)
        {
            if (!home.TryGetValue(v, out int h) || h != block) { outlives.Add(v); continue; }
            for (int x = to; x < instrs.Count && !outlives.Contains(v); x++)
                foreach (Named r in instrs[x].Regs)
                    if (r.Reg == v) { outlives.Add(v); break; }
        }

        int before = Peak(instrs, from, Enumerable.Range(0, n).ToArray(), lastUse, defined, outlives, rf, t);
        // Only a stretch whose own values outnumber the registers somewhere:
        // below that, the selector's order costs no spill (Goodman and Hsu).
        if (before <= k) return;

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
        int now = Peak(instrs, from, order, lastUse, defined, outlives, rf, t);
        if (now >= before) return;
        AInstr[] copy = new AInstr[n];
        for (int x = 0; x < n; x++) copy[x] = instrs[from + order[x]];
        for (int x = 0; x < n; x++) instrs[from + x] = copy[x];
    }

    /// <summary>How many more of the first class's values are live above `i` than below it, placed bottom-up.</summary>
    private static int Delta(AInstr i, HashSet<int> live, RegisterFile rf, ITarget t)
    {
        int delta = 0;
        foreach (Named r in i.Regs)
        {
            if (r.Reg < rf.FirstVirtual || t.ClassOf(r.Reg) != 0) continue;
            bool use = (r.Role & Role.Use) != 0, def = (r.Role & Role.Def) != 0;
            if (use && !live.Contains(r.Reg)) delta++;
            else if (def && !use && live.Contains(r.Reg)) delta--;
        }
        return delta;
    }

    /// <summary>
    /// The most of the first class's values live at once over the stretch in
    /// `order`: each from its definition (or the stretch's start, read before
    /// any) to its last read (or the stretch's end, where it outlives it).
    /// </summary>
    private static int Peak(List<AInstr> instrs, int from, int[] order, Dictionary<int, int> lastUse, HashSet<int> defined, HashSet<int> outlives, RegisterFile rf, ITarget t)
    {
        // Each value's last mention: a read, or a definition nothing reads.
        Dictionary<int, int> last = new();
        for (int s = 0; s < order.Length; s++)
            foreach (Named r in instrs[from + order[s]].Regs)
                if (r.Reg >= rf.FirstVirtual && t.ClassOf(r.Reg) == 0) last[r.Reg] = s;
        HashSet<int> live = new(outlives.Where(v => !defined.Contains(v)));
        foreach (int v in lastUse.Keys) if (!defined.Contains(v)) live.Add(v);
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
