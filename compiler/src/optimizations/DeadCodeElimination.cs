#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Removes instructions whose result nobody reads and whose execution
/// nobody would notice. "Nobody reads" is judged over the whole function
/// by use count, not by liveness, because a register with a use anywhere
/// might be that use's only definition on some path -- the IR is not SSA
/// and the pass does not try to prove otherwise. The cost is that a dead
/// store to a register that is also assigned elsewhere survives; the
/// pay-off is that this is correct without a reaching-definitions pass.
///
/// Removing one instruction can orphan the ones that fed it, so it
/// repeats until nothing changes. Calls, stores, atomics, syscalls and
/// division stay whatever happens to their result; see
/// <see cref="IrInfo.IsPure"/> for the list and the reasons.
/// </summary>
public sealed class DeadCodeElimination : IPass
{
    public string Name => "dce";

    public void Run(Function f)
    {
        Dictionary<VReg, int> uses = new();
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                foreach (Operand rOperand in (i).Operands) if (rOperand is RegOperand { Reg: var r })
                {
                    uses[r] = uses.GetValueOrDefault(r) + 1;
                }
            }
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Block b in f.Blocks)
            {
                int removed = b.Instrs.RemoveAll(i =>
                {
                    // An object made and never looked at is no allocation at
                    // all: making it changes nothing anything can see.
                    if (i.Dest is null || uses.GetValueOrDefault(i.Dest) > 0
                        || !IrInfo.IsPure(i) && !(i.Op == Opcode.Call && Escape.IsAllocator(i.Callee)))
                    {
                        return false;
                    }
                    // Its operands lose a use each; that may free them next round.
                    foreach (Operand rOperand in (i).Operands) if (rOperand is RegOperand { Reg: var r })
                    {
                        uses[r]--;
                    }
                    return true;
                });
                if (removed > 0)
                {
                    changed = true;
                }
            }
        }

        Overwritten(f, uses);

        // A call whose result is unused still runs, but need not write a
        // register the allocator would otherwise have to find room for.
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null && uses.GetValueOrDefault(i.Dest) == 0
                    && i.Op is (Opcode.Call or Opcode.CallIndirect) && i.Callee != "__exception")
                {
                    i.Dest = null;
                }
            }
        }
    }

    /// <summary>
    /// A WRITE THE SAME BLOCK OVERWRITES before anything reads it: `x = 0;`
    /// then `x = made;`, as a foreach's enumerator is cleared and then set
    /// once the guarded walk around it has folded away. Left, the register
    /// has two writes, and what joins it is no longer one object. Only when
    /// nothing between can leave for a landing pad that might read the
    /// first value: no call, no load, no division -- a store only into an
    /// object just made, which cannot fault.
    /// </summary>
    private static void Overwritten(Function f, Dictionary<VReg, int> uses)
    {
        HashSet<VReg>? fresh = null;
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr first = b.Instrs[k];
                if (first.Dest is not { } d || !IrInfo.IsPure(first) || first.Op == Opcode.Load) continue;
                for (int n = k + 1; n < b.Instrs.Count; n++)
                {
                    Instr next = b.Instrs[n];
                    if (next.Operands.Any(o => o is RegOperand r && ReferenceEquals(r.Reg, d))) break;
                    if (ReferenceEquals(next.Dest, d))
                    {
                        foreach (Operand o in first.Operands)
                            if (o is RegOperand r) uses[r.Reg]--;
                        b.Instrs.RemoveAt(k--);
                        break;
                    }
                    if (next.Op == Opcode.Store)
                    {
                        fresh ??= Fresh(f);
                        if (next.Operands[0] is RegOperand at && fresh.Contains(at.Reg)) continue;
                        break;
                    }
                    if (!IrInfo.IsPure(next) || next.Op == Opcode.Load) break;
                }
            }
        }
    }

    /// <summary>Registers written once, with an object an allocator just made (or a copy of one).</summary>
    private static HashSet<VReg> Fresh(Function f)
    {
        Dictionary<VReg, int> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;
        HashSet<VReg> fresh = new();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d && writes[d] == 1 && !f.Params.Contains(d) && !fresh.Contains(d)
                        && (i.Op == Opcode.Call && Escape.IsAllocator(i.Callee)
                            || i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Operands[0] is RegOperand r && fresh.Contains(r.Reg)))
                    { fresh.Add(d); grew = true; }
        }
        return fresh;
    }
}
