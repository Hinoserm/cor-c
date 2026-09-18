#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Recognize the top-bit generate/propagate identity for unsigned addition carry.</summary>
public sealed class CarryRecognition : IPass
{
    public string Name => "carry-recognition";
    public void Run(Function f)
    {
        Defs defs = new(f);
        Dictionary<VReg, int> uses = new();
        foreach (var block in f.Blocks)
            foreach (Instr i in block.Instrs)
                foreach (VReg r in IrInfo.Uses(i)) uses[r] = uses.GetValueOrDefault(r) + 1;
        Dictionary<Instr, List<Instr>> replacements = new();
        foreach (var block in f.Blocks)
        for (int k = 0; k < block.Instrs.Count; k++)
        {
            Instr root = block.Instrs[k];
            if (root.Dest?.Type is not (IrType.I32 or IrType.I64) || root.Op != Opcode.And
                || root.Operands.Count != 2) continue;
            Operand? shifted = IrInfo.IsImm(root.Operands[1], 1) ? root.Operands[0]
                : IrInfo.IsImm(root.Operands[0], 1) ? root.Operands[1] : null;
            if (shifted is null) continue;
            Instr? shift = Node(shifted, Opcode.ShrS) ?? Node(shifted, Opcode.ShrU);
            if (shift is null || !IrInfo.IsImm(shift.Operands[1], root.Dest.Type == IrType.I64 ? 63 : 31)) continue;
            Instr? combine = Node(shift.Operands[0], Opcode.Or);
            if (combine is null) continue;
            for (int order = 0; order < 2; order++)
            {
                Instr? generate = Node(combine.Operands[order], Opcode.And);
                Instr? propagate = Node(combine.Operands[1 - order], Opcode.And);
                if (generate is null || propagate is null) continue;
                for (int side = 0; side < 2; side++)
                {
                    Instr? either = Node(propagate.Operands[side], Opcode.Or);
                    Instr? inverse = Node(propagate.Operands[1 - side], Opcode.Not);
                    if (either is null || inverse is null || !SamePair(generate, either)) continue;
                    Instr? sum = Node(inverse.Operands[0], Opcode.Add, exclusive: false);
                    if (sum is null || !SamePair(generate, sum)) continue;
                    // For s = a+b modulo 2^w, carry iff unsigned(s) < unsigned(a).
                    // Keep the original sum and all memory/trap operations in
                    // place. Only single-use boolean intermediates disappear.
                    VReg carry = root.Dest.Type == IrType.I32 ? root.Dest : f.NewReg(IrType.I32, "carry");
                    List<Instr> replacement = new()
                    {
                        new Instr { Op = Opcode.LtU, Dest = carry, Line = root.Line,
                            Operands = { inverse.Operands[0], generate.Operands[0] } },
                    };
                    if (root.Dest.Type == IrType.I64)
                        replacement.Add(new Instr { Op = Opcode.ZExt32, Dest = root.Dest, Line = root.Line,
                            Operands = { new RegOperand(carry) } });
                    replacements[root] = replacement;
                    break;
                }
                if (replacements.ContainsKey(root)) break;
            }

            Instr? Node(Operand operand, Opcode op, bool exclusive = true)
            {
                if (operand is not RegOperand r || r.Type != root.Dest.Type
                    || (exclusive && uses.GetValueOrDefault(r.Reg) != 1)) return null;
                var site = defs.Site(r.Reg);
                if (site is null || site.Value.Block != block || site.Value.Index >= k) return null;
                Instr node = block.Instrs[site.Value.Index];
                if (node.Op != op || node.Operands.Count != (op == Opcode.Not ? 1 : 2)) return null;
                foreach (VReg input in IrInfo.Uses(node))
                    if (!defs.CanForward(input, block, site.Value.Index, block, k)) return null;
                return node;
            }
        }
        if (replacements.Count == 0) return;
        foreach (var block in f.Blocks)
        {
            List<Instr> result = new();
            foreach (Instr i in block.Instrs)
                if (replacements.TryGetValue(i, out var replacement)) result.AddRange(replacement);
                else result.Add(i);
            block.Instrs.Clear(); block.Instrs.AddRange(result);
        }
    }
    private static bool Same(Operand a, Operand b) => a.Type == b.Type &&
        (a is RegOperand ar && b is RegOperand br && ar.Reg == br.Reg
        || a is ImmOperand ai && b is ImmOperand bi && ai.Value == bi.Value);
    private static bool SamePair(Instr a, Instr b) =>
        Same(a.Operands[0], b.Operands[0]) && Same(a.Operands[1], b.Operands[1])
        || Same(a.Operands[0], b.Operands[1]) && Same(a.Operands[1], b.Operands[0]);
}
