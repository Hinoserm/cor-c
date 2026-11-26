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

    // AND THOSE A PROJECT'S ENTRY UNIT DECIDES APART FROM THE REST (--lib
    // --no-shared-init are its others'): kept with the unit as well, rather
    // than safe only while entry units compile alone first.

    /// Lowering.SharedObject: this unit makes a shared object's start.
    public bool SharedObject;

    /// Lowering.Dynamic: a program over shared libraries, not a library.
    public bool Dynamic;

    /// Lowering.ClosedWorld: the whole program, no other image beside.
    public bool ClosedWorld;

    /// Lowering.SharedLiterals: literals one at the link.
    public bool SharedLiterals;

    // ITS STATISTICS, its own: counted by its thread and its helpers at once
    // (Interlocked), printed with the unit.

    /// Monomorphiser's: specialisations made, their members, deferred ones,
    /// those with bodies, members made later, imported deferrals and their
    /// members made later.
    public long Specialisations, SpecialisedMembers, DeferredSpecialisations, DeferredWithBodies, MembersMadeLater, DeferredImports, ImportedMembersMadeLater;

    /// TypeSymbol's: deferred types declared after all, their members, and
    /// lookups answered from a deferred type's names.
    public long DeferredDeclared, DeferredMembersDeclared, ProbesAnswered;

    /// Escape's last run (--stats).
    public (int Promoted, int Owned, int OwnedReturns, int Fresh, int Fields, int Variables) EscapeRun;

    /// The pipeline's phase reporter (--report-phases), this unit's clock.
    public System.Action<string>? Stage;

    /// What the unit's helpers allocated (Run), added to its own thread's.
    public long HelpersAllocated;

    /// Where its own thread's allocation stood as it began (Begin).
    public long Begun;
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
        unit.Begun = System.GC.GetAllocatedBytesForCurrentThread();
        return unit.Begun;
    }

    /// What this thread's unit has allocated so far, its helpers' too; the
    /// process's whole outside one -- what a compile's statistics print.
    public static long AllocatedSoFar()
    {
        UnitState? unit = _current;
        return unit is null ? System.GC.GetTotalAllocatedBytes()
            : System.GC.GetAllocatedBytesForCurrentThread() - unit.Begun + Interlocked.Read(ref unit.HelpersAllocated);
    }

    /// The unit's end on this thread: what it allocated, its helpers' too.
    public static long End(UnitState unit, long begun)
    {
        _current = null;
        return System.GC.GetAllocatedBytesForCurrentThread() - begun + Interlocked.Read(ref unit.HelpersAllocated);
    }

    /// The unit left without a count (a failed unit's way out).
    public static void Leave() => _current = null;

    /// A thread of its own (not yet started) for work done for the unit of
    /// the thread that makes it, as Run's task: where the work may not be a
    /// pool's (it would wait on itself) or needs a stack of its own size.
    public static Thread OnThread(System.Action work, int stack)
    {
        UnitState? unit = _current;
        if (unit is null) return new Thread(() => work(), stack);
        return new Thread(() =>
        {
            _current = unit;
            long begun = System.GC.GetAllocatedBytesForCurrentThread();
            try
            {
                work();
            }
            finally
            {
                Interlocked.Add(ref unit.HelpersAllocated, System.GC.GetAllocatedBytesForCurrentThread() - begun);
                _current = null;
            }
        }, stack);
    }

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
