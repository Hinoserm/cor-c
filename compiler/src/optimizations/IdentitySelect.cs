#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A CHOICE THAT IS ITS OWN OPERAND, NOT MADE: `r = x == 0 ? 0 : x` -- an
/// inlined null test's `p == null ? null : p`, a guard's "zero or the value"
/// -- is `r = x` whichever way it goes:
///
///     B: c = eq x 0                       B: r = copy x
///        branch c -> T, F         =>         jump J
///     T: r = copy 0 ; jump J
///     F: r = copy x ; jump J
///
/// (and `ne`, its arms the other way round). Left as it was, the backend
/// made it a test, a move of 0 and a CMOV on a register just zeroed --
/// UserMode.Current's, at every one of its 1,216 call sites. T and F are
/// left to the cleanup when nothing else jumps to them.
/// </summary>
public sealed class IdentitySelect : IPass
{
    public string Name => "identity-select";

    public void Run(Function f)
    {
        bool changed = false;
        foreach (Block b in f.Blocks)
        {
            if (b.Instrs.Count < 2 || b.Instrs[^1] is not { Op: Opcode.Branch } branch || branch.Targets.Count != 2
                || branch.Operands is not [RegOperand { Reg: var condition }]) continue;
            Instr test = b.Instrs[^2];
            if (test.Dest != condition || test.Op is not (Opcode.Eq or Opcode.Ne) || test.Operands.Count != 2) continue;
            // The tested register and the zero, either way round.
            VReg? x = test.Operands switch
            {
                [RegOperand { Reg: var a }, ImmOperand { Value: 0 }] => a,
                [ImmOperand { Value: 0 }, RegOperand { Reg: var a }] => a,
                _ => null,
            };
            if (x is null) continue;
            // `eq`: the true arm is the zero one; `ne`: the false arm is.
            Block zeroArm = test.Op == Opcode.Eq ? branch.Targets[0] : branch.Targets[1];
            Block valueArm = test.Op == Opcode.Eq ? branch.Targets[1] : branch.Targets[0];
            if (ReferenceEquals(zeroArm, valueArm) || ReferenceEquals(zeroArm, b) || ReferenceEquals(valueArm, b)) continue;
            if (Arm(zeroArm) is not { } zero || Arm(valueArm) is not { } value) continue;
            if (!ReferenceEquals(zero.Join, value.Join) || zero.Copy.Dest != value.Copy.Dest || zero.Copy.Dest is null) continue;
            if (zero.Copy.Operands is not [ImmOperand { Value: 0 }]) continue;
            if (value.Copy.Operands is not [RegOperand { Reg: var copied }] || copied != x) continue;
            // B copies and jumps; the test stays for any other reader of c,
            // the cleanup drops it where there is none.
            Instr result = new() { Op = Opcode.Copy, Dest = zero.Copy.Dest };
            result.Operands.Add(RegOperand.Of(x));
            Instr jump = new() { Op = Opcode.Jump, InitialTargets = new[] { zero.Join } };
            b.Instrs[^1] = result;
            b.Instrs.Add(jump);
            changed = true;
        }
        // A BRANCH ON A CONSTANT A PREDECESSOR JUST SET, THREADED: P ends
        // `v = copy K; jump J`, and J is only `c = eq|ne v K'; branch c`
        // with c read nowhere else -- P jumps to the side J would take.
        // UserMode.Current's thread-block-zero path set its answer to 0
        // and then tested it: `xor; test; jne` on a known zero.
        Dictionary<VReg, int> reads = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is RegOperand { Reg: var r }) reads[r] = reads.GetValueOrDefault(r) + 1;
        foreach (Block p in f.Blocks)
        {
            if (p.Instrs.Count < 2 || p.Instrs[^1] is not { Op: Opcode.Jump, Targets.Count: 1 } jumpTo) continue;
            Block j = jumpTo.Targets[0];
            // J: copies, then the test, then the branch on it.
            int n = j.Instrs.Count;
            if (ReferenceEquals(j, p) || n < 2 || j.Instrs[n - 1] is not { Op: Opcode.Branch, Targets.Count: 2 } branch
                || j.Instrs[n - 2] is not { Op: Opcode.Eq or Opcode.Ne, Dest: { } c } compare
                || branch.Operands is not [RegOperand { Reg: var tested }] || tested != c || reads.GetValueOrDefault(c) != 1
                || compare.Operands is not [RegOperand { Reg: var compared }, ImmOperand { Value: var against }]) continue;
            List<Instr> copies = j.Instrs.Take(n - 2).ToList();
            if (copies.Any(i => i.Op != Opcode.Copy || i.Dest is null || i.Operands.Count != 1)) continue;
            // What P leaves known: its last copy of a constant.
            if (p.Instrs[^2] is not { Op: Opcode.Copy, Dest: { } set, Operands: [ImmOperand { Value: var known }] }) continue;
            Dictionary<VReg, long> constants = new() { [set] = known };
            foreach (Instr copy in copies)
            {
                if (copy.Operands[0] is ImmOperand { Value: var imm }) constants[copy.Dest!] = imm;
                else if (copy.Operands[0] is RegOperand { Reg: var from } && constants.TryGetValue(from, out long through)) constants[copy.Dest!] = through;
                else constants.Remove(copy.Dest!);
            }
            if (!constants.TryGetValue(compared, out long value)) continue;
            bool equal = value == against;
            bool taken = compare.Op == Opcode.Eq ? equal : !equal;
            // J's copies made on P's way too, before it goes on: what they
            // write may be read wherever the branch leads.
            p.Instrs.RemoveAt(p.Instrs.Count - 1);
            foreach (Instr copy in copies) p.Instrs.Add(copy.Clone());
            p.Instrs.Add(jumpTo);
            jumpTo.SetTarget(0, taken ? branch.Targets[0] : branch.Targets[1]);
            changed = true;
        }
        if (changed) f.Edited();
    }

    /// <summary>An arm of exactly one copy and a jump.</summary>
    private static (Instr Copy, Block Join)? Arm(Block arm)
        => arm.Instrs is [{ Op: Opcode.Copy, Operands.Count: 1 } copy, { Op: Opcode.Jump, Targets.Count: 1 } jump] ? (copy, jump.Targets[0]) : null;
}
