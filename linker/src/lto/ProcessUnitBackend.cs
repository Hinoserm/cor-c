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

    public ProcessUnitBackend(string? executable = null)
    {
        string path = executable ?? Environment.GetEnvironmentVariable("CORC")
            ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "corc.exe" : "corc");
        if (!File.Exists(path)) throw new FileNotFoundException("IR LTO needs the compiler backend; set CORC or --lto-backend, or use --no-lto", path);
        ProcessStartInfo start = new(Path.GetFullPath(path)) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("backend");
        work = Directory.CreateTempSubdirectory("corc-lto-").FullName;
        try { process = Process.Start(start) ?? throw new IOException("Could not start compiler backend"); }
        catch { Directory.Delete(work); throw; }
        writer = new BinaryWriter(process.StandardInput.BaseStream, BackendProtocol.Utf8, leaveOpen: true);
        reader = new BinaryReader(process.StandardOutput.BaseStream, BackendProtocol.Utf8, leaveOpen: true);
    }

    public ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports)
    {
        string input = Path.Combine(work, "input-" + sequence + ".o"), output = Path.Combine(work, "output-" + sequence++ + ".o");
        try
        {
            File.WriteAllBytes(input, ElfWriter.WriteObject(original));
            BackendProtocol.WriteRequest(writer, new(input, output, imports));
            Task response = Task.Run(() => BackendProtocol.ReadResponse(reader));
            try { response.WaitAsync(TimeSpan.FromMinutes(5)).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            { process.Kill(entireProcessTree: true); throw new IOException("Compiler backend exceeded five-minute unit timeout"); }
            if (!File.Exists(output)) throw new IOException("Compiler backend reported success without an object");
            return ElfReader.ReadObject(File.ReadAllBytes(output));
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    public void Dispose()
    {
        try { writer.Dispose(); } catch (IOException) { }
        try { process.StandardInput.Close(); } catch (IOException) { }
        if (!process.WaitForExit(2000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        reader.Dispose(); process.Dispose();
        // Only this instance's exact private directory; no recursive deletion.
        foreach (string file in Directory.EnumerateFiles(work)) File.Delete(file);
        Directory.Delete(work);
    }
}
