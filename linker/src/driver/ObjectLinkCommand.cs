#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using System.Globalization;

namespace Corsac;

/// <summary>Link previously compiled objects without invoking the frontend.</summary>
public static class ObjectLinkCommand
{
    public static int Run(string[] args, IUnitBackend? backend = null)
    {
        string? output = null;
        string entry = "_start";
        ulong? baseAddress = null;
        ulong? physicalAddress = null;
        bool flat = false;
        // The image is the whole program: nothing outside it calls in or
        // throws. Flat and physically placed images are so by their nature;
        // an executable linked against no shared library is so when asked.
        bool closed = false;
        string? map = null;
        bool shared = false;
        bool noUndefined = false;
        bool lto = true;
        string? backendPath = null;
        int importBytes = 1024 * 1024;
        List<string> paths = new();
        List<string> sharedLibraries = new();
        string? runpath = null;
        List<string> cpuArguments = new();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--link-shared" or "--runpath")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                if (arg == "--runpath") runpath = args[i]; else sharedLibraries.Add(args[i]);
                continue;
            }
            if (arg is "--cpu" or "--tune" or "--fpu")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                cpuArguments.Add(arg); cpuArguments.Add(args[i]); continue;
            }
            if (arg.StartsWith("--cpu=") || arg.StartsWith("--tune=") || arg.StartsWith("--fpu=")
                || arg is "--enable-mmx" or "--disable-mmx" or "--enable-3dnow" or "--disable-3dnow")
            { cpuArguments.Add(arg); continue; }
            if (arg is "-o" or "--entry" or "--base" or "--paddr" or "--lto-backend" or "--lto-import-bytes")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                if (arg == "-o") output = args[i];
                else if (arg == "--entry") entry = args[i];
                else if (arg == "--lto-backend") backendPath = args[i];
                else if (arg == "--lto-import-bytes")
                {
                    if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out importBytes)
                        || importBytes < 0 || importBytes > 16 * 1024 * 1024) return Fail("invalid LTO import budget");
                }
                else
                {
                    string number = args[i];
                    bool hex = number.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                    if (!ulong.TryParse(hex ? number[2..] : number, hex ? NumberStyles.HexNumber : NumberStyles.None,
                        CultureInfo.InvariantCulture, out ulong address)) return Fail("invalid address " + number);
                    if (arg == "--base") baseAddress = address;
                    else physicalAddress = address;
                }
            }
            else if (arg == "--flat") flat = true;
            else if (arg == "--closed") closed = true;
            else if (arg == "--map" && i + 1 < args.Length) map = args[++i];
            else if (arg == "--shared") shared = true;
            else if (arg == "--no-undefined") noUndefined = true;
            else if (arg == "--no-lto") lto = false;
            else if (arg == "--lto") lto = true;
            else if (arg.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown link option '" + arg + "'");
            else paths.Add(arg);
        }
        if (output is null || paths.Count == 0)
            return Fail("usage: corlink <file.o> ... -o <output> [--entry symbol] [--flat] [--closed] [--base address] [--paddr address] [--no-lto]");
        if (flat && physicalAddress is not null) return Fail("--paddr is for ELF output; use --base for flat images");
        if (closed && (shared || sharedLibraries.Count > 0)) return Fail("--closed is for an image linked against no shared library");
        if ((shared || sharedLibraries.Count > 0) && (flat || physicalAddress is not null))
            return Fail("shared libraries cannot be combined with flat output or a physical address");
        if (!shared && sharedLibraries.Count > 0 && baseAddress is not null)
            return Fail("a dynamically linked executable is laid out at the default address");
        string destination = Path.GetFullPath(output);
        List<(string, ObjectFile)> inputs = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            if (full == destination) return Fail("output would overwrite input '" + path + "'");
            if (!seen.Add(full)) return Fail("object supplied twice: '" + path + "'");
            if (!File.Exists(full)) return Fail("object does not exist: '" + path + "'");
            // Read so that each unit's IR stays in its file (ReadObjectFile):
            // a link holds the objects' code and tables, never their IR.
            try { inputs.Add((path, ElfReader.ReadObjectFile(full))); }
            catch (ElfFormatException error) { return Fail(path + ": " + error.Message); }
        }
        // Resolve every input and relocation before writing the destination.
        // LinkException is rendered by Driver, just as for compile-and-link.
        TargetContract.Validate(inputs);
        X86CodeGenerationContract? selected = cpuArguments.Count == 0 ? null : Lang.X86.X86Cpu.Parse(cpuArguments).Contract;
        if (selected is not null) X86CodeGenerationContract.ValidateTarget(inputs, selected);
        ManagedLayoutContract.Validate(inputs);
        int regenerated = IrLinkOptimizer.Run(inputs, () => backend ?? new ProcessUnitBackend(backendPath), lto, importBytes,
            closedImageEntry: flat || closed || physicalAddress is not null ? entry : null, parallelBackends: backend is null);
        int folded = LinkTimeOptimizer.Run(inputs, lto);
        if (selected is not null) X86CodeGenerationContract.ValidateTarget(inputs, selected);
        // Long mode is read before the notes that say so go.
        bool longMode = inputs.Any(input => TargetContract.IsLongMode(input.Item2));

        // These contracts have been consumed by validation. Concatenating one
        // copy per input into an executable is neither a valid contract nor
        // runtime metadata, and can dwarf a small kernel's actual load image.
        foreach (var input in inputs)
            input.Item2.Sections.RemoveAll(section => section.Name == TargetContract.SectionName
                || section.Name == X86CodeGenerationContract.SectionName
                || section.Name == ManagedLayoutContract.SectionName);
        // Every unit's frame table names from one pool (FramePool).
        if (lto) FramePool.Run(inputs);
        byte[] image;
        bool streamed = false;
        if (flat)
        {
            Linker.FlatImage linked = Linker.LinkFlat(inputs, entry, checked((uint)(baseAddress ?? 0x10000)), longMode, map);
            image = linked.Bytes;
            Console.Error.WriteLine($"flat: entry=0x{linked.Entry:x} base=0x{linked.Base:x} bss={linked.BssSize} memory={linked.MemorySize}");
        }
        else if (shared || sharedLibraries.Count > 0)
        {
            HashSet<string> defined = new(inputs.SelectMany(x => x.Item2.Symbols).Where(s => s.IsDefined).Select(s => s.Name), StringComparer.Ordinal);
            HashSet<string> unresolved = new(inputs.SelectMany(x => x.Item2.Symbols).Where(s => !s.IsDefined && !defined.Contains(s.Name)).Select(s => s.Name), StringComparer.Ordinal);
            List<string> needed = sharedLibraries.Distinct(StringComparer.Ordinal).Where(path => ElfReader.ExportsOf(File.ReadAllBytes(path)).Any(unresolved.Contains)).ToList();
            image = shared ? Linker.LinkShared(inputs, Path.GetFileName(output), needed, runpath, checked((uint)(baseAddress ?? 0)), sharedLibraries, longMode: longMode)
                : Linker.Link(inputs, entry, needed, runpath, libraries: sharedLibraries, longMode: longMode);
        }
        else
        {
            // Straight to the file, a chunk at a time (Linker.LinkTo), the
            // inputs' sections taken rather than copied: nothing links them again.
            Linker.ReleaseSections = true;
            Linker.LinkTo(output, inputs, entry, baseAddress ?? Linker.DefaultLoadAddress, physicalAddress, longMode: longMode);
            image = Array.Empty<byte>();
            streamed = true;
        }
        if (noUndefined && (shared || sharedLibraries.Count > 0))
        {
            HashSet<string> provided = new(sharedLibraries.SelectMany(path => ElfReader.ExportsOf(File.ReadAllBytes(path))), StringComparer.Ordinal);
            string[] missing = ElfReader.ImportsOf(image).Where(name => !provided.Contains(name)).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length != 0) return Fail("unresolved shared-library imports: " + string.Join(", ", missing));
        }
        if (!streamed) File.WriteAllBytes(output, image);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(output, File.GetUnixFileMode(output)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        Console.Error.WriteLine($"{output}: {inputs.Count} objects, {(streamed ? new FileInfo(output).Length : image.Length)} bytes; LTO calls folded={folded}; IR units regenerated={regenerated}");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("corlink: " + message);
        return 1;
    }
}
