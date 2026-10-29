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

    /// <summary>The process-wide flags this corc was given, for every corc it starts (Run).</summary>
    public static List<string> ChildFlags => Switches.ChildFlags;

    public static int Run(string[] args)
    {
        Binder.LibrarySource = IsLibrarySource;
        string called = Path.GetFileName(InvokedAs());
        if (CommandForName(called) is string implied) args = [implied, ..args];
        if (args.Length == 0)
        {
            return Usage();
        }

        // FLAGS FOR THE WHOLE PROCESS, whatever the command, taken off before
        // the command reads its own: --gc-stats has the collector say its
        // counts at exit (a switch .NET's own collector ignores), and
        // --trace-escape NAME has the escape analysis say how it judged the
        // functions whose names contain NAME. Every child corc a command
        // starts is handed them again (ChildFlags). The diagnostic switches
        // are Switches'.
        // --tune IS GONE: code is made for the processor --cpu names. Said,
        // not skipped -- skipped, `--tune k6` made a source file of `k6`.
        if (args.TakeWhile(a => a != "--").Any(a => a == "--tune" || a.StartsWith("--tune=", StringComparison.Ordinal)))
            return Fail("--tune is gone: code is optimised for the processor --cpu names");
        // -m32 AND -m64, gcc's spelling, are --target x86 and --target x86-64
        // for every command; with a --target too, the two must agree.
        if (WidthFlags(args, out string? widthRefused) is not string[] widened) return Fail(widthRefused!);
        args = widened;
        List<string> taken;
        try { taken = Switches.Take(args); }
        // A RANGE OR A NULL REFUSED INSIDE THE COMPILER is a bug in it, not a
        // word to its user: said with where it came from. A link that stopped
        // on a negative length said only that, and nothing of where.
        catch (ArgumentException e) when (e is ArgumentOutOfRangeException or ArgumentNullException)
        {
            Console.Error.WriteLine(e.ToString());
            return Fail(e.Message);
        }
        catch (ArgumentException e) { return Fail(e.Message); }
        int traceAt = taken.IndexOf("--trace-escape");
        if (traceAt >= 0 && traceAt + 1 < taken.Count)
        {
            Corsac.Lang.Opt.Escape.PromoteTrace = taken[traceAt + 1];
            Switches.HandOn("--trace-escape", taken[traceAt + 1]);
            taken.RemoveRange(traceAt, 2);
        }
        int dumpAt = taken.IndexOf("--dump-layout");
        if (dumpAt >= 0 && dumpAt + 1 < taken.Count)
        {
            Corsac.Lang.ManagedLayouts.DumpLayout = taken[dumpAt + 1];
            Switches.HandOn("--dump-layout", taken[dumpAt + 1]);
            taken.RemoveRange(dumpAt, 2);
        }
        int regallocAt = taken.IndexOf("--trace-regalloc");
        if (regallocAt >= 0 && regallocAt + 1 < taken.Count)
        {
            Corsac.Lang.RegAlloc.Allocator.Trace = taken[regallocAt + 1];
            taken.RemoveRange(regallocAt, 2);
        }
        int lifetimesAt = taken.IndexOf("--trace-lifetimes");
        if (lifetimesAt >= 0 && lifetimesAt + 1 < taken.Count)
        {
            Corsac.Lang.Lto.LifetimeSolver.Trace = taken[lifetimesAt + 1];
            Switches.HandOn("--trace-lifetimes", taken[lifetimesAt + 1]);
            taken.RemoveRange(lifetimesAt, 2);
        }
        // The collector's workers in a child a project build started: its
        // share of the machine beside its siblings (ProjectCommand).
        if (Switches.GcWorkers >= 0) AppContext.SetData("Corsac.GC.Workers", Switches.GcWorkers);
        // What every whole collection found live, by type (Gc.AskCensus).
        if (taken.Remove("--gc-census"))
        {
            while (taken.Remove("--gc-census")) { }
            Switches.HandOn("--gc-census");
            AppContext.SetSwitch("Corsac.GC.Census", true);
        }
        // And what the collector found dead, by type (Gc.AskDeadCensus).
        if (taken.Remove("--gc-dead-census"))
        {
            while (taken.Remove("--gc-dead-census")) { }
            Switches.HandOn("--gc-dead-census");
            AppContext.SetSwitch("Corsac.GC.DeadCensus", true);
        }
        // And who holds what, by the pair of types (Gc.AskParentCensus).
        if (taken.Remove("--gc-parent-census"))
        {
            while (taken.Remove("--gc-parent-census")) { }
            Switches.HandOn("--gc-parent-census");
            AppContext.SetSwitch("Corsac.GC.ParentCensus", true);
        }
        // Where the allocations come from, sampled and said at exit (AllocSamples).
        if (taken.Remove("--alloc-sample"))
        {
            while (taken.Remove("--alloc-sample")) { }
            Switches.HandOn("--alloc-sample");
            AppContext.SetSwitch("Corsac.GC.AllocSample", true);
        }
        if (taken.Remove("--gc-stats"))
        {
            while (taken.Remove("--gc-stats")) { }
            Switches.HandOn("--gc-stats");
            AppContext.SetSwitch("Corsac.GC.Stats", true);
        }
        args = taken.ToArray();
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
                // EACH UNIT REGENERATED BY A BACKEND PROCESS OF ITS OWN, as many
                // as the machine has room for (IrLinkOptimizer): one backend in
                // this process took 275 of the compiler's 335-second link alone.
                "link" => ObjectLinkCommand.Run(Response(rest), declarationStamp: Corsac.Lang.Metadata.DeclarationStamp.Of,
                    declarationTable: Corsac.Lang.Metadata.DeclarationFingerprint.KernelTable),
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
        // A RANGE OR A NULL REFUSED INSIDE THE COMPILER is a bug in it, not a
        // word to its user: said with where it came from. A link that stopped
        // on a negative length said only that, and nothing of where.
        catch (ArgumentException e) when (e is ArgumentOutOfRangeException or ArgumentNullException)
        {
            Console.Error.WriteLine(e.ToString());
            return Fail(e.Message);
        }
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
            + "[--base address] [--paddr address] [--shared] [--cpu name] [--no-lto] [--region-engine escape|andersen] [--no-rta] [--region-report names] [--unused-report file] [--timings]");
        Console.WriteLine("""

              --exports <file>     a kernel as a library: write its globals to <file>, a
                                   .dynsym a module links against, and stamp the kernel and
                                   the file with the hash of --decl-index. The kernel is then
                                   not closed: modules call into it and derive from its classes
              --decl-index <idx>   with --exports: the declaration index the kernel's units
                                   were compiled against; its hash is the build stamp
              --kernel <exports>   link a module of that kernel: a shared object with no
                                   runtime of its own, importing from the kernel and from
                                   the modules named with --link-shared, with the kernel's
                                   stamp and the units' .corsac.modinfo
            """);
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
              corc index --assembly <identity> <sources...> -o <declarations.idx> [--ring n]
              corc project <file.csproj> [--configuration Release] [--framework net10.0] [--jobs N] [--link-only | --runtime-only] [--region-engine escape|andersen] [--no-rta] [--region-report NAMES] [--unused-report FILE] [--timings] [-o output]
              corc compile-project --units <units.tsv> --decl-index <idx> --assembly <identity> [--unit-census]
              corc build [target/path] [Name=Value ...] [--file corsac.build] [--jobs N]
              corc asm --target x86-16 <file.asm> -o <output.bin>
              corc asm --target x86-32 <file.asm> --obj -o <output.o>
              corc asm --target x86-64 <file.asm> --obj -o <output.o>

            options:
              --target <name>    x86 (default; also x86-32, i386) or x86-64 (also amd64)
              -m32, -m64         --target x86, --target x86-64
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
              --runtime-knob <name=value> a setting of the runtime's, baked into
                                 the program as ILC's --runtimeknob bakes one:
                                 System.GC.HeapHardLimit, System.GC.Gen0Size,
                                 Corsac.GC.Stats and the collector's others, and
                                 what AppContext answers. May be repeated
              --dynamic          link the runtime and the class library as the
                                 shared objects in build/lib rather than
                                 compiling them in. DT_NEEDED names only the
                                 ones the program actually reaches
              --libdir <dir>     where --dynamic looks for them
              --runpath <text>   what DT_RUNPATH says, instead of where the
                                 libraries were found. `/lib` for a disc
              --pic              position-independent code: globals through the GOT
              --link-shared <so> link against a shared object; may be repeated
              --kernel <exports> compile a kernel module: a freestanding shared object
                                 with no runtime of its own, against the exports
                                 `corc link --exports` wrote and the kernel's own
                                 --decl-index (whose stamp must be the kernel's).
                                 Implies --shared --freestanding --nostdlib
              --ring <n>         the ring being compiled, 0 to 3: a class marked
                                 [Ring0]..[Ring3] for another ring is left out
                                 before binding; an unmarked one is every ring's.
                                 corc index takes it too
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
              --count-spills     count the register allocator's spill-slot reads and
                                 writes as the program runs, said at its end (a Linux
                                 program, i386 or x86-64, compiled whole: not a shared object,
                                 nor units the link makes again; one thread's counts)
              --count-spills-map <file> --count-spills, and each counted block's runs said
                                 too, the blocks listed in <file>: number, function,
                                 block, reads, writes (tools/measure/spill-blocks.py)
              --region-report <names> say, for the boundaries whose names hold one of
                                 these (comma-separated), which allocations their return
                                 is proved to leave dead; corc link takes it too, for
                                 the regions it finds over every unit of a closed image
              --region-engine <escape|andersen> how the link finds those regions:
                                 escape (the default) solves each function and cycle
                                 from the bottom of the calls up; andersen solves the
                                 whole image at once, with object contexts
              --no-rta           the link's regions take a virtual call's overrides on
                                 every type, not only on those the closed image makes
              --unused-report <file> the program's dead code: every function of its own
                                 sources nothing reaches from the entry, and every static
                                 nothing reads, judged on what each definition names as
                                 written; to <file> ("-" for the error stream). On a unit
                                 compiled with --obj it leaves the notes the link reads;
                                 corc link, compile-project and corc project take it too
              --experimental-batch enable the staged large-batch optimizer checkpoint
              --batch-without <pass> omit one experimental pass for regression isolation
              --experimental-ssa run verified SSA optimisations after the default pipeline
              --trace-opt <fn>   print that function after every optimiser pass
              -D <name>          define a conditional symbol, for `#if`; may be
                                 repeated, and several may be separated by commas
              -Wno-error         show warnings and go on; by default a warning fails the compile
              --check            read and bind the program, report what is wrong, write nothing
              --entry <sym>      the entry symbol (default _start)
              --freestanding     no operating system: link runtime/src/arch/x86/baremetal.cor,
                                 no thread scheduler, and an entry stub that
                                 touches neither the entry stack nor a syscall
              --tls-gs           with --freestanding: the thread block is found
                                 at gs:[0], as on Linux, not in one global word.
                                 For a kernel with a block per processor; the
                                 program must load GS before managed code runs
              --kernel-syscalls  with --freestanding or --kernel: Sys.Syscall is a
                                 call of the kernel's KernelFiles.Syscall(number,
                                 a..f), missing arguments 0 -- ring 0's Linux file
                                 calls, served by the kernel without a trap
              --dll              a 32-bit Windows DLL (PE32): entered at DllMain and at
                                 each [UnmanagedCallersOnly(EntryPoint = "Name")]
                                 method, which it exports by that name; its
                                 [DllImport]s go through the import directory.
                                 Its runtime is the bare-metal one over
                                 kernel32; --base is 0x10000000 unless given
              --store-sequences  every reference store one sequence no thread is
                                 stopped inside, as a kernel's images are built
              --base <addr>      load or link address, hexadecimal (0x10000)
              --load <addr>      where a loader puts the image, when that is
                                 not where it is linked to run: the ELF's
                                 p_paddr. --paddr is the same flag.
              --with <file.o>    link an assembled object into the program;
                                 may be repeated
              --asm-entry <sym>  an assembled object owns _start: name the
                                 COR-C# entry stub <sym>, and leave clearing
                                 .bss and setting the stack to that object
              --cpu <name>       ISA profile: 486 (default), pentium, pentium-mmx,
                                 pentium-pro, pentium2, pentium3, pentium4, k6,
                                 k6-2, k6-3, k6-2+, k6-3+, athlon, athlon-xp,
                                 athlon64, 6x86mx, geode-lx
              --disable-mmx      exclude MMX and what needs it: 3DNow!, the
                                 MMX extensions and SSE
              --disable-3dnow    exclude 3DNow! while retaining MMX
              --disable-3dnow-ext exclude the 3DNow! extensions (PF2IW, PI2FW,
                                 PFNACC, PFPNACC, PSWAPD), retaining 3DNow!
              --disable-mmx-ext  exclude the MMX extensions (PAVGB, PSHUFW,
                                 PMOVMSKB, MOVNTQ, SFENCE...), and SSE with them
              --disable-cmov     exclude CMOV, and FCMOV/FCOMI with it
              --disable-sse      exclude SSE and SSE2
              --disable-sse2     exclude SSE2 while retaining SSE
                                 --cpu is a floor: a packed block keeps
                                 variants for newer processors, picked at
                                 run time. Every --disable-NAME is a ban:
                                 no such variant is kept either.
              --enable-<name>    the same seven extensions granted beyond the
                                 CPU's own: a promise the processor has them;
                                 a disable wins over an enable
              --packed-report    bare metal: every method that keeps the
                                 processor's x87, MMX and SSE state, and why
                                 (its float code, its packed operations, or
                                 a call in a loop to one that keeps it): the
                                 compiler's own saves, each said once
              --x87-in-blocks-only
                                 bare metal (the default there): the
                                 program's own float code only where the
                                 processor's state is kept -- which the
                                 compiler does around each method that
                                 needs it, and under its callers' for one
                                 they call -- never in a lambda or an async
                                 body. Judged where every caller is known:
                                 the compile of a program, else the link.
                                 Refused under an operating system (Linux,
                                 Windows), whose threads own their state
              --x87-outside-blocks
                                 bare metal: this unit's own float code
                                 allowed outside packed blocks (wins over
                                 the above). A call it makes outside a block
                                 is judged as any caller's: a function whose
                                 float code relies on its callers' blocks is
                                 refused if reached that way. Refused for
                                 hosted code
              --tag <text>       name and version the image carries in a
                                 .corsac.tag note, for the bootloader's menu
              --no-collector     link no collector: what cannot be proved freed or
                                 made once is never given back, each such
                                 allocation named in a note
              --flat             write a flat binary rather than an ELF file:
                                 .text, .rodata and .data contiguous from --base,
                                 no headers, and the .bss size reported so the
                                 loader knows how much to zero. Needs --freestanding
              --map <file>       with --flat: every symbol of the image, largest
                                 first, with its address and section
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
    private static ulong? Address(string text)
    {
        // 64 bits, for a long-mode kernel linked into the top of the address
        // space; an i386 image refuses one that does not fit when it is written.
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out ulong hex) ? hex : null;
        }
        if (ulong.TryParse(text, out ulong dec))
        {
            return dec;
        }
        return ulong.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out ulong fallback) ? fallback : null;
    }

    /// <summary>
    /// `args` with -m32 or -m64 made --target x86 or --target x86-64, before
    /// any `--` (what follows is the program's); null, and why, where two
    /// widths are asked for or a --target names the other one.
    /// </summary>
    private static string[]? WidthFlags(string[] args, out string? refused)
    {
        refused = null;
        int end = Array.IndexOf(args, "--");
        if (end < 0) end = args.Length;
        string? asked = null;
        List<string> kept = new();
        for (int i = 0; i < end; i++)
        {
            if (args[i] is "-m32" or "-m64")
            {
                if (asked is not null && asked != args[i]) { refused = "-m32 and -m64 both given"; return null; }
                asked = args[i];
                continue;
            }
            kept.Add(args[i]);
        }
        if (asked is null) return args;
        Target wanted = asked == "-m64" ? Target.X86_64 : Target.X86;
        if (Value(kept.ToArray(), "--target") is string given)
        {
            if (Target.ByName(given) != wanted) { refused = $"{asked} and --target {given} disagree"; return null; }
        }
        // A --target with nothing after it is the command's to refuse, as it
        // would be without the width: one more added here would be read as
        // its value.
        else if (!kept.Any(a => a == "--target" || a.StartsWith("--target=", StringComparison.Ordinal)))
        {
            kept.Add("--target");
            kept.Add(wanted == Target.X86_64 ? "x86-64" : "x86");
        }
        kept.AddRange(args[end..]);
        return kept.ToArray();
    }

    /// <summary>
    /// A flag's value, written `--name value` or `--name=value`: the second
    /// spelling was a word no command knew, skipped like any other, and
    /// `--target=x86-64` compiled for i386 without a word said.
    /// </summary>
    internal static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name) return i + 1 < args.Length ? args[i + 1] : null;
            if (args[i].Length > name.Length && args[i][name.Length] == '=' && args[i].StartsWith(name, StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>Every occurrence of a flag that takes a value, in order, in either spelling (Value).</summary>
    internal static List<string> Values(string[] args, string name)
    {
        List<string> values = new();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
            {
                values.Add(args[++i]);
            }
            else if (args[i].Length > name.Length && args[i][name.Length] == '=' && args[i].StartsWith(name, StringComparison.Ordinal))
            {
                values.Add(args[i][(name.Length + 1)..]);
            }
        }
        return values;
    }

    /// <summary>
    /// What is wrong with --cpu for the x86-64 target, or null: its one
    /// processor is the K8, which --cpu may name (as k8 or x86-64) or leave
    /// out. Both of X86Cpu.Parse's spellings are read, `--cpu NAME` and
    /// `--cpu=NAME`, so neither slips a 32-bit profile past.
    /// </summary>
    private static string? LongModeCpuRefusal(string[] args)
    {
        IEnumerable<string> cpuNames = Values(args, "--cpu");
        string? other = cpuNames.FirstOrDefault(name => name is not ("k8" or "x86-64"));
        return other is null ? null
            : $"--cpu {other}: the x86-64 target's processor is k8 (the first AMD64)";
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
                if (args[i] is "-o" or "--target" or "--cpu" or "--fpu")
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
        // The compiler's names for the other two (Target.ByName): x86 and
        // x86-32 alike, amd64 and x86-64 alike, here as there.
        Target? named = targetName is "x86-16" or "x86_16" ? null : Target.ByName(targetName);
        if (named is null && targetName is not ("x86-16" or "x86_16"))
        {
            return Fail($"unknown build target '{targetName}'");
        }

        // --target names the assembler's default mode, which `.bits` in the
        // source still overrides: x86-16 for a boot sector, x86-32 for the
        // kernel's own assembly, which has no real mode left to speak of.
        int bits = named == Target.X86 ? 32 : named == Target.X86_64 ? 64 : 16;
        // A long-mode assembly runs on a K8 or later, whatever mode each of
        // its parts is in, and the assembler holds it to no profile; --cpu
        // may name the K8, as for compile, and nothing else.
        X86Cpu? cpu = null;
        if (bits == 64)
        {
            if (LongModeCpuRefusal(args) is { } refusal)
            {
                return Fail(refusal);
            }
        }
        else
        {
            cpu = X86Cpu.Parse(args);
        }
        bool asObject = args.Contains("--obj");
        string output = Value(args, "-o") ?? Path.ChangeExtension(files[0], asObject ? ".o" : ".bin");
        Corsac.Asm.X86Assembler.Result result = Corsac.Asm.X86Assembler.AssembleFile(files[0], bits, asObject, cpu);
        if (asObject)
        {
            ElfWriter.WriteObjectFile(result.Object!, output);
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

    /// <summary>What a unit's front end leaves for the rest of its compile: the module and what is taken from the syntax and bindings before they go.</summary>
    private sealed record UnitFront(Module Module, Dictionary<string, string> Entries, Dictionary<string, byte[]> DefinitionSemantics,
        List<ManagedTypeLayout> Layouts, List<RegistrySchema> Schemas, List<string> ModuleInfo);

    /// <summary>--check: the program is read and bound, and its errors reported, and nothing more (FrontToModule).</summary>
    private static bool _checkOnly;

    /// <summary>What FrontToModule answers for a program --check found nothing wrong with: no module is made.</summary>
    private static readonly UnitFront Checked = new(null!, new(), new(), new(), new(), new());

    /// <summary>
    /// Parses, binds and lowers a unit, and takes from its syntax and
    /// bindings what is written into the object after code generation: the
    /// definitions' semantics, the managed layouts and the registry schemas.
    /// Null on an error, which is reported here. Never inlined, so that
    /// everything the front end makes is dead when it returns.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static UnitFront? FrontToModule(List<string> files, string name, bool library, List<string> libraryMark, List<string> symbols,
        List<string> references, int workers, IndexedDeclarations? declarations, Action<string> phase, List<string> classLibrary)
    {
        (CompilationUnit unit, BindResult bound)? front =
            Frontend.Compile(files, name, library, libraryMark, symbols, references, workers, declarations, classLibrary);
        // The names the front end shared are let go with it: what lowering
        // still holds keeps its own, and the table kept every one of them.
        Lang.Interned.Forget();
        phase("front");
        if (declarations is not null) Console.Error.WriteLine("indexed declaration payloads loaded=" + declarations.PayloadLoads
            + " passes=" + declarations.Passes + " token-cache hits=" + declarations.Tokens.Hits
            + " misses=" + declarations.Tokens.Misses + " bytes=" + declarations.Tokens.ResidentBytes
            // ALLOCATED BYTES ARE THE STOPWATCH HERE. A machine with other
            // builds on it cannot be timed: wall clock moves with whatever
            // else is running. What the compiler allocates does not, so a
            // change that removes repeated work shows up as a smaller number
            // whoever else is using the processors.
            + " allocated=" + GC.GetTotalAllocatedBytes()
            + " specialisations=" + Lang.Monomorphiser.Specialisations + " members=" + Lang.Monomorphiser.SpecialisedMembers
            + " deferred=" + Lang.Monomorphiser.DeferredSpecialisations + " with-bodies=" + Lang.Monomorphiser.DeferredWithBodies + " members-made-later=" + Lang.Monomorphiser.MembersMadeLater
            + " deferred-declared=" + Lang.TypeSymbol.DeferredDeclared + " members-declared-later=" + Lang.TypeSymbol.DeferredMembersDeclared
            + " probes-answered=" + Lang.TypeSymbol.ProbesAnswered
            + " imported-deferred=" + Lang.Monomorphiser.DeferredImports + " imported-members-made-later=" + Lang.Monomorphiser.ImportedMembersMadeLater);
        if (front is null)
        {
            return null;
        }
        if (_checkOnly)
        {
            return Checked;
        }

        List<CompileError> errors = new();
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("lower");
#endif
        // What the unit's driver classes say of the module (ModuleDeclarations),
        // read before lowering takes the declarations' place.
        List<string> moduleInfo = ModuleDeclarations.Collect(front.Value.unit, errors);
        Module module = Lowering.Lower(front.Value.bound, front.Value.unit, name, library, errors, entries);
        module.LibraryCodeIsShared = Corsac.Lang.Lower.Lowering.Dynamic;
        phase("lower");

        if (errors.Count > 0)
        {
            foreach (CompileError e in errors)
            {
                Console.Error.WriteLine(e.ToString());
            }
            return null;
        }
        // Every call lowering made, as its callee takes it (Verifier.CheckCalls):
        // a fault there is the compiler's own, refused before it is code.
        try { Corsac.Lang.Opt.Verifier.CheckCalls(module, "lowered"); }
        catch (Corsac.Lang.Opt.IrVerifyException fault)
        {
            Console.Error.WriteLine("corc: internal error, a call made not as its callee takes it:\n" + fault.Message);
            return null;
        }

        Dictionary<string, byte[]> definitionSemantics = DefinitionSemantics.Capture(module);

        // THE FRONT END IS LET GO HERE. Its syntax, symbols and bindings were
        // held to the end of the unit for two things written into the object
        // after code generation, and they were a unit's largest structure
        // alive through its largest phase. What those two need is taken now.
        List<ManagedTypeLayout> layouts = ManagedLayouts.Capture(front.Value.bound, library);
        // AND WHICH TYPES IT NAMED AND NEVER ASKED THE MEMBERS OF, now that
        // nothing more will (Dependence.Header).
        if (declarations is not null)
        {
            // By key: the binder's declaration is a copy of the one read.
            // Read by one symbol of a key, it is read; the names probed of
            // each that was not, all of them.
            Dictionary<string, HashSet<string>?> unread = new(StringComparer.Ordinal);
            foreach (TypeSymbol type in front.Value.bound.Types.Values)
            {
                if (type.Decl is null) continue;
                bool had = unread.TryGetValue(type.Key, out HashSet<string>? names);
                if (!type.MembersPending) { unread[type.Key] = null; continue; }
                if (had && names is null) continue;
                names ??= new(StringComparer.Ordinal);
                if (type.ProbedNames is { } probed) names.UnionWith(probed);
                unread[type.Key] = names;
            }
            declarations.MembersUnread(decl => unread.GetValueOrDefault(Binder.TypeKey(decl)));
        }
        List<RegistrySchema> schemas = new(front.Value.unit.RegistrySchemas);
        return new UnitFront(module, entries, definitionSemantics, layouts, schemas, moduleInfo);
    }

    internal static int Compile(string[] argv)
    {
        string[] args = Response(argv);
        // A KERNEL MODULE (--kernel FILE, the exports `corc link --exports`
        // wrote): a shared object with no operating system under it and no
        // library of its own -- the kernel's runtime, collector and class
        // library are the ones it runs on -- compiled against the declaration
        // index the kernel was, so the kernel's types are declared and
        // defined elsewhere, exactly as a program's are against a library's.
        string? kernelPath = Value(args, "--kernel");
        KernelExports? kernel = null;
        if (kernelPath is not null)
        {
            if (Value(args, "--decl-index") is null)
                return Fail("--kernel compiles a module against the kernel's declarations: name the index the kernel was compiled against with --decl-index");
            try { kernel = KernelExports.Read(kernelPath); }
            catch (ElfFormatException e) { return Fail(e.Message); }
            args = args.Concat(new[] { "--shared", "--freestanding", "--nostdlib" }.Where(flag => !args.Contains(flag))).ToArray();
        }
        // THE RING THIS COMPILE IS FOR (Parser.Ring): a class marked for
        // another is left out as it is read.
        int ring = -1;
        if (Value(args, "--ring") is string ringText && (!int.TryParse(ringText, out ring) || ring < 0 || ring > 3))
            return Fail("--ring is the ring being compiled: 0, 1, 2 or 3");
        Parser.Ring = ring;
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
                if (args[i] is "-o" or "--target" or "--entry" or "--link-shared" or "--base" or "--tag" or "--map" or "--why" or "--debug-keep-object"
                    or "--load" or "--paddr" or "--cpu" or "--fpu" or "--with" or "--asm-entry"
                    or "--ref" or "--libdir" or "--runpath" or "--trace-opt" or "--batch-without" or "--region-report" or "--count-spills-map" or "--unused-report"
                    or "-D" or "--define" or "--jobs" or "--decl-index" or "--assembly" or "--dependency-file" or "--main-type" or "--runtime-knob"
                    or "--syscall-abi" or "--subsystem" or "--resources" or "--icon-resource" or "--using" or "--kernel" or "--ring" or "--module-index" or "--inside-facts")
                {
                    i++;
                }
                continue;
            }
            files.Add(args[i]);
        }

        // The runtime DLL (--windows --shared) is the library set alone.
        if (files.Count == 0 && !(args.Contains("--shared") && (args.Contains("--windows") || args.Contains("--dll"))))
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

        // LONG MODE IS ONE PROCESSOR BASELINE: the K8, the first AMD64. Every
        // x86-64 processor since runs what it runs, and the 32-bit profiles'
        // choices -- an FPU or none, MMX, 3DNow! -- do not apply.
        bool longMode = target == Target.X86_64;
        if (longMode)
        {
            if (LongModeCpuRefusal(args) is { } refusal)
            {
                return Fail(refusal);
            }
            target.Cpu = "k8";
        }
        else
        {
            X86Cpu profile = X86Cpu.Parse(args);
            if (profile.Fpu == "none") return Fail("Software floating-point lowering is not yet complete; --fpu=none native compilation is not available yet");
            target.Cpu = profile.Name;
            target.X86Profile = profile;
        }

        // Bare metal: no operating system under the program, and therefore a
        // different platform library, no thread scheduler, and an entry stub
        // that touches nothing the loader did not give it.
        // THE SYSTEM CALL INTERFACE this program is made for: 32 is Linux's i386
        // (int 0x80, six 32-bit registers), 64 its x86-64 (the syscall
        // instruction, six 64-bit registers). Left out, the target's own.
        int syscallAbi = longMode ? 64 : 32;
        if (Value(args, "--syscall-abi") is string abiText)
        {
            if (abiText != "32" && abiText != "64") return Fail("--syscall-abi is 32 or 64");
            syscallAbi = int.Parse(abiText);
            // The one the target has. A 32-bit interface from long mode (int
            // 0x80 with the i386 registers, for a compat process) is not
            // lowered yet; nothing runs one.
            if (syscallAbi != (longMode ? 64 : 32))
                return Fail("--syscall-abi=" + abiText + " is not made for --target " + targetName + " (its own is " + (longMode ? 64 : 32) + ")");
        }
        Corsac.Lang.Lower.Lowering.SyscallAbi = syscallAbi;
        bool freestanding = args.Contains("--freestanding");
        bool flat = args.Contains("--flat");
        // WINDOWS (docs/WIN32-TARGET.md): a PE32 image whose calls into C go
        // through its import directory; --dll makes it a DLL, entered at
        // DllMain and at the methods it exports (Lowering.Windows).
        bool dll = args.Contains("--dll");
        bool windows = dll || args.Contains("--windows");
        if (windows && longMode) return Fail("--windows makes an i386 image: x86-64's PE32+ is not made yet");
        if (windows && (flat || kernelPath is not null || args.Contains("--obj")))
            return Fail("--windows links a PE image whole: not with --flat, --obj or --kernel");
        // --windows --shared: THE RUNTIME DLL, the runtime and its library
        // with every global exported, which --windows --dynamic images import.
        bool runtimeDll = windows && args.Contains("--shared");
        if (runtimeDll && args.Contains("--dynamic")) return Fail("--windows --shared makes the runtime DLL, which imports no runtime: not with --dynamic");
        dll |= runtimeDll;
        // Its runtime is the bare-metal one with kernel32 answering its hooks
        // (runtime/src/platforms/win32/win32.cor).
        freestanding |= windows;
        Corsac.Lang.Lower.Lowering.Windows = windows;
        Corsac.Lang.Lower.Lowering.Dll = dll;
        Corsac.Lang.Lower.Lowering.Freestanding = freestanding;
        Corsac.Lang.Lower.Lowering.TlsGs = freestanding && args.Contains("--tls-gs");
        // RING 0'S FILE CALLS, served by the kernel's own function
        // (Lowering.KernelSyscalls): the kernel and its modules.
        Corsac.Lang.Lower.Lowering.KernelSyscalls = (freestanding || Value(args, "--kernel") is not null) && args.Contains("--kernel-syscalls");
        if (args.Contains("--kernel-syscalls") && !Corsac.Lang.Lower.Lowering.KernelSyscalls)
            return Fail("--kernel-syscalls is for ring-0 code: a --freestanding kernel or a --kernel module");
        // ITS THREADS ARE STOPPED AT ANY INSTRUCTION: every reference store is
        // made one sequence no thread is stopped inside (Lowering.StoreSequences):
        // ring 0's kernel and its modules, whose processors answer their collector from a Kick's interrupt -- the
        // image build asks with --store-sequences. ANY COMPILE MAY ASK: the
        // core runtime has every routine the stubs call, and a program run on
        // Linux compiled so is the kernels' scheme tested across the whole
        // class library (1453-1456, which the flag did nothing for while it
        // was a freestanding compile's alone). The card mark before a store
        // stays only for a store that cannot be made one
        // (Lowering.CardMarkBefore), in the same images: one scheme an image.
        // A thread block is a processor's in a ring-0 image only.
        // LONG MODE HAS THEM TOO (X64Backend.EmitStoreSequences), in the
        // image that defines the runtime -- not in a position-independent
        // one, whose stubs would be another image's and are not made: there
        // the flag would do nothing, and a build that asked for them would
        // believe it had them. A kernel's module is such an image (--kernel
        // makes it --shared), so a long-mode kernel built with sequences has
        // no modules yet: how a module reaches its kernel's stubs is still
        // to be settled with the kernel.
        if (longMode && args.Contains("--store-sequences") && (args.Contains("--pic") || args.Contains("--shared")))
            return Fail("--store-sequences in long mode is for an image that is not position-independent: a kernel's image, not its modules (--kernel, --shared or --pic)");
        Corsac.Lang.Lower.Lowering.StoreSequences = args.Contains("--store-sequences");
        Corsac.Lang.Lower.Lowering.CardMarkBefore = Corsac.Lang.Lower.Lowering.StoreSequences;
        Corsac.Lang.Lower.Lowering.ThreadBlockPerProcessor = Corsac.Lang.Lower.Lowering.StoreSequences && freestanding;

        // --asm-entry: an assembled object supplies `_start`, and this is the
        // name it calls once it has a stack and a cleared .bss.
        string? asmEntry = Value(args, "--asm-entry");
        Corsac.Lang.Lower.Lowering.EntryName = asmEntry ?? "_start";
        Corsac.Lang.Lower.Lowering.EntryClearsBss = asmEntry is null;
        Corsac.Lang.Lower.Lowering.StartupObject = Value(args, "--main-type");
        // THE RUNTIME'S SETTINGS, BAKED INTO THE PROGRAM (Lowering.RuntimeKnobs):
        // --runtime-knob NAME=VALUE, once for each, as ILC's --runtimeknob.
        // The program's own and not a process flag (Switches): corc's
        // --gc-stats has corc say its collector's counts, and
        // --runtime-knob Corsac.GC.Stats=1 has the program it builds say its
        // own. The last of a name given twice is the one.
        List<string> knobs = new();
        foreach (string knob in Values(args, "--runtime-knob"))
        {
            int equals = knob.IndexOf('=');
            if (equals <= 0 || knob.Contains('\0')) return Fail($"--runtime-knob '{knob}': a knob is NAME=VALUE");
            knobs.RemoveAll(earlier => earlier.StartsWith(knob[..(equals + 1)], StringComparison.Ordinal));
            knobs.Add(knob);
        }
        Corsac.Lang.Lower.Lowering.RuntimeKnobs = knobs;
        // THE SYSTEM LIBRARY'S OWN SOURCES, whose types are .NET's own in
        // System.Private.CoreLib: not every library a compile is given.
        Corsac.Lang.Lower.Lowering.SystemSources = new HashSet<string>(
            DefaultLibraries(Target.X86, false).Concat(DefaultLibraries(Target.X86, true)).Select(Path.GetFullPath), StringComparer.Ordinal);
        Corsac.Lang.Lower.Lowering.LibraryPath = IsLibrarySource;
        // THE PROGRAM'S ASSEMBLY, as its types' full names qualify them: the
        // project's, which every unit of it is told, else the image's name.
        Corsac.Lang.Lower.Lowering.AssemblyName = Value(args, "--assembly")
            ?? (Value(args, "-o") is string named ? Path.GetFileName(named).Split('.')[0] : "program");

        ulong? loadBase = null;
        if (Value(args, "--base") is { } baseText)
        {
            if (Address(baseText) is not { } parsed)
            {
                return Fail($"--base '{baseText}' is not an address");
            }
            // An i386 image's addresses are 32 bits wide: one based past
            // them is refused here, before the compile, by the flag that
            // put it there (the link refuses an image that runs past them).
            if (!longMode && parsed > uint.MaxValue)
            {
                return Fail($"--base {baseText} is above four gigabytes; an i386 image cannot be there");
            }
            loadBase = parsed;
        }

        // --load (or --paddr) is where the loader PUTS the image; --base is
        // where it will RUN. They differ only for a kernel linked into the
        // higher half and loaded low, and the difference shows up as p_paddr.
        ulong? physicalBase = null;
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
        if (shared && Value(args, "--unused-report") is not null)
            return Fail("--unused-report judges a program; a shared object is library code");
        List<string> sharedLibs = Values(args, "--link-shared");
        string name = Path.GetFileName(files.Count > 0 ? files[0] : Value(args, "-o") ?? "runtime");

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
            string? dir = Value(args, "--libdir") ?? SharedLibraryDirectory();
            if (dir is null || !Directory.Exists(dir))
            {
                return Fail($"--dynamic: no shared library directory{(dir is null ? "" : $" at {dir}")} (build one, or name it with --libdir)");
            }
            List<string> found = Directory.GetFiles(dir, windows ? "*.dll" : "*.so").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (found.Count == 0)
            {
                return Fail($"--dynamic: {dir} holds no shared objects");
            }
            sharedLibs.AddRange(found.Where(f => !sharedLibs.Contains(f)));
        }

        // --count-spills counts into the runtime's counters, named directly --
        // absolutely on i386, RIP-relatively on x86-64 -- not through a GOT,
        // and Linux's exit says them: a compile it cannot count, or whose
        // counts nothing would say, is refused by name rather than built
        // without them.
        bool countSpills = args.Contains("--count-spills") || Value(args, "--count-spills-map") is not null;
        if (countSpills)
        {
            string? why = windows || freestanding ? "Windows or bare-metal code, whose exit says nothing of them"
                : shared || args.Contains("--pic") ? "a shared object or position-independent code (--shared, --pic, --kernel)"
                : library || sharedLibs.Count > 0 ? "a library, or a program over shared ones (--lib, --dynamic, --link-shared): the runtime and its counters must be compiled in"
                : null;
            if (why is not null) return Fail("--count-spills counts in a Linux program compiled whole, not in " + why);
            if (Value(args, "--count-spills-map") is not null && args.Contains("--asm"))
                return Fail("--count-spills-map lists the blocks of an object it makes, not of an assembly listing (--asm)");
        }

        // The default library set, so `corc compile hello.cor` is a complete
        // command and memory management is nobody's business but the
        // library's. Found relative to the compiler's own location, which is
        // where the repository keeps stdlib/ and runtime/.
        List<string> classLibrary = DefaultLibraries(target, freestanding, windows);
        if (!args.Contains("--nostdlib") && classLibrary.Count > 0)
        {
            List<string> added = classLibrary.Where(d => !files.Any(f => Path.GetFullPath(f) == Path.GetFullPath(d))).ToList();
            files.InsertRange(0, added);

            // A SHARED OBJECT OF ONE'S OWN SOURCES IS A LIBRARY ABOVE THE CLASS
            // LIBRARY: `corc compile --shared mine.cor` is `--shared mine.cor
            // --ref <the class library>`, as a library of the class library's
            // own is built (docs/X86-BACKEND.md, "What each image contains") --
            // its code left to the shared objects that have it. Owned, the
            // whole class library was rooted in the one object, every copy's
            // every method, and the compile stopped on a body first wanted
            // after binding ('List$byte') or ran out of memory. A class
            // library source named on the command line stays this one's own.
            // THE RUNTIME DLL is the class library, so its sources are its own
            // (as the freestanding set compiled whole for the kernel is).
            if (shared && !runtimeDll)
            {
                references.AddRange(added.Where(d => !references.Any(r => Path.GetFullPath(r) == Path.GetFullPath(d))));
            }
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
        symbols.AddRange(TargetSymbols(target));
        // A Windows image's platform answers Clock and Native itself (TARGET_WINDOWS).
        if (windows) symbols.Add("TARGET_WINDOWS");

        // THE PROJECT'S USINGS, in every file (Parser.ProjectUsings).
        Parser.ProjectUsings = Values(args, "--using").ToList();
        // SyscallWord, the width of a system call's registers (--syscall-abi).
        Parser.SyscallWordType = syscallAbi == 64 ? "ulong" : "uint";
        // EVERY BODY IN THE COMPILER'S OWN PACKED BLOCK, bare-metal i386
        // (Parser.PackedEverywhere): the processor's state kept where float
        // code and packed operations need it (Binder.PackedStateOf). Not
        // Windows', whose threads own theirs (Lowering.OwnsFpuState).
        Parser.PackedEverywhere = !Corsac.Lang.Lower.Lowering.OwnsFpuState && !longMode;
        // CALLS INTO ANOTHER IMAGE'S PACKED METHODS (Lowering.InsideElsewhere):
        // the $inside copies the kernel's exports and the modules this one is
        // built on (--link-shared) define, which a kept state's calls take.
        // ONLY WHERE THIS UNIT'S SAVE COVERS THEIR COPY: a copy may use the
        // XMM registers where its image's --cpu has SSE, and a kept state
        // here is saved by FXSAVE for a call to one only where this --cpu has
        // it (Selector.SettlePackedSaves). So the kernel's copies where this
        // --cpu has SSE, or the kernel's has none (its exports carry its
        // --cpu, KernelExports.Contract); a module's, whose image says nothing
        // of its --cpu, only where this has SSE.
        HashSet<string> insideElsewhere = new(StringComparer.Ordinal);
        if (Parser.PackedEverywhere)
        {
            bool covers = Target.Current.X86Profile.Has(X86Feature.Sse);
            bool kernelSse;
            try { kernelSse = kernel?.Contract is not { } theirs || theirs.Fpu == "sse2" || (theirs.Features & X86Feature.Sse) != 0; }
            catch (ElfFormatException e) { return Fail(e.Message); }
            if (kernel is not null && (covers || !kernelSse))
                insideElsewhere.UnionWith(kernel.Names.Where(n => n.EndsWith(Linker.InsideSuffix, StringComparison.Ordinal)));
            if (covers)
                foreach (string under in sharedLibs)
                    insideElsewhere.UnionWith(ElfReader.ExportsOf(File.ReadAllBytes(under)).Where(n => n.EndsWith(Linker.InsideSuffix, StringComparison.Ordinal)));
            // AND THE OTHER UNITS OF THIS IMAGE'S (--inside-facts, ProjectCompile's
            // InsideRounds): compiled apart, a unit knows its neighbours' copies
            // only as the project build says them. One --cpu for every unit of
            // an image, so whatever its saves are, they cover their copies.
            if (Value(args, "--inside-facts") is string facts)
                insideElsewhere.UnionWith(File.ReadAllLines(facts).Where(n => n.EndsWith(Linker.InsideSuffix, StringComparison.Ordinal)));
        }
        Corsac.Lang.Lower.Lowering.InsideElsewhere = insideElsewhere;
        Binder.KeepsStateElsewhere = insideElsewhere.Count == 0 ? null
            : m => Corsac.Lang.Lower.Lowering.InsideCandidate(m) && insideElsewhere.Contains(Corsac.Lang.Lower.Lowering.Label(m) + Linker.InsideSuffix);
        Corsac.Lang.Lower.Lowering.PackedReport = args.Contains("--packed-report");

        Frontend.WarningsAreErrors = !args.Contains("-Wno-error");
        // --check: the source read and bound, its errors and warnings
        // reported, and nothing written -- the question of whether a program
        // compiles, asked of a whole tree in the time its binding takes.
        bool checkOnly = _checkOnly = args.Contains("--check");

        // A shared object IS the class library, so everything in it counts as
        // library source: that is what lets a definition be given away to
        // another shared object that already has it, and a program's own type
        // shadowing a library one be kept.
        List<string> libraryMark = library ? new List<string>(files) : classLibrary;
        Corsac.Lang.Lower.Lowering.SharedObject = shared && !args.Contains("--no-shared-init");
        // The runtime DLL is lowered as the library's own objects are (--obj),
        // which is how the class library has always been compiled whole.
        Corsac.Lang.Lower.Lowering.PartOfALibrary = references.Count > 0 || args.Contains("--decl-index") || args.Contains("--obj") || runtimeDll;
        // Literals shared across the objects a link puts together, except in
        // position-independent code, where a global one is reached through the
        // GOT (Lowering.SharedLiterals). Not position-independent are a kernel
        // module's units with text its binder relocates (--relocated-text) and
        // an executable's objects (--dynamic --obj, X86Backend below): a
        // literal is reached by its address there, so every unit's copy of
        // "System.String" -- a hundred and twenty-five in the NT module, a
        // third of its read-only data duplicates -- is one at the link.
        Corsac.Lang.Lower.Lowering.SharedLiterals = (Corsac.Lang.Lower.Lowering.PartOfALibrary || library)
            && !shared && !args.Contains("--pic");
        // A unit linked with others keeps its interrupt facts for the link's
        // check of handlers across units (InterruptNotes); so does any
        // build naming code that may never wait, handler or none.
        Binder.CollectInterruptFacts = args.Contains("--obj") || library || Corsac.Switches.WaitFreeRoots is { Length: > 0 } || Corsac.Switches.IrqRoots is { Length: > 0 };
        Corsac.Lang.Lower.Lowering.Dynamic = !library && sharedLibs.Count > 0;
        // A CLOSED WORLD: everything that can call through a vtable is in this
        // compile, so a slot's method is compiled only once something reads
        // the slot (Lowering.Slots). Not for an object another link takes,
        // a library or shared object, a program over shared ones, or a
        // kernel whose modules derive from its classes; nor under --no-rta.
        Corsac.Lang.Lower.Lowering.Why = Value(args, "--why");
        Corsac.Lang.Lower.Lowering.ClosedWorld = !args.Contains("--obj") && !library && sharedLibs.Count == 0
            && Value(args, "--kernel") is null && Value(args, "--exports") is null && !args.Contains("--no-rta");
        // Native task workers serve parsing, optimization and code generation.
        // Initialize once before publishing work in any compilation phase:
        // .NET's call, the scheduler's workers in a native corc (an #if !NET
        // was never compiled -- corc defines NET as the SDK does).
        if (workers > 1) ThreadPool.SetMinThreads(workers, workers);
        string? declarationIndex = Value(args, "--decl-index");
        if (declarationIndex is not null && Value(args, "--assembly") is null)
            return Fail("--decl-index requires --assembly");
        if (Value(args, "--dependency-file") is not null && (declarationIndex is null || !args.Contains("--obj")))
            return Fail("--dependency-file requires indexed object compilation");
        // THE INDEX IS THE KERNEL'S, or the module would be built against
        // declarations the kernel does not have: its stamp is the one the
        // kernel's link wrote into the exports.
        if (kernel is not null && declarationIndex is not null)
        {
            byte[] indexStamp = Corsac.Lang.Metadata.DeclarationStamp.Of(declarationIndex);
            if (!indexStamp.AsSpan().SequenceEqual(kernel.Stamp))
                return Fail($"--decl-index {declarationIndex} is not the index {kernel.Name} was compiled against: its stamp is {KernelExports.Text(indexStamp)}, the kernel's {KernelExports.Text(kernel.Stamp)}");
        }
        // A MODULE OF SEVERAL UNITS (--module-index): each unit is compiled
        // against the module's own index, made on the kernel's (corc index
        // --on), which answers for the kernel's declarations from the
        // kernel's index and for the module's other units from its own. The
        // stamp is still the kernel index's: the module index must have been
        // made on the very index --decl-index names, the one checked above.
        string? moduleIndex = Value(args, "--module-index");
        if (moduleIndex is not null && kernel is null)
            return Fail("--module-index is a kernel module's own index, made on its kernel's: it goes with --kernel");
        using IndexedDeclarations? declarations = declarationIndex is null ? null
            : Session is not null ? new IndexedDeclarations(Session, files)
            : new IndexedDeclarations(moduleIndex ?? declarationIndex, Value(args, "--assembly")!, files);
        if (declarations is not null && kernel is not null)
        {
            declarations.ModuleOfKernel = true;
            // ON THE KERNEL'S INDEX AT THE BOTTOM: a module's index made on the
            // kernel's, or on another module's made on it (nt on gui).
            if (moduleIndex is not null && (declarations.BaseStamp is not { } under || !under.AsSpan().SequenceEqual(kernel.Stamp)))
                return Fail($"--module-index {moduleIndex} was not made on the index {kernel.Name} was compiled against (corc index --on {declarationIndex}, or on a module's index made on it)");
        }
        // WHERE A UNIT'S TIME AND ALLOCATION GO, by phase, when asked. The
        // frontend's own line covers what happened before this point.
        bool phases = Switches.ReportPhases;
        System.Diagnostics.Stopwatch phaseClock = System.Diagnostics.Stopwatch.StartNew();
        long phaseBytes = GC.GetAllocatedBytesForCurrentThread();
        long phaseFreed = AppContext.GetData("Corsac.GC.FreedBytes") as long? ?? 0, phaseCollected = AppContext.GetData("Corsac.GC.CollectedBytes") as long? ?? 0;
        void Phase(string what)
        {
            if (!phases) return;
            long now = GC.GetAllocatedBytesForCurrentThread();
            // And what is held at its end: the managed heap's live bytes after
            // a collection, and the process's resident set and its peak.
            long held = GC.GetTotalMemory(true) >> 20;
            using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
            // Native, with --gc-stats: what the phase gave back, by the program
            // and by the collector -- the collection just made took what died
            // in it. Nothing under .NET, whose AppContext has no such data.
            string given = "";
            if (AppContext.GetData("Corsac.GC.FreedBytes") is long freedNow && AppContext.GetData("Corsac.GC.CollectedBytes") is long collectedNow)
            {
                given = " freed " + ((freedNow - phaseFreed) >> 20) + "MiB collected " + ((collectedNow - phaseCollected) >> 20) + "MiB";
                phaseFreed = freedNow; phaseCollected = collectedNow;
            }
            Console.Error.WriteLine("phase " + what + " " + phaseClock.ElapsedMilliseconds + "ms " + ((now - phaseBytes) >> 20) + "MiB"
                + " live " + held + "MiB rss " + (self.WorkingSet64 >> 20) + "MiB peak " + (self.PeakWorkingSet64 >> 20) + "MiB" + given);
            phaseClock.Restart(); phaseBytes = now;
        }
        if (phases) Corsac.Lang.Opt.Pipeline.Stage = Phase;
        // THE FRONT END IN A CALL OF ITS OWN (FrontToModule): parsing,
        // binding and lowering make the unit's largest structure -- syntax,
        // symbols, bindings -- and all of it is dead once the module and the
        // few things captured from it come back. Done inline, no function
        // returned where it died, and nothing could give it back as one
        // (regions open and close at calls).
        UnitFront? made = FrontToModule(files, name, library, libraryMark, symbols, references, workers, declarations, Phase, classLibrary);
        if (made is null)
        {
            return 1;
        }
        if (checkOnly)
        {
            return 0;
        }
        Module module = made.Module;
        // --unused-report: what each definition names as written, taken now,
        // before a pass inlines a call away (UsesCapture), for the link's
        // report of the program's dead code (UnusedReport).
        List<Corsac.Lang.Lto.UsesNotes.Definition>? usesNotes = Value(args, "--unused-report") is not null ? UsesCapture.Of(module) : null;
        Dictionary<string, string> entries = made.Entries;
        Dictionary<string, byte[]> definitionSemantics = made.DefinitionSemantics;
        List<ManagedTypeLayout> layouts = made.Layouts;
        List<RegistrySchema> schemas = made.Schemas;
        if (args.Contains("--dump-ir"))
        {
            Console.Write(module.Dump());
        }

        // Whether this object will carry link-time hints (Escape's), decided
        // before optimising as it is when the object is written: an object,
        // position-dependent, with the link-time optimizer on, linked against
        // no shared library. Only such an object may call symbols only the
        // link defines (field sites).
        // AND A UNIT OF A KERNEL MODULE (--kernel --obj), position-independent
        // as a module is, whose units the module's link optimises together as
        // a kernel's units are (IrLinkOptimizer): the link defines its field
        // sites in the module itself, and regenerates it position-independent
        // again (IrUnitCodec.Settings.PositionIndependent, UnitBackend).
        List<Corsac.Lang.Lto.IrArchiveRecord>? linkRecords = null;
        // A PROGRAM COMPILED WHOLE keeps its IR for its own link to run its
        // types' initialisers (Lto.Preinit, PreinitMachine): its object
        // carries no archive, and code generation lets each body go.
        List<Corsac.Lang.Lto.IrArchiveRecord>? preinitRecords = null;
        // THE LINK'S HINTS AS BYTES, written when they are made: the pointer
        // constraints (RegionSummary) are an object for every site and call
        // of every function, made before the late passes and read by nothing
        // but their own Write when the object is put together -- through the
        // late passes and all of code generation, the unit's largest phases.
        // The bytes are what the object gets either way; held as bytes, they
        // are a fraction of the objects.
        byte[]? regionHintBytes = null, lifetimeHintBytes = null, heapRecordBytes = null;
        Dictionary<string, string[]>? archivedGone = null;
        // --with: objects somebody else made. Assembly, in practice -- the
        // kernel's entry stub and its interrupt vectors, which no language
        // with functions in it can spell. Read before optimising, for what
        // they name of the program's (Module.ObjectNames): a static only the
        // stub read was one nothing read to DeadStatics, which took out its
        // stores and its word, and the link stopped on an undefined symbol.
        List<(string, ObjectFile)> withObjects = new();
        foreach (string with in Values(args, "--with"))
        {
            if (!File.Exists(with))
            {
                return Fail($"--with '{with}' does not exist");
            }
            try
            {
                withObjects.Add((Path.GetFileName(with), ElfReader.ReadObjectFile(with, leaveInFile: false)));
            }
            catch (ElfFormatException e)
            {
                return Fail($"--with '{with}': {e.Message}");
            }
        }
        foreach ((string _, ObjectFile with) in withObjects)
            foreach (Symbol symbol in with.Symbols)
                if (!symbol.IsDefined) module.ObjectNames.Add(symbol.Name);
        // A CLOSED IMAGE: an executable linked here, with no shared object
        // beside it and nothing compiled apart that may call into it -- every
        // write to a static of the program is in this module or in those
        // objects (UnwrittenStatics).
        module.ClosedImage = !args.Contains("--obj") && !library && kernel is null && sharedLibs.Count == 0 && !module.PreserveExports;
        module.NoCollector = args.Contains("--no-collector");
        bool moduleUnit = kernel is not null && args.Contains("--obj");
        // A RING-1 MODULE WHOSE TEXT ITS BINDER RELOCATES (--relocated-text):
        // no position-independent code -- no GOT register, no call/pop to find
        // it, a static by its address and a call into the kernel a plain
        // rel32 -- and the link leaves R_386_RELATIVE, R_386_32 and R_386_PC32
        // in the text (Linker.LinkModule). 32-bit only.
        bool relocatedText = kernel is not null && args.Contains("--relocated-text");
        if (relocatedText && target == Target.X86_64) return Fail("--relocated-text is for a 32-bit module");
        // Whether the late passes may give objects to regions: not where there
        // is no operating system to keep an arena, nor under --no-regions; a
        // link making the unit again asks the same (IrUnitCodec.Settings.Regions).
        bool regions = !freestanding && !args.Contains("--no-regions");
        // Loops that may not end by themselves poll for the collector at
        // their back edges where threads run under an operating system and a
        // collector may run (BackEdgePolls); a kernel interrupts its
        // processors instead.
        module.SafepointPolls = !freestanding && !module.NoCollector;
        module.LeavesLinkHints = (args.Contains("--obj") || library) && !args.Contains("--no-lto") && !args.Contains("--no-opt")
            && sharedLibs.Count == 0 && (moduleUnit || !(shared || args.Contains("--pic")));
        if (!args.Contains("--no-opt"))
        {
#if COR_SELFHOST_BENCHMARK
            Program.BenchmarkStage("optimise");
#endif
            // THE IR THE LINK GETS is the module as the late passes find it,
            // with what they read of this compile, so a link can run them
            // again knowing the whole program (UnitBackend).
            // And what its functions do with pointers, from the same IR, for
            // the link to find regions over every unit (RegionSummary).
            Action<Module>? beforeLate = !module.LeavesLinkHints ? null : m =>
            {
                linkRecords = Corsac.Lang.Metadata.IrUnitCodec.Snapshot(m,
                    !args.Contains("--no-stackmaps"),
                    new(true, m.NoCollector, m.CallsCollector, m.LeavesLinkHints, args.Contains("--opt-size"), args.Contains("--experimental-batch"),
                        m.RuntimeHelpers.ToArray(), PositionIndependent: moduleUnit && !relocatedText, Regions: regions, Polls: m.SafepointPolls));
                // Not without an operating system: no arena there (baremetal.cor),
                // and a unit with no summary keeps the link from finding regions.
                // Nor under --no-regions: the link would place the unit's sites in regions all the same.
                regionHintBytes = !regions ? null : Corsac.Lang.Opt.RegionSummary.Of(m).Write();
                // And the archive's edges a program without a collector lacks (HeapRecords.Gone).
                archivedGone = Corsac.Lang.Opt.Escape.GoneOf(m);
                // Escape writes the heap records with them as it makes them (Module.HeapRecordsAsBytes).
                m.HeapGone = archivedGone;
                m.HeapRecordsAsBytes = true;
                if (Switches.ReportPhases)
                    Console.Error.WriteLine("phase snapshot: " + linkRecords.Count + " records, " + (linkRecords.Sum(r => (long)r.BodyLength) >> 10)
                        + " KiB of bodies held in " + (linkRecords.Sum(r => (long)r.Payload.Length) >> 10) + " KiB, region hints " + ((regionHintBytes?.Length ?? 0) >> 10) + " KiB");
            };
            // The backend puts each function's allocations in place as it
            // selects it (AllocatorFastPaths.AtCodegen), on both targets: not
            // the late passes, where they grew the whole module's IR at once.
            module.AllocatorsAtCodegen = true;
            if (beforeLate is null && module.Preinit is { Types.Count: > 0 })
                beforeLate = m => preinitRecords = Corsac.Lang.Metadata.IrUnitCodec.Snapshot(m, false, null);
            Optimise(module, Value(args, "--trace-opt"), args.Contains("--experimental-ssa"), args.Contains("--opt-size"), args.Contains("--experimental-batch"), Value(args, "--batch-without"), workers, beforeLate, Value(args, "--region-report"), regions: regions);
            Phase("optimise");
            // The lifetime hints are complete once the passes are (Escape and
            // its owned-unit pass wrote them): written now, as the region
            // hints were, and the hints themselves let go.
            if (module.LeavesLinkHints && module.LifetimeHints is { IsEmpty: false } hints) lifetimeHintBytes = hints.Write();
            module.LifetimeHints = null;
            if (module.LeavesLinkHints && module.HeapRecordBytes is { } heapBytes) heapRecordBytes = heapBytes;
            else if (module.LeavesLinkHints && module.HeapRecords is { } heap)
            {
                foreach ((string function, string[] gone) in archivedGone ?? new()) heap.Gone[function] = gone;
                heapRecordBytes = heap.Write();
            }
            module.HeapRecords = null;
            module.HeapRecordBytes = null;
            module.HeapGone = null;
            if (Switches.ReportPhases) Console.Error.WriteLine("phase lifetime hints " + ((lifetimeHintBytes?.Length ?? 0) >> 10) + " KiB");
            Phase("hints");
            // What the lifetime pass kept of each function between questions
            // (Escape.StampIndex): no pass after this one asks, and every
            // function held its index -- registers of a body that code
            // generation lets go -- to the end of the compile.
            foreach (Function function in module.Functions) function.AnalysisIndex = null;
            if (args.Contains("--dump-opt"))
            {
                Console.Write(module.Dump());
            }
        }
        // UNOPTIMISED, ITS STORES ARE SEQUENCES ALL THE SAME: where the image
        // stops its threads anywhere (Lowering.StoreSequences) the last pass's
        // conversion runs without the passes before it (CardMarks.FuseStores),
        // or --no-opt would leave every barrier's test and its store two
        // places a thread can be stopped between.
        else if (module.RuntimeHelpers.Contains(Corsac.Lang.Lto.RuntimeAbi.RefStore))
        {
            new Corsac.Lang.Opt.CardMarks().Run(module);
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
        // WHAT THIS UNIT TOOK FROM THE EXPORTS rather than compiled -- a
        // library function or static the kernel, or a module under it,
        // defines already -- AND WHAT IT COMPILED, nothing exporting it: both
        // in its receipt, so that a kernel that no longer exports one it took
        // rebuilds the unit, which then compiles its own, and one that now
        // exports one it kept rebuilds it to take the kernel's, one type
        // having one descriptor (UnitDependencies.Stale). No declaration says
        // what is exported.
        List<string> takenFromKernel = new(), keptFromLibrary = new();
        if (sharedLibs.Count > 0 || kernel is not null)
        {
            // A module's first library is the kernel, which it never needs by
            // name: the kernel is always there.
            HashSet<string> provided = new(kernel?.Names ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (string lib in sharedLibs)
            {
                if (!File.Exists(lib))
                {
                    return Fail($"--link-shared '{lib}' does not exist");
                }
                HashSet<string> mine;
                try
                {
                    byte[] image = File.ReadAllBytes(lib);
                    mine = new HashSet<string>(windows ? Corsac.Lang.Pe.PeReader.ExportsOf(image) : ElfReader.ExportsOf(image), StringComparer.Ordinal);
                }
                catch (Exception e) when (e is ElfFormatException or InvalidDataException)
                {
                    return Fail($"--link-shared '{lib}': {e.Message}");
                }
                exports[lib] = mine;
                provided.UnionWith(mine);
            }
            if (kernel is not null)
            {
                takenFromKernel.AddRange(module.Functions.Where(f => f.FromLibrary && f.Name != module.Entry && provided.Contains(f.Name)).Select(f => f.Name));
                takenFromKernel.AddRange(module.Data.Where(d => d.FromLibrary && provided.Contains(d.Name)).Select(d => d.Name));
            }
            module.Provided(provided);
            if (kernel is not null)
            {
                keptFromLibrary.AddRange(module.Functions.Where(f => f.FromLibrary && f.Name != module.Entry).Select(f => f.Name));
                keptFromLibrary.AddRange(module.Data.Where(d => d.FromLibrary).Select(d => d.Name));
            }
            // WHAT ONLY THE LIBRARY'S CODE CALLED goes with it. A library body
            // the optimiser worked on here -- a copy of one of its callees
            // specialised for a constant argument -- has a name the shared
            // object does not export, and outlived the caller it was made for:
            // a program linked against libcorsacrt carried a piece of Gc.
            // THE ALLOCATOR'S BODIES GO TOO, unnamed by any call until the
            // backend puts them in place (Module.AllocatorsAtCodegen): against
            // a shared runtime or the kernel, every `new` stays the call. Kept
            // here, a program carried its own copy of the collector's slow
            // path beside the library's, or the kernel's allocator inlined.
            if (module.Entry is not null)
            {
                Corsac.Lang.Opt.Inline.RemoveDeadFunctions(module, new HashSet<string>(StringComparer.Ordinal));
            }
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

        // FLOAT CODE ONLY INSIDE A PACKED BLOCK, bare metal: there the x87
        // state is the interrupted thread's, which only a block saves. On by
        // default there, --x87-outside-blocks takes it away and
        // --x87-in-blocks-only says it again; code under an operating system
        // -- Linux, Windows -- owns its state (Lowering.OwnsFpuState), and a
        // block there saves nothing to tell inside from outside by, so the
        // check is refused there by name.
        bool ownsFpu = Corsac.Lang.Lower.Lowering.OwnsFpuState;
        if (ownsFpu && (args.Contains("--x87-in-blocks-only") || args.Contains("--x87-outside-blocks")))
            return Fail((args.Contains("--x87-in-blocks-only") ? "--x87-in-blocks-only" : "--x87-outside-blocks")
                + " is for bare-metal code: code under an operating system owns its x87 state, and its float code may be anywhere");
        X86Backend x86Backend = new()
        {
            AutomaticPacked = ownsFpu,
            OptimizeSize = args.Contains("--opt-size"),
            CountSpills = countSpills,
            SpillMap = Value(args, "--count-spills-map"),
            X87InBlocksOnly = !ownsFpu && !args.Contains("--x87-outside-blocks"),
            // AN EXECUTABLE'S OBJECT IS NOT POSITION-INDEPENDENT, made apart
            // (--obj) or not: it is linked at an address of its own, so its
            // names are absolute and a library's are reached through its GOT
            // slots at their absolute addresses (Imports; R_386_GOT32X with no
            // base register in the object). PIC there paid a GOT pointer --
            // a thunk and a register -- in every function that named anything:
            // 35,618 thunks over a kernel's 113 programs. A shared object's
            // object is (--shared, or --pic for one linked into one later).
            // A Windows image moves by its .reloc, not by position-independent
            // code, the runtime DLL and the images over it alike.
            PositionIndependent = !windows && !relocatedText && (shared || args.Contains("--pic")),
            WindowsTls = windows,
            Workers = workers,
            EmitLinkSummary = !args.Contains("--no-lto") && !args.Contains("--no-opt"),
            // Only a collector reads stack maps: a program found to need none
            // (Escape) carries none.
            StackMaps = !args.Contains("--no-stackmaps") && module.NeedsHeap,
            // Unless the object's IR is taken from the module after Generate
            // (IrUnitCodec.Attach, below): with the link's records taken
            // before the late passes, or no IR in the object, nothing below
            // reads a function's body once Generate has placed it.
            ReleaseBodies = linkRecords is not null || !(args.Contains("--obj") || library),
            // A closed image that needs no heap has no collector to answer, and
            // one whose inliner let Runtime.Poll go has none either.
            SafepointPolls = module.SafepointPolls && (!module.ClosedImage
                || module.NeedsHeap && module.Functions.Any(f => f.Name == Corsac.Lang.Opt.BackEdgePolls.Poll)),
        };
        // Relocated text reaches the kernel's names where they are: the
        // binder fixes each reference, so none goes through a GOT slot.
        foreach (string symbol in relocatedText ? Enumerable.Empty<string>() : imported)
        {
            x86Backend.Imported.Add(symbol);
        }
        Corsac.Lang.X64.X64Backend x64Backend = new()
        {
            PositionIndependent = x86Backend.PositionIndependent,
            Workers = workers,
            EmitLinkSummary = x86Backend.EmitLinkSummary,
            SafepointPolls = x86Backend.SafepointPolls,
            CountSpills = countSpills,
            SpillMap = Value(args, "--count-spills-map"),
        };
        foreach (string symbol in imported)
        {
            x64Backend.Imported.Add(symbol);
        }
        IBackend backend = target.Name switch
        {
            "x86" => x86Backend,
            "x86-64" => x64Backend,
            _ => throw new NotSupportedException($"no backend for target '{target.Name}'"),
        };

        // THE ALLOCATION PUT IN PLACE AS EACH FUNCTION IS SELECTED, where the
        // late passes left it to the backend (Module.AllocatorsAtCodegen).
        if (module.AllocatorsAtCodegen)
        {
            Action<Function>? prepare = Corsac.Lang.Opt.AllocatorFastPaths.AtCodegen(module);
            if (backend == x86Backend) x86Backend.Prepare = prepare;
            else x64Backend.Prepare = prepare;
        }

        if (args.Contains("--asm"))
        {
            Console.Write(backend.Assembly(module));
        }

        List<string> backendErrors = new();
        // Flat stage-two objects must already place their managed entry first;
        // the separate linker does not regenerate code or guess its prologue.
        // NOT WHERE AN ASSEMBLED STARTUP OWNS THE ENTRY (--asm-entry): the
        // stub is what that startup calls, and the image begins with the
        // object that defines _start (below).
        if (flat && module.Entry is { } flatEntry && Value(args, "--asm-entry") is null)
        {
            Function? first = module.Functions.Find(function => function.Name == flatEntry);
            if (first is null) return Fail("flat entry is not a function: " + flatEntry);
            module.Functions.Remove(first); module.Functions.Insert(0, first);
        }
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("code-generation");
#endif
        // The calls the inliner had to keep for the link (Escape's pending
        // hints) are read by the IR the object carries: taken already
        // (linkRecords) or none at all whenever the bodies go as they are
        // placed. The set held one instruction of every such call, and so
        // that instruction past its function's release.
        if (x86Backend.ReleaseBodies && backend == x86Backend) module.KeepCalls.Clear();
        ObjectFile obj = backend.Generate(module, backendErrors);
        if (preinitRecords is not null) PreinitMachine.Remember(obj, preinitRecords);
        Phase("codegen");
        Corsac.Lang.Opt.Pipeline.ReportAccounts();
        new TargetContract(windows ? TargetContract.WindowsTlsSlot : freestanding ? (Lowering.TlsGs ? 2u : 1u) : 0u, requiresManagedLayouts: true, requiresCodeGenerationContract: true, longMode: longMode,
            storeSequences: module.RuntimeHelpers.Contains(Corsac.Lang.Lto.RuntimeAbi.RefStore)).Attach(obj);
        // The C libraries its [DllImport]s call, for the link to need.
        NativeLibraries.Attach(obj, module.NativeLibraries);
        // And what a DLL exports, for its link's export directory.
        NativeExports.Attach(obj, module.NativeExports);
        ManagedLayoutContract.Attach(obj, layouts);
        if (usesNotes is not null) Corsac.Lang.Lto.UsesNotes.Attach(obj, usesNotes);
        // What the link checks interrupt handlers' calls into other units by.
        if (module.InterruptFacts is { } interruptFacts) Corsac.Lang.Lto.InterruptNotes.Attach(obj, interruptFacts);
        if (module.Preinit is { } preinit) Corsac.Lang.Lto.Preinit.Attach(obj, preinit);

        // WHAT THIS PROGRAM'S SETTINGS ARE, for the kernel to read out of the
        // file rather than out of the running process -- which is why the
        // section is a note and is not loaded. docs/software/REGISTRY.md in
        // the OS repository describes both the declarations and the bytes.
        foreach (RegistrySchema schema in schemas)
        {
            Section declared = new(RegistrySchema.SectionName, SectionKind.Note);

            declared.Bytes.AddRange(schema.Encode());
            obj.Sections.Add(declared);
        }

        DefinitionSemantics.Attach(obj, definitionSemantics);

        // WHAT A MODULE SAYS ABOUT ITSELF (ModuleInfo): its aliases and
        // dependencies as its classes declare them, and the ring this image
        // of it is for; the link adds its name and stamp. And its
        // initialisers: a shared object's loader runs them, a program's entry
        // before Main (Linker.ProgramInitializers), as .NET runs a module's
        // before any of its code.
        List<string> moduleInfo = new(made.ModuleInfo);
        if (kernel is not null && ring >= 0) moduleInfo.Insert(0, "ring=" + ring);
        if (moduleInfo.Count > 0) obj.Sections.Add(ModuleInfo.Note(ModuleInfo.SectionName, moduleInfo));
        // What of the kernel it was compiled against, by name -- every key it
        // read and lookup it asked, as the kernel's index answers them -- for
        // the link to hold it to (Linker.LinkModule, DeclarationReads). Not
        // the whole index's stamp: a unit right for a kernel changed only
        // where it never looked is kept, built before.
        if (kernel is not null) obj.Sections.Add(KernelExports.ReadsNote(declarations!.KernelReads()));
        if (module.Initializers.Count > 0) obj.Sections.Add(ModuleInfo.Note(ModuleInfo.InitializerSection, module.Initializers));
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
            var lifetimes = Corsac.Lang.Opt.Escape.LastRun;
            Console.Error.WriteLine($"lifetimes: {lifetimes.Promoted} objects in frames, {lifetimes.Owned} freed by the compiler ({lifetimes.OwnedReturns} of them handed over by a fresh return), {lifetimes.Fresh} functions return fresh objects, {lifetimes.Fields} fields freed with their owner, {lifetimes.Variables} reassigned variables owned");
            if (backend is X86Backend x86)
            {
                Console.Error.Write(x86.Statistics());
            }
            else if (backend is Corsac.Lang.X64.X64Backend x64)
            {
                Console.Error.Write(x64.Statistics());
            }
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

        // A MODULE OR A SHARED OBJECT IS LINKED HERE TOO, and is held to
        // what an executable is: every interrupt handler checked through it
        // (InterruptNotes), and the notes that check and the link read gone
        // from the image -- a --shared compile's 17 KB libData.so carried a
        // 14.7 KB .corsac.irq.
        if (!args.Contains("--obj") && (kernel is not null || shared))
        {
            List<string> handlerErrors = Corsac.Lang.Lto.InterruptNotes.Check(new[] { obj }, Corsac.Lang.Lto.Preinit.Peek(new[] { obj }, longMode: false)?.FrozenSteps);
            if (handlerErrors.Count > 0) throw new LinkException(handlerErrors);
            // And where its float code runs (X87Notes), against the kernel's and
            // the modules' it links with, as their links saw them; its own
            // facts beside it, for what links against it.
            bool judges = Corsac.Lang.Lto.X87Notes.Judges(new[] { obj });
            List<Corsac.Lang.Lto.X87Notes.Fact> beside = new();
            if (kernelPath is not null) beside.AddRange(Corsac.Lang.Lto.X87Notes.ReadBeside(kernelPath, judges));
            foreach (string lib in sharedLibs) beside.AddRange(Corsac.Lang.Lto.X87Notes.ReadBeside(lib, judges));
            List<string> moduleFloats = Corsac.Lang.Lto.X87Notes.Check(new[] { obj }, beside);
            if (moduleFloats.Count > 0) throw new LinkException(moduleFloats);
            Corsac.Lang.Lto.X87Notes.WriteBeside(output, new[] { obj });
            Corsac.Lang.Lto.X87Notes.Strip(new[] { obj });
            Corsac.Lang.Lto.UsesNotes.Strip(new[] { obj });
            Corsac.Lang.Lto.InterruptNotes.Strip(new[] { obj });
            Corsac.Lang.Lto.LinkTimeOptimizer.StripLinkNotes(new[] { obj });
        }
        if (kernel is not null && !args.Contains("--obj"))
        {
            byte[] ko = Linker.LinkModule(new[] { (name, obj) }, output, kernel, sharedLibs, relocatedText);
            File.WriteAllBytes(output, ko);
            Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code, {ko.Length} bytes module of {kernel.Name}");
            return 0;
        }
        if (shared && !args.Contains("--obj") && !windows)
        {
            // DT_SONAME is the name the loader will look for, so it is the
            // name the file is being given here and not the path it sits at.
            byte[] so = Linker.LinkShared(new[] { (name, obj) }, Path.GetFileName(output), Needed(obj, sharedLibs, exports), Value(args, "--runpath"));
            File.WriteAllBytes(output, so);
            Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code, {so.Length} bytes shared object");
            return 0;
        }

        if ((args.Contains("--obj") || library) && !windows)
        {
            // A module's unit only with the IR taken before the late passes,
            // whose settings say it is position-independent: regenerated
            // from an archive that does not say so, it would come back as a
            // kernel's code is, with absolute addresses in its text.
            if (x86Backend.EmitLinkSummary && sharedLibs.Count == 0
                && (!x86Backend.PositionIndependent || moduleUnit && linkRecords is not null))
            {
                // The lifetime hints first: the IR archive's integrity hash
                // covers every other section, these included.
                if (lifetimeHintBytes is not null) Corsac.Lang.Lto.LifetimeHints.Attach(obj, lifetimeHintBytes);
                if (heapRecordBytes is not null) Corsac.Lang.Lto.HeapRecords.Attach(obj, heapRecordBytes);
                if (linkRecords is not null && regionHintBytes is not null) Corsac.Lang.Lto.RegionHints.Attach(obj, regionHintBytes);
                // Each body let go as it is copied: nothing reads the records after.
                if (linkRecords is not null) Corsac.Lang.Lto.IrArchive.AttachConsuming(obj, linkRecords);
                else IrUnitCodec.Attach(obj, module, x86Backend.StackMaps);
            }
            ElfWriter.WriteObjectFile(obj, output);
            if (Value(args, "--dependency-file") is string dependencyFile) declarations!.WriteDependencies(dependencyFile, takenFromKernel, keptFromLibrary);
            Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code");
            return 0;
        }

        // _START BY DEFAULT, AS THE HELP SAYS, when an assembled object owns
        // it (--asm-entry): the stub is what its startup calls, and entered
        // straight from the loader it ran on the loader's stack with .bss
        // never cleared (EntryClearsBss) and no GS for --tls-gs.
        string entry = Value(args, "--entry") ?? (Value(args, "--asm-entry") is not null ? "_start" : module.Entry)
            ?? (runtimeDll ? Corsac.Lang.Lower.Lowering.DllEntryName : "_start");
        List<(string, ObjectFile)> link = new() { (name, obj) };
        // And the objects --with named, read before optimising.
        link.AddRange(withObjects);
        // A FLAT IMAGE BEGINS WITH ITS ENTRY: the object that defines it
        // first, when that is one --with named.
        if (flat)
        {
            int owner = link.FindIndex(input => input.Item2.Symbols.Any(symbol => symbol.IsDefined && symbol.Global && symbol.Name == entry));
            if (owner > 0)
            {
                (string, ObjectFile) first = link[owner];
                link.RemoveAt(owner);
                link.Insert(0, first);
            }
        }
        // The report before link-time optimisation; the notes out after it,
        // whose IR archives' integrity hash covers them.
        if (Value(args, "--unused-report") is string unusedReport) Corsac.Lang.Lto.UnusedReport.Write(link, entry, unusedReport);
        // Every interrupt handler, through every object linked here
        // (InterruptNotes): what each unit's compile could not follow.
        List<string> interruptErrors = Corsac.Lang.Lto.InterruptNotes.Check(link.Select(input => input.Item2), Corsac.Lang.Lto.Preinit.Peek(link.Select(input => input.Item2), longMode: false)?.FrozenSteps);
        // REFUSED, every path listed (InterruptNotes.EveryAllocation): the
        // kernel's handlers reach no allocation since the wake predicate
        // left WaitQueue.WakeAll (Sched.WakeQueuedIfOver) and the AIC's
        // drain stopped making its messages.
        if (interruptErrors.Count > 0) throw new LinkException(interruptErrors);
        if (Switches.IrqLocksReport) foreach (string line in Corsac.Lang.Lto.InterruptNotes.IrqLocksReport(link.Select(input => input.Item2))) Console.Error.WriteLine(line);
        // Where every unit's freestanding float code runs (X87Notes): what no
        // one unit's compile could see, its callers in the others.
        bool judgesFloats = Corsac.Lang.Lto.X87Notes.Judges(link.Select(input => input.Item2));
        List<Corsac.Lang.Lto.X87Notes.Fact> floatsBeside = new();
        foreach (string lib in sharedLibs) floatsBeside.AddRange(Corsac.Lang.Lto.X87Notes.ReadBeside(lib, judgesFloats));
        List<string> floatErrors = Corsac.Lang.Lto.X87Notes.Check(link.Select(input => input.Item2), floatsBeside, new[] { entry });
        if (floatErrors.Count > 0) throw new LinkException(floatErrors);

        if (flat)
        {
            // The entry was ordered before initial emission, exactly as for
            // --flat --obj. Keep the original metadata and --with objects.
            TargetContract.Validate(link);
            Corsac.Lang.Lto.LinkTimeOptimizer.Run(link, !args.Contains("--no-lto") && !args.Contains("--no-opt"));
            Corsac.Lang.Lto.UsesNotes.Strip(link.Select(input => input.Item2));
            Corsac.Lang.Lto.InterruptNotes.Strip(link.Select(input => input.Item2));
            Corsac.Lang.Lto.X87Notes.Strip(link.Select(input => input.Item2));
            // --map FILE: every symbol of the image, largest first, as `corc link` writes it.
            Linker.FlatImage image = Linker.LinkFlat(link, entry, loadBase ?? 0x10000, mapPath: Value(args, "--map"));
            File.WriteAllBytes(output, image.Bytes);
            Console.Error.WriteLine(
                $"{output}: flat image at 0x{image.Base:x}, {image.Bytes.Length} bytes "
                + $"(text {image.TextSize}, rodata {image.ReadOnlySize}, data {image.DataSize}), "
                + $"bss {image.BssSize} bytes to zero at 0x{image.Base + (ulong)image.Bytes.Length:x}, "
                + $"{image.MemorySize} bytes of memory");
            return 0;
        }

        if (windows)
        {
            // A WINDOWS IMAGE: the same link with a PE32 file around it
            // (Linker.LinkPe), based where Microsoft's linker bases one.
            uint imageBase = dll ? Corsac.Lang.Pe.PeFormat.DefaultDllBase : Corsac.Lang.Pe.PeFormat.DefaultImageBase;
            if (loadBase is { } given)
            {
                if (given % 0x10000 != 0) return Fail($"--base 0x{given:x}: a Windows image's base is a multiple of 64 KiB");
                imageBase = (uint)given;
            }
            if (Value(args, "--debug-keep-object") is string keepObject) ElfWriter.WriteObjectFile(obj, keepObject);
            TargetContract.Validate(link);
            Corsac.Lang.Lto.LinkTimeOptimizer.Run(link, !args.Contains("--no-lto") && !args.Contains("--no-opt"));
            Corsac.Lang.Lto.UsesNotes.Strip(link.Select(input => input.Item2));
            Corsac.Lang.Lto.InterruptNotes.Strip(link.Select(input => input.Item2));
            Corsac.Lang.Lto.X87Notes.Strip(link.Select(input => input.Item2));
            Linker.LinkPeTo(output, link, new Linker.PeOptions
            {
                Dll = dll,
                ImageBase = imageBase,
                Entry = entry,
                Subsystem = Value(args, "--subsystem") == "console" ? Corsac.Lang.Pe.PeSubsystem.WindowsConsole : Corsac.Lang.Pe.PeSubsystem.WindowsGui,
                Name = Path.GetFileName(output),
                MapPath = Value(args, "--map"),
                ExportAll = runtimeDll,
                RuntimeSymbols = sharedLibs.Count == 0 ? null
                    : exports.SelectMany(lib => lib.Value.Select(symbol => (symbol, dll: Path.GetFileName(lib.Key))))
                        .GroupBy(e => e.symbol, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().dll, StringComparer.Ordinal),
            });
            Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code, {new FileInfo(output).Length} bytes {(dll ? "DLL" : "program")}");
            return 0;
        }

        if (sharedLibs.Count == 0)
        {
            TargetContract.Validate(link);
            Corsac.Lang.Lto.LinkTimeOptimizer.Run(link, !args.Contains("--no-lto") && !args.Contains("--no-opt"));
        }
        // Linked against shared libraries, with no optimiser to read them:
        // the unit's summaries go all the same (a --link-shared program
        // carried its .corsac.lto).
        else Corsac.Lang.Lto.LinkTimeOptimizer.StripLinkNotes(link.Select(input => input.Item2));
        Corsac.Lang.Lto.UsesNotes.Strip(link.Select(input => input.Item2));
        Corsac.Lang.Lto.InterruptNotes.Strip(link.Select(input => input.Item2));
        Corsac.Lang.Lto.X87Notes.Strip(link.Select(input => input.Item2));
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
        // A static image goes to its file a chunk at a time (Linker.LinkTo),
        // never as one array of its whole size.
        if (sharedLibs.Count > 0)
            File.WriteAllBytes(output, Linker.Link(new[] { (name, obj) }, entry, Needed(obj, sharedLibs, exports), Value(args, "--runpath"), program: program));
        else Linker.LinkTo(output, link, entry, loadBase ?? Linker.DefaultLoadAddress, physicalBase, program);
        long exeLength = new FileInfo(output).Length;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(output, File.GetUnixFileMode(output) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        Console.Error.WriteLine($"{output}: {obj.Section(".text").Size} bytes of code, {exeLength} bytes");
        return 0;
    }

    /// <summary>
    /// THE TARGET'S OWN CONDITIONAL SYMBOLS, the names .NET's own class library
    /// is written against: TARGET_64BIT or TARGET_32BIT for the width of a
    /// word, TARGET_AMD64 or TARGET_X86 for the instruction set. The runtime
    /// reads its pointer-sized layouts through them -- `#if TARGET_64BIT` --
    /// exactly as CoreLib does, so one source serves both machines.
    /// </summary>
    internal static IEnumerable<string> TargetSymbols(Target target)
    {
        if (target == Target.X86_64)
        {
            yield return "TARGET_64BIT";
            yield return "TARGET_AMD64";
            // The interface its system calls take (--syscall-abi), which is
            // its own: the symbol the library's per-interface code asks.
            yield return "SYSCALL64";
        }
        else if (target == Target.X86)
        {
            yield return "TARGET_32BIT";
            yield return "TARGET_X86";
        }
        else if (target.WordSize == 8)
        {
            yield return "TARGET_64BIT";
        }
        else
        {
            yield return "TARGET_32BIT";
        }
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
        // A call of a method's $inside copy is of the method itself where the
        // library no longer has the copy (Linker.InsideOrItself, after this):
        // the library that has the method is needed all the same.
        foreach (string name in undefined.Where(n => n.EndsWith(Linker.InsideSuffix, StringComparison.Ordinal)).ToList())
            undefined.Add(name[..^Linker.InsideSuffix.Length]);
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
    /// Located from --lib-root if given to the repository root, else from the
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
    internal static string? LibraryRoot()
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
        string? root = Switches.LibRoot;
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

    public static List<string> DefaultLibraries(Target target, bool freestanding = false, bool windows = false)
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
            Path.Combine(root, "runtime", "src", "core", "unicode.cor"),
            Path.Combine(root, "runtime", "src", "core", "gc.cor"),
            Path.Combine(root, "stdlib", "src", "System", "GC.cor"),
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
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "threads.cor"));
        }
        string system = freestanding
            ? Path.Combine(root, "runtime", "src", "arch", "x86", "baremetal.cor")
            : Path.Combine(root, "runtime", "src", "platforms", "linux", "system.cor");
        if (File.Exists(system))
        {
            libs.Add(system);
        }
        if (windows)
        {
            // THE WINDOWS PLATFORM: the bare-metal runtime's hooks answered by
            // kernel32 (win32.cor), its own clock and threads, and the file
            // calls under Linux's names and numbers (abi.cor), so the hosted
            // class library above it is the one Linux has.
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "win32", "win32.cor"));
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "win32", "threading.cor"));
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "threads.cor"));
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "linux", "abi.cor"));
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "win32", "files.cor"));
            foreach (string dotnet in new[] {
                "System/interop.cor", "System/IO/io.cor", "System/Collections/Collections.cor", "System/IO/io-streams.cor",
                "System/IO/timezone.cor", "System/time.cor", "System/values.cor", "System/console.cor", "System/environment.cor",
                "System/signals.cor",
                // Windows Forms and System.Drawing over the seat, which on
                // Windows is in the program (WinSeat.cor) and draws with the
                // GUI kernel's engine; and what Forms reaches for besides.
                "System/IO/compression.cor", "System/ServiceProcess/ServiceWire.cor",
                "System/Runtime/InteropServices/ComInterop.cor", "System/Runtime/InteropServices/ComNdr.cor",
                "System/Runtime/InteropServices/ComRemote.cor", "System/Runtime/InteropServices/ComOle.cor",
                "System/Drawing/Drawing.cor", "System/Drawing/Drawing2D.cor", "System/Drawing/FontEngine.cor", "System/Drawing/TrueType.cor",
                "System/Drawing/BitmapFont.cor", "System/Drawing/Text.cor", "System/Drawing/Imaging.cor",
                "Corsac/GUI/Engine/enginedraw.cor", "Corsac/GUI/Engine/enginepaths.cor", "Corsac/GUI/Engine/engineregions.cor",
                "Corsac/GUI/Engine/enginestrokes.cor", "Corsac/GUI/Engine/enginexform.cor", "Corsac/GUI/Engine/scrollbars.cor",
                "Corsac/GUI/Gui.cor", "Corsac/GUI/WinSeat.cor",
                "System/Windows/Forms/Forms.cor", "Microsoft/Win32/SystemEvents.cor",
                "System/Drawing/Printing/SpoolWire.cor", "System/Drawing/Printing/Printing.cor",
                "System/Windows/Forms/PrintDialogs.cor", "System/Windows/Forms/PrintPreview.cor" })
            {
                libs.Add(Path.Combine(root, "stdlib", "src", dotnet));
            }
        }
        if (!freestanding)
        {
            // Linux's numbers and structures, and its file calls, which
            // CORSAC's kernel compiles too (files.cor, --kernel-syscalls).
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "linux", "abi.cor"));
            libs.Add(Path.Combine(root, "runtime", "src", "platforms", "linux", "files.cor"));
        }
        if (freestanding)
        {
            // INTERLOCKED AND VOLATILE ON BARE METAL. The runtime never
            // depends on them -- it has Sys's atomics -- but they are .NET's,
            // and a kernel written in C# uses them as any C# program does.
            // They need nothing but Sys and core's AtomicCell; an image that
            // calls neither has every one of their functions removed as
            // unreachable. The hosted set has them among the class library's
            // sources below.
            libs.Add(Path.Combine(root, "stdlib", "src", "System", "Threading", "interlocked.cor"));
        }
        if (!freestanding)
        {
            // The .NET class library over the system library: System.IO,
            // System.IO.Compression, System.Formats.Tar, System.Console,
            // System.Environment, System.Net(.Sockets),
            // System.Security.Cryptography, PosixSignalRegistration,
            // System.Diagnostics.Process.
            foreach (string dotnet in new[] {
                "System/interop.cor", "System/Threading/interlocked.cor", "System/IO/io.cor", "System/Collections/Collections.cor",
                "System/IO/io-streams.cor", "System/IO/timezone.cor", "System/IO/compression.cor", "System/IO/tar.cor", "System/IO/watcher.cor",
                "System/time.cor", "System/values.cor", "System/Reflection/Assembly.cor", "System/numerics.cor",
                "System/Text/RegularExpressions.cor", "System/Xml/Xml.cor", "System/Text/Json/Json.cor", "System/console.cor", "System/environment.cor",
                "System/Net/Net.cor", "System/Security/Cryptography/Cryptography.cor", "System/Security/Cryptography/Hashing.cor",
                // The CORSAC/SECURE primitives and X.509 beside the hashes (System.Security.Cryptography),
                // then TLS (System.Net.Security), in the order their libraries own them.
                "Corsac/Security/Cryptography/crypto.cor", "Corsac/Security/Cryptography/crypto_base.cor", "Corsac/Security/Cryptography/crypto_der.cor", "Corsac/Security/Cryptography/crypto_random.cor", "Corsac/Security/Cryptography/crypto_sha512.cor", "Corsac/Security/Cryptography/crypto_chacha.cor", "Corsac/Security/Cryptography/crypto_poly1305.cor", "Corsac/Security/Cryptography/crypto_chachapoly.cor", "Corsac/Security/Cryptography/crypto_aes.cor",
                "Corsac/Security/Cryptography/crypto_gcm.cor", "Corsac/Security/Cryptography/crypto_field25519.cor", "Corsac/Security/Cryptography/crypto_x25519.cor", "Corsac/Security/Cryptography/crypto_ed25519.cor", "Corsac/Security/Cryptography/crypto_p256_field.cor", "Corsac/Security/Cryptography/crypto_p256.cor", "Corsac/Security/Cryptography/crypto_p384.cor", "Corsac/Security/Cryptography/crypto_rsa.cor",
                "System/Security/Cryptography/Oid.cor", "System/Security/Cryptography/X509Certificates/X509Der.cor", "System/Security/Cryptography/X509Certificates/X509CertificateView.cor", "System/Security/Cryptography/X509Certificates/X509PathChecks.cor", "System/Security/Cryptography/X509Certificates/X509Certificate.cor", "System/Security/Cryptography/X509Certificates/X509Store.cor", "System/Security/Cryptography/X509Certificates/X509Chain.cor", "System/Security/Cryptography/X509Certificates/X509KeyExtensions.cor",
                "System/Security/Cryptography/HKDF.cor", "System/Security/Cryptography/Aead.cor", "System/Security/Cryptography/Asymmetric.cor",
                "System/Net/SslPolicyErrors.cor", "System/Security/Authentication/SslProtocols.cor", "System/Security/Authentication/AuthenticationException.cor",
                "System/Net/Security/tls.cor", "System/Net/Security/tls13.cor", "System/Net/Security/tls_aesgcm.cor", "System/Net/Security/tls_stream.cor", "System/Net/Security/tls_handshake.cor", "System/Net/Security/tls_hello.cor", "System/Net/Security/tls_policy.cor", "System/Net/Security/tls_client.cor", "System/Net/Security/SslStream.cor",
                "System/ServiceProcess/ServiceWire.cor", "System/ServiceProcess/ServiceProcess.cor", "System/Security/Cryptography/ProtectedData.cor",
                "System/Runtime/InteropServices/ComInterop.cor", "System/Runtime/InteropServices/ComNdr.cor", "System/Runtime/InteropServices/ComRemote.cor", "System/Runtime/InteropServices/ComOle.cor", "System/Dynamic/Dynamic.cor",
                "System/signals.cor", "System/unix.cor", "System/process.cor", "System/power.cor",
                "Microsoft/Win32/Registry.cor",
                "System/Drawing/Drawing.cor", "System/Drawing/Drawing2D.cor", "System/Drawing/FontEngine.cor", "System/Drawing/TrueType.cor", "System/Drawing/BitmapFont.cor", "System/Drawing/Text.cor",
                "Corsac/GUI/Gui.cor", "System/Drawing/Imaging.cor",
                "System/Windows/Forms/Forms.cor", "Microsoft/Win32/SystemEvents.cor",
                "System/Drawing/Printing/SpoolWire.cor", "System/Drawing/Printing/Printing.cor", "System/Windows/Forms/PrintDialogs.cor", "System/Windows/Forms/PrintPreview.cor" })
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
    private static void Optimise(Module module, string? traced = null, bool experimentalSsa = false, bool optimizeSize = false, bool experimentalBatch = false, string? batchWithout = null, int workers = 1,
        Action<Module>? beforeLate = null, string? regionReport = null, bool regions = true)
    {
        Corsac.Lang.Opt.Pipeline pipeline = Corsac.Lang.Opt.Pipeline.Default(optimizeSize: optimizeSize, experimentalBatch: experimentalBatch, regionReport: regionReport, regions: regions);
        pipeline.Workers = workers;
        pipeline.BeforeLate = beforeLate;
        pipeline.Reports = true;
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
            pipeline.TraceFunction = traced;
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
