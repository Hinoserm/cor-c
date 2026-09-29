#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Tests.Elf;

/// <summary>
/// Long mode: the same object model written as ELF64 with RELA relocations,
/// read back, linked into an ELF64 executable by this linker and by GNU ld,
/// and run by the host kernel's x86-64 loader.
/// </summary>
public static partial class Program
{
    /// <summary>_start: call f; mov edi, eax; mov eax, 60; syscall -- exit(f()).</summary>
    private static ObjectFile Caller64()
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0xe8, 0, 0, 0, 0, 0x89, 0xc7, 0xb8, 60, 0, 0, 0, 0x0f, 0x05 });
        text.Relocs.Add(new Relocation(1, "f", -4, RelocKind.Plt32));
        o.Sections.Add(text);
        o.Symbols.Add(new Symbol { Name = "_start", Section = text, Offset = 0, Size = 14, IsFunction = true });
        o.Symbols.Add(new Symbol { Name = "f", Section = null });
        new TargetContract(0, longMode: true).Attach(o);
        return o;
    }

    /// <summary>
    /// f: mov rax, [rip + pmsg]; movzx eax, byte [rax]; ret -- the first byte
    /// of the string .data points at, 42: a RIP-relative load (PC32) and an
    /// eight-byte address in data (R_X86_64_64).
    /// </summary>
    private static ObjectFile Callee64()
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0x48, 0x8b, 0x05, 0, 0, 0, 0, 0x0f, 0xb6, 0x00, 0xc3 });
        text.Relocs.Add(new Relocation(3, "pmsg", -4, RelocKind.Rel32));
        Section rodata = new(".rodata", SectionKind.ReadOnlyData) { Align = 8 };
        rodata.Bytes.AddRange(new byte[] { 42, (byte)'h', (byte)'i', 0 });
        Section data = new(".data", SectionKind.Data) { Align = 8 };
        data.Bytes.AddRange(new byte[8]);
        data.Relocs.Add(new Relocation(0, "msg", 0, RelocKind.Abs64));
        o.Sections.Add(text);
        o.Sections.Add(rodata);
        o.Sections.Add(data);
        o.Symbols.Add(new Symbol { Name = "msg", Section = rodata, Offset = 0, Size = 4, Global = false });
        o.Symbols.Add(new Symbol { Name = "f", Section = text, Offset = 0, Size = 11, IsFunction = true });
        o.Symbols.Add(new Symbol { Name = "pmsg", Section = data, Offset = 0, Size = 8 });
        new TargetContract(0, longMode: true).Attach(o);
        return o;
    }

    private static void LongModeObjects()
    {
        string caller = Write("caller64.o", ElfWriter.WriteObject(Caller64()));
        string callee = Write("callee64.o", ElfWriter.WriteObject(Callee64()));

        (int code, string output) = Run("readelf", "-hr", callee);
        Check(code == 0 && !output.Contains("Warning") && !output.Contains("Error"), "readelf reads it without complaint", output);
        Check(output.Contains("ELF64") && output.Contains("X86-64") && output.Contains("REL (Relocatable file)"), "readelf sees ET_REL ELF64 x86-64", output);
        Check(output.Contains("R_X86_64_PC32") && output.Contains("R_X86_64_64"), "the relocations are RELA PC32 and 64", output);
        (code, output) = Run("readelf", "-r", caller);
        Check(code == 0 && output.Contains("R_X86_64_PLT32"), "the call is R_X86_64_PLT32", output);

        foreach ((string name, ObjectFile o) in new[] { ("caller64", Caller64()), ("callee64", Callee64()) })
        {
            byte[] first = ElfWriter.WriteObject(o);
            ObjectFile back = ElfReader.ReadObject(first);
            byte[] second = ElfWriter.WriteObject(back);
            Check(first.AsSpan().SequenceEqual(second), $"{name}: write(read(write(o))) == write(o)");
            Check(TargetContract.IsLongMode(back), $"{name}: read back as long mode");
        }

        string exe = Path.Combine(_dir, "exit42-ld64");
        (code, output) = Run("ld", "-m", "elf_x86_64", "-o", exe, caller, callee);
        Check(code == 0 && output.Trim().Length == 0, "GNU ld links the two without complaint", output);
        Check(Exec(exe) == 42, "the ld-linked executable exits 42");
    }

    private static void LongModeLink()
    {
        byte[] image = Linker.Link(new[] { ("caller64", Caller64()), ("callee64", Callee64()) }, "_start");
        string exe = Write("exit42-64", image);
        (int code, string output) = Run("readelf", "-hl", exe);
        Check(code == 0 && output.Contains("ELF64") && output.Contains("EXEC") && output.Contains("0x0000000000400000"), "an ELF64 executable at 0x400000", output);
        Check(Exec(exe) == 42, "it exits 42");

        // Through the file: the objects as written, read back, linked.
        ObjectFile a = ElfReader.ReadObject(ElfWriter.WriteObject(Caller64()));
        ObjectFile b = ElfReader.ReadObject(ElfWriter.WriteObject(Callee64()));
        string again = Write("exit42-64-read", Linker.Link(new[] { ("caller64.o", a), ("callee64.o", b) }, "_start"));
        Check(Exec(again) == 42, "the same objects read back from ELF64 link and exit 42");
    }

    /// <summary>
    /// The library: f adds msg[0] read through an address held in .data
    /// (R_X86_64_64, a RELATIVE for the loader) to msg[0] read through its
    /// GOT slot (GOTPCREL, which this linker relaxes to a lea since the
    /// library defines msg) -- 42 + 42.
    ///
    ///   mov rax, [rip + pmsg]           48 8b 05 d32
    ///   movzx ecx, byte [rax]           0f b6 08
    ///   mov rax, [rip + msg@GOTPCREL]   48 8b 05 d32
    ///   movzx eax, byte [rax]           0f b6 00
    ///   add eax, ecx                    01 c8
    ///   ret                             c3
    /// </summary>
    private static ObjectFile Library64()
    {
        ObjectFile o = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        text.Bytes.AddRange(new byte[] { 0x48, 0x8b, 0x05, 0, 0, 0, 0, 0x0f, 0xb6, 0x08, 0x48, 0x8b, 0x05, 0, 0, 0, 0, 0x0f, 0xb6, 0x00, 0x01, 0xc8, 0xc3 });
        text.Relocs.Add(new Relocation(3, "pmsg", -4, RelocKind.Rel32));
        text.Relocs.Add(new Relocation(13, "msg", -4, RelocKind.GotPcRel));
        Section rodata = new(".rodata", SectionKind.ReadOnlyData) { Align = 8 };
        rodata.Bytes.AddRange(new byte[] { 42, (byte)'h', (byte)'i', 0 });
        Section data = new(".data", SectionKind.Data) { Align = 8 };
        data.Bytes.AddRange(new byte[8]);
        data.Relocs.Add(new Relocation(0, "msg", 0, RelocKind.Abs64));
        o.Sections.Add(text);
        o.Sections.Add(rodata);
        o.Sections.Add(data);
        o.Symbols.Add(new Symbol { Name = "msg", Section = rodata, Offset = 0, Size = 4 });
        o.Symbols.Add(new Symbol { Name = "f", Section = text, Offset = 0, Size = text.Bytes.Count, IsFunction = true });
        o.Symbols.Add(new Symbol { Name = "pmsg", Section = data, Offset = 0, Size = 8, Global = false });
        new TargetContract(0, longMode: true).Attach(o);
        return o;
    }

    private static void LongModeDynamic()
    {
        string lib = Write("libf64.so", Linker.LinkShared(new[] { ("library64", Library64()) }, "libf64.so"));
        (int code, string output) = Run("readelf", "-hdrW", lib);
        Check(code == 0 && !output.Contains("Warning") && !output.Contains("Error"), "readelf reads the shared object without complaint", output);
        Check(output.Contains("ELF64") && output.Contains("DYN (Shared object file)"), "an ELF64 shared object", output);
        Check(output.Contains("(SONAME)") && output.Contains("(RELA)") && output.Contains("BIND_NOW"), "SONAME, RELA and eager binding", output);
        Check(output.Contains("R_X86_64_RELATIVE") && !output.Contains("TEXTREL"), "the address in .data is RELATIVE; the text has none", output);
        Check(!output.Contains("R_X86_64_GLOB_DAT"), "msg's GOT load was relaxed: no slot for a symbol the library defines", output);

        // Our program against our library.
        string exe = Write("dyn84-64", Linker.Link(new[] { ("caller64", Caller64()) }, "_start", new[] { lib }));
        (code, output) = Run("readelf", "-lW", exe);
        Check(code == 0 && output.Contains("/lib64/ld-linux-x86-64.so.2"), "the program names the x86-64 loader", output);
        Check(Exec(exe) == 84, "the loader binds f from the library and the program exits 84");

        // GNU ld's program against our library: the .so is an ordinary one.
        string caller = Write("caller64-dyn.o", ElfWriter.WriteObject(Caller64()));
        string ldExe = Path.Combine(_dir, "dyn84-64-ld");
        (code, output) = Run("ld", "-m", "elf_x86_64", "-o", ldExe, "--dynamic-linker", "/lib64/ld-linux-x86-64.so.2",
            "-rpath", _dir, caller, lib);
        Check(code == 0, "GNU ld links a program against it", output);
        Check(Exec(ldExe) == 84, "and that program exits 84 too");
    }

    private static void LongModeMixture()
    {
        ObjectFile i386 = Callee();
        new TargetContract(0).Attach(i386);
        try
        {
            TargetContract.Validate(new[] { ("caller64", Caller64()), ("callee32", i386) });
            Check(false, "a 32-bit object beside a 64-bit one is refused");
        }
        catch (LinkException e)
        {
            Check(e.Message.Contains("64-bit") || e.Errors.Any(m => m.Contains("64-bit")), "a 32-bit object beside a 64-bit one is refused", e.Message);
        }
    }
}
