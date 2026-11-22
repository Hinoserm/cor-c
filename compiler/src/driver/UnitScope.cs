#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Corsac;

/// <summary>
/// One unit of a project's compile, as the threads compiling it see it: the
/// compile settings that differ from unit to unit, and what the unit
/// allocated. A project's units compile side by side in one process
/// (ProjectCompile), so what one unit's compile decides from its own files
/// cannot sit in a static every unit shares: `guimodule.cor` set the library
/// sources it owns -- none -- while `TrueType.cor` beside it was still to ask
/// whether it owned its own, and it found it owned nothing and refused the
/// font engine's code (the K8 image, 11-22). How many units ran at once
/// went by the memory there was, so a heap limit had kept them apart.
/// </summary>
public sealed class UnitState
{
    /// The class library's sources this unit's compile owns (Lowering.OwnLibrarySources).
    public IReadOnlySet<string> OwnLibrarySources = new HashSet<string>(System.StringComparer.Ordinal);

    /// Lowering.SplitLibrary: a shared object of library sources of its own.
    public bool SplitLibrary;

    /// Lowering.LibraryProvided: images under it provide the class library.
    public bool LibraryProvided;

    /// What the unit's helpers allocated (Run), added to its own thread's.
    public long HelpersAllocated;
}

/// <summary>
/// The unit each thread works for: the thread compiling it, and every helper
/// that thread starts (the binder's, the inliner's, the optimiser's, the
/// backends' workers: Run). Outside a project's unit, the process's one.
/// </summary>
public static class UnitScope
{
    [System.ThreadStatic] private static UnitState? _current;
    private static readonly UnitState Process = new();

    /// The unit this thread works for, or the process's own outside one.
    public static UnitState Current => _current ?? Process;

    /// This thread now works for `unit`; answers where its allocation stood.
    public static long Begin(UnitState unit)
    {
        _current = unit;
        return System.GC.GetAllocatedBytesForCurrentThread();
    }

    /// The unit's end on this thread: what it allocated, its helpers' too.
    public static long End(UnitState unit, long begun)
    {
        _current = null;
        return System.GC.GetAllocatedBytesForCurrentThread() - begun + Interlocked.Read(ref unit.HelpersAllocated);
    }

    /// The unit left without a count (a failed unit's way out).
    public static void Leave() => _current = null;

    /// Task.Run, the work done for the unit of the thread that starts it:
    /// its settings, and its allocation counted to it.
    public static Task Run(System.Action work)
    {
        UnitState? unit = _current;
        if (unit is null) return Task.Run(work);
        return Task.Run(() =>
        {
            UnitState? was = _current;
            _current = unit;
            long begun = System.GC.GetAllocatedBytesForCurrentThread();
            try
            {
                work();
            }
            finally
            {
                Interlocked.Add(ref unit.HelpersAllocated, System.GC.GetAllocatedBytesForCurrentThread() - begun);
                _current = was;
            }
        });
    }
}
