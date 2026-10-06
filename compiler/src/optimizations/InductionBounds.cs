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
/// </summary>
public sealed class InductionBounds : IPass
{
    public string Name => "induction-bounds";

    private Function _f = null!;
    private Cfg _cfg = null!;
    private Defs _defs = null!;
    private Dictionary<VReg, List<(Block Block, int Index)>>? _writes;

    public void Run(Function function)
    {
        if (function.Async is not null || !Candidates(function)) return;
        _f = function;
        _cfg = new Cfg(function);
        if (_cfg.Roots.Count != 1) return;
        _defs = new Defs(_cfg);
        _writes = null;
        foreach (Block block in function.Blocks)
        {
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr test = block.Instrs[k];
                if (test.Op is not (Opcode.LtU or Opcode.GeU) || test.Dest is null || test.Operands.Count != 2
                    || test.Operands[0] is not RegOperand { Reg: var index } || test.Operands[1] is not RegOperand { Reg: var bound }
                    || index.Type != IrType.I32 || bound.Type != IrType.I32) continue;
                if (!Guarded(index, bound, block, k) || !NeverNegative(index, new HashSet<VReg>())) continue;
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
    /// Whether (block, at) is reached only through the true side of a signed
    /// `index &lt; bound` (or its false side's mirror), with neither register
    /// written since the compare. A null bound takes any.
    /// </summary>
    private bool Guarded(VReg index, VReg? bound, Block block, int at)
    {
        Block child = block;
        for (int depth = 0; depth < 32; depth++)
        {
            Block? parent = _cfg.Idom(child);
            if (parent is null) return false;
            Edges preds = _cfg.Preds(child);
            if (preds.Count == 1 && ReferenceEquals(preds[0], parent)
                && parent.Terminator is { Op: Opcode.Branch } branch && branch.Targets.Count == 2
                && branch.Targets[0] != branch.Targets[1] && branch.Operands[0] is RegOperand { Reg: var condition }
                && _defs.IsSingle(condition) && _defs.Site(condition) is { } site
                && _cfg.Dominates(site.Block, parent))
            {
                Instr compare = site.Block.Instrs[site.Index];
                bool truth = branch.Targets[0] == child;
                // index < bound: LtS true, GeS false; bound > index: GtS true, LeS false.
                (Operand Less, Operand More)? holds = (compare.Op, truth) switch
                {
                    (Opcode.LtS, true) or (Opcode.GeS, false) => (compare.Operands[0], compare.Operands[1]),
                    (Opcode.GtS, true) or (Opcode.LeS, false) => (compare.Operands[1], compare.Operands[0]),
                    _ => null,
                };
                if (holds is { } h && h.Less is RegOperand { Reg: var less } && less == index && less.Type == IrType.I32
                    && Unchanged(index, site.Block, site.Index, block, at)
                    && (bound is null || h.More is RegOperand { Reg: var more }
                        && (more == bound ? Unchanged(bound, site.Block, site.Index, block, at) : SameLength(more, bound))))
                    return true;
            }
            child = parent;
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

    /// <summary>Whether every value a register is ever given is at least zero (see the class).</summary>
    private bool NeverNegative(VReg register, HashSet<VReg> asking)
    {
        if (register.Type != IrType.I32 || _f.Params.Contains(register) || !asking.Add(register) || asking.Count > 8) return false;
        if (!Writes().TryGetValue(register, out var writes)) return false;
        foreach ((Block block, int index) in writes)
        {
            Instr write = block.Instrs[index];
            bool fine = write.Op switch
            {
                Opcode.Copy => write.Operands[0] switch
                {
                    ImmOperand { Value: >= 0 and <= int.MaxValue } => true,
                    RegOperand { Reg: var from } => Increment(from, register) is { } step
                        ? Guarded(register, null, step.Block, step.Index)
                        : NeverNegative(from, asking),
                    _ => false,
                },
                Opcode.Add => IsIncrementOf(write, register) && Guarded(register, null, block, index),
                _ => false,
            };
            if (!fine) return false;
        }
        return true;
    }

    /// <summary>Where `from` is made as `of + 1`, when it is written once and so.</summary>
    private (Block Block, int Index)? Increment(VReg from, VReg of)
    {
        if (!_defs.IsSingle(from) || _defs.Site(from) is not { } site) return null;
        return IsIncrementOf(site.Block.Instrs[site.Index], of) ? site : null;
    }

    private static bool IsIncrementOf(Instr add, VReg of)
        => add.Op == Opcode.Add && add.Operands.Count == 2
            && (add.Operands[0] is RegOperand { Reg: var a } && a == of && add.Operands[1] is ImmOperand { Value: 1 }
                || add.Operands[1] is RegOperand { Reg: var b } && b == of && add.Operands[0] is ImmOperand { Value: 1 });

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
