namespace Corsac.Lang.Lto;

/// <summary>
/// HOW MUCH MEMORY THE TOOLCHAIN MAY USE, asked of the machine as it runs.
/// The toolchain must build the system on a machine of 128 MB as well as on
/// one of many gigabytes, and build the same system on both: memory decides
/// how work is batched and how much runs at once, never what is decided.
/// Every limit that shapes an answer is a fixed constant somewhere else;
/// what is read here only sizes batches and admits work.
/// </summary>
public static class MachineMemory
{
    /// <summary>
    /// Bytes the process can still take: the smaller of what the machine has
    /// available (Linux MemAvailable) and what the runtime's own heap limit
    /// leaves, if either is known.
    /// </summary>
    public static long Available()
    {
        long best = long.MaxValue;
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out long kib)) best = Math.Min(best, kib * 1024);
                break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes > 0)
            best = Math.Min(best, Math.Max(0, info.TotalAvailableMemoryBytes - GC.GetTotalMemory(false)));
        return best == long.MaxValue ? 256L * 1024 * 1024 : best;
    }

    /// <summary>
    /// A working budget for one batch of work: a quarter of what is available,
    /// between `floor` and `ceiling`. `CORC_WORK_BUDGET` (bytes) overrides it,
    /// so a large machine can be made to work as a small one does -- and must
    /// then produce the same bytes.
    /// </summary>
    public static long WorkBudget(long floor, long ceiling)
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("CORC_WORK_BUDGET"), out long forced) && forced > 0) return forced;
        return Math.Clamp(Available() / 4, floor, ceiling);
    }
}
