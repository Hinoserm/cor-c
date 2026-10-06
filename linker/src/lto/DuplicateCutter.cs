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
/// symbol begins, no other symbol sits inside it, and taking it keeps the
/// section's alignment. Any other is left where it is, as before.
/// </summary>
internal static class DuplicateCutter
{
    const string CodeBase = "__corsac_code_base";
    const string FramesBase = "__corsac_frames_base";
    const uint StackMapMagic = 0x314d5343;      // 'CSM1'
    const uint FrameMagic = FrameTableFormat.Local;     // 'CFR5'
    const string FrameSymbol = "__corsac_frames";
    const string StackMapStart = "__corsac_stackmaps";
    const string StackMapEnd = "__corsac_stackmaps_end";

    /// <summary>Cuts what it can of <paramref name="losers"/> and returns the rest.</summary>
    public static List<Symbol> Cut(ObjectFile obj, List<Symbol> losers)
    {
        List<Symbol> kept = new();
        Dictionary<Section, List<(long Start, long End)>> cuts = new();
        HashSet<Symbol> cutSymbols = new(ReferenceEqualityComparer.Instance);
        foreach (Symbol loser in losers)
        {
            Section? section = loser.Section;
            if (section is null || section.Kind == SectionKind.Uninitialised || loser.Size <= 0) { kept.Add(loser); continue; }
            long start = loser.Offset;
            long end = section.Bytes.Count;
            bool alias = false;
            foreach (Symbol other in obj.Symbols)
            {
                if (ReferenceEquals(other, loser) || other.Section != section) continue;
                if (other.Offset == start) { alias = true; break; }
                if (other.Offset > start && other.Offset < end) end = other.Offset;
            }
            if (alias || end < start + loser.Size || (end - start) % section.Align != 0) { kept.Add(loser); continue; }
            if (!cuts.TryGetValue(section, out var list)) cuts.Add(section, list = new());
            list.Add((start, end));
            cutSymbols.Add(loser);
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
                code = null;
                maps = null;
                frames = null;
            }
        }
        foreach (var list in cuts.Values) list.Sort();

        // THE FRAME TABLE REBUILT FIRST, before anything is written. With
        // fewer functions it is nearly always smaller, but its deltas and its
        // shared line programs are all chosen afresh, and a table that would
        // come out longer leaves the code uncut rather than move what follows.
        byte[]? frameBytes = null;
        long framesBase = 0;
        if (code is not null && frames is not null)
        {
            frameBytes = frames.Build(cuts[code], out framesBase);
            if (frameBytes.Length > frames.Symbol.Size)
            {
                foreach (Symbol s in cutSymbols.Where(s => s.Section == code).ToList()) { kept.Add(s); cutSymbols.Remove(s); }
                cuts.Remove(code);
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
                maps.Write(codeCuts);
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
            if (maps is not null) ReplaceReloc(maps.Section, 16, CodeBase);
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

    internal static void Splice(ObjectFile obj, Section section, List<(long Start, long End)> cuts, HashSet<Symbol> losers)
    {
        List<byte> bytes = new(section.Bytes.Count);
        int at = 0;
        foreach ((long start, long end) in cuts)
        {
            for (; at < start; at++) bytes.Add(section.Bytes[at]);
            at = (int)end;
        }
        for (; at < section.Bytes.Count; at++) bytes.Add(section.Bytes[at]);
        section.Bytes.Clear();
        section.Bytes.AddRange(bytes);

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

    static uint U32(List<byte> b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

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

    /// The stack-map table: 'CSM1' and a version. Version 6 (StackMapTable,
    /// the x86 code generator's) is decoded and encoded again; versions 3 and
    /// 4 (the x86-64 generator's is 3) are fixed entries -- the count, the
    /// entry size and the relocated base, entries whose first word is a
    /// return address from the base, then the pool of bitmaps they point into.
    sealed class StackMaps
    {
        public required Section Section;
        public bool Unknown;
        long _base;
        int _entry, _count;
        int[] _pointers = [];

        public static StackMaps? Read(ObjectFile obj, Section code)
        {
            Section? s = obj.Sections.FirstOrDefault(s => s.Name.EndsWith(".corsac.stackmaps", StringComparison.Ordinal));
            if (s is null || s.Bytes.Count == 0) return null;
            StackMaps m = new() { Section = s };
            List<byte> b = s.Bytes;
            if (b.Count < 20 || U32(b, 0) != StackMapMagic) { m.Unknown = true; return m; }
            uint version = U32(b, 4);
            if (version == StackMapTable.Version)
            {
                // Functions with their sites, as varints (StackMapTable).
                m._functions = StackMapTable.Decode(b);
                if (m._functions is null) { m.Unknown = true; return m; }
                if (m._functions.Count == 0) return null;
                m._base = BaseOffset(obj, s, StackMapTable.BaseOffset, code, out bool known6);
                if (!known6) m.Unknown = true;
                return m;
            }
            m._count = (int)U32(b, 8);
            m._entry = (int)U32(b, 12);
            m._pointers = (version, m._entry) switch { (4, 20) => [2, 4], (3, 16) => [2], _ => [] };
            if (m._pointers.Length == 0 || b.Count < 20 + m._count * m._entry) { m.Unknown = true; return m; }
            if (m._count == 0) return null;
            m._base = BaseOffset(obj, s, 16, code, out bool known);
            if (!known) m.Unknown = true;
            return m;
        }

        List<StackMapTable.Function>? _functions;

        /// Version 6: functions whose code was cut go with their sites; the
        /// rest are measured from the code's first byte (the new base), their
        /// sites unchanged, and the table is encoded again.
        void WriteFunctions(List<(long Start, long End)> cuts)
        {
            List<StackMapTable.Function> kept = new();
            foreach (StackMapTable.Function f in _functions!)
            {
                long to = Map(cuts, _base + f.Start);
                if (to < 0) continue;
                f.Start = (int)to;
                kept.Add(f);
            }
            List<byte> b = Section.Bytes;
            b.Clear();
            b.AddRange(StackMapTable.Encode(kept));
        }

        public void Write(List<(long Start, long End)> cuts)
        {
            if (_functions is not null) { WriteFunctions(cuts); return; }
            List<byte> b = Section.Bytes;
            List<byte[]> entries = new();
            for (int i = 0; i < _count; i++)
            {
                int at = 20 + i * _entry;
                // A return address can be the next function's first byte; the
                // call before it is what places it.
                long ret = _base + U32(b, at);
                long to = Map(cuts, ret - 1);
                if (to < 0) continue;
                byte[] e = b.GetRange(at, _entry).ToArray();
                BitConverter.TryWriteBytes(e.AsSpan(0, 4), (uint)(to + 1));
                entries.Add(e);
            }
            int shift = (_count - entries.Count) * _entry;
            List<byte> table = new(b.Count);
            table.AddRange(b.GetRange(0, 20));
            BitConverter.TryWriteBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(table).Slice(8, 4), (uint)entries.Count);
            foreach (byte[] e in entries)
            {
                foreach (int word in _pointers)
                {
                    uint p = BitConverter.ToUInt32(e, word * 4);
                    if (p != 0) BitConverter.TryWriteBytes(e.AsSpan(word * 4, 4), (uint)(p - shift));
                }
                table.AddRange(e);
            }
            table.AddRange(b.GetRange(20 + _count * _entry, b.Count - (20 + _count * _entry)));
            b.Clear();
            b.AddRange(table);
            // The base word is relocated; its section offset has not moved.
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
            List<byte> b = f.Section.Bytes;
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

        /// The table without the cut functions; `first` is where the first
        /// kept function now starts, the new base.
        public byte[] Build(List<(long Start, long End)> cuts, out long first)
        {
            List<byte> b = Section.Bytes;
            int at = (int)Symbol.Offset;
            List<FrameTableFormat.Entry> kept = new();
            first = -1;
            foreach (FrameTableFormat.Entry e in _entries)
            {
                long to = Map(cuts, _base + e.Start);
                if (to < 0) continue;
                if (first < 0) first = to;
                kept.Add(e with { Start = to - first });
            }
            if (first < 0) first = 0;
            return FrameTableFormat.Build(FrameMagic, kept, b.GetRange(at + _strings, (int)Symbol.Size - _strings));
        }
    }
}
