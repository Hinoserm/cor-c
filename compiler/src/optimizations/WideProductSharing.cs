#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Expose the low product's word products when a nearby high-half expansion needs them too.</summary>
public sealed class WideProductSharing : IPass
{
    public string Name => "wide-product-sharing";
    public void Run(Function f)
    {
        Defs defs = new(f);
        Dictionary<Instr, List<Instr>> replacements = new();
        foreach (var block in f.Blocks)
        for (int k = 0; k < block.Instrs.Count; k++)
        {
            Instr low = block.Instrs[k];
            if (low.Op != Opcode.Mul || low.Dest?.Type != IrType.I64 || low.Operands.Count != 2
                || low.Operands[0] is not RegOperand a || low.Operands[1] is not RegOperand b
                || !defs.IsSingle(a.Reg) || !defs.IsSingle(b.Reg) || low.Dest == a.Reg || low.Dest == b.Reg) continue;
            int products = 0;
            for (int at = k + 1; at < block.Instrs.Count && at <= k + 128; at++)
            {
                Instr part = block.Instrs[at];
                if (part.Op != Opcode.Mul || part.Dest?.Type != IrType.I64 || part.Operands.Count != 2) continue;
                int x = Part(part.Operands[0], at), y = Part(part.Operands[1], at);
                if (Pair(x, y, 1, 4)) products |= 1;
                if (Pair(x, y, 1, 8)) products |= 2;
                if (Pair(x, y, 2, 4)) products |= 4;
                if (Pair(x, y, 2, 8)) products |= 8;
            }
            if (products != 15) continue;
            List<Instr> expanded = new();
            Operand mask = new ImmOperand(4294967295, IrType.I64), count = new ImmOperand(32, IrType.I32);
            VReg al = Binary(Opcode.And, a, mask), ah = Binary(Opcode.ShrU, a, count);
            VReg bl = Binary(Opcode.And, b, mask), bh = Binary(Opcode.ShrU, b, count);
            VReg p00 = Binary(Opcode.Mul, new RegOperand(al), new RegOperand(bl));
            VReg p01 = Binary(Opcode.Mul, new RegOperand(al), new RegOperand(bh));
            VReg p10 = Binary(Opcode.Mul, new RegOperand(ah), new RegOperand(bl));
            // Share the middle column itself as well as its products. Its
            // low word is the low result's high word, and its high word is
            // the carry consumed by the existing high-half expansion.
            VReg carry = Binary(Opcode.ShrU, new RegOperand(p00), count);
            VReg p01lo = Binary(Opcode.And, new RegOperand(p01), mask);
            VReg middle0 = Binary(Opcode.Add, new RegOperand(carry), new RegOperand(p01lo));
            VReg p10lo = Binary(Opcode.And, new RegOperand(p10), mask);
            VReg middle = Binary(Opcode.Add, new RegOperand(middle0), new RegOperand(p10lo));
            VReg upper = Binary(Opcode.Shl, new RegOperand(middle), count);
            VReg lower = Binary(Opcode.And, new RegOperand(p00), mask);
            expanded.Add(new Instr { Op = Opcode.Or, Dest = low.Dest, Line = low.Line,
                Operands = { new RegOperand(lower), new RegOperand(upper) } });
            replacements[low] = expanded;

            VReg Binary(Opcode op, Operand left, Operand right)
            {
                VReg result = f.NewReg(IrType.I64, "productpart");
                expanded.Add(new Instr { Op = op, Dest = result, Line = low.Line, Operands = { left, right } });
                return result;
            }
            int Part(Operand operand, int use)
            {
                if (operand is not RegOperand r || r.Type != IrType.I64) return 0;
                var site = defs.Site(r.Reg);
                if (site is null || site.Value.Block != block || site.Value.Index >= use) return 0;
                Instr extract = block.Instrs[site.Value.Index];
                if (extract.Operands.Count != 2 || extract.Operands[0] is not RegOperand source) return 0;
                bool lower = extract.Op == Opcode.And && IrInfo.IsImm(extract.Operands[1], 4294967295);
                bool upperHalf = extract.Op == Opcode.ShrU && IrInfo.IsImm(extract.Operands[1], 32);
                if (!lower && !upperHalf) return 0;
                if (!defs.CanForward(source.Reg, block, k, block, site.Value.Index)) return 0;
                return (source.Reg == a.Reg ? lower ? 1 : 2 : 0)
                    | (source.Reg == b.Reg ? lower ? 4 : 8 : 0);
            }
        }
        if (replacements.Count == 0) return;
        foreach (var block in f.Blocks)
        {
            List<Instr> result = new();
            foreach (Instr i in block.Instrs)
                if (replacements.TryGetValue(i, out var expanded)) result.AddRange(expanded);
                else result.Add(i);
            block.Instrs.Clear(); block.Instrs.AddRange(result);
        }
    }
    private static bool Pair(int a, int b, int x, int y) =>
        ((a & x) != 0 && (b & y) != 0) || ((a & y) != 0 && (b & x) != 0);
}
