#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using System.Globalization;

namespace Corsac;

/// <summary>Link previously compiled objects without invoking the frontend.</summary>
public static class ObjectLinkCommand
{
    public static int Run(string[] args)
    {
        string? output = null;
        string entry = "_start";
        uint? baseAddress = null;
        uint? physicalAddress = null;
        bool flat = false;
        bool lto = true;
        List<string> paths = new();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-o" or "--entry" or "--base" or "--paddr")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                if (arg == "-o") output = args[i];
                else if (arg == "--entry") entry = args[i];
                else
                {
                    string number = args[i];
                    bool hex = number.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                    if (!uint.TryParse(hex ? number[2..] : number, hex ? NumberStyles.HexNumber : NumberStyles.None,
                        CultureInfo.InvariantCulture, out uint address)) return Fail("invalid address " + number);
                    if (arg == "--base") baseAddress = address;
                    else physicalAddress = address;
                }
            }
            else if (arg == "--flat") flat = true;
            else if (arg == "--no-lto") lto = false;
            else if (arg == "--lto") lto = true;
            else if (arg.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown link option '" + arg + "'");
            else paths.Add(arg);
        }
        if (output is null || paths.Count == 0)
            return Fail("usage: corlink <file.o> ... -o <output> [--entry symbol] [--flat] [--base address] [--paddr address] [--no-lto]");
        if (flat && physicalAddress is not null) return Fail("--paddr is for ELF output; use --base for flat images");
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
        TargetContract.Validate(inputs);
        int folded = LinkTimeOptimizer.Run(inputs, lto);
        byte[] image;
        if (flat)
        {
            Linker.FlatImage linked = Linker.LinkFlat(inputs, entry, baseAddress ?? 0x10000);
            image = linked.Bytes;
            Console.Error.WriteLine($"flat: entry=0x{linked.Entry:x} base=0x{linked.Base:x} bss={linked.BssSize} memory={linked.MemorySize}");
        }
        else image = Linker.Link(inputs, entry, baseAddress ?? Linker.DefaultLoadAddress, physicalAddress);
        File.WriteAllBytes(output, image);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(output, File.GetUnixFileMode(output)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        Console.Error.WriteLine($"{output}: {inputs.Count} objects, {image.Length} bytes; LTO calls folded={folded}");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("corlink: " + message);
        return 1;
    }
}
