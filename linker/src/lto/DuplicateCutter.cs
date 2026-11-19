using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// THE BYTES OF A DEFINITION ANOTHER OBJECT ALSO GIVES, TAKEN OUT. Every
/// unit compiled on its own carries the generic bodies it uses, so a list's
/// Add is in forty objects and the image kept thirty-nine dead copies, their
/// stack maps and their frame entries with them: two thirds of a megabyte of
/// the GUI kernel's code. Nothing reaches a copy except through its name --
/// every call and every address of code is a relocation, and no branch leaves
/// its function unrelocated -- so its range is cut out of its section and
/// what came after it moves down: symbols, relocations, the stack-map entries
/// and the frame table's, both rebased on the start of the code.
///
/// A copy is cut only where that is plainly safe: it ends where the next
/// symbol begins, no other symbol sits inside it, and what is taken is a
/// whole number of the section's alignment, so that all after it stays as
/// aligned as it was. Any other is left where it is, as before.
///
/// IN CODE, COPIES THAT LIE END TO START ARE ONE RUN, cut together: a
/// function taking its arguments in registers is two symbols, its stub and
/// the body the stub runs on into (the x86 backend's RegisterArgs), and the
/// stub begins wherever the body's alignment put it -- neither is a whole
/// number of the alignment alone, nor always the two together. Of a run the
/// most whole alignments are taken, from its end; the few bytes left at its
/// start are dead (their symbols, relocations and table entries go with
/// the rest) and are written over with no-ops, the gap before whatever
/// follows.
/// </summary>
internal static class DuplicateCutter
{
    const string CodeBase = "__corsac_code_base";
    const string FramesBase = "__corsac_frames_base";
    const uint FrameMagic = FrameTableFormat.Local;     // 'CFR5'
    const string FrameSymbol = "__corsac_frames";
    const string StackMapStart = "__corsac_stackmaps";
    const string StackMapEnd = "__corsac_stackmaps_end";

    /// <summary>Cuts what it can of <paramref name="losers"/> and returns the rest.</summary>
    public static List<Symbol> Cut(ObjectFile obj, List<Symbol> losers)
    {
        List<Symbol> kept = new();
        Dictionary<Section, List<(long Start, long End)>> cuts = new();
        // What of a run of code is not taken out (the bytes at its start
        // short of a whole alignment), by section: dead as the cut is.
        Dictionary<Section, List<(long Start, long End)>> dead = new();
        HashSet<Symbol> cutSymbols = new(ReferenceEqualityComparer.Instance);
        // EACH SECTION'S SYMBOLS BY OFFSET, sorted once: every loser walked
        // the object's whole symbol table for the one that follows it --
        // losers times symbols, and a unit of the compiler's own build loses
        // thousands of generic copies out of tens of thousands of symbols.
        // What the walk found is the same: another symbol where the loser
        // starts makes it an alias, else the first one after it ends it.
        Dictionary<Section, (long[] Offsets, Symbol[] Symbols)> placed = new();
        Dictionary<Section, List<(long Start, long End, Symbol Loser)>> runs = new();
        foreach (Symbol loser in losers)
        {
            Section? section = loser.Section;
            if (section is null || section.Kind == SectionKind.Uninitialised || loser.Size <= 0) { kept.Add(loser); continue; }
            long start = loser.Offset;
            long end = section.Length;
            bool alias = false;
            if (!placed.TryGetValue(section, out var sorted)) placed.Add(section, sorted = Placed(obj, section));
            int at = Array.BinarySearch(sorted.Offsets, start);
            if (at < 0) at = ~at;
            else while (at > 0 && sorted.Offsets[at - 1] == start) at--;
            for (; at < sorted.Offsets.Length && sorted.Offsets[at] == start; at++)
                if (!ReferenceEquals(sorted.Symbols[at], loser)) { alias = true; break; }
            if (!alias && at < sorted.Offsets.Length && sorted.Offsets[at] < end) end = sorted.Offsets[at];
            if (alias || end < start + loser.Size) { kept.Add(loser); continue; }
            if (section.Kind == SectionKind.Code)
            {
                // Code: judged with its neighbours, below.
                if (!runs.TryGetValue(section, out var run)) runs.Add(section, run = new());
                run.Add((start, end, loser));
                continue;
            }
            if ((end - start) % section.Align != 0) { kept.Add(loser); continue; }
            if (!cuts.TryGetValue(section, out var list)) cuts.Add(section, list = new());
            list.Add((start, end));
            cutSymbols.Add(loser);
        }
        // THE RUNS OF CODE: copies in the order they lie, each that begins
        // where the one before it ends joined to it. (No two begin at one
        // place: such a copy is an alias, kept above.)
        foreach ((Section section, List<(long Start, long End, Symbol Loser)> run) in runs)
        {
            run.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int first = 0; first < run.Count;)
            {
                int next = first + 1;
                while (next < run.Count && run[next].Start == run[next - 1].End) next++;
                long start = run[first].Start, end = run[next - 1].End;
                long whole = (end - start) / section.Align * section.Align;
                if (whole == 0)
                {
                    for (int i = first; i < next; i++) kept.Add(run[i].Loser);
                }
                else
                {
                    if (!cuts.TryGetValue(section, out var list)) cuts.Add(section, list = new());
                    list.Add((end - whole, end));
                    if (end - whole > start)
                    {
                        if (!dead.TryGetValue(section, out var left)) dead.Add(section, left = new());
                        left.Add((start, end - whole));
                    }
                    for (int i = first; i < next; i++) cutSymbols.Add(run[i].Loser);
                }
                first = next;
            }
        }
        if (cuts.Count == 0) return kept;

        // The code's own tables, read before anything moves.
        Section? code = cuts.Keys.FirstOrDefault(s => s.Kind == SectionKind.Code);
        StackMaps? maps = null;
        Frames? frames = null;
        if (code is not null)
        {
            maps = StackMaps.Read(obj, code);
            frames = Frames.Read(obj, code);
            if (maps is { Unknown: true } || frames is { Unknown: true })
            {
                // A table this does not know how to rewrite: leave the code.
                foreach (Symbol s in cutSymbols.Where(s => s.Section == code).ToList()) { kept.Add(s); cutSymbols.Remove(s); }
                cuts.Remove(code);
                dead.Remove(code);
                code = null;
                maps = null;
                frames = null;
            }
        }
        foreach (var list in cuts.Values) list.Sort();
        foreach (var list in dead.Values) list.Sort();
        List<(long Start, long End)> none = new();

        // THE FRAME TABLE REBUILT FIRST, before anything is written. With
        // fewer functions it is nearly always smaller, but its deltas and its
        // shared line programs are all chosen afresh, and a table that would
        // come out longer leaves the code uncut rather than move what follows.
        byte[]? frameBytes = null;
        long framesBase = 0;
        if (code is not null && frames is not null)
        {
            frameBytes = frames.Build(cuts[code], dead.GetValueOrDefault(code, none), out framesBase);
            if (frameBytes.Length > frames.Symbol.Size)
            {
                foreach (Symbol s in cutSymbols.Where(s => s.Section == code).ToList()) { kept.Add(s); cutSymbols.Remove(s); }
                cuts.Remove(code);
                dead.Remove(code);
                code = null;
                maps = null;
                frames = null;
                frameBytes = null;
            }
        }
        if (code is not null)
        {
            List<(long Start, long End)> codeCuts = cuts[code];
            if (maps is not null)
            {
                maps.Write(codeCuts, dead.GetValueOrDefault(code, none));
                // The table's bounds follow it.
                for (int i = 0; i < obj.Symbols.Count; i++)
                {
                    Symbol b = obj.Symbols[i];
                    if (b.Section != maps.Section) continue;
                    if (b.Name == StackMapStart) obj.Symbols[i] = new Symbol { Name = b.Name, Section = b.Section, Offset = 0, Size = maps.Section.Bytes.Count, Global = b.Global };
                    else if (b.Name == StackMapEnd) obj.Symbols[i] = new Symbol { Name = b.Name, Section = b.Section, Offset = maps.Section.Bytes.Count, Size = 0, Global = b.Global };
                }
            }
            if (frames is not null)
            {
                // The table shrinks; the rodata after it moves down by whole
                // alignment units, and what does not divide is left as zeros.
                byte[] bytes = frameBytes!;
                Section at = frames.Section;
                int old = (int)frames.Symbol.Size;
                int spare = (old - bytes.Length) / at.Align * at.Align;
                for (int i = 0; i < old; i++) at.Bytes[(int)frames.Symbol.Offset + i] = i < bytes.Length ? bytes[i] : (byte)0;
                if (spare > 0)
                {
                    if (!cuts.TryGetValue(at, out var list)) cuts.Add(at, list = new());
                    list.Add((frames.Symbol.Offset + old - spare, frames.Symbol.Offset + old));
                    list.Sort();
                }
                int index = obj.Symbols.IndexOf(frames.Symbol);
                obj.Symbols[index] = new Symbol
                {
                    Name = frames.Symbol.Name, Section = at, Offset = frames.Symbol.Offset, Size = old - spare,
                    IsFunction = false, Global = frames.Symbol.Global,
                };
                ReplaceReloc(at, (int)frames.Symbol.Offset + 8, FramesBase);
            }
            if (maps is not null) ReplaceReloc(maps.Section, StackMapTable.BaseOffset, CodeBase);
        }

        // What is left of each run first: its relocations go (one may reach
        // from it into the bytes the cut takes), its bytes become no-ops.
        foreach ((Section section, List<(long Start, long End)> list) in dead)
        {
            List<Relocation> stay = new(section.Relocs.Count);
            foreach (Relocation r in section.Relocs) if (!Within(list, r.Offset)) stay.Add(r);
            section.Relocs.Clear();
            section.Relocs.AddRange(stay);
            foreach ((long start, long end) in list)
                for (long i = start; i < end; i++) section.Bytes[(int)i] = 0x90;
        }
        foreach ((Section section, List<(long Start, long End)> list) in cuts) Splice(obj, section, list, cutSymbols);
        // The tables' new bases, placed once the code has moved: the stack
        // maps count from the code's first byte, the frame table from its
        // first function still present, so neither can grow.
        if (maps is not null) obj.Symbols.Add(new Symbol { Name = CodeBase, Section = code, Offset = 0, Size = 0, Global = false });
        if (frames is not null) obj.Symbols.Add(new Symbol { Name = FramesBase, Section = code, Offset = framesBase, Size = 0, Global = false });
        foreach (Symbol loser in cutSymbols)
        {
            obj.Symbols.Add(new Symbol { Name = loser.Name, IsFunction = loser.IsFunction });
            obj.SuppressedDefinitions.Add(loser.Name);
        }
        // What moved is a new symbol now (Splice): hand back the ones in the
        // object, which is what the caller will take out of it.
        return kept.Select(k => obj.Symbols.First(s => s.Name == k.Name && s.IsDefined && s.Global)).ToList();
    }

    static void ReplaceReloc(Section section, int offset, string symbol)
    {
        for (int i = 0; i < section.Relocs.Count; i++)
            if (section.Relocs[i].Offset == offset)
            {
                Relocation r = section.Relocs[i];
                section.Relocs[i] = new Relocation(r.Offset, symbol, 0, r.Kind);
                return;
            }
        throw new ElfFormatException("table base relocation missing at " + section.Name + "+" + offset);
    }

    /// Where an offset ends up once the cuts are taken; -1 inside one.
    static long Map(List<(long Start, long End)> cuts, long offset)
    {
        long removed = 0;
        foreach ((long start, long end) in cuts)
        {
            if (offset < start) break;
            if (offset < end) return -1;
            removed += end - start;
        }
        return offset - removed;
    }

    /// Whether an offset lies in one of the ranges, sorted and apart: by
    /// halving, a section's every relocation being asked of what is left of
    /// every run.
    static bool Within(List<(long Start, long End)> ranges, long offset)
    {
        int low = 0, high = ranges.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (ranges[middle].End <= offset) low = middle + 1;
            else high = middle;
        }
        return low < ranges.Count && ranges[low].Start <= offset;
    }

    internal static void Splice(ObjectFile obj, Section section, List<(long Start, long End)> cuts, HashSet<Symbol> losers)
    {
        // IN PLACE: a cut only removes bytes, so what is kept moves down and
        // never over a byte not yet read; no second copy of the section.
        ChunkedBytes bytes = section.Bytes;
        int at = 0, kept = 0;
        foreach ((long start, long end) in cuts)
        {
            for (; at < start; at++) bytes[kept++] = bytes[at];
            at = (int)end;
        }
        for (; at < bytes.Count; at++) bytes[kept++] = bytes[at];
        bytes.Truncate(kept);

        List<Relocation> relocs = new(section.Relocs.Count);
        foreach (Relocation r in section.Relocs)
        {
            long to = Map(cuts, r.Offset);
            if (to >= 0) relocs.Add(r with { Offset = (int)to });
        }
        section.Relocs.Clear();
        section.Relocs.AddRange(relocs);

        for (int i = obj.Symbols.Count - 1; i >= 0; i--)
        {
            Symbol s = obj.Symbols[i];
            if (s.Section != section) continue;
            if (losers.Contains(s)) { obj.Symbols.RemoveAt(i); continue; }
            // One that falls in a cut is a bug in the checks above.
            long to = Map(cuts, s.Offset);
            if (to < 0) throw new ElfFormatException("symbol " + s.Name + " inside a cut duplicate");
            if (to != s.Offset)
                obj.Symbols[i] = new Symbol { Name = s.Name, Section = section, Offset = to, Size = s.Size, IsFunction = s.IsFunction, Global = s.Global };
        }
    }

    /// <summary>A section's symbols and their offsets, ascending by offset.</summary>
    static (long[] Offsets, Symbol[] Symbols) Placed(ObjectFile obj, Section section)
    {
        List<Symbol> here = new();
        foreach (Symbol symbol in obj.Symbols) if (symbol.Section == section) here.Add(symbol);
        Symbol[] symbols = here.ToArray();
        long[] offsets = new long[symbols.Length];
        for (int i = 0; i < symbols.Length; i++) offsets[i] = symbols[i].Offset;
        Array.Sort(offsets, symbols);
        return (offsets, symbols);
    }

    static uint U32(IReadOnlyList<byte> b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    static void Put(List<byte> b, uint v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }

    static long BaseOffset(ObjectFile obj, Section table, int relocAt, Section code, out bool known)
    {
        known = false;
        foreach (Relocation r in table.Relocs)
        {
            if (r.Offset != relocAt) continue;
            Symbol? s = obj.Symbols.FirstOrDefault(s => s.Name == r.Symbol && s.Section == code);
            if (s is null || r.Addend != 0) return 0;
            known = true;
            return s.Offset;
        }
        return 0;
    }

    /// The stack-map table (StackMapTable, the x86 code generator's): decoded,
    /// and encoded again without what was cut. A table in any other format is
    /// Unknown, and the code it describes is left as it is.
    sealed class StackMaps
    {
        public required Section Section;
        public bool Unknown;
        long _base;
        List<StackMapTable.Function> _functions = new();

        public static StackMaps? Read(ObjectFile obj, Section code)
        {
            Section? s = obj.Sections.FirstOrDefault(s => s.Name.EndsWith(".corsac.stackmaps", StringComparison.Ordinal));
            if (s is null || s.Bytes.Count == 0) return null;
            StackMaps m = new() { Section = s };
            // Functions with their sites, as varints.
            List<StackMapTable.Function>? functions = StackMapTable.Decode(s.Bytes);
            if (functions is null) { m.Unknown = true; return m; }
            if (functions.Count == 0) return null;
            m._functions = functions;
            m._base = BaseOffset(obj, s, StackMapTable.BaseOffset, code, out bool known);
            if (!known) m.Unknown = true;
            return m;
        }

        /// Functions whose code was cut go with their sites; the rest are
        /// measured from the code's first byte (the new base), their sites
        /// unchanged, and the table is encoded again.
        public void Write(List<(long Start, long End)> cuts, List<(long Start, long End)> dead)
        {
            List<StackMapTable.Function> kept = new();
            foreach (StackMapTable.Function f in _functions)
            {
                long to = Map(cuts, _base + f.Start);
                if (to < 0 || Within(dead, _base + f.Start)) continue;
                f.Start = (int)to;
                kept.Add(f);
            }
            ChunkedBytes b = Section.Bytes;
            b.Clear();
            b.AddRange(StackMapTable.Encode(kept));
        }
    }

    /// The frame table (FrameTable in the compiler, FrameTableFormat its
    /// layout): a header, entries delta-coded from a relocated base, then the
    /// line programs and the strings. Read whole, its line programs decoded,
    /// and written again, so the programs the cut functions shared go with
    /// them and the rest are shared afresh.
    sealed class Frames
    {
        public required Section Section;
        public required Symbol Symbol;
        public bool Unknown;
        long _base;
        int _strings;
        List<FrameTableFormat.Entry> _entries = new();

        public static Frames? Read(ObjectFile obj, Section code)
        {
            Symbol? sym = obj.Symbols.FirstOrDefault(s => s.Name == FrameSymbol && s.IsDefined);
            if (sym is null) return null;
            Frames f = new() { Section = sym.Section!, Symbol = sym };
            ChunkedBytes b = f.Section.Bytes;
            int at = (int)sym.Offset;
            if (sym.Size < FrameTableFormat.HeaderBytes || U32(b, at) != FrameMagic) { f.Unknown = true; return f; }
            var entries = FrameTableFormat.Read(b, at, (int)sym.Size);
            if (entries is null) { f.Unknown = true; return f; }
            if (entries.Count == 0) return null;
            f._entries = entries;
            f._strings = (int)U32(b, at + 12);
            f._base = BaseOffset(obj, f.Section, at + FrameTableFormat.BaseOffset, code, out bool known);
            if (!known || f._strings < FrameTableFormat.HeaderBytes || f._strings > sym.Size) f.Unknown = true;
            return f;
        }

        /// The table without the cut functions (nor those in what is left of a run, dead); `first` is where the first
        /// kept function now starts, the new base.
        public byte[] Build(List<(long Start, long End)> cuts, List<(long Start, long End)> dead, out long first)
        {
            ChunkedBytes b = Section.Bytes;
            int at = (int)Symbol.Offset;
            List<FrameTableFormat.Entry> kept = new();
            first = -1;
            foreach (FrameTableFormat.Entry e in _entries)
            {
                long to = Map(cuts, _base + e.Start);
                if (to < 0 || Within(dead, _base + e.Start)) continue;
                if (first < 0) first = to;
                kept.Add(e with { Start = to - first });
            }
            if (first < 0) first = 0;
            return FrameTableFormat.Build(FrameMagic, kept, b.Slice(at + _strings, (int)Symbol.Size - _strings));
        }
    }
}
