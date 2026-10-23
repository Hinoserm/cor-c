#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;
using Corsac.Lang.Pe;

namespace Corsac.Lang.Elf;

/// <summary>
/// THE WINDOWS IMAGE: the same link -- merge, resolve, addresses, relocate --
/// with a PE32 file around it instead of an ELF one (docs/WIN32-TARGET.md).
/// The objects do not change; only what is written at the end does.
///
///   ImageBase     MS-DOS header and stub, PE headers, section table
///   +0x1000       .text    code, then a `jmp [IAT]` thunk per import
///                 .rdata   constants, then the export directory
///                 .data    numbers, statics, then .bss (memory, no file bytes)
///                 .idata   the import directory, its lookup tables and IAT
///                 .reloc   a HIGHLOW fix-up for every absolute address
///
/// Each section starts on its own page (SectionAlignment) and on a sector in
/// the file (FileAlignment). The code is not position-independent: every
/// absolute address in it and in data is listed in .reloc, so the loader can
/// move the image where another already sits, which for a DLL is the usual
/// case and on Vista and later (ASLR) is every case.
/// </summary>
public static partial class Linker
{
    /// <summary>What kind of Windows image a link makes, and where.</summary>
    public sealed class PeOptions
    {
        /// <summary>A DLL: IMAGE_FILE_DLL, an export directory, and an entry point only if one is named.</summary>
        public bool Dll { get; init; }

        /// <summary>The address it is linked to, a multiple of 64 KiB as Windows requires.</summary>
        public uint ImageBase { get; init; } = PeFormat.DefaultImageBase;

        /// <summary>The symbol the loader enters: a program's start, a DLL's DllMain. Null: a DLL with none.</summary>
        public string? Entry { get; init; }

        public PeSubsystem Subsystem { get; init; } = PeSubsystem.WindowsGui;

        /// <summary>The image's own name, which its export directory carries: the file's.</summary>
        public string Name { get; init; } = "";
    }

    /// <summary>One section of the image: the link's output sections it holds, in address order.</summary>
    private sealed class PeSection
    {
        public PeSection(string name, PeSectionFlags flags, params OutputSection?[] parts)
        {
            Name = name;
            Flags = flags;
            foreach (OutputSection? part in parts)
            {
                if (part is not null) Parts.Add(part);
            }
        }

        public string Name { get; }
        public PeSectionFlags Flags { get; set; }
        public List<OutputSection> Parts { get; } = new();
        public uint Rva { get; set; }
        public uint VirtualSize { get; set; }
        public uint RawPointer { get; set; }
        public uint RawSize { get; set; }
        public bool Empty => Parts.All(part => part.Size == 0);
    }

    /// <summary>One function an image calls in a DLL, and the symbol its objects name it by.</summary>
    private sealed record PeImport(string Dll, string? Export, ushort Ordinal, string Symbol);

    /// <summary>A thunk: `jmp dword [IAT slot]`, six bytes.</summary>
    private const int PeThunkBytes = 6;

    public static void LinkPeTo(string path, IEnumerable<(string Name, ObjectFile Object)> objects, PeOptions options)
        => File.WriteAllBytes(path, LinkPe(objects, options));

    public static byte[] LinkPe(IEnumerable<(string Name, ObjectFile Object)> objects, PeOptions options)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ImageBase % 0x10000 != 0)
        {
            throw new ArgumentException($"image base 0x{options.ImageBase:x} is not a multiple of 64 KiB, which Windows requires", nameof(options));
        }
        List<Input> inputs = objects.Select(o => new Input(o.Name, o.Object)).ToList();
        if (inputs.Count == 0)
        {
            throw new LinkException(new[] { "nothing to link" });
        }

        TargetContract.Validate(inputs.Select(i => (i.Name, i.Object)));
        if (inputs.Any(i => TargetContract.IsLongMode(i.Object)))
        {
            throw new LinkException(new[] { "a Windows image is made for i386 only: x86-64's PE32+ is not made yet" });
        }
        ManagedLayoutContract.Validate(inputs.Select(i => (i.Name, i.Object)));
        Corsac.Lang.Lto.DefinitionCoalescer.Run(inputs.Select(i => (i.Name, i.Object)).ToArray());
        List<string> errors = new();
        if (ProgramInitializers(inputs, longMode: false) is ObjectFile programInit) inputs.Add(new Input("the program's initialisers", programInit));
        if (Corsac.Lang.Lto.Preinit.Lay(inputs.Select(i => i.Object), false) is ObjectFile preinitialised) inputs.Add(new Input("the statics laid down", preinitialised));

        Layout layout = new(options.ImageBase);
        layout.ArrangeStatic();
        AddManagedMetadata(inputs, layout, errors);
        Merge(inputs, layout, errors);
        Resolve(inputs, layout, errors);
        ResolveManagedMetadata(layout, errors);

        // THE IMPORTS: every `dll!export` an object names and none defines. A
        // thunk each, at the end of the code, and the symbol defined there, so
        // the relocation phase reaches it as it reaches any other function.
        List<PeImport> imports = PeImportsOf(inputs, layout, errors);
        OutputSection? thunks = null;
        if (imports.Count > 0)
        {
            thunks = new OutputSection(".text$imports", SectionKind.Code) { Align = 4, Size = (uint)(imports.Count * PeThunkBytes) };
            thunks.Content = new byte[thunks.Size];
            layout.ReadOnly.Insert(layout.ReadOnly.IndexOf(layout.Text) + 1, thunks);
            for (int i = 0; i < imports.Count; i++)
            {
                layout.Globals[imports[i].Symbol] = new Definition("the import directory", thunks, (ulong)(i * PeThunkBytes), null);
                layout.GlobalOrder.Add(imports[i].Symbol);
            }
        }
        List<(Input Input, string Export, string Symbol)> exports = PeExportsOf(inputs, errors);
        OutputSection? exportSection = null;
        if (exports.Count > 0)
        {
            exportSection = new OutputSection(".edata", SectionKind.ReadOnlyData) { Align = 4 };
            exportSection.Size = PeExportBytes(exports, options.Name);
            exportSection.Content = new byte[exportSection.Size];
            layout.ReadOnly.Add(exportSection);
        }
        OutputSection? importSection = null;
        if (imports.Count > 0)
        {
            importSection = new OutputSection(".idata", SectionKind.Data) { Align = 4 };
            importSection.Size = PeImportBytes(imports);
            importSection.Content = new byte[importSection.Size];
            layout.Extra.Add(importSection);
        }
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }

        List<PeSection> sections = new()
        {
            new PeSection(".text", PeSectionFlags.Code | PeSectionFlags.Execute | PeSectionFlags.Read, layout.Text, thunks),
            new PeSection(".rdata", PeSectionFlags.InitializedData | PeSectionFlags.Read, layout.ReadOnlyData, exportSection),
            new PeSection(".data", PeSectionFlags.InitializedData | PeSectionFlags.Read | PeSectionFlags.Write, layout.Numbers, layout.Data, layout.Bss),
            new PeSection(".idata", PeSectionFlags.InitializedData | PeSectionFlags.Read | PeSectionFlags.Write, importSection),
        };
        sections.RemoveAll(section => section.Empty);

        // .reloc is last, and whether it is there at all is known now: an
        // image with no absolute address in it moves without one.
        bool hasFixups = thunks is not null || inputs.Any(input => input.Placed.Any(p => p.Output.IsAllocated && p.Section.Relocs.Any(r => r.Kind == RelocKind.Abs32)));
        int sectionCount = sections.Count + (hasFixups ? 1 : 0);
        uint headerBytes = (uint)(PeFormat.DosBytes + 4 + PeFormat.CoffHeaderBytes + PeFormat.OptionalHeaderBytes + sectionCount * PeFormat.SectionHeaderBytes);
        uint sizeOfHeaders = PeFormat.AlignUp(headerBytes, PeFormat.FileAlignment);
        uint nextRva = 0, nextFile = 0;
        Place(layout, l => (nextRva, nextFile) = AssignPeAddresses(l, sections, sizeOfHeaders));
        BelowFourGigabytes(layout);
        DefineLinkerSymbols(layout);
        Relocate(inputs, layout, errors);

        uint entryRva = 0;
        if (options.Entry is not null)
        {
            if (!layout.Globals.TryGetValue(options.Entry, out Definition entry))
            {
                errors.Add($"entry symbol '{options.Entry}' is not defined by any object");
            }
            else
            {
                entryRva = checked((uint)(entry.Address - options.ImageBase));
            }
        }
        else if (!options.Dll)
        {
            errors.Add("a Windows program needs an entry point");
        }
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }

        // THE TABLES, now that everything has an address.
        if (thunks is not null && importSection is not null)
        {
            FillImports(imports, importSection, thunks, options.ImageBase);
        }
        if (exportSection is not null)
        {
            FillExports(exports, exportSection, layout, options.ImageBase, options.Name, errors);
            if (errors.Count != 0)
            {
                throw new LinkException(errors);
            }
        }

        // THE FIX-UPS: every word an Abs32 relocation wrote in a loaded
        // section, and every thunk's IAT address.
        List<uint> fixups = new();
        foreach (Input input in inputs)
        {
            foreach (Placed p in input.Placed)
            {
                if (!p.Output.IsAllocated)
                {
                    continue;
                }
                foreach (Relocation r in p.Section.Relocs)
                {
                    if (r.Kind == RelocKind.Abs32)
                    {
                        fixups.Add(checked((uint)(p.Output.Addr - options.ImageBase + p.Offset + (uint)r.Offset)));
                    }
                }
            }
        }
        if (thunks is not null)
        {
            for (int i = 0; i < imports.Count; i++)
            {
                fixups.Add(checked((uint)(thunks.Addr - options.ImageBase + (ulong)(i * PeThunkBytes) + 2)));
            }
        }
        PeSection? relocSection = null;
        if (hasFixups)
        {
            byte[] blocks = BaseRelocations(fixups);
            OutputSection reloc = new(".reloc", SectionKind.ReadOnlyData) { Align = 4, Size = (uint)blocks.Length, Content = blocks };
            relocSection = new PeSection(".reloc", PeSectionFlags.InitializedData | PeSectionFlags.Read | PeSectionFlags.Discardable, reloc)
            {
                Rva = nextRva,
                RawPointer = nextFile,
                VirtualSize = reloc.Size,
                RawSize = PeFormat.AlignUp(reloc.Size, PeFormat.FileAlignment),
            };
            reloc.Addr = options.ImageBase + nextRva;
            reloc.FileOffset = nextFile;
            sections.Add(relocSection);
            nextRva = PeFormat.AlignUp(nextRva + reloc.Size, PeFormat.SectionAlignment);
            nextFile += relocSection.RawSize;
        }

        byte[] image = new byte[nextFile];
        WritePeHeaders(image, sections, options, sizeOfHeaders, nextRva, entryRva, exportSection, importSection, thunks, imports, relocSection);
        foreach (PeSection section in sections)
        {
            foreach (OutputSection s in section.Parts)
            {
                if (s.Size == 0 || !s.HasBytes)
                {
                    continue;
                }
                if (s.Content is not null)
                {
                    Array.Copy(s.Content, 0, image, s.FileOffset, s.Content.Length);
                    continue;
                }
                foreach (Placed part in s.Parts)
                {
                    part.Bytes.CopyTo(image, checked((int)(s.FileOffset + part.Offset)));
                    part.Bytes.Clear();
                }
            }
        }
        uint checksum = PeChecksum(image, PeFormat.DosBytes + 4 + PeFormat.CoffHeaderBytes + 64);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(PeFormat.DosBytes + 4 + PeFormat.CoffHeaderBytes + 64), checksum);
        return image;
    }

    /// <summary>
    /// Every import the inputs name: an undefined global that a relocation
    /// uses and that is spelt `dll!export`, sorted by DLL and then by name so
    /// that the same objects make the same image.
    /// </summary>
    private static List<PeImport> PeImportsOf(List<Input> inputs, Layout layout, List<string> errors)
    {
        SortedDictionary<string, PeImport> found = new(StringComparer.Ordinal);
        foreach (Input input in inputs)
        {
            foreach (Section s in input.Object.Sections)
            {
                foreach (Relocation r in s.Relocs)
                {
                    if (found.ContainsKey(r.Symbol) || input.Locals.ContainsKey(r.Symbol)
                        || !WindowsImports.TryParse(r.Symbol, out string dll, out string? export, out ushort ordinal))
                    {
                        continue;
                    }
                    if (layout.Globals.ContainsKey(r.Symbol))
                    {
                        errors.Add($"'{r.Symbol}' names a function of {dll}, and {layout.Globals[r.Symbol].Object} defines it too");
                        continue;
                    }
                    found[r.Symbol] = new PeImport(dll, export, ordinal, r.Symbol);
                }
            }
        }
        return found.Values.OrderBy(i => i.Dll, StringComparer.Ordinal)
            .ThenBy(i => i.Export is null ? 1 : 0).ThenBy(i => i.Export ?? "", StringComparer.Ordinal).ThenBy(i => i.Ordinal).ToList();
    }

    /// <summary>
    /// Every export the inputs ask for (NativeExports), in the order the
    /// loader searches them: by name, byte by byte. A name asked for twice is
    /// refused, as is one that is not plain ASCII, which is what an export
    /// directory's names are.
    /// </summary>
    private static List<(Input Input, string Export, string Symbol)> PeExportsOf(List<Input> inputs, List<string> errors)
    {
        Dictionary<string, string> owners = new(StringComparer.Ordinal);
        List<(Input Input, string Export, string Symbol)> exports = new();
        foreach (Input input in inputs)
        {
            foreach ((string export, string symbol) in NativeExports.Read(input.Object))
            {
                if (export.Length == 0 || export.Any(c => c <= ' ' || c > '~'))
                {
                    errors.Add($"export '{export}' from {input.Name}: an export's name is printable ASCII with no spaces");
                    continue;
                }
                if (!owners.TryAdd(export, input.Name))
                {
                    errors.Add($"'{export}' is exported by both {owners[export]} and {input.Name}");
                    continue;
                }
                exports.Add((input, export, symbol));
            }
        }
        exports.Sort((a, b) => string.CompareOrdinal(a.Export, b.Export));
        return exports;
    }

    /// <summary>The export directory's size: its header, the three arrays, then the names.</summary>
    private static uint PeExportBytes(List<(Input Input, string Export, string Symbol)> exports, string name)
    {
        uint size = PeFormat.ExportDirectoryBytes + (uint)exports.Count * (4 + 4 + 2);
        size += (uint)Encoding.ASCII.GetByteCount(name) + 1;
        foreach (var export in exports)
        {
            size += (uint)export.Export.Length + 1;
        }
        return size;
    }

    /// <summary>
    /// The import directory's size. In order: the descriptors and the null
    /// one ending them; each DLL's lookup table; each DLL's address table
    /// (all of them together, which is the IAT the data directory names);
    /// the hint-and-name entries, each an even number of bytes; the DLLs' names.
    /// </summary>
    private static uint PeImportBytes(List<PeImport> imports)
    {
        List<string> dlls = imports.Select(i => i.Dll).Distinct(StringComparer.Ordinal).ToList();
        uint size = (uint)(dlls.Count + 1) * PeFormat.ImportDescriptorBytes;
        size += 2 * (uint)(imports.Count + dlls.Count) * 4;
        foreach (PeImport import in imports)
        {
            if (import.Export is not null) size += HintNameBytes(import.Export);
        }
        foreach (string dll in dlls)
        {
            size += (uint)dll.Length + 1;
        }
        return size;
    }

    private static uint HintNameBytes(string export) => PeFormat.AlignUp(2 + (uint)export.Length + 1, 2);

    /// <summary>The import directory's bytes, and each thunk's jump through its IAT slot.</summary>
    private static void FillImports(List<PeImport> imports, OutputSection idata, OutputSection thunks, uint imageBase)
    {
        byte[] b = idata.Content!;
        uint rva = checked((uint)(idata.Addr - imageBase));
        List<string> dlls = imports.Select(i => i.Dll).Distinct(StringComparer.Ordinal).ToList();
        uint lookups = (uint)(dlls.Count + 1) * PeFormat.ImportDescriptorBytes;
        uint tables = 4 * (uint)(imports.Count + dlls.Count);
        uint addresses = lookups + tables;
        uint names = addresses + tables;
        uint dllNames = names;
        foreach (PeImport import in imports)
        {
            if (import.Export is not null) dllNames += HintNameBytes(import.Export);
        }

        uint slot = 0, name = names, dllName = dllNames;
        Dictionary<string, uint> slotOf = new(StringComparer.Ordinal);
        for (int d = 0; d < dlls.Count; d++)
        {
            uint descriptor = (uint)d * PeFormat.ImportDescriptorBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)descriptor), rva + lookups + slot * 4);        // OriginalFirstThunk
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)descriptor + 12), rva + dllName);               // Name
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)descriptor + 16), rva + addresses + slot * 4);  // FirstThunk
            Encoding.ASCII.GetBytes(dlls[d], b.AsSpan((int)dllName));
            dllName += (uint)dlls[d].Length + 1;
            foreach (PeImport import in imports.Where(i => i.Dll == dlls[d]))
            {
                uint entry;
                if (import.Export is null)
                {
                    entry = PeFormat.ImportByOrdinal | import.Ordinal;
                }
                else
                {
                    entry = rva + name;
                    // The hint is the export's index in the DLL's name table,
                    // which only the DLL knows; zero sends the loader straight
                    // to its search.
                    Encoding.ASCII.GetBytes(import.Export, b.AsSpan((int)name + 2));
                    name += HintNameBytes(import.Export);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)(lookups + slot * 4)), entry);
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)(addresses + slot * 4)), entry);
                slotOf[import.Symbol] = slot;
                slot++;
            }
            slot++; // the null entry ending this DLL's tables
        }

        byte[] t = thunks.Content!;
        for (int i = 0; i < imports.Count; i++)
        {
            int at = i * PeThunkBytes;
            t[at] = 0xFF;      // jmp dword [disp32]
            t[at + 1] = 0x25;
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(at + 2), checked((uint)(idata.Addr + addresses + slotOf[imports[i].Symbol] * 4)));
        }
    }

    /// <summary>The export directory's bytes: ordinals from 1, in the order of the names.</summary>
    private static void FillExports(List<(Input Input, string Export, string Symbol)> exports, OutputSection edata, Layout layout, uint imageBase, string name, List<string> errors)
    {
        byte[] b = edata.Content!;
        uint rva = checked((uint)(edata.Addr - imageBase));
        uint n = (uint)exports.Count;
        uint functions = PeFormat.ExportDirectoryBytes;
        uint namePointers = functions + 4 * n;
        uint ordinals = namePointers + 4 * n;
        uint strings = ordinals + 2 * n;

        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), rva + strings);   // Name
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 1);               // Base
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), n);               // NumberOfFunctions
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), n);               // NumberOfNames
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), rva + functions); // AddressOfFunctions
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), rva + namePointers);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), rva + ordinals);
        Encoding.ASCII.GetBytes(name, b.AsSpan((int)strings));
        uint at = strings + (uint)Encoding.ASCII.GetByteCount(name) + 1;

        for (int i = 0; i < exports.Count; i++)
        {
            (Input input, string export, string symbol) = exports[i];
            if (Lookup(input, layout, symbol) is not { Section: { Kind: SectionKind.Code } } d)
            {
                errors.Add($"export '{export}' names '{symbol}', which is not a function {input.Name} defines");
                continue;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)functions + 4 * i), checked((uint)(d.Address - imageBase)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)namePointers + 4 * i), rva + at);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)ordinals + 2 * i), (ushort)i);
            Encoding.ASCII.GetBytes(export, b.AsSpan((int)at));
            at += (uint)export.Length + 1;
        }
    }

    /// <summary>
    /// .reloc's bytes: the fix-ups by page, a block a page, each entry the
    /// HIGHLOW type and the offset in the page, a block padded to four bytes
    /// with an ABSOLUTE entry the loader skips.
    /// </summary>
    private static byte[] BaseRelocations(List<uint> fixups)
    {
        List<byte> bytes = new();
        foreach (IGrouping<uint, uint> page in fixups.Distinct().Order().GroupBy(rva => rva & ~(PeFormat.SectionAlignment - 1)))
        {
            List<uint> entries = page.ToList();
            int count = entries.Count + (entries.Count & 1);
            uint blockSize = PeFormat.BaseRelocationBlockBytes + (uint)count * 2;
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, page.Key);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blockSize);
            bytes.AddRange(header.ToArray());
            foreach (uint rva in entries)
            {
                ushort entry = (ushort)((ushort)PeBaseRelocation.HighLow << 12 | (rva & (PeFormat.SectionAlignment - 1)));
                bytes.Add((byte)entry);
                bytes.Add((byte)(entry >> 8));
            }
            if ((entries.Count & 1) != 0)
            {
                bytes.Add(0);
                bytes.Add(0);
            }
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// Addresses for a Windows image: each section on its own page from the
    /// first one after the headers, its parts packed inside it as aligned as
    /// they ask. Answers where the next section would go, in memory and in
    /// the file, which is where .reloc goes once its size is known.
    /// </summary>
    private static (uint Rva, uint File) AssignPeAddresses(Layout layout, List<PeSection> sections, uint sizeOfHeaders)
    {
        layout.HeaderBytes = sizeOfHeaders;
        uint rva = PeFormat.AlignUp(sizeOfHeaders, PeFormat.SectionAlignment);
        uint file = sizeOfHeaders;
        foreach (PeSection section in sections)
        {
            section.Rva = rva;
            section.RawPointer = file;
            uint at = 0, bytesEnd = 0;
            foreach (OutputSection part in section.Parts)
            {
                if (part.Size == 0)
                {
                    continue;
                }
                at = PeFormat.AlignUp(at, part.Align);
                part.Addr = checked(layout.LoadAddress + rva + at);
                part.FileOffset = file + at;
                at = checked(at + part.Size);
                if (part.HasBytes)
                {
                    bytesEnd = at;
                }
            }
            section.VirtualSize = at;
            section.RawSize = PeFormat.AlignUp(bytesEnd, PeFormat.FileAlignment);
            rva = PeFormat.AlignUp(checked(rva + at), PeFormat.SectionAlignment);
            file = checked(file + section.RawSize);
        }
        // Notes are not loaded, and a Windows image has no place for them.
        foreach (OutputSection n in layout.Notes)
        {
            n.Addr = 0;
            n.FileOffset = 0;
        }
        layout.FileEnd = file;
        return (rva, file);
    }

    /// <summary>
    /// The headers: MS-DOS's (with a stub that says the program is not for
    /// MS-DOS), the PE signature, the COFF header, the PE32 optional header
    /// with its data directories, and the section table.
    /// </summary>
    private static void WritePeHeaders(byte[] b, List<PeSection> sections, PeOptions options, uint sizeOfHeaders, uint sizeOfImage, uint entryRva,
                                       OutputSection? exports, OutputSection? imports, OutputSection? thunks, List<PeImport> importList, PeSection? reloc)
    {
        // THE MS-DOS HEADER: what makes the file MZ, and e_lfanew. The stub
        // after it prints the sentence and exits with 1.
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 0x90);    // bytes on the last page
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 3);       // pages
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), 4);       // header paragraphs
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 0xFFFF); // most memory
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 0xB8);   // SP
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24), 0x40);   // relocation table
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(PeFormat.NewHeaderPointer), PeFormat.DosBytes);
        byte[] stub =
        {
            0x0E,             // push cs
            0x1F,             // pop ds
            0xBA, 0x0E, 0x00, // mov dx, message
            0xB4, 0x09,       // mov ah, 9
            0xCD, 0x21,       // int 21h
            0xB8, 0x01, 0x4C, // mov ax, 4C01h
            0xCD, 0x21,       // int 21h
        };
        stub.CopyTo(b, 0x40);
        Encoding.ASCII.GetBytes("This program cannot be run in DOS mode.\r\r\n$", b.AsSpan(0x40 + stub.Length));

        int at = PeFormat.DosBytes;
        b[at] = (byte)'P'; b[at + 1] = (byte)'E';
        at += 4;

        // THE COFF HEADER.
        PeCharacteristics characteristics = PeCharacteristics.ExecutableImage | PeCharacteristics.Machine32Bit
            | PeCharacteristics.LineNumsStripped | PeCharacteristics.LocalSymsStripped | PeCharacteristics.DebugStripped;
        if (options.Dll) characteristics |= PeCharacteristics.Dll;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)PeMachine.I386);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 2), (ushort)sections.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 16), PeFormat.OptionalHeaderBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 18), (ushort)characteristics);
        at += PeFormat.CoffHeaderBytes;

        // THE OPTIONAL HEADER. Windows 95's and NT 4's loaders refuse a
        // subsystem or system version above 4.0, so both are 4.0.
        int o = at;
        uint code = 0, initialised = 0;
        foreach (PeSection s in sections)
        {
            if ((s.Flags & PeSectionFlags.Code) != 0) code += s.RawSize;
            else initialised += s.RawSize;
        }
        PeSection? text = sections.FirstOrDefault(s => (s.Flags & PeSectionFlags.Code) != 0);
        PeSection? data = sections.FirstOrDefault(s => (s.Flags & PeSectionFlags.Code) == 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), PeFormat.Pe32Magic);
        b[o + 2] = 1;                                                                   // linker version 1.0
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), code);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 8), initialised);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 16), entryRva);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 20), text?.Rva ?? 0);    // BaseOfCode
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 24), data?.Rva ?? 0);    // BaseOfData
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 28), options.ImageBase);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 32), PeFormat.SectionAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 36), PeFormat.FileAlignment);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 40), 4);                 // operating system 4.0
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 48), 4);                 // subsystem 4.0
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 56), sizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 60), sizeOfHeaders);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 68), (ushort)options.Subsystem);
        PeDllCharacteristics dll = PeDllCharacteristics.NxCompatible | (reloc is not null ? PeDllCharacteristics.DynamicBase : 0);
        if (!options.Dll) dll |= PeDllCharacteristics.TerminalServerAware;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 70), (ushort)dll);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 72), 0x100000);          // stack reserved
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 76), 0x1000);            // stack committed
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 80), 0x100000);          // heap reserved
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 84), 0x1000);            // heap committed
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 92), PeFormat.DataDirectoryCount);
        void Directory(PeDirectory which, ulong address, uint size)
        {
            int d = o + 96 + 8 * (int)which;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(d), checked((uint)(address - options.ImageBase)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(d + 4), size);
        }
        if (exports is not null) Directory(PeDirectory.Export, exports.Addr, exports.Size);
        if (imports is not null)
        {
            int dllCount = importList.Select(i => i.Dll).Distinct(StringComparer.Ordinal).Count();
            uint tables = 4 * (uint)(importList.Count + dllCount);
            Directory(PeDirectory.Import, imports.Addr, (uint)(dllCount + 1) * PeFormat.ImportDescriptorBytes);
            Directory(PeDirectory.ImportAddressTable, imports.Addr + (uint)(dllCount + 1) * PeFormat.ImportDescriptorBytes + tables, tables);
        }
        if (reloc is not null) Directory(PeDirectory.BaseRelocation, options.ImageBase + reloc.Rva, reloc.VirtualSize);
        at += PeFormat.OptionalHeaderBytes;

        // THE SECTION TABLE.
        foreach (PeSection s in sections)
        {
            Encoding.ASCII.GetBytes(s.Name, b.AsSpan(at, Math.Min(8, s.Name.Length)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), s.VirtualSize);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 12), s.Rva);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 16), s.RawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 20), s.RawSize == 0 ? 0 : s.RawPointer);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 36), (uint)s.Flags);
            at += PeFormat.SectionHeaderBytes;
        }
    }

    /// <summary>
    /// The optional header's CheckSum, as IMAGEHLP's CheckSumMappedFile makes
    /// it: the file's 16-bit words added with their carries folded back in,
    /// the field itself counted as zero, and the file's length added last.
    /// </summary>
    private static uint PeChecksum(byte[] image, int checksumAt)
    {
        uint sum = 0;
        for (int i = 0; i < image.Length; i += 2)
        {
            if (i == checksumAt || i == checksumAt + 2)
            {
                continue;
            }
            uint word = i + 1 < image.Length ? (uint)(image[i] | image[i + 1] << 8) : image[i];
            sum += word;
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        sum = (sum & 0xFFFF) + (sum >> 16);
        return sum + (uint)image.Length;
    }
}
