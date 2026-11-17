namespace Corsac.Lang.Lto;

/// <summary>
/// --collector-report at the link: where the compiler's report (Escape.WriteReport)
/// is told of the archived bodies that still ask the allocator, as their units'
/// compiles left them. Set by the compiler when a report is wanted; the link
/// calls it with each such function's name.
/// </summary>
public static class CollectorReportHook
{
    public static Action<string>? Archived { get; set; }

    /// <summary>The runtime's allocators, by name: what a body that still allocates calls.</summary>
    public static bool IsAllocator(string callee)
        => callee is RuntimeAbi.Alloc or RuntimeAbi.AllocLeaf or RuntimeAbi.AllocObject;
}
