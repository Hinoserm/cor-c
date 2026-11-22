#nullable enable
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Corsac;

/// <summary>
/// WHAT ONE UNIT OF A PROJECT ALLOCATES, its own: the thread compiling it and
/// every helper it starts (the binder's, the inliner's, the optimiser's and
/// the backend's workers), never another unit's. The process's total
/// (GC.GetTotalAllocatedBytes) counted every unit running beside it, so a
/// unit begun with seven others said four or five times what it made.
/// </summary>
public static class AllocationMeter
{
    /// The unit this thread works for, or null outside a project's unit.
    [ThreadStatic] private static StrongBox<long>? _unit;

    /// Begins counting for a unit on this thread; what helpers add goes to `unit`.
    public static long Begin(StrongBox<long> unit)
    {
        _unit = unit;
        return System.GC.GetAllocatedBytesForCurrentThread();
    }

    /// Ends it: this thread's share since Begin and every helper's.
    public static long End(StrongBox<long> unit, long begun)
    {
        _unit = null;
        return System.GC.GetAllocatedBytesForCurrentThread() - begun + Interlocked.Read(ref unit.Value);
    }

    /// Task.Run, the work counted to the unit of the thread that starts it.
    public static Task Run(System.Action work)
    {
        StrongBox<long>? unit = _unit;
        if (unit is null) return Task.Run(work);
        return Task.Run(() =>
        {
            StrongBox<long>? was = _unit;
            _unit = unit;
            long begun = System.GC.GetAllocatedBytesForCurrentThread();
            try
            {
                work();
            }
            finally
            {
                Interlocked.Add(ref unit.Value, System.GC.GetAllocatedBytesForCurrentThread() - begun);
                _unit = was;
            }
        });
    }
}
