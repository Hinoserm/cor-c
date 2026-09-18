using Corsac.Lang.Ir;
using Block = Corsac.Lang.Ir.Block;

namespace Corsac.Lang.X86;

/// <summary>Bounded dominance proof for blocks that execute on every loop backedge.</summary>
internal static class RepeatingRegions
{
    public static HashSet<Block> Find(Function function)
    {
        HashSet<Block> result = new();
        int count = function.Blocks.Count;
        // At most 32 KiB of dominance bits per worker. Large functions keep
        // compact bulk-memory instructions rather than spending unbounded RAM.
        if (count == 0 || count > 512) return result;
        Dictionary<Block, int> ids = function.Blocks.Select((block, index) => (block, index)).ToDictionary(pair => pair.block, pair => pair.index);
        List<int>[] successors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        List<int>[] predecessors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < count; index++)
        {
            Instr? terminator = function.Blocks[index].Terminator;
            if (terminator is null) continue;
            IEnumerable<Block> targets = terminator.Targets;
            if (terminator.Default is { } fallback) targets = targets.Append(fallback);
            foreach (Block target in targets.Distinct())
                if (ids.TryGetValue(target, out int next)) { successors[index].Add(next); predecessors[next].Add(index); }
        }
        int words = (count + 63) / 64;
        ulong[][] dominates = Enumerable.Range(0, count).Select(_ => Enumerable.Repeat(ulong.MaxValue, words).ToArray()).ToArray();
        bool[] roots = Enumerable.Range(0, count).Select(index => index == 0 || predecessors[index].Count == 0 || function.Blocks[index].IsLandingPad).ToArray();
        bool[] reachable = new bool[count]; Queue<int> pending = new();
        for (int index = 0; index < count; index++) if (roots[index]) { reachable[index] = true; pending.Enqueue(index); }
        while (pending.Count != 0)
            foreach (int successor in successors[pending.Dequeue()])
                if (!reachable[successor]) { reachable[successor] = true; pending.Enqueue(successor); }
        for (int index = 0; index < count; index++)
            if (roots[index] || !reachable[index]) { Array.Clear(dominates[index]); dominates[index][index / 64] = 1UL << (index % 64); }
        ulong[] nextBits = new ulong[words]; bool changed;
        do
        {
            changed = false;
            for (int index = 0; index < count; index++)
            {
                if (roots[index] || !reachable[index]) continue;
                Array.Fill(nextBits, ulong.MaxValue);
                foreach (int predecessor in predecessors[index])
                    if (reachable[predecessor]) for (int word = 0; word < words; word++) nextBits[word] &= dominates[predecessor][word];
                nextBits[index / 64] |= 1UL << (index % 64);
                for (int word = 0; word < words; word++)
                    if (dominates[index][word] != nextBits[word]) { dominates[index][word] = nextBits[word]; changed = true; }
            }
        } while (changed);
        bool Has(int block, int dominator) => (dominates[block][dominator / 64] & (1UL << (dominator % 64))) != 0;
        for (int latch = 0; latch < count; latch++)
        foreach (int header in successors[latch])
            if (reachable[latch] && Has(latch, header))
                for (int block = 0; block < count; block++)
                    if (Has(latch, block) && Has(block, header)) result.Add(function.Blocks[block]);
        return result;
    }
}
