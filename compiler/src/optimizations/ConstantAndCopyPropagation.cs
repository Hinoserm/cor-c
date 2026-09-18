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
        Defs defs = new(f, _ssa);
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
                if (src is ImmOperand || src is RegOperand r && !ReferenceEquals(r.Reg, i.Dest) && defs.IsSingle(r.Reg))
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
                Instr i = b.Instrs[k];
                Block useBlock = b;
                int useIndex = k;
                IrInfo.ReplaceUses(i, r => Resolve(r, defs, copies, useBlock, useIndex));
            }
        }
    }

    /// <summary>What a use of <paramref name="r"/> at the given point may become, or null to leave it.</summary>
    private static Operand? Resolve(VReg r, Defs defs, Dictionary<VReg, (Operand Value, Block Block, int Index)> copies, Block useBlock, int useIndex)
    {
        Operand? best = null;
        HashSet<VReg> seen = new();
        VReg cur = r;
        while (copies.TryGetValue(cur, out (Operand Value, Block Block, int Index) c) && seen.Add(cur))
        {
            if (c.Value is ImmOperand)
            {
                return c.Value;
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
