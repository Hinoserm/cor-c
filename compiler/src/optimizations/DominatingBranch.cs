#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A BRANCH ON A CONDITION ALREADY DECIDED ON THE WAY IN, made a jump. An
/// inlined getter that tests what its caller's loop has just tested --
/// `while (_pos &lt; _toks.Count) { ... Peek ... }` with Peek's own
/// `_pos &lt; _toks.Count ? ... : ...` -- left the value of the compare in a
/// register, the first branch on it and a second one under it: the compare
/// was made a byte with setcc, widened, and tested twice, where one compare
/// and one jump would do. Where a block is reached only through one side of
/// a branch on the same register, with the register not written since, its
/// own branch on it can go only one way, and jumps there; the compare is
/// then read once, and the selector fuses it with the branch that is left.
///
/// AND A BRANCH ON THE OPPOSITE COMPARE OF THE SAME OPERANDS. `if (n == 0)
/// return; ... x / n` tests n for zero twice, once as written and once as the
/// division's own check (Lowering.DivideByZeroCheck): `eq n 0` and `ne n 0`
/// are two registers, and the check stayed, with its call that throws -- in a
/// runtime routine the ownership proofs follow, one that may throw is one
/// that may keep what it was handed, and a collection's storage field was no
/// longer owned (1904). Eq and Ne of the same two operands decide each other
/// where the operands are registers written once, or constants.
/// </summary>
public sealed class DominatingBranch : IPass
{
    public string Name => "dominating-branch";

    public void Run(Function function)
    {
        if (function.Async is not null) return;
        // Most functions branch on no register twice: count before building a graph.
        Dictionary<VReg, int> branches = new();
        bool repeated = false;
        foreach (Block b in function.Blocks)
            if (b.Terminator is { Op: Opcode.Branch, Operands: [RegOperand { Reg: var c }] })
            {
                int n = branches.GetValueOrDefault(c) + 1;
                branches[c] = n;
                repeated |= n > 1;
            }
        Cfg? cfg = null;
        Defs? defs = null;
        // The compares branched on, by their operands: one pair may decide another.
        Dictionary<string, int>? compared = null;
        if (!repeated)
        {
            cfg = PipelineAnalyses.CfgOf(function);
            if (cfg.Roots.Count != 1) return;
            defs = PipelineAnalyses.DefsOf(function);
            compared = new();
            foreach (Block b in function.Blocks)
                if (b.Terminator is { Op: Opcode.Branch, Operands: [RegOperand { Reg: var c }] } && EqualityOf(defs, c) is { } key)
                    compared[key.Key] = compared.GetValueOrDefault(key.Key) + 1;
            if (!compared.Values.Any(n => n > 1)) return;
        }
        cfg ??= PipelineAnalyses.CfgOf(function);
        if (cfg.Roots.Count != 1) return;
        defs ??= PipelineAnalyses.DefsOf(function);
        foreach (Block block in function.Blocks)
        {
            if (block.Terminator is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var condition }] } branch
                || branch.Targets.Count != 2 || !defs.IsSingle(condition)) continue;
            (string Key, bool Equal)? mine = EqualityOf(defs, condition);
            if (branches.GetValueOrDefault(condition) < 2 && mine is null) continue;
            Block child = block;
            for (int depth = 0; depth < 32; depth++)
            {
                Block? parent = cfg.Idom(child);
                if (parent is null) break;
                Edges preds = cfg.Preds(child);
                if (preds.Count == 1 && ReferenceEquals(preds[0], parent) && !ReferenceEquals(parent, block)
                    && parent.Terminator is { Op: Opcode.Branch, Operands: [RegOperand { Reg: var earlier }] } decided
                    && earlier == condition && decided.Targets.Count == 2 && decided.Targets[0] != decided.Targets[1]
                    && defs.CanForward(condition, parent, parent.Instrs.Count - 1, block, block.Instrs.Count - 1))
                {
                    Block to = decided.Targets[0] == child ? branch.Targets[0] : branch.Targets[1];
                    block.Instrs[^1] = new Instr { Op = Opcode.Jump, InitialTargets = new[] { to }, Line = branch.Line };
                    break;
                }
                // THE SAME OPERANDS COMPARED FOR EQUALITY, the other way or the
                // same: what the earlier branch made of `a == b` on the way in
                // is what this one asks of `a != b`.
                if (mine is { } asked && preds.Count == 1 && ReferenceEquals(preds[0], parent) && !ReferenceEquals(parent, block)
                    && parent.Terminator is { Op: Opcode.Branch, Operands: [RegOperand { Reg: var before }] } known
                    && known.Targets.Count == 2 && known.Targets[0] != known.Targets[1] && defs.IsSingle(before)
                    && EqualityOf(defs, before) is { } was && was.Key == asked.Key)
                {
                    bool equalHere = known.Targets[0] == child ? was.Equal : !was.Equal;
                    bool taken = asked.Equal ? equalHere : !equalHere;
                    Block to = taken ? branch.Targets[0] : branch.Targets[1];
                    block.Instrs[^1] = new Instr { Op = Opcode.Jump, InitialTargets = new[] { to }, Line = branch.Line };
                    break;
                }
                child = parent;
            }
        }
    }

    /// <summary>
    /// What an equality compare is of, or null: `eq`/`ne` of two operands, each
    /// a constant or a register written once (so the same pair compared
    /// anywhere is the same question), as a key that does not depend on their
    /// order, and whether it asks for equal.
    /// </summary>
    private static (string Key, bool Equal)? EqualityOf(Defs defs, VReg condition)
    {
        if (!defs.IsSingle(condition) || defs.Definition(condition) is not { Op: Opcode.Eq or Opcode.Ne, Operands: [var left, var right] } compare) return null;
        string Name(Operand o) => o switch
        {
            ImmOperand imm => "i" + imm.Value,
            RegOperand { Reg: var r } when defs.IsSingle(r) => "r" + r.Id,
            _ => "",
        };
        string a = Name(left), b = Name(right);
        if (a.Length == 0 || b.Length == 0) return null;
        return (string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a, compare.Op == Opcode.Eq);
    }
}
