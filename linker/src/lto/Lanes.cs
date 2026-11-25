namespace Corsac.Lang.Lto;

/// <summary>
/// WORK THAT IS EACH ITEM'S ALONE, on as many threads as there are
/// processors: every thread takes the next item not yet taken, so one large
/// item holds up only its own thread, and each answer goes to its item's
/// place. What the caller reads afterwards is in the items' order, and a
/// failure is thrown as a loop over them in order would have thrown it --
/// the first item's, as it was thrown. The link's answers never depend on
/// how many threads there were.
///
/// ONE AT A TIME IN A 32-BIT PROCESS. What the lanes make is read from
/// notes: many small objects each, and much let go on the way. Sixteen
/// threads making them at once left the native compiler's own link, whose
/// heap moves nothing, half a gigabyte larger with no more live in it
/// (3090 MB resident where the regions begin, 2594 MB with one lane, 1750
/// MB live in both) -- free memory in the wrong size classes, which the
/// region solve's tables could not use, and the link ran out of its four
/// gigabytes. The lanes saved it some twenty-five seconds of twenty
/// minutes. Where the address space is the limit, it is spent on the link.
/// </summary>
public static class Lanes
{
    /// <summary>How many threads work at once: the processors', and one in a 32-bit process.</summary>
    public static int Width => Environment.Is64BitProcess ? Environment.ProcessorCount : 1;

    /// <summary>`make(k)` for every k below `count`, each answer in its place.</summary>
    public static T[] Each<T>(int count, Func<int, T> make)
    {
        T[] made = new T[count];
        if (count == 0) return made;
        Exception?[] failed = new Exception?[count];
        int next = -1;
        void Work()
        {
            for (int k; (k = Interlocked.Increment(ref next)) < count;)
            {
                try { made[k] = make(k); }
                catch (Exception error) { failed[k] = error; }
            }
        }
        Task[] others = new Task[Math.Max(0, Math.Min(count, Width) - 1)];
        for (int t = 0; t < others.Length; t++) others[t] = Task.Run(Work);
        Work();
        Task.WaitAll(others);
        for (int k = 0; k < count; k++)
            if (failed[k] is Exception error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return made;
    }
}
