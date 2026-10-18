#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A SAFEPOINT ON EVERY WAY ROUND A LOOP THAT MAY NOT END BY ITSELF. A
/// collection asks every running thread for an answer (GcThreads.Round), and
/// a thread gives one where it allocates, blocks or polls. One spinning
/// `while (!Volatile.Read(ref done));` does none of them: the collector waits
/// for it while it waits for the collector's thread, or, with nobody waiting
/// on it, the round stays open and every other thread's heap grows. So each
/// such loop's back edge reads one word, GcThreads.Asking, which is set while
/// a round is open, and calls Runtime.Poll when it is: one load and a branch
/// that is never taken a lap, and the poll a call with a stack map, which is
/// all an answer needs.
///
/// A COUNTED LOOP IS PASSED OVER: one whose every lap passes a test of a
/// counter, stepped every lap by a constant, against a bound the loop never
/// writes ends by itself, its laps known when it starts. That is every walk
/// over an array, where a lap is a few instructions and the word's read would
/// be a share of them.
///
/// NOT IN THE COLLECTOR'S OWN CODE: its loops run under the heap lock or
/// answer for themselves, and its mark loop is the collector's time. Nor
/// without an operating system, where a kernel prompts every processor with
/// an interrupt instead (Platform.PromptStop): the backends run this only
/// where their SafepointPolls says so.
/// </summary>
public static class BackEdgePolls
{
    public const string Poll = Corsac.Lang.Lto.RuntimeAbi.Poll;
    public const string Asking = "s_GcThreads_Asking";

    // The runtime's classes that hold the heap lock, answer rounds or are the
    // collector: a poll there would only cost.
    private static readonly string[] Collector =
    {
        "m_Gc_", "m_GcLock_", "m_GcRoots_", "m_GcThreads_", "m_HeapChunks_", "m_Runtime_", "m_Platform_",
        "m_Atom_", "m_AllocSamples_", "m_Native_", "m_Os_", "m_Clock_", "m_Tls_", "m_ThreadStatics_",
        "m_ThreadStaticTable_", "m_InterruptCompletions_", "m_WordSize_",
    };

    /// <summary>The polls put on `f`'s back edges; how many.</summary>
    public static int Run(Function f)
    {
        if (f.Name.StartsWith("__", StringComparison.Ordinal)) return 0;
        foreach (string prefix in Collector)
            if (f.Name.StartsWith(prefix, StringComparison.Ordinal)) return 0;
        if (f.Blocks.Count < 2) return 0;
        Cfg cfg = new(f);
        HashSet<Block> reached = cfg.Reachable();
        // Each loop by its header: the blocks with an edge back to it, which
        // it dominates.
        Dictionary<Block, List<Block>>? latches = null;
        foreach (Block b in f.Blocks)
        {
            if (!reached.Contains(b)) continue;
            foreach (Block s in cfg.Succs(b))
                if (cfg.Dominates(s, b))
                {
                    latches ??= new();
                    if (!latches.TryGetValue(s, out List<Block>? into)) latches[s] = into = new();
                    if (!into.Contains(b)) into.Add(b);
                }
        }
        // EVERY OTHER WAY ROUND, found before any poll moves an edge: a loop
        // whose lap passes the join after a try and its catch has a header
        // that dominates nothing past the join -- a landing pad is a root of
        // its own (Cfg) -- and so has a loop an await's or a yield's resume
        // rejoins, or a goto makes; none of them has an edge back to a block
        // that dominates where it comes from, and each was polled nowhere.
        // An edge back to a block on a depth-first walk's path that is no
        // such edge is polled, counted or not: no body to count it by.
        List<(Block From, Block To)> retreating = Retreating(f, cfg);
        if (latches is null && retreating.Count == 0) return 0;
        HashSet<VReg>? lengths = null;
        int made = 0;
        if (latches is not null)
            foreach ((Block header, List<Block> backs) in latches)
            {
                if (header.Instrs.Count > 0 && header.Instrs[0].Op == Opcode.Phi) continue;
                HashSet<Block> body = Body(cfg, header, backs);
                if (Counted(f, cfg, body, backs, ref lengths)) continue;
                foreach (Block latch in backs) made += PollEdges(f, latch, header);
            }
        foreach ((Block from, Block to) in retreating) made += PollEdges(f, from, to);
        return made;
    }

    /// <summary>Every edge of `from`'s last instruction to `to` made to pass a poll first; how many.</summary>
    private static int PollEdges(Function f, Block from, Block to)
    {
        if (to.Instrs.Count > 0 && to.Instrs[0].Op == Opcode.Phi) return 0;
        int made = 0;
        Instr last = from.Instrs[^1];
        for (int t = 0; t < last.Targets.Count; t++)
        {
            if (!ReferenceEquals(last.Targets[t], to)) continue;
            Block test = f.NewBlock("poll");
            Block call = f.NewBlock("polled");
            Builder e = new(f, test);
            VReg asked = e.Load(IrTypes.Word, new SymOperand(Asking), 0, IrTypes.Word.Bytes());
            e.Branch(asked, call, to);
            e.SetBlock(call);
            e.Call(Poll, IrType.Void);
            e.Jump(to);
            last.SetTarget(t, test);
            made++;
        }
        return made;
    }

    /// <summary>
    /// The edges back to a block still on the path of a depth-first walk
    /// from every root that are no natural loop's: their target does not
    /// dominate their source.
    /// </summary>
    private static List<(Block From, Block To)> Retreating(Function f, Cfg cfg)
    {
        List<(Block From, Block To)> found = new();
        byte[] state = new byte[f.Blocks.Count];   // 0 not seen, 1 on the path, 2 done
        Stack<(Block Block, int Next)> path = new();
        foreach (Block root in cfg.Roots)
        {
            if (state[root.Order] != 0) continue;
            state[root.Order] = 1;
            path.Push((root, 0));
            while (path.TryPop(out (Block Block, int Next) at))
            {
                Edges succs = cfg.Succs(at.Block);
                if (at.Next >= succs.Count)
                {
                    state[at.Block.Order] = 2;
                    continue;
                }
                path.Push((at.Block, at.Next + 1));
                Block s = succs[at.Next];
                if (state[s.Order] == 0)
                {
                    state[s.Order] = 1;
                    path.Push((s, 0));
                }
                else if (state[s.Order] == 1 && !cfg.Dominates(s, at.Block) && !found.Contains((at.Block, s)))
                {
                    found.Add((at.Block, s));
                }
            }
        }
        return found;
    }

    /// <summary>The loop's blocks: the header and every block that reaches a latch without passing it.</summary>
    private static HashSet<Block> Body(Cfg cfg, Block header, List<Block> backs)
    {
        HashSet<Block> body = new() { header };
        Stack<Block> walk = new();
        foreach (Block latch in backs)
            if (body.Add(latch)) walk.Push(latch);
        while (walk.TryPop(out Block? b))
            foreach (Block p in cfg.Preds(b))
                if (body.Add(p)) walk.Push(p);
        return body;
    }

    /// <summary>
    /// Whether every lap passes, in a block that dominates every latch, a
    /// branch out of the loop on a compare, made every lap, of a counter with
    /// a bound the loop never writes, the counter written in the loop once,
    /// in a block that dominates every latch too, to itself plus or minus a
    /// constant -- a step TOWARD the way out that cannot pass it. A counter
    /// stepped away from its bound, or past it (`i != 7` by twos, `i &lt; n`
    /// by twos with n the largest int), held to a bound every value may meet
    /// (`i &lt;= n` with n the largest int, an unsigned `u &gt;= 0`), or one of
    /// 64 bits run to a `!=`, never ends by itself, or not this side of the
    /// heat death of the machine, and passed over it would keep every other
    /// thread's collection waiting for as long as it ran.
    /// </summary>
    private static bool Counted(Function f, Cfg cfg, HashSet<Block> body, List<Block> backs, ref HashSet<VReg>? lengths)
    {
        Dictionary<VReg, List<(Block Block, Instr Write)>> writes = new();
        foreach (Block b in body)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d)
                {
                    if (!writes.TryGetValue(d, out var list)) writes[d] = list = new();
                    list.Add((b, i));
                }
        bool EveryLap(Block b) => backs.All(latch => cfg.Dominates(b, latch));
        // A bound the loop never writes, or an array's length read in it of
        // an array it never writes: a length never changes (`i < a.Length`,
        // read each lap where nothing hoisted it).
        bool Invariant(Operand o)
        {
            if (o is ImmOperand) return true;
            if (o is not RegOperand { Reg: var r }) return false;
            if (!writes.TryGetValue(r, out var at)) return true;
            return at.Count == 1 && at[0].Write is { Op: Opcode.ArrayLength, Operands: [RegOperand { Reg: var array }] } && !writes.ContainsKey(array);
        }
        // The counter's step a lap: its one write in the loop is itself plus
        // or minus a constant, directly or through one copy of such a sum,
        // each made every lap. Zero when it is no counter. Read at the
        // counter's width, so that a 32-bit step of -1 held as its unsigned
        // bits is still -1.
        long Step(VReg counter)
        {
            if (!writes.TryGetValue(counter, out var mine) || mine.Count != 1 || !EveryLap(mine[0].Block)) return 0;
            Instr w = mine[0].Write;
            if (w is { Op: Opcode.Copy, Operands: [RegOperand { Reg: var sum }] })
            {
                if (!writes.TryGetValue(sum, out var sums) || sums.Count != 1 || !EveryLap(sums[0].Block)) return 0;
                w = sums[0].Write;
            }
            if (w is not { Op: Opcode.Add or Opcode.Sub, Operands: [RegOperand { Reg: var from }, ImmOperand { Value: var by } constant] } || from != counter)
                return 0;
            if (constant.Type.Bytes() == 4) by = (int)by;
            if (by == long.MinValue) return 0;
            return w.Op == Opcode.Add ? by : -by;
        }
        // An array's length, wherever the function reads it: below every
        // int's ceiling by more than a short step. Found once a function.
        HashSet<VReg>? known = lengths;
        bool IsLength(Operand o)
        {
            if (o is not RegOperand { Reg: var r }) return false;
            known ??= Lengths(f);
            return known.Contains(r);
        }
        foreach (Block b in body)
        {
            if (b.Instrs.Count == 0 || b.Instrs[^1] is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var cond }] } branch
                || branch.Targets.Count != 2 || body.Contains(branch.Targets[0]) == body.Contains(branch.Targets[1]) || !EveryLap(b))
                continue;
            // A condition the loop never writes is no counter's, and one it
            // does not write every lap may hold its last value for ever.
            if (!writes.TryGetValue(cond, out var tests) || tests.Count != 1 || !EveryLap(tests[0].Block)) continue;
            Instr test = tests[0].Write;
            if (test is not { Op: Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU or Opcode.Ne or Opcode.Eq, Operands.Count: 2 })
                continue;
            Operand a = test.Operands[0], c = test.Operands[1];
            Opcode op = test.Op;
            long step = a is RegOperand { Reg: var left } && Invariant(c) ? Step(left) : 0;
            if (step == 0)
            {
                // `bound op counter`, read as `counter op' bound`.
                if (c is not RegOperand { Reg: var right } || !Invariant(a) || (step = Step(right)) == 0) continue;
                (a, c) = (c, a);
                op = Swapped(op);
            }
            // What holds while the laps go on: the compare, or its opposite
            // where the branch's true arm is the way out.
            if (!body.Contains(branch.Targets[0])) op = Negated(op);
            if (Ends(op, step, a.Type, c, IsLength(c)))
            {
                lengths = known;
                return true;
            }
        }
        lengths = known;
        return false;
    }

    /// <summary>The registers every write of which in `f` is an array's length.</summary>
    private static HashSet<VReg> Lengths(Function f)
    {
        HashSet<VReg> lengths = new(), others = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d)
                    (i.Op == Opcode.ArrayLength ? lengths : others).Add(d);
        foreach (VReg p in f.Params) others.Add(p);
        lengths.ExceptWith(others);
        return lengths;
    }

    /// <summary>
    /// Whether `counter op bound`, the counter stepped by `step` a lap, stops
    /// holding by itself, its last step short of wrapping past the bound: a
    /// constant bound with room for the step beyond it, an array's length
    /// (`length`) with a short step, or a step of one toward a strict bound,
    /// which meets any bound exactly.
    /// </summary>
    private static bool Ends(Opcode op, long step, IrType width, Operand bound, bool length)
    {
        bool wide = width.Bytes() == 8;
        bool known = bound is ImmOperand;
        long raw = bound is ImmOperand { Value: var v } ? v : 0;
        long at = wide ? raw : (int)raw;
        ulong uat = wide ? (ulong)raw : (uint)raw;
        long max = wide ? long.MaxValue : int.MaxValue, min = wide ? long.MinValue : int.MinValue;
        ulong umax = wide ? ulong.MaxValue : uint.MaxValue;
        bool shortStep = length && step <= 16;
        return op switch
        {
            Opcode.Eq => true,
            Opcode.Ne => !wide && step is 1 or -1,
            Opcode.LtS => step > 0 && (step == 1 || known && at <= max - step + 1 || shortStep),
            Opcode.LeS => step > 0 && (known && at <= max - step || shortStep),
            Opcode.GtS => step < 0 && (step == -1 || known && at >= min - step - 1),
            Opcode.GeS => step < 0 && known && at >= min - step,
            Opcode.LtU => step > 0 && (step == 1 || known && uat <= umax - (ulong)step + 1 || shortStep),
            Opcode.LeU => step > 0 && (known && uat <= umax - (ulong)step || shortStep),
            Opcode.GtU => step < 0 && (step == -1 || known && uat >= (ulong)(-step) - 1),
            Opcode.GeU => step < 0 && known && uat >= (ulong)(-step),
            _ => false,
        };
    }

    /// <summary>The compare of the same two operands taken the other way round.</summary>
    private static Opcode Swapped(Opcode op) => op switch
    {
        Opcode.LtS => Opcode.GtS, Opcode.GtS => Opcode.LtS, Opcode.LeS => Opcode.GeS, Opcode.GeS => Opcode.LeS,
        Opcode.LtU => Opcode.GtU, Opcode.GtU => Opcode.LtU, Opcode.LeU => Opcode.GeU, Opcode.GeU => Opcode.LeU,
        _ => op,
    };

    /// <summary>The compare that holds where `op` does not.</summary>
    private static Opcode Negated(Opcode op) => op switch
    {
        Opcode.Eq => Opcode.Ne, Opcode.Ne => Opcode.Eq,
        Opcode.LtS => Opcode.GeS, Opcode.GeS => Opcode.LtS, Opcode.LeS => Opcode.GtS, Opcode.GtS => Opcode.LeS,
        Opcode.LtU => Opcode.GeU, Opcode.GeU => Opcode.LtU, Opcode.LeU => Opcode.GtU, Opcode.GtU => Opcode.LeU,
        _ => op,
    };
}
