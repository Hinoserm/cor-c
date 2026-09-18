#nullable enable
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Tests.Elf;

/// <summary>
/// Drives the ELF writer, reader and linker against the real tools: GNU
/// readelf/objdump/nm/ld, gcc -m32, gdb, and the kernel's own i386 loader.
/// Usage: ElfTests [work-directory]. Every intermediate file is left in the
/// work directory for inspection.
/// </summary>
public static class Program
{
    private static int _failures;
    private static int _passes;
    private static string _dir = "";

    public static int Main(string[] args)
    {
        _dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "corsac-elf-tests");
        Directory.CreateDirectory(_dir);
        Console.WriteLine($"work directory: {_dir}");

        Try("object accepted by binutils and ld", ObjectAcceptedByBinutils);
        Try("object round trip is byte-exact", RoundTrip);
        Try("gcc object reads and relinks", GccObject);
        Try("static link of exit(42)", LinkExit42);
        Try("link address and load address differ", HigherHalfLink);
        Try("link with Rel32 call and Abs32 data across objects", LinkTwoObjects);
        Try("link errors are reported", LinkErrors);
        Try("linker-defined layout symbols", LinkerSymbols);
        Try("linked with gcc object", LinkWithGcc);
        Try("shared object structure", SharedObjectStructure);
        Try("relocated constants stay outside mutable static roots", RelocatedConstantRoots);
        Try("dynamically linked executable runs", DynamicExecutable);
        Try("one shared object importing from another", TwoSharedObjects);
        Try("shared object used by a C program", SharedObjectFromC);

        Console.WriteLine($"{_passes} passed, {_failures} failed");
        return _failures == 0 ? 0 : 1;
    }

    // ---- Objects under test ----------------------------------------------

    /// <summary>_start: mov eax,1; mov ebx,42; int 0x80.</summary>
    private static ObjectFile Exit42()
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0xb8, 1, 0, 0, 0, 0xbb, 42, 0, 0, 0, 0xcd, 0x80 });
        o.Sections.Add(text);
        o.Symbols.Add(new Symbol { Name = "_start", Section = text, Offset = 0, Size = 12, IsFunction = true });
        return o;
    }

    /// <summary>_start: call f; mov ebx,eax; mov eax,1; int 0x80. f is external.</summary>
    private static ObjectFile Caller()
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0xe8, 0, 0, 0, 0, 0x89, 0xc3, 0xb8, 1, 0, 0, 0, 0xcd, 0x80 });
        text.Relocs.Add(new Relocation(1, "f", -4, RelocKind.Rel32));
        o.Sections.Add(text);
        o.Symbols.Add(new Symbol { Name = "_start", Section = text, Offset = 0, Size = 14, IsFunction = true });
        o.Symbols.Add(new Symbol { Name = "f", Section = null });
        return o;
    }

    /// <summary>
    /// f: mov eax,[pmsg]; movzx eax,byte [eax]; ret -- returns the first byte
    /// of the string that .data points at, which is 42. Exercises Abs32 into
    /// .text (the [pmsg] operand), Abs32 into .data (the pointer), a local
    /// symbol, .bss and a note section carrying a relocation.
    /// </summary>
    private static ObjectFile Callee()
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0xa1, 0, 0, 0, 0, 0x0f, 0xb6, 0x00, 0xc3 });
        text.Relocs.Add(new Relocation(1, "pmsg", 0, RelocKind.Abs32));
        Section rodata = new(".rodata", SectionKind.ReadOnlyData) { Align = 4 };
        rodata.Bytes.AddRange(new byte[] { 42, (byte)'h', (byte)'i', 0 });
        Section data = new(".data", SectionKind.Data) { Align = 4 };
        data.Bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        data.Relocs.Add(new Relocation(0, "msg", 0, RelocKind.Abs32));
        Section bss = new(".bss", SectionKind.Uninitialised) { Align = 16, ZeroBytes = 64 };
        Section meta = new(".corsac.meta", SectionKind.Note) { Align = 4 };
        meta.Bytes.AddRange(Encoding.ASCII.GetBytes("META"));
        meta.Bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        meta.Relocs.Add(new Relocation(4, "f", 0, RelocKind.Abs32));
        o.Sections.Add(text);
        o.Sections.Add(rodata);
        o.Sections.Add(data);
        o.Sections.Add(bss);
        o.Sections.Add(meta);
        o.Symbols.Add(new Symbol { Name = "msg", Section = rodata, Offset = 0, Size = 4, Global = false });
        o.Symbols.Add(new Symbol { Name = "f", Section = text, Offset = 0, Size = 9, IsFunction = true });
        o.Symbols.Add(new Symbol { Name = "pmsg", Section = data, Offset = 0, Size = 4 });
        o.Symbols.Add(new Symbol { Name = "buf", Section = bss, Offset = 0, Size = 64 });
        return o;
    }

    // ---- Tests -----------------------------------------------------------

    private static void ObjectAcceptedByBinutils()
    {
        string path = Write("exit42.o", ElfWriter.WriteObject(Exit42()));

        (int code, string output) = Run("readelf", "-a", path);
        Check(code == 0, "readelf -a exits 0", output);
        Check(!output.Contains("Warning") && !output.Contains("Error"), "readelf -a reports no warnings", output);
        Check(output.Contains("REL (Relocatable file)") && output.Contains("Intel 80386"), "readelf sees ET_REL i386", output);

        (code, output) = Run("objdump", "-dr", path);
        Check(code == 0 && output.Contains("<_start>:") && output.Contains("int    $0x80"), "objdump -dr disassembles _start", output);

        (code, output) = Run("nm", path);
        Check(code == 0 && Regex.IsMatch(output, @"^00000000 T _start$", RegexOptions.Multiline), "nm shows T _start", output);

        string exe = Path.Combine(_dir, "exit42-ld");
        (code, output) = Run("ld", "-m", "elf_i386", "-o", exe, path);
        Check(code == 0 && output.Trim().Length == 0, "ld links it without complaint", output);
        Check(Exec(exe) == 42, "ld-linked executable exits 42");

        // The relocatable one too: ld has to like the .rel.text.
        string callee = Write("callee.o", ElfWriter.WriteObject(Callee()));
        (code, output) = Run("objdump", "-dr", callee);
        Check(code == 0 && output.Contains("R_386_32") && output.Contains("pmsg"), "objdump shows R_386_32 against pmsg", output);
        (code, output) = Run("readelf", "-r", callee);
        Check(code == 0 && output.Contains(".rel.corsac.meta"), "readelf lists relocations for the note section", output);
    }

    private static void RoundTrip()
    {
        foreach ((string name, ObjectFile o) in new[] { ("exit42", Exit42()), ("caller", Caller()), ("callee", Callee()) })
        {
            byte[] first = ElfWriter.WriteObject(o);
            ObjectFile back = ElfReader.ReadObject(first);
            byte[] second = ElfWriter.WriteObject(back);
            Check(first.AsSpan().SequenceEqual(second), $"{name}: write(read(write(o))) == write(o)");
            Check(back.Sections.Count == o.Sections.Count, $"{name}: section count survives");
            Check(back.Symbols.Count == o.Symbols.Count, $"{name}: symbol count survives");
            for (int i = 0; i < o.Sections.Count; i++)
            {
                Section a = o.Sections[i];
                Section b = back.Sections[i];
                Check(a.Name == b.Name && a.Kind == b.Kind && a.Align == b.Align && a.Size == b.Size, $"{name}: section {a.Name} survives");
                Check(a.Relocs.SequenceEqual(b.Relocs), $"{name}: relocations of {a.Name} survive");
            }
        }
    }

    private static string? _gccObject;

    private static string? CompileWithGcc()
    {
        if (_gccObject is not null)
        {
            return _gccObject;
        }
        string src = Path.Combine(_dir, "gccpart.c");
        File.WriteAllText(src, """
            static const char message[] = "*hello";
            const char *pointer = message;
            static int counter;
            __attribute__((noinline)) static int helper(int x) { return x + 1; }
            int f(void) { counter++; return helper(pointer[0] + counter - 2); }
            """);
        string obj = Path.Combine(_dir, "gccpart.o");
        (int code, string output) = Run("gcc", "-m32", "-c", "-O1", "-ffreestanding", "-nostdlib", "-fno-pic", "-fno-asynchronous-unwind-tables", "-fno-common", "-o", obj, src);
        if (code != 0)
        {
            Console.WriteLine($"  skipped: gcc -m32 is not usable here:\n{output}");
            return null;
        }
        _gccObject = obj;
        return obj;
    }

    private static void GccObject()
    {
        string? obj = CompileWithGcc();
        if (obj is null)
        {
            return;
        }
        ObjectFile o = ElfReader.ReadObject(File.ReadAllBytes(obj));
        Check(o.Symbols.Any(s => s.Name == "f" && s.Global && s.IsFunction && s.Section?.Kind == SectionKind.Code), "gcc object: f is a global function in code");
        Check(o.Symbols.Any(s => s.Name == "pointer" && s.Global && s.Section?.Kind == SectionKind.Data), "gcc object: pointer is global data");
        Check(o.Symbols.Any(s => s.Name == "counter" && !s.Global && s.Section?.Kind == SectionKind.Uninitialised), "gcc object: counter is a local in bss");
        Section data = o.Sections.First(s => s.Kind == SectionKind.Data);
        Check(data.Relocs.Count == 1 && data.Relocs[0].Kind == RelocKind.Abs32 && data.Relocs[0].Symbol.StartsWith(".rodata"), "gcc object: pointer is relocated against the .rodata section symbol", string.Join(", ", data.Relocs));
        Check(o.Sections.Any(s => s.Kind == SectionKind.Code && s.Relocs.Any(r => r.Kind == RelocKind.Abs32 || r.Kind == RelocKind.Rel32)), "gcc object: code has relocations");

        // Writing it back out must give ld something it can still link.
        string rewritten = Write("gccpart-rewritten.o", ElfWriter.WriteObject(o));
        string caller = Write("caller.o", ElfWriter.WriteObject(Caller()));
        string exe = Path.Combine(_dir, "gcc-rewritten-ld");
        (int code, string output) = Run("ld", "-m", "elf_i386", "-o", exe, caller, rewritten);
        Check(code == 0, "ld links the rewritten gcc object", output);
        Check(Exec(exe) == 42, "ld-linked rewritten gcc program exits 42");
    }

    private static void LinkExit42()
    {
        byte[] exe = Linker.Link(new[] { Exit42() }, "_start");
        string path = Write("exit42-corsac", exe);
        Check(Exec(path) == 42, "our executable exits 42");

        (int code, string output) = Run("readelf", "-a", path);
        Check(code == 0 && !output.Contains("Warning"), "readelf -a accepts the executable", output);
        Check(output.Contains("EXEC (Executable file)"), "it is ET_EXEC", output);
        Check(Regex.IsMatch(output, @"LOAD\s+0x000000 0x08048000 0x08048000 0x\w+ 0x\w+ R E 0x1000"), "text segment at 0x08048000, RX, page aligned", output);
        Check(output.Contains("GNU_STACK") && Regex.IsMatch(output, @"GNU_STACK.*RW "), "non-executable stack requested", output);

        (code, output) = Run("nm", path);
        Match m = Regex.Match(output, @"^(\w+) T _start$", RegexOptions.Multiline);
        Check(m.Success, "nm shows _start in the executable", output);
        (code, output) = Run("readelf", "-h", path);
        Check(output.Contains($"Entry point address:               0x{m.Groups[1].Value.TrimStart('0')}"), "entry point is _start", output);

        (code, output) = Run("objdump", "-d", path);
        Check(code == 0 && output.Contains("<_start>:") && output.Contains("int    $0x80"), "objdump -d shows _start", output);

        (code, output) = Run("gdb", "-batch", "-ex", "info address _start", path);
        Check(code == 0 && Regex.IsMatch(output, @"Symbol ""_start"" is .*at (address )?0x8048"), "gdb reads the symbol table", output);
    }

    /// <summary>
    /// A kernel linked to run high and loaded low: every PT_LOAD's p_vaddr is
    /// the link address and its p_paddr the physical one, because the thing
    /// that copies the image has no paging on yet and can only obey p_paddr.
    /// </summary>
    private static void HigherHalfLink()
    {
        const uint Link = 0xC0100000;
        const uint Load = 0x00100000;
        byte[] exe = Linker.Link(new[] { ("kernel", Callee()) }, "f", Link, Load);
        string path = Write("higherhalf", exe);
        (int code, string output) = Run("readelf", "-lW", path);
        Check(code == 0, "readelf -l accepts the higher-half image", output);

        int loads = 0;
        foreach (Match m in Regex.Matches(output, @"LOAD\s+0x(\w+) 0x(\w+) 0x(\w+)"))
        {
            loads++;
            uint vaddr = Convert.ToUInt32(m.Groups[2].Value, 16);
            uint paddr = Convert.ToUInt32(m.Groups[3].Value, 16);
            Check(vaddr >= Link, $"p_vaddr 0x{vaddr:x8} is the link address", output);
            Check(vaddr - paddr == Link - Load, $"p_paddr 0x{paddr:x8} is 0x{Link - Load:x} below p_vaddr", output);
        }
        Check(loads > 0, "the image has a loadable segment", output);

        // The entry is where the loader jumps with paging still off.
        (_, string header) = Run("readelf", "-hW", path);
        Match e = Regex.Match(header, @"Entry point address: *0x(\w+)");
        uint entry = Convert.ToUInt32(e.Groups[1].Value, 16);
        Check(entry >= Load && entry < Link, $"the entry 0x{entry:x8} is a physical address", header);

        // The default -- every hosted program -- must be unchanged: p_paddr
        // equal to p_vaddr, which is what ld writes and gdb expects.
        string plain = Write("plainload", Linker.Link(new[] { ("plain", Callee()) }, "f"));
        (_, string plainOut) = Run("readelf", "-lW", plain);
        foreach (Match m in Regex.Matches(plainOut, @"LOAD\s+0x(\w+) 0x(\w+) 0x(\w+)"))
        {
            Check(m.Groups[2].Value == m.Groups[3].Value, "without --load, p_paddr equals p_vaddr", plainOut);
        }
    }

    private static void LinkTwoObjects()
    {
        byte[] exe = Linker.Link(new[] { ("caller.o", Caller()), ("callee.o", Callee()) }, "_start");
        string path = Write("two-corsac", exe);
        Check(Exec(path) == 42, "two-object executable exits 42");

        (int code, string output) = Run("nm", path);
        Check(code == 0, "nm reads the executable", output);
        uint f = Address(output, "T", "f");
        uint pmsg = Address(output, "D", "pmsg");
        uint msg = Address(output, "r", "msg");
        uint buf = Address(output, "B", "buf");
        uint start = Address(output, "T", "_start");
        Check(f != 0 && pmsg != 0 && msg != 0 && buf != 0, "all symbols present with the expected types", output);
        Check(Address(output, "A", "_end") >= buf + 64, "_end is past .bss", output);

        // The Rel32 call resolved to f, and the Abs32 pointer to msg.
        (code, output) = Run("objdump", "-d", path);
        Check(Regex.IsMatch(output, $@"e8 .*call\s+{f:x} <f>"), "the call in _start targets f", output);
        Check(Regex.IsMatch(output, $@"a1 .*mov\s+0x{pmsg:x},%eax"), "the load in f reads pmsg", output);

        (code, output) = Run("readelf", "-x", ".data", path);
        Match m = Regex.Match(output, @"0x(\w{8}) (\w{8})");
        Check(m.Success && Convert.ToUInt32(m.Groups[1].Value, 16) == pmsg, "readelf -x .data starts at pmsg", output);
        string word = m.Groups[2].Value;
        uint stored = Convert.ToUInt32(word.Substring(6, 2) + word.Substring(4, 2) + word.Substring(2, 2) + word.Substring(0, 2), 16);
        Check(stored == msg, $"pmsg holds the address of msg (0x{stored:x} vs 0x{msg:x})", output);

        (code, output) = Run("readelf", "-l", path);
        Check(Regex.IsMatch(output, @"LOAD\s+0x(\w+) 0x0804(\w)(\w{3}) .* RW  0x1000"), "data segment is RW and page aligned", output);
        Match rw = Regex.Match(output, @"LOAD\s+0x(\w+) 0x(\w+) 0x\w+ 0x(\w+) 0x(\w+) RW ");
        Check(rw.Success && Convert.ToUInt32(rw.Groups[1].Value, 16) % 4096 == Convert.ToUInt32(rw.Groups[2].Value, 16) % 4096, "data segment offset is congruent to its address modulo the page", output);
        Check(rw.Success && Convert.ToUInt32(rw.Groups[4].Value, 16) > Convert.ToUInt32(rw.Groups[3].Value, 16), "data segment memsz exceeds filesz for .bss", output);
        Check(rw.Success && Convert.ToUInt32(rw.Groups[2].Value, 16) >= 0x08049000 && start < 0x08049000, "data lives on a later page than text", output);

        (code, output) = Run("readelf", "-S", path);
        Check(output.Contains(".corsac.meta") && Regex.IsMatch(output, @"\.corsac\.meta\s+PROGBITS\s+00000000"), ".corsac.meta is present and not allocated", output);

        // The same inputs through ld must run too: the objects are honest.
        string ldExe = Path.Combine(_dir, "two-ld");
        (code, output) = Run("ld", "-m", "elf_i386", "-o", ldExe, Write("caller.o", ElfWriter.WriteObject(Caller())), Write("callee.o", ElfWriter.WriteObject(Callee())));
        Check(code == 0 && Exec(ldExe) == 42, "ld links the same objects to a working executable", output);
    }

    private static void LinkErrors()
    {
        try
        {
            Linker.Link(new[] { Caller() }, "_start");
            Check(false, "undefined symbol is an error");
        }
        catch (LinkException e)
        {
            Check(e.Errors.Count == 1 && e.Errors[0].Contains("undefined symbol 'f'") && e.Errors[0].Contains("object 1"), "undefined symbol names symbol and object", e.Message);
        }
        try
        {
            Linker.Link(new[] { ("a.o", Callee()), ("b.o", Callee()), ("c.o", Caller()) }, "_start");
            Check(false, "duplicate symbol is an error");
        }
        catch (LinkException e)
        {
            Check(e.Errors.Any(m => m.Contains("'f' is defined in both a.o and b.o")), "duplicate symbol names both objects", e.Message);
            Check(e.Errors.Any(m => m.Contains("'pmsg' is defined in both")), "every duplicate is reported", e.Message);
        }
        try
        {
            Linker.Link(new[] { Exit42() }, "main");
            Check(false, "missing entry symbol is an error");
        }
        catch (LinkException e)
        {
            Check(e.Errors.Any(m => m.Contains("entry symbol 'main'")), "missing entry names the symbol", e.Message);
        }
    }

    /// <summary>
    /// _start: mov eax,[pend]; sub eax,[pdata]; mov ebx,eax; mov eax,1;
    /// int 0x80 -- exits with _end - __data_start, which the readelf
    /// section layout lets us predict exactly. .data holds the two
    /// pointers (Abs32 to linker symbols); .bss adds 24 bytes.
    /// </summary>
    private static void LinkerSymbols()
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0xa1, 0, 0, 0, 0, 0x2b, 0x05, 0, 0, 0, 0, 0x89, 0xc3, 0xb8, 1, 0, 0, 0, 0xcd, 0x80 });
        text.Relocs.Add(new Relocation(1, "pend", 0, RelocKind.Abs32));
        text.Relocs.Add(new Relocation(7, "pdata", 0, RelocKind.Abs32));
        Section data = new(".data", SectionKind.Data) { Align = 4 };
        data.Bytes.AddRange(new byte[8]);
        data.Relocs.Add(new Relocation(0, "_end", 0, RelocKind.Abs32));
        data.Relocs.Add(new Relocation(4, "__data_start", 0, RelocKind.Abs32));
        Section bss = new(".bss", SectionKind.Uninitialised) { Align = 4, ZeroBytes = 24 };
        o.Sections.Add(text);
        o.Sections.Add(data);
        o.Sections.Add(bss);
        o.Symbols.Add(new Symbol { Name = "_start", Section = text, Offset = 0, Size = 20, IsFunction = true });
        o.Symbols.Add(new Symbol { Name = "pend", Section = data, Offset = 0, Size = 4 });
        o.Symbols.Add(new Symbol { Name = "pdata", Section = data, Offset = 4, Size = 4 });

        string path = Write("linksyms-corsac", Linker.Link(new[] { o }, "_start"));
        Check(Exec(path) == 8 + 24, "_end - __data_start spans .data and .bss at run time");

        (int code, string output) = Run("readelf", "-S", path);
        Check(code == 0, "readelf -S reads the executable", output);
        Match data_ = Regex.Match(output, @"\.data\s+PROGBITS\s+(\w{8}) \w+ (\w{6})");
        Match bss_ = Regex.Match(output, @"\.bss\s+NOBITS\s+(\w{8}) \w+ (\w{6})");
        Match text_ = Regex.Match(output, @"\.text\s+PROGBITS\s+(\w{8}) \w+ (\w{6})");
        Check(data_.Success && bss_.Success && text_.Success, "readelf lists .text, .data and .bss", output);
        uint dataAddr = Convert.ToUInt32(data_.Groups[1].Value, 16);
        uint dataSize = Convert.ToUInt32(data_.Groups[2].Value, 16);
        uint bssAddr = Convert.ToUInt32(bss_.Groups[1].Value, 16);
        uint bssSize = Convert.ToUInt32(bss_.Groups[2].Value, 16);
        uint textAddr = Convert.ToUInt32(text_.Groups[1].Value, 16);
        uint textSize = Convert.ToUInt32(text_.Groups[2].Value, 16);

        (code, output) = Run("nm", path);
        Check(Address(output, "A", "__text_start") == textAddr, "__text_start is the .text address", output);
        Check(Address(output, "A", "_etext") == textAddr + textSize, "_etext is the end of .text", output);
        Check(Address(output, "A", "__data_start") == dataAddr, "__data_start is the .data address", output);
        Check(Address(output, "A", "_edata") == dataAddr + dataSize, "_edata is the end of .data", output);
        Check(Address(output, "A", "__bss_start") == bssAddr, "__bss_start is the .bss address", output);
        Check(Address(output, "A", "_end") == bssAddr + bssSize, "_end is the end of .bss", output);

        (code, output) = Run("readelf", "-x", ".data", path);
        Match m = Regex.Match(output, @"0x\w{8} (\w{8}) (\w{8})");
        Check(m.Success && Le(m.Groups[1].Value) == bssAddr + bssSize && Le(m.Groups[2].Value) == dataAddr, "Abs32 words in .data hold _end and __data_start", output);
    }

    private static uint Le(string hex)
    {
        return Convert.ToUInt32(hex.Substring(6, 2) + hex.Substring(4, 2) + hex.Substring(2, 2) + hex.Substring(0, 2), 16);
    }

    private static void LinkWithGcc()
    {
        string? obj = CompileWithGcc();
        if (obj is null)
        {
            return;
        }
        ObjectFile gcc = ElfReader.ReadObject(File.ReadAllBytes(obj));
        byte[] exe = Linker.Link(new[] { ("caller.o", Caller()), ("gccpart.o", gcc) }, "_start");
        string path = Write("gcc-corsac", exe);
        Check(Exec(path) == 42, "program with a gcc-compiled f exits 42");
        (int code, string output) = Run("nm", path);
        Check(code == 0 && Regex.IsMatch(output, @" b counter$", RegexOptions.Multiline) && Regex.IsMatch(output, @" t helper$", RegexOptions.Multiline), "gcc's local symbols appear in the executable", output);
    }

    // ---- Dynamic linking -------------------------------------------------

    /// <summary>
    /// A position-independent library exporting `answer`, which reads a
    /// private word through the GOT pointer and returns it. Hand-encoded,
    /// so the test says what the ABI requires rather than what the code
    /// generator happens to emit.
    /// </summary>
    private static ObjectFile Answer(bool callsOut)
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        Section data = new(".data", SectionKind.Data) { Align = 4 };
        data.Bytes.AddRange(new byte[] { 42, 0, 0, 0 });
        o.Sections.Add(text);
        o.Sections.Add(data);

        List<byte> code = new() { 0x53 };                       // push ebx
        code.AddRange(new byte[] { 0xe8, 0, 0, 0, 0 });         // call __x86.get_pc_thunk.bx
        text.Relocs.Add(new Relocation(code.Count - 4, "__x86.get_pc_thunk.bx", -4, RelocKind.Rel32));
        code.AddRange(new byte[] { 0x81, 0xc3, 0, 0, 0, 0 });   // add ebx, _GLOBAL_OFFSET_TABLE_
        text.Relocs.Add(new Relocation(code.Count - 4, "_GLOBAL_OFFSET_TABLE_", 2, RelocKind.GotPc));
        code.AddRange(new byte[] { 0x8b, 0x83, 0, 0, 0, 0 });   // mov eax, [ebx + value@GOTOFF]
        text.Relocs.Add(new Relocation(code.Count - 4, "value", 0, RelocKind.GotOff));
        if (callsOut)
        {
            code.AddRange(new byte[] { 0x50 });                 // push eax
            code.AddRange(new byte[] { 0xe8, 0, 0, 0, 0 });     // call bump@PLT
            text.Relocs.Add(new Relocation(code.Count - 4, "bump", -4, RelocKind.Plt32));
            code.AddRange(new byte[] { 0x83, 0xc4, 0x04 });     // add esp, 4
        }
        code.Add(0x5b);                                         // pop ebx
        code.Add(0xc3);                                         // ret
        int size = code.Count;
        text.Bytes.AddRange(code);
        o.Symbols.Add(new Symbol { Name = "answer", Section = text, Offset = 0, Size = size, IsFunction = true });

        int thunk = text.Bytes.Count;
        text.Bytes.AddRange(new byte[] { 0x8b, 0x1c, 0x24, 0xc3 });
        o.Symbols.Add(new Symbol { Name = "__x86.get_pc_thunk.bx", Section = text, Offset = thunk, Size = 4, IsFunction = true, Global = false });
        o.Symbols.Add(new Symbol { Name = "value", Section = data, Offset = 0, Size = 4, Global = false });
        if (callsOut)
        {
            o.Symbols.Add(new Symbol { Name = "bump", Section = null });
        }
        return o;
    }

    private static void RelocatedConstantRoots()
    {
        ObjectFile obj = Answer(false);
        Section constants = new(".data.rel.ro", SectionKind.Data) { Align = 4 };
        constants.Bytes.AddRange(new byte[4]);
        constants.Relocs.Add(new Relocation(0, "answer", 0, RelocKind.Abs32));
        obj.Sections.Add(constants);
        obj.Symbols.Add(new Symbol { Name = "immutable_address", Section = constants, Size = 4 });
        string path = Write("librelocated.so", Linker.LinkShared(new[] { ("relocated.o", obj) }, "librelocated.so"));
        (int code, string output) = Run("readelf", "-aW", path);
        Check(code == 0 && output.Contains(".data.rel.ro"), "relocated constants retain a mapped section", output);
        Check(!output.Contains("TEXTREL"), "loader writes require no text relocation", output);
        (code, output) = Run("nm", path);
        uint constant = Address(output, "D", "immutable_address");
        uint roots = Address(output, "A", "__data_start");
        uint value = Address(output, "d", "value");
        uint end = Address(output, "A", "_end");
        Check(constant != 0 && constant + 4 <= roots, "immutable relocation is before the root range", output);
        Check(value >= roots && value + 4 <= end, "ordinary mutable data remains a root", output);
    }

    private static void SharedObjectStructure()
    {
        byte[] so = Linker.LinkShared(new[] { ("answer.o", Answer(false)) }, "libanswer.so");
        string path = Write("libanswer.so", so);

        (int code, string output) = Run("readelf", "-a", path);
        Check(code == 0, "readelf -a accepts the shared object", output);
        Check(output.Contains("DYN (Shared object file)"), "it is ET_DYN", output);
        Check(Regex.IsMatch(output, @"SONAME.*libanswer\.so"), "DT_SONAME is set", output);
        Check(output.Contains("DYNAMIC") && output.Contains(".dynamic"), "PT_DYNAMIC and .dynamic are present", output);
        Check(!output.Contains("TEXTREL"), "no text relocations", output);
        Check(Regex.IsMatch(output, @"Symbol table '\.dynsym'"), "a dynamic symbol table exists", output);

        (code, output) = Run("readelf", "-d", path);
        Check(code == 0 && output.Contains("(SONAME)") && output.Contains("(HASH)") && output.Contains("(STRTAB)") && output.Contains("(SYMTAB)"), "readelf -d shows a complete dynamic array", output);
        Check(output.Contains("BIND_NOW") || output.Contains("(BIND_NOW)"), "binding is eager", output);

        (code, output) = Run("nm", "-D", path);
        Check(code == 0 && Regex.IsMatch(output, @"^\w+ T answer$", RegexOptions.Multiline), "nm -D shows the exported function", output);
        Check(!output.Contains("value"), "a private symbol is not exported", output);

        (code, output) = Run("ldd", path);
        Check(code == 0 && !output.Contains("not a dynamic executable"), "ldd accepts it", output);
    }

    private static void DynamicExecutable()
    {
        byte[] so = Linker.LinkShared(new[] { ("answer.o", Answer(false)) }, "libanswer.so");
        string lib = Write("libanswer.so", so);

        // _start: call answer; mov ebx,eax; mov eax,1; int 0x80
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0xe8, 0, 0, 0, 0, 0x89, 0xc3, 0xb8, 1, 0, 0, 0, 0xcd, 0x80 });
        text.Relocs.Add(new Relocation(1, "answer", -4, RelocKind.Plt32));
        o.Sections.Add(text);
        o.Symbols.Add(new Symbol { Name = "_start", Section = text, Offset = 0, Size = 14, IsFunction = true });
        o.Symbols.Add(new Symbol { Name = "answer", Section = null });

        byte[] exe = Linker.Link(new[] { ("main.o", o) }, "_start", new[] { lib });
        string path = Write("uses-libanswer", exe);

        (int code, string output) = Run("readelf", "-a", path);
        Check(code == 0, "readelf -a accepts the executable", output);
        Check(output.Contains("[Requesting program interpreter: /lib/ld-linux.so.2]"), "PT_INTERP names the system loader", output);
        Check(Regex.IsMatch(output, @"NEEDED.*libanswer\.so"), "DT_NEEDED names the library", output);
        Check(Regex.IsMatch(output, @"R_386_JUMP_SLOT\s+\w+\s+answer"), "the import is a JUMP_SLOT relocation", output);

        (code, output) = Run("ldd", path);
        Check(code == 0 && output.Contains("libanswer.so") && !output.Contains("not found"), "ldd resolves the library", output);

        Check(Exec(path) == 42, "the program runs under the system loader and exits 42");
    }

    /// <summary>
    /// The case the runtime needs: a library that calls a function another
    /// library exports, resolved by the system loader at load time.
    /// </summary>
    private static void TwoSharedObjects()
    {
        ObjectFile bump = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        // bump(x): mov eax,[esp+4]; inc eax; ret -- no globals, so no GOT.
        text.Bytes.AddRange(new byte[] { 0x8b, 0x44, 0x24, 0x04, 0x40, 0xc3 });
        bump.Sections.Add(text);
        bump.Symbols.Add(new Symbol { Name = "bump", Section = text, Offset = 0, Size = 6, IsFunction = true });
        string libBump = Write("libbump.so", Linker.LinkShared(new[] { ("bump.o", bump) }, "libbump.so"));
        string libAnswer = Write("libanswer3.so", Linker.LinkShared(new[] { ("answer.o", Answer(true)) }, "libanswer3.so", new[] { "libbump.so" }));

        (int code, string output) = Run("readelf", "-d", libAnswer);
        Check(code == 0 && Regex.IsMatch(output, @"NEEDED.*libbump\.so"), "the library records what it needs", output);

        ObjectFile main = new();
        Section mt = new(".text", SectionKind.Code) { Align = 16 };
        mt.Bytes.AddRange(new byte[] { 0xe8, 0, 0, 0, 0, 0x89, 0xc3, 0xb8, 1, 0, 0, 0, 0xcd, 0x80 });
        mt.Relocs.Add(new Relocation(1, "answer", -4, RelocKind.Plt32));
        main.Sections.Add(mt);
        main.Symbols.Add(new Symbol { Name = "_start", Section = mt, Offset = 0, Size = 14, IsFunction = true });
        main.Symbols.Add(new Symbol { Name = "answer", Section = null });

        string path = Write("uses-two-libs", Linker.Link(new[] { ("main.o", main) }, "_start", new[] { libAnswer, libBump }));
        (code, output) = Run("ldd", path);
        Check(code == 0 && output.Contains("libanswer3.so") && output.Contains("libbump.so") && !output.Contains("not found"), "both libraries are found", output);
        Check(Exec(path) == 43, "the call across two libraries returns 42 + 1");
    }

    private static void SharedObjectFromC()
    {
        string source = Path.Combine(_dir, "usesanswer.c");
        File.WriteAllText(source, """
            #include <stdio.h>
            int answer(void);
            int bump(int x) { return x + 1; }
            int main(void) { int a = answer(); printf("%d\n", a); return a == 43 ? 0 : 1; }
            """);
        byte[] so = Linker.LinkShared(new[] { ("answer.o", Answer(true)) }, "libanswer2.so");
        string lib = Write("libanswer2.so", so);

        string prog = Path.Combine(_dir, "c-uses-answer");
        (int code, string output) = Run("gcc", "-m32", "-o", prog, source, lib, "-Wl,-rpath," + _dir, "-Wl,--export-dynamic");
        if (code != 0)
        {
            Console.WriteLine($"  skip  gcc -m32 cannot link against our library here: {output.Split('\n')[0]}");
            return;
        }
        Check(true, "gcc -m32 links a C program against our shared object");
        (code, output) = Run(prog);
        Check(code == 0 && output.Trim() == "43", "the C program calls into the library and back out to its own symbol", output);
    }

    // ---- Harness ---------------------------------------------------------

    private static void Try(string name, Action test)
    {
        Console.WriteLine($"--- {name}");
        try
        {
            test();
        }
        catch (Exception e)
        {
            Check(false, $"threw {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Check(bool ok, string what, string? detail = null)
    {
        if (ok)
        {
            _passes++;
            Console.WriteLine($"  ok    {what}");
            return;
        }
        _failures++;
        Console.WriteLine($"  FAIL  {what}");
        if (detail is not null)
        {
            foreach (string line in detail.Split('\n').Take(40))
            {
                Console.WriteLine($"        {line}");
            }
        }
    }

    private static string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static uint Address(string nm, string type, string name)
    {
        Match m = Regex.Match(nm, $@"^(\w+) {type} {Regex.Escape(name)}$", RegexOptions.Multiline);
        return m.Success ? Convert.ToUInt32(m.Groups[1].Value, 16) : 0;
    }

    private static (int Code, string Output) Run(string program, params string[] args)
    {
        ProcessStartInfo psi = new(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {program}");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr);
    }

    /// <summary>Make it executable and run it; the exit status is what the test wants.</summary>
    private static int Exec(string path)
    {
        // The tests run i386 binaries under the host kernel; there is no
        // Windows story for that, so the guard is for the analyser only.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        (int code, string output) = Run(path);
        if (output.Length != 0)
        {
            Console.WriteLine($"        {path}: {output}");
        }
        return code;
    }
}
