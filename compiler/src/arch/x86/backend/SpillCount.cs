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

    public static void Run(MFunction m)
    {
        foreach (MBlock block in m.Blocks)
        {
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
    /// What an instruction does to its operand `k`, a spill slot. Written
    /// alone: a move's, a pop's, a setcc's or a store's destination. Read and
    /// written: the destination of the operations that combine into it (the
    /// two-operand arithmetic, the shifts, neg and not, and xchg's either
    /// side). Everything else only reads: any source, a push's or a
    /// compare's operand, and one-operand mul and div (Foldable), whose
    /// results are EDX:EAX.
    /// </summary>
    private static (bool Read, bool Write) Access(MInstr i, int k)
    {
        if (i.Op == MOp.Xchg) return (true, true);
        if (k > 0) return (true, false);
        return i.Op switch
        {
            MOp.Mov or MOp.Movsx or MOp.Movzx or MOp.Setcc or MOp.Pop or MOp.Fstp or MOp.Fistp or MOp.Fnstcw => (false, true),
            MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Neg or MOp.Not
                or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Ror or MOp.Shld or MOp.Shrd or MOp.Xadd or MOp.Cmpxchg => (true, true),
            _ => (true, false),
        };
    }
}
