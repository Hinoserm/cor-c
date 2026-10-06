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
    /// What the MACHINE has available (Linux MemAvailable), whatever this
    /// process's own heap may hold -- but no more than the process's control
    /// group has left (GroupAvailable); 0 when neither is known. What decides
    /// how many processes a build can spread over, each with its own address
    /// space: every one of them is in the same group, so on a 48 GB server
    /// inside a 256 MB group, MemAvailable alone started a child per two
    /// gigabytes of the server and the group killed them.
    /// </summary>
    public static long MachineAvailable()
    {
        long machine = 0;
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                machine = parts.Length >= 2 && long.TryParse(parts[1], out long kib) ? kib * 1024 : 0;
                break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        long group = GroupAvailable();
        if (group < 0) return machine;
        return machine > 0 ? Math.Min(machine, group) : group;
    }

    /// <summary>
    /// What the process's control group has left before its memory limit:
    /// the least, along the group's path to the root, of a limit less what
    /// that group uses -- cgroup v2's memory.max and memory.current, a parent
    /// binding its children as much as the group itself; or, when v2 sets no
    /// limit, v1's memory.limit_in_bytes and memory.usage_in_bytes. -1 when no
    /// group limits the process (the runtime reads the same files for its
    /// heap, Platform.MemoryLimit; this is the build's own reading of them,
    /// which also holds under dotnet).
    /// </summary>
    public static long GroupAvailable()
    {
        string[] lines;
        try { lines = File.ReadAllLines("/proc/self/cgroup"); }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
        long best = -1;
        foreach (string line in lines)
        {
            if (!line.StartsWith("0::", StringComparison.Ordinal)) continue;
            string group = line[3..].TrimEnd('/');
            while (true)
            {
                long left = GroupLeft("/sys/fs/cgroup" + group, "memory.max", "memory.current");
                if (left >= 0 && (best < 0 || left < best)) best = left;
                if (group.Length == 0) break;
                int slash = group.LastIndexOf('/');
                group = slash <= 0 ? "" : group[..slash];
            }
        }
        if (best >= 0) return best;
        foreach (string line in lines)
        {
            int at = line.IndexOf(":memory:", StringComparison.Ordinal);
            if (at < 0) continue;
            string group = line[(at + 8)..].TrimEnd('/');
            return GroupLeft("/sys/fs/cgroup/memory" + group, "memory.limit_in_bytes", "memory.usage_in_bytes");
        }
        return -1;
    }

    /// <summary>
    /// One group's limit less its use, or -1 when it sets no limit ("max", a
    /// v1 limit near 2^63, or no file). A use that cannot be read counts as
    /// none.
    /// </summary>
    private static long GroupLeft(string directory, string limitFile, string usageFile)
    {
        long limit = ReadNumber(directory + "/" + limitFile);
        if (limit <= 0 || limit >= long.MaxValue / 2) return -1;
        long used = ReadNumber(directory + "/" + usageFile);
        return Math.Max(0, limit - Math.Max(0, used));
    }

    /// <summary>The number a cgroup file holds, or -1.</summary>
    private static long ReadNumber(string path)
    {
        try
        {
            return long.TryParse(File.ReadAllText(path).Trim(), out long value) ? value : -1;
        }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }

    /// <summary>
    /// Bytes the process can still take: the smaller of what the machine (or
    /// its control group) has available and what the runtime's own heap limit
    /// leaves, if either is known.
    /// </summary>
    public static long Available()
    {
        long best = long.MaxValue;
        long machine = MachineAvailable();
        if (machine > 0) best = machine;
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes > 0)
            best = Math.Min(best, Math.Max(0, info.TotalAvailableMemoryBytes - GC.GetTotalMemory(false)));
        return best == long.MaxValue ? 256L * 1024 * 1024 : best;
    }

    /// <summary>
    /// A working budget for one batch of work: a quarter of what is available,
    /// between `floor` and `ceiling`. --work-budget BYTES overrides it,
    /// so a large machine can be made to work as a small one does -- and must
    /// then produce the same bytes.
    /// </summary>
    public static long WorkBudget(long floor, long ceiling)
    {
        if (Switches.WorkBudget > 0) return Switches.WorkBudget;
        return Math.Clamp(Available() / 4, floor, ceiling);
    }
}
