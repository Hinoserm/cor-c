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
        // THE WHOLE RUN, NOT A PHASE OF IT. This machine is too busy to time a
        // compiler, but what it allocates does not depend on what else is
        // running. Reported at exit so it covers code generation and emission
        // too: an earlier mid-way figure made a whole-compile total look like
        // a regression against a frontend-only one.
        if (Environment.GetEnvironmentVariable("CORC_REPORT_ALLOC") is null) return Driver.Run(args);
        try { return Driver.Run(args); }
        finally { Console.Error.WriteLine("run-allocated=" + GC.GetTotalAllocatedBytes()); }
#endif
    }
}
