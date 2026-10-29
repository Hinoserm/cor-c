#nullable enable

namespace Corsac.Lang.RegAlloc;

/// <summary>
/// A VALUE SPLIT AT EVERY LOOP THAT USES IT, before the scan, on any machine:
/// in the loop it is a register of its own, copied from the value in a
/// preheader every way in goes through, and copied back on every way out
/// where the value is wanted after. An interval is one place from its spill
/// point to its end, so a value the scan gave up before a loop -- in a short
/// crowd of temporaries ahead of it -- was read from the frame, and written
/// back to it, every lap. Split, the loop's piece is allocated on the loop's
/// own pressure: where the scan puts both pieces in one register the copies
/// vanish (a move onto itself goes), where it spills the outside piece and
/// not the loop's the copy in is the one reload, and the copy out the one
/// store (Braun and Hack's coupling code, GCC IRA's regions, LLVM's region
/// split, V8's splinters).
///
/// The pieces of one value share its spill slot (Pieces): they are never
/// live at once but across the copy itself, so a copy between two spilled
/// pieces is no work at all (Allocator's Decisions drop it).
///
/// IN ROUNDS, INNERMOST FIRST: each round splits every loop with no unsplit
/// loop inside it -- natural loops with different heads are nested or apart,
/// so these are apart, and one liveness serves them all -- then the loops
/// found again, the new blocks now inside the ones around them, and the next
/// ring out split, its pieces copying into and out of the inner ones. The
/// liveness is the allocator's kind: only values some block reads before it
/// writes them, the rest being live into no block at all.
/// Not a loop entered other than through its head, nor one headed by the
/// entry block, nor one with a block whose address is taken or a jump to an
/// address (left by no edge the split could see), nor one holding a landing
/// pad; nor a value live into any landing pad -- entered from anywhere in
/// its try, it finds what the frame holds -- nor a constant, which the
/// allocator remakes where it is read.
/// </summary>
public sealed class LoopSplit
{
    /// <summary>Each piece's original value; an original is its own, and not listed.</summary>
    public readonly Dictionary<int, int> Root = new();
    /// <summary>Each split value's pieces, the original among them, by the original.</summary>
    public readonly Dictionary<int, List<int>> Pieces = new();
    /// <summary>The copies between pieces, into a loop and out of it.</summary>
    public readonly HashSet<AInstr> Copies = new(ReferenceEqualityComparer.Instance);

    public int RootOf(int v) => Root.TryGetValue(v, out int r) ? r : v;

    private readonly AFunction _f;
    private readonly List<ABlock> _succ = new();
    private readonly Dictionary<HashSet<ABlock>, ABlock> _heads = new(ReferenceEqualityComparer.Instance);
    /// <summary>The values whose one definition is a constant (Constants), found once: no split makes another.</summary>
    private BitSet _constant = new(0);
    private int _constantWidth;

    private LoopSplit(AFunction f) => _f = f;

    /// <summary>Splits the function's values at its loops; null where nothing was split.</summary>
    public static LoopSplit? Run(AFunction f)
    {
        foreach (ABlock b in f.Blocks)
        {
            if (b.AddressTaken) return null;
            foreach (AInstr i in b.Instrs) if (i.JumpsAnywhere) return null;
        }
        LoopSplit s = new(f);
        s._constant = s.Constants(s._constantWidth = f.NextVReg);
        HashSet<ABlock> done = new(ReferenceEqualityComparer.Instance);
        while (true)
        {
            List<HashSet<ABlock>> loops = s.FindLoops();
            // Each unsplit loop holding no unsplit loop: apart from each other.
            List<HashSet<ABlock>> ring = loops.Where(l => !done.Contains(s._heads[l])
                && !loops.Any(o => !ReferenceEquals(o, l) && !done.Contains(s._heads[o]) && l.Contains(s._heads[o]) && o.Count < l.Count)).ToList();
            if (ring.Count == 0) break;
            Liveness live = s.LiveIn();
            s.JumpsInto(live);
            s.Link();
            foreach (HashSet<ABlock> body in ring)
            {
                done.Add(s._heads[body]);
                s.Split(body, live);
            }
            s.Unlink();
        }
        return s.Copies.Count == 0 ? null : s;
    }

    // ---- the layout during a round: linked, so each split's blocks go in at
    // once, and the list made again once the round is done -------------------

    /// <summary>Each block's neighbours in the layout, while a round splits (Link to Unlink).</summary>
    private readonly Dictionary<ABlock, ABlock?> _next = new(ReferenceEqualityComparer.Instance), _prev = new(ReferenceEqualityComparer.Instance);
    private ABlock? _lastBlock;

    private void Link()
    {
        _next.Clear();
        _prev.Clear();
        ABlock? before = null;
        foreach (ABlock b in _f.Blocks)
        {
            _prev[b] = before;
            if (before is not null) _next[before] = b;
            before = b;
        }
        if (before is not null) _next[before] = null;
        _lastBlock = before;
    }

    private void Unlink()
    {
        ABlock? first = _f.Blocks.Count > 0 ? _f.Blocks[0] : null;
        _f.Blocks.Clear();
        for (ABlock? b = first; b is not null; b = _next[b]) _f.Blocks.Add(b);
    }

    /// <summary>`b` laid out just before `at` (never before the entry: no loop is headed by it).</summary>
    private void InsertBefore(ABlock at, ABlock b)
    {
        ABlock? p = _prev[at] ?? throw new InvalidOperationException($"{_f.Name}: a block put before the entry ({at.Name})");
        _prev[b] = p;
        _next[b] = at;
        _prev[at] = b;
        if (p is not null) _next[p] = b;
    }

    /// <summary>`b` laid out just after `at`, or last.</summary>
    private void InsertAfter(ABlock? at, ABlock b)
    {
        at ??= _lastBlock!;
        ABlock? n = _next[at];
        _next[at] = b;
        _prev[b] = at;
        _next[b] = n;
        if (n is not null) _prev[n] = b;
        else _lastBlock = b;
    }

    /// <summary>A block's successors in the linked layout: its jumps' targets, then the next block where it falls through.</summary>
    private void SuccessorsLinked(ABlock b, List<ABlock> into)
    {
        into.Clear();
        foreach (AInstr i in b.Instrs)
            if (i.Targets is { } targets) into.AddRange(targets);
        if (!b.EndsUnconditionally && _next[b] is { } n) into.Add(n);
    }

    private Dictionary<ABlock, int> Index()
    {
        Dictionary<ABlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < _f.Blocks.Count; b++) index[_f.Blocks[b]] = b;
        return index;
    }

    /// <summary>The natural loops entered only through their heads, a set of blocks each, every back edge to one head together.</summary>
    private List<HashSet<ABlock>> FindLoops()
    {
        _heads.Clear();
        int nb = _f.Blocks.Count;
        Dictionary<ABlock, int> index = Index();
        List<int>[] succ = new List<int>[nb], pred = new List<int>[nb];
        for (int b = 0; b < nb; b++) { succ[b] = new(); pred[b] = new(); }
        for (int b = 0; b < nb; b++)
        {
            _f.SuccessorsInto(b, _succ);
            foreach (ABlock t in _succ) { succ[b].Add(index[t]); pred[index[t]].Add(b); }
        }
        byte[] state = new byte[nb];
        int[] next = new int[nb];
        Stack<int> stack = new();
        List<(int Tail, int Head)> backs = new();
        stack.Push(0); state[0] = 1;
        while (stack.Count > 0)
        {
            int b = stack.Peek();
            if (next[b] < succ[b].Count)
            {
                int t = succ[b][next[b]++];
                if (state[t] == 1) backs.Add((b, t));
                else if (state[t] == 0) { state[t] = 1; stack.Push(t); }
            }
            else { state[b] = 2; stack.Pop(); }
        }
        Dictionary<int, HashSet<int>> byHead = new();
        foreach ((int tail, int head) in backs)
        {
            if (!byHead.TryGetValue(head, out HashSet<int>? body)) byHead[head] = body = new() { head };
            Stack<int> work = new();
            if (body.Add(tail)) work.Push(tail);
            while (work.Count > 0)
                foreach (int p in pred[work.Pop()])
                    if (state[p] != 0 && body.Add(p)) work.Push(p);
        }
        List<HashSet<ABlock>> loops = new();
        foreach ((int head, HashSet<int> body) in byHead)
        {
            if (head == 0) continue;
            bool single = true;
            foreach (int b in body)
                if (b != head)
                    foreach (int p in pred[b])
                        if (state[p] != 0 && !body.Contains(p)) single = false;
            if (!single) continue;
            HashSet<ABlock> blocks = new(ReferenceEqualityComparer.Instance);
            foreach (int b in body) blocks.Add(_f.Blocks[b]);
            _heads[blocks] = _f.Blocks[head];
            loops.Add(blocks);
        }
        return loops;
    }

    /// <summary>
    /// What is live into each block, over only the values some block reads
    /// before it writes them (Globals; a value every block writes before it
    /// reads is live into none), each block's row in one array (Rows).
    /// </summary>
    private sealed class Liveness
    {
        public required Dictionary<ABlock, int> Index;
        public required int[] GlobalOf;
        public required List<int> Globals;
        public required BitRows Rows;
        /// <summary>
        /// A preheader made this round stands for its head: what goes into it
        /// goes into the head, the copies it makes reading values live there.
        /// (A loop's exits are reached from that loop alone, apart from the
        /// others this round.)
        /// </summary>
        public Dictionary<ABlock, ABlock> StandsFor { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>The blocks whose jumps name each block, kept as this round's splits retarget and add them.</summary>
        public Dictionary<ABlock, HashSet<ABlock>> JumpsFrom { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>The values live into any landing pad, left alone: entered from anywhere in its try, a pad finds what the frame holds.</summary>
        public HashSet<int> PadLive { get; } = new();
        public void Jumps(ABlock from, ABlock to)
        {
            if (!JumpsFrom.TryGetValue(to, out HashSet<ABlock>? set)) JumpsFrom[to] = set = new(ReferenceEqualityComparer.Instance);
            set.Add(from);
        }
        public bool LiveInto(ABlock b, int v)
        {
            if (StandsFor.TryGetValue(b, out ABlock? head)) b = head;
            return (uint)v < (uint)GlobalOf.Length && GlobalOf[v] >= 0 && Rows.Get(Index[b], GlobalOf[v]);
        }
    }

    private Liveness LiveIn()
    {
        int nb = _f.Blocks.Count, n = _f.NextVReg, fv = _f.Registers.FirstVirtual;
        Dictionary<ABlock, int> index = Index();
        int[] globalOf = new int[n];
        Array.Fill(globalOf, -1);
        int[] lastDef = new int[n];
        Array.Fill(lastDef, -1);
        List<int> globals = new();
        for (int b = 0; b < nb; b++)
            foreach (AInstr i in _f.Blocks[b].Instrs)
            {
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && (r.Role & Role.Use) != 0 && lastDef[r.Reg] != b && globalOf[r.Reg] < 0)
                    {
                        globalOf[r.Reg] = globals.Count;
                        globals.Add(r.Reg);
                    }
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && (r.Role & Role.Def) != 0) lastDef[r.Reg] = b;
            }
        int g = globals.Count;
        // The thread's kept rows, the allocator's own (BitRows.Kept): its
        // liveness comes after the split and fills them afresh, so the split's
        // three tables a round cost no memory of their own.
        BitRows use = BitRows.Kept(0, nb, g), def = BitRows.Kept(1, nb, g), live = BitRows.Kept(2, nb, g);
        int[][] succ = new int[nb][];
        List<int>[] pred = new List<int>[nb];
        for (int b = 0; b < nb; b++) pred[b] = new();
        for (int b = 0; b < nb; b++)
        {
            foreach (AInstr i in _f.Blocks[b].Instrs)
            {
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && (r.Role & Role.Use) != 0 && globalOf[r.Reg] is >= 0 and var at && !def.Get(b, at)) use.Set(b, at);
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && (r.Role & Role.Def) != 0 && globalOf[r.Reg] is >= 0 and var at) def.Set(b, at);
            }
            _f.SuccessorsInto(b, _succ);
            succ[b] = _succ.Select(t => index[t]).ToArray();
            foreach (int t in succ[b]) pred[t].Add(b);
        }
        // live-in(b) = use(b) | (live-out(b) & ~def(b)), to the least fixed
        // point by a worklist: a block again only when a successor changed.
        BitSet row = new(g), into = new(g);
        Stack<int> work = new();
        bool[] owed = new bool[nb];
        for (int b = 0; b < nb; b++) { work.Push(b); owed[b] = true; }
        while (work.Count > 0)
        {
            int b = work.Pop();
            owed[b] = false;
            row.Clear();
            foreach (int t in succ[b]) row.Or(live, t);
            row.AndNot(def, b);
            row.Or(use, b);
            if (live.RowEquals(b, row)) continue;
            live.CopyRow(b, row);
            foreach (int p in pred[b]) if (!owed[p]) { owed[p] = true; work.Push(p); }
        }
        return new Liveness { Index = index, GlobalOf = globalOf, Globals = globals, Rows = live };
    }

    /// <summary>Who jumps where, and what landing pads want, for the round.</summary>
    private void JumpsInto(Liveness live)
    {
        foreach (ABlock b in _f.Blocks)
        {
            foreach (AInstr i in b.Instrs)
                if (i.Targets is { } targets)
                    foreach (ABlock t in targets) live.Jumps(b, t);
            if (b.LandingPad)
                foreach (int v in live.Globals) if (live.LiveInto(b, v)) live.PadLive.Add(v);
        }
    }

    private bool Split(HashSet<ABlock> body, Liveness live)
    {
        ABlock head = _heads[body];
        foreach (ABlock b in body) if (b.LandingPad) return false;
        int fv = _f.Registers.FirstVirtual;
        int width = _f.NextVReg;
        // What the loop reads or writes.
        BitSet touched = new(width);
        foreach (ABlock b in body)
            foreach (AInstr i in b.Instrs)
                foreach (Named named in i.Regs)
                    if (named.Reg >= fv) touched.Set(named.Reg);
        List<int> values = new();
        foreach (int v in live.Globals)
            if (v < width && live.LiveInto(head, v) && touched.Get(v) && !live.PadLive.Contains(v) && !(v < _constantWidth && _constant.Get(v))) values.Add(v);
        values.Sort();
        if (values.Count == 0) return false;

        int line = head.Instrs.Count > 0 ? head.Instrs[0].Line : 0;
        Dictionary<int, int> renamed = new();
        ABlock pre = new() { Name = head.Name + "$pre" };
        foreach (int v in values)
        {
            int piece = _f.Target.NewVRegLike(v);
            _f.NextVReg = Math.Max(_f.NextVReg, piece + 1);
            renamed[v] = piece;
            int root = RootOf(v);
            Root[piece] = root;
            if (!Pieces.TryGetValue(root, out List<int>? all)) Pieces[root] = all = new() { root };
            all.Add(piece);
            AInstr copy = MakeCopy(piece, v, line);
            pre.Instrs.Add(copy);
            Copies.Add(copy);
        }

        // The ways out, found before anything moves: each edge from the loop
        // to a block outside it where some value split here is live.
        List<(ABlock From, ABlock To, List<int> Live)> exits = new();
        foreach (ABlock b in body)
        {
            SuccessorsLinked(b, _succ);
            foreach (ABlock t in _succ.Distinct(ReferenceEqualityComparer.Instance).Cast<ABlock>())
            {
                if (body.Contains(t)) continue;
                List<int> wanted = values.Where(v => live.LiveInto(t, v)).ToList();
                if (wanted.Count > 0) exits.Add((b, t, wanted));
            }
        }

        // Every way in from outside goes through the preheader, which falls
        // into the head; a loop block that fell into the head jumps to it.
        ABlock? before = _prev[head];
        if (before is not null && body.Contains(before) && !before.EndsUnconditionally)
            before.Instrs.Add(MakeJump(head, before.Instrs.Count > 0 ? before.Instrs[^1].Line : line));
        if (live.JumpsFrom.TryGetValue(head, out HashSet<ABlock>? into))
            foreach (ABlock b in into.ToList())
                if (!body.Contains(b))
                {
                    Retarget(b, head, pre);
                    live.Jumps(b, pre);
                }
        InsertBefore(head, pre);
        live.StandsFor[pre] = head;

        // The loop reads and writes its own pieces.
        foreach (ABlock b in body)
            foreach (AInstr i in b.Instrs)
                Rename(i, renamed);

        // Each way out copies back what is wanted there, in a block of its
        // own on the edge: after the block it leaves where that one fell
        // through to it, else at the end, jumping on.
        int n = 0;
        foreach ((ABlock from, ABlock to, List<int> wanted) in exits)
        {
            ABlock exit = new() { Name = from.Name + "$out" + n++ };
            int exitLine = from.Instrs.Count > 0 ? from.Instrs[^1].Line : line;
            foreach (int v in wanted)
            {
                AInstr copy = MakeCopy(v, renamed[v], exitLine);
                exit.Instrs.Add(copy);
                Copies.Add(copy);
            }
            exit.Instrs.Add(MakeJump(to, exitLine));
            live.Jumps(exit, to);
            bool fallsInto = !from.EndsUnconditionally && ReferenceEquals(_next[from], to);
            Retarget(from, to, exit);
            InsertAfter(fallsInto ? from : null, exit);
        }
        return true;
    }

    /// <summary>A copy of a whole register into another of its class, made by the target when the function is rebuilt (Made.Copy).</summary>
    private static AInstr MakeCopy(int dst, int src, int line) => new()
    {
        Regs = new[] { new Named(dst, Role.Def, 0, false), new Named(src, Role.Use, 1, false) },
        OperandCount = 2, IsCopy = true, WholeCopy = true, DropsSelf = true, MayMemory = 3, MayMemoryWrite = 3, Line = line, Made = Made.Copy,
    };

    private static AInstr MakeJump(ABlock to, int line) => new()
    {
        Regs = Array.Empty<Named>(), Targets = new() { to }, Unconditional = true, Line = line, Made = Made.Jump,
    };

    /// <summary>Every jump and table entry of a block to `from` made to `to`.</summary>
    private static void Retarget(ABlock b, ABlock from, ABlock to)
    {
        foreach (AInstr i in b.Instrs)
            if (i.Targets is { } targets)
                for (int k = 0; k < targets.Count; k++)
                    if (ReferenceEquals(targets[k], from)) targets[k] = to;
    }

    /// <summary>An instruction's registers renamed, in operands and memory operands alike.</summary>
    internal static void Rename(AInstr i, IReadOnlyDictionary<int, int> renamed)
    {
        Named[] regs = i.Regs;
        Named[]? made = null;
        for (int k = 0; k < regs.Length; k++)
        {
            if (!renamed.TryGetValue(regs[k].Reg, out int to)) continue;
            made ??= (Named[])regs.Clone();
            made[k] = regs[k] with { Reg = to };
        }
        if (made is not null) i.Regs = made;
    }

    /// <summary>The values whose one definition is a constant: remade where they are wanted, never split.</summary>
    private BitSet Constants(int width)
    {
        int fv = _f.Registers.FirstVirtual;
        int[] defs = new int[width];
        BitSet constant = new(width);
        foreach (ABlock b in _f.Blocks)
            foreach (AInstr i in b.Instrs)
                foreach (Named n in i.Regs)
                {
                    if (n.Reg < fv || (n.Role & Role.Def) == 0) continue;
                    defs[n.Reg]++;
                    if (i.Constant is not null && n.Operand == 0) constant.Set(n.Reg);
                }
        for (int v = 0; v < width; v++) if (defs[v] != 1) constant.Clear(v);
        return constant;
    }
}
