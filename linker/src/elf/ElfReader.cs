#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// Reads a relocatable ELF32 i386 object -- ours, or one from gcc/as --
/// into the in-memory form the linker consumes. The inverse of
/// <see cref="ElfWriter"/>: what it wrote reads back to an object that
/// writes to the same bytes.
///
/// What it refuses, it refuses loudly: an unknown relocation type, a
/// common symbol, an absolute symbol, an allocated section of a type we do
/// not lay out. Silently dropping any of those would produce an executable
/// that is wrong in a way nobody would find quickly.
/// </summary>
public static class ElfReader
{
    /// <summary>
    /// A linked image's dynamic tables, read through its section headers
    /// from either class: an i386 image (ELF32) or an x86-64 one (ELF64).
    /// Everything this reads is a file a linker wrote -- this one or GNU ld
    /// -- and section headers are the simpler road to the same tables than
    /// PT_DYNAMIC.
    /// </summary>
    private sealed class DynamicView
    {
        public readonly List<(string Name, ushort Shndx, ulong Value)> Symbols = new();
        public readonly List<(long Tag, ulong Value)> Entries = new();
        public ReadOnlyMemory<byte> Strings = ReadOnlyMemory<byte>.Empty;
        public ulong LowAddress = ulong.MaxValue;
        public bool HasDynamic;

        public string Text(ulong at, string what) => StringTable.Read(Strings.Span, checked((uint)at), what);

        public static DynamicView Read(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            ReadOnlySpan<byte> f = bytes;
            if (f.Length < Elf.HeaderSize || !f[..4].SequenceEqual(Elf.Magic) || f[5] != Elf.Data2Lsb
                || (f[4] != Elf.Class32 && f[4] != Elf.Class64))
            {
                throw new ElfFormatException("not a little-endian ELF file");
            }
            bool wide = f[4] == Elf.Class64;
            if (wide && f.Length < Elf.Header64Size) throw new ElfFormatException("ELF64 header is truncated");
            ulong Word(int at) => wide ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            ulong phoff = wide ? Word(32) : Word(28);
            ulong shoff = wide ? Word(40) : Word(32);
            int phent = wide ? Elf.ProgramHeader64Size : Elf.ProgramHeaderSize;
            int shent = wide ? Elf.SectionHeader64Size : Elf.SectionHeaderSize;
            ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(f[(wide ? 56 : 44)..]);
            ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(f[(wide ? 60 : 48)..]);
            DynamicView view = new();
            for (int i = 0; i < phnum; i++)
            {
                int at = checked((int)phoff + i * phent);
                if (at + phent > f.Length) break;
                if (BinaryPrimitives.ReadUInt32LittleEndian(f[at..]) != Elf.PtLoad) continue;
                ulong vaddr = wide ? Word(at + 16) : Word(at + 8);
                view.LowAddress = Math.Min(view.LowAddress, vaddr);
            }
            if (view.LowAddress == ulong.MaxValue) view.LowAddress = 0;

            // (type, offset, size, link) of every section.
            List<(uint Type, ulong Offset, ulong Size, uint Link)> sections = new();
            for (int i = 0; i < shnum && shoff != 0; i++)
            {
                int at = checked((int)shoff + i * shent);
                if (at + shent > f.Length) throw new ElfFormatException("section headers run past the end of the file");
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(f[(at + 4)..]);
                sections.Add(wide
                    ? (type, Word(at + 24), Word(at + 32), BinaryPrimitives.ReadUInt32LittleEndian(f[(at + 40)..]))
                    : (type, Word(at + 16), Word(at + 20), BinaryPrimitives.ReadUInt32LittleEndian(f[(at + 24)..])));
            }
            ReadOnlySpan<byte> Content((uint Type, ulong Offset, ulong Size, uint Link) s)
            {
                if (s.Offset + s.Size > (ulong)bytes.Length) throw new ElfFormatException("a section runs past the end of the file");
                return bytes.AsSpan((int)s.Offset, (int)s.Size);
            }
            int dynsym = sections.FindIndex(s => s.Type == Elf.ShtDynSym);
            int dynamic = sections.FindIndex(s => s.Type == Elf.ShtDynamic);
            int strings = dynsym >= 0 ? (int)sections[dynsym].Link : dynamic >= 0 ? (int)sections[dynamic].Link : 0;
            if (strings > 0 && strings < sections.Count) view.Strings = Content(sections[strings]).ToArray();
            if (dynsym >= 0 && strings > 0)
            {
                ReadOnlySpan<byte> table = Content(sections[dynsym]);
                int size = wide ? Elf.Symbol64Size : Elf.SymbolSize;
                for (int at = 0; at + size <= table.Length; at += size)
                {
                    uint name = BinaryPrimitives.ReadUInt32LittleEndian(table[at..]);
                    ushort shndx = BinaryPrimitives.ReadUInt16LittleEndian(table[(at + (wide ? 6 : 14))..]);
                    ulong value = wide ? BinaryPrimitives.ReadUInt64LittleEndian(table[(at + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(table[(at + 4)..]);
                    if (name == 0) continue;
                    view.Symbols.Add((view.Text(name, ".dynsym"), shndx, value));
                }
            }
            if (dynamic >= 0)
            {
                view.HasDynamic = true;
                ReadOnlySpan<byte> table = Content(sections[dynamic]);
                int size = wide ? 16 : 8;
                for (int at = 0; at + size <= table.Length; at += size)
                {
                    long tag = wide ? BinaryPrimitives.ReadInt64LittleEndian(table[at..]) : BinaryPrimitives.ReadInt32LittleEndian(table[at..]);
                    ulong value = wide ? BinaryPrimitives.ReadUInt64LittleEndian(table[(at + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(table[(at + 4)..]);
                    if (tag == Elf.DtNull) break;
                    view.Entries.Add((tag, value));
                }
            }
            return view;
        }
    }

    /// <summary>
    /// The name a shared object calls itself: its DT_SONAME, or null if it
    /// has none or is not a shared object at all. A stripped-to-the-segments
    /// library answers null and the caller falls back to the file name,
    /// which is what the loader would have looked for anyway.
    /// </summary>
    public static string? SoNameOf(byte[] bytes)
    {
        DynamicView view = DynamicView.Read(bytes);
        foreach ((long tag, ulong value) in view.Entries)
        {
            if (tag == Elf.DtSoName) return view.Text(value, "DT_SONAME");
        }
        return null;
    }

    /// <summary>
    /// Every name a shared object DEFINES, out of its .dynsym: what a
    /// consumer must not compile a second copy of, and what it may leave
    /// for the loader to supply.
    /// </summary>
    public static List<string> ExportsOf(byte[] bytes) => DynamicNames(bytes, defined: true);

    /// <summary>Names the dynamic loader must resolve from other images.</summary>
    public static List<string> ImportsOf(byte[] bytes) => DynamicNames(bytes, defined: false);

    /// <summary>
    /// What a linked shared object says about itself, for prebinding: its
    /// soname, the libraries it needs, its checksum tag, the lowest address
    /// it was laid out at, and every symbol it exports with its address.
    /// </summary>
    public sealed record SharedImageInfo(string? Soname, List<string> Needed, uint Checksum, uint LowAddress, Dictionary<string, uint> Exports);

    public static SharedImageInfo ReadSharedImage(byte[] bytes)
    {
        DynamicView view = DynamicView.Read(bytes);
        Dictionary<string, uint> exports = new(StringComparer.Ordinal);
        foreach ((string name, ushort shndx, ulong value) in view.Symbols)
        {
            if (shndx != Elf.ShnUndef && value <= uint.MaxValue) exports.TryAdd(name, (uint)value);
        }
        List<string> needed = new();
        string? soname = null;
        uint checksum = 0;
        foreach ((long tag, ulong value) in view.Entries)
        {
            if (tag == Elf.DtNeeded) needed.Add(view.Text(value, ".dynstr"));
            else if (tag == Elf.DtSoName) soname = view.Text(value, ".dynstr");
            else if (tag == Elf.DtCorsacChecksum) checksum = (uint)value;
        }
        // An image laid out above 4 GiB has no preferred address prebinding can use.
        uint low = view.LowAddress <= uint.MaxValue ? (uint)view.LowAddress : 0;
        return new SharedImageInfo(soname, needed, checksum, low, exports);
    }

    private static List<string> DynamicNames(byte[] bytes, bool defined)
    {
        List<string> names = new();
        foreach ((string name, ushort shndx, _) in DynamicView.Read(bytes).Symbols)
        {
            if ((shndx != Elf.ShnUndef) == defined) names.Add(name);
        }
        return names;
    }

    /// <summary>The file ReadObjectFile is reading, on this thread: its IR sections are left there.</summary>
    [ThreadStatic] internal static string? BackingPath;

    /// <summary>
    /// WHAT A LINK LEAVES IN THE FILE: a unit's IR, and the notes the link
    /// reads once or twice and takes out before the image is laid out -- the
    /// lifetime and region hints, the managed layouts, the coalescing
    /// contract. Held in memory, the compiler's own link kept 170 MB of
    /// notes, half of what its objects took, from the first phase to the last.
    /// </summary>
    internal static bool LeftInFile(string name)
        => name is Corsac.Lang.Lto.IrArchive.SectionName or Corsac.Lang.Lto.LifetimeHints.SectionName
            or Corsac.Lang.Lto.RegionHints.SectionName or ManagedLayoutContract.SectionName
            or Corsac.Lang.Lto.CoalescingContract.SectionName;

    /// <summary>
    /// An object read from a file that stays where it is while the object is
    /// used: its IR archive is not copied into memory but read from the file
    /// a record at a time, and the object knows its file (SourcePath), so a
    /// link hands the file itself to the backend rather than a copy.
    /// </summary>
    public static ObjectFile ReadObjectFile(string path)
    {
        string full = Path.GetFullPath(path);
        BackingPath = full;
        try
        {
            ObjectFile obj = ReadObject(File.ReadAllBytes(full));
            obj.SourcePath = full;
            return obj;
        }
        finally { BackingPath = null; }
    }

    public static ObjectFile ReadObject(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ReadOnlySpan<byte> f = bytes;

        if (f.Length < Elf.HeaderSize || !f[..4].SequenceEqual(Elf.Magic))
        {
            throw new ElfFormatException("not an ELF file");
        }
        if (f[4] == Elf.Class64 && f[5] == Elf.Data2Lsb)
        {
            return Elf64Object.Read(bytes);
        }
        if (f[4] != Elf.Class32)
        {
            throw new ElfFormatException("not a 32-bit ELF file");
        }
        if (f[5] != Elf.Data2Lsb)
        {
            throw new ElfFormatException("not a little-endian ELF file");
        }
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(f[16..]);
        if (type != Elf.TypeRel)
        {
            throw new ElfFormatException($"not a relocatable object (e_type = {type})");
        }
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(f[18..]);
        if (machine != Elf.MachineI386)
        {
            throw new ElfFormatException($"not an i386 object (e_machine = {machine})");
        }
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(f[32..]);
        ushort shentsize = BinaryPrimitives.ReadUInt16LittleEndian(f[46..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(f[48..]);
        ushort shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(f[50..]);
        if (shnum == 0)
        {
            throw new ElfFormatException("object has no section headers");
        }
        if (shentsize != Elf.SectionHeaderSize)
        {
            throw new ElfFormatException($"section header entry size is {shentsize}, expected {Elf.SectionHeaderSize}");
        }

        SectionHeader[] sh = new SectionHeader[shnum];
        for (int i = 0; i < shnum; i++)
        {
            sh[i] = SectionHeader.Read(Slice(f, shoff + (uint)(i * Elf.SectionHeaderSize), Elf.SectionHeaderSize, $"section header {i}"));
        }
        if (shstrndx >= shnum || sh[shstrndx].Type != Elf.ShtStrTab)
        {
            throw new ElfFormatException("section name string table is missing");
        }
        ReadOnlySpan<byte> shstr = Content(f, sh[shstrndx], "section name table");
        string[] names = new string[shnum];
        for (int i = 0; i < shnum; i++)
        {
            names[i] = StringTable.Read(shstr, sh[i].Name, $"section {i}");
        }

        ObjectFile obj = new();
        Section?[] bySh = new Section?[shnum];
        HashSet<string> seen = new();
        for (int i = 1; i < shnum; i++)
        {
            SectionHeader h = sh[i];
            if (names[i] == Elf.GnuStackNote)
            {
                continue;
            }
            SectionKind? kind = Classify(h, names[i]);
            if (kind is null)
            {
                continue;
            }
            if (!seen.Add(names[i]))
            {
                throw new ElfFormatException($"two sections are named '{names[i]}'");
            }
            Section s = new(names[i], kind.Value) { Align = h.AddrAlign == 0 ? 1 : checked((int)h.AddrAlign) };
            if (h.Type == Elf.ShtNoBits)
            {
                s.ZeroBytes = checked((int)h.Size);
            }
            else
            {
                // A unit's IR stays in its file when the file is known: a link
                // reads it a record at a time (IrArchive). So do the notes a
                // link reads and drops (LeftInFile).
                if (ElfReader.BackingPath is string backing && LeftInFile(names[i]))
                {
                    Content(f, h, $"section '{names[i]}'");
                    s.FileBacked = (backing, (long)h.Offset, checked((int)h.Size));
                }
                else s.Bytes.AddRange(Content(f, h, $"section '{names[i]}'"));
            }
            obj.Sections.Add(s);
            bySh[i] = s;
        }

        int symtabIndex = -1;
        for (int i = 1; i < shnum; i++)
        {
            if (sh[i].Type != Elf.ShtSymTab)
            {
                continue;
            }
            if (symtabIndex >= 0)
            {
                throw new ElfFormatException("object has more than one symbol table");
            }
            symtabIndex = i;
        }

        // Symbols. Section symbols are not given back as symbols: a section's
        // name stands for its start, and relocations against them use it.
        SymbolEntry[] syms = Array.Empty<SymbolEntry>();
        string[] symNames = Array.Empty<string>();
        Section?[] sectionSym = Array.Empty<Section?>();
        if (symtabIndex >= 0)
        {
            SectionHeader h = sh[symtabIndex];
            if (h.EntSize != Elf.SymbolSize)
            {
                throw new ElfFormatException($"symbol entry size is {h.EntSize}, expected {Elf.SymbolSize}");
            }
            if (h.Link >= shnum || sh[h.Link].Type != Elf.ShtStrTab)
            {
                throw new ElfFormatException("symbol table has no string table");
            }
            ReadOnlySpan<byte> strtab = Content(f, sh[h.Link], "symbol string table");
            ReadOnlySpan<byte> table = Content(f, h, "symbol table");
            int count = (int)(h.Size / Elf.SymbolSize);
            syms = new SymbolEntry[count];
            symNames = new string[count];
            sectionSym = new Section?[count];
            HashSet<string> symbolNames = new();
            for (int j = 1; j < count; j++)
            {
                SymbolEntry e = SymbolEntry.Read(table[(j * Elf.SymbolSize)..]);
                syms[j] = e;
                symNames[j] = StringTable.Read(strtab, e.Name, $"symbol {j}");
                if (e.Type == Elf.SttSection)
                {
                    if (e.Shndx < shnum)
                    {
                        sectionSym[j] = bySh[e.Shndx];
                    }
                    continue;
                }
                if (e.Type == Elf.SttFile || symNames[j].Length == 0)
                {
                    continue;
                }
                string name = symNames[j];
                Section? section;
                switch (e.Shndx)
                {
                    case Elf.ShnUndef:
                        if (e.Bind == Elf.StbLocal)
                        {
                            throw new ElfFormatException($"local symbol '{name}' is undefined");
                        }
                        section = null;
                        break;
                    case Elf.ShnAbs:
                        throw new ElfFormatException($"symbol '{name}' is absolute, which the object model cannot express");
                    case Elf.ShnCommon:
                        throw new ElfFormatException($"symbol '{name}' is a common symbol; compile with -fno-common");
                    default:
                        if (e.Shndx >= Elf.ShnLoReserve)
                        {
                            throw new ElfFormatException($"symbol '{name}' has reserved section index 0x{e.Shndx:x}");
                        }
                        if (e.Shndx >= shnum || bySh[e.Shndx] is null)
                        {
                            throw new ElfFormatException($"symbol '{name}' is in section '{(e.Shndx < shnum ? names[e.Shndx] : "?")}', which cannot be linked");
                        }
                        section = bySh[e.Shndx];
                        break;
                }
                if (!symbolNames.Add(name))
                {
                    throw new ElfFormatException($"two symbols are named '{name}'");
                }
                // Weak is not modelled; a weak definition is taken as a
                // strong one, which is right until two objects supply it.
                obj.Symbols.Add(new Symbol
                {
                    Name = name,
                    Section = section,
                    Offset = (long)e.Value,
                    Size = e.Size,
                    IsFunction = e.Type == Elf.SttFunc,
                    Global = e.Bind != Elf.StbLocal,
                });
            }
        }

        for (int i = 1; i < shnum; i++)
        {
            SectionHeader h = sh[i];
            if (h.Type == Elf.ShtRela)
            {
                throw new ElfFormatException($"section '{names[i]}' is RELA; i386 objects use REL");
            }
            if (h.Type != Elf.ShtRel)
            {
                continue;
            }
            if (h.Info >= shnum)
            {
                throw new ElfFormatException($"'{names[i]}' relocates section {h.Info}, which does not exist");
            }
            Section? target = bySh[h.Info];
            if (target is null)
            {
                // Relocations for a section we dropped (a .group, say) go with it.
                continue;
            }
            if ((int)h.Link != symtabIndex)
            {
                throw new ElfFormatException($"'{names[i]}' uses symbol table {h.Link}, not the object's");
            }
            if (h.EntSize != Elf.RelSize)
            {
                throw new ElfFormatException($"'{names[i]}' has entry size {h.EntSize}, expected {Elf.RelSize}");
            }
            if (target.Kind == SectionKind.Uninitialised)
            {
                throw new ElfFormatException($"'{names[i]}' relocates '{target.Name}', which has no contents");
            }
            ReadOnlySpan<byte> table = Content(f, h, $"'{names[i]}'");
            int count = (int)(h.Size / Elf.RelSize);
            // Sized once from the table: a link reads every object's relocations
            // and keeps them, and grown by doubling they kept up to twice their size.
            target.Relocs.EnsureCapacity(target.Relocs.Count + count);
            for (int j = 0; j < count; j++)
            {
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(table[(j * Elf.RelSize)..]);
                uint info = BinaryPrimitives.ReadUInt32LittleEndian(table[(j * Elf.RelSize + 4)..]);
                int symIdx = (int)(info >> 8);
                byte rtype = (byte)(info & 0xff);
                RelocKind? kind = Elf.RelocKindOf(rtype);
                if (kind is null)
                {
                    throw new ElfFormatException($"unsupported relocation type {rtype} in '{target.Name}' at 0x{offset:x}");
                }
                if (offset + 4 > (uint)target.Bytes.Count)
                {
                    throw new ElfFormatException($"relocation at 0x{offset:x} is outside '{target.Name}' ({target.Bytes.Count} bytes)");
                }
                string symbol;
                if (symIdx == 0)
                {
                    if (kind != RelocKind.Relative)
                    {
                        throw new ElfFormatException($"relocation in '{target.Name}' at 0x{offset:x} names no symbol");
                    }
                    symbol = "";
                }
                else if (symIdx >= syms.Length)
                {
                    throw new ElfFormatException($"relocation in '{target.Name}' at 0x{offset:x} names symbol {symIdx}, which does not exist");
                }
                else if (syms[symIdx].Type == Elf.SttSection)
                {
                    symbol = sectionSym[symIdx]?.Name
                        ?? throw new ElfFormatException($"relocation in '{target.Name}' at 0x{offset:x} is against a section that cannot be linked");
                }
                else
                {
                    symbol = symNames[symIdx];
                    if (symbol.Length == 0)
                    {
                        throw new ElfFormatException($"relocation in '{target.Name}' at 0x{offset:x} is against an unnamed symbol");
                    }
                }
                // REL: the addend is whatever sits in the word -- of a section
                // left in its file too, which then comes into memory.
                if (target.FileBacked is not null) { target.Bytes.AddRange(target.Content()); target.FileBacked = null; }
                int addend = BinaryPrimitives.ReadInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(target.Bytes)[(int)offset..]);
                target.Relocs.Add(new Relocation((int)offset, symbol, addend, kind.Value));
            }
        }

        return obj;
    }

    /// <summary>
    /// What an input section is to us, or null to drop it. Allocated
    /// sections of a type we cannot lay out are an error, because dropping
    /// one (an .init_array, say) would change what the program does.
    /// </summary>
    private static SectionKind? Classify(in SectionHeader h, string name)
    {
        bool alloc = (h.Flags & Elf.ShfAlloc) != 0;
        if (h.Type == Elf.ShtNoBits)
        {
            return alloc ? SectionKind.Uninitialised : null;
        }
        if (h.Type != Elf.ShtProgBits && h.Type != Elf.ShtNote)
        {
            if (alloc)
            {
                throw new ElfFormatException($"section '{name}' is allocated but of type {h.Type}, which cannot be linked");
            }
            return null;
        }
        if (!alloc)
        {
            return SectionKind.Note;
        }
        if ((h.Flags & Elf.ShfExecInstr) != 0)
        {
            return SectionKind.Code;
        }
        if ((h.Flags & Elf.ShfWrite) != 0)
        {
            return SectionKind.Data;
        }
        return SectionKind.ReadOnlyData;
    }

    private static ReadOnlySpan<byte> Content(ReadOnlySpan<byte> f, in SectionHeader h, string what)
    {
        if (h.Type == Elf.ShtNoBits)
        {
            return ReadOnlySpan<byte>.Empty;
        }
        return Slice(f, h.Offset, h.Size, what);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> f, uint offset, uint size, string what)
    {
        if ((ulong)offset + size > (ulong)f.Length)
        {
            throw new ElfFormatException($"{what}: 0x{offset:x}+0x{size:x} is past the end of the file ({f.Length} bytes)");
        }
        return f.Slice((int)offset, (int)size);
    }
}
