#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// The control-flow graph of a function: predecessors, successors, an
/// ordering for dataflow, reachability and dominators. A snapshot: passes
/// that change the block list build a fresh one rather than editing this,
/// which keeps every query here trivially consistent with itself.
///
/// The graph has more than one root. The entry block is one; every landing
/// pad is another, because control arrives there by <see cref="Opcode.Unwind"/>
/// through a handler record rather than by a branch, and nothing in the
/// graph names that edge. A block whose address is taken by
/// <see cref="Opcode.LabelAddr"/> is treated the same way for the same
/// reason. A pass that reasons "no predecessors, so dead" must ask
/// <see cref="IsRoot"/> first.
/// </summary>
public sealed class Cfg
{
    public Function Function { get; }

    // BY POSITION, NOT BY IDENTITY. These were dictionaries keyed on the
    // block object, which means a hash of its reference: compiling one kernel
    // source builds a hundred and ten thousand of these graphs, and profiling
    // put an eighth of the whole compile in Dictionary.FindValue,
    // Dictionary.TryInsert and ObjectHeader.GetHashCode -- the last because
    // hashing a reference has to reach into the object's header word. A block
    // already has a position in its function; the graph is a snapshot of that
    // function, so the position is a dense key and the maps are arrays.
    // Made when an edge arrives, not for every block: an entry has no
    // predecessor, a return no successor, and most of the rest have one or
    // two, so two full lists a block was most of what building a graph
    // allocated -- a hundred and ten thousand graphs for one source.
    private readonly List<Block>?[] _preds;
    private readonly List<Block>?[] _succs;
    private static readonly Block[] None = Array.Empty<Block>();
    private readonly bool[] _root;
    private readonly List<Block> _roots = new();
    private List<Block>? _rpo;
    private Dictionary<Block, HashSet<Block>>? _reach;
    private ulong[][]? _dom;
    private Dictionary<Block, Block?>? _idom;
    private Dictionary<Block, List<Block>>? _domChildren;
    private Dictionary<Block, HashSet<Block>>? _frontier;

    public Cfg(Function f)
    {
        Function = f;
        int count = f.Blocks.Count;
        _preds = new List<Block>?[count];
        _succs = new List<Block>?[count];
        _root = new bool[count];
        for (int k = 0; k < count; k++) f.Blocks[k].Order = k;

        Root(f.Entry);
        foreach (Block b in f.Blocks)
        {
            if (b.IsLandingPad)
            {
                Root(b);
            }
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.LabelAddr)
                {
                    foreach (Block t in i.Targets)
                    {
                        Root(t);
                    }
                }
            }
            foreach (Block s in b.Successors)
            {
                // A Switch may list the same block many times; one edge is
                // enough for every analysis here, and it keeps the
                // predecessor count honest for "sole predecessor" checks.
                List<Block> outgoing = _succs[b.Order] ??= new List<Block>(2);
                if (!outgoing.Contains(s))
                {
                    outgoing.Add(s);
                    (_preds[s.Order] ??= new List<Block>(2)).Add(b);
                }
            }
        }
    
    }

    /// <summary>Records a block control can reach other than by a branch.</summary>
    private void Root(Block b)
    {
        if (_root[b.Order]) return;
        _root[b.Order] = true;
        _roots.Add(b);
    }

    public IReadOnlyList<Block> Preds(Block b) => _preds[b.Order] ?? (IReadOnlyList<Block>)None;
    public IReadOnlyList<Block> Succs(Block b) => _succs[b.Order] ?? (IReadOnlyList<Block>)None;

    /// <summary>Whether control can enter the block by something other than a branch from a predecessor.</summary>
    public bool IsRoot(Block b) => _root[b.Order];
    public IReadOnlyCollection<Block> Roots => _roots;

    /// <summary>
    /// Reverse postorder over every block reachable from any root, entry
    /// first: a forward dataflow pass converges fastest visiting blocks in
    /// this order, a backward one in its reverse.
    /// </summary>
    public IReadOnlyList<Block> ReversePostorder
    {
        get
        {
            if (_rpo is null)
            {
                List<Block> post = new();
                HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
                // The entry goes first so it ends up last in postorder, ahead
                // of every pad, and the pads follow in block order so the
                // result is deterministic.
                foreach (Block root in Function.Blocks.Where(IsRoot))
                {
                    Postorder(root, seen, post);
                }
                post.Reverse();
                _rpo = post;
            }
            return _rpo;
        }
    }

    private void Postorder(Block start, HashSet<Block> seen, List<Block> post)
    {
        // Iterative, because lowering can emit a chain of thousands of
        // blocks for a long method and recursion would overflow the stack.
        if (!seen.Add(start))
        {
            return;
        }
        Stack<(Block, int)> stack = new();
        stack.Push((start, 0));
        while (stack.Count > 0)
        {
            (Block b, int k) = stack.Pop();
            IReadOnlyList<Block> succs = Succs(b);
            if (k < succs.Count)
            {
                stack.Push((b, k + 1));
                if (seen.Add(succs[k]))
                {
                    stack.Push((succs[k], 0));
                }
            }
            else
            {
                post.Add(b);
            }
        }
    }

    /// <summary>The blocks reachable from a root.</summary>
    public HashSet<Block> Reachable()
    {
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in ReversePostorder)
        {
            seen.Add(b);
        }
        return seen;
    }

    /// <summary>
    /// Whether there is a path of one or more edges from <paramref name="from"/>
    /// to <paramref name="to"/>. Cached per source; a function with n blocks
    /// costs at most n such walks, which the propagation passes stay well
    /// under because they only ask about definition blocks.
    /// </summary>
    public bool Reaches(Block from, Block to)
    {
        _reach ??= new Dictionary<Block, HashSet<Block>>(ReferenceEqualityComparer.Instance);
        if (!_reach.TryGetValue(from, out HashSet<Block>? set))
        {
            set = new HashSet<Block>(ReferenceEqualityComparer.Instance);
            Stack<Block> work = new(Succs(from));
            while (work.Count > 0)
            {
                Block b = work.Pop();
                if (set.Add(b))
                {
                    foreach (Block s in Succs(b))
                    {
                        work.Push(s);
                    }
                }
            }
            _reach[from] = set;
        }
        return set.Contains(to);
    }

    /// <summary>Whether the block lies on a cycle: its own instructions can execute more than once.</summary>
    public bool InCycle(Block b) => Reaches(b, b);

    /// <summary>
    /// Whether some path of one or more edges leads from <paramref name="from"/>
    /// to <paramref name="to"/> without passing through <paramref name="from"/>
    /// again on the way. The forwarding passes ask this: a path that
    /// returns to the block they started in re-executes the instruction
    /// they are reasoning from, so it does not count. Not cached, because
    /// the callers ask about few blocks and a cache keyed on the pair
    /// would mostly miss.
    /// </summary>
    public bool ReachesWithoutReentering(Block from, Block to)
    {
        if (ReferenceEquals(from, to))
        {
            return false;
        }
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance) { from };
        Stack<Block> work = new(Succs(from));
        while (work.Count > 0)
        {
            Block b = work.Pop();
            if (ReferenceEquals(b, to))
            {
                return true;
            }
            if (seen.Add(b))
            {
                foreach (Block s in Succs(b))
                {
                    work.Push(s);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="a"/> dominates <paramref name="b"/>: every path
    /// from a root to b passes through a. A block dominates itself. Computed
    /// as bit sets by iteration, which is asymptotically worse than the
    /// Cooper-Harvey-Kennedy tree but handles several roots without special
    /// cases, and functions are small enough that it is not the cost that
    /// matters. Blocks reachable from no root dominate nothing and are
    /// dominated by everything, which is the conventional answer.
    /// </summary>
    public bool Dominates(Block a, Block b)
    {
        _dom ??= ComputeDominators();
        int ia = a.Order;
        return (_dom[b.Order][ia >> 6] & (1UL << (ia & 63))) != 0;
    }

    private ulong[][] ComputeDominators()
    {
        int n = Function.Blocks.Count;
        int words = (n + 63) >> 6;
        ulong[][] dom = new ulong[n][];
        ulong[] all = new ulong[words];
        for (int k = 0; k < n; k++)
        {
            all[k >> 6] |= 1UL << (k & 63);
        }
        for (int k = 0; k < n; k++)
        {
            dom[k] = (ulong[])all.Clone();
        }
        foreach (Block root in _roots)
        {
            int r = root.Order;
            Array.Clear(dom[r]);
            dom[r][r >> 6] |= 1UL << (r & 63);
        }

        IReadOnlyList<Block> order = ReversePostorder;
        ulong[] tmp = new ulong[words];
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Block b in order)
            {
                if (IsRoot(b))
                {
                    continue;
                }
                int ib = b.Order;
                Array.Copy(all, tmp, words);
                foreach (Block p in Preds(b))
                {
                    ulong[] dp = dom[p.Order];
                    for (int w = 0; w < words; w++)
                    {
                        tmp[w] &= dp[w];
                    }
                }
                tmp[ib >> 6] |= 1UL << (ib & 63);
                ulong[] db = dom[ib];
                for (int w = 0; w < words; w++)
                {
                    if (db[w] != tmp[w])
                    {
                        db[w] = tmp[w];
                        changed = true;
                    }
                }
            }
        }
        return dom;
    }

    /// <summary>
    /// Drops every block no root reaches. Returns whether anything changed.
    /// The entry stays at index zero because it is a root, so
    /// <see cref="Function.Entry"/> keeps meaning what it did.
    /// </summary>
    public static bool RemoveUnreachable(Function f)
    {
        Cfg cfg = new(f);
        HashSet<Block> live = cfg.Reachable();
        if (live.Count == f.Blocks.Count)
        {
            return false;
        }
        foreach (Block dead in f.Blocks)
        {
            if (live.Contains(dead))
            {
                continue;
            }
            // A surviving successor loses a predecessor; its phis must not
            // go on naming it.
            foreach (Block s in cfg.Succs(dead))
            {
                if (live.Contains(s))
                {
                    Phi.RemoveIncoming(s, dead);
                }
            }
        }
        f.Blocks.RemoveAll(b => !live.Contains(b));
        return true;
    }

    /// <summary>
    /// The immediate dominator: the nearest strict dominator. Null for a
    /// root and for a block no root reaches. Read off the dominator sets
    /// as the strict dominator with the most dominators of its own, which
    /// is the deepest one.
    /// </summary>
    public Block? Idom(Block b)
    {
        BuildTree();
        return _idom![b];
    }

    /// <summary>The blocks whose immediate dominator this is, in block order.</summary>
    public IReadOnlyList<Block> DomChildren(Block b)
    {
        BuildTree();
        return _domChildren![b];
    }

    /// <summary>
    /// The dominance frontier: blocks with a predecessor this dominates
    /// that are not themselves strictly dominated by it. Where SSA puts
    /// phis. Cooper, Harvey and Kennedy's walk up the dominator tree from
    /// each join block's predecessors.
    /// </summary>
    public IReadOnlySet<Block> Frontier(Block b)
    {
        if (_frontier is null)
        {
            BuildTree();
            _frontier = new Dictionary<Block, HashSet<Block>>(ReferenceEqualityComparer.Instance);
            foreach (Block x in Function.Blocks)
            {
                _frontier[x] = new HashSet<Block>(ReferenceEqualityComparer.Instance);
            }
            HashSet<Block> live = Reachable();
            foreach (Block join in Function.Blocks)
            {
                if (Preds(join).Count < 2 || !live.Contains(join))
                {
                    continue;
                }
                Block? stop = _idom![join];
                foreach (Block p in Preds(join))
                {
                    if (!live.Contains(p))
                    {
                        continue;
                    }
                    Block? runner = p;
                    while (runner is not null && !ReferenceEquals(runner, stop))
                    {
                        _frontier[runner].Add(join);
                        runner = _idom[runner];
                    }
                }
            }
        }
        return _frontier[b];
    }

    private void BuildTree()
    {
        if (_idom is not null)
        {
            return;
        }
        _dom ??= ComputeDominators();
        _idom = new Dictionary<Block, Block?>(ReferenceEqualityComparer.Instance);
        _domChildren = new Dictionary<Block, List<Block>>(ReferenceEqualityComparer.Instance);
        foreach (Block b in Function.Blocks)
        {
            _domChildren[b] = new List<Block>();
        }
        HashSet<Block> live = Reachable();
        int[] size = new int[Function.Blocks.Count];
        for (int k = 0; k < size.Length; k++)
        {
            foreach (ulong w in _dom[k])
            {
                size[k] += System.Numerics.BitOperations.PopCount(w);
            }
        }
        foreach (Block b in Function.Blocks)
        {
            Block? best = null;
            if (live.Contains(b) && !IsRoot(b))
            {
                int ib = b.Order;
                foreach (Block d in Function.Blocks)
                {
                    int id = d.Order;
                    if (id != ib && (_dom[ib][id >> 6] & (1UL << (id & 63))) != 0
                        && (best is null || size[id] > size[best.Order]))
                    {
                        best = d;
                    }
                }
            }
            _idom[b] = best;
            if (best is not null)
            {
                _domChildren[best].Add(b);
            }
        }
    }
}
