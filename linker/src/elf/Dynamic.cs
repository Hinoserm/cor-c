#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// The dynamic half of the linker: shared objects out, and executables
/// that name a shared object and let the system loader finish the link.
///
/// The image of a shared object, which is the executable's layout with the
/// loader's tables folded into the read-only segment:
///
///   0x0000  ELF header, program headers                \
///           .interp (executables only)                 |
///           .hash, .dynsym, .dynstr                    |  PT_LOAD R X
///           .rel.dyn, .rel.plt, .plt                   |
///           .text, .rodata                             /
///   next page
///           .data                                      \
///           .got  (three reserved words, then a slot    |  PT_LOAD RW
///                  per symbol reached indirectly)       |
///           .dynamic                                   |
///           .bss                                       /
///
/// Two decisions are worth naming, because they are choices and not the
/// only possibility:
///
///   * **Binding is eager.** DT_BIND_NOW, and a PLT entry is one indirect
///     jump through its GOT slot rather than the usual push-and-jump pair
///     with a resolver stub. The loader fills every slot before the first
///     instruction runs, so the lazy path is dead code we do not emit.
///   * **Defined symbols bind to their own definition**, what GNU ld calls
///     -Bsymbolic: a call inside the library to a function the library
///     exports is a direct call, and a GOT slot for a name this object
///     defines is filled at link time. Interposing on a COR-C# library's
///     internals is not a thing we want to support, and the alternative
///     costs an indirection on every call between two library functions.
/// </summary>
public static partial class Linker
{
    /// <summary>The first three GOT words the ABI reserves: _DYNAMIC, and two for a lazy resolver we do not use.</summary>
    private const int ReservedGotWords = 3;

    /// <summary>One indirect jump and two no-ops: the PLT entry an eagerly bound object needs.</summary>
    private const int PltEntrySize = 8;

    /// <summary>
    /// Link a shared object. Undefined symbols are left for the loader, so
    /// a library may call back into its consumer; everything the objects
    /// define and export appears in .dynsym.
    /// </summary>
    public static byte[] LinkShared(IEnumerable<(string Name, ObjectFile Object)> objects, string soname, IEnumerable<string>? needed = null, string? runPath = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(soname);
        Dyn dyn = new() { Shared = true, Soname = soname };
        if (needed is not null)
        {
            // A LIBRARY NEEDS ITS OWN RUNPATH. DT_RUNPATH is used for the
            // direct dependencies of the object that carries it and for
            // nothing further -- unlike the DT_RPATH it replaced -- so an
            // executable's runpath does not help the loader find what one of
            // ITS libraries names. Every image says where its own were found.
            HashSet<string> where = new(StringComparer.Ordinal);
            foreach (string lib in needed)
            {
                dyn.Needed.Add(SoName(lib));
                string? dir = Path.GetDirectoryName(Path.GetFullPath(lib));
                if (!string.IsNullOrEmpty(dir))
                {
                    where.Add(dir);
                }
            }
            dyn.RunPath = runPath ?? (where.Count == 0 ? null : string.Join(':', where));
        }
        // A shared object is ET_DYN and loads wherever the kernel puts it,
        // so every address in it is an offset from zero.
        return LinkDynamic(objects, dyn, null, 0);
    }

    /// <summary>
    /// Link a dynamically linked executable: the objects, plus the shared
    /// libraries whose names go in DT_NEEDED and whose symbols the loader
    /// supplies. The executable's own code stays non-PIC.
    /// </summary>
    public static byte[] Link(IEnumerable<(string Name, ObjectFile Object)> objects, string entrySymbol, IEnumerable<string> sharedLibs, string? runPath = null, uint loadAddress = DefaultLoadAddress, ProgramInfo? program = null)
    {
        _program = program;
        ArgumentNullException.ThrowIfNull(sharedLibs);
        ArgumentNullException.ThrowIfNull(entrySymbol);
        Dyn dyn = new() { Shared = false, Interpreter = Elf.DefaultInterpreter };
        HashSet<string> runPaths = new(StringComparer.Ordinal);
        foreach (string lib in sharedLibs)
        {
            dyn.Needed.Add(SoName(lib));
            string? dir = Path.GetDirectoryName(Path.GetFullPath(lib));
            if (!string.IsNullOrEmpty(dir))
            {
                runPaths.Add(dir);
            }
        }
        // The libraries are in a build directory, not on the loader's search
        // path, and a program that will not run without LD_LIBRARY_PATH set
        // by hand is not a program anyone can use.
        dyn.RunPath = runPath ?? (runPaths.Count == 0 ? null : string.Join(':', runPaths));
        return LinkDynamic(objects, dyn, entrySymbol, loadAddress);
    }

    /// <summary>
    /// What a library calls itself: its DT_SONAME if it has one, else the
    /// file's name, which is what the loader will look for.
    /// </summary>
    private static string SoName(string path)
    {
        try
        {
            string? name = ElfReader.SoNameOf(File.ReadAllBytes(path));
            if (name is not null)
            {
                return name;
            }
        }
        catch (IOException)
        {
            // Not readable here is not fatal: the name on disk is still the
            // name the loader will be given, and it may exist by then.
        }
        catch (ElfFormatException)
        {
        }
        return Path.GetFileName(path);
    }

    private static byte[] LinkDynamic(IEnumerable<(string Name, ObjectFile Object)> objects, Dyn dyn, string? entrySymbol, uint loadAddress)
    {
        List<Input> inputs = new();
        foreach ((string name, ObjectFile o) in objects)
        {
            inputs.Add(new Input(name, o));
        }
        if (inputs.Count == 0)
        {
            throw new LinkException(new[] { "nothing to link" });
        }

        List<string> errors = new();
        Layout layout = new(loadAddress) { Dyn = dyn };
        TargetContract.Validate(inputs.Select(input => (input.Name, input.Object)));
        ManagedLayoutContract.Validate(inputs.Select(input => (input.Name, input.Object)));
        Corsac.Lang.Lto.DefinitionCoalescer.Run(inputs.Select(input => (input.Name, input.Object)).ToArray());
        AddManagedMetadata(inputs, layout, errors);
        Merge(inputs, layout, errors);
        Resolve(inputs, layout, errors);
        ResolveManagedMetadata(layout, errors);
        // Before the scan, because the scan decides what the LOADER has to
        // find and these are names this link defines itself -- even though
        // what they are worth is not known until the sections have addresses.
        ReserveLinkerSymbols(layout);
        foreach (string name in DynamicSymbols)
        {
            if (layout.Globals.TryAdd(name, new Definition("the linker", null, 0, null)))
            {
                layout.GlobalOrder.Add(name);
                layout.NotExported.Add(name);
            }
        }
        ScanDynamic(inputs, layout, dyn, errors);
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }
        Arrange(layout, dyn);
        AssignAddresses(layout);
        DefineLinkerSymbols(layout);
        DefineDynamicSymbols(layout, dyn);
        Relocate(inputs, layout, errors);
        FillDynamic(layout, dyn, errors);

        uint entry = 0;
        if (entrySymbol is not null)
        {
            if (!layout.Globals.TryGetValue(entrySymbol, out Definition e))
            {
                errors.Add($"entry symbol '{entrySymbol}' is not defined by any object");
            }
            else
            {
                entry = e.Address;
            }
        }
        if (errors.Count != 0)
        {
            throw new LinkException(errors);
        }
        return Emit(inputs, layout, entry, dyn.Shared ? Elf.TypeDyn : Elf.TypeExec);
    }

    // ---- the state a dynamic link carries -------------------------------

    private sealed class Dyn
    {
        public bool Shared { get; init; }
        public string? Soname { get; init; }
        public string? Interpreter { get; init; }
        public string? RunPath { get; set; }
        public List<string> Needed { get; } = new();

        public OutputSection Interp { get; } = new(".interp", SectionKind.ReadOnlyData) { Align = 1 };
        public OutputSection Hash { get; } = new(".hash", SectionKind.ReadOnlyData) { Align = 4, EntSize = 4, TypeOverride = Elf.ShtHash, LinkTo = ".dynsym" };
        public OutputSection DynSym { get; } = new(".dynsym", SectionKind.ReadOnlyData) { Align = 4, EntSize = (uint)Elf.SymbolSize, TypeOverride = Elf.ShtDynSym, LinkTo = ".dynstr" };
        public OutputSection DynStr { get; } = new(".dynstr", SectionKind.ReadOnlyData) { Align = 1, TypeOverride = Elf.ShtStrTab };
        public OutputSection RelDyn { get; } = new(".rel.dyn", SectionKind.ReadOnlyData) { Align = 4, EntSize = (uint)Elf.RelSize, TypeOverride = Elf.ShtRel, LinkTo = ".dynsym" };
        public OutputSection RelPlt { get; } = new(".rel.plt", SectionKind.ReadOnlyData) { Align = 4, EntSize = (uint)Elf.RelSize, TypeOverride = Elf.ShtRel, LinkTo = ".dynsym" };
        public OutputSection Plt { get; } = new(".plt", SectionKind.Code) { Align = 4 };
        public OutputSection Got { get; } = new(".got", SectionKind.Data) { Align = 4, EntSize = 4 };
        public OutputSection Dynamic { get; } = new(".dynamic", SectionKind.Data) { Align = 4, EntSize = 8, TypeOverride = Elf.ShtDynamic, LinkTo = ".dynstr" };

        /// <summary>Dynamic symbols in table order; index 0 is the null entry.</summary>
        public List<DynSymbol> Symbols { get; } = new() { new DynSymbol("", null) };
        public Dictionary<string, int> SymbolIndex { get; } = new(StringComparer.Ordinal);

        /// <summary>Symbols with a GOT slot, in slot order after the reserved words.</summary>
        public List<string> GotOrder { get; } = new();
        public Dictionary<string, int> GotSlot { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// What each slot's symbol resolved to HERE, or absent when only the
        /// loader can say. Kept from the scan rather than looked up again
        /// when the slots are filled, because a name may be LOCAL to one of
        /// the objects -- the frame table, a jump table -- and a local
        /// definition is in no global table to find it in.
        /// </summary>
        public Dictionary<string, Definition> GotDefinition { get; } = new(StringComparer.Ordinal);

        /// <summary>Symbols with a PLT entry, in entry order.</summary>
        public List<string> PltOrder { get; } = new();
        public Dictionary<string, int> PltEntry { get; } = new(StringComparer.Ordinal);

        /// <summary>Relocations the loader performs, recorded before their addresses are known.</summary>
        public List<DynReloc> Relocations { get; } = new();

        /// <summary>Set when a relocation lands in a section the loader will have to make writable.</summary>
        public bool TextRel { get; set; }

        public bool Imports(string name) => SymbolIndex.TryGetValue(name, out int i) && Symbols[i].Definition is null;
    }

    /// <summary>A symbol in .dynsym: exported when it has a definition, imported when it does not.</summary>
    private sealed record DynSymbol(string Name, Definition? Definition)
    {
        public Symbol? Symbol => Definition?.Symbol;
    }

    /// <summary>
    /// A relocation for the loader. The place is a section and an offset
    /// rather than an address because these are decided before addresses
    /// are, and the GOT ones name a slot rather than a place at all.
    /// </summary>
    private sealed record DynReloc(OutputSection Section, uint Offset, string Symbol, RelocKind Kind, bool InPlt);

    // ---- what needs a GOT slot, a PLT entry, or the loader's help -------

    private static void ScanDynamic(List<Input> inputs, Layout layout, Dyn dyn, List<string> errors)
    {
        // Everything defined and global is exported from a library; an
        // executable exports nothing, so the loader only has to find what it
        // imports.
        if (dyn.Shared)
        {
            foreach (string name in layout.GlobalOrder)
            {
                if (layout.NotExported.Contains(name))
                {
                    continue;
                }
                AddDynSymbol(dyn, name, layout.Globals[name]);
            }
        }

        foreach (Input input in inputs)
        {
            foreach (Placed p in input.Placed)
            {
                foreach (Relocation r in p.Section.Relocs)
                {
                    Definition? d = Lookup(input, layout, r.Symbol);
                    if (d is null)
                    {
                        // Unresolved here means the loader must resolve it.
                        AddDynSymbol(dyn, r.Symbol, null);
                    }
                    string where = $"{input.Name} ({p.Section.Name}+0x{r.Offset:x})";
                    switch (r.Kind)
                    {
                        case RelocKind.Abs32:
                            if (d is null)
                            {
                                NeedDynReloc(dyn, p, r, RelocKind.Abs32);
                            }
                            else if (dyn.Shared)
                            {
                                // The address is right for a load at zero and
                                // wrong everywhere else, so the loader adds the base.
                                NeedDynReloc(dyn, p, r, RelocKind.Relative);
                            }
                            break;
                        case RelocKind.Rel32:
                        case RelocKind.Plt32:
                            if (d is null)
                            {
                                NeedPlt(dyn, r.Symbol);
                            }
                            break;
                        case RelocKind.Got32:
                        case RelocKind.GotAddr:
                            NeedGot(dyn, r.Symbol, d);
                            break;
                        case RelocKind.GotOff:
                            if (d is null)
                            {
                                errors.Add($"'{r.Symbol}' is reached from {where} as if this object defined it, and it does not");
                            }
                            break;
                        case RelocKind.GotPc:
                            break;
                        default:
                            errors.Add($"{r.Kind} relocation against '{r.Symbol}' from {where} is not one this linker produces");
                            break;
                    }
                }
            }
        }
    }

    private static void AddDynSymbol(Dyn dyn, string name, Definition? definition)
    {
        if (name.Length == 0 || name == "_GLOBAL_OFFSET_TABLE_")
        {
            return;
        }
        if (dyn.SymbolIndex.ContainsKey(name))
        {
            return;
        }
        dyn.SymbolIndex[name] = dyn.Symbols.Count;
        dyn.Symbols.Add(new DynSymbol(name, definition));
    }

    private static void NeedGot(Dyn dyn, string name, Definition? d)
    {
        if (dyn.GotSlot.ContainsKey(name))
        {
            return;
        }
        dyn.GotSlot[name] = dyn.GotOrder.Count;
        dyn.GotOrder.Add(name);
        if (d is not null)
        {
            dyn.GotDefinition[name] = d.Value;
        }
        if (d is null)
        {
            // Filled by the loader from the symbol table.
            dyn.Relocations.Add(new DynReloc(dyn.Got, 0, name, RelocKind.GlobData, false));
        }
        else if (dyn.Shared)
        {
            // Bound here to this object's own definition; the loader only
            // has to add the base it chose.
            dyn.Relocations.Add(new DynReloc(dyn.Got, 0, name, RelocKind.Relative, false));
        }
    }

    private static void NeedPlt(Dyn dyn, string name)
    {
        if (dyn.PltEntry.ContainsKey(name))
        {
            return;
        }
        dyn.PltEntry[name] = dyn.PltOrder.Count;
        dyn.PltOrder.Add(name);
        // A PLT entry jumps through a GOT slot the loader fills, which is a
        // JUMP_SLOT relocation and not the GLOB_DAT a plain GOT slot gets.
        if (!dyn.GotSlot.ContainsKey(name))
        {
            dyn.GotSlot[name] = dyn.GotOrder.Count;
            dyn.GotOrder.Add(name);
        }
        dyn.Relocations.RemoveAll(x => x.Symbol == name && x.Section == dyn.Got && !x.InPlt);
        dyn.Relocations.Add(new DynReloc(dyn.Got, 0, name, RelocKind.JumpSlot, true));
    }

    private static void NeedDynReloc(Dyn dyn, Placed p, Relocation r, RelocKind kind)
    {
        if (p.Output.Kind is SectionKind.Code or SectionKind.ReadOnlyData)
        {
            // The loader can still do it, with DT_TEXTREL, at the cost of
            // making the page writable and private. The code generator
            // avoids ever asking in position-independent mode.
            dyn.TextRel = true;
        }
        dyn.Relocations.Add(new DynReloc(p.Output, checked(p.Offset + (uint)r.Offset), r.Symbol, kind, false));
    }

    // ---- sizes, then addresses ------------------------------------------

    private static void Arrange(Layout layout, Dyn dyn)
    {
        int syms = dyn.Symbols.Count;
        StringTable str = new();
        foreach (DynSymbol s in dyn.Symbols)
        {
            str.Add(s.Name);
        }
        foreach (string n in dyn.Needed)
        {
            str.Add(n);
        }
        if (dyn.Soname is not null)
        {
            str.Add(dyn.Soname);
        }
        if (dyn.RunPath is not null)
        {
            str.Add(dyn.RunPath);
        }
        dyn.DynStr.Content = str.ToArray();
        dyn.DynStr.Size = (uint)dyn.DynStr.Content.Length;

        dyn.DynSym.Size = (uint)(syms * Elf.SymbolSize);
        int buckets = Math.Max(1, syms | 1);
        dyn.Hash.Size = (uint)((2 + buckets + syms) * 4);

        int plt = 0;
        int rel = 0;
        foreach (DynReloc r in dyn.Relocations)
        {
            if (r.InPlt)
            {
                plt++;
            }
            else
            {
                rel++;
            }
        }
        dyn.RelPlt.Size = (uint)(plt * Elf.RelSize);
        dyn.RelDyn.Size = (uint)(rel * Elf.RelSize);
        dyn.Plt.Size = (uint)(dyn.PltOrder.Count * PltEntrySize);
        dyn.Got.Size = (uint)((ReservedGotWords + dyn.GotOrder.Count) * 4);
        dyn.Dynamic.Size = (uint)(DynamicEntries(layout, dyn).Count * 8);

        if (dyn.Interpreter is not null)
        {
            dyn.Interp.Content = System.Text.Encoding.ASCII.GetBytes(dyn.Interpreter + "\0");
            dyn.Interp.Size = (uint)dyn.Interp.Content.Length;
            layout.ReadOnly.Add(dyn.Interp);
        }
        layout.ReadOnly.Add(dyn.Hash);
        layout.ReadOnly.Add(dyn.DynSym);
        layout.ReadOnly.Add(dyn.DynStr);
        layout.ReadOnly.Add(dyn.RelDyn);
        layout.ReadOnly.Add(dyn.RelPlt);
        layout.ReadOnly.Add(dyn.Plt);
        layout.ReadOnly.Add(layout.Text);
        layout.ReadOnly.Add(layout.ReadOnlyData);
        layout.Writable.Add(layout.RelocatedConstants);
        layout.Writable.Add(layout.Data);
        layout.Writable.Add(dyn.Got);
        layout.Writable.Add(dyn.Dynamic);
        layout.Writable.Add(layout.Bss);
    }

    /// <summary>PT_INTERP and PT_DYNAMIC, once the sections they point at have addresses.</summary>
    private static void AddDynamicSegments(Layout layout)
    {
        Dyn? dyn = layout.Dyn;
        if (dyn is null)
        {
            return;
        }
        if (dyn.Interpreter is not null)
        {
            layout.Segments.Add(new ProgramHeader(Elf.PtInterp, dyn.Interp.FileOffset, dyn.Interp.Addr, dyn.Interp.Size, dyn.Interp.Size, Elf.PfR, 1));
        }
        layout.Segments.Add(new ProgramHeader(Elf.PtDynamic, dyn.Dynamic.FileOffset, dyn.Dynamic.Addr, dyn.Dynamic.Size, dyn.Dynamic.Size, Elf.PfR | Elf.PfW, 4));
    }

    /// <summary>The two names a dynamic image defines for itself, needed before any relocation is applied.</summary>
    private static void DefineDynamicSymbols(Layout layout, Dyn dyn)
    {
        foreach ((string name, OutputSection section) in new[] { ("_GLOBAL_OFFSET_TABLE_", dyn.Got), ("_DYNAMIC", dyn.Dynamic) })
        {
            if (layout.Globals.TryAdd(name, new Definition("the linker", section, 0, null)))
            {
                layout.GlobalOrder.Add(name);
            }
            else if (layout.NotExported.Contains(name))
            {
                // Reserved before the sections had addresses; this is where it is.
                layout.Globals[name] = new Definition("the linker", section, 0, null);
            }
        }
    }

    // ---- relocation, with somewhere to put what cannot be done here ----

    private static bool DynamicValue(Input input, Layout layout, Placed p, Relocation r, long place, string where, List<string> errors, out long value)
    {
        Dyn dyn = layout.Dyn!;
        value = 0;
        Definition? d = Lookup(input, layout, r.Symbol);
        uint gotBase = dyn.Got.Addr;
        switch (r.Kind)
        {
            case RelocKind.Abs32:
                // An imported symbol's address is not known until load, and
                // the loader adds it to whatever is in the word.
                value = d is null ? r.Addend : d.Value.Address + r.Addend;
                return true;
            case RelocKind.Rel32:
            case RelocKind.Plt32:
                if (d is not null)
                {
                    value = d.Value.Address + r.Addend - place;
                    return true;
                }
                value = PltAddress(dyn, r.Symbol) + r.Addend - place;
                return true;
            case RelocKind.Got32:
                // The slot's offset from the GOT base: what GOT-relative code adds.
                value = (ReservedGotWords + dyn.GotSlot[r.Symbol]) * 4 + r.Addend;
                return true;
            case RelocKind.GotAddr:
                // The slot itself, at the address this link gave it.
                value = GotAddress(dyn, r.Symbol) + r.Addend;
                return true;
            case RelocKind.GotOff:
                if (d is null)
                {
                    errors.Add($"undefined symbol '{r.Symbol}' referenced from {where}");
                    return false;
                }
                value = d.Value.Address + r.Addend - gotBase;
                return true;
            case RelocKind.GotPc:
                value = gotBase + r.Addend - place;
                return true;
            default:
                errors.Add($"{r.Kind} relocation against '{r.Symbol}' from {where} is not one this linker produces");
                return false;
        }
    }

    private static uint PltAddress(Dyn dyn, string symbol)
    {
        return dyn.Plt.Addr + (uint)(dyn.PltEntry[symbol] * PltEntrySize);
    }

    // ---- the tables themselves ------------------------------------------

    private static void FillDynamic(Layout layout, Dyn dyn, List<string> errors)
    {
        Present(layout);

        StringTable str = new();
        List<SymbolEntry> entries = new();
        foreach (DynSymbol s in dyn.Symbols)
        {
            if (s.Name.Length == 0)
            {
                entries.Add(default);
                continue;
            }
            Definition? d = s.Definition;
            byte type = d?.Symbol is null ? Elf.SttNoType : Elf.SymbolType(d.Value.Symbol!);
            uint size = d?.Symbol is null ? 0 : (uint)Math.Clamp(d.Value.Symbol!.Size, 0, uint.MaxValue);
            ushort shndx = d is null || d.Value.Section is null || d.Value.Section.Index == 0 ? Elf.ShnUndef : (ushort)d.Value.Section.Index;
            entries.Add(new SymbolEntry(str.Add(s.Name), d?.Address ?? 0, size, SymbolEntry.MakeInfo(Elf.StbGlobal, type), 0, shndx));
        }

        ElfBuffer sym = new();
        foreach (SymbolEntry e in entries)
        {
            e.WriteTo(sym);
        }
        dyn.DynSym.Content = sym.ToArray();

        dyn.Hash.Content = Hash(dyn);

        // The GOT: the reserved words, then one slot per symbol, holding
        // this object's own definition where there is one and zero where
        // the loader will write.
        ElfBuffer got = new();
        got.U32(dyn.Dynamic.Addr);
        got.U32(0);
        got.U32(0);
        foreach (string name in dyn.GotOrder)
        {
            // The global table first, because the linker's own symbols were
            // RESERVED before the sections had addresses and are only worth
            // anything there; then what the scan resolved, which is the only
            // place a name local to an object can be found.
            if (!layout.Globals.TryGetValue(name, out Definition d) && !dyn.GotDefinition.TryGetValue(name, out d))
            {
                got.U32(0);
                continue;
            }
            got.U32(!dyn.Imports(name) && !dyn.PltEntry.ContainsKey(name) ? d.Address : 0);
        }
        dyn.Got.Content = got.ToArray();

        ElfBuffer plt = new();
        foreach (string name in dyn.PltOrder)
        {
            uint slot = GotAddress(dyn, name);
            if (dyn.Shared)
            {
                // jmp *disp32(%ebx): the caller's EBX is this object's GOT,
                // which is the whole reason PIC reserves it.
                plt.U8(0xFF);
                plt.U8(0xA3);
                plt.U32(slot - dyn.Got.Addr);
            }
            else
            {
                // jmp *disp32: an executable knows where its own GOT is.
                plt.U8(0xFF);
                plt.U8(0x25);
                plt.U32(slot);
            }
            plt.U8(0x90);
            plt.U8(0x90);
        }
        dyn.Plt.Content = plt.ToArray();

        ElfBuffer relDyn = new();
        ElfBuffer relPlt = new();
        foreach (DynReloc r in dyn.Relocations)
        {
            uint at = r.Section == dyn.Got ? GotAddress(dyn, r.Symbol) : r.Section.Addr + r.Offset;
            uint index = r.Kind == RelocKind.Relative ? 0 : (uint)dyn.SymbolIndex.GetValueOrDefault(r.Symbol, 0);
            if (index == 0 && r.Kind != RelocKind.Relative)
            {
                errors.Add($"'{r.Symbol}' needs a dynamic relocation and is not in the dynamic symbol table");
                continue;
            }
            ElfBuffer into = r.InPlt ? relPlt : relDyn;
            into.U32(at);
            into.U32((index << 8) | Elf.RelocType(r.Kind));
        }
        dyn.RelDyn.Content = relDyn.ToArray();
        dyn.RelPlt.Content = relPlt.ToArray();

        ElfBuffer dynamic = new();
        foreach ((int tag, uint value) in DynamicEntries(layout, dyn))
        {
            dynamic.U32(unchecked((uint)tag));
            dynamic.U32(value);
        }
        dyn.Dynamic.Content = dynamic.ToArray();

        // Every one of these was sized before addresses were assigned; a
        // disagreement now is a layout bug and not something to paper over.
        foreach (OutputSection s in new[] { dyn.DynSym, dyn.DynStr, dyn.Hash, dyn.Got, dyn.Plt, dyn.RelDyn, dyn.RelPlt, dyn.Dynamic })
        {
            if (s.Content is not null && s.Content.Length != s.Size)
            {
                errors.Add($"{s.Name}: reserved {s.Size} bytes and produced {s.Content.Length}");
            }
        }
    }

    private static uint GotAddress(Dyn dyn, string symbol)
    {
        return dyn.Got.Addr + (uint)((ReservedGotWords + dyn.GotSlot[symbol]) * 4);
    }

    /// <summary>The SysV hash table: buckets into chains, as the loader walks them.</summary>
    private static byte[] Hash(Dyn dyn)
    {
        int n = dyn.Symbols.Count;
        int buckets = Math.Max(1, n | 1);
        uint[] bucket = new uint[buckets];
        uint[] chain = new uint[n];
        for (int i = n - 1; i >= 1; i--)
        {
            int b = (int)(Elf.HashName(dyn.Symbols[i].Name) % (uint)buckets);
            chain[i] = bucket[b];
            bucket[b] = (uint)i;
        }
        ElfBuffer b2 = new();
        b2.U32((uint)buckets);
        b2.U32((uint)n);
        foreach (uint v in bucket)
        {
            b2.U32(v);
        }
        foreach (uint v in chain)
        {
            b2.U32(v);
        }
        return b2.ToArray();
    }

    /// <summary>
    /// The dynamic array. Called twice: once to count the entries before
    /// addresses exist, once to write them, which is why it reads addresses
    /// that are zero the first time and never depends on them for its length.
    /// </summary>
    private static List<(int Tag, uint Value)> DynamicEntries(Layout layout, Dyn dyn)
    {
        StringTable str = new();
        foreach (DynSymbol s in dyn.Symbols)
        {
            str.Add(s.Name);
        }

        List<(int, uint)> d = new();
        foreach (string n in dyn.Needed)
        {
            d.Add((Elf.DtNeeded, str.Add(n)));
        }
        if (dyn.Soname is not null)
        {
            d.Add((Elf.DtSoName, str.Add(dyn.Soname)));
        }
        if (dyn.RunPath is not null)
        {
            d.Add((Elf.DtRunPath, str.Add(dyn.RunPath)));
        }
        // WHAT THE LOADER CALLS BEFORE ANYTHING USES THIS LIBRARY. The
        // compiler puts one function of its own in every shared object (see
        // Lowering.EmitSharedInit): it hands this image's statics to the
        // collector and its frame table to the stack walker, neither of
        // which the runtime could find from the other side of the boundary.
        if (dyn.Shared && layout.Globals.TryGetValue(Elf.SharedInitName, out Definition init))
        {
            d.Add((Elf.DtInit, init.Address));
        }
        d.Add((Elf.DtHash, dyn.Hash.Addr));
        d.Add((Elf.DtStrTab, dyn.DynStr.Addr));
        d.Add((Elf.DtSymTab, dyn.DynSym.Addr));
        d.Add((Elf.DtStrSz, dyn.DynStr.Size));
        d.Add((Elf.DtSymEnt, (uint)Elf.SymbolSize));
        if (dyn.RelDyn.Size != 0)
        {
            d.Add((Elf.DtRel, dyn.RelDyn.Addr));
            d.Add((Elf.DtRelSz, dyn.RelDyn.Size));
            d.Add((Elf.DtRelEnt, (uint)Elf.RelSize));
        }
        if (dyn.RelPlt.Size != 0)
        {
            d.Add((Elf.DtPltGot, dyn.Got.Addr));
            d.Add((Elf.DtPltRelSz, dyn.RelPlt.Size));
            d.Add((Elf.DtPltRel, Elf.DtRel));
            d.Add((Elf.DtJmpRel, dyn.RelPlt.Addr));
        }
        if (dyn.TextRel)
        {
            d.Add((Elf.DtTextRel, 0));
        }
        if (!dyn.Shared)
        {
            // WHERE THE LOADER PUTS THE LINK MAP. The loader writes the
            // address of its r_debug here, and the program's startup walks
            // the chain to run each library's initialiser -- which is what a
            // loader living in a kernel cannot do for it, since it cannot
            // call into the program's own privilege level. It is also where
            // a debugger looks, so nothing bespoke is invented.
            d.Add((Elf.DtDebug, 0));
        }
        // Eager binding, and this object's own definitions win: see the
        // class comment for why both.
        d.Add((Elf.DtBindNow, 0));
        uint flags = Elf.DfBindNow | Elf.DfSymbolic | (dyn.TextRel ? Elf.DfTextRel : 0);
        d.Add((Elf.DtFlags, flags));
        d.Add((Elf.DtFlags1, Elf.Df1Now));
        d.Add((Elf.DtNull, 0));
        return d;
    }
}
