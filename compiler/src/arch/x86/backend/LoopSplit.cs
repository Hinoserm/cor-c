#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;

namespace Corsac.Lang.X86;

/// <summary>
/// A VALUE SPLIT AT EVERY LOOP THAT USES IT, before the register allocator
/// sees the function: in the loop it is a register of its own, copied from
/// the value in a preheader every way in goes through, and copied back on
/// every way out where the value is wanted after. An interval is one place
/// from its spill point to its end, so a value the scan gave up before a
/// loop -- in a short crowd of temporaries ahead of it -- was read from the
/// frame, and written back to it, every lap. Split, the loop's piece is
/// allocated on the loop's own pressure: where the scan puts both pieces in
/// one register the copies vanish (Rewrite drops a move onto itself), where
/// it spills the outside piece and not the loop's the copy in is the one
/// reload, and the copy out the one store (Braun and Hack's coupling code,
/// LLVM's region split, regalloc2's split hoisted out of a loop).
///
/// The pieces of one value share its spill slot (Root): they are never live
/// at once but across the copy itself, so a copy between two spilled pieces
/// is no work at all, and is dropped (Allocator.Rewrite).
///
/// Outermost loop first, its pieces split again at the loops inside it.
/// Not a loop entered other than through its head, nor one headed by the
/// entry block, nor one whose head's address is taken, nor one holding a
/// landing pad; nor a value live into any landing pad -- entered from
/// anywhere in its try, it finds what the frame holds -- nor a constant,
/// which the allocator remakes where it is read.
/// </summary>
internal sealed class LoopSplit
{
    /// <summary>Each piece's original value; the original is its own and not listed.</summary>
    public readonly Dictionary<int, int> Root = new();
    /// <summary>The copies between pieces, into a loop and out of it.</summary>
    public readonly HashSet<MInstr> Copies = new(ReferenceEqualityComparer.Instance);

    public int RootOf(int v) => Root.TryGetValue(v, out int r) ? r : v;

    private readonly MFunction _m;
    private readonly List<MBlock> _succ = new();
    private readonly List<(MReg Reg, Role Role, int Operand, bool InMem)> _regs = new();

    private LoopSplit(MFunction m) => _m = m;

    /// <summary>Splits the function's values at its loops; null where nothing was split.</summary>
    public static LoopSplit? Run(MFunction m, int maxLoops = 64)
    {
        LoopSplit s = new(m);
        List<HashSet<MBlock>> loops = s.FindLoops();
        if (loops.Count == 0) return null;
        // Outermost first: a piece split at a loop is split again at the loops inside it.
        loops.Sort((x, y) => y.Count.CompareTo(x.Count));
        int done = 0;
        foreach (HashSet<MBlock> body in loops)
        {
            if (done == maxLoops) break;
            if (s.Split(body)) done++;
        }
        return s.Copies.Count == 0 ? null : s;
    }

    private Dictionary<MBlock, int> Index()
    {
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < _m.Blocks.Count; b++) index[_m.Blocks[b]] = b;
        return index;
    }

    /// <summary>The natural loops, a set of blocks each, every back edge to one head together (its head in _heads).</summary>
    private List<HashSet<MBlock>> FindLoops()
    {
        int nb = _m.Blocks.Count;
        Dictionary<MBlock, int> index = Index();
        List<int>[] succ = new List<int>[nb], pred = new List<int>[nb];
        for (int b = 0; b < nb; b++) { succ[b] = new(); pred[b] = new(); }
        for (int b = 0; b < nb; b++)
        {
            _m.SuccessorsInto(b, _succ);
            foreach (MBlock t in _succ) { succ[b].Add(index[t]); pred[index[t]].Add(b); }
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
        List<HashSet<MBlock>> loops = new();
        foreach ((int head, HashSet<int> body) in byHead)
        {
            if (head == 0) continue;
            // Entered only through its head.
            bool single = true;
            foreach (int b in body)
                if (b != head)
                    foreach (int p in pred[b])
                        if (state[p] != 0 && !body.Contains(p)) single = false;
            if (!single) continue;
            HashSet<MBlock> blocks = new(ReferenceEqualityComparer.Instance);
            foreach (int b in body) blocks.Add(_m.Blocks[b]);
            _heads[blocks] = _m.Blocks[head];
            loops.Add(blocks);
        }
        return loops;
    }

    private readonly Dictionary<HashSet<MBlock>, MBlock> _heads = new(ReferenceEqualityComparer.Instance);

    /// <summary>Each block's virtual registers live on entry, the function as it is now.</summary>
    private BitSet[] LiveIn(out int width)
    {
        int nb = _m.Blocks.Count;
        width = _m.NextVReg;
        Dictionary<MBlock, int> index = Index();
        BitSet[] use = new BitSet[nb], def = new BitSet[nb], liveIn = new BitSet[nb];
        int[][] succ = new int[nb][];
        for (int b = 0; b < nb; b++)
        {
            use[b] = new BitSet(width); def[b] = new BitSet(width); liveIn[b] = new BitSet(width);
            foreach (MInstr i in _m.Blocks[b].Instrs)
            {
                foreach ((MReg r, Role role, _, _) in Allocator.RegsOf(i, _regs))
                    if (!r.IsPhys && (role & Role.Use) != 0 && !def[b].Get(r.Id)) use[b].Set(r.Id);
                foreach ((MReg r, Role role, _, _) in Allocator.RegsOf(i, _regs))
                    if (!r.IsPhys && (role & Role.Def) != 0) def[b].Set(r.Id);
            }
            _m.SuccessorsInto(b, _succ);
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
                if (!outSet.Words.AsSpan().SequenceEqual(liveIn[b].Words)) { liveIn[b].CopyFrom(outSet); changed = true; }
            }
        }
        return liveIn;
    }

    private bool Split(HashSet<MBlock> body)
    {
        MBlock head = _heads[body];
        foreach (MBlock b in body) if (b.Source?.IsLandingPad == true) return false;
        // A head whose address is taken is entered by it, not by an edge.
        foreach (MBlock b in _m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach (MOperand o in i.Operands)
                    if (o is MImm { Label: var l } && ReferenceEquals(l, head) || o is MMem { Label: var ml } && ReferenceEquals(ml, head)) return false;

        BitSet[] liveIn = LiveIn(out int width);
        Dictionary<MBlock, int> index = Index();
        BitSet padLive = new(width);
        for (int b = 0; b < _m.Blocks.Count; b++)
            if (_m.Blocks[b].Source?.IsLandingPad == true) padLive.Or(liveIn[b]);

        // What the loop reads or writes, and which values are constants.
        BitSet constant = Constants(width);
        BitSet touched = new(width);
        foreach (MBlock b in body)
            foreach (MInstr i in b.Instrs)
                foreach ((MReg r, _, _, _) in Allocator.RegsOf(i, _regs))
                    if (!r.IsPhys) touched.Set(r.Id);
        List<int> values = new();
        BitSet headIn = liveIn[index[head]];
        for (int v = 8; v < width; v++)
            if (headIn.Get(v) && touched.Get(v) && !padLive.Get(v) && !constant.Get(v)) values.Add(v);
        if (values.Count == 0) return false;

        int line = head.Instrs.Count > 0 ? head.Instrs[0].Line : 0;
        Dictionary<int, int> renamed = new();
        MBlock pre = new(head.Name + "$pre");
        foreach (int v in values)
        {
            int piece = _m.NewReg().Id;
            renamed[v] = piece;
            Root[piece] = RootOf(v);
            if (_m.ByteRegs.Contains(v)) _m.ByteRegs.Add(piece);
            if (_m.WideHalves.Contains(v)) _m.WideHalves.Add(piece);
            MInstr copy = new(MOp.Mov, MReg.Of(piece), MReg.Of(v)) { Line = line };
            pre.Instrs.Add(copy);
            Copies.Add(copy);
        }

        // The ways out, found before anything moves: each edge from the loop
        // to a block outside it where some value split here is live.
        List<(MBlock From, MBlock To, List<int> Live)> exits = new();
        foreach (MBlock b in body)
        {
            _m.SuccessorsInto(index[b], _succ);
            foreach (MBlock t in _succ.Distinct(ReferenceEqualityComparer.Instance).Cast<MBlock>())
            {
                if (body.Contains(t)) continue;
                List<int> live = values.Where(v => liveIn[index[t]].Get(v)).ToList();
                if (live.Count > 0) exits.Add((b, t, live));
            }
        }

        // Every way in from outside goes through the preheader, which falls
        // into the head; a loop block that fell into the head jumps to it.
        int at = _m.Blocks.IndexOf(head);
        MBlock? before = at > 0 ? _m.Blocks[at - 1] : null;
        if (before is not null && body.Contains(before) && !before.EndsUnconditionally)
            before.Instrs.Add(new MInstr(MOp.Jmp, new MLabel(head)) { Line = before.Instrs.Count > 0 ? before.Instrs[^1].Line : line });
        foreach (MBlock b in _m.Blocks)
            if (!body.Contains(b)) Retarget(b, head, pre);
        _m.Blocks.Insert(at, pre);

        // The loop reads and writes its own pieces.
        foreach (MBlock b in body)
            foreach (MInstr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is MReg r && renamed.TryGetValue(r.Id, out int to)) i.Operands[k] = MReg.Of(to);
                    else if (i.Operands[k] is MMem mem && (mem.Base is { } mb && renamed.ContainsKey(mb.Id) || mem.Index is { } mi && renamed.ContainsKey(mi.Id)))
                        i.Operands[k] = new MMem(mem.Base is { } b2 && renamed.TryGetValue(b2.Id, out int nb2) ? MReg.Of(nb2) : mem.Base, mem.Disp)
                        {
                            Index = mem.Index is { } i2 && renamed.TryGetValue(i2.Id, out int ni2) ? MReg.Of(ni2) : mem.Index,
                            Scale = mem.Scale, Symbol = mem.Symbol, Reloc = mem.Reloc, Label = mem.Label, IsSpill = mem.IsSpill, Device = mem.Device,
                        };
                }

        // Each way out copies back what is wanted there, in a block of its
        // own on the edge: after the block it leaves where that one fell
        // through to it, else at the end, jumping on.
        int n = 0;
        foreach ((MBlock from, MBlock to, List<int> live) in exits)
        {
            MBlock exit = new(from.Name + "$out" + n++);
            int exitLine = from.Instrs.Count > 0 ? from.Instrs[^1].Line : line;
            foreach (int v in live)
            {
                MInstr copy = new(MOp.Mov, MReg.Of(v), MReg.Of(renamed[v])) { Line = exitLine };
                exit.Instrs.Add(copy);
                Copies.Add(copy);
            }
            exit.Instrs.Add(new MInstr(MOp.Jmp, new MLabel(to)) { Line = exitLine });
            int fromAt = _m.Blocks.IndexOf(from);
            bool fallsInto = !from.EndsUnconditionally && fromAt + 1 < _m.Blocks.Count && ReferenceEquals(_m.Blocks[fromAt + 1], to);
            Retarget(from, to, exit);
            if (fallsInto) _m.Blocks.Insert(fromAt + 1, exit);
            else _m.Blocks.Add(exit);
        }
        return true;
    }

    /// <summary>Every jump and table entry of a block to `from` made to `to`.</summary>
    private static void Retarget(MBlock b, MBlock from, MBlock to)
    {
        foreach (MInstr i in b.Instrs)
        {
            if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands[0] is MLabel { Target: var t } && ReferenceEquals(t, from))
                i.Operands[0] = new MLabel(to);
            if (i.Table is { } table)
                for (int k = 0; k < table.Count; k++)
                    if (ReferenceEquals(table[k], from)) table[k] = to;
        }
    }

    /// <summary>The values whose one definition is a constant: remade where they are wanted, never split.</summary>
    private BitSet Constants(int width)
    {
        int[] defs = new int[width];
        BitSet constant = new(width);
        foreach (MBlock b in _m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach ((MReg r, Role role, int k, _) in Allocator.RegsOf(i, _regs))
                {
                    if (r.IsPhys || (role & Role.Def) == 0) continue;
                    defs[r.Id]++;
                    if (i.Op == MOp.Mov && k == 0 && i.Width == 4 && i.Operands[1] is MImm) constant.Set(r.Id);
                }
        for (int v = 0; v < width; v++) if (defs[v] != 1) constant.Clear(v);
        return constant;
    }
}
