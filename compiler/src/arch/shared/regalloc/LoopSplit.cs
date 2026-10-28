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
/// Outermost loop first, its pieces split again at the loops inside it.
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

    private LoopSplit(AFunction f) => _f = f;

    /// <summary>Splits the function's values at its loops; null where nothing was split.</summary>
    public static LoopSplit? Run(AFunction f, int maxLoops = 64)
    {
        foreach (ABlock b in f.Blocks)
        {
            if (b.AddressTaken) return null;
            foreach (AInstr i in b.Instrs) if (i.JumpsAnywhere) return null;
        }
        LoopSplit s = new(f);
        List<HashSet<ABlock>> loops = s.FindLoops();
        if (loops.Count == 0) return null;
        // Outermost first: a piece split at a loop is split again at the loops inside it.
        loops.Sort((x, y) => y.Count.CompareTo(x.Count));
        int done = 0;
        foreach (HashSet<ABlock> body in loops)
        {
            if (done == maxLoops) break;
            if (s.Split(body)) done++;
        }
        return s.Copies.Count == 0 ? null : s;
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

    /// <summary>Each block's virtual registers live on entry, the function as it is now.</summary>
    private BitSet[] LiveIn(out int width)
    {
        int nb = _f.Blocks.Count;
        width = _f.NextVReg;
        int fv = _f.Registers.FirstVirtual;
        Dictionary<ABlock, int> index = Index();
        BitSet[] use = new BitSet[nb], def = new BitSet[nb], liveIn = new BitSet[nb];
        int[][] succ = new int[nb][];
        for (int b = 0; b < nb; b++)
        {
            use[b] = new BitSet(width); def[b] = new BitSet(width); liveIn[b] = new BitSet(width);
            foreach (AInstr i in _f.Blocks[b].Instrs)
            {
                foreach (Named n in i.Regs)
                    if (n.Reg >= fv && (n.Role & Role.Use) != 0 && !def[b].Get(n.Reg)) use[b].Set(n.Reg);
                foreach (Named n in i.Regs)
                    if (n.Reg >= fv && (n.Role & Role.Def) != 0) def[b].Set(n.Reg);
            }
            _f.SuccessorsInto(b, _succ);
            succ[b] = _succ.Select(t => index[t]).ToArray();
        }
        BitSet outSet = new(width);
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int b = nb - 1; b >= 0; b--)
            {
                outSet.Clear();
                foreach (int t in succ[b]) outSet.Or(liveIn[t]);
                outSet.AndNot(def[b]);
                outSet.Or(use[b]);
                if (!outSet.Equals(liveIn[b])) { liveIn[b].CopyFrom(outSet); changed = true; }
            }
        }
        return liveIn;
    }

    private bool Split(HashSet<ABlock> body)
    {
        ABlock head = _heads[body];
        foreach (ABlock b in body) if (b.LandingPad) return false;
        int fv = _f.Registers.FirstVirtual;
        BitSet[] liveIn = LiveIn(out int width);
        Dictionary<ABlock, int> index = Index();
        BitSet padLive = new(width);
        for (int b = 0; b < _f.Blocks.Count; b++)
            if (_f.Blocks[b].LandingPad) padLive.Or(liveIn[b]);

        // What the loop reads or writes, and which values are constants.
        BitSet constant = Constants(width);
        BitSet touched = new(width);
        foreach (ABlock b in body)
            foreach (AInstr i in b.Instrs)
                foreach (Named named in i.Regs)
                    if (named.Reg >= fv) touched.Set(named.Reg);
        List<int> values = new();
        BitSet headIn = liveIn[index[head]];
        for (int v = fv; v < width; v++)
            if (headIn.Get(v) && touched.Get(v) && !padLive.Get(v) && !constant.Get(v)) values.Add(v);
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
            _f.SuccessorsInto(index[b], _succ);
            foreach (ABlock t in _succ.Distinct(ReferenceEqualityComparer.Instance).Cast<ABlock>())
            {
                if (body.Contains(t)) continue;
                List<int> live = values.Where(v => liveIn[index[t]].Get(v)).ToList();
                if (live.Count > 0) exits.Add((b, t, live));
            }
        }

        // Every way in from outside goes through the preheader, which falls
        // into the head; a loop block that fell into the head jumps to it.
        int at = _f.Blocks.IndexOf(head);
        ABlock? before = at > 0 ? _f.Blocks[at - 1] : null;
        if (before is not null && body.Contains(before) && !before.EndsUnconditionally)
            before.Instrs.Add(MakeJump(head, before.Instrs.Count > 0 ? before.Instrs[^1].Line : line));
        foreach (ABlock b in _f.Blocks)
            if (!body.Contains(b)) Retarget(b, head, pre);
        _f.Blocks.Insert(at, pre);

        // The loop reads and writes its own pieces.
        foreach (ABlock b in body)
            foreach (AInstr i in b.Instrs)
                Rename(i, renamed);

        // Each way out copies back what is wanted there, in a block of its
        // own on the edge: after the block it leaves where that one fell
        // through to it, else at the end, jumping on.
        int n = 0;
        foreach ((ABlock from, ABlock to, List<int> live) in exits)
        {
            ABlock exit = new() { Name = from.Name + "$out" + n++ };
            int exitLine = from.Instrs.Count > 0 ? from.Instrs[^1].Line : line;
            foreach (int v in live)
            {
                AInstr copy = MakeCopy(v, renamed[v], exitLine);
                exit.Instrs.Add(copy);
                Copies.Add(copy);
            }
            exit.Instrs.Add(MakeJump(to, exitLine));
            int fromAt = _f.Blocks.IndexOf(from);
            bool fallsInto = !from.EndsUnconditionally && fromAt + 1 < _f.Blocks.Count && ReferenceEquals(_f.Blocks[fromAt + 1], to);
            Retarget(from, to, exit);
            if (fallsInto) _f.Blocks.Insert(fromAt + 1, exit);
            else _f.Blocks.Add(exit);
        }
        return true;
    }

    /// <summary>A copy of a whole register into another of its class, made by the target when the function is rebuilt (Made.Copy).</summary>
    private static AInstr MakeCopy(int dst, int src, int line) => new()
    {
        Regs = new[] { new Named(dst, Role.Def, 0, false), new Named(src, Role.Use, 1, false) },
        OperandCount = 2, IsCopy = true, WholeCopy = true, DropsSelf = true, MayMemory = 3, Line = line, Made = Made.Copy,
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
