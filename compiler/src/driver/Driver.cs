#nullable enable
using Corsac.Lang;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lower;
using Corsac.Lang.X86;
using Corsac.Lang.Metadata;

namespace Corsac;

/// <summary>
/// The commands. `compile` runs the whole pipeline -- parse, bind, lower,
/// optimise, generate, link -- and the flags let each stage be inspected
/// on its way through, which is how the backend gets debugged.
/// </summary>
public static class Driver
{
    /// <summary>
    /// The command a name stands for, when the toolchain is reached under one
    /// of the names it used to install. `corlink a.o -o x` and `build test`
    /// were how these were typed for a long time and appear in scripts,
    /// notes and muscle memory; a link named after the old program keeps
    /// them working, and the name is simply read as the first argument.
    /// </summary>
    private static string? CommandForName(string name) => name switch
    {
        "corlink" or "corlink.exe" => "link",
        "build" or "build.exe" or "corbuild" => "build",
        "corasm" => "asm",
        _ => null,
    };

    /// <summary>
    /// The name this run was typed as, which is not the same as the file it
    /// ended up executing: the runtime resolves its own path through the
    /// symbolic link, so it reports corc whatever name was used. Linux keeps
    /// the word the shell actually passed, and that is the one that decides.
    /// </summary>
    private static string InvokedAs()
    {
        try
        {
            if (File.Exists("/proc/self/cmdline"))
            {
                string line = File.ReadAllText("/proc/self/cmdline");
                int end = line.IndexOf('\0');
                string first = end < 0 ? line : line[..end];
                if (first.Length != 0) return first;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Environment.GetCommandLineArgs().FirstOrDefault() ?? "";
    }

    public static int Run(string[] args)
    {
        Binder.LibrarySource = IsLibrarySource;
        string called = Path.GetFileName(InvokedAs());
        if (CommandForName(called) is string implied) args = [implied, ..args];
        if (args.Length == 0)
        {
            return Usage();
        }

        string command = args[0];
        string[] rest = args[1..];

        try
        {
            return command switch
            {
                "compile" or "cc" => Compile(rest),
                "compile-project" => ProjectCompile.Run(rest),
                "link" when rest.Length == 1 && rest[0] is "--help" or "-h" => LinkUsage(),
                "link" => ObjectLinkCommand.Run(Response(rest), new UnitBackend()),
                "index" => IndexCommand.Run(Response(rest)),
                "library-sources" => LibrarySources(rest),
                "dependencies-current" => DependenciesCurrent(rest),
                "project" => Projects.ProjectCommand.Run(rest),
                "backend" when rest.Length == 0 => BackendCommand.Run(),
                // `corc build` runs a build manifest; assembling one file is
                // `corc asm`, which is what that command was always called
                // when anyone wrote it down.
                "build" => Corsac.Build.BuildCommand.Run(rest).GetAwaiter().GetResult(),
                "asm" => Build(rest),
                "help" or "--help" or "-h" => Usage(),
                _ => Fail($"unknown command '{command}'"),
            };
        }
        catch (Corsac.Asm.AsmException e)
        {
            Console.Error.WriteLine(e.ToString());
            return 1;
        }
        catch (FileNotFoundException e)
        {
            return Fail(e.Message);
        }
        catch (InvalidDataException e) { return Fail(e.Message); }
        catch (ElfFormatException e) { return Fail(e.Message); }
        catch (IOException e) { return Fail(e.Message); }
        catch (ArgumentException e) { return Fail(e.Message); }
        catch (CompileError e) { Console.Error.WriteLine(e.ToString()); return 1; }
        catch (LinkException e)
        {
            foreach (string error in e.Errors)
            {
                Console.Error.WriteLine($"corc: {error}");
            }
            return 1;
        }
    }

    private static int LinkUsage()
    {
        Console.WriteLine("corc link <file.o> ... -o <output> [--entry symbol] [--flat] "
            + "[--base address] [--paddr address] [--shared] [--cpu name] [--no-lto]");
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            corc - the COR-C# toolchain

            usage:
              corc compile <file.cor> ... -o <output> [options]
              corc compile @sources.list -o <output> [options]
              corc link <file.o> ... -o <output> [--entry <symbol>]
              corc link @objects.list -o <output> [--entry <symbol>]
              corc index --assembly <identity> <sources...> -o <declarations.idx>
              corc project <file.csproj> [--configuration Release] [--framework net10.0] [--jobs N] [-o output]
              corc compile-project --units <units.tsv> --decl-index <idx> --assembly <identity>
              corc build [target/path] [Name=Value ...] [--file corsac.build] [--jobs N]
              corc asm --target x86-16 <file.asm> -o <output.bin>
              corc asm --target x86-32 <file.asm> --obj -o <output.o>

            options:
              --target <name>    x86 (default) or corsac
              --lib              build a library rather than a program
              --shared           build an ELF shared object (implies --lib and --pic)
              --ref <file.cor>   compile this source for its declarations only:
                                 its code is in a shared object being linked.
              --decl-index <file> demand-load non-generic declarations from an index
              --assembly <name>  assembly identity for indexed declaration lookup
              --dependency-file <path> record consumed declarations for indexed objects
              --main-type <name> select the class supplying the entry point
                                 May be repeated; how one library of several
                                 is built from sources they all have to see
              --dynamic          link the runtime and the class library as the
                                 shared objects in build/lib rather than
                                 compiling them in. DT_NEEDED names only the
                                 ones the program actually reaches
              --libdir <dir>     where --dynamic looks for them
              --runpath <text>   what DT_RUNPATH says, instead of where the
                                 libraries were found. `/lib` for a disc
              --pic              position-independent code: globals through the GOT
              --link-shared <so> link against a shared object; may be repeated
              --nostdlib         do not link the default libraries (std, runtime, system)
              --obj              write a relocatable object rather than an executable
              --no-lto           omit link-time optimization summaries
              --no-stackmaps     omit precise stack-map metadata (no precise-GC consumer)
              --dump-ir          print the IR after lowering
              --dump-opt         print the IR after optimisation
              --asm              print the generated assembly
              --stats            print whether a heap is needed and each function's code size
              --jobs <count>     compiler task workers (positive count; default 1)
              --no-opt           skip the optimiser
              --opt-size         use experimental size-oriented inlining budgets
              --experimental-batch enable the staged large-batch optimizer checkpoint
              --batch-without <pass> omit one experimental pass for regression isolation
              --experimental-ssa run verified SSA optimisations after the default pipeline
              --trace-opt <fn>   print that function after every optimiser pass
              -D <name>          define a conditional symbol, for `#if`; may be
                                 repeated, and several may be separated by commas
              -Wno-error         show warnings and go on; by default a warning fails the compile
              --entry <sym>      the entry symbol (default _start)
              --freestanding     no operating system: link lib/sys/baremetal.cor,
                                 no thread scheduler, and an entry stub that
                                 touches neither the entry stack nor a syscall
              --tls-gs           with --freestanding: the thread block is found
                                 at gs:[0], as on Linux, not in one global word.
                                 For a kernel with a block per processor; the
                                 program must load GS before managed code runs
              --base <addr>      load or link address, hexadecimal (0x10000)
              --load <addr>      where a loader puts the image, when that is
                                 not where it is linked to run: the ELF's
                                 p_paddr. --paddr is the same flag.
              --with <file.o>    link an assembled object into the program;
                                 may be repeated
              --asm-entry <sym>  an assembled object owns _start: name the
                                 COR-C# entry stub <sym>, and leave clearing
                                 .bss and setting the stack to that object
              --cpu <name>       ISA profile: 486 (default), pentium, pentium-mmx, k6, k6-2, k6-3, k6-3+
              --tune <name>      optimization preferences without enabling additional instructions
              --disable-mmx     exclude MMX and its dependent 3DNow! instruction families
              --disable-3dnow   exclude 3DNow! while retaining MMX
              --tag <text>       name and version the image carries in a
                                 .corsac.tag note, for the bootloader's menu
              --flat             write a flat binary rather than an ELF file:
                                 .text, .rodata and .data contiguous from --base,
                                 no headers, and the .bss size reported so the
                                 loader knows how much to zero. Needs --freestanding
            """);
        return 0;
    }

    private static int LibrarySources(string[] args)
    {
        if (args.Any(argument => argument != "--freestanding")) return Fail("library-sources accepts only --freestanding");
        foreach (string path in DefaultLibraries(Target.X86, args.Contains("--freestanding"))) Console.WriteLine(path);
        return 0;
    }

    private static int DependenciesCurrent(string[] args)
    {
        if (args.Length != 2) return Fail("dependencies-current needs a receipt and declaration index");
        return UnitDependencies.IsCurrent(args[0], args[1]) ? 0 : 1;
    }

    internal static int Fail(string message)
    {
        Console.Error.WriteLine($"corc: {message}");
        return 2;
    }

    /// <summary>An address on the command line: hexadecimal with 0x, else decimal.</summary>
    private static uint? Address(string text)
    {
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out uint hex) ? hex : null;
        }
        if (uint.TryParse(text, out uint dec))
        {
            return dec;
        }
        return uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out uint fallback) ? fallback : null;
    }

    internal static string? Value(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>Every occurrence of a flag that takes a value, in order.</summary>
    private static List<string> Values(string[] args, string name)
    {
        List<string> values = new();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                values.Add(args[i + 1]);
            }
        }
        return values;
    }

    /// <summary>
    /// `corc build --target x86-16 file.asm -o file.bin`: hand-written
    /// assembly to a flat binary. Separate from `compile` because there is no
    /// front end, no linker and no object file involved -- what comes out is
    /// the bytes, in order, which is the only thing a boot sector can be.
    /// </summary>
    private static int Build(string[] args)
    {
        List<string> files = new();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (args[i] is "-o" or "--target" or "--cpu" or "--tune" or "--fpu")
                {
                    i++;
                }
                continue;
            }
            files.Add(args[i]);
        }

        if (files.Count != 1)
        {
            return Fail("build takes one assembly source file");
        }

        string targetName = Value(args, "--target") ?? "x86-16";
        if (targetName is not ("x86-16" or "x86_16" or "x86-32"))
        {
            return Fail($"unknown build target '{targetName}'");
        }

        // --target names the assembler's default mode, which `.bits` in the
        // source still overrides: x86-16 for a boot sector, x86-32 for the
        // kernel's own assembly, which has no real mode left to speak of.
        int bits = targetName == "x86-32" ? 32 : 16;
        bool asObject = args.Contains("--obj");
        string output = Value(args, "-o") ?? Path.ChangeExtension(files[0], asObject ? ".o" : ".bin");
        Corsac.Asm.X86Assembler.Result result = Corsac.Asm.X86Assembler.AssembleFile(files[0], bits, asObject, X86Cpu.Parse(args));
        if (asObject)
        {
            File.WriteAllBytes(output, ElfWriter.WriteObject(result.Object!));
            return 0;
        }
        File.WriteAllBytes(output, result.Bytes);
        return 0;
    }

    /// <summary>
    /// Expands `@file` arguments: one argument per line, blank lines and
    /// lines starting with # skipped, paths taken relative to the file that
    /// names them. A kernel is a hundred sources and a command line is not.
    /// </summary>
    internal static string[] Response(string[] args)
    {
        if (!args.Any(a => a.StartsWith('@')))
        {
            return args;
        }
        List<string> out_ = new();
        foreach (string arg in args)
        {
            if (!arg.StartsWith('@'))
            {
                out_.Add(arg);
                continue;
            }
            string path = arg[1..];
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"response file '{path}' does not exist");
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }
                out_.Add(line.StartsWith('-') || Path.IsPathRooted(line) ? line : Path.Combine(dir, line));
            }
        }
        return out_.ToArray();
    }

    /// <summary>The project session in force, when sources are compiled together.</summary>
    internal static Corsac.Lang.Metadata.DeclarationSession? Session { get; set; }

    internal static int Compile(string[] argv)
    {
        string[] args = Response(argv);
        int workers = 1;
        if (args.Contains("--jobs"))
        {
            string? jobs = Value(args, "--jobs");
            if (jobs is null || !int.TryParse(jobs, out workers) || workers < 1)
                return Fail("--jobs requires a positive worker count");
        }
        List<string> files = new();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (args[i] is "-o" or "--target" or "--entry" or "--link-shared" or "--base" or "--tag"
                    or "--load" or "--paddr" or "--cpu" or "--tune" or "--fpu" or "--with" or "--asm-entry"
                    or "--ref" or "--libdir" or "--runpath" or "--trace-opt" or "--batch-without"
                    or "-D" or "--define" or "--jobs" or "--decl-index" or "--assembly" or "--dependency-file" or "--main-type"
                    or "--subsystem" or "--resources" or "--icon-resource")
                {
                    i++;
                }
                continue;
            }
            files.Add(args[i]);
        }

        if (files.Count == 0)
        {
            return Fail("compile needs at least one source file");
        }

        string targetName = Value(args, "--target") ?? "x86";
        Target? target = Target.ByName(targetName);
        if (target is null)
        {
            return Fail($"unknown target '{targetName}'");
        }
        Target.Current = target;

        X86Cpu profile = X86Cpu.Parse(args);
        if (profile.Fpu == "none") return Fail("Software floating-point lowering is not yet complete; --fpu=none native compilation is not available yet");
        if (profile.Name == "386") return Fail("The 386 backend instruction/runtime audit is not yet complete");
        target.Cpu = profile.Name;
        target.X86Profile = profile;

        // Bare metal: no operating system under the program, and therefore a
        // different platform library, no thread scheduler, and an entry stub
        // that touches nothing the loader did not give it.
        bool freestanding = args.Contains("--freestanding");
        bool flat = args.Contains("--flat");
        Corsac.Lang.Lower.Lowering.Freestanding = freestanding;
        Corsac.Lang.Lower.Lowering.TlsGs = freestanding && args.Contains("--tls-gs");

        // --asm-entry: an assembled object supplies `_start`, and this is the
        // name it calls once it has a stack and a cleared .bss.
        string? asmEntry = Value(args, "--asm-entry");
        Corsac.Lang.Lower.Lowering.EntryName = asmEntry ?? "_start";
        Corsac.Lang.Lower.Lowering.EntryClearsBss = asmEntry is null;
        Corsac.Lang.Lower.Lowering.StartupObject = Value(args, "--main-type");

        uint? loadBase = null;
        if (Value(args, "--base") is { } baseText)
        {
            if (Address(baseText) is not { } parsed)
            {
                return Fail($"--base '{baseText}' is not an address");
            }
            loadBase = parsed;
        }

        // --load (or --paddr) is where the loader PUTS the image; --base is
        // where it will RUN. They differ only for a kernel linked into the
        // higher half and loaded low, and the difference shows up as p_paddr.
        uint? physicalBase = null;
        string? physText = Value(args, "--load") ?? Value(args, "--paddr");
        if (physText is not null)
        {
            if (Address(physText) is not { } parsedPhys)
            {
                return Fail($"--load '{physText}' is not an address");
            }
            physicalBase = parsedPhys;
        }
        if (physicalBase is not null && flat)
        {
            return Fail("--load is for an ELF image; a flat image is entered at the address it was loaded to");
        }
        if (flat && !freestanding)
        {
            return Fail("--flat is for a freestanding image; add --freestanding");
        }

        bool shared = args.Contains("--shared");
        // A shared object has no entry point and exports its declarations,
        // which is what --lib already means to the front end.
        bool library = args.Contains("--lib") || shared;
        List<string> sharedLibs = Values(args, "--link-shared");
        string name = Path.GetFileName(files[0]);

        // --ref: sources given for their declarations, whose code is in a
        // shared object this one links. How one library of several is built
        // out of a set of sources they all have to see.
        List<string> references = Values(args, "--ref");
        foreach (string reference in references)
        {
            if (!File.Exists(reference))
            {
                return Fail($"--ref '{reference}' does not exist");
            }
            if (!files.Any(f => Path.GetFullPath(f) == Path.GetFullPath(reference)))
            {
                files.Add(reference);
            }
        }

        // --dynamic: the runtime and the class library are shared objects on
        // the disc rather than source compiled in. Every .so in the library
        // directory is offered; which ones the program actually needs is
        // decided from the symbols it turns out to reference, after it is
        // compiled, and only those get a DT_NEEDED.
        if (args.Contains("--dynamic"))
        {
            string? dir = Value(args, "--libdir") ?? Environment.GetEnvironmentVariable("CORC_SO") ?? SharedLibraryDirectory();
            if (dir is null || !Directory.Exists(dir))
            {
                return Fail($"--dynamic: no shared library directory{(dir is null ? "" : $" at {dir}")} (build one, or name it with --libdir)");
            }
            List<string> found = Directory.GetFiles(dir, "*.so").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (found.Count == 0)
            {
                return Fail($"--dynamic: {dir} holds no shared objects");
            }
            sharedLibs.AddRange(found.Where(f => !sharedLibs.Contains(f)));
        }

        // The default library set, so `corc compile hello.cor` is a complete
        // command and memory management is nobody's business but the
        // library's. Found relative to the compiler's own location, which is
        // where the repository keeps lib/.
        List<string> classLibrary = DefaultLibraries(target, freestanding);
        if (!args.Contains("--nostdlib") && classLibrary.Count > 0)
        {
            files.InsertRange(0, classLibrary.Where(d => !files.Any(f => Path.GetFullPath(f) == Path.GetFullPath(d))));
        }

        // THE CONDITIONAL SYMBOLS, which is what `#if DEBUG` asks about. C#
        // spells the flag -d:NAME and csc also accepts /define; the short form
        // written against every other compiler is -D NAME, and both are taken
        // here. A comma or a semicolon separates several in one flag, as it
        // does for csc.
        List<string> symbols = new();
        foreach (string given in Values(args, "-D").Concat(Values(args, "--define")))
        {
            symbols.AddRange(given.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        Frontend.WarningsAreErrors = !args.Contains("-Wno-error");

        // A shared object IS the class library, so everything in it counts as
        // library source: that is what lets a definition be given away to
        // another shared object that already has it, and a program's own type
        // shadowing a library one be kept.
        List<string> libraryMark = library ? new List<string>(files) : classLibrary;
        Corsac.Lang.Lower.Lowering.SharedObject = shared && !args.Contains("--no-shared-init");
        Corsac.Lang.Lower.Lowering.PartOfALibrary = references.Count > 0 || args.Contains("--decl-index") || args.Contains("--obj");
        Corsac.Lang.Lower.Lowering.Dynamic = !library && sharedLibs.Count > 0;
#if !NET
        // Native task workers serve parsing, optimization and code generation.
        // Initialize once before publishing work in any compilation phase.
        if (workers > 1) Scheduler.UseThreads(workers);
#endif
        string? declarationIndex = Value(args, "--decl-index");
        if (declarationIndex is not null && Value(args, "--assembly") is null)
            return Fail("--decl-index requires --assembly");
        if (Value(args, "--dependency-file") is not null && (declarationIndex is null || !args.Contains("--obj")))
            return Fail("--dependency-file requires indexed object compilation");
        using IndexedDeclarations? declarations = declarationIndex is null ? null
            : Session is not null ? new IndexedDeclarations(Session, files)
            : new IndexedDeclarations(declarationIndex, Value(args, "--assembly")!, files);
        (CompilationUnit unit, BindResult bound)? front =
            Frontend.Compile(files, name, library, libraryMark, symbols, references, workers, declarations);
        if (declarations is not null) Console.Error.WriteLine("indexed declaration payloads loaded=" + declarations.PayloadLoads
            + " passes=" + declarations.Passes + " token-cache hits=" + declarations.Tokens.Hits
            + " misses=" + declarations.Tokens.Misses + " bytes=" + declarations.Tokens.ResidentBytes
            // ALLOCATED BYTES ARE THE STOPWATCH HERE. A machine with other
            // builds on it cannot be timed: wall clock moves with whatever
            // else is running. What the compiler allocates does not, so a
            // change that removes repeated work shows up as a smaller number
            // whoever else is using the processors.
            + " allocated=" + GC.GetTotalAllocatedBytes());
        if (front is null)
        {
            return 1;
        }

        // WHERE A UNIT'S TIME AND ALLOCATION GO, by phase, when asked. The
        // frontend's own line covers what happened before this point.
        bool phases = Environment.GetEnvironmentVariable("CORC_REPORT_PHASES") is not null;
        System.Diagnostics.Stopwatch phaseClock = System.Diagnostics.Stopwatch.StartNew();
        long phaseBytes = GC.GetAllocatedBytesForCurrentThread();
        void Phase(string what)
        {
            if (!phases) return;
            long now = GC.GetAllocatedBytesForCurrentThread();
            Console.Error.WriteLine("phase " + what + " " + phaseClock.ElapsedMilliseconds + "ms " + ((now - phaseBytes) >> 20) + "MiB");
            phaseClock.Restart(); phaseBytes = now;
        }
        List<CompileError> errors = new();
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("lower");
#endif
        Module module = Lowering.Lower(front.Value.bound, front.Value.unit, name, library, errors, entries);
        Phase("lower");

        if (errors.Count > 0)
        {
            foreach (CompileError e in errors)
            {
                Console.Error.WriteLine(e.ToString());
            }
            return 1;
        }

        Dictionary<string, byte[]> definitionSemantics = DefinitionSemantics.Capture(module);
        if (args.Contains("--dump-ir"))
        {
            Console.Write(module.Dump());
        }

        if (!args.Contains("--no-opt"))
        {
#if COR_SELFHOST_BENCHMARK
            Program.BenchmarkStage("optimise");
#endif
            Optimise(module, Value(args, "--trace-opt"), args.Contains("--experimental-ssa"), args.Contains("--opt-size"), args.Contains("--experimental-batch"), Value(args, "--batch-without"), workers);
            Phase("optimise");
            if (args.Contains("--dump-opt"))
            {
                Console.Write(module.Dump());
            }
        }

        // Async bodies become state machines once their registers are final;
        // no backend knows what a suspension marker is.
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("async-homes-imports");
#endif
        Corsac.Lang.Opt.AsyncTransform.Run(module, target.WordSize);
        Corsac.Lang.Opt.LandingPadHomes.Run(module);
        if (args.Contains("--dump-async"))
        {
            Console.Write(module.Dump());
        }

        // WHAT THE SHARED OBJECTS ALREADY HAVE. A definition this module
        // makes that one of them exports is dropped: that is the whole point
        // of linking them, and it is also what makes a type's identity one
        // address rather than two. What is left over -- the program's own
        // code, and a generic instantiation no library was ever asked for --
        // is compiled here as before.
        HashSet<string> imported = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> exports = new(StringComparer.Ordinal);
        if (sharedLibs.Count > 0)
        {
            HashSet<string> provided = new(StringComparer.Ordinal);
            foreach (string lib in sharedLibs)
            {
                if (!File.Exists(lib))
                {
                    return Fail($"--link-shared '{lib}' does not exist");
                }
                HashSet<string> mine;
                try
                {
                    mine = new HashSet<string>(ElfReader.ExportsOf(File.ReadAllBytes(lib)), StringComparer.Ordinal);
                }
                catch (ElfFormatException e)
                {
                    return Fail($"--link-shared '{lib}': {e.Message}");
                }
                exports[lib] = mine;
                provided.UnionWith(mine);
            }
            module.Provided(provided);
            HashSet<string> defined = new(
                module.Functions.Select(f => f.Name).Concat(module.Data.Select(d => d.Name)),
                StringComparer.Ordinal);
            foreach (string symbol in provided)
            {
                if (!defined.Contains(symbol))
                {
                    imported.Add(symbol);
                }
            }
        }

        X86Backend x86Backend = new()
        {
            AutomaticPacked = !freestanding,
            // A relocatable dynamic unit must use standard ELF GOT relocations.
            // Absolute GOT-slot addresses exist only inside a final executable
            // link and cannot be serialized as relocatable object relocations.
            PositionIndependent = shared || args.Contains("--pic") || (sharedLibs.Count > 0 && args.Contains("--obj")),
            Workers = workers,
            EmitLinkSummary = !args.Contains("--no-lto") && !args.Contains("--no-opt"),
            StackMaps = !args.Contains("--no-stackmaps"),
        };
        foreach (string symbol in imported)
        {
            x86Backend.Imported.Add(symbol);
        }
        IBackend backend = target.Name switch
        {
            "x86" => x86Backend,
            _ => throw new NotSupportedException($"no backend for target '{target.Name}'"),
        };

        if (args.Contains("--asm"))
        {
            Console.Write(backend.Assembly(module));
        }

        List<string> backendErrors = new();
        List<CompileError> registryErrors = new();
        // Flat stage-two objects must already place their managed entry first;
        // the separate linker does not regenerate code or guess its prologue.
        if (flat && module.Entry is { } flatEntry)
        {
            Function? first = module.Functions.Find(function => function.Name == flatEntry);
            if (first is null) return Fail("flat entry is not a function: " + flatEntry);
            module.Functions.Remove(first); module.Functions.Insert(0, first);
        }
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("code-generation");
#endif
        ObjectFile obj = backend.Generate(module, backendErrors);
        Phase("codegen");
        Corsac.Lang.Opt.Pipeline.ReportAccounts();
        new TargetContract(freestanding ? (Lowering.TlsGs ? 2u : 1u) : 0u, requiresManagedLayouts: true, requiresCodeGenerationContract: true).Attach(obj);
        ManagedLayouts.Attach(obj, front.Value.bound);

        // WHAT THIS PROGRAM'S SETTINGS ARE, for the kernel to read out of the
        // file rather than out of the running process -- which is why the
        // section is a note and is not loaded. docs/software/REGISTRY.md in
        // the OS repository describes both the declarations and the bytes.
        foreach (RegistrySchema schema in RegistryDeclarations.Collect(front.Value.unit, registryErrors))
        {
            Section declared = new(RegistrySchema.SectionName, SectionKind.Note);

            declared.Bytes.AddRange(schema.Encode());
            obj.Sections.Add(declared);
        }

        DefinitionSemantics.Attach(obj, definitionSemantics);
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("link-output");
#endif

        // `--tag TEXT` names the image from inside: a note section the
        // bootloader reads to show what a kernel is and which version,
        // without running it. Not loaded, not part of the program.
        string? tag = Value(args, "--tag");
        if (tag is not null)
        {
            Section note = new(".corsac.tag", SectionKind.Note);
            note.Bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(tag));
            note.Bytes.Add(0);
            obj.Sections.Add(note);
        }

        if (args.Contains("--stats"))
        {
            Console.Error.WriteLine($"heap: {(module.NeedsHeap ? "needed (collector linked)" : "not needed (no collector)")}");
            if (backend is X86Backend x86)
            {
                Console.Error.Write(x86.Statistics());
            }
        }
        if (registryErrors.Count > 0)
        {
            foreach (CompileError e in registryErrors)
            {
                Console.Error.WriteLine(e.ToString());
            }
            return 1;
        }

        if (backendErrors.Count > 0)
        {
            foreach (string e in backendErrors)
            {
                Console.Error.WriteLine($"corc: {e}");
            }
            return 1;
        }

        string? output = Value(args, "-o");
        if (output is null)
        {
            return 0;
        }

        if (shared && !args.Contains("--obj"))
        {
            // DT_SONAME is the name the loader will look for, so it is the
            // name the file is being given here and not the path it sits at.
            byte[] so = Linker.LinkShared(new[] { (name, obj) }, Path.GetFileName(output), Needed(obj, sharedLibs, exports), Value(args, "--runpath"));
            File.WriteAllBytes(output, so);
            Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code, {so.Length} bytes shared object");
            return 0;
        }

        if (args.Contains("--obj") || library)
        {
            if (x86Backend.EmitLinkSummary && !x86Backend.PositionIndependent && sharedLibs.Count == 0)
                IrUnitCodec.Attach(obj, module, x86Backend.StackMaps);
            File.WriteAllBytes(output, ElfWriter.WriteObject(obj));
            if (Value(args, "--dependency-file") is string dependencyFile) declarations!.WriteDependencies(dependencyFile);
            Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code");
            return 0;
        }

        string entry = Value(args, "--entry") ?? module.Entry ?? "_start";
        List<(string, ObjectFile)> link = new() { (name, obj) };

        // --with: objects somebody else made. Assembly, in practice -- the
        // kernel's entry stub and its interrupt vectors, which no language
        // with functions in it can spell.
        foreach (string with in Values(args, "--with"))
        {
            if (!File.Exists(with))
            {
                return Fail($"--with '{with}' does not exist");
            }
            try
            {
                link.Add((Path.GetFileName(with), ElfReader.ReadObject(File.ReadAllBytes(with))));
            }
            catch (ElfFormatException e)
            {
                return Fail($"--with '{with}': {e.Message}");
            }
        }

        if (flat)
        {
            // The entry was ordered before initial emission, exactly as for
            // --flat --obj. Keep the original metadata and --with objects.
            TargetContract.Validate(link);
            Corsac.Lang.Lto.LinkTimeOptimizer.Run(link, !args.Contains("--no-lto") && !args.Contains("--no-opt"));
            Linker.FlatImage image = Linker.LinkFlat(link, entry, loadBase ?? 0x10000);
            File.WriteAllBytes(output, image.Bytes);
            Console.Error.WriteLine(
                $"{output}: flat image at 0x{image.Base:x}, {image.Bytes.Length} bytes "
                + $"(text {image.TextSize}, rodata {image.ReadOnlySize}, data {image.DataSize}), "
                + $"bss {image.BssSize} bytes to zero at 0x{image.Base + (uint)image.Bytes.Length:x}, "
                + $"{image.MemorySize} bytes of memory");
            return 0;
        }

        if (sharedLibs.Count == 0)
        {
            TargetContract.Validate(link);
            Corsac.Lang.Lto.LinkTimeOptimizer.Run(link, !args.Contains("--no-lto") && !args.Contains("--no-opt"));
        }
        // What a CORSAC program carries beyond its code: --subsystem
        // console|gui|service, --resources <segment file>, --icon-resource <id>.
        // See docs/software/GUI-EXECUTABLE.md in the OS repository.
        Linker.ProgramInfo? program = null;
        if (Value(args, "--subsystem") is not null || Value(args, "--resources") is not null || Value(args, "--icon-resource") is not null)
        {
            program = new Linker.ProgramInfo();
            string subsystemName = Value(args, "--subsystem") ?? "console";
            program.Subsystem = subsystemName switch
            {
                "console" => Linker.ProgramInfo.Console,
                "gui" => Linker.ProgramInfo.Graphical,
                "service" => Linker.ProgramInfo.Service,
                _ => uint.MaxValue
            };
            if (program.Subsystem == uint.MaxValue) return Fail($"unknown subsystem '{subsystemName}' (console, gui or service)");
            if (Value(args, "--resources") is { } resourcesPath)
            {
                if (!File.Exists(resourcesPath)) return Fail($"resources file not found: {resourcesPath}");
                program.Resources = File.ReadAllBytes(resourcesPath);
            }
            if (Value(args, "--icon-resource") is { } iconText)
            {
                if (!uint.TryParse(iconText, out uint icon)) return Fail($"invalid icon resource id '{iconText}'");
                program.IconResource = icon;
            }
        }
        byte[] exe = sharedLibs.Count > 0
            ? Linker.Link(new[] { (name, obj) }, entry, Needed(obj, sharedLibs, exports), Value(args, "--runpath"), program: program)
            : Linker.Link(link, entry, loadBase ?? Linker.DefaultLoadAddress, physicalBase, program);
        File.WriteAllBytes(output, exe);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(output, File.GetUnixFileMode(output) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code, {exe.Length} bytes");
        return 0;
    }

    /// <summary>
    /// AS NEEDED: of the shared objects offered, the ones that actually
    /// supply a name this object does not define. A program that prints a
    /// string names the runtime and nothing else, and its DT_NEEDED says so
    /// -- so the loader opens two files and not twenty, and a library nobody
    /// uses costs nobody anything.
    ///
    /// The order they were given in is kept, which for the build's own
    /// directory listing is the bottom of the stack first.
    /// </summary>
    private static List<string> Needed(ObjectFile obj, List<string> offered, Dictionary<string, HashSet<string>> exports)
    {
        if (offered.Count == 0)
        {
            return offered;
        }
        HashSet<string> undefined = new(
            obj.Symbols.Where(s => !s.IsDefined).Select(s => s.Name), StringComparer.Ordinal);
        return offered.Where(lib => exports.TryGetValue(lib, out HashSet<string>? e) && e.Overlaps(undefined)).ToList();
    }

    /// <summary>
    /// Where the shared objects are: build/lib beside the sources, found the
    /// same way the class library's sources are.
    /// </summary>
    private static string? SharedLibraryDirectory()
    {
        string? root = LibraryRoot();
        return root is null ? null : Path.Combine(root, "build", "lib");
    }

    /// <summary>
    /// The libraries every program links unless told otherwise: the standard
    /// library, the runtime, and the target's system library, in that order.
    /// Located from CORC_LIB if set to the repository root, else from the
    /// repository the compiler
    /// was built in.
    /// </summary>
    /// <summary>
    /// Whether a source file is one of the compiler's own libraries (stdlib,
    /// runtime): the set whose interface and virtual slot numbering is an
    /// ABI shared by every image. Asked by the index builder and the binder.
    /// </summary>
    /// <summary>
    /// Answered once per path. The binder asks this of every type it lays
    /// out, and answering it walked up the directory tree looking for the
    /// library root, with a File.Exists at each level: compiling the kernel
    /// made 221,000 lstat calls, 183,000 of which found nothing, which is
    /// what looking for a file that is not there costs. Neither the root nor
    /// a path's answer changes while the compiler runs.
    /// </summary>
    private static readonly Dictionary<string, bool> librarySources = new(StringComparer.Ordinal);
    private static readonly object librarySourceGate = new();

    internal static bool IsLibrarySource(string path)
    {
        lock (librarySourceGate)
        {
            if (librarySources.TryGetValue(path, out bool known)) return known;
        }
        bool answer = false;
        string? root = LibraryRoot();
        if (root is not null)
        {
            string full = Path.GetFullPath(path);
            foreach (string part in new[] { "stdlib", "runtime" })
            {
                string prefix = Path.GetFullPath(Path.Combine(root, part)) + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.Ordinal)) { answer = true; break; }
            }
        }
        lock (librarySourceGate) librarySources[path] = answer;
        return answer;
    }

    private static string? libraryRoot;
    private static bool libraryRootKnown;
    private static readonly object libraryRootGate = new();

    /// <summary>Where the compiler's own library sources live, found once.</summary>
    private static string? LibraryRoot()
    {
        lock (libraryRootGate)
        {
            if (libraryRootKnown) return libraryRoot;
            libraryRoot = FindLibraryRoot();
            libraryRootKnown = true;
            return libraryRoot;
        }
    }

    private static string? FindLibraryRoot()
    {
        string? root = Environment.GetEnvironmentVariable("CORC_LIB");
        if (root is null)
        {
            // bin/<config>/net10.0/corc.dll -> compiler/ -> repository root
            string here = AppContext.BaseDirectory;
            for (DirectoryInfo? d = new(here); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "stdlib", "src", "System", "Core.cor")))
                {
                    root = d.FullName;
                    break;
                }
            }
        }
        return root;
    }

    public static List<string> DefaultLibraries(Target target, bool freestanding = false)
    {
        string? root = LibraryRoot();
        if (root is null)
        {
            return new List<string>();
        }

        List<string> libs = new()
        {
            Path.Combine(root, "stdlib", "src", "System", "Core.cor"),
            Path.Combine(root, "stdlib", "src", "System", "Runtime", "ExceptionServices", "ExceptionDispatchInfo.cor"),
            Path.Combine(root, "runtime", "src", "core", "runtime.cor"),
            Path.Combine(root, "runtime", "src", "core", "gc.cor"),
        };
        // The scheduler makes no system call of its own: it asks the
        // platform for the time, a sleep, a wait and a wake, so the same
        // tasks and awaits run in a Linux program, in the bootloader and in
        // the kernel. What differs is the file answering those questions:
        // threading-linux.cor (clock_gettime, futexes, clone) beside the
        // Linux system library, or baremetal.cor's PIT and no threads.
        //
        // The collector stays in the set because whether it is NEEDED is a
        // question only escape analysis can answer, and it answers it after
        // lowering; an image that allocates nothing has every one of its
        // functions removed as unreachable and pays nothing for having had
        // the source compiled.
        libs.Add(Path.Combine(root, "runtime", "src", "core", "threading.cor"));
        if (!freestanding)
        {
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "linux", "threading.cor"));
        }
        string system = freestanding
            ? Path.Combine(root, "runtime", "src", "arch", "x86", "baremetal.cor")
            : Path.Combine(root, "runtime", "src", "platforms", "linux", "system.cor");
        if (File.Exists(system))
        {
            libs.Add(system);
        }
        if (!freestanding)
        {
            // The .NET class library over the system library: System.IO,
            // System.IO.Compression, System.Formats.Tar, System.Console,
            // System.Environment, System.Net(.Sockets),
            // System.Security.Cryptography, PosixSignalRegistration,
            // System.Diagnostics.Process.
            foreach (string dotnet in new[] {
                "System/interop.cor", "System/IO/io.cor", "System/Collections/Collections.cor",
                "System/IO/io-streams.cor", "System/IO/compression.cor", "System/IO/tar.cor",
                "System/time.cor", "System/values.cor", "System/numerics.cor",
                "System/Text/RegularExpressions.cor", "System/console.cor", "System/environment.cor",
                "System/Net/Net.cor", "System/Security/Cryptography/Cryptography.cor",
                "System/signals.cor", "System/unix.cor", "System/process.cor", "System/power.cor",
                "System/Drawing/Drawing.cor", "Corsac/GUI/Gui.cor", "System/Drawing/Imaging.cor",
                "System/Windows/Forms/Forms.cor" })
            {
                libs.Add(Path.Combine(root, "stdlib", "src", dotnet));
            }
        }
        return libs.Where(File.Exists).ToList();
    }

    /// <summary>
    /// The optimisation pipeline: one list, in order, between lowering and the
    /// backend. Heavier passes slot in here as they arrive.
    /// </summary>
    private static void Optimise(Module module, string? traced = null, bool experimentalSsa = false, bool optimizeSize = false, bool experimentalBatch = false, string? batchWithout = null, int workers = 1)
    {
        Corsac.Lang.Opt.Pipeline pipeline = Corsac.Lang.Opt.Pipeline.Default(optimizeSize: optimizeSize, experimentalBatch: experimentalBatch);
        pipeline.Workers = workers;
        if (batchWithout is not null)
        {
            if (!experimentalBatch || batchWithout is not ("bit-fact-simplify" or "edge-predicates"
                or "common-tail-merge" or "constant-returns" or "dead-returns" or "dead-arguments" or "store-back" or "load-reuse" or "frame-address-fold"))
                throw new ArgumentException("--batch-without requires --experimental-batch and an experimental pass name");
            pipeline.Passes.RemoveAll(pass => pass.Name == batchWithout);
            pipeline.ModulePasses.RemoveAll(pass => pass.Name == batchWithout);
        }

        // --trace-opt <function>: that function after every pass, on the error
        // stream. Two compilers that should agree and do not are told apart by
        // the first pass after which they differ.
        if (traced is not null)
        {
            foreach (var inliner in pipeline.ModulePasses.Concat(pipeline.LatePasses).OfType<Corsac.Lang.Opt.Inline>())
                inliner.TraceDecision = (caller, callee, decision) =>
                {
                    if (caller.Name == traced || callee.Name == traced)
                        Console.Error.WriteLine($"---- inline {caller.Name} -> {callee.Name}: {decision}");
                };
            pipeline.Trace = (pass, f, text) =>
            {
                if (f.Name == traced)
                {
                    Console.Error.WriteLine("---- after " + pass.Name);
                    Console.Error.Write(text);
                }
            };
        }

        pipeline.Run(module);
        if (experimentalSsa)
        {
            // Opt-in investigation only: keep the production pipeline unchanged
            // until memory effects, EH and target workloads have been validated.
            foreach (Function f in module.Functions)
            {
                Corsac.Lang.Opt.Verifier.Check(f, "before experimental-ssa");
                new Corsac.Lang.Opt.SsaOptimise { Verify = true }.Run(f);
                if (f.Name == traced)
                {
                    Console.Error.WriteLine("---- after experimental-ssa");
                    System.Text.StringBuilder text = new();
                    f.Dump(text);
                    Console.Error.Write(text);
                }
            }
        }
    }
}
