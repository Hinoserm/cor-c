using Corsac.Lang.Elf;
using Corsac.Lang.Lto;

namespace Corsac;

public static class BackendCommand
{
    public static int Run()
    {
        using BinaryReader reader = new(Console.OpenStandardInput(), BackendProtocol.Utf8);
        using BinaryWriter writer = new(Console.OpenStandardOutput(), BackendProtocol.Utf8);
        UnitBackend backend = new();
        // THE WORKERS ITS PASSES AND CODE GENERATION HAND WORK TO, as a
        // compile's (Driver.Compile): without them every Task.Run waited on
        // ran on this thread, one after another, and the link's largest unit
        // was regenerated on one processor. .NET's call, which the native
        // library answers with the scheduler's workers: an #if !NET here was
        // never compiled, corc defining NET as the SDK does.
        if (UnitBackend.Workers > 0) ThreadPool.SetMinThreads(UnitBackend.Workers, UnitBackend.Workers);
        while (true)
        {
            BackendRequest? request;
            try { request = BackendProtocol.ReadRequest(reader); }
            catch (Exception error) { BackendProtocol.WriteResponse(writer, error.Message); return 1; }
            // --report-passes: what each pass cost this backend over every
            // unit it regenerated, as a compile says it after code generation.
            if (request is null) { Corsac.Lang.Opt.Pipeline.ReportAccounts(); return 0; }
            try
            {
                if (Path.GetFullPath(request.Input) == Path.GetFullPath(request.Output)) throw new InvalidDataException("Backend output would overwrite input");
                var original = ElfReader.ReadObjectFile(request.Input, leaveInFile: false);
                var result = backend.Recompile(original, request.Imports, request.Retained, request.Facts);
                ElfWriter.WriteObjectFile(result, request.Output);
                BackendProtocol.WriteResponse(writer, null);
            }
            catch (Exception error)
            {
                // The whole of it where a person can read it: the link hears
                // only the message, and a failure in a backend process had no
                // trace anywhere.
                Console.Error.WriteLine("corc backend: " + request.Input + ": " + error);
                BackendProtocol.WriteResponse(writer, error.Message);
            }
        }
    }
}
