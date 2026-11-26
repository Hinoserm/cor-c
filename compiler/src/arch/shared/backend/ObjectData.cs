#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Backends;

/// <summary>
/// WHAT EVERY BACKEND DOES WITH A MODULE'S DATA AND ITS PLACED FUNCTIONS,
/// said once: each of the two x86 backends had its own copy, and i386's
/// had grown three things x86-64's never got -- reference-free statics
/// kept out of the collector's roots, constants naming another image kept
/// apart, and a placed item's bytes let go. A backend gives its sections,
/// the relocation an address is (four bytes or eight), and how it knows a
/// name is another image's.
/// </summary>
internal static class ObjectData
{
    /// <summary>
    /// Writable data that holds no reference (DataItem.NoReferences) goes in
    /// this section, which the linker places outside the statics the
    /// collector reads as roots (Linker's Numbers).
    /// </summary>
    public const string NumbersSection = ".data.corsac.numbers";

    /// <summary>Constants naming another image, which the loader writes at every start: kept together by the linker (its RelocatedImports).</summary>
    public const string ImportConstantsSection = ".data.rel.ro.import";

    /// <summary>
    /// A placed function's jump tables, each its targets' absolute
    /// addresses -- the function's label plus each target's offset (`at`)
    /// -- a word each (`word` bytes, `absolute` the relocation of one), in
    /// `tables`: read-only data, or, in a shared object whose loader
    /// writes them, the relocated constants.
    /// </summary>
    public static void JumpTables<TTarget>(ObjectFile obj, Section tables, HashSet<string> defined, string function,
        IEnumerable<(string Symbol, List<TTarget> Targets)> list, Func<TTarget, int> at, int word, RelocKind absolute)
    {
        foreach ((string symbol, List<TTarget> targets) in list)
        {
            Pad(tables, word, 0);
            obj.Symbols.Add(new Symbol { Name = symbol, Section = tables, Offset = tables.Bytes.Count, Size = targets.Count * word, Global = false });
            defined.Add(symbol);
            foreach (TTarget target in targets)
            {
                tables.Relocs.Add(new Relocation(tables.Bytes.Count, function, at(target), absolute));
                for (int z = 0; z < word; z++) tables.Bytes.Add(0);
            }
        }
    }

    public static void Pad(Section s, int align, byte fill)
    {
        while (s.Bytes.Count % align != 0) s.Bytes.Add(fill);
    }

    /// <summary>
    /// A PLACED FUNCTION'S IR LET GO, for a caller that reads nothing of the
    /// module's bodies after code generation: its blocks, and what hangs
    /// off it beside them -- its parameters and frame slots, its state
    /// machine's frame, and the index an analysis kept (Escape.StampIndex),
    /// which holds registers of the body just let go. Nothing past this
    /// reads a placed function but its name. The whole module's IR lived
    /// through code generation beside the machine code otherwise, and that
    /// was the largest a compile ever got.
    /// </summary>
    public static void Release(Function f)
    {
        f.Blocks.Clear();
        f.Blocks.TrimExcess();
        f.Params.Clear();
        f.Params.TrimExcess();
        f.Slots.Clear();
        f.Slots.TrimExcess();
        f.Async = null;
        f.AnalysisIndex = null;
    }

    /// <summary>
    /// EVERY DATA ITEM INTO ITS SECTION, with its symbol.
    ///
    /// READ-ONLY UNTIL SOMEBODY HAS TO WRITE IN IT. An item holding an
    /// address is a relocation, and a relocation in a read-only page is
    /// one the loader must make writable -- the whole page, privately, for
    /// every process. In a shared object that is every address; in an
    /// executable it is only an address a library supplies
    /// (`suppliedByAnother`), which a string literal has (the descriptor of
    /// `byte[]` is the runtime's). Such constants go in a section the
    /// loader relocates and then protects; those of them naming what this
    /// module does not define (its functions and its data, not the tables
    /// every object carries), in a shared object, in one of their own
    /// (ImportConstantsSection): immutable relocations need the loader's
    /// writes, not the collector's conservative reading as roots. Writable
    /// data with no reference in it goes outside the roots
    /// (NumbersSection). Those two sections are made, aligned to the
    /// target's `word`, only where an item goes in them.
    ///
    /// `release`: a placed item's bytes are the section's now, and were
    /// held twice over to the end of the compile -- a zero-filled one an
    /// array of zeros as large as the storage it stands for. Let go where
    /// the caller reads nothing of the module after this.
    /// </summary>
    public static void Place(Module module, ObjectFile obj, Section rodata, Section data, Section bss, Section relocatedConstants,
        int word, RelocKind address, bool positionIndependent, Func<string, bool> suppliedByAnother, HashSet<string> defined,
        List<string> errors, bool release)
    {
        HashSet<string> moduleDefines = new(StringComparer.Ordinal);
        foreach (Function f in module.Functions) moduleDefines.Add(f.Name);
        foreach (DataItem d in module.Data) moduleDefines.Add(d.Name);
        Section? imports = null, plain = null;
        Section Made(ref Section? made, string name)
        {
            if (made is null) obj.Sections.Add(made = new Section(name, SectionKind.Data) { Align = word });
            return made;
        }
        Section importConstants() => Made(ref imports, ImportConstantsSection);
        Section numbers() => Made(ref plain, NumbersSection);
        foreach (DataItem d in module.Data)
        {
            bool loaderWrites = d.Relocs.Count > 0 && (positionIndependent || d.Relocs.Any(r => suppliedByAnother(r.Symbol)));
            bool namesImport = loaderWrites && d.Relocs.Any(r => !moduleDefines.Contains(r.Symbol));
            Section s = d.Zero ? bss
                : d.ReadOnly ? (loaderWrites ? (namesImport && positionIndependent ? importConstants() : relocatedConstants) : rodata)
                : d.NoReferences ? numbers() : data;
            int align = Math.Max(d.Align, 1);
            long offset;
            if (d.Zero)
            {
                bss.ZeroBytes = (bss.ZeroBytes + align - 1) / align * align;
                offset = bss.ZeroBytes;
                bss.ZeroBytes += d.Bytes.Length;
                if (d.Relocs.Count > 0) errors.Add($"{d.Name}: a zero-filled item cannot hold addresses");
            }
            else
            {
                Pad(s, align, 0);
                offset = s.Bytes.Count;
                s.Bytes.AddRange(d.Bytes);
                foreach (DataReloc r in d.Relocs) s.Relocs.Add(new Relocation((int)offset + r.Offset, r.Symbol, r.Addend, address));
            }
            s.Align = Math.Max(s.Align, align);
            obj.Symbols.Add(new Symbol { Name = d.Name, Section = s, Offset = offset, Size = d.Bytes.Length, Global = d.Exported });
            defined.Add(d.Name);
            if (release)
            {
                d.Bytes = Array.Empty<byte>();
                d.Relocs.Clear();
                d.Relocs.TrimExcess();
            }
        }
    }

    /// <summary>
    /// Whatever is referenced and not defined here is the linker's to
    /// find: an undefined symbol for each such name, the module's imports
    /// and every relocation's, in the names' order.
    /// </summary>
    public static void DeclareUndefined(ObjectFile obj, IEnumerable<string> imports, HashSet<string> defined)
    {
        HashSet<string> undefined = new(imports, StringComparer.Ordinal);
        foreach (Section s in obj.Sections)
            foreach (Relocation r in s.Relocs) undefined.Add(r.Symbol);
        foreach (string name in undefined.OrderBy(n => n, StringComparer.Ordinal))
            if (!defined.Contains(name)) obj.Symbols.Add(new Symbol { Name = name });
    }

    /// <summary>
    /// THE FLOAT CONSTANTS' POOL: a float's or a double's bits, read-only,
    /// one copy an object, named by its width and its bits after `prefix`
    /// and local to the object. Made after the code, for every name of it
    /// the code's relocations hold, each once, eight-byte aligned.
    /// </summary>
    public static void PlaceFloatPool(ObjectFile obj, Section text, Section rodata, string prefix, HashSet<string> defined)
    {
        SortedSet<string> pool = new(StringComparer.Ordinal);
        foreach (Relocation r in text.Relocs)
            if (r.Symbol.StartsWith(prefix, StringComparison.Ordinal)) pool.Add(r.Symbol);
        foreach (string name in pool)
        {
            int width = name[prefix.Length] == '4' ? 4 : 8;
            ulong bits = Convert.ToUInt64(name[(prefix.Length + 2)..], 16);
            Pad(rodata, 8, 0);
            int at = rodata.Bytes.Count;
            for (int k = 0; k < width; k++) rodata.Bytes.Add((byte)(bits >> (8 * k)));
            rodata.Align = Math.Max(rodata.Align, 8);
            obj.Symbols.Add(new Symbol { Name = name, Section = rodata, Offset = at, Size = width, Global = false });
            defined.Add(name);
        }
    }
}
