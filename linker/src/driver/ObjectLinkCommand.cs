#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac;

/// <summary>Link previously compiled objects without invoking the frontend.</summary>
public static class ObjectLinkCommand
{
    public static int Run(string[] args)
    {
        string? output = null;
        string entry = "_start";
        List<string> paths = new();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-o" or "--entry")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                if (arg == "-o") output = args[i];
                else entry = args[i];
            }
            else if (arg.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown link option '" + arg + "'");
            else paths.Add(arg);
        }
        if (output is null || paths.Count == 0)
            return Fail("usage: corlink <file.o> ... -o <executable> [--entry <symbol>]");
        string destination = Path.GetFullPath(output);
        List<(string, ObjectFile)> inputs = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            if (full == destination) return Fail("output would overwrite input '" + path + "'");
            if (!seen.Add(full)) return Fail("object supplied twice: '" + path + "'");
            if (!File.Exists(full)) return Fail("object does not exist: '" + path + "'");
            try { inputs.Add((path, ElfReader.ReadObject(File.ReadAllBytes(full)))); }
            catch (ElfFormatException error) { return Fail(path + ": " + error.Message); }
        }
        // Resolve every input and relocation before writing the destination.
        // LinkException is rendered by Driver, just as for compile-and-link.
        byte[] image = Linker.Link(inputs, entry);
        File.WriteAllBytes(output, image);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(output, File.GetUnixFileMode(output)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        Console.Error.WriteLine($"{output}: {inputs.Count} objects, {image.Length} bytes");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("corlink: " + message);
        return 1;
    }
}
