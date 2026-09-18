#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Hoist only non-trapping integer expressions from natural loops
/// with an existing exclusive preheader. No memory or FP speculation.</summary>
public sealed class LoopInvariant : IPass
{
    public string Name => "loop-invariant";
    public void Run(Function function)
    {
        if (function.Async is not null || function.Blocks.Count == 0) return;
        Cfg cfg = new(function);
        if (cfg.Roots.Count != 1) return; // EH/indirect entry needs separate reasoning.
        Defs defs = new(cfg);
        Dictionary<VReg, Block> sites = new();
        foreach (Block block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
                if (instruction.Dest is { } dest) sites[dest] = block;

        foreach (Block header in function.Blocks)
        {
            Block[] latches = cfg.Preds(header).Where(p => cfg.Dominates(header, p)).ToArray();
            if (latches.Length == 0) continue;
            HashSet<Block> loop = new() { header };
            Stack<Block> todo = new(latches);
            while (todo.TryPop(out Block? block))
                if (loop.Add(block)) foreach (Block pred in cfg.Preds(block)) todo.Push(pred);
            if (loop.Any(b => !cfg.Dominates(header, b))) continue;
            Block[] outside = cfg.Preds(header).Where(p => !loop.Contains(p)).ToArray();
            if (outside.Length != 1) continue;
            Block preheader = outside[0];
            if (preheader.Terminator?.Op != Opcode.Jump || cfg.Succs(preheader).Count != 1) continue;
            int budget = 16;
            bool changed;
            do
            {
                changed = false;
                foreach (Block block in function.Blocks.Where(loop.Contains))
                foreach (Instr instruction in block.Instrs.ToArray())
                {
                    if (budget == 0 || instruction.Dest is not { } dest || !dest.Type.IsInt()
                        || !defs.IsSingle(dest) || !Safe(instruction.Op)) continue;
                    bool invariant = instruction.Operands.All(operand => operand switch
                    {
                        ImmOperand or SymOperand or SlotOperand => true,
                        RegOperand reg => defs.IsSingle(reg.Reg)
                            && (!sites.TryGetValue(reg.Reg, out Block? site)
                                || (!loop.Contains(site) && cfg.Dominates(site, preheader))),
                        _ => false,
                    });
                    if (!invariant) continue;
                    block.Instrs.Remove(instruction);
                    preheader.Instrs.Insert(preheader.Instrs.Count - 1, instruction);
                    sites[dest] = preheader;
                    budget--; changed = true;
                }
            } while (changed && budget > 0);
        }
    }
    private static bool Safe(Opcode op) => op is Opcode.Copy or Opcode.Add or Opcode.Sub
        or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor or Opcode.Shl or Opcode.ShrS
        or Opcode.ShrU or Opcode.Neg or Opcode.Not or Opcode.ByteSwap or Opcode.Eq or Opcode.Ne or Opcode.LtS
        or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU
        or Opcode.GeU or Opcode.SExt8 or Opcode.SExt16 or Opcode.ZExt8 or Opcode.ZExt16
        or Opcode.SExt32 or Opcode.ZExt32 or Opcode.Trunc64;
}
