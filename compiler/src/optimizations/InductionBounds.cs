#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// BOUNDS CHECKS A LOOP'S OWN TEST ALREADY MADE. `for (int i = 0; i &lt;
/// a.Length; i++) a[i]` tests `i &lt; length` signed at the top of every lap
/// and then, for the element, `i &lt; length` unsigned (Lowering.BoundsCheck),
/// which also refuses a negative index; List's indexer, inlined into
/// `for (i = 0; i &lt; list.Count; i++)`, asks `(uint)i >= (uint)count` of
/// the count the loop has just compared. Where the unsigned question is
/// asked under the signed answer, of the same two values, and the index
/// can never be negative, it has the same answer, and is folded to it: the
/// compare, its branch and the throw path behind it go.
///
/// NEVER NEGATIVE is proved of the index from every write of it: a constant
/// from 0 up, a copy of another such register, or one more than itself
/// where a signed `itself &lt; something` held on the way in -- an index
/// below some int is below int.MaxValue, so adding one cannot wrap. By
/// induction over the writes, every value it takes is non-negative. A
/// parameter is not (nothing writes it here), nor anything else.
///
/// AN OFFSET FROM THE COUNTER TOO. `for (i = 0; i + 3 &lt; a.Length; i +=
/// 4)` reading a[i] to a[i + 3], `for (i = 1; i &lt; a.Length; i++)` reading
/// a[i - 1], SHA-256's eight rounds a lap reading w[i] to w[i + 7]: the
/// index is `counter + c1` and the fact `counter + c2 &lt; length` with c1 at
/// most c2, so the index is below the length, and at least the counter's
/// least value plus c1 (AtLeast), which must not be negative. A sum with a
/// positive offset could wrap where the counter is near int.MaxValue, so
/// one is folded only where every value the counter takes is at most an
/// array's length (UpperBounded): each step of it is made under a fact
/// bounding it by one, and lowering never makes an array within Reach of
/// int.MaxValue elements.
/// </summary>
public sealed class InductionBounds : IPass
{
    public string Name => "induction-bounds";

    // ONE WALK A RUN: the pipeline runs a pass's one instance over several
    // functions at once (FunctionWorkers), so what a walk holds is its own.
    public void Run(Function function) => new Walk().Run(function);

    private sealed class Walk
    {
    private Function _f = null!;
    private Cfg _cfg = null!;
    private Defs _defs = null!;
    private Dictionary<VReg, List<(Block Block, int Index)>>? _writes;

    public void Run(Function function)
    {
        if (function.Async is not null || !Candidates(function)) return;
        _f = function;
        _cfg = PipelineAnalyses.CfgOf(function);
        if (_cfg.Roots.Count != 1) return;
        _defs = PipelineAnalyses.DefsOf(function);
        _writes = null;
        foreach (Block block in function.Blocks)
        {
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr test = block.Instrs[k];
                if (test.Op is not (Opcode.LtU or Opcode.GeU) || test.Dest is null || test.Operands.Count != 2
                    || test.Operands[0] is not RegOperand { Reg: var index } || test.Operands[1] is not RegOperand { Reg: var bound }
                    || index.Type != IrType.I32 || bound.Type != IrType.I32) continue;
                if (!InBounds(index, bound, block, k)) continue;
                block.Instrs[k] = IrInfo.CopyOf(test, new ImmOperand(test.Op == Opcode.LtU ? 1 : 0, IrType.I32));
            }
        }
        _f = null!; _cfg = null!; _defs = null!; _writes = null;
    }

    /// <summary>Whether there is an unsigned compare of two 32-bit registers at all: most functions have none.</summary>
    private static bool Candidates(Function function)
    {
        foreach (Block block in function.Blocks)
            foreach (Instr i in block.Instrs)
                if (i.Op is Opcode.LtU or Opcode.GeU && i.Operands.Count == 2 && i.Operands[0] is RegOperand && i.Operands[1] is RegOperand)
                    return true;
        return false;
    }

    /// <summary>
    /// The most an index's offset from the loop counter may be, either way,
    /// and the headroom an array's length leaves below int.MaxValue: lowering
    /// refuses an array of more than (0x7FFFFFF0 - header) / stride elements
    /// (Lowering's ArraySize check), so every length is at most
    /// int.MaxValue - Reach (UpperBounded).
    /// </summary>
    private const int Reach = 16;

    /// <summary>
    /// `index` as `counter + offset`: written once as an add or a sub of a
    /// small constant, else itself with offset 0. `At` is where the counter
    /// was read, or null when `index` is the counter itself.
    /// </summary>
    private (VReg Counter, long Offset, (Block Block, int Index)? At) Affine(VReg index)
    {
        if (_defs.IsSingle(index) && _defs.Site(index) is { } site && site.Block.Instrs[site.Index] is { Operands.Count: 2 } def)
        {
            if (def.Op == Opcode.Add && def.Operands[0] is RegOperand { Reg: var a } && def.Operands[1] is ImmOperand { Value: >= -Reach and <= Reach } x)
                return (a, x.Value, site);
            if (def.Op == Opcode.Add && def.Operands[1] is RegOperand { Reg: var b } && def.Operands[0] is ImmOperand { Value: >= -Reach and <= Reach } y)
                return (b, y.Value, site);
            if (def.Op == Opcode.Sub && def.Operands[0] is RegOperand { Reg: var c } && def.Operands[1] is ImmOperand { Value: >= -Reach and <= Reach } z)
                return (c, -z.Value, site);
        }
        return (index, 0, null);
    }

    /// <summary>
    /// The signed facts `less &lt; more` (or a mirror of it) that hold at
    /// (block, at): each comes from a branch every path to it took one side
    /// of, with where its compare was made.
    /// </summary>
    private IEnumerable<(VReg Less, Operand More, Block Block, int Index)> Facts(Block block)
    {
        Block child = block;
        for (int depth = 0; depth < 32; depth++)
        {
            Block? parent = _cfg.Idom(child);
            if (parent is null) yield break;
            Edges preds = _cfg.Preds(child);
            if (preds.Count == 1 && ReferenceEquals(preds[0], parent)
                && parent.Terminator is { Op: Opcode.Branch } branch && branch.Targets.Count == 2
                && branch.Targets[0] != branch.Targets[1] && branch.Operands[0] is RegOperand { Reg: var condition }
                && _defs.IsSingle(condition) && _defs.Site(condition) is { } site
                && _cfg.Dominates(site.Block, parent))
            {
                Instr compare = site.Block.Instrs[site.Index];
                bool truth = branch.Targets[0] == child;
                // less < more: LtS true, GeS false; more > less: GtS true, LeS false.
                (Operand Less, Operand More)? holds = (compare.Op, truth) switch
                {
                    (Opcode.LtS, true) or (Opcode.GeS, false) => (compare.Operands[0], compare.Operands[1]),
                    (Opcode.GtS, true) or (Opcode.LeS, false) => (compare.Operands[1], compare.Operands[0]),
                    _ => null,
                };
                if (holds is { } h && h.Less is RegOperand { Reg: var less } && less.Type == IrType.I32)
                    yield return (less, h.More, site.Block, site.Index);
            }
            child = parent;
        }
    }

    /// <summary>
    /// Whether `counter`, read at each of `reads`, is the counter as it is at
    /// (block, at): written on no path from any of them to there.
    /// </summary>
    private bool Same(VReg counter, Block block, int at, params (Block Block, int Index)?[] reads)
    {
        foreach (var read in reads)
            if (read is { } r && !Unchanged(counter, r.Block, r.Index, block, at)) return false;
        return true;
    }

    /// <summary>
    /// Whether `index &lt; bound` unsigned holds at (block, at): index is
    /// `counter + c1`, a fact on the way in says `counter + c2 &lt; bound`
    /// signed (the same bound, or the same array's length) with c1 &lt;= c2,
    /// the counter is never below -c1, and neither sum can wrap -- no offset
    /// above zero without the counter's upper bound (UpperBounded). So
    /// `for (i = 0; i + 3 &lt; a.Length; i += 4)` reads a[i] to a[i + 3]
    /// unchecked, and `for (i = 1; i &lt; a.Length; i++)` a[i - 1].
    /// </summary>
    private bool InBounds(VReg index, VReg bound, Block block, int at)
    {
        (VReg counter, long c1, var indexRead) = Affine(index);
        foreach ((VReg less, Operand more, Block factBlock, int factIndex) in Facts(block))
        {
            (VReg guarded, long c2, var guardRead) = Affine(less);
            if (guarded != counter || c1 > c2 || more is not RegOperand { Reg: var limit }) continue;
            if (!(limit == bound ? Unchanged(bound, factBlock, factIndex, block, at) : SameLength(limit, bound))) continue;
            if (!Same(counter, block, at, indexRead, guardRead, (factBlock, factIndex))) continue;
            if (AtLeast(counter, new HashSet<VReg>()) is not { } low || low + c1 < 0) continue;
            if ((c1 > 0 || c2 > 0) && !UpperBounded(counter, Math.Max(c1, c2), new HashSet<VReg>())) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The least value every write gives `register`, or null when one cannot
    /// be told: a constant from 0 up, a copy of a register with a least value,
    /// or the register plus a small constant that cannot wrap (SafeStep).
    /// </summary>
    private long? AtLeast(VReg register, HashSet<VReg> asking)
    {
        if (register.Type != IrType.I32 || _f.Params.Contains(register) || !asking.Add(register) || asking.Count > 8) return null;
        if (!Writes().TryGetValue(register, out var writes)) return null;
        long? least = null;
        foreach ((Block block, int index) in writes)
        {
            Instr write = block.Instrs[index];
            long? given;
            if (write.Op == Opcode.Copy && write.Operands[0] is ImmOperand { Value: >= 0 and <= int.MaxValue } constant) given = constant.Value;
            else if (Step(write, register, block, index) is { } step)
            {
                if (!SafeStep(register, step.Amount, step.Block, step.Index)) return null;
                continue;      // counter + k, k >= 1: no less than it was
            }
            else if (write.Op == Opcode.Copy && write.Operands[0] is RegOperand { Reg: var from }) given = AtLeast(from, asking);
            else return null;
            if (given is null) return null;
            least = least is null ? given : Math.Min(least.Value, given.Value);
        }
        return least;
    }

    /// <summary>
    /// Whether every value `register` is given is at most int.MaxValue -
    /// `slack`, so the register plus up to `slack` cannot wrap. A constant
    /// that small; a copy of such a register; or the register plus k under a
    /// fact `register + c &lt; m` whose own sum could not wrap (c at most the
    /// slack, the induction's hypothesis for the register's earlier values):
    /// then the new value is below m by c + 1 - k, and m is at most
    /// int.MaxValue, or Reach below it when it is an array's length. `i += 1`
    /// under `i + 1 &lt; n` keeps i a step below int.MaxValue whatever n is;
    /// `i += 8` under `i + 7 &lt; a.Length` keeps it Reach below, by the length.
    /// </summary>
    private bool UpperBounded(VReg register, long slack, HashSet<VReg> asking)
    {
        // Asked again of a register already being asked about: the
        // induction's hypothesis, that its earlier values were bounded.
        if (!asking.Add(register)) return true;
        if (register.Type != IrType.I32 || _f.Params.Contains(register) || asking.Count > 8 || slack < 0 || slack > Reach) return false;
        if (!Writes().TryGetValue(register, out var writes)) return false;
        foreach ((Block block, int index) in writes)
        {
            Instr write = block.Instrs[index];
            if (write.Op == Opcode.Copy && write.Operands[0] is ImmOperand { Value: >= 0 } constant && constant.Value <= int.MaxValue - slack) continue;
            if (Step(write, register, block, index) is { } step)
            {
                if (!StepStaysBelow(register, step.Amount, slack, step.Block, step.Index)) return false;
                continue;
            }
            if (write.Op == Opcode.Copy && write.Operands[0] is RegOperand { Reg: var from } && from != register
                && UpperBounded(from, slack, new HashSet<VReg>(asking))) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Whether `register + k`, read at (block, at), is at most int.MaxValue -
    /// slack, by a fact there (UpperBounded).
    /// </summary>
    private bool StepStaysBelow(VReg register, long k, long slack, Block block, int at)
    {
        foreach ((VReg less, Operand more, Block factBlock, int factIndex) in Facts(block))
        {
            (VReg guarded, long c, var guardRead) = Affine(less);
            if (guarded != register || c < 0 || c > slack || more is not RegOperand { Reg: var limit }) continue;
            long headroom = (_defs.Definition(limit) is { Op: Opcode.ArrayLength } ? Reach : 0) + c + 1 - k;
            if (headroom >= slack && Same(register, block, at, guardRead, (factBlock, factIndex))) return true;
        }
        return false;
    }

    /// <summary>
    /// A write of `register` that is `register + k` (k from 1 to Reach+1),
    /// directly or as a copy of a register made so: k, and where the add read
    /// the register.
    /// </summary>
    private (long Amount, Block Block, int Index)? Step(Instr write, VReg register, Block block, int index)
    {
        if (write.Op is Opcode.Add && write.Operands.Count == 2
            && (write.Operands[0] is RegOperand { Reg: var a } && a == register && write.Operands[1] is ImmOperand { Value: >= 1 and <= Reach + 1 } x))
            return (x.Value, block, index);
        if (write.Op is Opcode.Add && write.Operands.Count == 2
            && (write.Operands[1] is RegOperand { Reg: var b } && b == register && write.Operands[0] is ImmOperand { Value: >= 1 and <= Reach + 1 } y))
            return (y.Value, block, index);
        if (write.Op == Opcode.Copy && write.Operands[0] is RegOperand { Reg: var from } && Affine(from) is { At: { } site } made
            && made.Counter == register && made.Offset is >= 1 and <= Reach + 1)
            return (made.Offset, site.Block, site.Index);
        return null;
    }

    /// <summary>
    /// Whether `register + k`, read at (block, at), cannot wrap: a fact
    /// `register + c &lt; anything` with c + 1 &gt;= k holds there, its own sum
    /// unable to wrap (c is 0, or the register is upper-bounded).
    /// </summary>
    private bool SafeStep(VReg register, long k, Block block, int at)
    {
        foreach ((VReg less, Operand _, Block factBlock, int factIndex) in Facts(block))
        {
            (VReg guarded, long c, var guardRead) = Affine(less);
            if (guarded != register || c < 0 || c + 1 < k || !Same(register, block, at, guardRead, (factBlock, factIndex))) continue;
            if (c == 0 || UpperBounded(register, c, new HashSet<VReg>())) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether two registers each hold the length of the same array -- one
    /// never written again, so whichever read it, it is one array, whose
    /// length never changes, however far apart the reads. The loop's test
    /// reads the length once; the bounds check after a call in its body reads
    /// it again, into a register of its own.
    /// </summary>
    private bool SameLength(VReg a, VReg b)
    {
        return Length(a) is { } x && Length(b) is { } y && x == y
            && (_defs.IsSingle(x) || _defs.Count(x) == 0 && _f.Params.Contains(x));

        VReg? Length(VReg r) => _defs.Definition(r) is { Op: Opcode.ArrayLength, Operands: [RegOperand { Reg: var array }] } ? array : null;
    }

    /// <summary>
    /// Whether no write of `register` lies on a path from (from, fromIndex)
    /// to (use, useIndex) -- Defs.CanForward, for the loop counter too, which
    /// is written more than once and which CanForward does not answer for.
    /// </summary>
    private bool Unchanged(VReg register, Block from, int fromIndex, Block use, int useIndex)
    {
        if (_defs.IsSingle(register)) return _defs.CanForward(register, from, fromIndex, use, useIndex);
        if (ReferenceEquals(from, use) && useIndex <= fromIndex) return false;
        if (!Writes().TryGetValue(register, out var writes)) return true;
        // On one straight run of a block: only a write between the two counts.
        if (ReferenceEquals(from, use))
            return !writes.Any(w => ReferenceEquals(w.Block, from) && w.Index > fromIndex && w.Index < useIndex);
        foreach ((Block block, int index) in writes)
        {
            if (ReferenceEquals(block, from) && index > fromIndex)
            {
                // After the compare in its own block: on the way to the use
                // unless the use comes first on the same straight run.
                if (!(ReferenceEquals(use, from) && useIndex <= index)) return false;
                continue;
            }
            if (ReferenceEquals(block, use))
            {
                // In the use's block: before the use it is on every way in;
                // after it, a path reaches the use before reaching the write.
                if (index < useIndex && !ReferenceEquals(block, from)) return false;
                continue;
            }
            if (ReferenceEquals(block, from)) continue;   // before the compare: a path back to it passes the compare again
            if (_cfg.ReachesWithoutReentering(from, block) && ReachesAvoiding(block, use, from)) return false;
        }
        return true;
    }

    /// <summary>Whether a path leads from `start` (after it) to `target` without passing through `avoid`.</summary>
    private bool ReachesAvoiding(Block start, Block target, Block avoid)
    {
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new();
        foreach (Block next in _cfg.Succs(start)) work.Push(next);
        while (work.TryPop(out Block? b))
        {
            if (ReferenceEquals(b, target)) return true;
            if (ReferenceEquals(b, avoid) || !seen.Add(b)) continue;
            foreach (Block next in _cfg.Succs(b)) work.Push(next);
        }
        return false;
    }

    /// <summary>Every write of every register, by register.</summary>
    private Dictionary<VReg, List<(Block Block, int Index)>> Writes()
    {
        if (_writes is not null) return _writes;
        _writes = new();
        foreach (Block block in _f.Blocks)
            for (int k = 0; k < block.Instrs.Count; k++)
                if (block.Instrs[k].Dest is { } dest)
                {
                    if (!_writes.TryGetValue(dest, out var list)) _writes[dest] = list = new();
                    list.Add((block, k));
                }
        return _writes;
    }
}
}
