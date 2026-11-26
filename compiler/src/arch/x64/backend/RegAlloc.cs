#nullable enable
using Corsac.Lang.X86;

namespace Corsac.Lang.X64;

/// <summary>
/// x86-64 REGISTER ALLOCATION: the function lowered into the one allocator's
/// form (X64Target) and allocated there (Corsac.Lang.RegAlloc.Allocator), with
/// i386's method and everything it has learnt -- holes, short reloads carried
/// along a run, a register kept over the reads before a spill point, a loop's
/// lap cost. The values nothing reads are pruned first, as i386's are (the
/// family's PruneUnusedValues); which registers an instruction names and
/// which of its operands may be memory is the family's too (Roles.RegsOf,
/// Foldable).
/// </summary>
internal static class Allocator
{
    internal static Gpr[] CalleeSaved => LongAbi.CalleeSaved;

    public static void Run(MFunction m)
    {
        Corsac.Lang.X86.Allocator.PruneUnusedValues(m);
        Corsac.Lang.RegAlloc.Allocator.Run(new X64Target(m).Lower());
    }
}
