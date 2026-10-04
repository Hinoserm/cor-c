#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using System.Globalization;

namespace Corsac;

/// <summary>Link previously compiled objects without invoking the frontend.</summary>
public static class ObjectLinkCommand
{
    /// <param name="declarationStamp">
    /// The build stamp of a declaration index (DeclarationStamp), for
    /// --exports: the index is the compiler's format, and this is how the
    /// compiler's own `corc link` hands the linker the means to read one.
    /// </param>
    public static int Run(string[] args, IUnitBackend? backend = null, Func<string, byte[]>? declarationStamp = null)
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
        string? regionReport = null;
        string? unusedReport = null;
        // THE KERNEL AS A LIBRARY (KernelExports): --exports FILE writes the
        // kernel's globals beside it, stamped with the hash of --decl-index,
        // the index its units were compiled against. --kernel FILE links a
        // module against such a file (Linker.LinkModule).
        string? exportsPath = null, stampIndex = null, kernelPath = null, keepPath = null;
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
            // The regions the link finds over every unit (RegionSolver): for
            // the boundaries whose names hold one of these, what is local and
            // what outlives them.
            if (arg == "--region-report")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                regionReport = args[i];
                continue;
            }
            // The program's dead code (UnusedReport), into a file ("-": the
            // error stream), from the notes units compiled with the same flag left.
            if (arg is "--exports" or "--decl-index" or "--kernel" or "--keep")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                if (arg == "--exports") exportsPath = args[i]; else if (arg == "--kernel") kernelPath = args[i]; else if (arg == "--keep") keepPath = args[i]; else stampIndex = args[i];
                continue;
            }
            if (arg == "--unused-report")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                unusedReport = args[i];
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
            // A line at the end of each phase: its time and the memory it took (LinkTimings).
            else if (arg == "--timings") LinkTimings.Enabled = true;
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
            return Fail("usage: corlink <file.o> ... -o <output> [--entry symbol] [--flat] [--closed] [--base address] [--paddr address] [--no-lto] [--timings] [--unused-report file]"
                + " [--exports file --decl-index index [--keep names]] [--kernel exports]");
        if (unusedReport is not null && shared) return Fail("--unused-report is for an image with an entry, not a shared object");
        if (exportsPath is not null)
        {
            if (shared || flat || kernelPath is not null || sharedLibraries.Count > 0)
                return Fail("--exports is for a kernel: one image linked whole, with an entry and its symbols");
            if (closed) return Fail("--exports makes the kernel a library, whose modules call into it: it is not --closed");
            if (stampIndex is null) return Fail("--exports needs --decl-index: the declaration index the kernel was compiled against, whose hash is its build stamp");
            if (declarationStamp is null) return Fail("--exports: this linker cannot read a declaration index; link with `corc link`");
            if (!File.Exists(stampIndex)) return Fail("--decl-index '" + stampIndex + "' does not exist");
            if (keepPath is not null && !File.Exists(keepPath)) return Fail("--keep '" + keepPath + "' does not exist");
        }
        else if (stampIndex is not null) return Fail("--decl-index stamps a kernel linked with --exports");
        else if (keepPath is not null) return Fail("--keep names what a kernel linked with --exports keeps for its modules");
        // WHAT THE BUILD'S MODULES IMPORT, one name a line: with it the kernel
        // is pruned to what it and they reach, as a closed image is; without
        // it every global is kept, for modules nobody has built yet.
        string[]? keep = keepPath is null ? null
            : File.ReadAllLines(keepPath).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        KernelExports? kernel = null;
        if (kernelPath is not null)
        {
            if (flat || physicalAddress is not null || closed || baseAddress is not null)
                return Fail("--kernel links a module, which is a shared object loaded wherever the kernel puts it");
            try { kernel = KernelExports.Read(kernelPath); }
            catch (ElfFormatException error) { return Fail(error.Message); }
            shared = true;
        }
        if (flat && physicalAddress is not null) return Fail("--paddr is for ELF output; use --base for flat images");
        if (closed && (shared || sharedLibraries.Count > 0)) return Fail("--closed is for an image linked against no shared library");
        if ((shared || sharedLibraries.Count > 0) && (flat || physicalAddress is not null))
            return Fail("shared libraries cannot be combined with flat output or a physical address");
        if (!shared && sharedLibraries.Count > 0 && baseAddress is not null)
            return Fail("a dynamically linked executable is laid out at the default address");
        string destination = Path.GetFullPath(output);
        LinkTimings.Start();
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
        LinkTimings.Phase("read objects");
        TargetContract.Validate(inputs);
        X86CodeGenerationContract? selected = cpuArguments.Count == 0 ? null : Lang.X86.X86Cpu.Parse(cpuArguments).Contract;
        if (selected is not null) X86CodeGenerationContract.ValidateTarget(inputs, selected);
        ManagedLayoutContract.Validate(inputs);
        // Before the link-time optimiser regenerates any unit: the notes are
        // the units' own, and the IR archive's digest covers them.
        if (unusedReport is not null) UnusedReport.Write(inputs, entry, unusedReport);
        // A MODULE INITIALISER IS RUN BY A LOADER, which a static image has
        // none of: the compiler refuses one in a program, and a unit
        // compiled into a library and linked into a program is refused here.
        if (!shared && inputs.Any(input => input.Item2.Sections.Any(section => section.Name == ModuleInfo.InitializerSection)))
            return Fail("[ModuleInitializer] runs when a shared object is loaded, and this image is not one");
        byte[]? stamp = exportsPath is null ? null : declarationStamp!(stampIndex!);
        // A KERNEL WITH EXPORTS IS NOT CLOSED, whatever its address says:
        // what a module calls, subclasses or stores into is outside it, so
        // nothing the link proves about the whole program holds (closed
        // facts, reachability), and neither does a virtual call reaching only
        // the overrides the image itself holds -- a module's driver overrides
        // the kernel's Driver.Bind.
        int regenerated = IrLinkOptimizer.Run(inputs, () => backend ?? new ProcessUnitBackend(backendPath), lto, importBytes,
            closedImageEntry: (flat || closed || physicalAddress is not null) && exportsPath is null ? entry : null, parallelBackends: backend is null, regionReport: regionReport,
            openTypes: exportsPath is not null, reachableFrom: keep is not null ? entry : null, keep: keep);
        int folded = LinkTimeOptimizer.Run(inputs, lto);
        LinkTimings.Phase("constant returns and coalescing");
        if (selected is not null) X86CodeGenerationContract.ValidateTarget(inputs, selected);
        // Long mode is read before the notes that say so go.
        bool longMode = inputs.Any(input => TargetContract.IsLongMode(input.Item2));

        // What a module must agree with, read before the contracts go: the
        // kernel's units' native contract (one that claims a thread-block
        // model, not an assembled stub's) and their processor.
        byte[]? kernelAbi = null, kernelCpu = null;
        if (exportsPath is not null)
        {
            foreach (var input in inputs)
                foreach (Section section in input.Item2.Sections)
                {
                    if (section.Name == TargetContract.SectionName && kernelAbi is null && section.Size == 28
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section.Content().AsSpan(20)) != TargetContract.NoTlsClaim)
                        kernelAbi = section.Content();
                    if (section.Name == X86CodeGenerationContract.SectionName && kernelCpu is null) kernelCpu = section.Content();
                }
            inputs.Add(("the build stamp", KernelExports.StampObject(stamp!, loaded: true)));
        }
        if (kernel is not null) Linker.AgreeWithKernel(inputs, kernel);
        // These contracts have been consumed by validation. Concatenating one
        // copy per input into an executable is neither a valid contract nor
        // runtime metadata, and can dwarf a small kernel's actual load image.
        foreach (var input in inputs)
            input.Item2.Sections.RemoveAll(section => section.Name == TargetContract.SectionName
                || section.Name == X86CodeGenerationContract.SectionName
                || section.Name == ManagedLayoutContract.SectionName
                || section.Name == UsesNotes.SectionName);
        // Every unit's frame table names from one pool (FramePool).
        if (lto) FramePool.Run(inputs);
        LinkTimings.Phase("frame names");
        byte[] image;
        bool streamed = false;
        if (flat)
        {
            Linker.FlatImage linked = Linker.LinkFlat(inputs, entry, checked((uint)(baseAddress ?? 0x10000)), longMode, map);
            image = linked.Bytes;
            Console.Error.WriteLine($"flat: entry=0x{linked.Entry:x} base=0x{linked.Base:x} bss={linked.BssSize} memory={linked.MemorySize}");
        }
        else if (kernel is not null)
        {
            image = Linker.LinkModule(inputs, output, kernel, sharedLibraries.Distinct(StringComparer.Ordinal).ToList());
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
        if (noUndefined && kernel is null && (shared || sharedLibraries.Count > 0))
        {
            HashSet<string> provided = new(sharedLibraries.SelectMany(path => ElfReader.ExportsOf(File.ReadAllBytes(path))), StringComparer.Ordinal);
            string[] missing = ElfReader.ImportsOf(image).Where(name => !provided.Contains(name)).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length != 0) return Fail("unresolved shared-library imports: " + string.Join(", ", missing));
        }
        if (!streamed) File.WriteAllBytes(output, image);
        LinkTimings.Phase("layout and write");
        if (exportsPath is not null)
        {
            List<KernelExports.Export> globals = KernelExports.GlobalsOf(output);
            KernelExports.Write(exportsPath, Path.GetFileName(output), globals, stamp!, kernelAbi, kernelCpu, longMode);
            Console.Error.WriteLine($"{exportsPath}: {globals.Count} kernel exports, build stamp {KernelExports.Text(stamp!)}");
        }
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
