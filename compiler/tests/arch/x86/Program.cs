#nullable enable
using System.Diagnostics;
using System.Text;
using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.X86;
using Corsac.Lang.Elf;
using Block = Corsac.Lang.Ir.Block;

// Hand-built IR through the x86 backend, with the bytes checked against
// objdump. Run from anywhere: `dotnet run --project compiler/tests/x86`.
// Exit code is the number of failed checks.

namespace Corsac.Tests.X86;

internal static class Program
{
    private static int _failures;

    private static void DeferredFunctions()
    {
        Module shells = new("deferred");
        Module eager = new("deferred");
        Function Load(int index)
        {
            Function f = new("deferred" + index, IrType.I32) { Exported = true };
            new Builder(f, f.NewBlock()).Ret(new ImmOperand(index, IrType.I32));
            return f;
        }
        for (int i = 0; i < 7; i++)
        {
            shells.Functions.Add(new Function("deferred" + i, IrType.Void) { Exported = true });
            eager.Functions.Add(Load(i));
        }
        List<string> errors = new();
        ObjectFile expected = new X86Backend { EmitLinkSummary = true }.Generate(eager, errors);
        foreach (int workers in new[] { 1, 4 })
        {
            int[] loads = new int[7];
            X86Backend backend = new()
            {
                Workers = workers, EmitLinkSummary = true, FunctionMemoryBudget = 200,
                FunctionLoadBytes = _ => 100,
                FunctionLoader = i => { System.Threading.Interlocked.Increment(ref loads[i]); return Load(i); },
            };
            ObjectFile actual = backend.Generate(shells, errors);
            Check(ElfWriter.WriteObject(actual).SequenceEqual(ElfWriter.WriteObject(expected)), "deferred codegen preserves object bytes");
            Check(loads.All(count => count == 1) && shells.Functions.All(f => f.Blocks.Count == 0), "deferred bodies loaded once and not retained in module");
            Check(backend.PeakBatchBytes <= 200 && backend.PeakBatchFunctions <= 2, "deferred worker window respects budget");
        }
        bool rejected = false;
        try { new X86Backend { FunctionLoader = Load, FunctionLoadBytes = _ => 201, FunctionMemoryBudget = 200 }.Generate(shells, errors); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && errors.Count == 0, "oversized deferred function rejected before loading");
    }

    private static int Main(string[] args)
    {
        if (args.Contains("--benchmark-packed")) return PackedMemoryBenchmarks.Run();
        Target.Current = Target.X86;
        Target.X86.X86Profile = X86Cpu.Parse(args);
        PruneArithmetic();
        DeferredFunctions();
        string outDir = Path.Combine(Path.GetTempPath(), "corsac-x86tests");
        Directory.CreateDirectory(outDir);

        Module m = new("tests");
        Add(m);
        Loop(m);
        Mul64(m);
        Float(m);
        Calls(m);
        Exit42(m);
        Pressure(m);
        Switch(m);
        Shift64(m);
        Cmp64(m);
        Landing(m);
        Evict(m);
        Convert(m);
        Bytes(m);
        SmallFills(m);
        PackedFrames(m);
        PackedArithmeticFrames(m);
        ByteSwaps(m);
        Exit(m);
        UDiv64(m);
        CmpZero(m);
        ConstBranch(m);
        DeadLoad(m);
        Remat(m);
        SpilledBool(m);
        CopyPerms(m);
        ByteWord(m);
        Mmap6(m);
        Ports(m);
        Start(m);
        m.Data.Add(new DataItem("greeting", Encoding.ASCII.GetBytes("hi\n")) { ReadOnly = true, Align = 1 });
        DataItem table = new("table", new byte[8]);
        table.Relocs.Add(new DataReloc(0, "sum", 0));
        table.Relocs.Add(new DataReloc(4, "greeting", 1));
        m.Data.Add(table);
        m.Data.Add(new DataItem("counter", new byte[4]) { Zero = true });
        m.Data.Add(new DataItem("procexe", Encoding.ASCII.GetBytes("/proc/self/exe\0")) { ReadOnly = true, Align = 1 });
        // Keeps the executable over two pages, so mmap6 has a page 1 to map.
        m.Data.Add(new DataItem("ballast", Enumerable.Range(0, 8192).Select(k => (byte)(k * 7 + 1)).ToArray()));

        X86Backend backend = new();
        string asm = backend.Assembly(m);
        bool usesBswap = Target.X86.X86Profile.Name != "386";
        Check(FunctionAsm(asm, "bswap32").Contains("bswap ") == usesBswap, "byte-swap instruction respects CPU profile");
        Check(FunctionAsm(asm, "bswap64_inplace").Contains("bswap ") == usesBswap, "wide byte-swap instruction respects CPU profile");
        Check(FunctionAsm(asm, "packed_frames").Contains("movq ") == Target.X86.X86Profile.Mmx, "packed frame operations respect MMX exclusion");
        Check(FunctionAsm(asm, "packed_frames").Contains("femms") == Target.X86.X86Profile.ThreeDNow, "packed frame exit respects 3DNow selection");
        Check(FunctionAsm(asm, "packed_arithmetic").Contains("paddb") == Target.X86.X86Profile.Mmx, "adjacent byte arithmetic is packed automatically");
        Check(FunctionAsm(asm, "packed_arithmetic").Contains("pmullw") == Target.X86.X86Profile.Mmx, "adjacent low-word products are packed automatically");
        File.WriteAllText(Path.Combine(outDir, "tests.asm"), asm);
        Console.WriteLine(asm);

        List<string> errors = new();
        ObjectFile obj = backend.Generate(m, errors);
        foreach (string e in errors)
        {
            Fail("backend error: " + e);
        }

        Section text = obj.Section(".text");
        string bin = Path.Combine(outDir, "text.bin");
        File.WriteAllBytes(bin, text.Bytes.ToArray());

        Console.WriteLine("---- symbols ----");
        foreach (Symbol s in obj.Symbols)
        {
            Console.WriteLine($"{s.Name,-24} {(s.Section?.Name ?? "UND"),-8} +{s.Offset,-5} size {s.Size,-4} {(s.Global ? "global" : "local")}{(s.IsFunction ? " func" : "")}");
        }
        Console.WriteLine("---- relocations ----");
        foreach (Section s in obj.Sections)
        {
            foreach (Relocation r in s.Relocs)
            {
                Console.WriteLine($"{s.Name} +{r.Offset:x4} {r.Kind} {r.Symbol}{(r.Addend != 0 ? $"{r.Addend:+0;-0}" : "")}");
            }
        }

        Console.WriteLine("---- objdump ----");
        string dump = Run("objdump", $"-D -b binary -m i386 -M intel {bin}");
        // Label the disassembly with function starts so it is readable.
        Dictionary<long, string> starts = obj.Symbols.Where(s => s.IsFunction).ToDictionary(s => s.Offset, s => s.Name);
        foreach (string line in dump.Split('\n'))
        {
            string t = line.TrimStart();
            int colon = t.IndexOf(':');
            if (colon > 0 && long.TryParse(t[..colon], System.Globalization.NumberStyles.HexNumber, null, out long at) && starts.TryGetValue(at, out string? name))
            {
                Console.WriteLine($"<{name}>:");
            }
            Console.WriteLine(line);
        }

        Check(!dump.Contains("(bad)"), "objdump decoded every byte");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(dump, @"\tnop\s*\n[^\n]*\tnop\s*\n"), "padding never uses two single-byte nops in a row");
        Check(obj.Symbols.Where(s => s.IsFunction).All(s => s.Offset % 4 == 0), "functions are 4-byte aligned");
        Check(backend.Statistics().Contains("  add\n") && backend.Statistics().Contains("total in"), "per-function code sizes are reported");
        Check(errors.Count == 0, "no backend errors");
        Check(obj.Section(".text").Relocs.Any(r => r.Symbol == "__udivdi3" && r.Kind == RelocKind.Rel32), "64-bit divide calls __udivdi3");
        Check(obj.Symbols.Any(s => s.Name == "table" && s.Section?.Name == ".data"), "table lands in .data");
        Check(obj.Symbols.Any(s => s.Name == "counter" && s.Section?.Name == ".bss"), "counter lands in .bss");
        Check(obj.Section(".data").Relocs.Count == 2, "data relocations converted");
        Check(obj.Section(".rodata").Relocs.Count >= 3, "jump table entries relocated");
        Check(!asm.Contains("cmov") && !asm.Contains("cpuid"), "no post-486 instructions");

        // Code-quality checks on the assembly text, one per known bad pattern.
        string cmpZero = FunctionAsm(asm, "cmpzero");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(cmpZero, @"xor \w+, 0\b"), "compare with zero emits no xor r, 0");
        Check(System.Text.RegularExpressions.Regex.IsMatch(cmpZero, @"test (\w+), \1") && !cmpZero.Contains("cmp "), "32-bit compare with zero is a test");
        Check(System.Text.RegularExpressions.Regex.IsMatch(cmpZero, @"or \w+, \w+"), "64-bit compare with zero is an or of the halves");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(asm, @"mov (\[ebp-\d+\]), (\w+)\n\s+mov \2, \1\n"), "no reload straight after a store to the same slot");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(asm, @"mov (\w+), \[ebp-\d+\]\n\s+xor \1, \1\n"), "no reload before a register is zeroed");
        string exit42 = FunctionAsm(asm, "exit42");
        Check(exit42.Contains("mov ebx, 42") && exit42.Contains("mov eax, 1") && !exit42.Contains("mov ebx, e"), "syscall immediates go straight to their registers");
        string constBranch = FunctionAsm(asm, "constbranch");
        Check(!constBranch.Contains("test") && !System.Text.RegularExpressions.Regex.IsMatch(constBranch, @"\n\s+j(?!mp)[a-z]{1,2} \."), "branch on a constant is folded");
        string deadLoad = FunctionAsm(asm, "deadload");
        Check(deadLoad.Split("[counter]").Length == 2, "a load overwritten before use is dropped");
        string addAsm = FunctionAsm(asm, "add");
        Check(!addAsm.Contains("sub esp") && !addAsm.Contains("push ebx"), "a leaf with no locals has a minimal prologue");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(FunctionAsm(asm, "sum"), @"j\w+ \.\w+\n\s+jmp"), "conditional jump around a jump is inverted");

        Check(!System.Text.RegularExpressions.Regex.IsMatch(asm, @"\n\s+mov e\w\w, 0\n"), "zeroing a register uses xor");
        string remat = FunctionAsm(asm, "remat");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(remat, @"mov (\w+), 77\n\s+mov \[ebp-\d+\], \1"), "a spilled constant is rematerialised, not stored");
        Check(remat.Contains(", 77"), "a rematerialised constant folds into its use");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(asm, @"mov (e\w\w), (e\w\w)\n\s+mov \[ebp-\d+\], \1\n\s+(xor|mov) \1,"), "a copy feeding only a store is forwarded");

        StackMaps(obj);

        int expectedFunctions = m.Functions.Count;
        Check(obj.Symbols.Count(s => s.IsFunction) == expectedFunctions, "one symbol per function");

        // End to end: link a static executable and run it. _start exits 42
        // when every runtime check passes, else the 1-based number of the
        // first Expect that failed.
        try
        {
            byte[] exe = Linker.Link(new[] { obj }, "_start");
            string exePath = Path.Combine(outDir, "e2e");
            File.WriteAllBytes(exePath, exe);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(exePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            string? emulation = args.FirstOrDefault(argument => argument.StartsWith("--qemu-cpu="));
            ProcessStartInfo start = new(emulation is null ? exePath : "qemu-i386") { RedirectStandardOutput = true };
            if (emulation is not null) { start.ArgumentList.Add("-cpu"); start.ArgumentList.Add(emulation[11..]); start.ArgumentList.Add(exePath); }
            using Process p = Process.Start(start)!;
            if (!p.WaitForExit(30000)) { p.Kill(true); p.WaitForExit(); throw new InvalidOperationException("Generated-code execution timed out"); }
            Check(p.ExitCode == 42, $"end-to-end executable exits 42 (got {p.ExitCode}{(p.ExitCode is > 0 and < 42 ? $": runtime check {p.ExitCode} (1-based, in Start) failed" : "")})");
        }
        catch (Exception e) when (e is LinkException or InvalidOperationException)
        {
            Fail("link failed: " + e.Message);
        }

        // Spot checks of encodings that are easy to get wrong.
        CheckDump(dump, "push   ebp", "prologue push");
        CheckDump(dump, "mov    ebp,esp", "prologue mov");
        CheckDump(dump, "leave", "epilogue leave");
        CheckDump(dump, "int    0x80", "syscall trap");
        CheckDump(dump, "cdq", "signed divide prep");
        CheckDump(dump, "fstp   QWORD PTR", "x87 store");
        CheckDump(dump, "shld", "64-bit shift");
        CheckDump(dump, "sbb", "64-bit compare");
        CheckDump(dump, "jmp    DWORD PTR [e", "jump table dispatch");

        // The driver and kernel instructions. Nothing here can run on Linux
        // in user mode -- every one of them faults -- so what is tested is
        // the encoding: objdump's decoding of the bytes, and the assembly
        // dump beside it, for the one function that contains them.
        string ports = FunctionAsm(asm, "ports");
        Check(ports.Length > 0, "the port-I/O function reached the assembly dump");
        foreach (string line in new[]
        {
            "in al, dx", "in ax, dx", "in eax, dx",
            "out dx, al", "out dx, ax", "out dx, eax",
            "rep insw", "rep outsw",
            "cli", "sti", "hlt",
            "lgdt [", "lidt [", "invlpg [",
            "mov eax, cr0", "mov cr3,", "mov ds, ax", "mov ss, ax",
        })
        {
            Check(ports.Contains(line), $"--asm prints '{line}'");
        }
        // The narrow reads zero the whole of EAX first, because IN AL leaves
        // the other three bytes as they were and the language promises a
        // canonical unsigned byte.
        Check(System.Text.RegularExpressions.Regex.IsMatch(ports, @"xor eax, eax
\s+in al, dx"), "a byte port read zeroes EAX first");
        Check(System.Text.RegularExpressions.Regex.IsMatch(ports, @"mov edx, 1016
\s+xor eax, eax
\s+in al, dx"), "the port goes to DX before the read");

        string portBytes = FunctionDump(dump, obj, "ports");
        Check(portBytes.Length > 0, "the port-I/O function was found in the disassembly");
        foreach ((string bytes, string what) in new[]
        {
            ("ec", "in al,dx"),
            ("66 ed", "in ax,dx"),
            ("ed", "in eax,dx"),
            ("ee", "out dx,al"),
            ("66 ef", "out dx,ax"),
            ("ef", "out dx,eax"),
            ("f3 66 6d", "rep insw"),
            ("f3 66 6f", "rep outsw"),
            ("fa", "cli"),
            ("fb", "sti"),
            ("f4", "hlt"),
            ("0f 01 10", "lgdt [eax]"),
            ("0f 01 18", "lidt [eax]"),
            ("0f 01 38", "invlpg [eax]"),
            ("0f 20 c0", "mov eax,cr0"),
            ("0f 22 d8", "mov cr3,eax"),
            ("8e d8", "mov ds,ax"),
        })
        {
            Check(portBytes.Contains("\t" + bytes + " "), $"encoding of {what} is {bytes}");
        }
        // objdump has to agree about what the bytes mean, not just that they
        // are there: a wrong prefix would still be bytes.
        CheckDump(portBytes, "in     al,dx", "objdump reads the byte port read");
        CheckDump(portBytes, "in     ax,dx", "objdump reads the word port read");
        CheckDump(portBytes, "in     eax,dx", "objdump reads the dword port read");
        CheckDump(portBytes, "out    dx,al", "objdump reads the byte port write");
        CheckDump(portBytes, "rep ins WORD PTR", "objdump reads rep insw");
        CheckDump(portBytes, "rep outs dx,WORD PTR", "objdump reads rep outsw");
        CheckDump(portBytes, "cli", "objdump reads cli");
        CheckDump(portBytes, "hlt", "objdump reads hlt");
        CheckDump(portBytes, "mov    eax,cr0", "objdump reads a control-register read");
        CheckDump(portBytes, "mov    cr3,eax", "objdump reads a control-register write");

        Flat();

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures;
    }

    /// <summary>
    /// The flat output: what a machine with no loader is given.
    ///
    /// Its own little module, because what is being tested is the layout and
    /// the layout depends on which function comes first -- a flat image is
    /// entered at its first byte, and the linker refuses one whose entry is
    /// anywhere else.
    /// </summary>
    private static void Flat()
    {
        Module m = new("flat");
        (Function entry, Builder be) = New(m, "boot", IrType.Void);
        be.Call("helper", IrType.Void, new SymOperand("message"));
        be.Store(new SymOperand("flag"), I(1));
        be.Unreachable();
        _ = entry;
        (Function helper, Builder bh) = New(m, "helper", IrType.Void, IrType.I32);
        bh.Store(new SymOperand("slot"), R(helper.Params[0]));
        bh.Ret();
        m.Data.Add(new DataItem("message", Encoding.ASCII.GetBytes("bare\n")) { ReadOnly = true, Align = 1 });
        m.Data.Add(new DataItem("slot", new byte[] { 1, 2, 3, 4 }));
        m.Data.Add(new DataItem("flag", new byte[8]) { Zero = true });

        List<string> errors = new();
        ObjectFile obj = new X86Backend().Generate(m, errors);
        Check(errors.Count == 0, "the flat module generates without error");

        const uint at = 0x10000;
        Linker.FlatImage image;
        try
        {
            image = Linker.LinkFlat(new[] { ("flat", obj) }, "boot", at);
        }
        catch (LinkException e)
        {
            Fail("flat link failed: " + e.Message);
            return;
        }

        uint text = (uint)obj.Section(".text").Size;
        Check(image.Base == at, "the flat image is based where it was asked for");
        Check(image.Entry == at, "a flat image is entered at its first byte");
        Check(image.TextSize == text, "the flat image's code is all of .text");
        Check(image.BssSize == 8, "the .bss a loader has to zero is reported, not written");
        // Code, constants and data and nothing else, give or take each
        // section's own alignment -- there is no page-sized hole between the
        // read-only group and the writable one, which is the whole difference
        // from the ELF layout.
        uint parts = image.TextSize + image.ReadOnlySize + image.DataSize;
        Check(image.Bytes.Length >= parts && image.Bytes.Length < parts + 32,
              $"the flat image is code, constants and data and nothing else ({image.Bytes.Length} for {parts})");
        Check(image.MemorySize == image.Bytes.Length + image.BssSize, "the memory an image occupies includes its .bss");
        // No headers: the first byte of the file is the first byte of code.
        Check(image.Bytes[0] == 0x55, "the flat image begins with the entry's prologue and no header");
        // The sections follow each other with no page-sized hole: the string
        // is inside the image, not a page further on.
        int found = IndexOf(image.Bytes, Encoding.ASCII.GetBytes("bare\n"));
        Check(found >= (int)text && found < image.Bytes.Length, "the constants sit immediately after the code");
        Check(IndexOf(image.Bytes, new byte[] { 1, 2, 3, 4 }) >= found, "initialised data follows the constants");
        // The call's relocation was applied against the flat addresses, so
        // the absolute reference to `message` names where it really is.
        uint messageAddr = at + (uint)found;
        Check(IndexOf(image.Bytes, BitConverter.GetBytes(messageAddr)) >= 0,
              $"a reference to a constant resolved to its flat address 0x{messageAddr:x}");

        // And an entry that is not first is refused rather than quietly
        // producing an image that runs the wrong function.
        try
        {
            Linker.LinkFlat(new[] { ("flat", obj) }, "helper", at);
            Fail("a flat image whose entry is not first is refused");
        }
        catch (LinkException)
        {
            Check(true, "a flat image whose entry is not first is refused");
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k])
            {
                k++;
            }
            if (k == needle.Length)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The disassembly of one function, found by its symbol's offset and
    /// ending at the next function's. The dump is of a raw .text image, so
    /// the addresses are offsets from the start of the section.
    /// </summary>
    private static string FunctionDump(string dump, ObjectFile obj, string name)
    {
        Symbol? sym = obj.Symbols.Find(x => x.IsFunction && x.Name == name);
        if (sym is null)
        {
            return "";
        }
        List<string> kept = new();
        foreach (string line in dump.Split('\n'))
        {
            string t = line.TrimStart();
            int colon = t.IndexOf(':');
            if (colon <= 0 || !long.TryParse(t[..colon], System.Globalization.NumberStyles.HexNumber, null, out long at))
            {
                continue;
            }
            if (at >= sym.Offset && at < sym.Offset + sym.Size)
            {
                kept.Add(t);
            }
        }
        return string.Join('\n', kept);
    }

    /// <summary>The assembly text of one function, from its label to the next global line.</summary>
    private static string FunctionAsm(string asm, string name)
    {
        int at = asm.IndexOf("\n" + name + ":\n", StringComparison.Ordinal);
        if (at < 0)
        {
            return "";
        }
        int end = asm.IndexOf("\nglobal ", at + 1, StringComparison.Ordinal);
        return end < 0 ? asm[at..] : asm[at..end];
    }

    /// <summary>
    /// The stack-map table: header, one entry per call, and a slot bitmap
    /// per entry that has live references in the frame.
    ///
    /// `pressure` keeps ten values live across a call, more than the three
    /// callee-saved registers, so its entry is the one that proves both
    /// halves of the format: a register mask AND a bitmap of frame slots.
    /// </summary>
    private static void StackMaps(ObjectFile obj)
    {
        Section? s = obj.Sections.Find(x => x.Name == X86Backend.StackMapSection);
        if (s is null)
        {
            Fail("the stack-map section exists");
            return;
        }

        byte[] b = s.Bytes.ToArray();
        uint W(int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

        Check(W(0) == 0x314d5343, "stack maps begin with the CSM1 magic");
        Check(W(4) == 1, "stack maps are version 1");
        Check(W(12) == 16, "a stack-map entry is sixteen bytes");

        int count = (int)W(8);
        Check(count > 0 && 16 + count * 16 <= b.Length, "the entry count fits the section");
        Check(s.Relocs.Count == count, "every entry's return address is a relocation");
        Check(obj.Symbols.Any(y => y.Name == X86Backend.StackMapStart && y.Offset == 0 && !y.Global)
            && obj.Symbols.Any(y => y.Name == X86Backend.StackMapEnd && y.Offset == b.Length && !y.Global),
            "object-local start and end symbols bracket the table");

        Dictionary<string, (uint Regs, int Map, uint Frame)> byFunction = new(StringComparer.Ordinal);
        bool bitmapsInRange = true;
        for (int i = 0; i < count; i++)
        {
            int at = 16 + i * 16;
            uint regs = W(at + 4);
            int map = (int)W(at + 8);
            if (map != 0 && (map + 4 > b.Length || map + 4 + (int)W(map) * 4 > b.Length))
            {
                bitmapsInRange = false;
            }
            // Only EBX, ESI and EDI survive a call, so no other bit may be set.
            Check((regs & ~0b1100_1000u) == 0, "a stack map names only callee-saved registers");
            string owner = s.Relocs[i].Symbol;
            if (!byFunction.ContainsKey(owner) || regs != 0 || map != 0)
            {
                byFunction[owner] = (regs, map, W(at + 12));
            }
        }
        Check(bitmapsInRange, "every bitmap offset lies inside the table");

        if (byFunction.TryGetValue("pressure", out (uint Regs, int Map, uint Frame) p))
        {
            Check(p.Regs != 0, "the pressure call site keeps references in callee-saved registers");
            Check(p.Map != 0, "the pressure call site has a frame bitmap");
            Check(p.Frame >= 4, "the pressure call site records its frame size");
            if (p.Map != 0)
            {
                int words = (int)W(p.Map);
                int bits = 0;
                for (int k = 0; k < words; k++)
                {
                    bits += System.Numerics.BitOperations.PopCount(W(p.Map + 4 + k * 4));
                }
                Check(bits > 0, "the pressure bitmap marks at least one slot");
                Check(words * 32 * 4 <= p.Frame + 128, "the bitmap covers no more than the frame it describes");
            }
        }
        else
        {
            Fail("pressure has a stack map");
        }

        // A function with no call has no entry, and every entry names a
        // function this object defines.
        Check(s.Relocs.All(r => obj.Symbols.Any(y => y.Name == r.Symbol && y.IsFunction)), "every entry names a function in this object");
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"[{(ok ? "ok" : "FAIL")}] {what}");
        if (!ok)
        {
            _failures++;
        }
    }

    private static void CheckDump(string dump, string needle, string what) => Check(dump.Contains(needle), $"{what}: '{needle}'");

    private static void Fail(string what) => Check(false, what);

    private static string Run(string exe, string args)
    {
        ProcessStartInfo psi = new(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o + e;
    }

    // ---- the functions -----------------------------------------------------------

    /// <summary>What pressure(p) computes, in C#, so the runtime check has a number to compare with.</summary>
    private static int PressureExpected(int p)
    {
        int[] vs = Enumerable.Range(0, 10).Select(k => p * (k + 3)).ToArray();
        int acc = vs[0];
        for (int k = 1; k < 10; k++)
        {
            acc ^= vs[k];
        }
        int d = acc / p;
        int r = (int)((uint)d % (uint)vs[9]);
        int sh = r >> (vs[8] & 31);
        return (sbyte)sh;
    }

    /// <summary>What landing(nonzero) does to the counter: an atomic add of one, then an atomic or of the value read.</summary>
    private static int LandingExpected(ref int counter)
    {
        int old = counter;
        counter += 1;
        int seen = counter;
        counter |= old;
        return seen;
    }

    private static int EvictExpected(int p)
    {
        int t = 0;
        for (int i = 0; i < p; i++)
        {
            t = (t + i) * 3;
        }
        int sum = 0;
        for (int k = 0; k < 8; k++)
        {
            sum += p * (k + 1);
        }
        return sum + t;
    }

    private static (Function F, Builder B) New(Module m, string name, IrType ret, params IrType[] ps)
    {
        Function f = new(name, ret);
        foreach (IrType t in ps)
        {
            f.Params.Add(f.NewReg(t, $"p{f.Params.Count}"));
        }
        Block entry = f.NewBlock("entry");
        m.Functions.Add(f);
        return (f, new Builder(f, entry));
    }

    /// <summary>
    /// Every instruction a driver or a bootloader needs and no ordinary code
    /// can express. Never called: each one of these faults in user mode, so
    /// the function exists to be encoded and disassembled, not to run.
    ///
    /// The reserved callee names are the seam between lowering and selection
    /// -- lowering emits a call, the selector emits the instruction -- and
    /// building them directly here tests the selector without dragging the
    /// whole front end into this project.
    /// </summary>
    private static void Ports(Module m)
    {
        (Function f, Builder b) = New(m, "ports", IrType.I32, IrType.I32);
        const string p = "__x86.i.";
        VReg byteIn = b.Call(p + "in8", IrType.I32, I(0x3F8))!;
        b.Call(p + "out8", IrType.Void, I(0x3F8), R(byteIn));
        VReg wordIn = b.Call(p + "in16", IrType.I32, I(0x1F0))!;
        b.Call(p + "out16", IrType.Void, I(0x1F0), R(wordIn));
        VReg longIn = b.Call(p + "in32", IrType.I32, I(0xCFC))!;
        b.Call(p + "out32", IrType.Void, I(0xCF8), R(longIn));
        b.Call(p + "insw", IrType.Void, I(0x1F0), R(f.Params[0]), I(256));
        b.Call(p + "outsw", IrType.Void, I(0x1F0), R(f.Params[0]), I(256));
        b.Call(p + "cli", IrType.Void);
        b.Call(p + "sti", IrType.Void);
        b.Call(p + "hlt", IrType.Void);
        b.Call(p + "lgdt", IrType.Void, R(f.Params[0]));
        b.Call(p + "lidt", IrType.Void, R(f.Params[0]));
        b.Call(p + "invlpg", IrType.Void, R(f.Params[0]));
        VReg cr0 = b.Call(p + "readcr", IrType.I32, I(0))!;
        b.Call(p + "writecr", IrType.Void, I(0), R(b.Binary(Opcode.Or, cr0, 1)));
        b.Call(p + "writecr", IrType.Void, I(3), R(f.Params[0]));
        b.Call(p + "loadsegments", IrType.Void, I(0x10));
        b.Ret(R(b.Binary(Opcode.Add, b.Binary(Opcode.Add, byteIn, wordIn), longIn)));
    }

    private static RegOperand R(VReg v) => new(v);
    private static ImmOperand I(long v, IrType t = IrType.I32) => new(v, t);

    /// <summary>int add(int a, int b) { return a + b; }</summary>
    private static void Add(Module m)
    {
        (Function f, Builder b) = New(m, "add", IrType.I32, IrType.I32, IrType.I32);
        VReg s = b.Binary(Opcode.Add, f.Params[0], f.Params[1]);
        b.Ret(R(s));
    }

    /// <summary>int sum(int n) { int s = 0; for (i = 0; i &lt; n; i++) s += i; return s; }</summary>
    private static void Loop(Module m)
    {
        (Function f, Builder b) = New(m, "sum", IrType.I32, IrType.I32);
        VReg s = b.Reg(IrType.I32, "s");
        VReg i = b.Reg(IrType.I32, "i");
        b.CopyTo(s, I(0));
        b.CopyTo(i, I(0));
        Block head = f.NewBlock("head");
        Block body = f.NewBlock("body");
        Block exit = f.NewBlock("exit");
        b.Jump(head);
        b.SetBlock(head);
        VReg c = b.Binary(Opcode.LtS, i, f.Params[0]);
        b.Branch(c, body, exit);
        b.SetBlock(body);
        b.CopyTo(s, R(b.Binary(Opcode.Add, s, i)));
        b.CopyTo(i, R(b.Binary(Opcode.Add, i, 1)));
        b.Jump(head);
        b.SetBlock(exit);
        b.Ret(R(s));
    }

    /// <summary>long mul64(long a, long b) { return a * b / 3; } -- also exercises the helper call.</summary>
    private static void Mul64(Module m)
    {
        (Function f, Builder b) = New(m, "mul64", IrType.I64, IrType.I64, IrType.I64);
        VReg p = b.Binary(Opcode.Mul, f.Params[0], f.Params[1]);
        VReg q = b.Binary(Opcode.DivU, p, 3);
        b.Ret(R(q));
    }

    /// <summary>double fma(double a, double b, int c) { return a * b + (double)c; } with a compare too.</summary>
    private static void Float(Module m)
    {
        (Function f, Builder b) = New(m, "fma", IrType.F64, IrType.F64, IrType.F64, IrType.I32);
        VReg p = b.Binary(Opcode.FMul, f.Params[0], f.Params[1]);
        VReg c = b.Unary(Opcode.IToF, R(f.Params[2]), IrType.F64);
        VReg s = b.Binary(Opcode.FAdd, p, c);
        VReg lt = b.Binary(Opcode.FLt, s, f.Params[0]);
        Block yes = f.NewBlock("yes");
        Block no = f.NewBlock("no");
        b.Branch(lt, yes, no);
        b.SetBlock(yes);
        VReg t = b.Unary(Opcode.FToI, R(s), IrType.I32);
        VReg back = b.Unary(Opcode.IToF, R(t), IrType.F64);
        b.Ret(R(back));
        b.SetBlock(no);
        VReg sq = b.Unary(Opcode.FSqrt, s);
        b.Ret(R(sq));
    }

    /// <summary>int calls(int x) { return add(x, 42) + sum(x) + (*fp)(x); } with a load from a static.</summary>
    private static void Calls(Module m)
    {
        (Function f, Builder b) = New(m, "calls", IrType.I32, IrType.I32);
        VReg a = b.Call("add", IrType.I32, R(f.Params[0]), I(42))!;
        VReg s = b.Call("sum", IrType.I32, R(f.Params[0]))!;
        VReg t = b.Binary(Opcode.Add, a, s);
        VReg fp = b.Load(IrType.I32, new SymOperand("table"));
        VReg r = b.CallIndirect(R(fp), IrType.I32, new Operand[] { R(f.Params[0]) })!;
        VReg u = b.Binary(Opcode.Add, t, r);
        b.Ret(R(u));
    }

    /// <summary>void exit42() { syscall(1, 42); }</summary>
    private static void Exit42(Module m)
    {
        (Function f, Builder b) = New(m, "exit42", IrType.Void);
        b.Syscall(I(1), new Operand[] { I(42) });
        b.Unreachable();
        _ = f;
    }

    /// <summary>Ten values live at once across a call: forces spills and callee-saved use.</summary>
    private static void Pressure(Module m)
    {
        (Function f, Builder b) = New(m, "pressure", IrType.I32, IrType.I32);
        List<VReg> vs = new();
        for (int k = 0; k < 10; k++)
        {
            vs.Add(b.Binary(Opcode.Mul, f.Params[0], k + 3));
        }
        b.Call("sum", IrType.Void, R(f.Params[0]));
        VReg acc = vs[0];
        for (int k = 1; k < 10; k++)
        {
            acc = b.Binary(Opcode.Xor, acc, vs[k]);
        }
        VReg d = b.Binary(Opcode.DivS, acc, f.Params[0]);
        VReg r = b.Binary(Opcode.RemU, d, vs[9]);
        VReg sh = b.Binary(Opcode.ShrS, r, vs[8]);
        VReg by = b.Unary(Opcode.SExt8, sh);
        b.Store(new SymOperand("counter"), R(by), 0, 1);
        b.Ret(R(by));
    }

    /// <summary>int sw(int k) { switch (k) { case 0: return 10; case 1: return 20; case 2: return 30; default: return -1; } }</summary>
    private static void Switch(Module m)
    {
        (Function f, Builder b) = New(m, "sw", IrType.I32, IrType.I32);
        Block c0 = f.NewBlock("c0");
        Block c1 = f.NewBlock("c1");
        Block c2 = f.NewBlock("c2");
        Block d = f.NewBlock("dflt");
        b.Switch(R(f.Params[0]), new[] { c0, c1, c2 }, d);
        b.SetBlock(c0);
        b.Ret(I(10));
        b.SetBlock(c1);
        b.Ret(I(20));
        b.SetBlock(c2);
        b.Ret(I(30));
        b.SetBlock(d);
        b.Ret(I(-1));
    }

    /// <summary>long sh(long a, int n) { return (a &lt;&lt; n) + (a &gt;&gt; 40) + ((ulong)a &gt;&gt; n); }</summary>
    private static void Shift64(Module m)
    {
        (Function f, Builder b) = New(m, "sh64", IrType.I64, IrType.I64, IrType.I32);
        VReg x = b.Binary(Opcode.Shl, R(f.Params[0]), R(f.Params[1]), IrType.I64);
        VReg y = b.Binary(Opcode.ShrS, f.Params[0], 40);
        VReg z = b.Binary(Opcode.ShrU, R(f.Params[0]), R(f.Params[1]), IrType.I64);
        VReg s = b.Binary(Opcode.Add, x, y);
        VReg t = b.Binary(Opcode.Add, s, z);
        VReg n = b.Unary(Opcode.Neg, t);
        b.Ret(R(n));
    }

    /// <summary>int cmp64(long a, long b) { return a &lt; b ? 1 : (a == b) + (int)(a &gt; b unsigned); }</summary>
    private static void Cmp64(Module m)
    {
        (Function f, Builder b) = New(m, "cmp64", IrType.I32, IrType.I64, IrType.I64);
        VReg lt = b.Binary(Opcode.LtS, f.Params[0], f.Params[1]);
        Block yes = f.NewBlock("yes");
        Block no = f.NewBlock("no");
        b.Branch(lt, yes, no);
        b.SetBlock(yes);
        b.Ret(I(1));
        b.SetBlock(no);
        VReg eq = b.Binary(Opcode.Eq, f.Params[0], f.Params[1]);
        VReg gt = b.Binary(Opcode.GtU, f.Params[0], f.Params[1]);
        VReg s = b.Binary(Opcode.Add, eq, gt);
        VReg w = b.Unary(Opcode.SExt32, s);
        VReg tr = b.Unary(Opcode.Trunc64, w);
        b.Ret(R(tr));
    }

    /// <summary>Eight values live across a loop that needs registers of its own: the allocator must evict.</summary>
    private static void Evict(Module m)
    {
        (Function f, Builder b) = New(m, "evict", IrType.I32, IrType.I32);
        List<VReg> vs = new();
        for (int k = 0; k < 8; k++)
        {
            vs.Add(b.Binary(Opcode.Mul, f.Params[0], k + 1));
        }
        VReg t = b.Reg(IrType.I32, "t");
        VReg i = b.Reg(IrType.I32, "i");
        b.CopyTo(t, I(0));
        b.CopyTo(i, I(0));
        Block head = f.NewBlock("head");
        Block body = f.NewBlock("body");
        Block exit = f.NewBlock("exit");
        b.Jump(head);
        b.SetBlock(head);
        b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), body, exit);
        b.SetBlock(body);
        b.CopyTo(t, R(b.Binary(Opcode.Mul, b.Binary(Opcode.Add, t, i), 3)));
        b.CopyTo(i, R(b.Binary(Opcode.Add, i, 1)));
        b.Jump(head);
        b.SetBlock(exit);
        VReg acc = t;
        foreach (VReg v in vs)
        {
            acc = b.Binary(Opcode.Add, acc, v);
        }
        b.Ret(R(acc));
    }

    /// <summary>uint convert(uint x) { return (uint)((double)x / 65536.0); } plus an unsigned 64-bit round trip.</summary>
    private static void Convert(Module m)
    {
        (Function f, Builder b) = New(m, "convert", IrType.I32, IrType.I32);
        VReg d = b.Unary(Opcode.UToF, R(f.Params[0]), IrType.F64);
        VReg k = b.Unary(Opcode.IToF, I(65536), IrType.F64);
        VReg q = b.Binary(Opcode.FDiv, d, k);
        VReg r = b.Unary(Opcode.FToU, R(q), IrType.I32);
        // (ulong)0xFFFFFFFF00000000 as a double is 2^64 - 2^32; back to u64 and >> 32 gives 0xFFFFFFFF... too big to
        // survive fistp, so use a value below 2^63 with the top bit of the low half set instead.
        VReg big = b.Const(0x7FFFFFFF80000000L, IrType.I64);
        VReg bf = b.Unary(Opcode.UToF, R(big), IrType.F64);
        VReg half = b.Binary(Opcode.FDiv, bf, b.Unary(Opcode.IToF, I(2), IrType.F64));
        VReg back = b.Unary(Opcode.FToU, R(half), IrType.I64);
        VReg hi = b.Unary(Opcode.Trunc64, R(b.Binary(Opcode.ShrU, R(back), I(32), IrType.I64)), IrType.I32);
        VReg ok = b.Binary(Opcode.Eq, hi, 0x3FFFFFFF);
        VReg f32 = b.Unary(Opcode.FConv, R(k), IrType.F32);
        VReg bits = b.Unary(Opcode.Bits, R(f32), IrType.I32);
        VReg isF = b.Binary(Opcode.Eq, bits, 0x47800000);  // 65536.0f
        VReg both = b.Binary(Opcode.And, ok, isF);
        VReg res = b.Binary(Opcode.Mul, r, both);
        b.Ret(R(res));
    }

    /// <summary>Fill a buffer, copy it, read a byte back through narrow loads and stores.</summary>
    private static void PruneArithmetic()
    {
        foreach (string mode in new[] { "dead", "live-flags", "carry-input", "zero-shift", "rotate-flags",
            "memory", "division", "physical", "boundary", "live-value", "bswap-flags" })
        {
            MFunction machine = new(new Function("prune-" + mode, IrType.I32));
            MBlock block = new("entry"); machine.Blocks.Add(block);
            MReg r = machine.NewReg(), other = machine.NewReg(), condition = machine.NewReg();
            MReg eax = MReg.Of(Gpr.Eax);
            machine.ByteRegs.Add(condition.Id);
            block.Instrs.Add(new MInstr(MOp.Mov, eax, new MImm(7)));
            block.Instrs.Add(new MInstr(MOp.Mov, r, new MImm(17)));
            block.Instrs.Add(new MInstr(MOp.Mov, other, new MImm(3)));
            MInstr operation = mode == "division" ? new MInstr(MOp.Div, r)
                : new MInstr(MOp.Add, mode == "physical" ? eax : r,
                    mode == "memory" ? new MMem(eax, 0) : new MImm(1));
            operation.Line = 123456; block.Instrs.Add(operation);
            if (mode == "carry-input") block.Instrs.Add(new MInstr(MOp.Adc, other, new MImm(0)));
            if (mode == "zero-shift") block.Instrs.Add(new MInstr(MOp.Shl, other, new MImm(0)));
            if (mode == "rotate-flags") block.Instrs.Add(new MInstr(MOp.Ror, other, new MImm(1)));
            if (mode == "bswap-flags") block.Instrs.Add(new MInstr(MOp.Bswap, other));
            if (mode is "live-flags" or "zero-shift" or "rotate-flags" or "bswap-flags")
            {
                block.Instrs.Add(new MInstr(MOp.Setcc, condition) { Width = 1, Cond = mode == "rotate-flags" ? Cond.E : Cond.B });
                block.Instrs.Add(new MInstr(MOp.Movzx, condition, condition) { Width = 1 });
                block.Instrs.Add(new MInstr(MOp.Mov, eax, condition));
            }
            else
            {
                if (mode != "boundary") block.Instrs.Add(new MInstr(MOp.Cmp, eax, eax));
                block.Instrs.Add(new MInstr(MOp.Mov, eax, mode == "live-value" ? r : mode == "carry-input" ? other : new MImm(0)));
            }
            block.Instrs.Add(new MInstr(MOp.Ret));
            Allocator.Run(machine);
            Check(machine.Blocks.SelectMany(b => b.Instrs).Any(i => i.Line == 123456 && i.Op == operation.Op)
                == (mode != "dead"), "preallocation dead arithmetic guard: " + mode);
        }
    }

    private static void ByteSwaps(Module m)
    {
        (Function narrow, Builder nb) = New(m, "bswap32", IrType.I32, IrType.I32);
        nb.Ret(R(nb.Unary(Opcode.ByteSwap, narrow.Params[0])));
        (Function wide, Builder wb) = New(m, "bswap64_inplace", IrType.I64, IrType.I64);
        wb.Emit(Opcode.ByteSwap, wide.Params[0], R(wide.Params[0]));
        wb.Ret(R(wide.Params[0]));
    }

    private static void PackedFrames(Module m)
    {
        (Function function, Builder b) = New(m, "packed_frames", IrType.I32);
        VReg okay = b.Const(1, IrType.I32);
        FrameSlot source = function.NewSlot(144, 8, "source"), destination = function.NewSlot(144, 8, "destination");
        foreach (int length in new[] { 0, 1, 31, 32, 33, 63, 64, 65, 127, 128, 129 })
        {
            for (int offset = 0; offset < 144; offset++)
            {
                b.Store(new SlotOperand(source), I((offset * 37 + 19) & 255), offset, 1);
                b.Store(new SlotOperand(destination), I(0xa5), offset, 1);
            }
            b.Emit(Opcode.MemCopy, null, new SlotOperand(destination), new SlotOperand(source), I(length));
            for (int offset = 0; offset < 144; offset++)
            {
                VReg actual = b.Load(IrType.I32, new SlotOperand(destination), offset, 1, false);
                okay = b.Binary(Opcode.And, okay, b.Binary(Opcode.Eq, actual, offset < length ? (offset * 37 + 19) & 255 : 0xa5));
            }
            b.Emit(Opcode.MemSet, null, new SlotOperand(destination), I(0), I(length));
            for (int offset = 0; offset < 144; offset++)
            {
                VReg actual = b.Load(IrType.I32, new SlotOperand(destination), offset, 1, false);
                okay = b.Binary(Opcode.And, okay, b.Binary(Opcode.Eq, actual, offset < length ? 0 : 0xa5));
            }
            VReg real = b.Unary(Opcode.IToF, I(7), IrType.F64);
            okay = b.Binary(Opcode.And, okay, b.Binary(Opcode.Eq, b.Unary(Opcode.FToI, R(real), IrType.I32), 7));
        }
        b.Ret(R(okay));
    }

    private static void PackedArithmeticFrames(Module m)
    {
        (Function function, Builder b) = New(m, "packed_arithmetic", IrType.I32);
        FrameSlot left = function.NewSlot(40, 8), right = function.NewSlot(40, 8), destination = function.NewSlot(40, 8);
        VReg okay = b.Const(1, IrType.I32);
        foreach (Opcode operation in new[] { Opcode.Add, Opcode.Sub, Opcode.And, Opcode.Or, Opcode.Xor, Opcode.Mul })
        foreach (int width in new[] { 1, 2, 4 })
        {
            int mask = width == 4 ? -1 : (1 << (8 * width)) - 1;
            for (int offset = 0; offset < 40; offset += width)
            {
                b.Store(new SlotOperand(left), I(unchecked((int)0x8000fff0 + offset * 37)), offset, width);
                b.Store(new SlotOperand(right), I(unchecked((int)0xffff8001 + offset * 97)), offset, width);
                b.Store(new SlotOperand(destination), I(0x55), offset, width);
            }
            for (int offset = 0; offset < 32; offset += width)
            {
                VReg a = b.Load(IrType.I32, new SlotOperand(left), offset, width, true);
                VReg c = b.Load(IrType.I32, new SlotOperand(right), offset, width, true);
                VReg value = b.Binary(operation, a, c); b.Store(new SlotOperand(destination), R(value), offset, width);
            }
            for (int offset = 0; offset < 40; offset += width)
            {
                int a = unchecked((int)0x8000fff0 + offset * 37), c = unchecked((int)0xffff8001 + offset * 97);
                int expected = (offset >= 32 ? 0x55 : operation switch { Opcode.Add => unchecked(a + c), Opcode.Sub => unchecked(a - c),
                    Opcode.And => a & c, Opcode.Or => a | c, Opcode.Xor => a ^ c, _ => unchecked(a * c) }) & mask;
                VReg actual = b.Load(IrType.I32, new SlotOperand(destination), offset, width, false);
                okay = b.Binary(Opcode.And, okay, b.Binary(Opcode.Eq, actual, expected));
            }
        }
        b.Ret(R(okay));
    }

    private static void SmallFills(Module m)
    {
        (Function f, Builder b) = New(m, "small_fills", IrType.I32);
        VReg okay = b.Const(1, IrType.I32);
        foreach (int length in new[] { 0, 1, 2, 3, 7, 16, 31, 32, 33 })
        foreach (int fill in new[] { 0, -1, 0x1234 })
        {
            FrameSlot slot = f.NewSlot(40, length == 7 ? 1 : 4, "fill");
            b.Emit(Opcode.MemSet, null, new SlotOperand(slot), I(0x7e), I(40));
            b.Emit(Opcode.MemSet, null, new SlotOperand(slot), I(fill), I(length));
            for (int offset = 0; offset < 40; offset++)
            {
                VReg actual = b.Load(IrType.I32, new SlotOperand(slot), offset, 1, false);
                VReg equal = b.Binary(Opcode.Eq, actual, offset < length ? fill & 255 : 0x7e);
                okay = b.Binary(Opcode.And, okay, equal);
            }
        }
        b.Ret(R(okay));
    }

    private static void Bytes(Module m)
    {
        (Function f, Builder b) = New(m, "bytes", IrType.I32);
        FrameSlot src = f.NewSlot(16, 4, "src");
        FrameSlot dst = f.NewSlot(16, 4, "dst");
        b.Emit(Opcode.MemSet, null, new SlotOperand(src), I(65), I(16));
        b.Store(new SlotOperand(src), I(-2), 5, 2);
        b.Emit(Opcode.MemCopy, null, new SlotOperand(dst), new SlotOperand(src), I(16));
        VReg a = b.Load(IrType.I32, new SlotOperand(dst), 0, 1, false);
        VReg w = b.Load(IrType.I32, new SlotOperand(dst), 5, 2, true);
        VReg wz = b.Load(IrType.I32, new SlotOperand(dst), 5, 2, false);
        VReg r = b.Binary(Opcode.Add, a, w);        // 65 + -2 = 63
        VReg r2 = b.Binary(Opcode.Sub, r, b.Unary(Opcode.ZExt16, wz));   // 63 - 65534
        VReg r3 = b.Binary(Opcode.Add, r2, 65534);
        b.Ret(R(r3));
    }

    /// <summary>
    /// The runtime helper the backend calls for unsigned 64-bit division,
    /// as a shift-and-subtract loop in IR: the test's stand-in for lib/rt,
    /// and a workout for every 64-bit operation the selector has.
    /// </summary>
    private static void UDiv64(Module m)
    {
        (Function f, Builder b) = New(m, "__udivdi3", IrType.I64, IrType.I64, IrType.I64);
        VReg n = f.Params[0];
        VReg d = f.Params[1];
        VReg q = b.Reg(IrType.I64, "q");
        VReg r = b.Reg(IrType.I64, "r");
        VReg i = b.Reg(IrType.I32, "i");
        b.CopyTo(q, I(0, IrType.I64));
        b.CopyTo(r, I(0, IrType.I64));
        b.CopyTo(i, I(63));
        Block head = f.NewBlock("head");
        Block body = f.NewBlock("body");
        Block sub = f.NewBlock("sub");
        Block next = f.NewBlock("next");
        Block done = f.NewBlock("done");
        b.Jump(head);
        b.SetBlock(head);
        b.Branch(b.Binary(Opcode.LtS, i, 0), done, body);
        b.SetBlock(body);
        VReg bit = b.Binary(Opcode.And, b.Binary(Opcode.ShrU, R(n), R(i), IrType.I64), 1);
        b.CopyTo(r, R(b.Binary(Opcode.Or, b.Binary(Opcode.Shl, r, 1), bit)));
        b.Branch(b.Binary(Opcode.GeU, r, d), sub, next);
        b.SetBlock(sub);
        b.CopyTo(r, R(b.Binary(Opcode.Sub, r, d)));
        b.CopyTo(q, R(b.Binary(Opcode.Or, q, b.Binary(Opcode.Shl, R(b.Const(1, IrType.I64)), R(i), IrType.I64))));
        b.Jump(next);
        b.SetBlock(next);
        b.CopyTo(i, R(b.Binary(Opcode.Sub, i, 1)));
        b.Jump(head);
        b.SetBlock(done);
        b.Ret(R(q));
    }

    /// <summary>int cmpzero(long a, int b) { return a == 0 ? 1 : b == 0 ? 2 : 3; }</summary>
    private static void CmpZero(Module m)
    {
        (Function f, Builder b) = New(m, "cmpzero", IrType.I32, IrType.I64, IrType.I32);
        Block one = f.NewBlock("one");
        Block rest = f.NewBlock("rest");
        Block two = f.NewBlock("two");
        Block three = f.NewBlock("three");
        b.Branch(b.Binary(Opcode.Eq, f.Params[0], 0), one, rest);
        b.SetBlock(one);
        b.Ret(I(1));
        b.SetBlock(rest);
        b.Branch(b.Binary(Opcode.Eq, f.Params[1], 0), two, three);
        b.SetBlock(two);
        b.Ret(I(2));
        b.SetBlock(three);
        b.Ret(I(3));
    }

    /// <summary>int constbranch() { bool c = false; if (c) return 1; return 7; } -- as the lowering leaves it.</summary>
    private static void ConstBranch(Module m)
    {
        (Function f, Builder b) = New(m, "constbranch", IrType.I32);
        VReg c = b.Const(0, IrType.I32);
        Block yes = f.NewBlock("yes");
        Block no = f.NewBlock("no");
        b.Branch(c, yes, no);
        b.SetBlock(yes);
        b.Ret(I(1));
        b.SetBlock(no);
        b.Ret(I(7));
    }

    /// <summary>int deadload() { x = counter; x = counter; return x; } -- the first load is dead.</summary>
    private static void DeadLoad(Module m)
    {
        (Function f, Builder b) = New(m, "deadload", IrType.I32);
        VReg x = b.Reg(IrType.I32, "x");
        for (int k = 0; k < 2; k++)
        {
            f.Blocks[0].Instrs.Add(new Instr { Op = Opcode.Load, Dest = x, Operands = { new SymOperand("counter") }, Size = 4 });
        }
        b.Ret(R(x));
    }

    /// <summary>A constant kept live across a call with the registers full: it must be remade, not spilled.</summary>
    private static void Remat(Module m)
    {
        (Function f, Builder b) = New(m, "remat", IrType.I32, IrType.I32);
        VReg c = b.Const(77, IrType.I32);
        List<VReg> vs = new();
        for (int k = 0; k < 8; k++)
        {
            vs.Add(b.Binary(Opcode.Mul, f.Params[0], k + 2));
        }
        b.Call("sum", IrType.Void, R(f.Params[0]));
        VReg acc = c;
        foreach (VReg v in vs)
        {
            acc = b.Binary(Opcode.Add, acc, v);
        }
        acc = b.Binary(Opcode.Add, acc, c);
        b.Ret(R(acc));
    }

    /// <summary>
    /// A bool from setcc, kept live across a call with the registers full
    /// so it is spilled, then read back: the reload must be the 32-bit slot,
    /// never the byte register the setcc wrote.
    /// </summary>
    private static void SpilledBool(Module m)
    {
        (Function f, Builder b) = New(m, "spilledbool", IrType.I32, IrType.I32, IrType.I32);
        VReg lt = b.Binary(Opcode.LtS, f.Params[0], f.Params[1]);
        VReg ge = b.Binary(Opcode.GeU, f.Params[0], f.Params[1]);
        List<VReg> vs = new();
        for (int k = 0; k < 8; k++)
        {
            vs.Add(b.Binary(Opcode.Mul, f.Params[0], k + 2));
        }
        b.Call("sum", IrType.Void, R(f.Params[1]));
        VReg acc = b.Binary(Opcode.Shl, lt, 8);
        acc = b.Binary(Opcode.Or, acc, ge);
        foreach (VReg v in vs)
        {
            acc = b.Binary(Opcode.Add, acc, v);
        }
        b.Ret(R(acc));
    }

    /// <summary>
    /// Every way three parameters can be handed to MemCopy as (dst, src, n),
    /// so whatever registers they arrive in, the moves into EDI, ESI and
    /// ECX cannot clobber one another. Each copies n bytes and returns the
    /// last byte copied plus n.
    /// </summary>
    private static void CopyPerms(Module m)
    {
        int[][] perms = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
        for (int p = 0; p < perms.Length; p++)
        {
            (Function f, Builder b) = New(m, $"copyperm{p}", IrType.I32, IrType.I32, IrType.I32, IrType.I32);
            VReg dst = f.Params[perms[p][0]];
            VReg src = f.Params[perms[p][1]];
            VReg n = f.Params[perms[p][2]];
            // Use each operand once before the copy so it is already in a register the allocator chose.
            VReg t = b.Binary(Opcode.Add, b.Binary(Opcode.Add, dst, src), n);
            b.Emit(Opcode.MemCopy, null, R(dst), R(src), R(n));
            VReg last = b.Load(IrType.I32, R(b.Binary(Opcode.Add, dst, n)), -1, 1, false);
            VReg r = b.Binary(Opcode.Add, last, n);
            b.Binary(Opcode.Sub, t, t);
            b.Ret(R(r));
        }
    }

    /// <summary>
    /// long byteword(int b0, int b1, int b2, int b3): the unoptimised
    /// shape of `b0 | (b1 &lt;&lt; 8) | (b2 &lt;&lt; 16) | ((long)b3 &lt;&lt; 24)`, where
    /// each distance is a register copied from a constant. A peephole once
    /// forwarded that copy into the shift's CL operand, which the encoding
    /// ignores, so the shifts used a stale CL: 0xabcdef12 became 0xab7fa012.
    /// </summary>
    private static void ByteWord(Module m)
    {
        (Function f, Builder b) = New(m, "byteword", IrType.I64, IrType.I32, IrType.I32, IrType.I32, IrType.I32);
        VReg d8 = b.Const(8, IrType.I32);
        VReg s1 = b.Binary(Opcode.Shl, f.Params[1], d8);
        VReg acc = b.Binary(Opcode.Or, f.Params[0], s1);
        VReg d16 = b.Const(16, IrType.I32);
        VReg s2 = b.Binary(Opcode.Shl, f.Params[2], d16);
        acc = b.Binary(Opcode.Or, acc, s2);
        VReg wide = b.Unary(Opcode.SExt32, acc);
        VReg d24 = b.Const(24, IrType.I32);
        VReg hi = b.Unary(Opcode.SExt32, f.Params[3]);
        VReg s3 = b.Binary(Opcode.Shl, R(hi), R(d24), IrType.I64);
        VReg w = b.Binary(Opcode.Or, wide, s3);
        b.Ret(R(w));
    }

    /// <summary>
    /// int mmap6(): a six-argument syscall whose sixth argument, which
    /// travels in EBP, changes the result. It maps page 1 of its own
    /// executable with mmap2(0, 4096, PROT_READ, MAP_PRIVATE, fd, pgoff),
    /// where pgoff is a register computed at run time, and compares the
    /// first word with what pread64 finds at file offset 4096. A dropped or
    /// wrong sixth argument maps some other page (or faults), and a frame
    /// pointer not restored after the trap shows up in the canary local.
    /// Returns 1 when all of that holds.
    /// </summary>
    private static void Mmap6(Module m)
    {
        (Function f, Builder b) = New(m, "mmap6", IrType.I32);
        FrameSlot canarySlot = f.NewSlot(4, 4, "canary");
        FrameSlot buf = f.NewSlot(4, 4, "buf");
        b.Store(new SlotOperand(canarySlot), I(0x5EED));
        VReg fd = b.Syscall(I(5), new Operand[] { new SymOperand("procexe"), I(0), I(0) });
        VReg n = b.Syscall(I(180), new Operand[] { R(fd), new SlotOperand(buf), I(4), I(4096), I(0) });
        VReg fromFile = b.Load(IrType.I32, new SlotOperand(buf));
        // pgoff = n - 3: one, but only once pread has run.
        VReg pgoff = b.Binary(Opcode.Sub, n, 3);
        VReg page1 = b.Syscall(I(192), new Operand[] { I(0), I(4096), I(1), I(2), R(fd), R(pgoff) });
        VReg page0 = b.Syscall(I(192), new Operand[] { I(0), I(4096), I(1), I(2), R(fd), I(0) });
        VReg mapped = b.Load(IrType.I32, R(page1));
        VReg magic = b.Load(IrType.I32, R(page0));
        VReg canary = b.Load(IrType.I32, new SlotOperand(canarySlot));
        VReg c1 = b.Binary(Opcode.Eq, n, 4);
        VReg c2 = b.Binary(Opcode.Eq, mapped, fromFile);
        VReg c3 = b.Binary(Opcode.Eq, magic, 0x464C457F);
        VReg c4 = b.Binary(Opcode.Ne, mapped, magic);
        VReg c5 = b.Binary(Opcode.Eq, canary, 0x5EED);
        VReg all = b.Binary(Opcode.And, b.Binary(Opcode.And, c1, c2), b.Binary(Opcode.And, b.Binary(Opcode.And, c3, c4), c5));
        b.Syscall(I(91), new Operand[] { R(page1), I(4096) });
        b.Syscall(I(91), new Operand[] { R(page0), I(4096) });
        b.Syscall(I(6), new Operand[] { R(fd) });
        b.Ret(R(all));
    }

    /// <summary>void exit(int code) -- Linux exit via int 0x80.</summary>
    private static void Exit(Module m)
    {
        (Function f, Builder b) = New(m, "exit", IrType.Void, IrType.I32);
        b.Syscall(I(1), new Operand[] { R(f.Params[0]) });
        b.Unreachable();
    }

    /// <summary>_start: every runtime check in turn; exit 42 if all pass, else the first failing check's number plus one.</summary>
    private static void Start(Module m)
    {
        (Function f, Builder b) = New(m, "_start", IrType.Void);
        m.Entry = "_start";
        int n = 0;

        void Expect(VReg got, Operand want, Opcode cmp = Opcode.Eq)
        {
            n++;
            Block ok = f.NewBlock("ok");
            Block bad = f.NewBlock("bad");
            b.Branch(b.Binary(cmp, R(got), want, IrType.I32), ok, bad);
            b.SetBlock(bad);
            b.Call("exit", IrType.Void, I(n));
            b.Unreachable();
            b.SetBlock(ok);
        }

        Expect(b.Call("add", IrType.I32, I(2), I(3))!, I(5));
        Expect(b.Call("small_fills", IrType.I32)!, I(1));
        Expect(b.Call("packed_frames", IrType.I32)!, I(1));
        Expect(b.Call("packed_arithmetic", IrType.I32)!, I(1));
        Expect(b.Call("bswap32", IrType.I32, I(0x11223344))!, I(0x44332211));
        Expect(b.Call("bswap32", IrType.I32, I(unchecked((int)0x80000001)))!, I(0x01000080));
        Expect(b.Call("bswap64_inplace", IrType.I64, I(0x0123456789abcdefL, IrType.I64))!,
            I(unchecked((long)0xefcdab8967452301UL), IrType.I64));
        Expect(b.Call("bswap64_inplace", IrType.I64, I(long.MinValue, IrType.I64))!, I(128, IrType.I64));
        Expect(b.Call("sum", IrType.I32, I(10))!, I(45));
        Expect(b.Call("mul64", IrType.I64, I(0x100000001L, IrType.I64), I(0x300000003L, IrType.I64))!, I(0x200000001L, IrType.I64));
        VReg fm = b.Call("fma", IrType.F64, I(3, IrType.I64), I(0x4000000000000000L, IrType.I64), I(1))!;
        // fma's first argument is 3 as raw bits: a denormal, so a*b is ~0 and the result is 1.0 + tiny -> 1.
        Expect(b.Unary(Opcode.FToI, R(fm), IrType.I32), I(1));
        Expect(b.Call("calls", IrType.I32, I(3))!, I(51));
        Expect(b.Call("pressure", IrType.I32, I(3))!, I(PressureExpected(3)));
        Expect(b.Call("sw", IrType.I32, I(1))!, I(20));
        Expect(b.Call("sw", IrType.I32, I(7))!, I(-1));
        Expect(b.Call("sw", IrType.I32, I(-1))!, I(-1));
        // sh64(1<<20, 33): (1<<53) + (0) + (1<<20 >>> 33 = 0) = 1<<53; negated.
        Expect(b.Call("sh64", IrType.I64, I(1L << 20, IrType.I64), I(33))!, I(-(1L << 53), IrType.I64));
        Expect(b.Call("sh64", IrType.I64, I(-1L << 40, IrType.I64), I(3))!, I(-(((-1L << 40) << 3) + (-1L << 40 >> 40) + (long)(unchecked((ulong)(-1L << 40)) >> 3)), IrType.I64));
        Expect(b.Call("cmp64", IrType.I32, I(-1, IrType.I64), I(1, IrType.I64))!, I(1));
        Expect(b.Call("cmp64", IrType.I32, I(1, IrType.I64), I(-1, IrType.I64))!, I(0));
        Expect(b.Call("cmp64", IrType.I32, I(5, IrType.I64), I(5, IrType.I64))!, I(1));
        Expect(b.Call("cmp64", IrType.I32, I(0x100000000L, IrType.I64), I(0xFFFFFFFFL, IrType.I64))!, I(1));
        // pressure() left its result in the counter's low byte; landing() then works on it.
        int counter = PressureExpected(3);
        Expect(b.Call("landing", IrType.I32, I(5))!, I(LandingExpected(ref counter)));
        Expect(b.Call("landing", IrType.I32, I(5))!, I(LandingExpected(ref counter)));
        Expect(b.Call("landing", IrType.I32, I(0))!, I(-13));
        Expect(b.Call("evict", IrType.I32, I(3))!, I(EvictExpected(3)));
        Expect(b.Call("evict", IrType.I32, I(9))!, I(EvictExpected(9)));
        Expect(b.Call("convert", IrType.I32, I(-1))!, I(65535));
        Expect(b.Call("bytes", IrType.I32)!, I(63));
        Expect(b.Call("cmpzero", IrType.I32, I(0, IrType.I64), I(5))!, I(1));
        Expect(b.Call("cmpzero", IrType.I32, I(1L << 40, IrType.I64), I(0))!, I(2));
        Expect(b.Call("cmpzero", IrType.I32, I(1, IrType.I64), I(9))!, I(3));
        Expect(b.Call("constbranch", IrType.I32)!, I(7));
        Expect(b.Call("deadload", IrType.I32)!, I(counter));
        Expect(b.Call("remat", IrType.I32, I(3))!, I(77 * 2 + 3 * Enumerable.Range(2, 8).Sum()));
        Expect(b.Call("byteword", IrType.I64, I(0x12), I(0xEF), I(0xCD), I(0xAB))!, I(0xABCDEF12L, IrType.I64));
        Expect(b.Call("byteword", IrType.I64, I(0xFF), I(0xFF), I(0xFF), I(0xFF))!, I(0xFFFFFFFFL, IrType.I64));
        Expect(b.Call("mmap6", IrType.I32)!, I(1));
        Expect(b.Call("spilledbool", IrType.I32, I(2), I(5))!, I(256 + 0 + 2 * Enumerable.Range(2, 8).Sum()));
        Expect(b.Call("spilledbool", IrType.I32, I(-3), I(5))!, I(256 + 1 + -3 * Enumerable.Range(2, 8).Sum()));
        Expect(b.Call("spilledbool", IrType.I32, I(9), I(5))!, I(0 + 1 + 9 * Enumerable.Range(2, 8).Sum()));
        FrameSlot buf = f.NewSlot(32, 4, "buf");
        b.Emit(Opcode.MemSet, null, new SlotOperand(buf), I(0x5A), I(16));
        for (int p = 0; p < 6; p++)
        {
            int[][] perms = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
            int[] perm = perms[p];
            VReg dstAddr = b.Binary(Opcode.Add, R(b.SlotAddress(buf)), I(16), IrType.I32);
            VReg srcAddr = b.SlotAddress(buf);
            Operand[] args = new Operand[3];
            args[perm[0]] = R(dstAddr);
            args[perm[1]] = R(srcAddr);
            args[perm[2]] = I(5 + p);
            Expect(b.Call($"copyperm{p}", IrType.I32, args)!, I(0x5A + 5 + p));
        }
        b.Call("exit", IrType.Void, I(42));
        b.Unreachable();
    }

    /// <summary>A try/catch shape: record in a frame slot, LabelAddr, Unwind, and a landing pad.</summary>
    private static void Landing(Module m)
    {
        (Function f, Builder b) = New(m, "landing", IrType.I32, IrType.I32);
        FrameSlot rec = f.NewSlot(16, 4, "handler");
        Block pad = f.NewBlock("pad");
        pad.IsLandingPad = true;
        Block body = f.NewBlock("body");
        VReg recAddr = b.SlotAddress(rec);
        VReg h = b.LabelAddress(pad);
        b.Store(R(recAddr), R(h), 4);
        b.Store(R(recAddr), R(b.Emit(Opcode.StackPointer, b.Reg(IrType.I32)).Dest!), 8);
        b.Store(R(recAddr), R(b.Emit(Opcode.FramePointer, b.Reg(IrType.I32)).Dest!), 12);
        b.Jump(body);
        b.SetBlock(body);
        VReg z = b.Binary(Opcode.Eq, f.Params[0], 0);
        Block thr = f.NewBlock("throw");
        Block ok = f.NewBlock("ok");
        b.Branch(z, thr, ok);
        b.SetBlock(thr);
        b.Emit(Opcode.Unwind, null, R(recAddr), I(13));
        b.SetBlock(ok);
        VReg old = b.Emit(Opcode.AtomicAdd, b.Reg(IrType.I32), new SymOperand("counter"), I(1)).Dest!;
        VReg o2 = b.Emit(Opcode.AtomicOr, b.Reg(IrType.I32), new SymOperand("counter"), R(old)).Dest!;
        b.Emit(Opcode.Fence, null);
        b.Ret(R(o2));
        b.SetBlock(pad);
        VReg exc = b.Call("__exception", IrType.I32)!;
        VReg neg = b.Unary(Opcode.Neg, exc);
        b.Ret(R(neg));
    }
}
