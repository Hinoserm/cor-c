#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// THE KERNEL AS A LIBRARY (docs/software/DRIVERS.md in the OS repository,
/// "Modules"). A module is a shared object linked against the kernel the way
/// a program is linked against libSystem.so, and this is what stands in for
/// the library: a file `corc link --exports` writes beside the kernel, with
/// every global the kernel defines in a .dynsym, its SysV hash and a
/// .dynamic that names them, and nothing else loaded.
///
///   0x0000  ELF header, program headers      \
///           .hash, .dynsym, .dynstr           |  PT_LOAD R, at 0
///           .dynamic                          /
///           .corsac.stamp   the build stamp
///           .corsac.abi     the kernel units' native contract
///           .corsac.cpu     and the processor they were built for
///
/// Every symbol is SHN_ABS: its value is where the kernel already is, and a
/// binder that maps this file to read its tables adds no bias to one. So the
/// kernel's own binder (elfload.cor) looks a module's imports up here with
/// the code it looks a library's up with -- DT_HASH, the chains, the names --
/// as one more image whose bias is nought.
///
/// It is a file and not a section of the kernel because nothing in the
/// kernel's own memory needs it until the first module loads: the tables of
/// thirty thousand mangled names are megabytes the kernel image, held below
/// the bootloader's scratch, cannot spare.
///
/// THE BUILD STAMP is a hash of the declaration index the kernel was compiled
/// against (DeclarationStamp in the compiler): what every type's fields and
/// every method's signature were, and nothing of any body. The kernel carries
/// it twice -- as `__corsac_build_stamp`, thirty-two bytes of .rodata the
/// running kernel can compare with (Sys.BuildStamp), and in a .corsac.stamp
/// note -- and so does every module built against it, in its own note. A
/// module whose stamp is not the running kernel's is refused: before
/// Release 0 there is no compatibility between builds, so exact is the rule.
/// </summary>
public sealed class KernelExports
{
    /// <summary>The note holding the stamp, in the kernel, this file and every module.</summary>
    public const string StampSection = ".corsac.stamp";
    /// <summary>The kernel's own copy, loaded: what Sys.BuildStamp() answers the address of.</summary>
    public const string StampSymbol = "__corsac_build_stamp";
    public const int StampBytes = 32;

    /// <summary>
    /// What is never a kernel export, though the kernel's symbol table calls
    /// it global: the names the linker gives every image -- one image's text,
    /// statics and frame tables each, which a module that took the kernel's
    /// would register as its own -- and the entry, which nothing calls.
    /// </summary>
    private static readonly HashSet<string> NeverExported = new(StringComparer.Ordinal)
    {
        "__text_start", "_etext", "__data_start", "_edata", "__bss_start", "_end",
        "_DYNAMIC", "_GLOBAL_OFFSET_TABLE_", "_start", Elf.SharedInitName,
    };

    public string Path { get; }
    /// <summary>The kernel's file name: what the exports call themselves (DT_SONAME).</summary>
    public string Name { get; }
    public byte[] Stamp { get; }
    /// <summary>The kernel units' .corsac.abi and .corsac.cpu, which a module's units must agree with; null where the kernel's had none.</summary>
    public byte[]? Abi { get; }
    public byte[]? Cpu { get; }
    public bool LongMode { get; }
    /// <summary>Every name the kernel exports.</summary>
    public HashSet<string> Names { get; }

    private KernelExports(string path, string name, byte[] stamp, byte[]? abi, byte[]? cpu, bool longMode, HashSet<string> names)
    {
        Path = path; Name = name; Stamp = stamp; Abi = abi; Cpu = cpu; LongMode = longMode; Names = names;
    }

    /// <summary>One global of a linked image: what a .dynsym entry of it says.</summary>
    public readonly record struct Export(string Name, ulong Value, ulong Size, byte Type);

    /// <summary>The stamp as it is written in a message: its hex.</summary>
    public static string Text(byte[] stamp) => Convert.ToHexString(stamp).ToLowerInvariant();

    // ---- reading -------------------------------------------------------------

    /// <summary>
    /// A file `corc link --exports` wrote. Anything else -- the kernel image
    /// itself, a library -- is refused by name: it has no stamp, and building
    /// a module against it would build one no kernel can load.
    /// </summary>
    public static KernelExports Read(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException error) { throw new ElfFormatException(path + ": " + error.Message); }
        List<SectionView> sections = Sections(bytes, path);
        SectionView? stamp = sections.Find(s => s.Name == StampSection);
        if (stamp is null || stamp.Size != StampBytes)
            throw new ElfFormatException(path + ": not a kernel's exports (no build stamp); write them with `corc link ... --exports`");
        byte[]? Copy(string name) => sections.Find(s => s.Name == name) is { } s ? bytes.AsSpan((int)s.Offset, (int)s.Size).ToArray() : null;
        HashSet<string> names = new(ElfReader.ExportsOf(bytes), StringComparer.Ordinal);
        string name = ElfReader.SoNameOf(bytes) ?? System.IO.Path.GetFileName(path);
        return new KernelExports(path, name, Copy(StampSection)!, Copy(TargetContract.SectionName), Copy(X86CodeGenerationContract.SectionName),
            bytes[4] == Elf.Class64, names);
    }

    /// <summary>The stamp in a linked image's .corsac.stamp, or null where it has none: a kernel's, a module's, or these exports'.</summary>
    public static byte[]? StampOf(byte[] image, string what)
    {
        SectionView? stamp = Sections(image, what).Find(s => s.Name == StampSection);
        return stamp is null || stamp.Size != StampBytes ? null : image.AsSpan((int)stamp.Offset, StampBytes).ToArray();
    }

    /// <summary>
    /// Every global a linked image DEFINES, from its .symtab: the kernel's
    /// functions, statics and type descriptors, with their addresses, less
    /// what <see cref="NeverExported"/> names and the per-image tables the
    /// linker and the code generator make (<see cref="PerImage"/>). Its own
    /// stamp is kept: Sys.BuildStamp() in a module asks for the kernel's.
    /// </summary>
    public static List<Export> GlobalsOf(string imagePath)
    {
        byte[] bytes = File.ReadAllBytes(imagePath);
        bool wide = bytes[4] == Elf.Class64;
        List<SectionView> sections = Sections(bytes, imagePath);
        SectionView? table = sections.Find(s => s.Type == Elf.ShtSymTab)
            ?? throw new ElfFormatException(imagePath + ": the image has no symbol table to export");
        if (table.Link >= sections.Count) throw new ElfFormatException(imagePath + ": the symbol table names no string table");
        SectionView strings = sections[(int)table.Link];
        ReadOnlySpan<byte> names = bytes.AsSpan((int)strings.Offset, (int)strings.Size);
        int size = wide ? Elf.Symbol64Size : Elf.SymbolSize;
        List<Export> exports = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (long at = table.Offset; at + size <= table.Offset + table.Size; at += size)
        {
            ReadOnlySpan<byte> entry = bytes.AsSpan((int)at, size);
            uint name = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            byte info = entry[wide ? 4 : 12];
            ushort shndx = BinaryPrimitives.ReadUInt16LittleEndian(entry[(wide ? 6 : 14)..]);
            ulong value = wide ? BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            ulong length = wide ? BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]) : BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            int binding = info >> 4;
            if (name == 0 || shndx == Elf.ShnUndef || (binding != Elf.StbGlobal && binding != Elf.StbWeak)) continue;
            string text = StringTable.Read(names, name, ".strtab");
            if (NeverExported.Contains(text) || PerImage(text)) continue;
            if (!seen.Add(text)) continue;
            exports.Add(new Export(text, value, length, (byte)(info & 0xF)));
        }
        return exports;
    }

    /// <summary>
    /// The tables every image has one of: its unit directory, each unit's
    /// frames and stack maps, the pool of their names. A module has its own,
    /// and one that imported the kernel's would hand the kernel's to
    /// Runtime.BeginImage as its own. The thread block (`__corsac_tls0`,
    /// `__corsac_tls_self`) is not one of them: there is one, the kernel's,
    /// and a module's code finds it by name.
    /// </summary>
    private static bool PerImage(string name)
        => name.StartsWith("__corsac_unit", StringComparison.Ordinal) || name.StartsWith("__corsac_frame", StringComparison.Ordinal)
        || name.StartsWith("__corsac_stackmaps", StringComparison.Ordinal) || name.StartsWith("__corsac_init", StringComparison.Ordinal)
        || name == Linker.ModuleInitName;

    // ---- writing -------------------------------------------------------------

    /// <summary>
    /// The exports file: <paramref name="symbols"/> in a .dynsym with a SysV
    /// hash over them, a .dynamic naming the tables and the kernel
    /// (DT_SONAME), and the stamp and the contracts as notes.
    /// </summary>
    public static void Write(string path, string kernelName, IReadOnlyList<Export> symbols, byte[] stamp, byte[]? abi, byte[]? cpu, bool wide)
    {
        if (stamp.Length != StampBytes) throw new ArgumentException("a build stamp is " + StampBytes + " bytes");
        int header = wide ? Elf.Header64Size : Elf.HeaderSize;
        int phent = wide ? Elf.ProgramHeader64Size : Elf.ProgramHeaderSize;
        int shent = wide ? Elf.SectionHeader64Size : Elf.SectionHeaderSize;
        int symbolSize = wide ? Elf.Symbol64Size : Elf.SymbolSize;
        int word = wide ? 8 : 4;

        StringTable dynstr = new();
        uint soname = dynstr.Add(kernelName);
        uint[] nameAt = symbols.Select(s => dynstr.Add(s.Name)).ToArray();
        byte[] strings = dynstr.ToArray();

        // The SysV hash, as Dynamic.cs builds a library's: a bucket per
        // symbol, odd, and the chains beside them.
        int count = symbols.Count + 1;
        int buckets = Math.Max(1, count | 1);
        uint[] bucket = new uint[buckets], chain = new uint[count];
        for (int i = 1; i < count; i++)
        {
            int b = (int)(Elf.HashName(symbols[i - 1].Name) % (uint)buckets);
            chain[i] = bucket[b];
            bucket[b] = (uint)i;
        }

        long Align(long at, int to) => (at + to - 1) / to * to;
        long hashAt = Align(header + 2L * phent, 8);
        long dynsymAt = Align(hashAt + (2L + buckets + count) * 4, word);
        long dynstrAt = dynsymAt + (long)count * symbolSize;
        long dynamicAt = Align(dynstrAt + strings.Length, word);
        (long Tag, ulong Value)[] dynamic =
        {
            (Elf.DtSoName, soname), (Elf.DtHash, (ulong)hashAt), (Elf.DtStrTab, (ulong)dynstrAt), (Elf.DtSymTab, (ulong)dynsymAt),
            (Elf.DtStrSz, (ulong)strings.Length), (Elf.DtSymEnt, (ulong)symbolSize), (Elf.DtNull, 0),
        };
        long loadEnd = dynamicAt + dynamic.Length * 2L * word;

        List<(string Name, byte[] Bytes)> notes = new() { (StampSection, stamp) };
        if (abi is not null) notes.Add((TargetContract.SectionName, abi));
        if (cpu is not null) notes.Add((X86CodeGenerationContract.SectionName, cpu));
        long[] noteAt = new long[notes.Count];
        long at = loadEnd;
        for (int i = 0; i < notes.Count; i++) { at = Align(at, 4); noteAt[i] = at; at += notes[i].Bytes.Length; }
        StringTable shstr = new();
        string[] sectionNames = new[] { ".hash", ".dynsym", ".dynstr", ".dynamic" }.Concat(notes.Select(n => n.Name)).Append(".shstrtab").ToArray();
        uint[] shName = sectionNames.Select(shstr.Add).ToArray();
        byte[] shstrtab = shstr.ToArray();
        long shstrAt = at;
        long shoff = Align(shstrAt + shstrtab.Length, 8);
        int shnum = sectionNames.Length + 1;

        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        void Word(ulong value) { if (wide) w.Write(value); else w.Write(checked((uint)value)); }
        void Pad(long to) { while (stream.Position < to) w.Write((byte)0); }

        // ELF header.
        w.Write(Elf.Magic.ToArray()); w.Write(wide ? Elf.Class64 : Elf.Class32); w.Write(Elf.Data2Lsb); w.Write(Elf.VersionCurrent); w.Write(Elf.OsAbiSysV);
        Pad(16);
        w.Write(Elf.TypeDyn); w.Write(wide ? Elf.MachineX86_64 : Elf.MachineI386); w.Write((uint)Elf.VersionCurrent);
        Word(0); Word((ulong)header); Word((ulong)shoff);
        w.Write(0u); w.Write((ushort)header); w.Write((ushort)phent); w.Write((ushort)2);
        w.Write((ushort)shent); w.Write((ushort)shnum); w.Write((ushort)(shnum - 1));

        // PT_LOAD over the tables, PT_DYNAMIC over .dynamic.
        void Segment(uint type, long offset, long bytes, uint flags, ulong align)
        {
            w.Write(type);
            if (wide) { w.Write(flags); w.Write((ulong)offset); w.Write((ulong)offset); w.Write((ulong)offset); w.Write((ulong)bytes); w.Write((ulong)bytes); w.Write(align); }
            else { w.Write((uint)offset); w.Write((uint)offset); w.Write((uint)offset); w.Write((uint)bytes); w.Write((uint)bytes); w.Write(flags); w.Write((uint)align); }
        }
        Segment(Elf.PtLoad, 0, loadEnd, Elf.PfR, Elf.PageSize);
        Segment(Elf.PtDynamic, dynamicAt, loadEnd - dynamicAt, Elf.PfR, (ulong)word);

        Pad(hashAt);
        w.Write((uint)buckets); w.Write((uint)count);
        foreach (uint b in bucket) w.Write(b);
        foreach (uint c in chain) w.Write(c);

        Pad(dynsymAt);
        w.Write(new byte[symbolSize]);
        for (int i = 0; i < symbols.Count; i++)
        {
            Export s = symbols[i];
            byte info = (byte)((Elf.StbGlobal << 4) | (s.Type is Elf.SttFunc or Elf.SttObject ? s.Type : Elf.SttNoType));
            if (wide) { w.Write(nameAt[i]); w.Write(info); w.Write((byte)0); w.Write(Elf.ShnAbs); w.Write(s.Value); w.Write(s.Size); }
            else { w.Write(nameAt[i]); w.Write(checked((uint)s.Value)); w.Write(checked((uint)s.Size)); w.Write(info); w.Write((byte)0); w.Write(Elf.ShnAbs); }
        }
        w.Write(strings);

        Pad(dynamicAt);
        foreach ((long tag, ulong value) in dynamic) { Word(unchecked((ulong)tag)); Word(value); }

        for (int i = 0; i < notes.Count; i++) { Pad(noteAt[i]); w.Write(notes[i].Bytes); }
        Pad(shstrAt);
        w.Write(shstrtab);
        Pad(shoff);

        // Section headers: name, type, flags, address, offset, size, link, info, alignment, entry size.
        void SectionHeader(uint name, uint type, ulong flags, long address, long offset, long size, uint link, uint align, uint entsize)
        {
            w.Write(name); w.Write(type);
            if (wide) { w.Write(flags); w.Write((ulong)address); w.Write((ulong)offset); w.Write((ulong)size); w.Write(link); w.Write(type == Elf.ShtDynSym ? 1u : 0u); w.Write((ulong)align); w.Write((ulong)entsize); }
            else { w.Write((uint)flags); w.Write((uint)address); w.Write((uint)offset); w.Write((uint)size); w.Write(link); w.Write(type == Elf.ShtDynSym ? 1u : 0u); w.Write(align); w.Write(entsize); }
        }
        w.Write(new byte[shent]);
        SectionHeader(shName[0], Elf.ShtHash, Elf.ShfAlloc, hashAt, hashAt, (2L + buckets + count) * 4, 2, 4, 4);
        SectionHeader(shName[1], Elf.ShtDynSym, Elf.ShfAlloc, dynsymAt, dynsymAt, (long)count * symbolSize, 3, (uint)word, (uint)symbolSize);
        SectionHeader(shName[2], Elf.ShtStrTab, Elf.ShfAlloc, dynstrAt, dynstrAt, strings.Length, 0, 1, 0);
        SectionHeader(shName[3], Elf.ShtDynamic, Elf.ShfAlloc, dynamicAt, dynamicAt, loadEnd - dynamicAt, 3, (uint)word, (uint)(2 * word));
        for (int i = 0; i < notes.Count; i++)
            SectionHeader(shName[4 + i], Elf.ShtProgBits, 0, 0, noteAt[i], notes[i].Bytes.Length, 0, 4, 0);
        SectionHeader(shName[^1], Elf.ShtStrTab, 0, 0, shstrAt, shstrtab.Length, 0, 1, 0);
        w.Flush();
        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>
    /// What the kernel's link adds to stamp it: thirty-two bytes of .rodata
    /// called <see cref="StampSymbol"/>, and the same in a .corsac.stamp note.
    /// A module's link adds only the note: its Sys.BuildStamp() is the
    /// kernel's, which is the one worth comparing with.
    /// </summary>
    public static ObjectFile StampObject(byte[] stamp, bool loaded)
    {
        ObjectFile stamped = new();
        Section note = new(StampSection, SectionKind.Note) { Align = 1 };
        note.Bytes.AddRange(stamp);
        stamped.Sections.Add(note);
        if (loaded)
        {
            Section data = new(".rodata", SectionKind.ReadOnlyData) { Align = 4 };
            data.Bytes.AddRange(stamp);
            stamped.Sections.Add(data);
            stamped.Symbols.Add(new Symbol { Name = StampSymbol, Section = data, Offset = 0, Size = StampBytes });
        }
        return stamped;
    }

    // ---- an image's sections, by name ------------------------------------------

    internal sealed record SectionView(string Name, uint Type, long Offset, long Size, uint Link);

    /// <summary>Every section of a linked ELF file of either class, with its name.</summary>
    internal static List<SectionView> Sections(byte[] bytes, string what)
    {
        if (bytes.Length < Elf.HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual(Elf.Magic) || bytes[5] != Elf.Data2Lsb
            || (bytes[4] != Elf.Class32 && bytes[4] != Elf.Class64))
            throw new ElfFormatException(what + ": not a little-endian ELF file");
        bool wide = bytes[4] == Elf.Class64;
        long Word(int at) => wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        long shoff = wide ? Word(40) : Word(32);
        int shent = wide ? Elf.SectionHeader64Size : Elf.SectionHeaderSize;
        int shnum = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(wide ? 60 : 48));
        int shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(wide ? 62 : 50));
        List<(uint Name, uint Type, long Offset, long Size, uint Link)> raw = new();
        for (int i = 0; i < shnum && shoff != 0; i++)
        {
            int at = checked((int)shoff + i * shent);
            if (at + shent > bytes.Length) throw new ElfFormatException(what + ": section headers run past the end of the file");
            uint name = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            raw.Add(wide
                ? (name, type, Word(at + 24), Word(at + 32), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 40)))
                : (name, type, Word(at + 16), Word(at + 20), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 24))));
        }
        List<SectionView> sections = new();
        if (raw.Count == 0) return sections;
        if (shstrndx >= raw.Count) throw new ElfFormatException(what + ": no section name table");
        var names = raw[shstrndx];
        if (names.Offset + names.Size > bytes.Length) throw new ElfFormatException(what + ": the section name table runs past the end of the file");
        foreach (var s in raw)
        {
            if (s.Type != Elf.ShtNoBits && s.Offset + s.Size > bytes.Length) throw new ElfFormatException(what + ": a section runs past the end of the file");
            sections.Add(new SectionView(StringTable.Read(bytes.AsSpan((int)names.Offset, (int)names.Size), s.Name, ".shstrtab"), s.Type, s.Offset, s.Size, s.Link));
        }
        return sections;
    }
}
