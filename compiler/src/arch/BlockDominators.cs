#nullable enable
namespace Corsac.Lang;

/// <summary>
/// Which blocks of a machine function dominate which: every block's
/// immediate dominator, by the iterative method of Cooper, Harvey and
/// Kennedy over reverse postorder from the entry, block 0. For the passes
/// of either backend that carry a fact from one block into those it
/// dominates (AddressFold's bases read in other blocks).
/// </summary>
internal static class BlockDominators
{
    /// <summary>Each block's immediate dominator, the entry its own, -1 for one the entry does not reach.</summary>
    public static int[] Of(int n, Func<int, IEnumerable<int>> successors)
    {
        int[] idom = new int[n];
        Array.Fill(idom, -1);
        if (n == 0) return idom;
        List<int>[] preds = new List<int>[n];
        for (int b = 0; b < n; b++) preds[b] = new();
        for (int b = 0; b < n; b++)
            foreach (int s in successors(b))
                preds[s].Add(b);
        // Postorder by an explicit stack: a function can be deeper than a thread's.
        int[] rpo = new int[n];
        bool[] seen = new bool[n];
        List<int> post = new();
        Stack<(int B, IEnumerator<int> Next)> stack = new();
        seen[0] = true;
        stack.Push((0, successors(0).GetEnumerator()));
        while (stack.Count > 0)
        {
            var (b, next) = stack.Peek();
            if (next.MoveNext())
            {
                int s = next.Current;
                if (!seen[s])
                {
                    seen[s] = true;
                    stack.Push((s, successors(s).GetEnumerator()));
                }
                continue;
            }
            stack.Pop();
            post.Add(b);
        }
        int[] order = new int[post.Count];
        for (int k = 0; k < post.Count; k++)
        {
            order[k] = post[post.Count - 1 - k];
            rpo[order[k]] = k;
        }
        idom[0] = 0;
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int k = 1; k < order.Length; k++)
            {
                int b = order[k];
                int best = -1;
                foreach (int p in preds[b])
                {
                    if (idom[p] < 0) continue;
                    if (best < 0) { best = p; continue; }
                    int f1 = p, f2 = best;
                    while (f1 != f2)
                    {
                        while (rpo[f1] > rpo[f2]) f1 = idom[f1];
                        while (rpo[f2] > rpo[f1]) f2 = idom[f2];
                    }
                    best = f1;
                }
                if (best >= 0 && idom[b] != best) { idom[b] = best; changed = true; }
            }
        }
        return idom;
    }

    /// <summary>Whether block a dominates block b and is not b.</summary>
    public static bool Strictly(int[] idom, int a, int b)
    {
        if (a == b || idom[b] < 0 || idom[a] < 0) return false;
        for (int at = b; at != 0;)
        {
            at = idom[at];
            if (at == a) return true;
        }
        return false;
    }
}
