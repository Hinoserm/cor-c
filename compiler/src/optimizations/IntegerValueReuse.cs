#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Integer expression reuse on unambiguous paths without assuming SSA.</summary>
public sealed class IntegerValueReuse : IPass
{
    public string Name => "integer-value-reuse";
    private sealed record Value(VReg Result, HashSet<VReg> Inputs);

    public void Run(Function f)
    {
        Cfg cfg = new(f);
        Dictionary<Corsac.Lang.Ir.Block, Dictionary<string, Value>> atEnd = new();
        foreach (var block in cfg.ReversePostorder)
        {
            Dictionary<string, Value> values = new();
            Dictionary<VReg, VReg> aliases = new();
            var predecessors = cfg.Preds(block);
            if (!cfg.IsRoot(block) && predecessors.Count == 1 && cfg.Dominates(predecessors[0], block)
                && atEnd.TryGetValue(predecessors[0], out var inherited))
                values = new(inherited);
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr i = block.Instrs[k];
                if (i.Op == Opcode.Phi) { values.Clear(); aliases.Clear(); continue; }
                // A CSE result is available to later expressions immediately,
                // not only after another whole pipeline round. Aliases are
                // canonical snapshots and are invalidated on either write.
                IrInfo.ReplaceUses(i, r => aliases.TryGetValue(r, out VReg? source) ? new RegOperand(source) : null);
                string? key = Key(i);
                VReg? reused = null;
                if (key is not null && values.TryGetValue(key, out Value? existing))
                {
                    reused = aliases.GetValueOrDefault(existing.Result, existing.Result);
                    block.Instrs[k] = IrInfo.CopyOf(i, new RegOperand(reused));
                }
                if (i.Dest is not { } dest) continue;
                aliases.Remove(dest);
                foreach (VReg stale in aliases.Where(p => p.Value == dest).Select(p => p.Key).ToArray()) aliases.Remove(stale);
                if (reused is not null && reused != dest)
                {
                    if (aliases.Count >= 256) aliases.Clear();
                    aliases[dest] = reused;
                }
                foreach (string stale in values.Where(p => p.Value.Result == dest || p.Value.Inputs.Contains(dest))
                    .Select(p => p.Key).ToArray()) values.Remove(stale);
                if (key is not null)
                {
                    HashSet<VReg> inputs = i.Operands.OfType<RegOperand>().Select(o => o.Reg).ToHashSet();
                    // The key describes values before this instruction. If
                    // it overwrites an input, that key is no longer current.
                    if (inputs.Contains(dest)) continue;
                    if (values.Count >= 128) values.Clear();
                    values[key] = new(dest, inputs);
                }
            }
            atEnd[block] = values;
        }
    }

    private static string? Key(Instr i)
    {
        if (i.Dest?.Type is not (IrType.I32 or IrType.I64)
            || i.Operands.Any(o => o is not (RegOperand or ImmOperand))) return null;
        // No memory, calls, division traps, floating point, flags or atomics.
        bool binary = i.Op is Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor
            or Opcode.Shl or Opcode.ShrS or Opcode.ShrU or Opcode.Eq or Opcode.Ne
            or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU;
        bool unary = i.Op is Opcode.Neg or Opcode.Not or Opcode.ByteSwap or Opcode.SExt8 or Opcode.SExt16
            or Opcode.ZExt8 or Opcode.ZExt16 or Opcode.SExt32 or Opcode.ZExt32 or Opcode.Trunc64;
        if ((!binary || i.Operands.Count != 2) && (!unary || i.Operands.Count != 1)) return null;
        string Part(Operand o) => o.Type + ":" + (o is RegOperand r ? "r" + r.Reg.Id : "c" + ((ImmOperand)o).Value);
        string first = Part(i.Operands[0]);
        string second = binary ? Part(i.Operands[1]) : "";
        if (binary && i.Op is Opcode.Add or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor or Opcode.Eq or Opcode.Ne
            && StringComparer.Ordinal.Compare(first, second) > 0) (first, second) = (second, first);
        return i.Op + ":" + i.Dest.Type + ":" + first + ":" + second;
    }
}
