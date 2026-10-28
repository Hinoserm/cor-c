#nullable enable
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X64;

/// <summary>
/// --count-spills on x86-64 (Corsac.Lang.RegAlloc.SpillCounter): each counted
/// block entered by pushfq, a 32-bit add and adc to each 64-bit total's two
/// halves and to its own run count, RIP-relative, then popfq -- nothing it
/// inserts touches a register or the flags. Run after every pass that moves
/// or removes a spill access (Peephole), so what is counted is what the
/// encoder writes.
/// </summary>
internal static class SpillCount
{
    public static void Run(MFunction m, SpillCounter counter)
    {
        counter.Count(m.Source.Name, m.Blocks.Count, b => Accesses(m.Blocks[b]), (b, reads, writes, at) =>
        {
            List<MInstr> count = new() { new MInstr(MOp.Pushf) };
            Add(count, SpillCounter.ReadsLow, SpillCounter.ReadsHigh, reads);
            Add(count, SpillCounter.WritesLow, SpillCounter.WritesHigh, writes);
            if (at >= 0)
            {
                count.Add(new MInstr(MOp.Add, MMem.Rip(SpillCounter.BlocksSymbol, at), new MImm(1)) { Width = 4 });
                count.Add(new MInstr(MOp.Adc, MMem.Rip(SpillCounter.BlocksSymbol, at + 4), new MImm(0)) { Width = 4 });
            }
            count.Add(new MInstr(MOp.Popf));
            m.Blocks[b].Instrs.InsertRange(0, count);
        });
    }

    private static (int Reads, int Writes) Accesses(MBlock block)
    {
        int reads = 0, writes = 0;
        foreach (MInstr i in block.Instrs)
            for (int k = 0; k < i.Operands.Count; k++)
            {
                if (i.Operands[k] is not MMem { IsSpill: true }) continue;
                (bool read, bool write) = SpillCounter.Access(Access(i, k));
                if (read) reads++;
                if (write) writes++;
            }
        return (reads, writes);
    }

    private static void Add(List<MInstr> into, string low, string high, int n)
    {
        if (n == 0) return;
        into.Add(new MInstr(MOp.Add, MMem.Rip(low, 0), new MImm(n)) { Width = 4 });
        into.Add(new MInstr(MOp.Adc, MMem.Rip(high, 0), new MImm(0)) { Width = 4 });
    }

    /// <summary>
    /// What an instruction does to its operand `k`, a spill slot: what it
    /// does to a register there (Roles.Of), but for xadd's and cmpxchg's
    /// destination, which their memory form reads and writes.
    /// </summary>
    private static Role Access(MInstr i, int k) => i.Op switch
    {
        MOp.Xadd or MOp.Cmpxchg when k == 0 => Role.UseDef,
        _ => (Role)(byte)Roles.Of(i, k),
    };
}
