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
        while (true)
        {
            BackendRequest? request;
            try { request = BackendProtocol.ReadRequest(reader); }
            catch (Exception error) { BackendProtocol.WriteResponse(writer, error.Message); return 1; }
            if (request is null) return 0;
            try
            {
                if (Path.GetFullPath(request.Input) == Path.GetFullPath(request.Output)) throw new InvalidDataException("Backend output would overwrite input");
                var original = ElfReader.ReadObject(File.ReadAllBytes(request.Input));
                var result = backend.Recompile(original, request.Imports, request.Retained);
                File.WriteAllBytes(request.Output, ElfWriter.WriteObject(result));
                BackendProtocol.WriteResponse(writer, null);
            }
            catch (Exception error) { BackendProtocol.WriteResponse(writer, error.Message); }
        }
    }
}
