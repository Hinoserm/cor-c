#nullable enable
namespace Corsac;

/// <summary>
/// The COR-C# toolchain driver. See Driver.cs for the commands.
/// </summary>
public static partial class Program
{
#if COR_SELFHOST_BENCHMARK
    // Phase/caller boundaries only: counters are sampled before formatting so
    // their values do not include allocations made by this diagnostic line.
    public static void BenchmarkStage(string stage)
    {
        long ticks = Stopwatch.GetTimestamp();
        long collections = Gc.Collections, marked = Gc.MarkCalls, walked = Gc.WalkSteps;
        long liveBytes = Gc.LiveBytes, liveBlocks = Gc.LiveBlocks;
        long heapBytes = HeapChunks.HeapBytes, metadataBytes = HeapChunks.BlockAnchorBytes;
        Console.Error.WriteLine("selfhost-stage " + stage + " ticks=" + ticks
            + " collections=" + collections + " mark-words=" + marked
            + " interior-steps=" + walked + " last-gc-live-bytes=" + liveBytes
            + " last-gc-live-blocks=" + liveBlocks + " mapped-heap-bytes=" + heapBytes
            + " anchor-metadata-bytes=" + metadataBytes);
    }
#endif
    public static int Main(string[] args)
    {
#if COR_SELFHOST_BENCHMARK
        long started = Stopwatch.GetTimestamp();
        long collections = Gc.Collections, marked = Gc.MarkCalls, walked = Gc.WalkSteps;
        try { return Driver.Run(args); }
        finally
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            long collected = Gc.Collections - collections;
            long markWork = Gc.MarkCalls - marked, walkWork = Gc.WalkSteps - walked;
            long liveBytes = Gc.LiveBytes, liveBlocks = Gc.LiveBlocks;
            Console.Error.WriteLine("selfhost-metrics ticks=" + elapsed + " frequency=" + Stopwatch.Frequency
                + " collections=" + collected + " mark-words=" + markWork + " interior-steps=" + walkWork
                + " last-gc-live-bytes=" + liveBytes + " last-gc-live-blocks=" + liveBlocks);
        }
#else
        return Driver.Run(args);
#endif
    }
}
