using System.Security.Cryptography;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

public static class LinkSummary
{
    public static void AddConstantReturns(Module module, ObjectFile obj, OptimizationSummary summary)
    {
        foreach (Function function in module.Functions)
        {
            if (function.Returns != IrType.I32 || function.Params.Count != 0 || function.Async is not null
                || function.Blocks.Count != 1 || function.Blocks[0].IsLandingPad) continue;
            Dictionary<VReg, int> values = new();
            int? result = null;
            bool valid = true;
            foreach (Instr instruction in function.Blocks[0].Instrs)
            {
                int? Value(Operand operand) => operand is ImmOperand literal && literal.Type == IrType.I32
                    ? unchecked((int)literal.Value)
                    : operand is RegOperand register && values.TryGetValue(register.Reg, out int known) ? known : null;
                if (result is not null) { valid = false; break; }
                if (instruction.Op == Opcode.Copy && instruction.Dest is { Type: IrType.I32 } dest
                    && instruction.Operands.Count == 1 && Value(instruction.Operands[0]) is int copy)
                    values[dest] = copy;
                else if (instruction.Op == Opcode.Ret && instruction.Operands.Count == 1)
                    result = Value(instruction.Operands[0]);
                else { valid = false; break; }
            }
            if (!valid || result is not int constant) continue;
            Symbol? symbol = obj.Symbols.FirstOrDefault(s => s.Name == function.Name && s.IsDefined && s.IsFunction);
            if (symbol is null) continue;
            summary.Returns.Add(new ConstantReturn(function.Name, constant,
                OptimizationSummary.HashCode(symbol.Section!, (int)symbol.Offset, (int)symbol.Size)));
        }
    }
}
