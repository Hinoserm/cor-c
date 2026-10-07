#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Replaces uses of a register that is only ever a copy of something else
/// by that something: an immediate, or another register. Lowering makes a
/// fresh register for every constant and every temporary, so almost all
/// the immediates <see cref="ConstantFold"/> and <see cref="Peephole"/>
/// act on arrive through here.
///
/// Only single-definition registers are candidates: the IR is not SSA,
/// and a register with two definitions has no one value to substitute.
/// An immediate can go anywhere the register could. Another register can
/// go only where <see cref="Defs.CanForward"/> says it still holds the
/// value the copy read, which rules out the loop-carried cases; the copy
/// itself stays until <see cref="DeadCodeElimination"/> finds it unused.
/// Chains (<c>a = copy b; b = copy c</c>) resolve in one run because
/// substitution follows the chain to its end before checking.
/// </summary>
public sealed class ConstantAndCopyPropagation : IPass
{
    private readonly bool _ssa;

    /// <param name="ssa">Whether the function is in SSA form when the pass runs; see <see cref="Defs.Ssa"/>.</param>
    public ConstantAndCopyPropagation(bool ssa = false) => _ssa = ssa;

    public string Name => _ssa ? "propagate-ssa" : "propagate";

    public void Run(Function f)
    {
        Defs defs = _ssa ? PipelineAnalyses.SsaDefsOf(f) : PipelineAnalyses.DefsOf(f);
        Dictionary<VReg, (Operand Value, Block Block, int Index)> copies = new();
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Copy || i.Dest is null || !defs.IsSingle(i.Dest))
                {
                    continue;
                }
                Operand src = i.Operands[0];
                // A register copied to itself is a curiosity lowering can
                // produce for a self-assignment; nothing to forward.
                // A copy between registers of two widths is a width change:
                // `%r:I32 = copy %w:I64`. Forwarded, %w would stand where an
                // I32 is read -- a native call's argument pushed as two words,
                // every argument after it off by one.
                if (src is ImmOperand || src is RegOperand r && !ReferenceEquals(r.Reg, i.Dest) && defs.IsSingle(r.Reg) && r.Reg.Type == i.Dest.Type)
                {
                    copies[i.Dest] = (src, b, k);
                }
            }
        }
        if (copies.Count == 0)
        {
            return;
        }

        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                // In a loop, not ReplaceUses with a lambda: the lambda held the
                // position, a closure for every instruction of the function.
                OperandList operands = b.Instrs[k].Operands;
                for (int o = 0; o < operands.Count; o++)
                    if (operands[o] is RegOperand { Reg: var r } && copies.ContainsKey(r)
                        && Resolve(r, defs, copies, b, k) is { } resolved)
                        operands[o] = resolved;
            }
        }
    }

    /// <summary>What a use of <paramref name="r"/> at the given point may become, or null to leave it.</summary>
    private static Operand? Resolve(VReg r, Defs defs, Dictionary<VReg, (Operand Value, Block Block, int Index)> copies, Block useBlock, int useIndex)
    {
        Operand? best = null;
        VReg cur = r;
        // A chain longer than there are copies has come round on itself: the
        // bound is the cycle test, where a set of the registers seen was one
        // more allocation for every use.
        for (int steps = 0; steps <= copies.Count && copies.TryGetValue(cur, out (Operand Value, Block Block, int Index) c); steps++)
        {
            if (c.Value is ImmOperand imm)
            {
                // At the width of the register it stands for.
                return imm.Type == r.Type ? imm : new ImmOperand(IrInfo.Normalise(imm.Value, r.Type), r.Type);
            }
            VReg next = ((RegOperand)c.Value).Reg;
            // The copy read `next` at its own position; the use is elsewhere.
            // Forwarding is a claim that `next` has not changed on the way,
            // and the chain stops at the first link where that is not so.
            if (!defs.CanForward(next, c.Block, c.Index, useBlock, useIndex))
            {
                break;
            }
            best = c.Value;
            cur = next;
        }
        return best;
    }
}
