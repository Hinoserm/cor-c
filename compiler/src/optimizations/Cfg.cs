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
    // EVERY EDGE IN TWO ARRAYS, one a direction, each block's run of it
    // found by its start: a list a block (and the list's own array) was
    // four allocations a block for every graph any pass builds, and a
    // hundred and ten thousand graphs are built for one source. Preds and
    // Succs answer a view of the run (Edges), which foreach walks without
    // an enumerator object.
    private readonly Block[] _predEdges;
    private readonly Block[] _succEdges;
    private readonly int[] _predStart;
    private readonly int[] _succStart;
    private readonly bool[] _root;
    private readonly List<Block> _roots;
    private List<Block>? _rpo;
    // THE ANALYSES, BY POSITION TOO: a row of bits a block (reachability,
    // dominators), a slot a block (the immediate dominator), the tree's
    // children as the edges are kept. Tables of sets keyed by block left a
    // set for every block to the collector each time a graph died.
    private int _words;
    private ulong[]? _reachBits;
    private bool[]? _reachDone;
    private ulong[]? _dom;
    private bool[]? _live;
    private Block?[]? _idom;
    private Block[]? _children;
    private int[]? _childStart;
    private HashSet<Block>?[]? _frontier;
    private static readonly HashSet<Block> NoFrontier = new(ReferenceEqualityComparer.Instance);
    private Stack<Block>? _walk;
    private int[]? _mark;
    private int _stamp;
    // A GRAPH NOBODY HOLDS ANY MORE (PipelineAnalyses' spare), whose arrays
    // this one takes as it needs them, cleared as far as it uses them: the
    // tables are by position and read only up to the function's own count.
    private Cfg? _spare;

    private static T[] Take<T>(T[]? spare, int count, bool clear)
    {
        if (spare is null || spare.Length < count) return new T[count];
        if (clear) Array.Clear(spare, 0, count);
        return spare;
    }

    public Cfg(Function f) : this(f, null) { }

    /// <summary>
    /// The graph of `f`, made in the storage of `spare` -- a graph of any
    /// function that nothing will read again -- where that is large enough
    /// (PipelineAnalyses.TakeSpare): the graphs a pipeline makes again and
    /// again were a tenth of what the collector took in a native compile.
    /// </summary>
    internal Cfg(Function f, Cfg? spare)
    {
        Function = f;
        _spare = spare;
        // One graph back, no further: the spare's own spare is let go.
        if (spare is not null) spare._spare = null;
        int count = f.Blocks.Count;
        _root = Take(spare?._root, count, clear: true);
        _roots = spare?._roots ?? new();
        _roots.Clear();
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
        }

        // The terminator's targets, then its default, as Block.Successors
        // yields them -- read here without the iterator, which was an
        // allocation per block for every pass that builds a graph. A Switch
        // may list the same block many times; one edge is enough for every
        // analysis here, and it keeps the predecessor count honest for "sole
        // predecessor" checks.
        int most = 0;
        foreach (Block b in f.Blocks)
            if (b.Terminator is { } end) most += end.Targets.Count + (end.Default is null ? 0 : 1);
        _succEdges = most == 0 ? Array.Empty<Block>() : Take(spare?._succEdges, most, clear: false);
        _succStart = Take(spare?._succStart, count + 1, clear: false);
        int[] incoming = new int[count + 1];
        int edges = 0;
        for (int k = 0; k < count; k++)
        {
            _succStart[k] = edges;
            if (f.Blocks[k].Terminator is not { } end) continue;
            int targets = end.Targets.Count;
            for (int t = 0; t <= targets; t++)
            {
                Block? s = t < targets ? end.Targets[t] : end.Default;
                if (s is null) continue;
                bool seen = false;
                for (int e = _succStart[k]; e < edges && !seen; e++) seen = ReferenceEquals(_succEdges[e], s);
                if (seen) continue;
                _succEdges[edges++] = s;
                incoming[s.Order]++;
            }
        }
        _succStart[count] = edges;
        _predStart = Take(spare?._predStart, count + 1, clear: false);
        _predStart[0] = 0;
        for (int k = 0; k < count; k++) _predStart[k + 1] = _predStart[k] + incoming[k];
        _predEdges = edges == 0 ? Array.Empty<Block>() : Take(spare?._predEdges, edges, clear: false);
        // Filled in block order, as the lists were: a block's predecessors
        // in the order their branches appear.
        for (int k = 0; k < count; k++) incoming[k] = _predStart[k];
        for (int k = 0; k < count; k++)
            for (int e = _succStart[k]; e < _succStart[k + 1]; e++)
                _predEdges[incoming[_succEdges[e].Order]++] = f.Blocks[k];
    }

    /// <summary>Records a block control can reach other than by a branch.</summary>
    private void Root(Block b)
    {
        if (_root[b.Order]) return;
        _root[b.Order] = true;
        _roots.Add(b);
    }

    /// <summary>What a pass that carries facts down the tree of sole predecessors does at each block (WalkSolePredecessors).</summary>
    public interface IScopedWalk
    {
        /// <summary>A position in the walker's log of changes, to undo back to.</summary>
        int Mark { get; }
        void Visit(Block block);
        void Undo(int mark);
    }

    /// <summary>
    /// THE TREE OF SOLE PREDECESSORS, depth first in reverse postorder: a block
    /// that is not a root and has one predecessor (that, with `dominating`,
    /// also dominates it) is visited right after it, with what the walker
    /// learnt there still in place; leaving a block's subtree undoes what was
    /// learnt in it, so its siblings and every other tree start from what they
    /// should. One set of tables for the function instead of one per block.
    /// </summary>
    public void WalkSolePredecessors(IScopedWalk walker, bool dominating)
    {
        IReadOnlyList<Block> order = ReversePostorder;
        int count = Function.Blocks.Count;
        int[] firstChild = new int[count];
        int[] nextSibling = new int[count];
        bool[] child = new bool[count];
        Array.Fill(firstChild, -1);
        Array.Fill(nextSibling, -1);
        // Linked by prepending in RPO, so each list runs in reverse RPO and,
        // pushed in that order, the children pop in RPO.
        for (int r = 0; r < order.Count; r++)
        {
            Block block = order[r];
            Edges predecessors = Preds(block);
            if (IsRoot(block) || predecessors.Count != 1 || ReferenceEquals(predecessors[0], block)
                || dominating && !Dominates(predecessors[0], block)) continue;
            int parent = predecessors[0].Order;
            child[block.Order] = true;
            nextSibling[block.Order] = firstChild[parent];
            firstChild[parent] = block.Order;
        }
        // A walker kept between walks starts clean even if the last one was
        // abandoned part way: everything it logged is undone.
        walker.Undo(0);
        Stack<(int Block, int Mark)> walk = new();
        foreach (Block root in order)
        {
            if (child[root.Order]) continue;
            walk.Push((root.Order, 0));
            while (walk.Count > 0)
            {
                (int at, int mark) = walk.Pop();
                if (at < 0)
                {
                    walker.Undo(mark);
                    continue;
                }
                // Undone once the subtree is done: pushed under its children.
                walk.Push((-1, walker.Mark));
                walker.Visit(Function.Blocks[at]);
                for (int c = firstChild[at]; c >= 0; c = nextSibling[c]) walk.Push((c, 0));
            }
        }
    }

    public Edges Preds(Block b) => new(_predEdges, _predStart[b.Order], _predStart[b.Order + 1] - _predStart[b.Order]);
    public Edges Succs(Block b) => new(_succEdges, _succStart[b.Order], _succStart[b.Order + 1] - _succStart[b.Order]);

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
                List<Block> post = _spare?._rpo is { } kept ? kept : new(Function.Blocks.Count);
                post.Clear();
                bool[] seen = new bool[Function.Blocks.Count];
                Stack<(Block, int)> stack = new();
                // The entry goes first so it ends up last in postorder, ahead
                // of every pad, and the pads follow in block order so the
                // result is deterministic.
                foreach (Block root in Function.Blocks)
                {
                    if (IsRoot(root)) Postorder(root, seen, post, stack);
                }
                post.Reverse();
                _rpo = post;
            }
            return _rpo;
        }
    }

    private void Postorder(Block start, bool[] seen, List<Block> post, Stack<(Block, int)> stack)
    {
        // Iterative, because lowering can emit a chain of thousands of
        // blocks for a long method and recursion would overflow the stack.
        // Seen by position, as the edges are kept (one array, not a set).
        if (seen[start.Order])
        {
            return;
        }
        seen[start.Order] = true;
        stack.Push((start, 0));
        while (stack.Count > 0)
        {
            (Block b, int k) = stack.Pop();
            Edges succs = Succs(b);
            if (k < succs.Count)
            {
                stack.Push((b, k + 1));
                if (!seen[succs[k].Order])
                {
                    seen[succs[k].Order] = true;
                    stack.Push((succs[k], 0));
                }
            }
            else
            {
                post.Add(b);
            }
        }
    }

    /// <summary>The blocks reachable from a root, by position.</summary>
    public bool[] Live
    {
        get
        {
            if (_live is null)
            {
                _live = new bool[Function.Blocks.Count];
                foreach (Block b in ReversePostorder) _live[b.Order] = true;
            }
            return _live;
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

    private int Words => _words != 0 ? _words : _words = Math.Max(1, (Function.Blocks.Count + 63) >> 6);

    /// <summary>
    /// Whether there is a path of one or more edges from <paramref name="from"/>
    /// to <paramref name="to"/>. Cached per source, a row of bits each; a
    /// function with n blocks costs at most n such walks, which the
    /// propagation passes stay well under because they only ask about
    /// definition blocks.
    /// </summary>
    public bool Reaches(Block from, Block to)
    {
        int n = Function.Blocks.Count, words = Words;
        _reachBits ??= Take(_spare?._reachBits, n * words, clear: true);
        _reachDone ??= Take(_spare?._reachDone, n, clear: true);
        int row = from.Order * words;
        if (!_reachDone[from.Order])
        {
            _reachDone[from.Order] = true;
            Stack<Block> work = _walk ??= _spare?._walk ?? new Stack<Block>();
            work.Clear();
            foreach (Block s0 in Succs(from)) work.Push(s0);
            while (work.Count > 0)
            {
                Block b = work.Pop();
                int at = row + (b.Order >> 6);
                ulong bit = 1UL << (b.Order & 63);
                if ((_reachBits[at] & bit) != 0) continue;
                _reachBits[at] |= bit;
                foreach (Block s1 in Succs(b)) work.Push(s1);
            }
        }
        return (_reachBits[row + (to.Order >> 6)] & (1UL << (to.Order & 63))) != 0;
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
    /// would mostly miss; the walk marks blocks with a stamp, so it makes
    /// nothing either.
    /// </summary>
    public bool ReachesWithoutReentering(Block from, Block to)
    {
        if (ReferenceEquals(from, to))
        {
            return false;
        }
        int[] mark = _mark ??= Take(_spare?._mark, Function.Blocks.Count, clear: true);
        int stamp = ++_stamp;
        mark[from.Order] = stamp;
        Stack<Block> work = _walk ??= _spare?._walk ?? new Stack<Block>();
        work.Clear();
        foreach (Block s0 in Succs(from)) work.Push(s0);
        while (work.Count > 0)
        {
            Block b = work.Pop();
            if (ReferenceEquals(b, to))
            {
                work.Clear();
                return true;
            }
            if (mark[b.Order] != stamp)
            {
                mark[b.Order] = stamp;
                foreach (Block s1 in Succs(b))
                {
                    work.Push(s1);
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
        return (_dom[b.Order * Words + (ia >> 6)] & (1UL << (ia & 63))) != 0;
    }

    private ulong[] ComputeDominators()
    {
        int n = Function.Blocks.Count;
        int words = Words;
        // Every row written below before any is read.
        ulong[] dom = Take(_spare?._dom, n * words, clear: false);
        ulong[] all = new ulong[words];
        for (int k = 0; k < n; k++)
        {
            all[k >> 6] |= 1UL << (k & 63);
        }
        for (int k = 0; k < n; k++)
        {
            Array.Copy(all, 0, dom, k * words, words);
        }
        foreach (Block root in _roots)
        {
            int r = root.Order;
            Array.Clear(dom, r * words, words);
            dom[r * words + (r >> 6)] |= 1UL << (r & 63);
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
                    int dp = p.Order * words;
                    for (int w = 0; w < words; w++)
                    {
                        tmp[w] &= dom[dp + w];
                    }
                }
                tmp[ib >> 6] |= 1UL << (ib & 63);
                int db = ib * words;
                for (int w = 0; w < words; w++)
                {
                    if (dom[db + w] != tmp[w])
                    {
                        dom[db + w] = tmp[w];
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
        bool[] live = cfg.Live;
        if (cfg.ReversePostorder.Count == f.Blocks.Count)
        {
            return false;
        }
        foreach (Block dead in f.Blocks)
        {
            if (live[dead.Order])
            {
                continue;
            }
            // A surviving successor loses a predecessor; its phis must not
            // go on naming it.
            foreach (Block s in cfg.Succs(dead))
            {
                if (live[s.Order])
                {
                    Phi.RemoveIncoming(s, dead);
                }
            }
        }
        // By position, as the graph saw them (RemoveAll would ask a closure).
        int kept = 0;
        for (int k = 0; k < f.Blocks.Count; k++)
            if (live[f.Blocks[k].Order]) f.Blocks[kept++] = f.Blocks[k];
        f.Blocks.RemoveRange(kept, f.Blocks.Count - kept);
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
        return _idom![b.Order];
    }

    /// <summary>The blocks whose immediate dominator this is, in block order.</summary>
    public Edges DomChildren(Block b)
    {
        BuildTree();
        return new Edges(_children!, _childStart![b.Order], _childStart[b.Order + 1] - _childStart[b.Order]);
    }

    /// <summary>
    /// The dominance frontier: blocks with a predecessor this dominates
    /// that are not themselves strictly dominated by it. Where SSA puts
    /// phis. Cooper, Harvey and Kennedy's walk up the dominator tree from
    /// each join block's predecessors. A set only for a block that has one.
    /// </summary>
    public IReadOnlySet<Block> Frontier(Block b)
    {
        if (_frontier is null)
        {
            BuildTree();
            _frontier = Take(_spare?._frontier, Function.Blocks.Count, clear: true);
            bool[] live = Live;
            foreach (Block join in Function.Blocks)
            {
                if (Preds(join).Count < 2 || !live[join.Order])
                {
                    continue;
                }
                Block? stop = _idom![join.Order];
                foreach (Block p in Preds(join))
                {
                    if (!live[p.Order])
                    {
                        continue;
                    }
                    Block? runner = p;
                    while (runner is not null && !ReferenceEquals(runner, stop))
                    {
                        (_frontier[runner.Order] ??= new HashSet<Block>(ReferenceEqualityComparer.Instance)).Add(join);
                        runner = _idom[runner.Order];
                    }
                }
            }
        }
        return _frontier[b.Order] ?? NoFrontier;
    }

    private void BuildTree()
    {
        if (_idom is not null)
        {
            return;
        }
        _dom ??= ComputeDominators();
        int n = Function.Blocks.Count, words = Words;
        // Every slot written below.
        _idom = Take(_spare?._idom, n, clear: false);
        bool[] live = Live;
        int[] size = new int[n];
        for (int k = 0; k < n; k++)
        {
            for (int w = 0; w < words; w++)
            {
                size[k] += System.Numerics.BitOperations.PopCount(_dom[k * words + w]);
            }
        }
        int[] childCount = new int[n + 1];
        foreach (Block b in Function.Blocks)
        {
            Block? best = null;
            if (live[b.Order] && !IsRoot(b))
            {
                int ib = b.Order;
                foreach (Block d in Function.Blocks)
                {
                    int id = d.Order;
                    if (id != ib && (_dom[ib * words + (id >> 6)] & (1UL << (id & 63))) != 0
                        && (best is null || size[id] > size[best.Order]))
                    {
                        best = d;
                    }
                }
            }
            _idom[b.Order] = best;
            if (best is not null) childCount[best.Order]++;
        }
        // The tree's children as the edges are kept: one array, a run a
        // block, in block order.
        _childStart = Take(_spare?._childStart, n + 1, clear: false);
        _childStart[0] = 0;
        for (int k = 0; k < n; k++) _childStart[k + 1] = _childStart[k] + childCount[k];
        _children = _childStart[n] == 0 ? Array.Empty<Block>() : Take(_spare?._children, _childStart[n], clear: false);
        int[] at = new int[n];
        for (int k = 0; k < n; k++) at[k] = _childStart[k];
        foreach (Block b in Function.Blocks)
            if (_idom[b.Order] is { } parent) _children[at[parent.Order]++] = b;
    }
}

/// <summary>
/// One block's run of a graph's edges (Cfg.Preds, Cfg.Succs): a view, made
/// where it is asked for and walked by foreach without an enumerator object.
/// </summary>
public readonly struct Edges : IReadOnlyList<Block>
{
    private readonly Block[] _all;
    private readonly int _start;
    public int Count { get; }

    internal Edges(Block[] all, int start, int count) { _all = all; _start = start; Count = count; }

    public Block this[int index] => (uint)index < (uint)Count ? _all[_start + index] : throw new ArgumentOutOfRangeException(nameof(index));

    public bool Contains(Block b)
    {
        for (int k = 0; k < Count; k++)
            if (ReferenceEquals(_all[_start + k], b)) return true;
        return false;
    }

    public Enumerator GetEnumerator() => new(this);
    // Behind an interface (LINQ, a set made from the edges): one object over
    // the run, not a copy of it as well.
    IEnumerator<Block> IEnumerable<Block>.GetEnumerator() => new Boxed(_all, _start, Count);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new Boxed(_all, _start, Count);

    private sealed class Boxed : IEnumerator<Block>
    {
        private readonly Block[] _all;
        private readonly int _start, _count;
        private int _at = -1;
        public Boxed(Block[] all, int start, int count) { _all = all; _start = start; _count = count; }
        public Block Current => _all[_start + _at];
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++_at < _count;
        public void Reset() => _at = -1;
        public void Dispose() { }
    }

    public Block[] ToArray()
    {
        Block[] copy = new Block[Count];
        Array.Copy(_all, _start, copy, 0, Count);
        return copy;
    }

    public struct Enumerator
    {
        private readonly Edges _edges;
        private int _at;
        internal Enumerator(Edges edges) { _edges = edges; _at = -1; }
        public Block Current => _edges._all[_edges._start + _at];
        public bool MoveNext() => ++_at < _edges.Count;
    }
}
