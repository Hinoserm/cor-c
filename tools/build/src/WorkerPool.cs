#nullable enable
namespace Corsac.Build;

/// <summary>
/// A worker budget shared by every process in one build.
///
/// The build runs several projects at once and each of them compiles several
/// sources at once, so neither the build tool nor any one compiler knows how
/// many workers it should use: that depends on what the others are doing
/// right now. A fixed share decided when a project starts is wrong within
/// seconds -- the eight one-file utilities finish and the kernel, which took
/// one worker because the budget was full at the time, keeps plodding along
/// with one while fifteen sit idle. It is GNU make's jobserver, made of files.
///
/// THE PROTOCOL -- the one place it is written; ImageBuild (the kernel
/// repository's tools/image) keeps a copy of TryTake/Give and must follow it:
///
/// - THE BUDGET is a directory holding exactly N empty files named
///   `worker-0` .. `worker-(N-1)`, N the build's --jobs. `corc build` makes one
///   for every build (Create) and names it to manifests as $(WorkerPool); a
///   process given one is told its path (`--worker-pool DIR`).
/// - A TOKEN is one of those files opened read-write with no sharing
///   (FileShare.None: an exclusive lock), and is held for as long as the
///   handle is open. Closing the handle gives it back; so does the process
///   ending, however it ends, so a killed build leaves no token behind.
/// - HOLD ONE ONLY WHILE COMPUTING: one compile of one unit, one link, one
///   tool's run. Never while waiting for another process, for input, or for
///   a further token -- a holder that waits for a second token can deadlock
///   with another doing the same.
/// - A TAKER THAT FINDS NONE FREE WAITS WITHOUT SPINNING (Take): it sleeps,
///   1 ms the first time, doubling to 32 ms, and tries again; a starved
///   coordinator costs a few wakes a second and no core.
/// - Tokens are not counted anywhere else: the files are the count. Nothing
///   writes into them.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    private readonly string directory;
    private readonly int size;
    private readonly List<FileStream> held = new();
    private readonly object gate = new();

    private WorkerPool(string directory, int size)
    {
        this.directory = directory;
        this.size = size;
    }

    /// <summary>Opens the budget a build tool laid out, or nothing when there is none.</summary>
    public static WorkerPool? Open(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return null;
        int size = Directory.GetFiles(directory, "worker-*").Length;
        return size == 0 ? null : new WorkerPool(directory, size);
    }

    /// <summary>Lays out a budget of <paramref name="size"/> workers.</summary>
    public static string Create(string directory, int size)
    {
        Directory.CreateDirectory(directory);
        foreach (string stale in Directory.GetFiles(directory, "worker-*")) File.Delete(stale);
        for (int i = 0; i < size; i++) File.WriteAllBytes(Path.Combine(directory, "worker-" + i), Array.Empty<byte>());
        return directory;
    }

    /// <summary>The most workers this budget could ever offer.</summary>
    public int Size => size;

    /// <summary>
    /// Takes a worker if one is going spare. Never waits: a caller that
    /// cannot have another worker gets on with the one it already has.
    /// </summary>
    public bool TryTake()
    {
        for (int i = 0; i < size; i++)
        {
            try
            {
                FileStream stream = new(Path.Combine(directory, "worker-" + i), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None);
                lock (gate) held.Add(stream);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    /// <summary>
    /// Takes a worker, waiting for one: sleeps 1 ms, doubling to 32 ms,
    /// between tries -- never spins. Holds none while it waits.
    /// </summary>
    public void Take(CancellationToken cancel = default)
    {
        int pause = 1;
        while (!TryTake())
        {
            cancel.ThrowIfCancellationRequested();
            Thread.Sleep(pause);
            if (pause < 32) pause *= 2;
        }
    }

    /// <summary>Take, for an async caller: the same backoff, no thread blocked.</summary>
    public async Task TakeAsync(CancellationToken cancel = default)
    {
        int pause = 1;
        while (!TryTake())
        {
            await Task.Delay(pause, cancel);
            if (pause < 32) pause *= 2;
        }
    }

    /// <summary>Gives a worker back for somebody else to take.</summary>
    public void Give()
    {
        FileStream? stream = null;
        lock (gate)
        {
            if (held.Count == 0) return;
            stream = held[^1];
            held.RemoveAt(held.Count - 1);
        }
        stream.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (FileStream stream in held) stream.Dispose();
            held.Clear();
        }
    }
}
