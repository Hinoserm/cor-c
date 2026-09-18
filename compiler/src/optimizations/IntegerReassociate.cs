#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Combine constant integer chains without extending source lifetimes across writes.</summary>
public sealed class IntegerReassociate : IPass
{
    public string Name => "integer-reassociate";
    private sealed record Expression(Opcode Op, Operand Source, long Constant);

    public void Run(Function f)
    {
        foreach (var block in f.Blocks)
        {
            Dictionary<VReg, Expression> known = new();
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr i = block.Instrs[k];
                if (i.Op == Opcode.Phi) { known.Clear(); continue; }
                Expression? expression = Read(i);
                if (expression is { Source: RegOperand r }
                    && known.TryGetValue(r.Reg, out var previous) && previous.Op == expression.Op
                    && previous.Source.Type == i.Dest!.Type)
                {
                    long a = previous.Constant, b = expression.Constant;
                    long folded = expression.Op switch
                    {
                        Opcode.Add => unchecked(a + b), Opcode.Mul => unchecked(a * b),
                        Opcode.And => a & b, Opcode.Or => a | b, Opcode.Xor => a ^ b,
                        _ => throw new InvalidOperationException(),
                    };
                    if (i.Dest.Type == IrType.I32) folded = unchecked((int)folded);
                    expression = new(expression.Op, previous.Source, folded);
                    block.Instrs[k] = new Instr { Op = expression.Op, Dest = i.Dest, Line = i.Line,
                        Operands = { expression.Source, new ImmOperand(folded, i.Dest.Type) } };
                }
                if (i.Dest is not { } dest) continue;
                known.Remove(dest);
                foreach (var stale in known.Where(p => p.Value.Source is RegOperand source && source.Reg == dest)
                    .Select(p => p.Key).ToArray()) known.Remove(stale);
                if (expression is not null && expression.Source is RegOperand input && input.Reg != dest)
                {
                    if (known.Count >= 128) known.Clear();
                    known[dest] = expression;
                }
            }
        }
    }

    private static Expression? Read(Instr i)
    {
        if (i.Dest?.Type is not (IrType.I32 or IrType.I64) || i.Operands.Count != 2
            || i.Operands.Any(o => o.Type != i.Dest.Type)
            || i.Op is not (Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor)) return null;
        Operand source = i.Operands[0], constant = i.Operands[1];
        if (i.Op != Opcode.Sub && source is ImmOperand) (source, constant) = (constant, source);
        if (source is not RegOperand || constant is not ImmOperand c) return null;
        long value = i.Op == Opcode.Sub ? unchecked(-c.Value) : c.Value;
        if (i.Dest.Type == IrType.I32) value = unchecked((int)value);
        return new(i.Op == Opcode.Sub ? Opcode.Add : i.Op, source, value);
    }
}
