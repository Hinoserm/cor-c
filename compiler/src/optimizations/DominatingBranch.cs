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
        if (!repeated) return;
        Cfg cfg = new(function);
        if (cfg.Roots.Count != 1) return;
        Defs defs = new(cfg);
        foreach (Block block in function.Blocks)
        {
            if (block.Terminator is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var condition }] } branch
                || branch.Targets.Count != 2 || branches.GetValueOrDefault(condition) < 2 || !defs.IsSingle(condition)) continue;
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
                child = parent;
            }
        }
    }
}
