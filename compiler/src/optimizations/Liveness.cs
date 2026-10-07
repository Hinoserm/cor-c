#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Which virtual registers are live at the boundaries of each block:
/// backward iterative dataflow over the CFG, the classic
/// in = use ∪ (out − def), out = ∪ in(succ). Sets are bit vectors indexed
/// by <see cref="VReg.Id"/>, so a function with thousands of registers
/// costs a few words per block per iteration rather than a hash set.
///
/// Both the optimiser (dead stores to registers, block merging) and the
/// register allocator want this, which is why it lives here rather than
/// in a backend. A landing pad has no predecessors in the graph, so its
/// live-in set is computed but flows nowhere; the IR's rule that every
/// register is dead on entry to a pad is the backend's to enforce, and
/// the pad's first instruction, <c>Call "__exception"</c>, defines the one
/// value that is actually available.
/// </summary>
public sealed class Liveness
{
    public Cfg Cfg { get; }

    /// <summary>Where each landing pad can be entered, with what it reads (Escape.PadLiveAt); made once.</summary>
    internal Dictionary<Block, HashSet<VReg>>? PadRegions { get; set; }
    /// <summary>A push's own block, where its pad can be entered only after the push: (that index, what the pad reads).</summary>
    internal Dictionary<Block, List<(int After, HashSet<VReg> Reads)>>? PadFrom { get; set; }
    private readonly int _words;
    private readonly int _registers;
    // FLAT: one row of _words per block, the block's row at its Order (as
    // Cfg numbered it) -- three arrays for the function where there were two
    // dictionaries and five arrays per block.
    private readonly ulong[] _in;
    private readonly ulong[] _out;
    private readonly Block[] _blocks;
    // The registers by number, for the walks that yield them (LiveIn,
    // LiveOut): found from the function when first asked. Most askers test
    // bits and never want one, and a table as long as the function's
    // registers was a third of what each liveness made.
    private VReg?[]? _regs;

    public Liveness(Function f) : this(new Cfg(f))
    {
    
    }

    public Liveness(Cfg cfg) : this(cfg, null) { }

    // THE SETS MADE ONLY TO BUILD THESE -- every block's uses, definitions
    // and phi reads -- as lists of register numbers, a run a block, in
    // arrays a thread keeps (Kept). As rows of bits, a block's row as wide as
    // the function's registers, they were three arrays of blocks times
    // registers for every liveness any pass made: tens of megabytes for one
    // large function, and a twelfth of what a native compile left the
    // collector. A block reads and writes a handful of registers.
    [ThreadStatic] private static int[]? _keptUseStart, _keptDefStart, _keptPhiStart, _keptUses, _keptDefs, _keptPhiBlocks, _keptPhiRegs, _keptPhis, _keptDefAt;
    [ThreadStatic] private static ulong[]? _keptRow;

    // At least `length` of a kept array, its old contents kept up to `used`.
    private static int[] Kept(ref int[]? kept, int length, int used = 0)
    {
        if (kept is not null && kept.Length >= length) return kept;
        int[] made = new int[Math.Max(length, Math.Max(64, (kept?.Length ?? 0) * 2))];
        if (kept is not null && used > 0) Array.Copy(kept, made, used);
        return kept = made;
    }

    private static void Append(ref int[]? list, ref int count, int value)
    {
        int[] at = Kept(ref list, count + 1, count);
        at[count++] = value;
    }

    private static ulong[] Take(ulong[]? spare, int words)
    {
        if (spare is null || spare.Length < words) return new ulong[words];
        Array.Clear(spare, 0, words);
        return spare;
    }

    /// <summary>
    /// The liveness of `cfg`'s function, in the storage of `spare` -- one
    /// nothing will read again (LandingPadHomes.Place's last) -- where that
    /// is large enough: rows read only up to this function's own count.
    /// </summary>
    internal Liveness(Cfg cfg, Liveness? spare)
    {
        Cfg = cfg;
        Function f = cfg.Function;
        _registers = f.RegCount;
        _words = (_registers + 63) >> 6;
        int count = f.Blocks.Count;
        _blocks = f.Blocks.ToArray();
        _in = Take(spare?._in, count * _words);
        _out = Take(spare?._out, count * _words);

        // Per-block use (read before any write in the block) and def lists,
        // computed once; the iteration only combines them. A phi reads its
        // operand at the end of the predecessor it names, not at the top of
        // its own block: those reads are gathered by predecessor and folded
        // into that block's live-out.
        int[] useStart = Kept(ref _keptUseStart, count + 1);
        int[] defStart = Kept(ref _keptDefStart, count + 1);
        // The block, plus one, that last wrote each register: a read after it
        // in the same block is no use.
        int[] defAt = Kept(ref _keptDefAt, _registers);
        Array.Clear(defAt, 0, _registers);
        int uses = 0, defs = 0, phis = 0;
        for (int at = 0; at < count; at++)
        {
            Block b = _blocks[at];
            useStart[at] = uses;
            defStart[at] = defs;
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Phi)
                {
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (i.Operands[k] is RegOperand pr && Row(i.Targets[k]) is int po and >= 0)
                        {
                            int from = _words == 0 ? 0 : po / _words;
                            Append(ref _keptPhiBlocks, ref phis, from);
                            phis--;
                            Append(ref _keptPhiRegs, ref phis, pr.Reg.Id);
                        }
                    }
                }
                else
                {
                    foreach (Operand rOperand in (i).Operands) if (rOperand is RegOperand { Reg: var r })
                    {
                        if (defAt[r.Id] != at + 1) Append(ref _keptUses, ref uses, r.Id);
                    }
                }
                if (i.Dest is not null)
                {
                    if (defAt[i.Dest.Id] != at + 1)
                    {
                        defAt[i.Dest.Id] = at + 1;
                        Append(ref _keptDefs, ref defs, i.Dest.Id);
                    }
                }
            }
        }
        useStart[count] = uses;
        defStart[count] = defs;
        if (_words == 0) return;
        int[] useList = _keptUses ?? Array.Empty<int>();
        int[] defList = _keptDefs ?? Array.Empty<int>();
        // The phi reads by the block they are live out of, a run a block.
        int[] phiStart = Kept(ref _keptPhiStart, count + 1);
        Array.Clear(phiStart, 0, count + 1);
        for (int k = 0; k < phis; k++) phiStart[_keptPhiBlocks![k] + 1]++;
        for (int k = 0; k < count; k++) phiStart[k + 1] += phiStart[k];
        int[] phiList = Kept(ref _keptPhis, phis);
        for (int k = phis - 1; k >= 0; k--) phiList[--phiStart[_keptPhiBlocks![k] + 1]] = _keptPhiRegs![k];
        for (int k = 0; k < count; k++) phiStart[k] = phiStart[k + 1];
        phiStart[count] = phis;
        ulong[] row = _keptRow is { } r0 && r0.Length >= _words ? r0 : _keptRow = new ulong[_words];

        // Blocks no root reaches are left with empty sets: nothing runs
        // there, so nothing is live there, and they are about to be
        // removed anyway.
        IReadOnlyList<Block> order = cfg.ReversePostorder;
        bool changed = true;
        while (changed)
        {
            changed = false;
            // Reverse postorder reversed is close to a topological order of
            // the reversed graph, so most blocks see their successors' final
            // sets on the first sweep and loops need one more.
            for (int k = order.Count - 1; k >= 0; k--)
            {
                Block b = order[k];
                int o = RowOf(b);
                int at = o / _words;
                Array.Clear(_out, o, _words);
                for (int p = phiStart[at]; p < phiStart[at + 1]; p++) Set(_out, o, phiList[p]);
                foreach (Block s in cfg.Succs(b))
                {
                    int si = RowOf(s);
                    for (int w = 0; w < _words; w++)
                    {
                        _out[o + w] |= _in[si + w];
                    }
                }
                // In: what is live out but for what the block writes, and
                // what it reads before it writes.
                Array.Copy(_out, o, row, 0, _words);
                for (int d = defStart[at]; d < defStart[at + 1]; d++) row[defList[d] >> 6] &= ~(1UL << (defList[d] & 63));
                for (int u = useStart[at]; u < useStart[at + 1]; u++) row[useList[u] >> 6] |= 1UL << (useList[u] & 63);
                for (int w = 0; w < _words; w++)
                {
                    if (row[w] != _in[o + w])
                    {
                        _in[o + w] = row[w];
                        changed = true;
                    }
                }
            }
        }
    }

    /// <summary>Where a block's row starts, or -1 for a block this analysis never saw.</summary>
    private int Row(Block b)
    {
        int k = b.Order;
        if ((uint)k >= (uint)_blocks.Length || !ReferenceEquals(_blocks[k], b)) k = Array.IndexOf(_blocks, b);
        return k < 0 ? -1 : k * _words;
    }

    private int RowOf(Block b) => Row(b) is int row and >= 0 ? row : throw new KeyNotFoundException("block " + b.Label + " is not in this liveness");

    public bool IsLiveIn(Block b, VReg r) => Test(_in, RowOf(b), r.Id);
    public bool IsLiveOut(Block b, VReg r) => Test(_out, RowOf(b), r.Id);

    /// <summary>Whether the register existed when this analysis was made; one made since has no answer here.</summary>
    public bool Tracks(VReg r) => r.Id < _registers;

    public IEnumerable<VReg> LiveIn(Block b) => Enumerate(_in, RowOf(b));
    public IEnumerable<VReg> LiveOut(Block b) => Enumerate(_out, RowOf(b));

    /// <summary>
    /// Walks a block backwards, yielding each instruction with the set of
    /// registers live immediately after it. What an allocator or a dead
    /// store pass wants; the set is reused between yields, so copy it to
    /// keep it.
    ///
    /// With <paramref name="skipNewer"/>, registers made after the analysis
    /// are left out instead of being an error: a pass that inserts only
    /// bookkeeping of its own -- new registers, no new blocks, no new reads
    /// of older ones -- may keep asking about the older registers, whose
    /// answers stay right or err toward live.
    /// </summary>
    public IEnumerable<(Instr Instr, ulong[] LiveAfter)> WalkBackwards(Block b, bool skipNewer = false)
    {
        ulong[] live = new ulong[_words];
        Array.Copy(_out, RowOf(b), live, 0, _words);
        for (int k = b.Instrs.Count - 1; k >= 0; k--)
        {
            Instr i = b.Instrs[k];
            yield return (i, live);
            StepBackwards(i, live, skipNewer);
        }
    }

    /// <summary>
    /// WalkBackwards by hand, for a walk made often enough that its iterator
    /// and its set were the cost: the registers live out of the block, in
    /// `into` when it is already the right size (else a new set, kept there).
    /// Each instruction is then passed to StepBackwards, last first, after
    /// its live-after set has been read.
    /// </summary>
    public ulong[] LiveOutInto(Block b, ref ulong[]? into)
    {
        if (into is null || into.Length != _words) into = new ulong[_words];
        Array.Copy(_out, RowOf(b), into, 0, _words);
        return into;
    }

    /// <summary>From the registers live after an instruction to those live before it, in place.</summary>
    public void StepBackwards(Instr i, ulong[] live, bool skipNewer)
    {
        if (i.Dest is not null && (!skipNewer || i.Dest.Id < _registers))
        {
            Clear(live, i.Dest.Id);
        }
        if (i.Op == Opcode.Phi)
        {
            return;       // its reads belong to the predecessors
        }
        foreach (Operand rOperand in (i).Operands) if (rOperand is RegOperand { Reg: var r })
        {
            if (!skipNewer || r.Id < _registers)
            {
                Set(live, r.Id);
            }
        }
    }

    public static bool Test(ulong[] set, int id) => (set[id >> 6] & (1UL << (id & 63))) != 0;
    private static void Set(ulong[] set, int id) => set[id >> 6] |= 1UL << (id & 63);
    private static void Clear(ulong[] set, int id) => set[id >> 6] &= ~(1UL << (id & 63));
    private static bool Test(ulong[] rows, int row, int id) => (rows[row + (id >> 6)] & (1UL << (id & 63))) != 0;
    private static void Set(ulong[] rows, int row, int id) => rows[row + (id >> 6)] |= 1UL << (id & 63);

    /// <summary>
    /// The registers by number, from the function as it is now: a register
    /// an edit since has removed from every instruction is in no table and
    /// is left out, as nothing in the function can read it.
    /// </summary>
    private VReg?[] Registers()
    {
        if (_regs is { } made) return made;
        VReg?[] regs = new VReg?[_registers];
        foreach (VReg p in Cfg.Function.Params) if (p.Id < _registers) regs[p.Id] = p;
        foreach (Block b in Cfg.Function.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is { } d && d.Id < _registers) regs[d.Id] = d;
                foreach (Operand o in i.Operands) if (o is RegOperand { Reg: var r } && r.Id < _registers) regs[r.Id] = r;
            }
        }
        return _regs = regs;
    }

    private IEnumerable<VReg> Enumerate(ulong[] rows, int row)
    {
        VReg?[] regs = Registers();
        for (int w = 0; w < _words; w++)
        {
            ulong bits = rows[row + w];
            while (bits != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (regs[(w << 6) + bit] is { } r) yield return r;
            }
        }
    }
}
