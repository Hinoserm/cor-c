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
        Dictionary<VReg, List<(Block Block, int Index)>>? accessed = null;
        try { Walk(); }
        // The thread's answers hold this function's blocks: let them go.
        finally { _asked?.Clear(); }
        void Walk()
        {
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
                if (!made) made = MadeBefore(function, block, receiver, ref cfg, ref defs, ref accessed);
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
    }

    /// <summary>
    /// Whether a block strictly dominating `block` reads or writes through
    /// `receiver`, written once (or a parameter), after it is written: it
    /// faulted there if it was null, and holds the same value here. The
    /// accesses are found once a function (Accessed), with the definitions,
    /// both as the function stood then: a check dropped since only shifts
    /// later places in its block down, keeping each block's order.
    /// </summary>
    private static bool MadeBefore(Function function, Block block, VReg receiver, ref Cfg? cfg, ref Defs? defs, ref Dictionary<VReg, List<(Block Block, int Index)>>? accessed)
    {
        cfg ??= PipelineAnalyses.CfgOf(function);
        if (cfg.Roots.Count != 1) return false;
        defs ??= PipelineAnalyses.DefsOf(function);
        accessed ??= Accessed(function);
        if (!defs.IsSingle(receiver) || !accessed.TryGetValue(receiver, out var at)) return false;
        // Asked once a receiver and block: `this`'s checks in one block are many.
        if (Asked.TryGetValue((receiver, block), out bool known)) return known;
        bool answer = false;
        (Block Block, int Index)? site = defs.Site(receiver) is { } s ? (s.Block, s.Index) : null;
        foreach ((Block where, int index) in at)
        {
            if (ReferenceEquals(where, block) || !cfg.Dominates(where, block)) continue;
            if (site is { } d && ReferenceEquals(d.Block, where) && index <= d.Index) continue;
            if (site is { } w && !ReferenceEquals(w.Block, where) && !cfg.Dominates(w.Block, where)) continue;
            answer = true;
            break;
        }
        Asked[(receiver, block)] = answer;
        return answer;
    }

    /// <summary>MadeBefore's answers this run, by receiver and block; cleared as each function starts.</summary>
    [ThreadStatic] private static Dictionary<(VReg, Block), bool>? _asked;
    private static Dictionary<(VReg, Block), bool> Asked => _asked ??= new();

    /// <summary>Every register read or written through within the null page, and where.</summary>
    private static Dictionary<VReg, List<(Block Block, int Index)>> Accessed(Function function)
    {
        Dictionary<VReg, List<(Block Block, int Index)>> accessed = new();
        foreach (Block b in function.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
                if (b.Instrs[k] is { Operands: [RegOperand { Reg: var r }, ..] } i && Accesses(i, r))
                    (accessed.TryGetValue(r, out var list) ? list : accessed[r] = new()).Add((b, k));
        return accessed;
    }

    /// <summary>Whether an instruction reads or writes through `receiver` within the page a null fault covers.</summary>
    private static bool Accesses(Instr i, VReg receiver)
        => i.Op is Opcode.Load or Opcode.Store or Opcode.ArrayLength && !i.Immutable && i.Operands.Count > 0
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
