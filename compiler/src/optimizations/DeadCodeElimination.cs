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
                foreach (VReg r in IrInfo.Uses(i))
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
                    if (i.Dest is null || uses.GetValueOrDefault(i.Dest) > 0 || !IrInfo.IsPure(i))
                    {
                        return false;
                    }
                    // Its operands lose a use each; that may free them next round.
                    foreach (VReg r in IrInfo.Uses(i))
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
}
