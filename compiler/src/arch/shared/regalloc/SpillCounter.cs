#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.RegAlloc;

/// <summary>
/// --count-spills: THE SPILL SLOTS' TRAFFIC AS THE PROGRAM RUNS, on any
/// machine. Each block that reads or writes a spill slot adds, as it is
/// entered, how many of each it makes to the runtime's SpillCounts, which
/// the program says at its end: Braun and Hack's measure of a spiller (CC
/// 2009), executed spills and reloads (REGALLOC-RESEARCH). The target counts
/// a block's accesses and makes the code that adds them, flags kept around
/// it; this numbers the blocks, lists them (--count-spills-map) and hands the
/// runtime the table of their runs. The adds are not locked: the counts are
/// one thread's, as a benchmark's are. A block left early -- a call that
/// throws -- counts all of its own.
/// </summary>
public sealed class SpillCounter
{
    /// <summary>The runtime's counters (runtime.cor's SpillCounts), by their static symbols: each total's low half and high half.</summary>
    public const string ReadsLow = "s_SpillCounts_ReadsLow", ReadsHigh = "s_SpillCounts_ReadsHigh",
        WritesLow = "s_SpillCounts_WritesLow", WritesHigh = "s_SpillCounts_WritesHigh";

    /// <summary>The table of each counted block's runs, eight bytes a block (--count-spills-map).</summary>
    public const string BlocksSymbol = "spill$blocks";

    /// <summary>The runtime's own saying of them (SpillCounts' methods) is not counted: its snapshot of the totals and the blocks is then of one moment.</summary>
    private const string CountersPrefix = "m_SpillCounts_";

    private readonly string? _mapPath;
    private readonly List<(string Function, int Block, int Reads, int Writes)> _blocks = new();

    /// <param name="mapPath">--count-spills-map's file, or null for the totals alone.</param>
    public SpillCounter(string? mapPath) => _mapPath = mapPath;

    /// <summary>
    /// One function's blocks counted, in the order the functions are laid
    /// down (never on a compile's workers: the numbers are the same run to
    /// run): every block with spill-slot accesses (`accesses`) handed to
    /// `insert` with its reads, its writes and, when the blocks are listed,
    /// its counter's offset in the table, else -1.
    /// </summary>
    public void Count(string function, int blocks, Func<int, (int Reads, int Writes)> accesses, Action<int, int, int, int> insert)
    {
        if (function.StartsWith(CountersPrefix, StringComparison.Ordinal)) return;
        for (int b = 0; b < blocks; b++)
        {
            (int reads, int writes) = accesses(b);
            if (reads == 0 && writes == 0) continue;
            int at = -1;
            if (_mapPath is not null)
            {
                at = _blocks.Count * 8;
                _blocks.Add((function, b, reads, writes));
            }
            insert(b, reads, writes, at);
        }
    }

    /// <summary>What a spill slot operand's role (what the instruction does to a register there) means for the slot: read, written.</summary>
    public static (bool Read, bool Write) Access(Role role) => ((role & Role.Use) != 0, (role & Role.Def) != 0);

    /// <summary>
    /// --count-spills-map: the counted blocks' table, zero-filled, and the
    /// runtime told where it is and how long (SpillCounts.Blocks, an address
    /// `pointerBytes` wide, and BlockCount: volatile statics filled in place
    /// of their zeros, every other attribute kept), so its exit says each
    /// block's runs; the blocks listed in the map file, one a line -- number,
    /// function, block, spill-slot reads, writes.
    /// </summary>
    public void Table(Module module, int pointerBytes, List<string> errors)
    {
        if (_mapPath is null) return;
        int n = _blocks.Count;
        int at = module.Data.FindIndex(d => d.Name == "s_SpillCounts_Blocks");
        int count = module.Data.FindIndex(d => d.Name == "s_SpillCounts_BlockCount");
        if (at < 0 || count < 0)
        {
            errors.Add("--count-spills-map: the runtime's SpillCounts.Blocks and BlockCount are not in this compile (a program compiled whole is)");
            return;
        }
        module.Data.Add(new DataItem(BlocksSymbol, new byte[Math.Max(n, 1) * 8]) { Zero = true, Align = 8, Exported = false });
        DataItem address = Filled(module.Data[at], new byte[pointerBytes], noReferences: false);
        address.Relocs.Add(new DataReloc(0, BlocksSymbol, 0));
        module.Data[at] = address;
        module.Data[count] = Filled(module.Data[count], BitConverter.GetBytes((uint)n), noReferences: true);
        try
        {
            File.WriteAllLines(_mapPath, _blocks.Select((b, k) => $"{k}\t{b.Function}\t{b.Block}\t{b.Reads}\t{b.Writes}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"--count-spills-map: {_mapPath} cannot be written: {e.Message}");
        }
    }

    /// <summary>A zero static made to hold `bytes`, everything else about it as it was.</summary>
    private static DataItem Filled(DataItem d, byte[] bytes, bool noReferences) => new(d.Name, bytes)
    {
        Align = d.Align, ReadOnly = d.ReadOnly, Exported = d.Exported, Coalescible = d.Coalescible, NoReferences = noReferences || d.NoReferences,
        Volatile = true, FromLibrary = d.FromLibrary, SystemCode = d.SystemCode, SourcePath = d.SourcePath, SourceFile = d.SourceFile,
        Line = d.Line, Display = d.Display, SharedCopies = d.SharedCopies,
    };
}
