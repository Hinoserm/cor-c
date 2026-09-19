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
