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
    private readonly int _words;
    private readonly Dictionary<Block, ulong[]> _in = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Block, ulong[]> _out = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, VReg> _regs = new();

    public Liveness(Function f) : this(new Cfg(f))
    {
    
    }

    public Liveness(Cfg cfg)
    {
        Cfg = cfg;
        Function f = cfg.Function;
        _words = (f.RegCount + 63) >> 6;

        // Per-block use (read before any write in the block) and def sets,
        // computed once; the iteration only combines them.
        Dictionary<Block, ulong[]> use = new(ReferenceEqualityComparer.Instance);
        Dictionary<Block, ulong[]> def = new(ReferenceEqualityComparer.Instance);
        // A phi reads its operand at the end of the predecessor it names,
        // not at the top of its own block: those reads are gathered per
        // predecessor and folded into that block's live-out.
        Dictionary<Block, ulong[]> phiOut = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
        {
            phiOut[b] = new ulong[_words];
        }
        foreach (Block b in f.Blocks)
        {
            ulong[] u = new ulong[_words];
            ulong[] d = new ulong[_words];
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Phi)
                {
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (i.Operands[k] is RegOperand pr && phiOut.TryGetValue(i.Targets[k], out ulong[]? po))
                        {
                            _regs[pr.Reg.Id] = pr.Reg;
                            Set(po, pr.Reg.Id);
                        }
                    }
                }
                else
                {
                    foreach (VReg r in IrInfo.Uses(i))
                    {
                        _regs[r.Id] = r;
                        if (!Test(d, r.Id))
                        {
                            Set(u, r.Id);
                        }
                    }
                }
                if (i.Dest is not null)
                {
                    _regs[i.Dest.Id] = i.Dest;
                    Set(d, i.Dest.Id);
                }
            }
            use[b] = u;
            def[b] = d;
            _in[b] = new ulong[_words];
            _out[b] = new ulong[_words];
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
                ulong[] o = _out[b];
                Array.Copy(phiOut[b], o, _words);
                foreach (Block s in cfg.Succs(b))
                {
                    ulong[] si = _in[s];
                    for (int w = 0; w < _words; w++)
                    {
                        o[w] |= si[w];
                    }
                }
                ulong[] u = use[b];
                ulong[] d = def[b];
                ulong[] n = _in[b];
                for (int w = 0; w < _words; w++)
                {
                    ulong v = u[w] | (o[w] & ~d[w]);
                    if (v != n[w])
                    {
                        n[w] = v;
                        changed = true;
                    }
                }
            }
        }
    }

    public bool IsLiveIn(Block b, VReg r) => Test(_in[b], r.Id);
    public bool IsLiveOut(Block b, VReg r) => Test(_out[b], r.Id);

    public IEnumerable<VReg> LiveIn(Block b) => Enumerate(_in[b]);
    public IEnumerable<VReg> LiveOut(Block b) => Enumerate(_out[b]);

    /// <summary>
    /// Walks a block backwards, yielding each instruction with the set of
    /// registers live immediately after it. What an allocator or a dead
    /// store pass wants; the set is reused between yields, so copy it to
    /// keep it.
    /// </summary>
    public IEnumerable<(Instr Instr, ulong[] LiveAfter)> WalkBackwards(Block b)
    {
        ulong[] live = (ulong[])_out[b].Clone();
        for (int k = b.Instrs.Count - 1; k >= 0; k--)
        {
            Instr i = b.Instrs[k];
            yield return (i, live);
            if (i.Dest is not null)
            {
                Clear(live, i.Dest.Id);
            }
            if (i.Op == Opcode.Phi)
            {
                continue;       // its reads belong to the predecessors
            }
            foreach (VReg r in IrInfo.Uses(i))
            {
                Set(live, r.Id);
            }
        }
    }

    public static bool Test(ulong[] set, int id) => (set[id >> 6] & (1UL << (id & 63))) != 0;
    private static void Set(ulong[] set, int id) => set[id >> 6] |= 1UL << (id & 63);
    private static void Clear(ulong[] set, int id) => set[id >> 6] &= ~(1UL << (id & 63));

    private IEnumerable<VReg> Enumerate(ulong[] set)
    {
        for (int w = 0; w < set.Length; w++)
        {
            ulong bits = set[w];
            while (bits != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                yield return _regs[(w << 6) + bit];
            }
        }
    }
}
