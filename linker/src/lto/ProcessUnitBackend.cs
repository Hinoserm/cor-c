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
    private const long UnitTimeoutMs = 5 * 60 * 1000;
    // When the request in flight runs out of time (0: none in flight), and
    // whether the watchdog killed the process for it.
    private long deadline;
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

    /// Kills the process when a request outlives its time: the read waiting
    /// on it then ends.
    private void Watch()
    {
        while (!Volatile.Read(ref disposed))
        {
            Thread.Sleep(1000);
            long due = Interlocked.Read(ref deadline);
            if (due != 0 && Environment.TickCount64 > due)
            {
                Volatile.Write(ref timedOut, true);
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return;
            }
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
            if (!own) File.WriteAllBytes(input, ElfWriter.WriteObject(original));
            BackendProtocol.WriteRequest(writer, new(input, output, imports, retained, facts));
            // READ HERE, ON THIS THREAD, with a watchdog for the time limit:
            // the read once ran as a task waited on with a timeout, and the
            // link's workers shared one task queue -- a worker ran another's
            // blocking read, and a response that had come was waited on to
            // the timeout. The watchdog's kill ends the read.
            Interlocked.Exchange(ref deadline, Environment.TickCount64 + UnitTimeoutMs);
            try { BackendProtocol.ReadResponse(reader); }
            catch (Exception) when (Volatile.Read(ref timedOut))
            { throw new IOException("Compiler backend exceeded five-minute unit timeout"); }
            finally { Interlocked.Exchange(ref deadline, 0); }
            if (!File.Exists(output)) throw new IOException("Compiler backend reported success without an object");
            return ElfReader.ReadObject(File.ReadAllBytes(output));
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
