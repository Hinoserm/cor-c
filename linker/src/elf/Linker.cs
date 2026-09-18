#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// The static linker: several objects in, one ET_EXEC out, laid out the way
/// GNU ld lays out a non-PIE i386 executable so gdb, objdump and the
/// kernel are all on familiar ground.
///
/// The link is five phases with no shared mutable state between them
/// beyond <see cref="Layout"/>: merge sections, resolve symbols, assign
/// addresses, apply relocations, emit. The dynamic-linking milestone adds
/// to each -- .dynsym/.plt/.got as more output sections, PT_INTERP and
/// PT_DYNAMIC as more program headers, GOT and PLT relocations as more
/// cases in <see cref="Relocate"/> -- without reordering them.
///
/// Memory image, with the traditional i386 Linux load address:
///
///   0x08048000  ELF header, program headers      \  PT_LOAD R X
///               .text, .rodata                   /
///   next page + (file offset mod 4096)
///               .data                            \  PT_LOAD RW
///               .bss  (memsz > filesz)           /
///   not loaded  .corsac.meta and other notes, .symtab, .strtab, .shstrtab
///
/// The RW segment's address is congruent to its file offset modulo the
/// page size, which is what lets the kernel mmap it rather than copy it,
/// and why there is a gap in the address space and not in the file.
/// </summary>
public static partial class Linker
{
    /// <summary>Managed shared-library initializer entry point.</summary>
    public const string SharedInitName = Elf.SharedInitName;
    public const uint DefaultLoadAddress = 0x08048000;

    public static byte[] Link(IEnumerable<ObjectFile> objects, string entrySymbol, uint loadAddress = DefaultLoadAddress)
    {
        ArgumentNullException.ThrowIfNull(objects);
        List<(string, ObjectFile)> named = new();
        int n = 0;
        foreach (ObjectFile o in objects)
        {
            n++;
            named.Add(($"object {n}", o));
        }
        return Link(named, entrySymbol, loadAddress);
    }

    /// <summary>
    /// Link objects that have names -- file names, usually -- so an error
    /// can say which one it means.
    /// </summary>
    public static byte[] Link(IEnumerable<(string Name, ObjectFile Object)> objects, string entrySymbol, uint loadAddress = DefaultLoadAddress, uint? physicalAddress = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(entrySymbol);
        if (loadAddress % Elf.PageSize != 0)
        {
            throw new ArgumentException($"load address 0x{loadAddress:x} is not page-aligned", nameof(loadAddress));
        }
        // The link address is where the code will RUN; the physical address
        // is where a loader with no paging on yet has to PUT it. They differ
        // for a higher-half kernel and for nothing else, and the difference
        // is one constant subtracted from every loaded segment's p_vaddr.
        if (physicalAddress is { } phys)
        {
            if (phys % Elf.PageSize != 0)
            {
                throw new ArgumentException($"physical address 0x{phys:x} is not page-aligned", nameof(physicalAddress));
            }
            if (phys > loadAddress)
            {
                throw new ArgumentException($"physical address 0x{phys:x} is above the link address 0x{loadAddress:x}", nameof(physicalAddress));
            }
        }

        List<Input> inputs = new();
        foreach ((string name, ObjectFile o) in objects)
        {
            inputs.Add(new Input(name, o));
        }
        if (inputs.Count == 0)
        {
            throw new LinkException(new[] { "nothing to link" });
        }

        TargetContract.Validate(inputs.Select(i => (i.Name, i.Object)));
        List<string> errors = new();
        Layout layout = new(loadAddress) { LoadBias = loadAddress - (physicalAddress ?? loadAddress) };
        layout.ArrangeStatic();
        Merge(inputs, layout, errors);
        Resolve(inputs, layout, errors);
        AssignAddresses(layout);
        DefineLinkerSymbols(layout);
        Relocate(inputs, layout, errors);

        if (!layout.Globals.TryGetValue(entrySymbol, out Definition entry))
        {
            errors.Add($"entry symbol '{entrySymbol}' is not defined by any object");
        }
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }
        // Where the loader jumps. With a load bias the image is entered
        // before anything has built a mapping -- that is what the bias means
        // -- so the entry has to be the PHYSICAL address of the entry symbol,
        // the same choice multiboot's entry_addr makes and for the same
        // reason. Without a bias the two are the same address anyway.
        return Emit(inputs, layout, entry.Address - layout.LoadBias, Elf.TypeExec);
    }

    /// <summary>
    /// A flat binary and what a loader has to be told about it.
    ///
    /// There is no header in the bytes -- a boot image is loaded at an
    /// address and jumped to, and anything in front of the first instruction
    /// would have to be stepped over -- so everything a loader needs is
    /// here instead, and whoever writes the image out records it.
    /// </summary>
    public sealed record FlatImage(
        byte[] Bytes,
        uint Base,
        uint Entry,
        uint TextSize,
        uint ReadOnlySize,
        uint DataSize,
        uint BssSize)
    {
        /// <summary>Bytes of memory the image occupies once .bss is zeroed.</summary>
        public uint MemorySize => (uint)Bytes.Length + BssSize;
    }

    /// <summary>
    /// The same link, with no ELF around it: .text, .rodata and .data laid
    /// out contiguously from <paramref name="baseAddress"/>, and .bss after
    /// them with no bytes in the file.
    ///
    /// This is what a machine with no loader can be given. Nothing zeroes
    /// .bss for us, so its size is reported and whatever put the image in
    /// memory has to clear that many bytes past the end; nothing maps the
    /// pieces separately either, so there is no page gap and no alignment
    /// beyond what the sections themselves ask for.
    ///
    /// The entry is required to be the first byte of the image. A loader
    /// that has just copied an image to an address knows that address and
    /// nothing else, so the alternative is a header, and a header in a flat
    /// binary is a thing the processor would try to execute. The driver puts
    /// the entry function first for this reason.
    /// </summary>
    public static FlatImage LinkFlat(IEnumerable<(string Name, ObjectFile Object)> objects, string entrySymbol, uint baseAddress)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(entrySymbol);

        List<Input> inputs = new();
        foreach ((string name, ObjectFile o) in objects)
        {
            inputs.Add(new Input(name, o));
        }
        if (inputs.Count == 0)
        {
            throw new LinkException(new[] { "nothing to link" });
        }

        TargetContract.Validate(inputs.Select(i => (i.Name, i.Object)));
        List<string> errors = new();
        Layout layout = new(baseAddress);
        layout.ArrangeStatic();
        Merge(inputs, layout, errors);
        Resolve(inputs, layout, errors);
        AssignFlatAddresses(layout);
        DefineLinkerSymbols(layout);
        Relocate(inputs, layout, errors);

        if (!layout.Globals.TryGetValue(entrySymbol, out Definition entry))
        {
            errors.Add($"entry symbol '{entrySymbol}' is not defined by any object");
        }
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }
        if (entry.Address != baseAddress)
        {
            throw new LinkException(new[]
            {
                $"'{entrySymbol}' is at 0x{entry.Address:x} but a flat image is entered at its first byte, 0x{baseAddress:x}",
            });
        }

        // .bss carries no bytes, so the file ends where .data does.
        uint size = layout.Data.Size != 0 ? layout.Data.Addr + layout.Data.Size - baseAddress
                  : layout.ReadOnlyData.Size != 0 ? layout.ReadOnlyData.Addr + layout.ReadOnlyData.Size - baseAddress
                  : layout.Text.Size;
        byte[] image = new byte[size];
        foreach (OutputSection s in new[] { layout.Text, layout.ReadOnlyData, layout.Data })
        {
            if (s.Size == 0)
            {
                continue;
            }
            if (s.Content is not null)
            {
                Array.Copy(s.Content, 0, image, s.Addr - baseAddress, s.Content.Length);
                continue;
            }
            foreach (Placed part in s.Parts)
            {
                Array.Copy(part.Bytes, 0, image, s.Addr - baseAddress + part.Offset, part.Bytes.Length);
            }
        }

        // Startup zeroes from the first byte after the file, so its range
        // includes any alignment gap before the BSS section itself.
        uint zeroBytes = layout.Bss.Size == 0 ? 0
            : checked(layout.Bss.Addr + layout.Bss.Size - baseAddress - size);
        return new FlatImage(image, baseAddress, entry.Address,
                             layout.Text.Size, layout.ReadOnlyData.Size, layout.Data.Size, zeroBytes);
    }

    /// <summary>
    /// Addresses for a flat image: everything in one run from the base, each
    /// section no more aligned than it asks to be. No headers, because there
    /// are none; no page-sized gap between the read-only and writable
    /// groups, because nothing is going to give them different permissions.
    /// </summary>
    private static void AssignFlatAddresses(Layout layout)
    {
        layout.HeaderBytes = 0;
        uint at = layout.LoadAddress;
        foreach (OutputSection s in layout.ReadOnly.Concat(layout.Writable))
        {
            if (s.Size == 0)
            {
                continue;
            }
            at = Elf.AlignUp(at, s.Align);
            s.Addr = at;
            s.FileOffset = at - layout.LoadAddress;
            at = checked(at + s.Size);
        }
        // A note section is not loaded and has no address; it has no place in
        // a flat image at all, and is dropped rather than appended.
        foreach (OutputSection n in layout.Notes)
        {
            n.Addr = 0;
            n.FileOffset = 0;
        }
        layout.FileEnd = at - layout.LoadAddress;
    }

    // ---- The model -------------------------------------------------------

    private sealed class Input
    {
        public string Name { get; }
        public ObjectFile Object { get; }
        public List<Placed> Placed { get; } = new();
        /// <summary>Names visible only from this object's relocations: its local symbols and its section names.</summary>
        public Dictionary<string, Definition> Locals { get; } = new();

        public Input(string name, ObjectFile obj)
        {
            Name = name;
            Object = obj;
        }
    }

    /// <summary>An input section placed within an output section.</summary>
    private sealed class Placed
    {
        public Section Section { get; }
        public OutputSection Output { get; }
        public uint Offset { get; }
        public byte[] Bytes { get; }

        public Placed(Section section, OutputSection output, uint offset)
        {
            Section = section;
            Output = output;
            Offset = offset;
            Bytes = section.Bytes.ToArray();
        }
    }

    private sealed class OutputSection
    {
        public string Name { get; }
        public SectionKind Kind { get; }

        /// <summary>
        /// Bytes the linker itself produced -- a PLT, a hash table, the
        /// dynamic array -- rather than bytes merged from an input. A
        /// synthetic section has no parts and is sized by this array, which
        /// is allocated when the section's size is known and filled once
        /// addresses are.
        /// </summary>
        public byte[]? Content { get; set; }

        /// <summary>SHT_ for the section header, when it is not the one the kind implies.</summary>
        public uint? TypeOverride { get; set; }
        public uint EntSize { get; set; }
        /// <summary>The section this one links to, by name; resolved at emit.</summary>
        public string? LinkTo { get; set; }
        public uint Align { get; set; } = 1;
        public uint Size { get; set; }
        public uint Addr { get; set; }
        public uint FileOffset { get; set; }
        /// <summary>Index in the output's section header table; 0 until emitted.</summary>
        public int Index { get; set; }
        public List<Placed> Parts { get; } = new();

        public bool IsAllocated => Kind != SectionKind.Note;
        public bool HasBytes => Kind != SectionKind.Uninitialised;

        public OutputSection(string name, SectionKind kind)
        {
            Name = name;
            Kind = kind;
        }
    }

    /// <summary>
    /// Where a name resolves to. A null section is an absolute value, used
    /// for the symbols the linker itself defines.
    /// </summary>
    private readonly record struct Definition(string Object, OutputSection? Section, uint Offset, Symbol? Symbol)
    {
        public uint Address => (Section?.Addr ?? 0) + Offset;
    }

    private sealed class Layout
    {
        public uint LoadAddress { get; }

        /// <summary>
        /// How far the link address runs ahead of the load address, so that
        /// a segment's p_paddr can differ from its p_vaddr. Zero for every
        /// hosted program; 0xC0000000 for a kernel linked high and loaded low.
        /// </summary>
        public uint LoadBias { get; set; }
        public OutputSection Text { get; } = new(".text", SectionKind.Code);
        public OutputSection ReadOnlyData { get; } = new(".rodata", SectionKind.ReadOnlyData);
        public OutputSection Data { get; } = new(".data", SectionKind.Data);
        public OutputSection RelocatedConstants { get; } = new(".data.rel.ro", SectionKind.Data);
        public OutputSection Bss { get; } = new(".bss", SectionKind.Uninitialised);
        public List<OutputSection> Notes { get; } = new();

        public Dictionary<string, Definition> Globals { get; } = new();
        /// <summary>Globals in definition order, so the output symbol table is deterministic.</summary>
        public List<string> GlobalOrder { get; } = new();

        /// <summary>
        /// Names the linker itself defines and a shared object does NOT
        /// export. `__data_start` and `_end` describe ONE image, and a
        /// library whose copy could be interposed by the executable's would
        /// scan the executable's statics and never its own.
        /// </summary>
        public HashSet<string> NotExported { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The read-only and read-write groups, in address order. The
        /// static link fills them with .text/.rodata and .data/.bss; a
        /// dynamic one inserts the loader's tables around those.
        /// </summary>
        public List<OutputSection> ReadOnly { get; } = new();
        public List<OutputSection> Writable { get; } = new();

        /// <summary>The dynamic-linking state, or null for a static link.</summary>
        public Dyn? Dyn { get; set; }

        public List<ProgramHeader> Segments { get; } = new();
        public uint HeaderBytes { get; set; }
        /// <summary>Where the last laid-out byte of section content is; the tables go after it.</summary>
        public uint FileEnd { get; set; }

        public Layout(uint loadAddress)
        {
            LoadAddress = loadAddress;
        }

        /// <summary>All output sections in file and address order, empty ones included.</summary>
        public IEnumerable<OutputSection> All()
        {
            foreach (OutputSection s in ReadOnly)
            {
                yield return s;
            }
            foreach (OutputSection s in Writable)
            {
                yield return s;
            }
            foreach (OutputSection n in Notes)
            {
                yield return n;
            }
        }

        /// <summary>The default arrangement: code and constants, then data and zeroes.</summary>
        public void ArrangeStatic()
        {
            ReadOnly.Add(Text);
            ReadOnly.Add(ReadOnlyData);
            Writable.Add(Data);
            Writable.Add(Bss);
        }

        public OutputSection For(Section s)
        {
            // A static link resolves these addresses before execution. A
            // dynamic link keeps loader-writable constants ahead of .data,
            // outside __data_start.._end (the conservative static roots).
            if (s.Kind == SectionKind.Data && (s.Name == ".data.rel.ro"
                || s.Name.StartsWith(".data.rel.ro.", StringComparison.Ordinal)))
                return Dyn is null ? ReadOnlyData : RelocatedConstants;
            switch (s.Kind)
            {
                case SectionKind.Code:
                    return Text;
                case SectionKind.ReadOnlyData:
                    return ReadOnlyData;
                case SectionKind.Data:
                    return Data;
                case SectionKind.Uninitialised:
                    return Bss;
                case SectionKind.Note:
                    foreach (OutputSection n in Notes)
                    {
                        if (n.Name == s.Name)
                        {
                            return n;
                        }
                    }
                    OutputSection made = new(s.Name, SectionKind.Note);
                    Notes.Add(made);
                    return made;
                default:
                    throw new ArgumentOutOfRangeException(nameof(s), s.Kind, null);
            }
        }
    }

    // ---- Phase 1: merge sections ----------------------------------------

    /// <summary>
    /// Allocated sections merge by kind, whatever they were called, so
    /// gcc's .text.startup and .rodata.str1.1 land where they belong. Notes
    /// merge by name: .corsac.meta must not be mixed with .comment.
    /// </summary>
    private static void Merge(List<Input> inputs, Layout layout, List<string> errors)
    {
        foreach (Input input in inputs)
        {
            foreach (Section s in input.Object.Sections)
            {
                if (s.Align <= 0 || (s.Align & (s.Align - 1)) != 0)
                {
                    errors.Add($"section '{s.Name}' in {input.Name} has alignment {s.Align}, which is not a power of two");
                    continue;
                }
                if ((uint)s.Align > Elf.PageSize)
                {
                    errors.Add($"section '{s.Name}' in {input.Name} wants alignment {s.Align}, more than a page");
                    continue;
                }
                OutputSection output = layout.For(s);
                uint align = (uint)s.Align;
                output.Align = Math.Max(output.Align, align);
                output.Size = Elf.AlignUp(output.Size, align);
                Placed placed = new(s, output, output.Size);
                output.Parts.Add(placed);
                input.Placed.Add(placed);
                output.Size = checked(output.Size + (uint)s.Size);
            }
        }
    }

    // ---- Phase 2: resolve symbols ---------------------------------------

    private static void Resolve(List<Input> inputs, Layout layout, List<string> errors)
    {
        foreach (Input input in inputs)
        {
            foreach (Symbol sym in input.Object.Symbols)
            {
                if (!sym.IsDefined)
                {
                    if (!sym.Global)
                    {
                        errors.Add($"local symbol '{sym.Name}' in {input.Name} is undefined");
                    }
                    // An undefined global is a request, not a definition;
                    // it matters only if a relocation actually uses it.
                    continue;
                }
                Placed? placed = null;
                foreach (Placed p in input.Placed)
                {
                    if (ReferenceEquals(p.Section, sym.Section))
                    {
                        placed = p;
                        break;
                    }
                }
                if (placed is null)
                {
                    errors.Add($"symbol '{sym.Name}' in {input.Name} is in section '{sym.Section!.Name}', which that object does not contain");
                    continue;
                }
                if (sym.Offset < 0 || sym.Offset > placed.Section.Size)
                {
                    errors.Add($"symbol '{sym.Name}' in {input.Name} is at 0x{sym.Offset:x}, outside '{placed.Section.Name}'");
                    continue;
                }
                Definition def = new(input.Name, placed.Output, checked(placed.Offset + (uint)sym.Offset), sym);
                if (!sym.Global)
                {
                    if (!input.Locals.TryAdd(sym.Name, def))
                    {
                        errors.Add($"{input.Name} defines local symbol '{sym.Name}' twice");
                    }
                    continue;
                }
                if (layout.Globals.TryGetValue(sym.Name, out Definition other))
                {
                    errors.Add($"symbol '{sym.Name}' is defined in both {other.Object} and {input.Name}");
                    continue;
                }
                layout.Globals[sym.Name] = def;
                layout.GlobalOrder.Add(sym.Name);
            }

            // Section names are implicit locals at the section's start; a
            // real symbol of the same name wins, which is why this is last.
            foreach (Placed p in input.Placed)
            {
                input.Locals.TryAdd(p.Section.Name, new Definition(input.Name, p.Output, p.Offset, null));
            }
        }
    }

    private static Definition? Lookup(Input input, Layout layout, string name)
    {
        if (input.Locals.TryGetValue(name, out Definition local))
        {
            return local;
        }
        if (layout.Globals.TryGetValue(name, out Definition global))
        {
            return global;
        }
        return null;
    }

    // ---- Phase 3: addresses ---------------------------------------------

    private static void AssignAddresses(Layout layout)
    {
        uint rwSize = 0;
        foreach (OutputSection s in layout.Writable)
        {
            rwSize += s.Size;
        }
        bool hasRw = rwSize != 0;
        // PT_LOAD for text, PT_LOAD for data if any, PT_GNU_STACK so the
        // kernel maps the stack non-executable, and the two the dynamic
        // linker needs when there is one.
        int segmentCount = 1 + (hasRw ? 1 : 0) + 1;
        if (layout.Dyn is not null)
        {
            segmentCount++;
            if (layout.Dyn.Interpreter is not null)
            {
                segmentCount++;
            }
        }
        layout.HeaderBytes = (uint)(Elf.HeaderSize + segmentCount * Elf.ProgramHeaderSize);

        // The first segment starts at file offset 0 so the headers are
        // mapped along with the code; ld does the same, and gdb expects it.
        uint off = layout.HeaderBytes;
        foreach (OutputSection s in layout.ReadOnly)
        {
            if (s.Size == 0)
            {
                continue;
            }
            off = Elf.AlignUp(off, s.Align);
            s.FileOffset = off;
            s.Addr = checked(layout.LoadAddress + off);
            off = checked(off + s.Size);
        }
        uint rxEnd = off;
        layout.Segments.Add(new ProgramHeader(Elf.PtLoad, 0, layout.LoadAddress, rxEnd, rxEnd, Elf.PfR | Elf.PfX, Elf.PageSize));

        if (hasRw)
        {
            uint align = 4;
            foreach (OutputSection s in layout.Writable)
            {
                align = Math.Max(align, s.Align);
            }
            off = Elf.AlignUp(off, align);
            uint addr = checked(Elf.AlignUp(layout.LoadAddress + rxEnd, Elf.PageSize) + off % Elf.PageSize);
            uint rwOffset = off;
            uint rwAddr = addr;
            uint fileEndOfRw = off;
            foreach (OutputSection s in layout.Writable)
            {
                if (s.Size == 0)
                {
                    continue;
                }
                addr = Elf.AlignUp(addr, s.Align);
                off = Elf.AlignUp(off, s.Align);
                s.Addr = addr;
                s.FileOffset = off;
                addr = checked(addr + s.Size);
                if (s.HasBytes)
                {
                    off = checked(off + s.Size);
                    fileEndOfRw = off;
                }
            }
            off = fileEndOfRw;
            layout.Segments.Add(new ProgramHeader(Elf.PtLoad, rwOffset, rwAddr, fileEndOfRw - rwOffset, addr - rwAddr, Elf.PfR | Elf.PfW, Elf.PageSize));
        }

        layout.Segments.Add(new ProgramHeader(Elf.PtGnuStack, 0, 0, 0, 0, Elf.PfR | Elf.PfW, 16));
        AddDynamicSegments(layout);

        foreach (OutputSection n in layout.Notes)
        {
            off = Elf.AlignUp(off, n.Align);
            n.FileOffset = off;
            off = checked(off + n.Size);
        }
        layout.FileEnd = off;
    }

    /// <summary>
    /// The symbols ld provides and the runtime relies on: the collector
    /// scans statics from __data_start to _end, the heap starts at _end.
    /// Only when no object defined them, so a program can still override.
    /// Each is absolute rather than section-relative because the section
    /// it names may be empty and therefore not emitted at all.
    /// </summary>
    /// <summary>
    /// The names of those, before any address exists. A dynamic link has to
    /// know that this object will define them before it decides what the
    /// loader must resolve, and that decision is made two phases earlier
    /// than their values are.
    /// </summary>
    private static readonly string[] LinkerSymbols =
    {
        "__text_start", "_etext", "__data_start", "_edata", "__bss_start", "_end",
    };

    /// <summary>
    /// The two a dynamic link defines for itself, reserved with the rest so
    /// that code may NAME them: the entry stub hands its own `_DYNAMIC` to
    /// the runtime, which is how a program finds the loader's link map.
    /// </summary>
    private static readonly string[] DynamicSymbols = { "_DYNAMIC", "_GLOBAL_OFFSET_TABLE_" };

    private static void ReserveLinkerSymbols(Layout layout)
    {
        foreach (string name in LinkerSymbols)
        {
            if (layout.Globals.TryAdd(name, new Definition("the linker", null, 0, null)))
            {
                layout.GlobalOrder.Add(name);
                layout.NotExported.Add(name);
            }
        }
    }

    private static void DefineLinkerSymbols(Layout layout)
    {
        uint textStart = layout.Text.Size != 0 ? layout.Text.Addr : layout.LoadAddress;
        uint textEnd = textStart + layout.Text.Size;
        // With no .data, the RW segment begins at .bss; with neither, the
        // statics range is empty and the collector scans nothing.
        uint dataStart = layout.Data.Size != 0 ? layout.Data.Addr : layout.Bss.Size != 0 ? layout.Bss.Addr : textEnd;
        uint dataEnd = layout.Data.Size != 0 ? layout.Data.Addr + layout.Data.Size : dataStart;
        uint bssStart = layout.Bss.Size != 0 ? layout.Bss.Addr : dataEnd;
        uint end = layout.Bss.Size != 0 ? layout.Bss.Addr + layout.Bss.Size : dataEnd;
        (string Name, uint Value)[] provided =
        {
            ("__text_start", textStart),
            ("_etext", textEnd),
            ("__data_start", dataStart),
            ("_edata", dataEnd),
            ("__bss_start", bssStart),
            ("_end", end),
        };
        foreach ((string name, uint value) in provided)
        {
            if (layout.Globals.TryAdd(name, new Definition("the linker", null, value, null)))
            {
                layout.GlobalOrder.Add(name);
            }
            else if (layout.NotExported.Contains(name))
            {
                // Reserved before the addresses existed; this is the value.
                layout.Globals[name] = new Definition("the linker", null, value, null);
            }
        }
    }

    // ---- Phase 4: relocations -------------------------------------------

    private static void Relocate(List<Input> inputs, Layout layout, List<string> errors)
    {
        foreach (Input input in inputs)
        {
            foreach (Placed p in input.Placed)
            {
                foreach (Relocation r in p.Section.Relocs)
                {
                    string where = $"{input.Name} ({p.Section.Name}+0x{r.Offset:x})";
                    if (p.Section.Kind == SectionKind.Uninitialised)
                    {
                        errors.Add($"relocation in a section with no contents: {where}");
                        continue;
                    }
                    if (r.Offset < 0 || r.Offset + 4 > p.Bytes.Length)
                    {
                        errors.Add($"relocation outside its section: {where}");
                        continue;
                    }
                    long value;
                    long place = p.Output.Addr + p.Offset + (uint)r.Offset;
                    if (layout.Dyn is not null)
                    {
                        if (!DynamicValue(input, layout, p, r, place, where, errors, out value))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        switch (r.Kind)
                        {
                            case RelocKind.Abs32:
                            case RelocKind.Rel32:
                            case RelocKind.Plt32:
                            {
                                Definition? d = Lookup(input, layout, r.Symbol);
                                if (d is null)
                                {
                                    errors.Add($"undefined symbol '{r.Symbol}' referenced from {where}");
                                    continue;
                                }
                                // Statically, a PLT call is a direct call: there
                                // is no other object for the PLT to reach.
                                value = d.Value.Address + r.Addend;
                                if (r.Kind != RelocKind.Abs32)
                                {
                                    value -= place;
                                }
                                break;
                            }
                            default:
                                errors.Add($"{r.Kind} relocation against '{r.Symbol}' from {where} needs a GOT, which a static link does not build");
                                continue;
                        }
                    }
                    // R_386_32 is a 32-bit addition, and it wraps like one.
                    // A kernel linked at 0xC0100000 whose entry stub wants a
                    // physical address writes `label - 0xC0000000`, which
                    // reaches the assembler as the label plus a large positive
                    // addend; the sum is over 4 GiB and comes back round to
                    // the address that was meant. The instruction holding it
                    // will do exactly this, so the linker does it too.
                    if (r.Kind == RelocKind.Abs32)
                    {
                        value = unchecked((uint)value);
                    }
                    bool fits = r.Kind == RelocKind.Abs32
                        ? value >= 0 && value <= uint.MaxValue
                        : value >= int.MinValue && value <= int.MaxValue;
                    if (!fits)
                    {
                        errors.Add($"relocation overflow: '{r.Symbol}'{(r.Addend >= 0 ? "+" : "")}{r.Addend} from {where} gives 0x{value:x}");
                        continue;
                    }
                    BinaryPrimitives.WriteUInt32LittleEndian(p.Bytes.AsSpan(r.Offset), unchecked((uint)value));
                }
            }
        }
    }

    // ---- Phase 5: emit ---------------------------------------------------

    private static byte[] Emit(List<Input> inputs, Layout layout, uint entry, ushort fileType)
    {
        List<OutputSection> present = Present(layout);

        StringTable strtab = new();
        List<SymbolEntry> symbols = BuildSymbolTable(inputs, layout, present, strtab);

        StringTable shstrtab = new();
        List<SectionHeader> headers = new() { default };
        foreach (OutputSection s in present)
        {
            (uint type, uint flags) = Elf.SectionTypeAndFlags(s.Kind);
            uint link = 0;
            if (s.LinkTo is not null)
            {
                foreach (OutputSection t in present)
                {
                    if (t.Name == s.LinkTo)
                    {
                        link = (uint)t.Index;
                    }
                }
            }
            // sh_info of .dynsym is the index of its first global, which for
            // a table with only the null symbol local is always one.
            uint info = s.Name == ".dynsym" ? 1u : 0u;
            headers.Add(new SectionHeader(shstrtab.Add(s.Name), s.TypeOverride ?? type, flags, s.Addr, s.FileOffset, s.Size, link, info, s.Align, s.EntSize));
        }
        uint symtabName = shstrtab.Add(".symtab");
        uint strtabName = shstrtab.Add(".strtab");
        uint shstrtabName = shstrtab.Add(".shstrtab");

        ElfBuffer b = new();
        Elf.WriteHeader(b, fileType, entry, Elf.HeaderSize, 0, (ushort)layout.Segments.Count, 0, 0);
        foreach (ProgramHeader ph in layout.Segments)
        {
            ph.WriteTo(b, layout.LoadBias);
        }
        foreach (OutputSection s in present)
        {
            if (!s.HasBytes)
            {
                continue;
            }
            b.PadTo(s.FileOffset);
            if (s.Content is not null)
            {
                b.Bytes(s.Content);
                continue;
            }
            foreach (Placed p in s.Parts)
            {
                b.PadTo(s.FileOffset + p.Offset);
                b.Bytes(p.Bytes);
            }
        }
        b.PadTo(layout.FileEnd);

        int firstGlobal = 0;
        for (int i = 0; i < symbols.Count; i++)
        {
            if (symbols[i].Bind == Elf.StbLocal)
            {
                firstGlobal = i + 1;
            }
        }
        {
            uint at = b.AlignTo(4);
            foreach (SymbolEntry e in symbols)
            {
                e.WriteTo(b);
            }
            headers.Add(new SectionHeader(symtabName, Elf.ShtSymTab, 0, 0, at, (uint)(symbols.Count * Elf.SymbolSize), (uint)(headers.Count + 1), (uint)firstGlobal, 4, Elf.SymbolSize));
        }
        {
            byte[] bytes = strtab.ToArray();
            headers.Add(new SectionHeader(strtabName, Elf.ShtStrTab, 0, 0, (uint)b.Length, (uint)bytes.Length, 0, 0, 1, 0));
            b.Bytes(bytes);
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

        byte[] file = b.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), shoff);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(48), (ushort)headers.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(50), (ushort)(headers.Count - 1));
        return file;
    }

    /// <summary>
    /// The sections that will be in the output, numbered as they will be.
    /// Called before the dynamic tables are filled, since a dynamic symbol
    /// names the section it is defined in, and again by the emitter.
    /// </summary>
    private static List<OutputSection> Present(Layout layout)
    {
        List<OutputSection> present = new();
        foreach (OutputSection s in layout.All())
        {
            if (s.Size != 0)
            {
                s.Index = present.Count + 1;
                present.Add(s);
            }
        }
        return present;
    }

    /// <summary>
    /// The executable's symbol table, for gdb and nm: every object's locals
    /// (a static function is still a function one wants to see in a
    /// backtrace), then the globals. Nothing here is needed to run.
    /// </summary>
    private static List<SymbolEntry> BuildSymbolTable(List<Input> inputs, Layout layout, List<OutputSection> present, StringTable strtab)
    {
        List<SymbolEntry> symbols = new() { default };
        foreach (OutputSection s in present)
        {
            symbols.Add(new SymbolEntry(0, s.Addr, 0, SymbolEntry.MakeInfo(Elf.StbLocal, Elf.SttSection), 0, (ushort)s.Index));
        }
        foreach (Input input in inputs)
        {
            foreach (Symbol sym in input.Object.Symbols)
            {
                if (sym.Global || !input.Locals.TryGetValue(sym.Name, out Definition d) || d.Symbol is null)
                {
                    continue;
                }
                symbols.Add(Entry(sym.Name, d, Elf.StbLocal, strtab));
            }
        }
        foreach (string name in layout.GlobalOrder)
        {
            symbols.Add(Entry(name, layout.Globals[name], Elf.StbGlobal, strtab));
        }
        return symbols;
    }

    private static SymbolEntry Entry(string name, Definition d, byte bind, StringTable strtab)
    {
        byte type = d.Symbol is null ? Elf.SttNoType : Elf.SymbolType(d.Symbol);
        uint size = d.Symbol is null ? 0 : (uint)Math.Clamp(d.Symbol.Size, 0, uint.MaxValue);
        // A symbol in an empty (hence unemitted) section has nowhere to point but ABS.
        ushort shndx = d.Section is null || d.Section.Index == 0 ? Elf.ShnAbs : (ushort)d.Section.Index;
        return new SymbolEntry(strtab.Add(name), d.Address, size, SymbolEntry.MakeInfo(bind, type), 0, shndx);
    }
}
