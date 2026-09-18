using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>Keep managed-array construction facts across ordinary element writes.</summary>
public sealed class ArrayLengthFacts : IPass
{
    public string Name => "array-length-facts";

    public void Run(Function function)
    {
        if (function.Async is not null || !function.Blocks.Any(block => block.Instrs.Any(instruction => instruction.Op == Opcode.ArrayLength))) return;
        Cfg cfg = new(function); Defs defs = new(cfg);
        object? Root(Operand operand, Block use, int index, int depth = 0)
        {
            if (operand is SlotOperand slot) return slot.Slot;
            if (operand is not RegOperand register || depth >= 16 || !defs.IsSingle(register.Reg)) return null;
            if (defs.Site(register.Reg) is not { } site) return register.Reg;
            if (!cfg.Dominates(site.Block, use) || (site.Block == use && site.Index >= index)) return null;
            Instr definition = site.Block.Instrs[site.Index];
            if (definition.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)
            {
                if (!defs.CanForward(register.Reg, site.Block, site.Index + 1, use, index)
                    && !(site.Block == function.Entry && cfg.Preds(function.Entry).Count == 0)) return null;
                return Root(definition.Operands[0], site.Block, site.Index, depth + 1);
            }
            return register.Reg;
        }
        Dictionary<object, (Block Block, int Index, int Length)> initializers = new();
        HashSet<object> ambiguous = new();
        foreach (Block block in function.Blocks)
        for (int index = 0; index < block.Instrs.Count; index++)
        {
            Instr instruction = block.Instrs[index];
            if (instruction.Op != Opcode.InitArrayLength || instruction.Operands.Count != 2
                || Root(instruction.Operands[0], block, index) is not { } root) continue;
            if (instruction.Operands[1] is not ImmOperand { Value: >= 0 and <= int.MaxValue } length
                || !initializers.TryAdd(root, (block, index, (int)length.Value))) ambiguous.Add(root);
        }
        foreach (Block block in function.Blocks)
        for (int index = 0; index < block.Instrs.Count; index++)
        {
            Instr instruction = block.Instrs[index];
            if (instruction.Op != Opcode.ArrayLength || instruction.Dest?.Type != IrType.I32 || instruction.Operands.Count != 1
                || Root(instruction.Operands[0], block, index) is not { } root || ambiguous.Contains(root)
                || !initializers.TryGetValue(root, out var initializer) || !cfg.Dominates(initializer.Block, block)
                || (initializer.Block == block && initializer.Index >= index)) continue;
            block.Instrs[index] = new Instr { Op = Opcode.Copy, Dest = instruction.Dest, Line = instruction.Line,
                Operands = { new ImmOperand(initializer.Length, IrType.I32) } };
        }
    }
}
