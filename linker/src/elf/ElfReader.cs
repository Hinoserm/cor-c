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
    /// The name a shared object calls itself: its DT_SONAME, or null if it
    /// has none or is not a shared object at all. Read through the section
    /// headers, which both this linker and GNU ld emit; a stripped-to-the-
    /// segments library answers null and the caller falls back to the file
    /// name, which is what the loader would have looked for anyway.
    /// </summary>
    public static string? SoNameOf(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ReadOnlySpan<byte> f = bytes;
        if (f.Length < Elf.HeaderSize || !f[..4].SequenceEqual(Elf.Magic) || f[4] != Elf.Class32 || f[5] != Elf.Data2Lsb)
        {
            throw new ElfFormatException("not a 32-bit little-endian ELF file");
        }
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(f[32..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(f[48..]);
        if (shoff == 0 || shnum == 0)
        {
            return null;
        }
        SectionHeader? dynamic = null;
        SectionHeader? strtab = null;
        for (int i = 0; i < shnum; i++)
        {
            int at = (int)shoff + i * Elf.SectionHeaderSize;
            if (at + Elf.SectionHeaderSize > f.Length)
            {
                throw new ElfFormatException("section headers run past the end of the file");
            }
            SectionHeader h = SectionHeader.Read(f[at..]);
            if (h.Type == Elf.ShtDynamic)
            {
                dynamic = h;
            }
        }
        if (dynamic is null)
        {
            return null;
        }
        // .dynamic's sh_link names the string table its tags index into.
        int linkAt = (int)shoff + (int)dynamic.Value.Link * Elf.SectionHeaderSize;
        if (dynamic.Value.Link == 0 || linkAt + Elf.SectionHeaderSize > f.Length)
        {
            return null;
        }
        strtab = SectionHeader.Read(f[linkAt..]);
        ReadOnlySpan<byte> table = f.Slice((int)strtab.Value.Offset, (int)strtab.Value.Size);
        for (uint at = dynamic.Value.Offset; at + 8 <= dynamic.Value.Offset + dynamic.Value.Size; at += 8)
        {
            int tag = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[(int)at..]);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(f[((int)at + 4)..]);
            if (tag == Elf.DtNull)
            {
                break;
            }
            if (tag == Elf.DtSoName)
            {
                return StringTable.Read(table, value, "DT_SONAME");
            }
        }
        return null;
    }

    /// <summary>
    /// Every name a shared object DEFINES, out of its .dynsym: what a
    /// consumer must not compile a second copy of, and what it may leave
    /// for the loader to supply.
    ///
    /// Read from the section headers rather than by walking PT_DYNAMIC,
    /// because everything this reads is a file the compiler itself wrote a
    /// moment earlier and section headers are the simpler road to the same
    /// table.
    /// </summary>
    public static List<string> ExportsOf(byte[] bytes) => DynamicNames(bytes, defined: true);

    /// <summary>Names the dynamic loader must resolve from other images.</summary>
    public static List<string> ImportsOf(byte[] bytes) => DynamicNames(bytes, defined: false);

    private static List<string> DynamicNames(byte[] bytes, bool defined)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ReadOnlySpan<byte> f = bytes;
        if (f.Length < Elf.HeaderSize || !f[..4].SequenceEqual(Elf.Magic) || f[4] != Elf.Class32 || f[5] != Elf.Data2Lsb)
        {
            throw new ElfFormatException("not a 32-bit little-endian ELF file");
        }
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(f[32..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(f[48..]);
        List<string> names = new();
        if (shoff == 0 || shnum == 0)
        {
            return names;
        }
        SectionHeader? dynsym = null;
        for (int i = 0; i < shnum; i++)
        {
            int at = (int)shoff + i * Elf.SectionHeaderSize;
            if (at + Elf.SectionHeaderSize > f.Length)
            {
                throw new ElfFormatException("section headers run past the end of the file");
            }
            SectionHeader h = SectionHeader.Read(f[at..]);
            if (h.Type == Elf.ShtDynSym)
            {
                dynsym = h;
            }
        }
        if (dynsym is null)
        {
            return names;
        }
        int strAt = (int)shoff + (int)dynsym.Value.Link * Elf.SectionHeaderSize;
        if (dynsym.Value.Link == 0 || strAt + Elf.SectionHeaderSize > f.Length)
        {
            return names;
        }
        SectionHeader str = SectionHeader.Read(f[strAt..]);
        ReadOnlySpan<byte> table = f.Slice((int)str.Offset, (int)str.Size);
        for (uint at = dynsym.Value.Offset; at + Elf.SymbolSize <= dynsym.Value.Offset + dynsym.Value.Size; at += (uint)Elf.SymbolSize)
        {
            SymbolEntry e = SymbolEntry.Read(f[(int)at..]);
            if ((e.Shndx != Elf.ShnUndef) != defined || e.Name == 0)
            {
                continue;
            }
            names.Add(StringTable.Read(table, e.Name, ".dynsym"));
        }
        return names;
    }

    public static ObjectFile ReadObject(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ReadOnlySpan<byte> f = bytes;

        if (f.Length < Elf.HeaderSize || !f[..4].SequenceEqual(Elf.Magic))
        {
            throw new ElfFormatException("not an ELF file");
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
                s.Bytes.AddRange(Content(f, h, $"section '{names[i]}'"));
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
                    Offset = e.Value,
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
                // REL: the addend is whatever sits in the word.
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
