#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// Turns an in-memory <see cref="ObjectFile"/> into a relocatable ELF32
/// i386 object that GNU binutils accept and <see cref="ElfReader"/> reads
/// back to the same thing.
///
/// Two conventions worth knowing. A section's name is an implicit local
/// symbol at its start, so a relocation may name a section directly; that
/// is how the reader expresses an assembler's "against .rodata+0x10". And
/// i386 uses REL, not RELA: the addend lives in the relocated word itself,
/// so the writer STORES each relocation's addend into its word, replacing
/// whatever the backend left there.
/// </summary>
public static class ElfWriter
{
    public static byte[] WriteObject(ObjectFile obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        // Long-mode code is an ELF64 object with RELA relocations.
        if (TargetContract.IsLongMode(obj))
        {
            return Elf64Object.Write(obj);
        }
        return Build(obj).ToArray();
    }

    /// <summary>
    /// A check of the object WriteObject would make (FastHash), hashed from
    /// the chunks it is built in rather than from one array of the whole image.
    /// </summary>
    public static byte[] HashObject(ObjectFile obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (TargetContract.IsLongMode(obj)) return FastHash.Of(Elf64Object.Write(obj));
        return Build(obj).Hash();
    }

    /// <summary>
    /// The object written to its file, streamed from the chunks it was built
    /// in: never the whole image as one array, nor a copy of any section --
    /// a large unit's object and its IR are tens of megabytes each, and a
    /// 32-bit heap cut into chunks has no such contiguous run to spare.
    /// </summary>
    public static void WriteObjectFile(ObjectFile obj, string path)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (TargetContract.IsLongMode(obj))
        {
            File.WriteAllBytes(path, Elf64Object.Write(obj));
            return;
        }
        ElfBuffer built = Build(obj, Path.GetFullPath(path));
        using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        built.WriteTo(output);
    }

    /// <summary>`writingTo`: the file the object is about to be written over, whose own sections are copied in, not spliced.</summary>
    private static ElfBuffer Build(ObjectFile obj, string? writingTo = null)
    {

        // Section header indices: the object's sections in the order given,
        // from 1, so a section symbol's index is its section's index.
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
        // The symbol table and the relocation lists below are a unit's
        // longest after its code: chunks, not doubling arrays.
        ChunkedList<SymbolEntry> symbols = new() { default };
        Dictionary<string, int> symbolIndex = new();

        for (int i = 0; i < obj.Sections.Count; i++)
        {
            symbols.Add(new SymbolEntry(0, 0, 0, SymbolEntry.MakeInfo(Elf.StbLocal, Elf.SttSection), 0, (ushort)(i + 1)));
        }

        // Locals precede globals: that is what lets sh_info say where the
        // globals start, and what the reader relies on to give them back in
        // the same order.
        foreach (bool global in new[] { false, true })
        {
            foreach (Symbol sym in obj.Symbols)
            {
                if (sym.Global != global)
                {
                    continue;
                }
                AddSymbol(obj, sym, symbols, symbolIndex, strtab);
            }
        }
        int firstGlobal = 1 + obj.Sections.Count;
        foreach (Symbol sym in obj.Symbols)
        {
            if (!sym.Global)
            {
                firstGlobal++;
            }
        }

        // A relocation may name something that is neither a symbol nor a
        // section of this object: that is a reference the linker must
        // satisfy, so it becomes an undefined global.
        // The addends are written into the image as the section goes in (REL
        // keeps them in the section's bytes), never into the object's own
        // bytes and never into a copy of them.
        ChunkedList<(int Offset, uint Addend)>[] addends = new ChunkedList<(int, uint)>[obj.Sections.Count];
        ChunkedList<(uint Offset, uint Info)>[] rels = new ChunkedList<(uint, uint)>[obj.Sections.Count];
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            Section s = obj.Sections[i];
            int length = s.Size;
            addends[i] = new ChunkedList<(int, uint)>();
            rels[i] = new ChunkedList<(uint, uint)>();
            foreach (Relocation r in s.Relocs)
            {
                if (s.Kind == SectionKind.Uninitialised)
                {
                    throw new ElfFormatException($"section '{s.Name}' is uninitialised but has a relocation at 0x{r.Offset:x}");
                }
                if (r.Offset < 0 || r.Offset + 4 > length)
                {
                    throw new ElfFormatException($"relocation at 0x{r.Offset:x} is outside section '{s.Name}' ({length} bytes)");
                }
                if (r.Addend < int.MinValue || r.Addend > uint.MaxValue)
                {
                    throw new ElfFormatException($"relocation addend {r.Addend} against '{r.Symbol}' in '{s.Name}' does not fit a 32-bit word");
                }
                int sym;
                if (r.Kind == RelocKind.Relative)
                {
                    // R_386_RELATIVE has no symbol; anything named is a mistake.
                    if (r.Symbol.Length != 0)
                    {
                        throw new ElfFormatException($"relative relocation in '{s.Name}' at 0x{r.Offset:x} names a symbol ('{r.Symbol}')");
                    }
                    sym = 0;
                }
                else if (!symbolIndex.TryGetValue(r.Symbol, out sym) && !sectionIndex.TryGetValue(r.Symbol, out sym))
                {
                    if (r.Symbol.Length == 0)
                    {
                        throw new ElfFormatException($"relocation in '{s.Name}' at 0x{r.Offset:x} names no symbol");
                    }
                    sym = symbols.Count;
                    symbols.Add(new SymbolEntry(strtab.Add(r.Symbol), 0, 0, SymbolEntry.MakeInfo(Elf.StbGlobal, Elf.SttNoType), 0, Elf.ShnUndef));
                    symbolIndex[r.Symbol] = sym;
                }
                addends[i].Add((checked((int)r.Offset), unchecked((uint)r.Addend)));
                rels[i].Add(((uint)r.Offset, (uint)(sym << 8) | Elf.RelocType(r.Kind)));
            }
        }

        // Header names first: the section header table needs them all, and
        // .shstrtab itself is written before the table is.
        StringTable shstrtab = new();
        uint[] sectionName = new uint[obj.Sections.Count];
        uint[] relName = new uint[obj.Sections.Count];
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            sectionName[i] = shstrtab.Add(obj.Sections[i].Name);
            if (rels[i].Count != 0)
            {
                relName[i] = shstrtab.Add(".rel" + obj.Sections[i].Name);
            }
        }
        uint gnuStackName = shstrtab.Add(Elf.GnuStackNote);
        uint symtabName = shstrtab.Add(".symtab");
        uint strtabName = shstrtab.Add(".strtab");
        uint shstrtabName = shstrtab.Add(".shstrtab");

        // File order: header, section contents, relocation tables, the
        // symbol table and both string tables, then the section header
        // table. The header's e_shoff is patched once that offset is known.
        ElfBuffer b = new();
        List<SectionHeader> headers = new() { default };
        Elf.WriteHeader(b, Elf.TypeRel, 0, 0, 0, 0, 0, 0);

        for (int i = 0; i < obj.Sections.Count; i++)
        {
            Section s = obj.Sections[i];
            (uint type, uint flags) = Elf.SectionTypeAndFlags(s.Kind);
            uint align = (uint)Math.Max(1, s.Align);
            // A power of two no bigger than a page, or the layout is wrong: a
            // stray alignment pads the object toward 4 GiB and dies far from
            // the cause with an OutOfMemoryException.
            if ((align & (align - 1)) != 0 || align > 0x1000)
                throw new InvalidOperationException($"section {s.Name}: alignment 0x{align:x} is not a power of two up to 0x1000");
            uint at = b.AlignTo(align);
            if (s.Kind != SectionKind.Uninitialised)
            {
                if (s.FileBacked is not null)
                {
                    // Spliced, read from its file only as the object is written
                    // or hashed: a unit's IR archive is tens of megabytes, and
                    // Content made it one array, then copying it in made it
                    // as many chunks.
                    // Not a section of the very file about to be written over,
                    // which is made empty before it would be read.
                    if (writingTo is null || s.FileBacked is not (string backing, _, _) || Path.GetFullPath(backing) != writingTo)
                    {
                        b.Splice(s);
                    }
                    else
                    {
                        using Stream from = s.OpenRead();
                        byte[] piece = new byte[64 * 1024];
                        for (int got; (got = from.Read(piece, 0, piece.Length)) > 0;) b.Bytes(new ReadOnlySpan<byte>(piece, 0, got));
                    }
                }
                else
                {
                    // Referred to, not copied: the object is written or
                    // hashed straight after it is built, and its addends
                    // are laid over these bytes, not written into them.
                    for (int k = 0; k < s.Bytes.SegmentCount; k++)
                    {
                        (byte[] array, int length) = s.Bytes.Segment(k);
                        b.Share(array, length);
                    }
                }
                foreach ((int offset, uint addend) in addends[i])
                {
                    b.PatchU32(checked((int)at + offset), addend);
                }
            }
            headers.Add(new SectionHeader(sectionName[i], type, flags, 0, at, (uint)s.Size, 0, 0, align, 0));
        }

        int symtabIndex = headers.Count + rels.Count(r => r.Count != 0) + 1;
        for (int i = 0; i < obj.Sections.Count; i++)
        {
            if (rels[i].Count == 0)
            {
                continue;
            }
            uint at = b.AlignTo(4);
            foreach ((uint offset, uint info) in rels[i])
            {
                b.U32(offset);
                b.U32(info);
            }
            headers.Add(new SectionHeader(relName[i], Elf.ShtRel, Elf.ShfInfoLink, 0, at, (uint)(rels[i].Count * Elf.RelSize), (uint)symtabIndex, (uint)(i + 1), 4, Elf.RelSize));
        }

        headers.Add(new SectionHeader(gnuStackName, Elf.ShtProgBits, 0, 0, (uint)b.Length, 0, 0, 0, 1, 0));

        {
            uint at = b.AlignTo(4);
            foreach (SymbolEntry e in symbols)
            {
                e.WriteTo(b);
            }
            headers.Add(new SectionHeader(symtabName, Elf.ShtSymTab, 0, 0, at, (uint)(symbols.Count * Elf.SymbolSize), (uint)(headers.Count + 1), (uint)firstGlobal, 4, Elf.SymbolSize));
        }
        {
            headers.Add(new SectionHeader(strtabName, Elf.ShtStrTab, 0, 0, (uint)b.Length, (uint)strtab.Length, 0, 0, 1, 0));
            strtab.WriteTo(b);
        }
        {
            byte[] bytes = shstrtab.ToArray();
            headers.Add(new SectionHeader(shstrtabName, Elf.ShtStrTab, 0, 0, (uint)b.Length, (uint)bytes.Length, 0, 0, 1, 0));
            b.Bytes(bytes);
        }

        uint shoff = b.AlignTo(4);
        foreach (SectionHeader h in headers)
        {
            h.WriteTo(b);
        }

        b.PatchU32(32, shoff);
        b.PatchU16(48, (ushort)headers.Count);
        b.PatchU16(50, (ushort)(headers.Count - 1));
        return b;
    }

    private static void AddSymbol(ObjectFile obj, Symbol sym, ChunkedList<SymbolEntry> symbols, Dictionary<string, int> symbolIndex, StringTable strtab)
    {
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
            if (sym.Offset < 0 || sym.Offset > sym.Section.Size)
            {
                throw new ElfFormatException($"symbol '{sym.Name}' at 0x{sym.Offset:x} is outside section '{sym.Section.Name}' ({sym.Section.Size} bytes)");
            }
            shndx = (ushort)(at + 1);
        }
        if (sym.Size < 0 || sym.Size > uint.MaxValue)
        {
            throw new ElfFormatException($"symbol '{sym.Name}' has size {sym.Size}");
        }
        if (!symbolIndex.TryAdd(sym.Name, symbols.Count))
        {
            throw new ElfFormatException($"duplicate symbol '{sym.Name}'");
        }
        byte bind = sym.Global ? Elf.StbGlobal : Elf.StbLocal;
        symbols.Add(new SymbolEntry(strtab.Add(sym.Name), (uint)sym.Offset, (uint)sym.Size, SymbolEntry.MakeInfo(bind, Elf.SymbolType(sym)), 0, shndx));
    }
}
