namespace Corsac.Lang.Lto;

/// <summary>
/// WORK THAT IS EACH ITEM'S ALONE, on as many threads as there are
/// processors: every thread takes the next item not yet taken, so one large
/// item holds up only its own thread, and each answer goes to its item's
/// place. What the caller reads afterwards is in the items' order, and a
/// failure is thrown as a loop over them in order would have thrown it --
/// the first item's, as it was thrown. The link's answers never depend on
/// how many threads there were.
/// </summary>
public static class Lanes
{
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
        Task[] others = new Task[Math.Max(0, Math.Min(count, Environment.ProcessorCount) - 1)];
        for (int t = 0; t < others.Length; t++) others[t] = Task.Run(Work);
        Work();
        Task.WaitAll(others);
        for (int k = 0; k < count; k++)
            if (failed[k] is Exception error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return made;
    }
}
