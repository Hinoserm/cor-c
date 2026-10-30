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
        // A ring-1 module whose text the binder relocates (Linker.LinkModule): no GOT, direct calls into the kernel.
        bool relocatedText = false;
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
        // THE KERNEL AS A LIBRARY (KernelExports): --exports puts the
        // kernel's export table in its own file, a section nothing loads.
        // --kernel FILE links a module against such a kernel (Linker.LinkModule).
        bool exports = false;
        string? kernelPath = null, keepPath = null;
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
            if (arg == "--exports") { exports = true; continue; }
            if (arg is "--kernel" or "--keep")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                if (arg == "--kernel") kernelPath = args[i]; else keepPath = args[i];
                continue;
            }
            // THE WAIT CHECK, as corc's own link takes it (InterruptNotes.WaitsFromHandlers):
            // a module's link checks the module's facts.
            if (arg is "--irq-waits" or "--wait-free-roots" or "--wait-free-stop" or "--irq-roots" or "--irq-stop")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                if (arg == "--irq-waits") Switches.IrqWaits = args[i];
                else if (arg == "--wait-free-roots") Switches.WaitFreeRoots = args[i];
                else if (arg == "--irq-roots") Switches.IrqRoots = args[i];
                else if (arg == "--irq-stop") Switches.IrqStop = args[i];
                else Switches.WaitFreeStop = args[i];
                continue;
            }
            if (arg == "--irq-locks-report") { Switches.IrqLocksReport = true; continue; }
            if (arg == "--unused-report")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                unusedReport = args[i];
                continue;
            }
            if (arg is "--cpu" or "--fpu")
            {
                if (++i == args.Length) return Fail("missing value for " + arg);
                cpuArguments.Add(arg); cpuArguments.Add(args[i]); continue;
            }
            if (arg.StartsWith("--cpu=") || arg.StartsWith("--fpu=")
                || Lang.X86.X86Cpu.IsSwitch(arg))
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
            else if (arg == "--relocated-text") relocatedText = true;
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
                + " [--exports [--keep names]] [--kernel kernel]");
        if (unusedReport is not null && shared) return Fail("--unused-report is for an image with an entry, not a shared object");
        if (exports)
        {
            if (shared || flat || kernelPath is not null || sharedLibraries.Count > 0)
                return Fail("--exports is for a kernel: one image linked whole, with an entry and its symbols");
            if (closed) return Fail("--exports makes the kernel a library, whose modules call into it: it is not --closed");
            if (keepPath is not null && !File.Exists(keepPath)) return Fail("--keep '" + keepPath + "' does not exist");
        }
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
        // What the units' archives say alike, held once whoever reads them first (IrArchivePool).
        IrArchivePool archivePool = new();
        foreach ((string _, ObjectFile obj) in inputs) obj.ArchivePool = archivePool;
        TargetContract.Validate(inputs);
        X86CodeGenerationContract? selected = cpuArguments.Count == 0 ? null : Lang.X86.X86Cpu.Parse(cpuArguments).Contract;
        if (selected is not null) X86CodeGenerationContract.ValidateTarget(inputs, selected);
        // NO PROCESSOR NAMED, NONE CHOSEN FOR IT: units made for different
        // ones -- a runtime compiled for the 486 in a 386's kernel -- linked
        // into an image that ran CMPXCHG where the 386 has none, and nothing
        // said so. They agree, or the link is told which processor it is for.
        else if (X86CodeGenerationContract.Disagreeing(inputs) is { } disagreeing)
            return Fail(disagreeing.First + " and " + disagreeing.Second + " were compiled for different processors (" + disagreeing.FirstCpu + ", "
                + disagreeing.SecondCpu + "): name the image's with --cpu");
        ManagedLayoutContract.Validate(inputs);
        // Before the link-time optimiser regenerates any unit: the notes are
        // the units' own, and the IR archive's digest covers them.
        if (unusedReport is not null) UnusedReport.Write(inputs, entry, unusedReport);
        // EVERY INTERRUPT HANDLER, THROUGH EVERY UNIT: what no one unit's
        // compile could follow (InterruptNotes).
        // The facts beside the kernel and the modules linked with
        // (InterruptNotes.ReadBeside): the wait check walks into them.
        List<InterruptNotes.Fact> imported = new();
        List<InterruptNotes.Fact> kernelImported = kernelPath is not null ? InterruptNotes.ReadBeside(kernelPath) : new();
        imported.AddRange(kernelImported);
        foreach (string library in sharedLibraries) imported.AddRange(InterruptNotes.ReadBeside(library));
        List<string> interruptErrors = InterruptNotes.Check(inputs.Select(input => input.Item2), Preinit.Peek(inputs.Select(input => input.Item2), longMode: false)?.FrozenSteps, imported);
        // REFUSED, every path listed (InterruptNotes.EveryAllocation): the
        // kernel's handlers reach no allocation since the wake predicate
        // left WaitQueue.WakeAll (Sched.WakeQueuedIfOver) and the AIC's
        // drain stopped making its messages.
        if (interruptErrors.Count > 0) throw new LinkException(interruptErrors);
        if (Switches.IrqLocksReport) foreach (string line in InterruptNotes.IrqLocksReport(inputs.Select(input => input.Item2))) Console.Error.WriteLine(line);
        // WHERE EVERY UNIT'S FREESTANDING FLOAT CODE RUNS (X87Notes): inside
        // a packed block, its own or every caller's -- the kernel's and the
        // modules' linked with, as their links saw them, beside them.
        bool judges = X87Notes.Judges(inputs.Select(input => input.Item2));
        List<X87Notes.Fact> floatsImported = new();
        List<X87Notes.Fact> kernelFloatsImported = kernelPath is not null ? X87Notes.ReadBeside(kernelPath, judges) : new();
        floatsImported.AddRange(kernelFloatsImported);
        foreach (string library in sharedLibraries) floatsImported.AddRange(X87Notes.ReadBeside(library, judges));
        List<string> floatErrors = X87Notes.Check(inputs.Select(input => input.Item2), floatsImported, new[] { entry });
        if (floatErrors.Count > 0) throw new LinkException(floatErrors);
        // AN IMAGE OTHERS LINK AGAINST says what its units call and take
        // beside it, for their links' checks -- a kernel's and a module's
        // beside the image, in the build, never in what is installed.
        if (exports || kernel is not null) InterruptNotes.WriteBeside(destination, inputs.Select(input => input.Item2));
        // And a shared object's: what links against it calls into it too.
        if (exports || kernel is not null || shared) X87Notes.WriteBeside(destination, inputs.Select(input => input.Item2));
        // A KERNEL WITH EXPORTS IS NOT CLOSED, whatever its address says:
        // what a module calls, subclasses or stores into is outside it, so
        // nothing the link proves about the whole program holds (closed
        // facts, reachability), and neither does a virtual call reaching only
        // the overrides the image itself holds -- a module's driver overrides
        // the kernel's Driver.Bind. NOR A MODULE: its units' calls reach the
        // kernel's overrides too, and other modules'.
        int regenerated = IrLinkOptimizer.Run(inputs, () => backend ?? new ProcessUnitBackend(backendPath), lto, importBytes,
            closedImageEntry: (flat || closed || physicalAddress is not null) && !exports ? entry : null, parallelBackends: backend is null, regionReport: regionReport,
            // Only the types the image makes, where nothing outside it makes
            // any: no shared object, no shared library, no exports a module
            // derives from (VirtualTargets.Made).
            madeOnly: !Switches.NoRta && !shared && sharedLibraries.Count == 0 && !exports && kernel is null,
            openTypes: exports || kernel is not null, reachableFrom: keep is not null ? entry : null, keep: keep,
            moduleOfKernel: kernel is not null);
        int folded = LinkTimeOptimizer.Run(inputs, lto);
        // The archives' last reader done: their pool goes, not held through layout.
        foreach ((string _, ObjectFile obj) in inputs) obj.ArchivePool = null;
        LinkTimings.Phase("constant returns and coalescing");
        if (selected is not null) X86CodeGenerationContract.ValidateTarget(inputs, selected);
        // Long mode is read before the notes that say so go.
        bool longMode = inputs.Any(input => TargetContract.IsLongMode(input.Item2));

        // What a module must agree with, read before the contracts go: the
        // kernel's units' native contract (one that claims a thread-block
        // model, not an assembled stub's) and their processor -- that same
        // unit's, never the stub's: the assembled entry, linked first, says
        // the 486, and a Pentium kernel's modules were refused for being
        // built as the kernel was.
        byte[]? kernelAbi = null, kernelCpu = null, firstCpu = null;
        if (exports)
        {
            foreach (var input in inputs)
            {
                byte[]? abi = null, cpu = null;
                foreach (Section section in input.Item2.Sections)
                {
                    if (section.Name == TargetContract.SectionName && section.Size == TargetContract.Size
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(section.Content().AsSpan(20)) != TargetContract.NoTlsClaim)
                        abi = section.Content();
                    if (section.Name == X86CodeGenerationContract.SectionName) cpu = section.Content();
                }
                firstCpu ??= cpu;
                if (abi is not null && kernelAbi is null) { kernelAbi = abi; kernelCpu = cpu; }
            }
            kernelCpu ??= firstCpu;
            inputs.Add(("the link's identity", KernelExports.LinkIdObject()));
        }
        if (kernel is not null) Linker.AgreeWithKernel(inputs, kernel);
        // These contracts have been consumed by validation, and the layout
        // would pass them over (Linker.ConsumedContract). This command links
        // its objects once, so it takes them out here: their memory goes
        // before the image is laid out.
        foreach (var input in inputs)
            input.Item2.Sections.RemoveAll(section => Linker.ConsumedContract(section.Name)
                || section.Name == UsesNotes.SectionName
                || section.Name == InterruptNotes.SectionName
                || section.Name == X87Notes.SectionName);
        // Every unit's frame table names from one pool (FramePool).
        if (lto) FramePool.Run(inputs, longMode);
        LinkTimings.Phase("frame names");
        byte[] image;
        bool streamed = false;
        if (flat)
        {
            Linker.FlatImage linked = Linker.LinkFlat(inputs, entry, baseAddress ?? 0x10000, longMode, map);
            image = linked.Bytes;
            Console.Error.WriteLine($"flat: entry=0x{linked.Entry:x} base=0x{linked.Base:x} bss={linked.BssSize} memory={linked.MemorySize}");
        }
        else if (kernel is not null)
        {
            image = Linker.LinkModule(inputs, output, kernel, sharedLibraries.Distinct(StringComparer.Ordinal).ToList(), relocatedText);
        }
        else if (shared || sharedLibraries.Count > 0)
        {
            HashSet<string> defined = new(inputs.SelectMany(x => x.Item2.Symbols).Where(s => s.IsDefined).Select(s => s.Name), StringComparer.Ordinal);
            HashSet<string> unresolved = new(inputs.SelectMany(x => x.Item2.Symbols).Where(s => !s.IsDefined && !defined.Contains(s.Name)).Select(s => s.Name), StringComparer.Ordinal);
            List<string> needed = sharedLibraries.Distinct(StringComparer.Ordinal).Where(path => ElfReader.ExportsOf(File.ReadAllBytes(path)).Any(unresolved.Contains)).ToList();
            image = shared ? Linker.LinkShared(inputs, Path.GetFileName(output), needed, runpath, baseAddress ?? 0, sharedLibraries, longMode: longMode)
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
        if (exports)
        {
            // In the kernel's own file, a section nothing loads (KernelExports),
            // with the identity the written kernel holds.
            byte[] linkId = KernelExports.StampLinkId(output);
            List<KernelExports.Export> globals = KernelExports.GlobalsOf(output);
            byte[] table = KernelExports.Table(globals, kernelAbi, kernelCpu, linkId, longMode);
            KernelExports.AddTo(output, table);
            Console.Error.WriteLine($"{output}: {globals.Count} kernel exports, {KernelExports.SectionName} {table.Length} bytes");
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
