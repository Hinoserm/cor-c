#nullable enable
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// --count-spills, in both modes of the family (Corsac.Lang.RegAlloc.
/// SpillCounter): each counted block entered by a push of the flags, a
/// 32-bit add and an adc to each 64-bit total's two halves and to its own
/// run count, then their pop -- nothing it inserts touches a register or
/// the flags, so the allocation and the code around it are as they are
/// without it. The counters by MMem.Data: absolute addresses on i386 (code
/// that is not position-independent only, refused elsewhere: Driver), from
/// the instruction in long mode. Run after every pass that moves or removes
/// a spill access (Peephole, FrameCompact), so what is counted is what the
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
                count.Add(new MInstr(MOp.Add, MMem.Data(SpillCounter.BlocksSymbol, at), new MImm(1)) { Width = 4 });
                count.Add(new MInstr(MOp.Adc, MMem.Data(SpillCounter.BlocksSymbol, at + 4), new MImm(0)) { Width = 4 });
            }
            count.Add(new MInstr(MOp.Popf));
            // After the prologue: a function starts with it, where a fault
            // reads its frame's shape (Runtime.FrameAt).
            List<MInstr> instrs = m.Blocks[b].Instrs;
            instrs.InsertRange(instrs.Count > 0 && instrs[0].Op == MOp.Prologue ? 1 : 0, count);
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
        into.Add(new MInstr(MOp.Add, MMem.Data(low, 0), new MImm(n)) { Width = 4 });
        into.Add(new MInstr(MOp.Adc, MMem.Data(high, 0), new MImm(0)) { Width = 4 });
    }

    /// <summary>
    /// What an instruction does to its operand `k`, a spill slot: what it
    /// does to a register there (Roles.Of), but for the operands whose
    /// memory form writes where the register form only reads -- xadd's and
    /// cmpxchg's destination, read and written, and the x87 stores.
    /// </summary>
    private static Role Access(MInstr i, int k) => i.Op switch
    {
        MOp.Xadd or MOp.Cmpxchg when k == 0 => Role.UseDef,
        MOp.Fstp or MOp.Fistp or MOp.Fnstcw => Role.Def,
        _ => (Role)(byte)Roles.Of(i.Op, k),
    };
}
