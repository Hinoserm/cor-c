#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Bookkeeping for <see cref="Opcode.Phi"/>: a phi's operands are keyed by
/// the predecessor they arrive from, so every pass that adds, removes or
/// redirects an edge while the function is in SSA form has to keep the
/// phis of the edge's target in step. These are the only ways to do that,
/// so the invariant "one operand per distinct predecessor" lives here.
/// </summary>
public static class Phi
{
    public static bool IsPhi(Instr i) => i.Op == Opcode.Phi;

    /// <summary>The phis of a block, which sit at its top.</summary>
    public static IEnumerable<Instr> Of(Block b)
    {
        foreach (Instr i in b.Instrs)
        {
            if (i.Op != Opcode.Phi)
            {
                yield break;
            }
            yield return i;
        }
    }

    public static bool HasPhis(Block b) => b.Instrs.Count > 0 && b.Instrs[0].Op == Opcode.Phi;

    /// <summary>The index of a predecessor in a phi, or -1.</summary>
    public static int IndexOf(Instr phi, Block pred)
    {
        for (int k = 0; k < phi.Targets.Count; k++)
        {
            if (ReferenceEquals(phi.Targets[k], pred))
            {
                return k;
            }
        }
        return -1;
    }

    /// <summary>The block no longer has <paramref name="pred"/> as a predecessor.</summary>
    public static void RemoveIncoming(Block b, Block pred)
    {
        foreach (Instr phi in Of(b))
        {
            int k = IndexOf(phi, pred);
            if (k >= 0)
            {
                phi.Operands.RemoveAt(k);
                phi.Targets.RemoveAt(k);
            }
        }
    }

    /// <summary>
    /// After a terminator of <paramref name="from"/> changed: if it no
    /// longer leads to <paramref name="to"/>, drop the phi entries for it.
    /// </summary>
    public static void DropEdgeIfGone(Block from, Block to)
    {
        foreach (Block s in from.Successors)
        {
            if (ReferenceEquals(s, to))
            {
                return;
            }
        }
        RemoveIncoming(to, from);
    }

    /// <summary>The predecessor <paramref name="oldPred"/> of the block is now called <paramref name="newPred"/>.</summary>
    public static void Rename(Block b, Block oldPred, Block newPred)
    {
        foreach (Instr phi in Of(b))
        {
            int k = IndexOf(phi, oldPred);
            if (k >= 0)
            {
                phi.Targets[k] = newPred;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="newPred"/> can become a predecessor of the
    /// block carrying the same values as <paramref name="viaPred"/> does:
    /// true if it is not one already, or if it is and every phi already
    /// agrees. Nothing is changed.
    /// </summary>
    public static bool CanAddIncoming(Block b, Block viaPred, Block newPred)
    {
        foreach (Instr phi in Of(b))
        {
            int k = IndexOf(phi, viaPred);
            int m = IndexOf(phi, newPred);
            if (k < 0)
            {
                return false;   // malformed; leave it alone
            }
            if (m >= 0 && !SameValue(phi.Operands[k], phi.Operands[m]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <paramref name="newPred"/> now reaches the block with the values
    /// <paramref name="viaPred"/> brought. Check <see cref="CanAddIncoming"/> first.
    /// </summary>
    public static void AddIncoming(Block b, Block viaPred, Block newPred)
    {
        foreach (Instr phi in Of(b))
        {
            int k = IndexOf(phi, viaPred);
            if (k >= 0 && IndexOf(phi, newPred) < 0)
            {
                phi.Operands.Add(phi.Operands[k]);
                phi.Targets.Add(newPred);
            }
        }
    }

    /// <summary>
    /// Turns the phis of a block that now has exactly one predecessor into
    /// copies, in place, so the block can be merged or simply read.
    /// </summary>
    public static void LowerSingleEntry(Block b)
    {
        for (int k = 0; k < b.Instrs.Count; k++)
        {
            Instr i = b.Instrs[k];
            if (i.Op != Opcode.Phi)
            {
                break;
            }
            if (i.Operands.Count == 1)
            {
                b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Operands = { i.Operands[0] }, Line = i.Line };
            }
        }
    }

    public static bool SameValue(Operand a, Operand b)
    {
        return (a, b) switch
        {
            (RegOperand x, RegOperand y) => ReferenceEquals(x.Reg, y.Reg),
            (ImmOperand x, ImmOperand y) => x.Type == y.Type && IrInfo.Normalise(x.Value, x.Type) == IrInfo.Normalise(y.Value, y.Type),
            (SymOperand x, SymOperand y) => x.Name == y.Name && x.Offset == y.Offset,
            (SlotOperand x, SlotOperand y) => ReferenceEquals(x.Slot, y.Slot),
            _ => false,
        };
    }
}
