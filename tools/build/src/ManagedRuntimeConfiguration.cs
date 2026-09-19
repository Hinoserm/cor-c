namespace Corsac.Build;

/// <summary>Standard SDK properties translated to CoreCLR runtime switches.</summary>
public static class ManagedRuntimeConfiguration
{
    public static Dictionary<string, object> Create(IReadOnlyDictionary<string, string> properties)
    {
        bool Read(string name, bool fallback)
        {
            if (!properties.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value)) return fallback;
            if (!bool.TryParse(value.Trim(), out bool result))
                throw new InvalidDataException(name + " must be true or false");
            return result;
        }
        Dictionary<string, object> result = new()
        {
            ["System.Globalization.Invariant"] = Read("InvariantGlobalization", false),
            ["System.Runtime.TieredCompilation"] = Read("TieredCompilation", true),
            ["System.Runtime.TieredPGO"] = Read("TieredPGO", true)
        };
        // HOW THIS PROGRAM COLLECTS ITS GARBAGE, when it says so. A compiler
        // worker is short-lived and allocates hard, and the right collector
        // for that is not the right one for every managed tool here, so
        // nothing is imposed: a project that sets these gets them, and a
        // project that does not is left alone. The numeric ones have no
        // standard SDK property, so they are spelled the way the runtime
        // switch is. See corc.csproj, which explains the values it picks.
        foreach (var gc in new[] {
            ("ServerGarbageCollection", "System.GC.Server"),
            ("ConcurrentGarbageCollection", "System.GC.Concurrent") })
            if (properties.TryGetValue(gc.Item1, out string? set) && !string.IsNullOrWhiteSpace(set))
                result[gc.Item2] = Read(gc.Item1, false);
        foreach (var gc in new[] {
            ("GCHeapCount", "System.GC.HeapCount"),
            ("GCHeapHardLimit", "System.GC.HeapHardLimit"),
            ("GCgen0size", "System.GC.Gen0Size") })
            if (properties.TryGetValue(gc.Item1, out string? size) && !string.IsNullOrWhiteSpace(size))
            {
                if (!long.TryParse(size.Trim(), out long parsed) || parsed <= 0)
                    throw new InvalidDataException(gc.Item1 + " must be a positive number of bytes or heaps");
                result[gc.Item2] = parsed;
            }
        // Preserve explicit SDK settings without imposing extra startup or
        // server-GC policy on every short-lived parallel compiler worker.
        foreach (var option in new[] {
            ("TieredCompilationQuickJit", "System.Runtime.TieredCompilation.QuickJit"),
            ("TieredCompilationQuickJitForLoops", "System.Runtime.TieredCompilation.QuickJitForLoops") })
            if (properties.TryGetValue(option.Item1, out string? value) && !string.IsNullOrWhiteSpace(value))
                result[option.Item2] = Read(option.Item1, false);
        return result;
    }
}
