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
        // Where each register is written, by its number. Loops and arrays
        // throughout: the lambdas here were closures and boxed edge walks
        // for every block of every function.
        Block?[] sites = new Block?[function.RegCount];
        foreach (Block block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
                if (instruction.Dest is { } dest && dest.Id < sites.Length) sites[dest.Id] = block;

        foreach (Block header in function.Blocks)
        {
            Stack<Block>? todo = null;
            foreach (Block p in cfg.Preds(header))
                if (cfg.Dominates(header, p)) (todo ??= new()).Push(p);
            if (todo is null) continue;
            HashSet<Block> loop = new() { header };
            while (todo.TryPop(out Block? block))
                if (loop.Add(block)) foreach (Block pred in cfg.Preds(block)) todo.Push(pred);
            bool dominated = true;
            foreach (Block b in loop) if (!cfg.Dominates(header, b)) { dominated = false; break; }
            if (!dominated) continue;
            Block? preheader = null;
            int outside = 0;
            foreach (Block p in cfg.Preds(header))
                if (!loop.Contains(p) && outside++ == 0) preheader = p;
            if (outside != 1) continue;
            if (preheader!.Terminator?.Op != Opcode.Jump || cfg.Succs(preheader).Count != 1) continue;
            int budget = 16;
            bool changed;
            do
            {
                changed = false;
                foreach (Block block in function.Blocks)
                {
                if (!loop.Contains(block)) continue;
                foreach (Instr instruction in block.Instrs.ToArray())
                {
                    if (budget == 0 || instruction.Dest is not { } dest || !dest.Type.IsInt()
                        || !defs.IsSingle(dest) || !Safe(instruction.Op)) continue;
                    bool invariant = true;
                    foreach (Operand operand in instruction.Operands)
                    {
                        invariant = operand switch
                        {
                            ImmOperand or SymOperand or SlotOperand => true,
                            RegOperand reg => defs.IsSingle(reg.Reg)
                                && ((uint)reg.Reg.Id >= (uint)sites.Length || sites[reg.Reg.Id] is not { } site
                                    || (!loop.Contains(site) && cfg.Dominates(site, preheader!))),
                            _ => false,
                        };
                        if (!invariant) break;
                    }
                    if (!invariant) continue;
                    block.Instrs.Remove(instruction);
                    preheader!.Instrs.Insert(preheader.Instrs.Count - 1, instruction);
                    if (dest.Id < sites.Length) sites[dest.Id] = preheader;
                    budget--; changed = true;
                }
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
