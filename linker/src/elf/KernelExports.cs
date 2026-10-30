#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// THE KERNEL AS A LIBRARY (docs/software/DRIVERS.md in the OS repository,
/// "Modules"). A module is a shared object linked against the kernel the way
/// a program is linked against libSystem.so, and a module and a kernel of
/// different builds go together exactly when every name the module imports
/// binds: a name carries what was compiled against it, so a changed
/// declaration is a missing name, never a wrong address.
///
/// What stands in for the library is a section of the kernel's own file,
/// .corsac.exports, outside every PT_LOAD -- nothing of it is in the
/// kernel's memory. The loader opens the file the kernel booted from, finds
/// the section by its header, reads it alone when it binds a module, and
/// lets it go. Its offsets are the section's own, from its first byte:
///
///   0x0000  the dynamic array: DT_HASH, DT_SYMTAB, DT_STRTAB, DT_STRSZ,
///           DT_SYMENT, the native contract and processor (DT_CORSAC_ABI,
///           _CPU and their sizes, Elf.DtCorsacAbi), DT_NULL; each value an
///           offset in this section
///           .hash     the SysV hash over the symbols
///           .dynsym   every export, SHN_ABS, its value the kernel's address
///           .dynstr
///           the contract and the processor, as the kernel's units say them
///           the link's identity (DT_CORSAC_LINKID): 32 bytes
///
/// THE LINK'S IDENTITY is the SHA-256 of every byte the kernel's PT_LOAD
/// segments take from its file, those 32 bytes read as nought, and the
/// kernel holds it too, loaded, as __corsac_link_id (Sys.LinkId()). An
/// install that writes a new kernel over the running one's file leaves the
/// two different: the loader, comparing them, refuses to bind a module to
/// addresses of a kernel not running.
///
/// A loader reads it with what it reads a library's tables with, DT_HASH and
/// the chains, as one more image whose bias is nought. A module's link (the
/// build) checks every import against the same section of the built kernel.
/// </summary>
public sealed class KernelExports
{
    /// <summary>The kernel's export table, in its file and not its memory.</summary>
    public const string SectionName = ".corsac.exports";

    /// <summary>
    /// What a module's unit carries for its link to know it a module's: an
    /// empty note its compile against a kernel adds (Driver), which the
    /// module's link takes out.
    /// </summary>
    public const string ModuleUnitSection = ".corsac.moduleunit";

    /// <summary>The kernel's own copy of its link identity, loaded: what Sys.LinkId() answers the address of.</summary>
    public const string LinkIdSymbol = "__corsac_link_id";
    public const int LinkIdBytes = 32;

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
        // The kernel's own: a module asks the kernel, never binds to it.
        LinkIdSymbol,
    };

    public string Path { get; }
    /// <summary>The kernel's file name, for what a module's link says of it.</summary>
    public string Name { get; }
    /// <summary>The kernel units' .corsac.abi and .corsac.cpu, which a module's units must agree with; null where the kernel's had none.</summary>
    public byte[]? Abi { get; }
    public byte[]? Cpu { get; }
    public bool LongMode { get; }
    /// <summary>Every name the kernel exports.</summary>
    public HashSet<string> Names { get; }

    /// <summary>
    /// The kernel units' code-generation contract, read from Cpu: their
    /// --cpu, which a module's units must not exceed and whose SSE a
    /// module's save must cover to call the kernel's $inside copies. Null
    /// where the exports carry none.
    /// </summary>
    public X86CodeGenerationContract? Contract
    {
        get
        {
            if (Cpu is null) return null;
            ObjectFile carrier = new();
            Section cpu = new(X86CodeGenerationContract.SectionName, SectionKind.Note);
            cpu.Bytes.AddRange(Cpu);
            carrier.Sections.Add(cpu);
            return X86CodeGenerationContract.Read(carrier);
        }
    }

    private KernelExports(string path, string name, byte[]? abi, byte[]? cpu, bool longMode, HashSet<string> names)
    {
        Path = path; Name = name; Abi = abi; Cpu = cpu; LongMode = longMode; Names = names;
    }

    /// <summary>One global of a linked image: what a .dynsym entry of it says.</summary>
    public readonly record struct Export(string Name, ulong Value, ulong Size, byte Type);

    // ---- reading -------------------------------------------------------------

    /// <summary>
    /// A kernel `corc link --exports` linked: its .corsac.exports. Anything
    /// else -- a kernel linked without --exports, a library -- is refused by
    /// name, having no table to bind a module against.
    /// </summary>
    public static KernelExports Read(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException error) { throw new ElfFormatException(path + ": " + error.Message); }
        SectionView table = Sections(bytes, path).Find(s => s.Name == SectionName)
            ?? throw new ElfFormatException(path + ": not a kernel with exports (no " + SectionName + "); link it with `corc link ... --exports`");
        bool wide = bytes[4] == Elf.Class64;
        byte[] section = bytes.AsSpan((int)table.Offset, (int)table.Size).ToArray();
        int word = wide ? 8 : 4;
        long Word(int at)
        {
            if (at < 0 || at + word > section.Length) throw new ElfFormatException(path + ": " + SectionName + " is cut short");
            return wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(section.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(at));
        }
        Dictionary<long, long> tags = new();
        for (int at = 0; ; at += 2 * word)
        {
            long tag = Word(at);
            if (tag == Elf.DtNull) break;
            tags[tag] = Word(at + word);
        }
        long Tag(int tag) => tags.TryGetValue(tag, out long value) ? value : throw new ElfFormatException(path + ": " + SectionName + " has no tag " + tag);
        long symbols = Tag(Elf.DtSymTab), strings = Tag(Elf.DtStrTab), stringBytes = Tag(Elf.DtStrSz), hash = Tag(Elf.DtHash), entry = Tag(Elf.DtSymEnt);
        if (entry != (wide ? Elf.Symbol64Size : Elf.SymbolSize) || hash < 0 || hash + 8 > section.Length
            || strings < 0 || stringBytes < 0 || strings + stringBytes > section.Length)
            throw new ElfFormatException(path + ": " + SectionName + " is malformed");
        // The hash's chain count is the symbols' count.
        long count = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan((int)(hash + 4)));
        if (symbols < 0 || symbols + count * entry > section.Length) throw new ElfFormatException(path + ": " + SectionName + " is malformed");
        ReadOnlySpan<byte> names = section.AsSpan((int)strings, (int)stringBytes);
        HashSet<string> exported = new(StringComparer.Ordinal);
        for (long i = 1; i < count; i++)
            exported.Add(StringTable.Read(names, BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan((int)(symbols + i * entry))), SectionName));
        byte[]? Bytes(int at, int size)
        {
            if (!tags.TryGetValue(at, out long offset) || !tags.TryGetValue(size, out long length)) return null;
            if (offset < 0 || length < 0 || offset + length > section.Length) throw new ElfFormatException(path + ": " + SectionName + " is malformed");
            return section.AsSpan((int)offset, (int)length).ToArray();
        }
        return new KernelExports(path, System.IO.Path.GetFileName(path), Bytes(Elf.DtCorsacAbi, Elf.DtCorsacAbiSz), Bytes(Elf.DtCorsacCpu, Elf.DtCorsacCpuSz),
            wide, exported);
    }

    /// <summary>
    /// Every global a linked image DEFINES, from its .symtab: the kernel's
    /// functions, statics and type descriptors, with their addresses, less
    /// what <see cref="NeverExported"/> names and the per-image tables the
    /// linker and the code generator make (<see cref="PerImage"/>).
    /// </summary>
    public static List<Export> GlobalsOf(string imagePath) => GlobalsOf(imagePath, everything: false);

    private static List<Export> GlobalsOf(string imagePath, bool everything)
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
            if (!everything && (NeverExported.Contains(text) || PerImage(text))) continue;
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
        || name == Linker.ModuleInitName
        // A string literal is every image's own, named by its text (Lowering.SharedLiterals).
        || name.StartsWith("str_", StringComparison.Ordinal);

    // ---- writing -------------------------------------------------------------

    /// <summary>
    /// The table: <paramref name="symbols"/> in a .dynsym with a SysV hash
    /// over them, the contract and the processor, and the dynamic array that
    /// names them all ahead of them, every offset the section's own.
    /// </summary>
    public static byte[] Table(IReadOnlyList<Export> symbols, byte[]? abi, byte[]? cpu, byte[] linkId, bool wide)
    {
        if (linkId.Length != LinkIdBytes) throw new ArgumentException("a link identity is " + LinkIdBytes + " bytes");
        int symbolSize = wide ? Elf.Symbol64Size : Elf.SymbolSize;
        int word = wide ? 8 : 4;
        StringTable dynstr = new();
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

        static long Align(long at, int to) => (at + to - 1) / to * to;
        int tagCount = 7 + (abi is null ? 0 : 2) + (cpu is null ? 0 : 2);
        long hashAt = Align(tagCount * 2L * word, 8);
        long dynsymAt = Align(hashAt + (2L + buckets + count) * 4, word);
        long dynstrAt = dynsymAt + (long)count * symbolSize;
        long abiAt = Align(dynstrAt + strings.Length, 4);
        long cpuAt = Align(abiAt + (abi?.Length ?? 0), 4);
        long linkIdAt = Align(cpuAt + (cpu?.Length ?? 0), 4);
        long end = linkIdAt + LinkIdBytes;
        List<(long Tag, ulong Value)> dynamic = new()
        {
            (Elf.DtHash, (ulong)hashAt), (Elf.DtSymTab, (ulong)dynsymAt), (Elf.DtStrTab, (ulong)dynstrAt),
            (Elf.DtStrSz, (ulong)strings.Length), (Elf.DtSymEnt, (ulong)symbolSize),
        };
        if (abi is not null) { dynamic.Add((Elf.DtCorsacAbi, (ulong)abiAt)); dynamic.Add((Elf.DtCorsacAbiSz, (ulong)abi.Length)); }
        if (cpu is not null) { dynamic.Add((Elf.DtCorsacCpu, (ulong)cpuAt)); dynamic.Add((Elf.DtCorsacCpuSz, (ulong)cpu.Length)); }
        dynamic.Add((Elf.DtCorsacLinkId, (ulong)linkIdAt));
        dynamic.Add((Elf.DtNull, 0UL));

        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        void Word(ulong value) { if (wide) w.Write(value); else w.Write(checked((uint)value)); }
        void Pad(long to) { while (stream.Position < to) w.Write((byte)0); }
        foreach ((long tag, ulong value) in dynamic) { Word(unchecked((ulong)tag)); Word(value); }
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
        if (abi is not null) { Pad(abiAt); w.Write(abi); }
        if (cpu is not null) { Pad(cpuAt); w.Write(cpu); }
        Pad(linkIdAt);
        w.Write(linkId);
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// What the kernel's link adds for its identity: thirty-two bytes of
    /// .rodata, nought until the image is written (StampLinkId).
    /// </summary>
    public static ObjectFile LinkIdObject()
    {
        ObjectFile made = new();
        Section data = new(".rodata", SectionKind.ReadOnlyData) { Align = 4 };
        data.Bytes.AddRange(new byte[LinkIdBytes]);
        made.Sections.Add(data);
        made.Symbols.Add(new Symbol { Name = LinkIdSymbol, Section = data, Offset = 0, Size = LinkIdBytes });
        return made;
    }

    /// <summary>
    /// The written kernel's identity, made and put where __corsac_link_id is
    /// and in its .corsac.exports (DT_CORSAC_LINKID): the SHA-256 of what its
    /// PT_LOAD segments take from the file, in their order, the identity's
    /// own bytes nought as the link left them. Made last, the file's header
    /// -- in the first segment -- as AddTo left it: a loader hashing the
    /// file's segments, those 32 bytes nought, makes the same.
    /// </summary>
    public static byte[] StampLinkId(string imagePath)
    {
        byte[] bytes = File.ReadAllBytes(imagePath);
        bool wide = bytes[4] == Elf.Class64;
        Export id = GlobalsOf(imagePath, everything: true).Find(e => e.Name == LinkIdSymbol);
        if (id.Name is null) throw new ElfFormatException(imagePath + ": no " + LinkIdSymbol + " to stamp");
        long phoff = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
        int phent = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(wide ? 54 : 42));
        int phnum = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(wide ? 56 : 44));
        using System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long at = -1;
        for (int p = 0; p < phnum; p++)
        {
            ReadOnlySpan<byte> ph = bytes.AsSpan((int)(phoff + p * phent), phent);
            if (BinaryPrimitives.ReadUInt32LittleEndian(ph) != Elf.PtLoad) continue;
            long offset = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(ph[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(ph[4..]);
            ulong address = wide ? BinaryPrimitives.ReadUInt64LittleEndian(ph[16..]) : BinaryPrimitives.ReadUInt32LittleEndian(ph[8..]);
            long size = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(ph[32..]) : BinaryPrimitives.ReadUInt32LittleEndian(ph[16..]);
            if (offset < 0 || size < 0 || offset + size > bytes.Length) throw new ElfFormatException(imagePath + ": a segment runs past the end of the file");
            hash.AppendData(bytes, (int)offset, (int)size);
            if (id.Value >= address && id.Value + LinkIdBytes <= address + (ulong)size) at = offset + (long)(id.Value - address);
        }
        if (at < 0) throw new ElfFormatException(imagePath + ": " + LinkIdSymbol + " is in no loaded segment");
        byte[] made = hash.GetHashAndReset();
        made.CopyTo(bytes, (int)at);
        // And the table's copy, which no segment loads: the hash is the same.
        SectionView table = Sections(bytes, imagePath).Find(s => s.Name == SectionName)
            ?? throw new ElfFormatException(imagePath + ": no " + SectionName + " to stamp");
        int word = wide ? 8 : 4;
        long field = -1;
        for (long d = table.Offset; d + 2 * word <= table.Offset + table.Size; d += 2 * word)
        {
            long tag = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)d)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)d));
            if (tag == Elf.DtNull) break;
            if (tag == Elf.DtCorsacLinkId)
                field = table.Offset + (wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)d + word)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)d + word)));
        }
        if (field < table.Offset || field + LinkIdBytes > table.Offset + table.Size) throw new ElfFormatException(imagePath + ": " + SectionName + " has no room for the link's identity");
        made.CopyTo(bytes, (int)field);
        File.WriteAllBytes(imagePath, bytes);
        return made;
    }

    /// <summary>
    /// <paramref name="table"/> added to the linked image at <paramref name="imagePath"/>
    /// as .corsac.exports, a section no segment loads: its bytes and a new
    /// section name table after the file's end, and the section headers again
    /// after them, the old ones as they were. Nothing loaded moves.
    /// </summary>
    public static void AddTo(string imagePath, byte[] table)
    {
        byte[] bytes = File.ReadAllBytes(imagePath);
        bool wide = bytes[4] == Elf.Class64;
        List<SectionView> sections = Sections(bytes, imagePath);
        if (sections.Any(s => s.Name == SectionName)) throw new ElfFormatException(imagePath + ": already has " + SectionName);
        int shent = wide ? Elf.SectionHeader64Size : Elf.SectionHeaderSize;
        long shoff = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32));
        int shnum = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(wide ? 60 : 48));
        int shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(wide ? 62 : 50));
        if (shnum == 0 || shstrndx >= shnum) throw new ElfFormatException(imagePath + ": no section headers to add to");
        SectionView names = sections[shstrndx];
        byte[] oldNames = bytes.AsSpan((int)names.Offset, (int)names.Size).ToArray();
        uint nameAt = (uint)oldNames.Length;
        byte[] newNames = oldNames.Concat(System.Text.Encoding.ASCII.GetBytes(SectionName)).Append((byte)0).ToArray();

        static long Align(long at, int to) => (at + to - 1) / to * to;
        long tableAt = Align(bytes.Length, 8);
        long namesAt = tableAt + table.Length;
        long headersAt = Align(namesAt + newNames.Length, 8);
        using MemoryStream stream = new();
        stream.Write(bytes);
        while (stream.Position < tableAt) stream.WriteByte(0);
        stream.Write(table);
        stream.Write(newNames);
        while (stream.Position < headersAt) stream.WriteByte(0);
        byte[] headers = bytes.AsSpan((int)shoff, shnum * shent).ToArray();
        // The name table's header, pointed at the new one.
        Span<byte> nameHeader = headers.AsSpan(shstrndx * shent, shent);
        if (wide) { BinaryPrimitives.WriteUInt64LittleEndian(nameHeader[24..], (ulong)namesAt); BinaryPrimitives.WriteUInt64LittleEndian(nameHeader[32..], (ulong)newNames.Length); }
        else { BinaryPrimitives.WriteUInt32LittleEndian(nameHeader[16..], (uint)namesAt); BinaryPrimitives.WriteUInt32LittleEndian(nameHeader[20..], (uint)newNames.Length); }
        stream.Write(headers);
        // Name, type, flags (none: not loaded), address, offset, size, link, info, alignment, entry size.
        using (BinaryWriter w = new(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write(nameAt); w.Write(Elf.ShtProgBits);
            if (wide) { w.Write(0UL); w.Write(0UL); w.Write((ulong)tableAt); w.Write((ulong)table.Length); w.Write(0u); w.Write(0u); w.Write(8UL); w.Write(0UL); }
            else { w.Write(0u); w.Write(0u); w.Write((uint)tableAt); w.Write((uint)table.Length); w.Write(0u); w.Write(0u); w.Write(8u); w.Write(0u); }
        }
        byte[] result = stream.ToArray();
        if (wide) BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(40), (ulong)headersAt);
        else BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32), checked((uint)headersAt));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(wide ? 60 : 48), checked((ushort)(shnum + 1)));
        File.WriteAllBytes(imagePath, result);
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
        if (shoff < 0 || shoff > bytes.Length || (long)shnum * shent > bytes.Length - shoff)
            throw new ElfFormatException(what + ": section headers run past the end of the file");
        List<(uint Name, uint Type, long Offset, long Size, uint Link)> raw = new();
        for (int i = 0; i < shnum && shoff != 0; i++)
        {
            int at = (int)shoff + i * shent;
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
            if (s.Type != Elf.ShtNoBits && (s.Offset < 0 || s.Size < 0 || s.Offset > bytes.Length || s.Size > bytes.Length - s.Offset))
                throw new ElfFormatException(what + ": a section runs past the end of the file");
            sections.Add(new SectionView(StringTable.Read(bytes.AsSpan((int)names.Offset, (int)names.Size), s.Name, ".shstrtab"), s.Type, s.Offset, s.Size, s.Link));
        }
        return sections;
    }
}
