using System.Diagnostics;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>A single backend process reused across selected compilation units.</summary>
public sealed class ProcessUnitBackend : IUnitBackend, IDisposable
{
    private readonly Process process;
    private readonly BinaryWriter writer;
    private readonly BinaryReader reader;
    private readonly string work;
    private int sequence;
    // A REQUEST IS ENDED WHEN IT STOPS WORKING, not when the clock says so:
    // when the backend's processor time has not moved for StallMs, or has
    // passed MostProcessorTime in all. Timed by the wall, a link on a busy
    // machine -- thirty compiles at once -- ended a backend still compiling
    // a program's one large Main (1321: 68 s idle, 226 s at a load of forty).
    private const long StallMs = 5 * 60 * 1000;
    private static readonly TimeSpan MostProcessorTime = TimeSpan.FromHours(1);
    // Whether a request is in flight (1), when its processor time last moved,
    // that time then, and at the request's start; why the watchdog killed it.
    private long inFlight, movedAt;
    private TimeSpan lastProcessor, startProcessor;
    private string? killedFor;
    private bool timedOut, disposed;
    private readonly Thread watchdog;

    public ProcessUnitBackend(string? executable = null)
    {
        // THE RUNNING COMPILER ITSELF, unless it runs under dotnet: a native
        // compiler need not be called corc, and the one beside it may be
        // another build.
        string? running = Environment.ProcessPath;
        bool hosted = running is null
            || Path.GetFileNameWithoutExtension(running).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        string path = executable
            ?? (hosted ? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "corc.exe" : "corc") : running!);
        if (!File.Exists(path)) throw new FileNotFoundException("IR LTO needs the compiler backend; pass --lto-backend, or use --no-lto", path);
        ProcessStartInfo start = new(Path.GetFullPath(path)) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (string flag in Switches.ChildFlags) start.ArgumentList.Add(flag);
        start.ArgumentList.Add("backend");
        work = Directory.CreateTempSubdirectory("corc-lto-").FullName;
        try { process = Process.Start(start) ?? throw new IOException("Could not start compiler backend"); }
        catch { Directory.Delete(work); throw; }
        writer = new BinaryWriter(process.StandardInput.BaseStream, BackendProtocol.Utf8, leaveOpen: true);
        reader = new BinaryReader(process.StandardOutput.BaseStream, BackendProtocol.Utf8, leaveOpen: true);
        watchdog = new Thread(Watch, 256 * 1024) { IsBackground = true, Name = "lto-watchdog" };
        watchdog.Start();
    }

    /// Kills the process when a request stops working (StallMs) or works past
    /// MostProcessorTime: the read waiting on it then ends.
    private void Watch()
    {
        while (!Volatile.Read(ref disposed))
        {
            Thread.Sleep(1000);
            if (Interlocked.Read(ref inFlight) == 0) continue;
            TimeSpan used;
            try { process.Refresh(); used = process.TotalProcessorTime; }
            catch (InvalidOperationException) { return; }
            long now = Environment.TickCount64;
            lock (process)
            {
                if (Interlocked.Read(ref inFlight) == 0) continue;
                if (used > lastProcessor) { lastProcessor = used; movedAt = now; }
                if (now - movedAt > StallMs) killedFor = "used no processor time for five minutes";
                else if (used - startProcessor > MostProcessorTime) killedFor = "used an hour of processor time";
                else continue;
            }
            Volatile.Write(ref timedOut, true);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return;
        }
    }

    private void Begin()
    {
        TimeSpan now;
        try { process.Refresh(); now = process.TotalProcessorTime; }
        catch (InvalidOperationException) { now = TimeSpan.Zero; }
        lock (process)
        {
            startProcessor = lastProcessor = now;
            movedAt = Environment.TickCount64;
            Interlocked.Exchange(ref inFlight, 1);
        }
    }

    public ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports, IReadOnlySet<string>? retained = null,
        LifetimeFacts? facts = null, IrArchive? archive = null)
    {
        // The unit's own file when it was read from one and is unchanged:
        // written out again, every unit's IR passed through the link's memory.
        bool own = original.SourcePath is not null && File.Exists(original.SourcePath);
        string input = own ? original.SourcePath! : Path.Combine(work, "input-" + sequence + ".o"), output = Path.Combine(work, "output-" + sequence++ + ".o");
        try
        {
            if (!own) ElfWriter.WriteObjectFile(original, input);
            BackendProtocol.WriteRequest(writer, new(input, output, imports, retained, facts));
            // READ HERE, ON THIS THREAD, with a watchdog for a stalled backend:
            // the read once ran as a task waited on with a timeout, and the
            // link's workers shared one task queue -- a worker ran another's
            // blocking read, and a response that had come was waited on to
            // the timeout. The watchdog's kill ends the read.
            Begin();
            try { BackendProtocol.ReadResponse(reader); }
            catch (Exception) when (Volatile.Read(ref timedOut))
            { throw new IOException("Compiler backend stopped on " + (original.SourcePath ?? input) + ": it " + killedFor); }
            finally { Interlocked.Exchange(ref inFlight, 0); }
            if (!File.Exists(output)) throw new IOException("Compiler backend reported success without an object");
            return ElfReader.ReadObjectFile(output, leaveInFile: false);
        }
        // WHICH UNIT, AND HOW THE BACKEND ENDED: a backend that died left
        // only "Broken pipe" behind, from the next request written to it.
        catch (IOException e) when (!Volatile.Read(ref timedOut))
        {
            string how = process.HasExited ? "exited with " + process.ExitCode : "still running";
            throw new IOException("compiler backend failed on " + (original.SourcePath ?? input) + " (" + how + "): " + e.Message, e);
        }
        finally { if (!own) File.Delete(input); File.Delete(output); }
    }

    public void Dispose()
    {
        Volatile.Write(ref disposed, true);
        try { writer.Dispose(); } catch (IOException) { }
        try { process.StandardInput.Close(); } catch (IOException) { }
        if (!process.WaitForExit(2000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        reader.Dispose(); process.Dispose();
        // Only this instance's exact private directory; no recursive deletion.
        foreach (string file in Directory.EnumerateFiles(work)) File.Delete(file);
        Directory.Delete(work);
    }
}
