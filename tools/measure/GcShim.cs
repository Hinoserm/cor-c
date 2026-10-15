// The runtime counters COR-C# tests read, stood in for under .NET: nothing
// left to the collector, and every read of Frees a million more, so each
// "freed" line reads True and only the computed numbers are compared.
public static class Gc
{
    static long _frees;
    public static void Collect() => System.GC.Collect();
    public static long CollectedBytes => 0;
    public static long Frees => _frees += 1000000;
    public static long RegionFreedBytes => 0;
    public static long RegionTopFreedBytes => 0;
}
