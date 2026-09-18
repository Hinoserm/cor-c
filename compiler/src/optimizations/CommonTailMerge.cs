#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Share exactly identical instruction tails inside one function.
/// No extra calls, speculation, register renaming or changed source lines.
/// Staged for the large batch; phi/EH/indirect-entry functions are excluded.</summary>
public sealed class CommonTailMerge : IPass
{
    public string Name => "common-tail-merge";

    public void Run(Function function)
    {
        if (function.Async is not null || function.Blocks.Count < 2) return;
        Cfg cfg = new(function);
        if (cfg.Roots.Count != 1 || function.Blocks.SelectMany(b => b.Instrs).Any(i =>
            i.Op is Opcode.Phi or Opcode.Unwind or Opcode.LabelAddr or Opcode.StackPointer or Opcode.FramePointer)) return;
        Dictionary<string, List<Block>> groups = new(StringComparer.Ordinal);
        int budget = 8192;
        foreach (Block block in function.Blocks.ToArray())
        {
            if (cfg.IsRoot(block) || block.Terminator is not { } end) continue;
            string key = end + ":line=" + end.Line;
            if (!groups.TryGetValue(key, out List<Block>? candidates)) groups[key] = candidates = new();
            bool merged = false;
            foreach (Block other in candidates)
            {
                if (--budget < 0) return;
                int count = 0, limit = Math.Min(32, Math.Min(block.Instrs.Count, other.Instrs.Count));
                while (count < limit && Same(block.Instrs[^(count + 1)], other.Instrs[^(count + 1)])) count++;
                if (count == block.Instrs.Count && count == other.Instrs.Count)
                {
                    Redirect(function, block, other);
                    function.Blocks.Remove(block);
                    merged = true;
                    break;
                }
                // Require an IR-size reduction. Final machine-byte and branch
                // profitability still belongs to the batch's code-size gate.
                if (count < 3) continue;
                Block tail = function.NewBlock("shared_tail");
                tail.Instrs.AddRange(block.Instrs.Skip(block.Instrs.Count - count));
                block.Instrs.RemoveRange(block.Instrs.Count - count, count);
                other.Instrs.RemoveRange(other.Instrs.Count - count, count);
                block.Instrs.Add(new Instr { Op = Opcode.Jump, Line = end.Line, Targets = { tail } });
                other.Instrs.Add(new Instr { Op = Opcode.Jump, Line = end.Line, Targets = { tail } });
                merged = true;
                break;
            }
            if (!merged && candidates.Count < 64) candidates.Add(block);
        }
    }

    private static bool Same(Instr a, Instr b)
    {
        if (a.Op != b.Op || a.Dest != b.Dest || a.Line != b.Line || a.Callee != b.Callee
            || a.Size != b.Size || a.Signed != b.Signed || a.Offset != b.Offset
            || a.Default != b.Default || a.Operands.Count != b.Operands.Count
            || a.Targets.Count != b.Targets.Count) return false;
        for (int k = 0; k < a.Targets.Count; k++) if (a.Targets[k] != b.Targets[k]) return false;
        for (int k = 0; k < a.Operands.Count; k++)
        {
            Operand x = a.Operands[k], y = b.Operands[k];
            if (x.Type != y.Type) return false;
            bool equal = (x, y) switch
            {
                (RegOperand l, RegOperand r) => l.Reg == r.Reg,
                (ImmOperand l, ImmOperand r) => l.Value == r.Value,
                (SymOperand l, SymOperand r) => l.Name == r.Name && l.Offset == r.Offset,
                (SlotOperand l, SlotOperand r) => l.Slot == r.Slot,
                _ => false,
            };
            if (!equal) return false;
        }
        return true;
    }

    private static void Redirect(Function function, Block from, Block to)
    {
        foreach (Instr instruction in function.Blocks.SelectMany(b => b.Instrs))
        {
            for (int k = 0; k < instruction.Targets.Count; k++)
                if (instruction.Targets[k] == from) instruction.Targets[k] = to;
            if (instruction.Default == from) instruction.Default = to;
        }
    }
}
