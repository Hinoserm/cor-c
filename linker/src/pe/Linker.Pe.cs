#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;
using Corsac.Lang.Pe;

namespace Corsac.Lang.Elf;

/// <summary>
/// THE WINDOWS IMAGE: the same link -- merge, resolve, addresses, relocate --
/// with a PE file around it instead of an ELF one (docs/WIN32-TARGET.md):
/// PE32 for i386 objects, PE32+ for long-mode ones (eight-byte addresses,
/// DIR64 fix-ups, RIP-relative thunks, and .pdata from the objects' unwind
/// tables). The objects do not change; only what is written at the end does.
///
///   ImageBase     MS-DOS header and stub, PE headers, section table
///   +0x1000       .text    code, then a `jmp [IAT]` thunk per import
///                 .rdata   constants, then the export directory
///                 .data    numbers, statics, then .bss (memory, no file bytes)
///                 .pdata   (PE32+) a RUNTIME_FUNCTION for every function
///                 .idata   the import directory, its lookup tables and IAT
///                 .reloc   a HIGHLOW (PE32) or DIR64 (PE32+) fix-up for every absolute address
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

        /// <summary>
        /// The address it is linked to, a multiple of 64 KiB as Windows
        /// requires; null for the default of its kind and mode (PeFormat:
        /// 0x400000 or 0x10000000 for i386, 0x140000000 or 0x180000000 for x64).
        /// </summary>
        public ulong? ImageBase { get; init; }

        /// <summary>The symbol the loader enters: a program's start, a DLL's DllMain. Null: a DLL with none.</summary>
        public string? Entry { get; init; }

        public PeSubsystem Subsystem { get; init; } = PeSubsystem.WindowsGui;

        /// <summary>--map FILE: every symbol of the image, largest first, as the flat link writes it.</summary>
        public string? MapPath { get; init; }

        /// <summary>The image's own name, which its export directory carries: the file's.</summary>
        public string Name { get; init; } = "";

        /// <summary>
        /// THE RUNTIME DLL (--windows --shared): every global the image
        /// defines exported by its own name, functions and data alike, for
        /// the images over it to import.
        /// </summary>
        public bool ExportAll { get; init; }

        /// <summary>
        /// AN IMAGE OVER A RUNTIME DLL (--windows --dynamic): each name a runtime
        /// DLL exports, and the DLL. An undefined symbol among them is imported
        /// from it -- a call through a thunk as any import, and a word holding
        /// its address filled at start from the import's slot (DataImports).
        /// </summary>
        public IReadOnlyDictionary<string, string>? RuntimeSymbols { get; init; }
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

    /// <summary>
    /// A thunk: `jmp [IAT slot]`, six bytes, FF 25 and a 32-bit field -- the
    /// slot's absolute address in a PE32 image (a HIGHLOW fix-up), its
    /// distance from the thunk's end in a PE32+ one, where FF 25 is
    /// RIP-relative and the jump takes the slot's eight bytes (MSVC's form;
    /// no fix-up).
    /// </summary>
    private const int PeThunkBytes = 6;

    /// <summary>The name of the table each long-mode object's functions are described in (UnwindTable), read here into .pdata.</summary>
    public const string UnwindTableSection = ".corsac.unwind";

    /// <summary>One record of UnwindTableSection: the function's start, its end and its UNWIND_INFO, each an Abs64.</summary>
    public const int UnwindRecordBytes = 24;

    public static void LinkPeTo(string path, IEnumerable<(string Name, ObjectFile Object)> objects, PeOptions options)
        => File.WriteAllBytes(path, LinkPe(objects, options));

    public static byte[] LinkPe(IEnumerable<(string Name, ObjectFile Object)> objects, PeOptions options)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        List<Input> inputs = objects.Select(o => new Input(o.Name, o.Object)).ToList();
        if (inputs.Count == 0)
        {
            throw new LinkException(new[] { "nothing to link" });
        }

        TargetContract.Validate(inputs.Select(i => (i.Name, i.Object)));
        // PE32 FOR i386 OBJECTS, PE32+ FOR LONG-MODE ONES (Validate has seen
        // that they are all of one mode): eight-byte addresses in data and in
        // the import tables, DIR64 fix-ups, RIP-relative thunks, and .pdata
        // describing every function's frame for Windows' unwinder.
        bool longMode = inputs.Any(i => TargetContract.IsLongMode(i.Object));
        int word = longMode ? 8 : 4;
        RelocKind absolute = longMode ? RelocKind.Abs64 : RelocKind.Abs32;
        ulong imageBase = options.ImageBase
            ?? (longMode ? options.Dll ? PeFormat.DefaultDllBase64 : PeFormat.DefaultImageBase64
                         : options.Dll ? PeFormat.DefaultDllBase : PeFormat.DefaultImageBase);
        if (imageBase % 0x10000 != 0)
        {
            throw new ArgumentException($"image base 0x{imageBase:x} is not a multiple of 64 KiB, which Windows requires", nameof(options));
        }
        if (!longMode && imageBase > uint.MaxValue)
        {
            throw new ArgumentException($"image base 0x{imageBase:x} is above four gigabytes, where an i386 image cannot be", nameof(options));
        }
        // EVERY UNIT FINDS ITS THREAD BLOCK IN THE TEB: one compiled for GS
        // (Linux) or for one global word (bare metal) reads the wrong place.
        if (inputs.FirstOrDefault(i => !TargetContract.IsWindows(i.Object) && i.Object.Sections.Any(s => s.Name == TargetContract.SectionName)
                && !i.Object.Sections.Any(s => s.Name == TargetContract.SectionName && s.Bytes.Count == TargetContract.Size
                    && BinaryPrimitives.ReadUInt32LittleEndian(s.Bytes.ToArray().AsSpan(20)) == TargetContract.NoTlsClaim)) is { } other)
        {
            throw new LinkException(new[] { $"{other.Name} was not compiled for Windows (--windows or --dll); its thread block is not in the TEB" });
        }
        ManagedLayoutContract.Validate(inputs.Select(i => (i.Name, i.Object)));
        Corsac.Lang.Lto.DefinitionCoalescer.Run(inputs.Select(i => (i.Name, i.Object)).ToArray());
        // The copies chosen, each object's descriptors packed (DescriptorPacker).
        Corsac.Lang.Lto.DescriptorPacker.Run(inputs.Select(i => i.Object));
        List<string> errors = new();
        if (ProgramInitializers(inputs, longMode) is ObjectFile programInit) inputs.Add(new Input("the program's initialisers", programInit));
        if (Corsac.Lang.Lto.Preinit.Lay(inputs.Select(i => i.Object), longMode) is ObjectFile preinitialised) inputs.Add(new Input("the statics laid down", preinitialised));

        // A DIRECT CALL TO A WAY IN FROM WINDOWS GOES TO ITS BODY in a PE32+
        // image: such a function's name is a stub Windows' x64 convention
        // calls (X64Backend.WindowsEntry), and its body, compiled by this
        // compiler's own convention, is the name with "$sysv". A call of this
        // language's (Plt32, `call rel32`) is by the body's convention; only
        // an address taken -- what Windows is handed -- stays the stub's.
        if (longMode) CallBodies(inputs);

        // (LongMode: eight-byte addresses in data, the managed metadata's
        // words PC-relative; Relocate's long-mode static branch.)
        Layout layout = new(imageBase) { LongMode = longMode };
        layout.ArrangeStatic();
        AddManagedMetadata(inputs, layout, errors);
        Merge(inputs, layout, errors);
        Resolve(inputs, layout, errors);
        ResolveManagedMetadata(layout, errors);

        // THE IMPORTS: every `dll!export` an object names and none defines. A
        // thunk each, at the end of the code, and the symbol defined there, so
        // the relocation phase reaches it as it reaches any other function.
        List<PeImport> imports = PeImportsOf(inputs, layout, errors, options.RuntimeSymbols);
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
        List<(Input Input, string Export, string Symbol)> exports = options.ExportAll ? EveryGlobal(inputs, layout) : PeExportsOf(inputs, errors);

        // THE WORDS AN IMPORT'S ADDRESS GOES IN (DataImports): every absolute
        // reference to a runtime DLL's symbol, in code or in data, listed in a
        // table of the image's own that the runtime fills from the import's
        // slot as the image starts. Laid down beside the constants, its ends
        // named for the entry that hands it over.
        HashSet<string> runtimeImports = new(imports.Where(i => options.RuntimeSymbols?.ContainsKey(i.Symbol) == true).Select(i => i.Symbol), StringComparer.Ordinal);
        List<(Placed Placed, Relocation Reloc)> dataImports = new();
        foreach (Input input in inputs)
            foreach (Placed p in input.Placed)
                if (p.Output.IsAllocated)
                    foreach (Relocation r in p.Section.Relocs)
                        if (r.Kind == absolute && runtimeImports.Contains(r.Symbol) && !input.Locals.ContainsKey(r.Symbol))
                            dataImports.Add((p, r));
        OutputSection? dataImportTable = null;
        if (options.RuntimeSymbols is not null)
        {
            dataImportTable = new OutputSection(".rdata$corsac_imports", SectionKind.ReadOnlyData) { Align = 4, Size = (uint)(dataImports.Count * 8) };
            dataImportTable.Content = new byte[dataImportTable.Size];
            layout.Globals[DataImportsStart] = new Definition("the data imports", dataImportTable, 0, null);
            layout.Globals[DataImportsEnd] = new Definition("the data imports", dataImportTable, dataImportTable.Size, null);
        }
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
            importSection = new OutputSection(".idata", SectionKind.Data) { Align = (uint)word };
            importSection.Size = PeImportBytes(imports, word);
            importSection.Content = new byte[importSection.Size];
            layout.Extra.Add(importSection);
        }
        // THE GOT IS THE IMPORT ADDRESS TABLE. Code reaches another image's
        // data through a slot holding its address -- i386's GotAddr, an
        // absolute slot address; long mode's GotPcRel, the slot RIP-relative
        // -- and an import's slot is that: the loader fills it. A symbol this
        // image defines gets a slot of its own, in a small table beside the
        // constants, but for long mode's `mov r64, [rip + slot]`, which
        // becomes `lea r64, [rip + symbol]` (the GOTPCRELX relaxation, as
        // Dynamic.RelaxableGotLoad does it). Each such relocation becomes an
        // absolute one to its slot on i386, a RIP-relative one in long mode.
        Dictionary<string, uint> importSlots = ImportSlotOffsets(imports, word);
        Dictionary<string, (Input Input, string Symbol, uint Offset)> ownSlots = new(StringComparer.Ordinal);
        OutputSection? ownGot = null;
        for (int n = 0; n < inputs.Count; n++)
        {
            Input input = inputs[n];
            foreach (Section section in input.Object.Sections)
            {
                for (int i = 0; i < section.Relocs.Count; i++)
                {
                    Relocation r = section.Relocs[i];
                    if (r.Kind != (longMode ? RelocKind.GotPcRel : RelocKind.GotAddr)) continue;
                    bool local = input.Locals.ContainsKey(r.Symbol);
                    string slot;
                    if (!local && importSlots.TryGetValue(r.Symbol, out uint offset))
                    {
                        slot = "got$" + r.Symbol;
                        layout.Globals[slot] = new Definition("the import directory", importSection!, offset, null);
                    }
                    else if (longMode && input.Placed.FirstOrDefault(placed => placed.Section == section) is { } p && RelaxableLoad(p.Bytes, r))
                    {
                        // mov r64, [rip + slot]  ->  lea r64, [rip + symbol],
                        // in the copy the link places (Merge's), as
                        // Dynamic.RelaxableGotLoad patches it.
                        p.Bytes[r.Offset - 2] = 0x8D;
                        section.Relocs[i] = r with { Kind = RelocKind.Rel32 };
                        continue;
                    }
                    else
                    {
                        slot = "got$" + (local ? n + "$" : "") + r.Symbol;
                        if (!ownSlots.ContainsKey(slot)) ownSlots[slot] = (input, r.Symbol, (uint)(ownSlots.Count * word));
                    }
                    section.Relocs[i] = r with { Kind = longMode ? RelocKind.Rel32 : RelocKind.Abs32, Symbol = slot };
                }
            }
        }
        if (ownSlots.Count > 0)
        {
            ownGot = new OutputSection(".rdata$got", SectionKind.ReadOnlyData) { Align = (uint)word, Size = (uint)(ownSlots.Count * word) };
            ownGot.Content = new byte[ownGot.Size];
            foreach ((string slot, (Input _, string _, uint offset)) in ownSlots)
                layout.Globals[slot] = new Definition("the image's GOT", ownGot, offset, null);
        }
        // AN ABSOLUTE 32-BIT ADDRESS IN A PE32+ IMAGE cannot be moved: the
        // image is above four gigabytes and .reloc has no 32-bit form there.
        // The long-mode backend writes none; an object that has one says so.
        if (longMode)
        {
            foreach (Input input in inputs)
                foreach (Section section in input.Object.Sections)
                    if (section.Kind != SectionKind.Note)
                        foreach (Relocation r in section.Relocs)
                            if (r.Kind is RelocKind.Abs32 or RelocKind.Abs32S)
                                errors.Add($"{input.Name} ({section.Name}+0x{r.Offset:x}): a 32-bit absolute address of '{r.Symbol}', which a 64-bit Windows image above four gigabytes cannot hold or move");
        }

        // .pdata: a RUNTIME_FUNCTION for every function the objects describe
        // (UnwindTableSection), filled once addresses are known.
        int unwindRecords = 0;
        foreach (Input input in inputs)
            foreach (Section section in input.Object.Sections)
                if (section.Name == UnwindTableSection) unwindRecords += section.Size / UnwindRecordBytes;
        OutputSection? pdata = null;
        if (unwindRecords > 0)
        {
            pdata = new OutputSection(".pdata", SectionKind.ReadOnlyData) { Align = 4, Size = (uint)unwindRecords * PeFormat.RuntimeFunctionBytes };
            pdata.Content = new byte[pdata.Size];
        }
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }

        List<PeSection> sections = new()
        {
            new PeSection(".text", PeSectionFlags.Code | PeSectionFlags.Execute | PeSectionFlags.Read, layout.Text, thunks),
            new PeSection(".rdata", PeSectionFlags.InitializedData | PeSectionFlags.Read, layout.ReadOnlyData, ownGot, dataImportTable, exportSection),
            new PeSection(".pdata", PeSectionFlags.InitializedData | PeSectionFlags.Read, pdata),
            new PeSection(".data", PeSectionFlags.InitializedData | PeSectionFlags.Read | PeSectionFlags.Write, layout.Numbers, layout.Data, layout.Bss),
            new PeSection(".idata", PeSectionFlags.InitializedData | PeSectionFlags.Read | PeSectionFlags.Write, importSection),
        };
        sections.RemoveAll(section => section.Empty);

        // .reloc is last, and whether it is there at all is known now: an
        // image with no absolute address in it moves without one.
        // (Not a word the runtime fills at start, DataImports: its words are no fix-up's.)
        HashSet<(Placed, int)> importWords = new(dataImports.Select(d => (d.Placed, d.Reloc.Offset)));
        bool hasFixups = (thunks is not null && !longMode) || ownSlots.Count > 0
            || inputs.Any(input => input.Placed.Any(p => p.Output.IsAllocated && p.Section.Relocs.Any(r => r.Kind == absolute && !importWords.Contains((p, r.Offset)))));
        int sectionCount = sections.Count + (hasFixups ? 1 : 0);
        int optionalHeaderBytes = longMode ? PeFormat.OptionalHeader64Bytes : PeFormat.OptionalHeaderBytes;
        uint headerBytes = (uint)(PeFormat.DosBytes + 4 + PeFormat.CoffHeaderBytes + optionalHeaderBytes + sectionCount * PeFormat.SectionHeaderBytes);
        uint sizeOfHeaders = PeFormat.AlignUp(headerBytes, PeFormat.FileAlignment);
        uint nextRva = 0, nextFile = 0;
        Place(layout, l => (nextRva, nextFile) = AssignPeAddresses(l, sections, sizeOfHeaders));
        if (!longMode) BelowFourGigabytes(layout);
        DefineLinkerSymbols(layout);
        Relocate(inputs, layout, errors);

        if (options.MapPath is not null) WriteMap(layout, inputs, options.MapPath);

        uint entryRva = 0;
        if (options.Entry is not null)
        {
            if (!layout.Globals.TryGetValue(options.Entry, out Definition entry))
            {
                errors.Add($"entry symbol '{options.Entry}' is not defined by any object");
            }
            else
            {
                entryRva = checked((uint)(entry.Address - imageBase));
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
        // The image's own GOT slots: each its symbol's address, moved by .reloc.
        List<uint> ownGotFixups = new();
        if (ownGot is not null)
        {
            foreach ((string slot, (Input input, string symbol, uint offset)) in ownSlots)
            {
                if (Lookup(input, layout, symbol) is not Definition d)
                {
                    errors.Add($"'{symbol}' is reached through the GOT from {input.Name}, and nothing defines it");
                    continue;
                }
                if (longMode) BinaryPrimitives.WriteUInt64LittleEndian(ownGot.Content!.AsSpan((int)offset), d.Address);
                else BinaryPrimitives.WriteUInt32LittleEndian(ownGot.Content!.AsSpan((int)offset), checked((uint)d.Address));
                ownGotFixups.Add(checked((uint)(ownGot.Addr - imageBase + offset)));
            }
        }
        Dictionary<string, ulong> slots = new(StringComparer.Ordinal);
        if (thunks is not null && importSection is not null)
        {
            slots = FillImports(imports, importSection, thunks, imageBase, word);
        }
        // Each word's place and its import's slot, as RVAs; the word itself
        // keeps only the addend, which the runtime adds the slot's value to.
        HashSet<ulong> filledAtStart = new();
        for (int i = 0; i < dataImports.Count; i++)
        {
            (Placed p, Relocation r) = dataImports[i];
            ulong site = p.Output.Addr + p.Offset + (ulong)r.Offset;
            if (longMode) p.Bytes.WriteInt64((int)r.Offset, r.Addend);
            else p.Bytes.WriteUInt32((int)r.Offset, unchecked((uint)r.Addend));
            filledAtStart.Add(site);
            BinaryPrimitives.WriteUInt32LittleEndian(dataImportTable!.Content!.AsSpan(i * 8), checked((uint)(site - imageBase)));
            BinaryPrimitives.WriteUInt32LittleEndian(dataImportTable.Content.AsSpan(i * 8 + 4), checked((uint)(slots[r.Symbol] - imageBase)));
        }
        if (exportSection is not null)
        {
            FillExports(exports, exportSection, layout, imageBase, options.Name, errors, options.ExportAll);
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
                    if (r.Kind == absolute && !filledAtStart.Contains(p.Output.Addr + p.Offset + (ulong)r.Offset))
                    {
                        fixups.Add(checked((uint)(p.Output.Addr - imageBase + p.Offset + (uint)r.Offset)));
                    }
                }
            }
        }
        fixups.AddRange(ownGotFixups);
        // (A PE32+ thunk's field is RIP-relative, and moves with the image.)
        if (thunks is not null && !longMode)
        {
            for (int i = 0; i < imports.Count; i++)
            {
                fixups.Add(checked((uint)(thunks.Addr - imageBase + (ulong)(i * PeThunkBytes) + 2)));
            }
        }
        if (pdata is not null) FillPdata(inputs, pdata, imageBase, errors);
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }
        PeSection? relocSection = null;
        if (hasFixups)
        {
            byte[] blocks = BaseRelocations(fixups, longMode ? PeBaseRelocation.Dir64 : PeBaseRelocation.HighLow);
            OutputSection reloc = new(".reloc", SectionKind.ReadOnlyData) { Align = 4, Size = (uint)blocks.Length, Content = blocks };
            relocSection = new PeSection(".reloc", PeSectionFlags.InitializedData | PeSectionFlags.Read | PeSectionFlags.Discardable, reloc)
            {
                Rva = nextRva,
                RawPointer = nextFile,
                VirtualSize = reloc.Size,
                RawSize = PeFormat.AlignUp(reloc.Size, PeFormat.FileAlignment),
            };
            reloc.Addr = imageBase + nextRva;
            reloc.FileOffset = nextFile;
            sections.Add(relocSection);
            nextRva = PeFormat.AlignUp(nextRva + reloc.Size, PeFormat.SectionAlignment);
            nextFile += relocSection.RawSize;
        }

        byte[] image = new byte[nextFile];
        WritePeHeaders(image, sections, options, imageBase, longMode, sizeOfHeaders, nextRva, entryRva, exportSection, importSection, thunks, imports, relocSection, pdata);
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
    private static List<PeImport> PeImportsOf(List<Input> inputs, Layout layout, List<string> errors, IReadOnlyDictionary<string, string>? runtime)
    {
        SortedDictionary<string, PeImport> found = new(StringComparer.Ordinal);
        foreach (Input input in inputs)
        {
            foreach (Section s in input.Object.Sections)
            {
                foreach (Relocation r in s.Relocs)
                {
                    if (found.ContainsKey(r.Symbol) || input.Locals.ContainsKey(r.Symbol)) continue;
                    // What a runtime DLL defines and this image does not.
                    if (runtime is not null && !layout.Globals.ContainsKey(r.Symbol) && runtime.TryGetValue(r.Symbol, out string? from))
                    {
                        found[r.Symbol] = new PeImport(from, r.Symbol, 0, r.Symbol);
                        continue;
                    }
                    if (!WindowsImports.TryParse(r.Symbol, out string dll, out string? export, out ushort ordinal))
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
    private static uint PeImportBytes(List<PeImport> imports, int word)
    {
        List<string> dlls = imports.Select(i => i.Dll).Distinct(StringComparer.Ordinal).ToList();
        uint size = DescriptorsBytes(dlls.Count, word);
        size += 2 * (uint)(imports.Count + dlls.Count) * (uint)word;
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

    /// <summary>
    /// The import directory's bytes, and each thunk's jump through its IAT
    /// slot. Lookup and address entries are a word each: four bytes in PE32,
    /// eight in PE32+, whose import by ordinal is bit 63.
    /// </summary>
    private static Dictionary<string, ulong> FillImports(List<PeImport> imports, OutputSection idata, OutputSection thunks, ulong imageBase, int word)
    {
        byte[] b = idata.Content!;
        uint rva = checked((uint)(idata.Addr - imageBase));
        List<string> dlls = imports.Select(i => i.Dll).Distinct(StringComparer.Ordinal).ToList();
        uint lookups = DescriptorsBytes(dlls.Count, word);
        uint tables = (uint)word * (uint)(imports.Count + dlls.Count);
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
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)descriptor), rva + lookups + slot * (uint)word);        // OriginalFirstThunk
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)descriptor + 12), rva + dllName);                        // Name
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)descriptor + 16), rva + addresses + slot * (uint)word);  // FirstThunk
            Encoding.ASCII.GetBytes(dlls[d], b.AsSpan((int)dllName));
            dllName += (uint)dlls[d].Length + 1;
            foreach (PeImport import in imports.Where(i => i.Dll == dlls[d]))
            {
                ulong entry;
                if (import.Export is null)
                {
                    entry = (word == 8 ? PeFormat.ImportByOrdinal64 : PeFormat.ImportByOrdinal) | import.Ordinal;
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
                if (word == 8)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)(lookups + slot * 8)), entry);
                    BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((int)(addresses + slot * 8)), entry);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)(lookups + slot * 4)), (uint)entry);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)(addresses + slot * 4)), (uint)entry);
                }
                slotOf[import.Symbol] = slot;
                slot++;
            }
            slot++; // the null entry ending this DLL's tables
        }

        byte[] t = thunks.Content!;
        for (int i = 0; i < imports.Count; i++)
        {
            int at = i * PeThunkBytes;
            t[at] = 0xFF;      // jmp [disp32]: absolute in PE32, RIP-relative (from the thunk's end) in PE32+
            t[at + 1] = 0x25;
            ulong slotAddress = idata.Addr + addresses + slotOf[imports[i].Symbol] * (uint)word;
            uint field = word == 8 ? unchecked((uint)checked((int)((long)slotAddress - (long)(thunks.Addr + (ulong)at + PeThunkBytes))))
                                   : checked((uint)slotAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(at + 2), field);
        }
        // Each import's slot in the IAT, by address.
        return slotOf.ToDictionary(kv => kv.Key, kv => idata.Addr + addresses + kv.Value * (uint)word, StringComparer.Ordinal);
    }

    /// <summary>Each import's IAT slot, as an offset in the import section (FillImports lays them out so).</summary>
    private static Dictionary<string, uint> ImportSlotOffsets(List<PeImport> imports, int word)
    {
        Dictionary<string, uint> offsets = new(StringComparer.Ordinal);
        List<string> dlls = imports.Select(i => i.Dll).Distinct(StringComparer.Ordinal).ToList();
        uint lookups = DescriptorsBytes(dlls.Count, word);
        uint addresses = lookups + (uint)word * (uint)(imports.Count + dlls.Count);
        uint slot = 0;
        foreach (string dll in dlls)
        {
            foreach (PeImport import in imports.Where(i => i.Dll == dll)) offsets[import.Symbol] = addresses + slot++ * (uint)word;
            slot++;
        }
        return offsets;
    }

    /// <summary>The body's suffix a way in from Windows is compiled under (X64Backend.NativeEntryBody).</summary>
    public const string NativeEntryBody = "$sysv";

    /// <summary>
    /// Every direct call (Plt32) to a symbol some input also defines with
    /// NativeEntryBody after it, sent to that body: the input's own local
    /// first, then a global.
    /// </summary>
    private static void CallBodies(List<Input> inputs)
    {
        HashSet<string> globalBodies = new(StringComparer.Ordinal);
        foreach (Input input in inputs)
            foreach (Symbol sym in input.Object.Symbols)
                if (sym.Global && sym.Section is not null && sym.Name.EndsWith(NativeEntryBody, StringComparison.Ordinal)) globalBodies.Add(sym.Name);
        foreach (Input input in inputs)
        {
            HashSet<string> localBodies = new(input.Object.Symbols
                .Where(sym => !sym.Global && sym.Section is not null && sym.Name.EndsWith(NativeEntryBody, StringComparison.Ordinal)).Select(sym => sym.Name), StringComparer.Ordinal);
            foreach (Section section in input.Object.Sections)
                for (int i = 0; i < section.Relocs.Count; i++)
                {
                    Relocation r = section.Relocs[i];
                    if (r.Kind != RelocKind.Plt32 || r.Symbol.EndsWith(NativeEntryBody, StringComparison.Ordinal)) continue;
                    string body = r.Symbol + NativeEntryBody;
                    if (localBodies.Contains(body) || globalBodies.Contains(body)) section.Relocs[i] = r with { Symbol = body };
                }
        }
    }

    /// <summary>
    /// The import descriptors and the null one ending them, padded to the
    /// word the lookup and address tables after them hold: a PE32+ image's
    /// eight-byte entries each on an eight-byte boundary, as MSVC's and
    /// LLD's are.
    /// </summary>
    private static uint DescriptorsBytes(int dlls, int word)
        => PeFormat.AlignUp((uint)(dlls + 1) * PeFormat.ImportDescriptorBytes, (uint)word);

    /// <summary>`mov r64, [rip + slot]` (REX.W 8B, ModRM mod 00 r/m 101) ending at the relocation's field: the GOT load a defined symbol's address relaxes from.</summary>
    private static bool RelaxableLoad(Corsac.Lang.Ir.ChunkedBytes bytes, Relocation r)
        => r.Offset >= 3 && r.Offset <= bytes.Count && r.Addend == -4
            && (bytes[r.Offset - 3] & 0xF8) == 0x48 && bytes[r.Offset - 2] == 0x8B && (bytes[r.Offset - 1] & 0xC7) == 0x05;

    /// <summary>
    /// .pdata: a RUNTIME_FUNCTION -- the begin, end and UNWIND_INFO RVAs --
    /// for each record of every object's UnwindTableSection, whose three
    /// Abs64 words the link has resolved by now; sorted by address, as
    /// Windows' unwinder searches it (RtlLookupFunctionEntry: a binary search).
    /// </summary>
    private static void FillPdata(List<Input> inputs, OutputSection pdata, ulong imageBase, List<string> errors)
    {
        List<(uint Begin, uint End, uint Info)> records = new();
        foreach (Input input in inputs)
        {
            foreach (Placed p in input.Placed)
            {
                if (p.Section.Name != UnwindTableSection) continue;
                for (int at = 0; at + UnwindRecordBytes <= p.Bytes.Count; at += UnwindRecordBytes)
                {
                    ulong begin = (ulong)p.Bytes.ReadInt64(at), end = (ulong)p.Bytes.ReadInt64(at + 8), info = (ulong)p.Bytes.ReadInt64(at + 16);
                    if (begin < imageBase || end <= begin || info < imageBase || end - imageBase > uint.MaxValue || info - imageBase > uint.MaxValue)
                    {
                        errors.Add($"{input.Name}: an unwind record at +0x{at:x} does not name a function and its UNWIND_INFO in the image");
                        continue;
                    }
                    records.Add(((uint)(begin - imageBase), (uint)(end - imageBase), (uint)(info - imageBase)));
                }
            }
        }
        // ONE RECORD A FUNCTION: a function several units define is kept once
        // (DefinitionCoalescer), and every copy's record then names the one
        // kept -- the same range twice, with the same blob's shape -- so
        // such twins are one entry. Two records that overlap otherwise are
        // an error. The unused tail of .pdata is left zero; its directory
        // says only what is filled (FillPdata's count).
        records.Sort((a, b) => a.Begin != b.Begin ? a.Begin.CompareTo(b.Begin) : a.End.CompareTo(b.End));
        List<(uint Begin, uint End, uint Info)> kept = new();
        foreach (var record in records)
        {
            if (kept.Count > 0 && kept[^1].Begin == record.Begin && kept[^1].End == record.End) continue;
            if (kept.Count > 0 && record.Begin < kept[^1].End)
            {
                errors.Add($"two functions' unwind records overlap at RVA 0x{record.Begin:x}");
                continue;
            }
            kept.Add(record);
        }
        records = kept;
        pdata.Size = (uint)records.Count * PeFormat.RuntimeFunctionBytes;
        byte[] b = pdata.Content!;
        for (int i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 12), records[i].Begin);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 12 + 4), records[i].End);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 12 + 8), records[i].Info);
        }
    }

    /// <summary>The ends of the table of words an import's address goes in (DataImports), which the entry hands the runtime.</summary>
    public const string DataImportsStart = "__corsac_dimports", DataImportsEnd = "__corsac_dimports_end";

    /// <summary>
    /// The runtime DLL's exports: every global the link defines, but the
    /// linker's own (NotExported), by name, sorted as the loader searches them.
    /// </summary>
    private static List<(Input Input, string Export, string Symbol)> EveryGlobal(List<Input> inputs, Layout layout)
        => layout.GlobalOrder.Where(name => !layout.NotExported.Contains(name) && !WindowsImports.TryParse(name, out _, out _, out _)
                && name.All(c => c > ' ' && c < 0x7F) && layout.Globals[name].Section is { } section && section.IsAllocated)
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => (inputs[0], name, name)).ToList();

    /// <summary>The export directory's bytes: ordinals from 1, in the order of the names.</summary>
    private static void FillExports(List<(Input Input, string Export, string Symbol)> exports, OutputSection edata, Layout layout, ulong imageBase, string name, List<string> errors, bool allData = false)
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
            if (Lookup(input, layout, symbol) is not { Section: { } section } d || !(section.Kind == SectionKind.Code || allData && section.IsAllocated))
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
    /// type (HIGHLOW for a PE32 image's four-byte words, DIR64 for a PE32+
    /// one's eight) and the offset in the page, a block padded to four bytes
    /// with an ABSOLUTE entry the loader skips.
    /// </summary>
    private static byte[] BaseRelocations(List<uint> fixups, PeBaseRelocation type)
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
                ushort entry = (ushort)((ushort)type << 12 | (rva & (PeFormat.SectionAlignment - 1)));
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
    /// MS-DOS), the PE signature, the COFF header, the optional header --
    /// PE32's for i386, PE32+'s for x64 (no BaseOfData, eight-byte ImageBase
    /// and stack and heap sizes, the directories at 112) -- with its data
    /// directories, and the section table.
    /// </summary>
    private static void WritePeHeaders(byte[] b, List<PeSection> sections, PeOptions options, ulong imageBase, bool longMode, uint sizeOfHeaders, uint sizeOfImage, uint entryRva,
                                       OutputSection? exports, OutputSection? imports, OutputSection? thunks, List<PeImport> importList, PeSection? reloc, OutputSection? pdata)
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

        // THE COFF HEADER. A PE32+ image is large-address aware (it may be
        // put anywhere, HighEntropyVa below) and not a 32-bit machine's.
        PeCharacteristics characteristics = PeCharacteristics.ExecutableImage
            | PeCharacteristics.LineNumsStripped | PeCharacteristics.LocalSymsStripped | PeCharacteristics.DebugStripped
            | (longMode ? PeCharacteristics.LargeAddressAware : PeCharacteristics.Machine32Bit);
        if (options.Dll) characteristics |= PeCharacteristics.Dll;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)(longMode ? PeMachine.Amd64 : PeMachine.I386));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 2), (ushort)sections.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 16), (ushort)(longMode ? PeFormat.OptionalHeader64Bytes : PeFormat.OptionalHeaderBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 18), (ushort)characteristics);
        at += PeFormat.CoffHeaderBytes;

        // THE OPTIONAL HEADER. Windows 95's and NT 4's loaders refuse a
        // subsystem or system version above 4.0, so both are 4.0 for PE32.
        // PE32+ names 5.2, the first 64-bit Windows (XP x64, Server 2003),
        // as MinGW's linker does, with the system version 4.0 as it writes.
        int o = at;
        uint code = 0, initialised = 0;
        foreach (PeSection s in sections)
        {
            if ((s.Flags & PeSectionFlags.Code) != 0) code += s.RawSize;
            else initialised += s.RawSize;
        }
        PeSection? text = sections.FirstOrDefault(s => (s.Flags & PeSectionFlags.Code) != 0);
        PeSection? data = sections.FirstOrDefault(s => (s.Flags & PeSectionFlags.Code) == 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), longMode ? PeFormat.Pe32PlusMagic : PeFormat.Pe32Magic);
        b[o + 2] = 1;                                                                   // linker version 1.0
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), code);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 8), initialised);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 16), entryRva);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 20), text?.Rva ?? 0);    // BaseOfCode
        if (longMode)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 24), imageBase);     // ImageBase, eight bytes where PE32 has BaseOfData and its own
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 24), data?.Rva ?? 0);    // BaseOfData
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 28), checked((uint)imageBase));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 32), PeFormat.SectionAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 36), PeFormat.FileAlignment);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 40), 4);                 // operating system 4.0
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 48), longMode ? PeFormat.Subsystem64Major : (ushort)4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 50), longMode ? PeFormat.Subsystem64Minor : (ushort)0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 56), sizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 60), sizeOfHeaders);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 68), (ushort)options.Subsystem);
        PeDllCharacteristics dll = PeDllCharacteristics.NxCompatible | (reloc is not null ? PeDllCharacteristics.DynamicBase : 0);
        if (longMode && reloc is not null) dll |= PeDllCharacteristics.HighEntropyVa;
        if (!options.Dll) dll |= PeDllCharacteristics.TerminalServerAware;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 70), (ushort)dll);
        // The stack and heap sizes: four bytes each in PE32, eight in PE32+.
        int sizes = 72, width = longMode ? 8 : 4;
        foreach (ulong value in new ulong[] { 0x100000, 0x1000, 0x100000, 0x1000 })   // stack reserved, committed; heap reserved, committed
        {
            if (longMode) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + sizes), value);
            else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + sizes), (uint)value);
            sizes += width;
        }
        int directories = longMode ? 112 : 96;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + directories - 4), PeFormat.DataDirectoryCount);
        void Directory(PeDirectory which, ulong address, uint size)
        {
            int d = o + directories + 8 * (int)which;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(d), checked((uint)(address - imageBase)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(d + 4), size);
        }
        if (exports is not null) Directory(PeDirectory.Export, exports.Addr, exports.Size);
        if (imports is not null)
        {
            int dllCount = importList.Select(i => i.Dll).Distinct(StringComparer.Ordinal).Count();
            int word = longMode ? 8 : 4;
            uint tables = (uint)word * (uint)(importList.Count + dllCount);
            Directory(PeDirectory.Import, imports.Addr, (uint)(dllCount + 1) * PeFormat.ImportDescriptorBytes);
            Directory(PeDirectory.ImportAddressTable, imports.Addr + DescriptorsBytes(dllCount, word) + tables, tables);
        }
        if (reloc is not null) Directory(PeDirectory.BaseRelocation, imageBase + reloc.Rva, reloc.VirtualSize);
        if (pdata is not null) Directory(PeDirectory.Exception, pdata.Addr, pdata.Size);
        at += longMode ? PeFormat.OptionalHeader64Bytes : PeFormat.OptionalHeaderBytes;

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
