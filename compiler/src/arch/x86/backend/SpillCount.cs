#nullable enable

namespace Corsac.Lang.X86;

/// <summary>
/// --count-spills: THE SPILL SLOTS' TRAFFIC AS THE PROGRAM RUNS. Each block
/// that reads or writes a spill slot (MMem.IsSpill) adds, as it is entered,
/// how many reads and how many writes of them it makes to the runtime's
/// SpillCounts, which the program says at its end. After every pass that
/// moves or removes a spill access (Peephole, FrameCompact), so what is
/// counted is what the encoder writes; nothing it inserts touches a register
/// or the flags (pushfd, then an add and an adc to each 64-bit total's two
/// halves, then popfd), so the allocation and the code around it are as they are
/// without it. Absolute addresses: code that is not position-independent
/// only -- a program, not a shared object, refused elsewhere (Driver). The
/// adds are not locked: the counts are a single thread's, as a benchmark's
/// are. A block left early -- a call that throws -- counts all of its own.
/// Braun and Hack's measure of a
/// spiller (CC 2009): executed spills and reloads (REGALLOC-RESEARCH).
/// </summary>
internal static class SpillCount
{
    /// <summary>The runtime's counters (runtime.cor's SpillCounts), by their static symbols: each total's low half and high half.</summary>
    private const string ReadsLow = "s_SpillCounts_ReadsLow", ReadsHigh = "s_SpillCounts_ReadsHigh",
        WritesLow = "s_SpillCounts_WritesLow", WritesHigh = "s_SpillCounts_WritesHigh";

    /// <summary>
    /// --count-spills-map: every counted block numbered, in the order the
    /// functions are emitted, with what it reads and writes; its runs, a
    /// 64-bit count each in a table of the backend's (BlocksSymbol), are
    /// said at the end beside the totals, and the two together are where the
    /// traffic is, function by function.
    /// </summary>
    public sealed class Blocks
    {
        public readonly List<(string Function, int Block, int Reads, int Writes)> All = new();
    }

    /// <summary>The table of each counted block's runs (Blocks), eight bytes a block.</summary>
    public const string BlocksSymbol = "spill$blocks";

    /// <summary>The runtime's SpillCounts' methods, by their symbols' start.</summary>
    private const string CountersPrefix = "m_SpillCounts_";

    public static void Run(MFunction m, Blocks? numbered = null)
    {
        // The runtime's own saying of them (SpillCounts) is not counted: its
        // snapshot of the totals and the blocks is then of one moment.
        if (m.Source.Name.StartsWith(CountersPrefix, StringComparison.Ordinal)) return;
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            MBlock block = m.Blocks[b];
            int reads = 0, writes = 0;
            foreach (MInstr i in block.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not MMem { IsSpill: true }) continue;
                    (bool read, bool write) = Access(i, k);
                    if (read) reads++;
                    if (write) writes++;
                }
            if (reads == 0 && writes == 0) continue;
            List<MInstr> count = new() { new MInstr(MOp.Pushfd) };
            Add(count, ReadsLow, ReadsHigh, reads);
            Add(count, WritesLow, WritesHigh, writes);
            if (numbered is not null)
            {
                int at = numbered.All.Count * 8;
                numbered.All.Add((m.Source.Name, b, reads, writes));
                count.Add(new MInstr(MOp.Add, MMem.Abs(BlocksSymbol, at), new MImm(1)));
                count.Add(new MInstr(MOp.Adc, MMem.Abs(BlocksSymbol, at + 4), new MImm(0)));
            }
            count.Add(new MInstr(MOp.Popfd));
            block.Instrs.InsertRange(0, count);
        }
    }

    private static void Add(List<MInstr> into, string low, string high, int n)
    {
        if (n == 0) return;
        into.Add(new MInstr(MOp.Add, MMem.Abs(low, 0), new MImm(n)));
        into.Add(new MInstr(MOp.Adc, MMem.Abs(high, 0), new MImm(0)));
    }

    /// <summary>
    /// What an instruction does to its operand `k`, a spill slot: what it
    /// does to a register there (Roles.Of), but for the operands whose
    /// memory form writes where the register form only reads -- xadd's and
    /// cmpxchg's destination, read and written, and the x87 stores.
    /// </summary>
    private static (bool Read, bool Write) Access(MInstr i, int k)
    {
        Roles.Role role = i.Op switch
        {
            MOp.Xadd or MOp.Cmpxchg when k == 0 => Roles.Role.UseDef,
            MOp.Fstp or MOp.Fistp or MOp.Fnstcw => Roles.Role.Def,
            _ => Roles.Of(i.Op, k),
        };
        return ((role & Roles.Role.Use) != 0, (role & Roles.Role.Def) != 0);
    }
}
