#nullable enable
using Corsac.Lang.X86;

namespace Corsac.Lang.X64;

/// <summary>
/// x86-64 REGISTER ALLOCATION: the function lowered into the one allocator's
/// form (X64Target) and allocated there (Corsac.Lang.RegAlloc.Allocator), with
/// i386's method and everything it has learnt -- holes, short reloads carried
/// along a run, a register kept over the reads before a spill point, a loop's
/// lap cost. Which registers an instruction names and which of its operands
/// may be memory is the family's (Roles.RegsOf, Foldable).
/// </summary>
internal static class Allocator
{
    internal static Gpr[] CalleeSaved => LongAbi.CalleeSaved;

    public static void Run(MFunction m)
    {
        PruneUnusedPoolLoads(m);
        Corsac.Lang.RegAlloc.Allocator.Run(new X64Target(m).Lower());
    }

    /// <summary>
    /// A FLOAT CONSTANT'S OWN LOAD, WHERE EVERY READER TOOK THE POOL'S WORD
    /// ITSELF (Select.Sse's XSource: `addsd xmm, [rip + constant]`): the
    /// register it filled is read by nothing, and the load goes -- a
    /// read-only word read for no one, which no later pass removes where the
    /// register is not written again in its block. i386's allocator drops
    /// these among the unused values it prunes (its PruneUnusedValues, by
    /// i386's own reading of what an instruction names); here only the
    /// pool's loads are taken.
    /// </summary>
    private static void PruneUnusedPoolLoads(MFunction m)
    {
        static bool PoolLoad(MInstr i) => i.Op == MOp.MovF && i.Operands.Count == 2 && i.Operands[0] is MReg { IsMachine: false }
            && i.Operands[1] is MMem { Base: null, Index: null, Symbol: { } pool } && pool.StartsWith(X86Backend.FloatPoolPrefix, StringComparison.Ordinal);
        if (!m.Blocks.Any(b => b.Instrs.Any(PoolLoad))) return;
        HashSet<int> read = new();
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach ((MReg reg, Roles.Role role, int _, bool _) in Roles.RegsOf(i, regs))
                    if (role != Roles.Role.Def) read.Add(reg.Id);
        foreach (MBlock b in m.Blocks)
            b.Instrs.RemoveAll(i => PoolLoad(i) && !read.Contains(((MReg)i.Operands[0]).Id));
    }
}
