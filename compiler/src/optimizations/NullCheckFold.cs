#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A NULL CHECK THE NEXT ACCESS ALREADY MAKES, dropped. A call on a
/// receiver reads the receiver's first word only so that a null one faults
/// at the call (Lowering.CallMethodCore); once the callee is inlined, that
/// read's result is unused and the inlined body reads a field of the same
/// receiver straight after. `for (i = 0; i < list.Count; i++)` read the
/// list's header and then its count in every lap. The field read faults on
/// a null receiver exactly as the header read did -- any offset below the
/// 64 KB the null fault covers (Runtime.NullFaultCatchable) -- so the check
/// goes when the access follows in the same block with nothing between
/// that does anything but compute: no store, call or other read that could
/// fault or be seen before it. And it goes when an access before it in the
/// block, through the receiver as it still is, has made it already.
/// </summary>
public sealed class NullCheckFold : IPass
{
    public string Name => "null-check-fold";

    /// <summary>Within the first page: an access past it is not relied on to fault as a null one.</summary>
    private const long Covered = 4096;

    public void Run(Function function)
    {
        HashSet<VReg>? used = null;
        Cfg? cfg = null;
        Defs? defs = null;
        foreach (Block block in function.Blocks)
        {
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr check = block.Instrs[k];
                if (check.Op != Opcode.Load || check.Dest is not { } dest || check.Operands.Count != 1
                    || check.Operands[0] is not RegOperand { Reg: var receiver } || check.Offset is < 0 or >= Covered) continue;
                used ??= Used(function);
                if (used.Contains(dest)) continue;
                // Or one an earlier access already made: the receiver read
                // before in this block, and not written since, faulted then
                // if it was null -- the second sealed call on one element.
                bool made = false;
                for (int j = k - 1; j >= 0 && !made; j--)
                {
                    Instr before = block.Instrs[j];
                    if (before.Dest == receiver) break;
                    made = Accesses(before, receiver);
                }
                // Or an access in a block every way here passes through first,
                // through the receiver written once: a type context's chain
                // read off `this` in a cold arm, after the method's first
                // line read a field of it.
                if (!made) made = MadeBefore(function, block, receiver, ref cfg, ref defs);
                if (made)
                {
                    block.Instrs.RemoveAt(k);
                    k--;
                    continue;
                }
                for (int j = k + 1; j < block.Instrs.Count; j++)
                {
                    Instr next = block.Instrs[j];
                    if (Accesses(next, receiver))
                    {
                        block.Instrs.RemoveAt(k);
                        k--;
                        break;
                    }
                    // Anything else must be a computation: no effect to be
                    // seen before the fault, and the receiver not written.
                    if (!IrInfo.IsPure(next) || next.Op == Opcode.Phi || next.Dest == receiver) break;
                }
            }
        }
    }

    /// <summary>
    /// Whether a block strictly dominating `block` reads or writes through
    /// `receiver`, written once (or a parameter), after it is written: it
    /// faulted there if it was null, and holds the same value here.
    /// </summary>
    private static bool MadeBefore(Function function, Block block, VReg receiver, ref Cfg? cfg, ref Defs? defs)
    {
        cfg ??= PipelineAnalyses.CfgOf(function);
        if (cfg.Roots.Count != 1) return false;
        defs ??= PipelineAnalyses.DefsOf(function);
        if (!defs.IsSingle(receiver)) return false;
        (Block Block, int Index)? site = defs.Site(receiver) is { } s ? (s.Block, s.Index) : null;
        for (Block? up = cfg.Idom(block); up is not null; up = cfg.Idom(up))
        {
            int from = site is { } d && ReferenceEquals(d.Block, up) ? d.Index + 1 : 0;
            for (int j = from; j < up.Instrs.Count; j++)
                if (Accesses(up.Instrs[j], receiver)) return true;
            if (site is { } w && ReferenceEquals(w.Block, up)) return false;
        }
        return false;
    }

    /// <summary>Whether an instruction reads or writes through `receiver` within the page a null fault covers.</summary>
    private static bool Accesses(Instr i, VReg receiver)
        => i.Op is Opcode.Load or Opcode.Store or Opcode.ArrayLength && i.Operands.Count > 0
            && i.Operands[0] is RegOperand { Reg: var address } && address == receiver && i.Offset is >= 0 and < Covered;

    private static HashSet<VReg> Used(Function function)
    {
        HashSet<VReg> used = new();
        foreach (Block block in function.Blocks)
            foreach (Instr i in block.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is RegOperand r) used.Add(r.Reg);
        return used;
    }
}
