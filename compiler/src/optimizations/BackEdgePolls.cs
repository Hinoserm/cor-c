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
    public const string Poll = "m_Runtime_Poll_0";
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
        if (latches is null) return 0;
        int made = 0;
        foreach ((Block header, List<Block> backs) in latches)
        {
            if (header.Instrs.Count > 0 && header.Instrs[0].Op == Opcode.Phi) continue;
            HashSet<Block> body = Body(cfg, header, backs);
            if (Counted(cfg, body, backs)) continue;
            foreach (Block latch in backs)
            {
                Instr last = latch.Instrs[^1];
                for (int t = 0; t < last.Targets.Count; t++)
                {
                    if (!ReferenceEquals(last.Targets[t], header)) continue;
                    Block test = f.NewBlock("poll");
                    Block call = f.NewBlock("polled");
                    Builder e = new(f, test);
                    VReg asked = e.Load(IrTypes.Word, new SymOperand(Asking), 0, IrTypes.Word.Bytes());
                    e.Branch(asked, call, header);
                    e.SetBlock(call);
                    e.Call(Poll, IrType.Void);
                    e.Jump(header);
                    last.SetTarget(t, test);
                    made++;
                }
            }
        }
        return made;
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
    /// branch out of the loop on a compare of a counter with a bound the loop
    /// never writes, the counter written in the loop once, in a block that
    /// dominates every latch too, to itself plus or minus a constant.
    /// </summary>
    private static bool Counted(Cfg cfg, HashSet<Block> body, List<Block> backs)
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
        // The counter stepped every lap: its one write in the loop is itself
        // plus a constant, directly or through one copy of such a sum.
        bool Steps(VReg counter)
        {
            if (!writes.TryGetValue(counter, out var mine) || mine.Count != 1 || !EveryLap(mine[0].Block)) return false;
            Instr w = mine[0].Write;
            if (w is { Op: Opcode.Copy, Operands: [RegOperand { Reg: var sum }] })
            {
                if (!writes.TryGetValue(sum, out var sums) || sums.Count != 1) return false;
                w = sums[0].Write;
            }
            return w is { Op: Opcode.Add or Opcode.Sub, Operands: [RegOperand { Reg: var from }, ImmOperand { Value: not 0 }] } && from == counter;
        }
        foreach (Block b in body)
        {
            if (b.Instrs.Count == 0 || b.Instrs[^1] is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var cond }] } branch
                || branch.Targets.Count != 2 || body.Contains(branch.Targets[0]) == body.Contains(branch.Targets[1]) || !EveryLap(b))
                continue;
            // A condition the loop never writes is no counter's.
            Instr? test = writes.TryGetValue(cond, out var tests) && tests.Count == 1 ? tests[0].Write : null;
            if (test is not { Op: Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU or Opcode.Ne, Operands.Count: 2 })
                continue;
            Operand a = test.Operands[0], c = test.Operands[1];
            if (a is RegOperand { Reg: var left } && Steps(left) && Invariant(c)) return true;
            if (c is RegOperand { Reg: var right } && Steps(right) && Invariant(a)) return true;
        }
        return false;
    }
}
