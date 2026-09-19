#nullable enable
namespace Corsac;

/// <summary>
/// A worker budget shared by every process in one build.
///
/// The build runs several projects at once and each of them compiles several
/// sources at once, so neither the build tool nor any one compiler knows how
/// many workers it should use: that depends on what the others are doing
/// right now. A fixed share decided when a project starts is wrong within
/// seconds -- the eight one-file utilities finish and the kernel, which took
/// one worker because the budget was full at the time, keeps plodding along
/// with one while fifteen sit idle.
///
/// So the budget is a directory of lock files, one per worker, and a worker
/// is held only while a source is actually being compiled. A compiler that
/// wants another source takes a lock if one is free and does not if it is
/// not, which means the kernel picks up the utilities' workers as they finish
/// without anybody being told to.
///
/// A lock file is exclusive to the process holding it, so a build that is
/// killed leaves no permits behind: the operating system drops the locks.
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
