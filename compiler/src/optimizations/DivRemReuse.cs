#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Reuse an earlier same-block quotient to compute its remainder.</summary>
public sealed class DivRemReuse : IPass
{
    public string Name => "div-rem-reuse";
    private sealed record Available(VReg Quotient, Operand Numerator, Operand Divisor);

    public void Run(Function f)
    {
        // One table for the function, and a block copied only once a
        // remainder in it is rewritten: this ran over every instruction of
        // every function on every round, copying each block whole.
        Dictionary<string, Available> available = new();
        List<string> stale = new();
        foreach (var block in f.Blocks)
        {
            available.Clear();
            List<Instr>? result = null;
            for (int n = 0; n < block.Instrs.Count; n++)
            {
                Instr i = block.Instrs[n];
                string? key = Key(i);
                if (key is not null && i.Op is Opcode.RemS or Opcode.RemU
                    && available.TryGetValue(key, out Available? previous))
                {
                    // The original division stays before this point, retaining
                    // zero/overflow traps and their order. No operands move.
                    VReg product = f.NewReg(i.Dest!.Type, "quotientProduct");
                    result ??= block.Instrs.GetRange(0, n);
                    result.Add(new Instr { Op = Opcode.Mul, Dest = product,
                        Operands = { new RegOperand(previous.Quotient), i.Operands[1] }, Line = i.Line });
                    result.Add(new Instr { Op = Opcode.Sub, Dest = i.Dest,
                        Operands = { i.Operands[0], new RegOperand(product) }, Line = i.Line });
                }
                else result?.Add(i);

                if (i.Dest is { } dest && available.Count != 0)
                {
                    foreach (var pair in available)
                        if (pair.Value.Quotient == dest || Uses(pair.Value.Numerator, dest) || Uses(pair.Value.Divisor, dest))
                            stale.Add(pair.Key);
                    foreach (string gone in stale) available.Remove(gone);
                    stale.Clear();
                }
                if (key is not null && i.Op is Opcode.DivS or Opcode.DivU && i.Dest is { } quotient
                    && !i.Operands.Any(o => Uses(o, quotient)))
                {
                    if (available.Count >= 64) available.Clear();
                    available[key] = new(quotient, i.Operands[0], i.Operands[1]);
                }
            }
            if (result is not null) { block.Instrs.Clear(); block.Instrs.AddRange(result); }
        }
    }

    private static bool Uses(Operand operand, VReg register) => operand is RegOperand r && r.Reg == register;
    private static string? Key(Instr i)
    {
        if (i.Op is not (Opcode.DivS or Opcode.DivU or Opcode.RemS or Opcode.RemU)
            || i.Dest?.Type is not (IrType.I32 or IrType.I64) || i.Operands.Count != 2
            || i.Operands.Any(o => o is not (RegOperand or ImmOperand))) return null;
        string Part(Operand o) => o is RegOperand r ? "r" + r.Reg.Id : "c" + ((ImmOperand)o).Value;
        return (i.Op is Opcode.DivS or Opcode.RemS ? "s" : "u") + i.Dest.Type
            + ":" + Part(i.Operands[0]) + ":" + Part(i.Operands[1]);
    }
}
