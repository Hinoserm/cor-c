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
    private readonly VReg?[] _regs;

    public Liveness(Function f) : this(new Cfg(f))
    {
    
    }

    public Liveness(Cfg cfg)
    {
        Cfg = cfg;
        Function f = cfg.Function;
        _registers = f.RegCount;
        _words = (_registers + 63) >> 6;
        int count = f.Blocks.Count;
        _blocks = f.Blocks.ToArray();
        _regs = new VReg?[_registers];
        _in = new ulong[count * _words];
        _out = new ulong[count * _words];

        // Per-block use (read before any write in the block) and def sets,
        // computed once; the iteration only combines them.
        ulong[] use = new ulong[count * _words];
        ulong[] def = new ulong[count * _words];
        // A phi reads its operand at the end of the predecessor it names,
        // not at the top of its own block: those reads are gathered per
        // predecessor and folded into that block's live-out.
        ulong[] phiOut = new ulong[count * _words];
        for (int at = 0; at < count; at++)
        {
            Block b = _blocks[at];
            int u = at * _words;
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Phi)
                {
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (i.Operands[k] is RegOperand pr && Row(i.Targets[k]) is int po and >= 0)
                        {
                            _regs[pr.Reg.Id] = pr.Reg;
                            Set(phiOut, po, pr.Reg.Id);
                        }
                    }
                }
                else
                {
                    foreach (Operand rOperand in (i).Operands) if (rOperand is RegOperand { Reg: var r })
                    {
                        _regs[r.Id] = r;
                        if (!Test(def, u, r.Id))
                        {
                            Set(use, u, r.Id);
                        }
                    }
                }
                if (i.Dest is not null)
                {
                    _regs[i.Dest.Id] = i.Dest;
                    Set(def, u, i.Dest.Id);
                }
            }
        }

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
                Array.Copy(phiOut, o, _out, o, _words);
                foreach (Block s in cfg.Succs(b))
                {
                    int si = RowOf(s);
                    for (int w = 0; w < _words; w++)
                    {
                        _out[o + w] |= _in[si + w];
                    }
                }
                for (int w = 0; w < _words; w++)
                {
                    ulong v = use[o + w] | (_out[o + w] & ~def[o + w]);
                    if (v != _in[o + w])
                    {
                        _in[o + w] = v;
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
            if (i.Dest is not null && (!skipNewer || i.Dest.Id < _registers))
            {
                Clear(live, i.Dest.Id);
            }
            if (i.Op == Opcode.Phi)
            {
                continue;       // its reads belong to the predecessors
            }
            foreach (Operand rOperand in (i).Operands) if (rOperand is RegOperand { Reg: var r })
            {
                if (!skipNewer || r.Id < _registers)
                {
                    Set(live, r.Id);
                }
            }
        }
    }

    public static bool Test(ulong[] set, int id) => (set[id >> 6] & (1UL << (id & 63))) != 0;
    private static void Set(ulong[] set, int id) => set[id >> 6] |= 1UL << (id & 63);
    private static void Clear(ulong[] set, int id) => set[id >> 6] &= ~(1UL << (id & 63));
    private static bool Test(ulong[] rows, int row, int id) => (rows[row + (id >> 6)] & (1UL << (id & 63))) != 0;
    private static void Set(ulong[] rows, int row, int id) => rows[row + (id >> 6)] |= 1UL << (id & 63);

    private IEnumerable<VReg> Enumerate(ulong[] rows, int row)
    {
        for (int w = 0; w < _words; w++)
        {
            ulong bits = rows[row + w];
            while (bits != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                yield return _regs[(w << 6) + bit]!;
            }
        }
    }
}
