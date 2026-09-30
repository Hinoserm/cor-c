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
    const uint FrameMagic = 0x4D524643;         // 'CFRM'
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

        long framesBase = 0;
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
                byte[] bytes = frames.Build(codeCuts, out framesBase);
                Section at = frames.Section;
                int old = (int)frames.Symbol.Size;
                if (bytes.Length > old) throw new ElfFormatException("frame table grew while cutting duplicates");
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

    /// The stack-map table: 'CSM1', a version, the count, the entry size and
    /// the relocated base; entries whose first word is a return address from
    /// the base; then the pool of bitmaps the entries point into.
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
            if (version == 5)
            {
                // A function record each, then a site record each (X86Backend.EmitStackMaps).
                if (b.Count < 32) { m.Unknown = true; return m; }
                m._v5 = true;
                m._count = (int)U32(b, 8);
                m._sites = (int)U32(b, 12);
                if (m._count == 0) return null;
                if (U32(b, 20) != 32 + m._count * 16 || U32(b, 28) != U32(b, 20) + m._sites * 8 || b.Count < U32(b, 28)) { m.Unknown = true; return m; }
                m._base = BaseOffset(obj, s, 16, code, out bool known5);
                if (!known5) m.Unknown = true;
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

        bool _v5;
        int _sites;

        /// Version 5: functions whose code was cut go with their sites; the
        /// rest are measured from the code's first byte (the new base), their
        /// sites unchanged, and pooled bitmaps move down with the tables.
        void WriteFunctions(List<(long Start, long End)> cuts)
        {
            List<byte> b = Section.Bytes;
            int sitesAt = (int)U32(b, 20), poolAt = (int)U32(b, 28);
            List<(uint Start, uint Frame, uint Objects, int First, int Last)> kept = new();
            long span = 0;
            for (int f = 0; f < _count; f++)
            {
                int at = 32 + f * 16;
                long start = _base + U32(b, at);
                int first = (int)U32(b, at + 12), last = f + 1 < _count ? (int)U32(b, at + 16 + 12) : _sites;
                long to = Map(cuts, start);
                if (to < 0) continue;
                for (int k = first; k < last; k++) span = Math.Max(span, to + (U32(b, sitesAt + k * 8) & 0x7FFFFF) + 1);
                kept.Add(((uint)to, U32(b, at + 4), U32(b, at + 8), first, last));
            }
            int keptSites = kept.Sum(k => k.Last - k.First);
            int shift = (_count - kept.Count) * 16 + (_sites - keptSites) * 8;
            uint Moved(uint word) => word != 0 && (word & 0x80000000) == 0 ? (uint)(word - shift) : word;
            List<byte> table = new(b.Count);
            Put(table, StackMapMagic); Put(table, 5); Put(table, (uint)kept.Count); Put(table, (uint)keptSites);
            Put(table, 0);                                   // the base, relocated
            Put(table, (uint)(32 + kept.Count * 16)); Put(table, (uint)span);
            Put(table, (uint)(32 + kept.Count * 16 + keptSites * 8));
            int index = 0;
            foreach (var k in kept)
            {
                Put(table, k.Start); Put(table, k.Frame); Put(table, Moved(k.Objects)); Put(table, (uint)index);
                index += k.Last - k.First;
            }
            foreach (var k in kept)
                for (int site = k.First; site < k.Last; site++)
                {
                    Put(table, U32(b, sitesAt + site * 8));
                    Put(table, Moved(U32(b, sitesAt + site * 8 + 4)));
                }
            table.AddRange(b.GetRange(poolAt, b.Count - poolAt));
            b.Clear();
            b.AddRange(table);
        }

        public void Write(List<(long Start, long End)> cuts)
        {
            if (_v5) { WriteFunctions(cuts); return; }
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

    /// The frame table (FrameTable in the compiler): a header, uleb entries
    /// delta-coded from a relocated base, then line programs and strings.
    sealed class Frames
    {
        public required Section Section;
        public required Symbol Symbol;
        public bool Unknown;
        long _base;
        int _count, _lines, _strings;

        public static Frames? Read(ObjectFile obj, Section code)
        {
            Symbol? sym = obj.Symbols.FirstOrDefault(s => s.Name == FrameSymbol && s.IsDefined);
            if (sym is null) return null;
            Frames f = new() { Section = sym.Section!, Symbol = sym };
            List<byte> b = f.Section.Bytes;
            int at = (int)sym.Offset;
            if (sym.Size < 20 || U32(b, at) != FrameMagic) { f.Unknown = true; return f; }
            f._count = (int)U32(b, at + 4);
            f._strings = (int)U32(b, at + 12);
            f._lines = (int)U32(b, at + 16);
            if (f._count == 0) return null;
            f._base = BaseOffset(obj, f.Section, at + 8, code, out bool known);
            if (!known || f._lines > f._strings || f._strings > sym.Size) f.Unknown = true;
            return f;
        }

        public byte[] Build(List<(long Start, long End)> cuts, out long first)
        {
            List<byte> b = Section.Bytes;
            int at = (int)Symbol.Offset;
            int p = at + 20;
            uint Next()
            {
                uint v = 0;
                int shift = 0;
                while (true)
                {
                    byte x = b[p++];
                    v |= (uint)(x & 0x7F) << shift;
                    if ((x & 0x80) == 0) return v;
                    shift += 7;
                }
            }
            List<byte> table = new();
            long start = _base, previous = -1;
            int kept = 0;
            first = 0;
            for (int i = 0; i < _count; i++)
            {
                start += Next();
                uint size = Next(), name = Next(), file = Next(), line = Next();
                long to = Map(cuts, start);
                if (to < 0) continue;
                if (previous < 0) first = previous = to;
                Uleb(table, (uint)(to - previous));
                previous = to;
                Uleb(table, size);
                Uleb(table, name);
                Uleb(table, file);
                Uleb(table, line);
                kept++;
            }
            List<byte> all = new();
            Put(all, FrameMagic);
            Put(all, (uint)kept);
            Put(all, 0);
            int lines = 20 + table.Count;
            Put(all, (uint)(lines + (_strings - _lines)));
            Put(all, (uint)lines);
            all.AddRange(table);
            all.AddRange(b.GetRange(at + _lines, (int)Symbol.Size - _lines));
            return all.ToArray();
        }

        static void Uleb(List<byte> into, uint v)
        {
            do
            {
                byte x = (byte)(v & 0x7F);
                v >>= 7;
                into.Add(v != 0 ? (byte)(x | 0x80) : x);
            }
            while (v != 0);
        }
    }
}
