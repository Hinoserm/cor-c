#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// Relocatable objects for x86-64: ELF64, EM_X86_64, with RELA relocations.
///
/// The same object model as the i386 objects ElfWriter and ElfReader handle,
/// in the other encoding: 64-bit headers and symbols, and a relocation that
/// carries its addend in its own entry rather than in the bytes it patches.
/// Which one an object is written as comes from its ABI note (long mode or
/// not); which one a file is read as comes from its ELF class.
/// </summary>
internal static class Elf64Object
{
    public const uint ShtRela = 4;
    public const int RelaSize = 24;

    // The x86-64 relocation types the object model has.
    public const uint R64 = 1, Pc32 = 2, Plt32 = 4, GlobDat = 6, JumpSlot = 7, Relative = 8, GotPcRel = 9, R32 = 10, R32S = 11,
        GotPcRelX = 41, RexGotPcRelX = 42;

    /// <summary>The x86-64 number of a relocation the loader performs, in a dynamic image's RELA tables.</summary>
    public static uint DynamicTypeOf(RelocKind kind) => kind switch
    {
        RelocKind.Abs64 => R64,
        RelocKind.GlobData => GlobDat,
        RelocKind.JumpSlot => JumpSlot,
        RelocKind.Relative => Relative,
        _ => throw new ElfFormatException($"{kind} is not a relocation the x86-64 loader performs"),
    };

    public static uint TypeOf(RelocKind kind) => kind switch
    {
        RelocKind.Abs64 => R64,
        RelocKind.Rel32 => Pc32,
        RelocKind.Plt32 => Plt32,
        RelocKind.Abs32 => R32,
        RelocKind.GotPcRel => RexGotPcRelX,
        RelocKind.Abs32S => R32S,
        _ => throw new ElfFormatException($"{kind} has no x86-64 relocation in a static object"),
    };

    public static RelocKind? KindOf(uint type) => type switch
    {
        R64 => RelocKind.Abs64,
        Pc32 => RelocKind.Rel32,
        Plt32 => RelocKind.Plt32,
        R32 => RelocKind.Abs32,
        GotPcRel or GotPcRelX or RexGotPcRelX => RelocKind.GotPcRel,
        R32S => RelocKind.Abs32S,
        _ => null,
    };

    public static byte[] Write(ObjectFile obj)
    {
        Dictionary<string, int> sectionIndex = new();
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            Section s = obj.Sections[i];
            if (!sectionIndex.TryAdd(s.Name, i + 1))
            {
                throw new ElfFormatException($"duplicate section name '{s.Name}'");
            }
            if (s.Kind == SectionKind.Uninitialised && s.Bytes.Count != 0)
            {
                throw new ElfFormatException($"section '{s.Name}' is uninitialised but has {s.Bytes.Count} bytes of content");
            }
        }

        StringTable strtab = new();
        List<(uint Name, byte Info, ushort Shndx, ulong Value, ulong Size)> symbols = new() { default };
        Dictionary<string, int> symbolIndex = new();
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            symbols.Add((0, SymbolEntry.MakeInfo(Elf.StbLocal, Elf.SttSection), (ushort)(i + 1), 0, 0));
        }
        foreach (bool global in new[] { false, true })
        {
            foreach (Symbol sym in obj.Symbols)
            {
                if (sym.Global != global)
                {
                    continue;
                }
                if (sym.Name.Length == 0)
                {
                    throw new ElfFormatException("a symbol has no name");
                }
                if (!sym.Global && !sym.IsDefined)
                {
                    throw new ElfFormatException($"local symbol '{sym.Name}' is undefined; only a global can be");
                }
                ushort shndx = Elf.ShnUndef;
                if (sym.Section is not null)
                {
                    int at = obj.Sections.IndexOf(sym.Section);
                    if (at < 0)
                    {
                        throw new ElfFormatException($"symbol '{sym.Name}' is in section '{sym.Section.Name}', which is not in this object");
                    }
                    shndx = (ushort)(at + 1);
                }
                if (!symbolIndex.TryAdd(sym.Name, symbols.Count))
                {
                    throw new ElfFormatException($"duplicate symbol '{sym.Name}'");
                }
                byte bind = sym.Global ? Elf.StbGlobal : Elf.StbLocal;
                symbols.Add((strtab.Add(sym.Name), SymbolEntry.MakeInfo(bind, Elf.SymbolType(sym)), shndx, (ulong)sym.Offset, (ulong)sym.Size));
            }
        }
        int firstGlobal = 1 + obj.Sections.Count + obj.Symbols.Count(s => !s.Global);

        List<(ulong Offset, ulong Info, long Addend)>[] relas = new List<(ulong, ulong, long)>[obj.Sections.Count];
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            Section s = obj.Sections[i];
            relas[i] = new();
            foreach (Relocation r in s.Relocs)
            {
                int width = r.Kind == RelocKind.Abs64 ? 8 : 4;
                if (r.Offset < 0 || r.Offset + width > s.Bytes.Count)
                {
                    throw new ElfFormatException($"relocation at 0x{r.Offset:x} is outside section '{s.Name}' ({s.Bytes.Count} bytes)");
                }
                if (!symbolIndex.TryGetValue(r.Symbol, out int sym) && !sectionIndex.TryGetValue(r.Symbol, out sym))
                {
                    if (r.Symbol.Length == 0)
                    {
                        throw new ElfFormatException($"relocation in '{s.Name}' at 0x{r.Offset:x} names no symbol");
                    }
                    sym = symbols.Count;
                    symbols.Add((strtab.Add(r.Symbol), SymbolEntry.MakeInfo(Elf.StbGlobal, Elf.SttNoType), Elf.ShnUndef, 0, 0));
                    symbolIndex[r.Symbol] = sym;
                }
                relas[i].Add(((ulong)r.Offset, ((ulong)sym << 32) | TypeOf(r.Kind), r.Addend));
            }
        }

        StringTable shstrtab = new();
        uint[] sectionName = new uint[obj.Sections.Count];
        uint[] relaName = new uint[obj.Sections.Count];
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            sectionName[i] = shstrtab.Add(obj.Sections[i].Name);
            if (relas[i].Count != 0)
            {
                relaName[i] = shstrtab.Add(".rela" + obj.Sections[i].Name);
            }
        }
        uint gnuStackName = shstrtab.Add(Elf.GnuStackNote);
        uint symtabName = shstrtab.Add(".symtab");
        uint strtabName = shstrtab.Add(".strtab");
        uint shstrtabName = shstrtab.Add(".shstrtab");

        ElfBuffer b = new();
        b.Bytes(Elf.Magic);
        b.U8(Elf.Class64);
        b.U8(Elf.Data2Lsb);
        b.U8(Elf.VersionCurrent);
        b.U8(Elf.OsAbiSysV);
        b.U8(0);
        b.Zeros(7);
        b.U16(Elf.TypeRel);
        b.U16(Elf.MachineX86_64);
        b.U32(Elf.VersionCurrent);
        b.U64(0);                          // e_entry
        b.U64(0);                          // e_phoff
        b.U64(0);                          // e_shoff, patched below
        b.U32(0);
        b.U16(Elf.Header64Size);
        b.U16(0);                          // no program headers in an object
        b.U16(0);
        b.U16(Elf.SectionHeader64Size);
        b.U16(0);                          // e_shnum, patched
        b.U16(0);                          // e_shstrndx, patched

        List<(uint Name, uint Type, ulong Flags, ulong Offset, ulong Size, uint Link, uint Info, ulong Align, ulong EntSize)> headers = new() { default };
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            Section s = obj.Sections[i];
            (uint type, uint flags) = Elf.SectionTypeAndFlags(s.Kind);
            uint align = (uint)Math.Max(1, s.Align);
            if ((align & (align - 1)) != 0 || align > 0x1000)
            {
                throw new InvalidOperationException($"section {s.Name}: alignment 0x{align:x} is not a power of two up to 0x1000");
            }
            uint at = b.AlignTo(align);
            if (s.Kind != SectionKind.Uninitialised)
            {
                b.Bytes(s.Content());
            }
            headers.Add((sectionName[i], type, flags, at, (ulong)s.Size, 0, 0, align, 0));
        }

        int symtabIndex = headers.Count + relas.Count(r => r.Count != 0) + 1;
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            if (relas[i].Count == 0)
            {
                continue;
            }
            uint at = b.AlignTo(8);
            foreach ((ulong offset, ulong info, long addend) in relas[i])
            {
                b.U64(offset);
                b.U64(info);
                b.U64((ulong)addend);
            }
            headers.Add((relaName[i], ShtRela, Elf.ShfInfoLink, at, (ulong)(relas[i].Count * RelaSize), (uint)symtabIndex, (uint)(i + 1), 8, RelaSize));
        }
        headers.Add((gnuStackName, Elf.ShtProgBits, 0, (ulong)b.Length, 0, 0, 0, 1, 0));
        {
            uint at = b.AlignTo(8);
            foreach (var e in symbols)
            {
                b.U32(e.Name);
                b.U8(e.Info);
                b.U8(0);
                b.U16(e.Shndx);
                b.U64(e.Value);
                b.U64(e.Size);
            }
            headers.Add((symtabName, Elf.ShtSymTab, 0, at, (ulong)(symbols.Count * Elf.Symbol64Size), (uint)(headers.Count + 1), (uint)firstGlobal, 8, Elf.Symbol64Size));
        }
        {
            byte[] bytes = strtab.ToArray();
            headers.Add((strtabName, Elf.ShtStrTab, 0, (ulong)b.Length, (ulong)bytes.Length, 0, 0, 1, 0));
            b.Bytes(bytes);
        }
        {
            byte[] bytes = shstrtab.ToArray();
            headers.Add((shstrtabName, Elf.ShtStrTab, 0, (ulong)b.Length, (ulong)bytes.Length, 0, 0, 1, 0));
            b.Bytes(bytes);
        }
        uint shoff = b.AlignTo(8);
        foreach (var h in headers)
        {
            b.U32(h.Name);
            b.U32(h.Type);
            b.U64(h.Flags);
            b.U64(0);                      // sh_addr
            b.U64(h.Offset);
            b.U64(h.Size);
            b.U32(h.Link);
            b.U32(h.Info);
            b.U64(h.Align);
            b.U64(h.EntSize);
        }
        byte[] file = b.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(40), shoff);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(60), (ushort)headers.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(62), (ushort)(headers.Count - 1));
        return file;
    }

    public static ObjectFile Read(byte[] bytes)
    {
        ReadOnlySpan<byte> f = bytes;
        if (f.Length < Elf.Header64Size)
        {
            throw new ElfFormatException("truncated ELF64 header");
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(f[16..]) != Elf.TypeRel)
        {
            throw new ElfFormatException("not a relocatable object");
        }
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(f[18..]);
        if (machine != Elf.MachineX86_64)
        {
            throw new ElfFormatException($"not an x86-64 object (e_machine = {machine})");
        }
        ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(f[40..]);
        ushort shentsize = BinaryPrimitives.ReadUInt16LittleEndian(f[58..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(f[60..]);
        ushort shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(f[62..]);
        if (shnum == 0 || shentsize != Elf.SectionHeader64Size)
        {
            throw new ElfFormatException("object has no usable section headers");
        }

        SectionHeader[] sh = new SectionHeader[shnum];
        for (int i = 0; i < shnum; i++)
        {
            ReadOnlySpan<byte> h = Slice(f, shoff + (ulong)(i * Elf.SectionHeader64Size), Elf.SectionHeader64Size, $"section header {i}");
            sh[i] = new SectionHeader(
                BinaryPrimitives.ReadUInt32LittleEndian(h),
                BinaryPrimitives.ReadUInt32LittleEndian(h[4..]),
                checked((uint)BinaryPrimitives.ReadUInt64LittleEndian(h[8..])),
                checked((uint)BinaryPrimitives.ReadUInt64LittleEndian(h[16..])),
                checked((uint)BinaryPrimitives.ReadUInt64LittleEndian(h[24..])),
                checked((uint)BinaryPrimitives.ReadUInt64LittleEndian(h[32..])),
                BinaryPrimitives.ReadUInt32LittleEndian(h[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(h[44..]),
                checked((uint)BinaryPrimitives.ReadUInt64LittleEndian(h[48..])),
                checked((uint)BinaryPrimitives.ReadUInt64LittleEndian(h[56..])));
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
            if (names[i] == Elf.GnuStackNote || h.Type is Elf.ShtSymTab or Elf.ShtStrTab or ShtRela)
            {
                continue;
            }
            bool alloc = (h.Flags & Elf.ShfAlloc) != 0;
            SectionKind kind;
            if (h.Type == Elf.ShtNoBits)
            {
                if (!alloc) continue;
                kind = SectionKind.Uninitialised;
            }
            else if (h.Type is Elf.ShtProgBits or Elf.ShtNote)
            {
                kind = !alloc ? SectionKind.Note
                    : (h.Flags & Elf.ShfExecInstr) != 0 ? SectionKind.Code
                    : (h.Flags & Elf.ShfWrite) != 0 ? SectionKind.Data
                    : SectionKind.ReadOnlyData;
            }
            else if (alloc)
            {
                throw new ElfFormatException($"section '{names[i]}' is allocated but of type {h.Type}, which cannot be linked");
            }
            else
            {
                continue;
            }
            if (!seen.Add(names[i]))
            {
                throw new ElfFormatException($"two sections are named '{names[i]}'");
            }
            Section s = new(names[i], kind) { Align = h.AddrAlign == 0 ? 1 : checked((int)h.AddrAlign) };
            if (h.Type == Elf.ShtNoBits)
            {
                s.ZeroBytes = checked((int)h.Size);
            }
            else
            {
                // A unit's IR stays in its file when the file is known: a link
                // reads it a record at a time (IrArchive).
                if (ElfReader.BackingPath is string backing && names[i] == Corsac.Lang.Lto.IrArchive.SectionName)
                {
                    Content(f, h, $"section '{names[i]}'");
                    s.FileBacked = (backing, (long)h.Offset, checked((int)h.Size));
                }
                else s.Bytes.AddRange(Content(f, h, $"section '{names[i]}'"));
            }
            obj.Sections.Add(s);
            bySh[i] = s;
        }

        int symtab = Array.FindIndex(sh, h => h.Type == Elf.ShtSymTab);
        string[] symNames = Array.Empty<string>();
        Section?[] sectionSym = Array.Empty<Section?>();
        byte[] symType = Array.Empty<byte>();
        if (symtab >= 0)
        {
            SectionHeader h = sh[symtab];
            if (h.EntSize != Elf.Symbol64Size || h.Link >= shnum)
            {
                throw new ElfFormatException("malformed symbol table");
            }
            ReadOnlySpan<byte> strtab = Content(f, sh[h.Link], "symbol string table");
            ReadOnlySpan<byte> table = Content(f, h, "symbol table");
            int count = (int)(h.Size / Elf.Symbol64Size);
            symNames = new string[count];
            sectionSym = new Section?[count];
            symType = new byte[count];
            for (int j = 1; j < count; j++)
            {
                ReadOnlySpan<byte> e = table[(j * Elf.Symbol64Size)..];
                uint nameAt = BinaryPrimitives.ReadUInt32LittleEndian(e);
                byte info = e[4];
                ushort shndx = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
                ulong size = BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
                byte bind = (byte)(info >> 4), type = (byte)(info & 15);
                symType[j] = type;
                symNames[j] = StringTable.Read(strtab, nameAt, $"symbol {j}");
                if (type == Elf.SttSection)
                {
                    if (shndx < shnum) sectionSym[j] = bySh[shndx];
                    continue;
                }
                if (type == Elf.SttFile || symNames[j].Length == 0)
                {
                    continue;
                }
                Section? section = null;
                if (shndx != Elf.ShnUndef)
                {
                    if (shndx >= shnum || bySh[shndx] is null)
                    {
                        throw new ElfFormatException($"symbol '{symNames[j]}' is in a section that cannot be linked");
                    }
                    section = bySh[shndx];
                }
                else if (bind == Elf.StbLocal)
                {
                    throw new ElfFormatException($"local symbol '{symNames[j]}' is undefined");
                }
                obj.Symbols.Add(new Symbol
                {
                    Name = symNames[j], Section = section, Offset = (long)value, Size = (long)size,
                    IsFunction = type == Elf.SttFunc, Global = bind != Elf.StbLocal,
                });
            }
        }

        for (int i = 1; i < shnum; i++)
        {
            SectionHeader h = sh[i];
            if (h.Type != ShtRela)
            {
                continue;
            }
            Section? target = h.Info < shnum ? bySh[h.Info] : null;
            if (target is null)
            {
                continue;
            }
            if (h.EntSize != RelaSize || (int)h.Link != symtab)
            {
                throw new ElfFormatException($"'{names[i]}' is not a relocation table this linker reads");
            }
            ReadOnlySpan<byte> table = Content(f, h, $"'{names[i]}'");
            int count = (int)(h.Size / RelaSize);
            for (int j = 0; j < count; j++)
            {
                ReadOnlySpan<byte> e = table[(j * RelaSize)..];
                ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(e);
                ulong info = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
                long addend = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
                int symIdx = (int)(info >> 32);
                RelocKind kind = KindOf((uint)info)
                    ?? throw new ElfFormatException($"unsupported x86-64 relocation type {(uint)info} in '{target.Name}' at 0x{offset:x}");
                if (symIdx <= 0 || symIdx >= symNames.Length)
                {
                    throw new ElfFormatException($"relocation in '{target.Name}' at 0x{offset:x} names no symbol");
                }
                string symbol = symType[symIdx] == Elf.SttSection
                    ? sectionSym[symIdx]?.Name ?? throw new ElfFormatException($"relocation in '{target.Name}' is against a section that cannot be linked")
                    : symNames[symIdx];
                target.Relocs.Add(new Relocation(checked((int)offset), symbol, addend, kind));
            }
        }
        return obj;
    }

    private static ReadOnlySpan<byte> Content(ReadOnlySpan<byte> f, in SectionHeader h, string what)
        => h.Type == Elf.ShtNoBits ? ReadOnlySpan<byte>.Empty : Slice(f, h.Offset, h.Size, what);

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> f, ulong offset, ulong size, string what)
    {
        if (offset + size > (ulong)f.Length)
        {
            throw new ElfFormatException($"{what}: 0x{offset:x}+0x{size:x} is past the end of the file ({f.Length} bytes)");
        }
        return f.Slice((int)offset, (int)size);
    }
}
