using System.Diagnostics;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHERE A LINK SPENDS ITS TIME AND ITS MEMORY (corc link --timings): a line
/// at the end of each phase with the phase's wall time, the process's
/// resident set then and its high-water mark so far (Linux VmRSS, VmHWM),
/// what of it is swapped out (VmSwap), and the collector's live heap, found
/// by a full collection whose own time is left out of every phase's. Off,
/// every call is a test of one flag. What the link decides never depends
/// on it.
/// </summary>
public static class LinkTimings
{
    public static bool Enabled { get; set; }
    private static readonly Stopwatch Clock = new();
    private static long last;

    /// <summary>The link starts here; phases are timed from it.</summary>
    public static void Start()
    {
        if (!Enabled) return;
        Clock.Restart();
        last = 0;
    }

    /// <summary>The phase just ended, with what it took.</summary>
    public static void Phase(string name)
    {
        if (!Enabled) return;
        long now = Clock.ElapsedMilliseconds;
        (long rss, long peak, long swap) = Resident();
        long live = GC.GetTotalMemory(true);
        Console.Error.WriteLine("link phase: " + name + " " + (now - last) + "ms (at "
            + now + "ms) rss=" + rss / (1024 * 1024) + "M peak=" + peak / (1024 * 1024)
            + "M swap=" + swap / (1024 * 1024) + "M live=" + live / (1024 * 1024) + "M");
        last = Clock.ElapsedMilliseconds;
    }

    private static (long Rss, long Peak, long Swap) Resident()
    {
        long rss = 0, peak = 0, swap = 0;
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kib(line);
                else if (line.StartsWith("VmHWM:", StringComparison.Ordinal)) peak = Kib(line);
                else if (line.StartsWith("VmSwap:", StringComparison.Ordinal)) swap = Kib(line);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (rss, peak, swap);
    }

    private static long Kib(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long kib) ? kib * 1024 : 0;
    }
}
