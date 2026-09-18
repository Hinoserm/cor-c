using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>Turn side-effect-free comparison ? -1 : 0 diamonds into masks.</summary>
public sealed class BooleanMaskDiamonds : IPass
{
    public string Name => "boolean-mask-diamonds";

    public void Run(Function function)
    {
        if (function.Async is not null) return;
        Cfg cfg = new(function);
        foreach (Block block in function.Blocks)
        {
            Instr? branch = block.Terminator;
            if (branch is not { Op: Opcode.Branch } || branch.Targets.Count != 2 || branch.Operands.Count != 1
                || branch.Operands[0] is not RegOperand condition) continue;
            Instr? producer = block.Instrs.LastOrDefault(i => i.Dest == condition.Reg);
            if (producer is null || producer.Op is not (Opcode.Eq or Opcode.Ne or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS
                or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU or Opcode.FEq or Opcode.FNe or Opcode.FLt or Opcode.FLe or Opcode.FGt or Opcode.FGe)) continue;
            Block yes = branch.Targets[0], no = branch.Targets[1];
            if (yes == no || cfg.IsRoot(yes) || cfg.IsRoot(no) || cfg.Preds(yes).Count != 1 || cfg.Preds(no).Count != 1
                || yes.Instrs.Count != 2 || no.Instrs.Count != 2) continue;
            Instr a = yes.Instrs[0], b = no.Instrs[0], aj = yes.Instrs[1], bj = no.Instrs[1];
            if (a.Op != Opcode.Copy || b.Op != Opcode.Copy || a.Dest is not { Type: IrType.I32 } destination || b.Dest != destination
                || a.Operands.Count != 1 || b.Operands.Count != 1 || a.Operands[0] is not ImmOperand { Value: -1 }
                || b.Operands[0] is not ImmOperand { Value: 0 } || aj.Op != Opcode.Jump || bj.Op != Opcode.Jump
                || aj.Targets.Count != 1 || bj.Targets.Count != 1 || aj.Targets[0] != bj.Targets[0]) continue;
            block.Instrs[^1] = new Instr { Op = Opcode.Neg, Dest = destination, Operands = { condition }, Line = branch.Line };
            block.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { aj.Targets[0] }, Line = branch.Line });
        }
        Cfg.RemoveUnreachable(function);
    }
}
