#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// THE DESCRIPTORS OF AN OBJECT, PACKED, once the link has chosen which copy
/// of each it keeps (DefinitionCoalescer). A type's descriptor is a header of
/// HeaderWords words and then its vtable, and the vtable is mostly empty: a
/// class's own virtuals are numbered above every interface's slot
/// (Binder.AssignSlots), and the interfaces it does not implement are holes.
/// A hole is a word nothing reads -- a virtual or an interface call reads a
/// slot only through a type that has it, a type test reads the display, and
/// a string or an array, which have no vtable, is told apart before any slot
/// is asked for (Lowering.AskSlot) -- so each descriptor is laid where its
/// words that are something (its header, and every word relocated or not
/// zero) fall on words no other has taken, its holes over the others'. The
/// GUI module's descriptors were two thirds holes.
///
/// HERE, NOT IN THE COMPILER: a definition several units give is proved the
/// same by the bytes and relocations under its name (DefinitionFingerprint),
/// and packed, those would be its neighbours' too, other in every unit. Once
/// the copies are chosen nothing is compared any more.
///
/// A DESCRIPTOR is a symbol whose SelfWord is a relocation to itself, whole
/// words, longer than its header, with no other symbol inside it but one at
/// its start no longer than it (an alias). Each section's are taken out --
/// a cut rounded down to the section's alignment, so what follows keeps
/// its own, the few bytes left over dead and their relocations dropped --
/// and laid packed at the section's end, the first place that fits among
/// the last Window words, the densest first; the same input lays the same
/// bytes. Not a section any relocation names by the section's own name: an
/// offset into it would no longer say what it said.
/// </summary>
public static class DescriptorPacker
{
    public const int HeaderWords = 12, SelfWord = 5;
    private const int Window = 4096;

    public static void Run(IEnumerable<ObjectFile> objects)
    {
        foreach (ObjectFile obj in objects) Pack(obj);
    }

    private sealed class Item
    {
        public required Symbol Symbol;
        public required List<Symbol> Aliases;
        public required List<Relocation> Relocs;
        public required uint[] Words;
        public required int[] Dense;
        public int Index;
        public int At;
    }

    private static void Pack(ObjectFile obj)
    {
        int w = TargetContract.IsLongMode(obj) ? 8 : 4;
        HashSet<string> sectionNames = new(obj.Sections.Select(s => s.Name), StringComparer.Ordinal);
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (Section s in obj.Sections)
            foreach (Relocation r in s.Relocs)
                if (sectionNames.Contains(r.Symbol)) named.Add(r.Symbol);
        foreach (Section s in obj.Sections)
        {
            if (s.Kind is not (SectionKind.ReadOnlyData or SectionKind.Data) || named.Contains(s.Name) || s.HandedOver is not null) continue;
            if (!obj.Symbols.Any(x => x.Section == s && x.Size > HeaderWords * w)) continue;
            s.Load();
            PackSection(obj, s, w);
        }
    }

    private static void PackSection(ObjectFile obj, Section s, int w)
    {
        List<Symbol> here = obj.Symbols.Where(x => x.Section == s).OrderBy(x => x.Offset).ThenByDescending(x => x.Size).ToList();
        Relocation[] relocs = s.Relocs.ToArray();
        Array.Sort(relocs, (a, b) => a.Offset.CompareTo(b.Offset));
        int[] offsets = relocs.Select(r => r.Offset).ToArray();

        List<Item> items = new();
        for (int i = 0; i < here.Count; i++)
        {
            Symbol d = here[i];
            long start = d.Offset, end = d.Offset + d.Size;
            if (d.Size <= HeaderWords * w || d.Size % w != 0 || start % w != 0 || end > s.Length) continue;
            // Nothing else inside it but aliases at its start.
            List<Symbol> aliases = new();
            bool alone = true;
            foreach (Symbol x in here)
            {
                if (ReferenceEquals(x, d) || x.Offset + Math.Max(x.Size, 1) <= start || x.Offset >= end) continue;
                if (x.Offset == start && x.Size <= d.Size) aliases.Add(x);
                else { alone = false; break; }
            }
            if (!alone) continue;
            int from = LowerBound(offsets, (int)start), to = LowerBound(offsets, (int)end);
            List<Relocation> mine = new();
            bool self = false, whole = true;
            for (int k = from; k < to; k++)
            {
                Relocation r = relocs[k];
                if ((r.Offset - start) % w != 0) { whole = false; break; }
                if (r.Offset - start == SelfWord * w && r.Symbol == d.Name && r.Addend == 0) self = true;
                mine.Add(r);
            }
            if (!self || !whole) continue;
            if (items.Count > 0 && items[^1].Symbol.Offset + items[^1].Symbol.Size > start) continue;
            int words = (int)(d.Size / w);
            uint[] values = new uint[words * (w / 4)];
            for (int k = 0; k < values.Length; k++) values[k] = s.Bytes.ReadUInt32((int)start + k * 4);
            HashSet<int> relocated = new(mine.Select(r => (int)((r.Offset - start) / w)));
            List<int> dense = new();
            for (int k = 0; k < words; k++)
            {
                bool nonzero = false;
                for (int q = 0; q < w / 4; q++) nonzero |= values[k * (w / 4) + q] != 0;
                if (k < HeaderWords || relocated.Contains(k) || nonzero) dense.Add(k);
            }
            items.Add(new Item { Symbol = d, Aliases = aliases, Relocs = mine, Words = values, Dense = dense.ToArray(), Index = items.Count });
        }
        if (items.Count < 2) return;

        // The block: densest first, each where its dense words meet none.
        List<Item> order = items.OrderByDescending(x => x.Dense.Length).ThenBy(x => x.Index).ToList();
        List<bool> taken = new();
        List<uint> block = new();
        int top = 0, per = w / 4;
        foreach (Item it in order)
        {
            int words = it.Words.Length / per;
            int at = Math.Max(0, top - Window);
            for (; at < top; at++)
            {
                bool fits = true;
                foreach (int k in it.Dense)
                    if (at + k < taken.Count && taken[at + k]) { fits = false; break; }
                if (fits) break;
            }
            while (taken.Count < at + words) taken.Add(false);
            while (block.Count < (at + words) * per) block.Add(0);
            foreach (int k in it.Dense)
            {
                taken[at + k] = true;
                for (int q = 0; q < per; q++) block[(at + k) * per + q] = it.Words[k * per + q];
            }
            top = Math.Max(top, at + words);
            it.At = at;
        }
        int packedBytes = top * w;
        long before = items.Sum(x => x.Symbol.Size);
        if (packedBytes >= before) return;

        // The old ranges cut, each rounded down to the section's alignment;
        // what is left of one is dead, and its relocations go.
        int align = Math.Max(s.Align, 1);
        HashSet<Symbol> moving = new(ReferenceEqualityComparer.Instance);
        List<(long Start, long End)> cuts = new();
        List<(long Start, long End)> dead = new();
        foreach (Item it in items)
        {
            moving.Add(it.Symbol);
            foreach (Symbol a in it.Aliases) moving.Add(a);
            long start = it.Symbol.Offset, end = start + it.Symbol.Size;
            long cut = (end - start) / align * align;
            if (cut > 0) cuts.Add((start, start + cut));
            if (start + cut < end) dead.Add((start + cut, end));
        }
        HashSet<Relocation> movedRelocs = new(items.SelectMany(x => x.Relocs));
        List<Relocation> keptRelocs = new(s.Relocs.Count);
        foreach (Relocation r in s.Relocs)
            if (!movedRelocs.Contains(r)) keptRelocs.Add(r);
        s.Relocs.Clear();
        s.Relocs.AddRange(keptRelocs);
        foreach ((long start, long end) in dead)
            for (long k = start; k < end; k++) s.Bytes[(int)k] = 0;

        // The moving symbols out of the way while the cuts are taken.
        List<Symbol> held = obj.Symbols.Where(x => moving.Contains(x)).ToList();
        obj.Symbols.RemoveAll(x => moving.Contains(x));
        DuplicateCutter.Splice(obj, s, cuts, new HashSet<Symbol>(ReferenceEqualityComparer.Instance));

        // The block at the end, aligned as the widest of them.
        int blockAlign = Math.Max(w, 8);
        while (s.Bytes.Count % blockAlign != 0) s.Bytes.Add(0);
        s.Align = Math.Max(s.Align, blockAlign);
        int baseAt = s.Bytes.Count;
        foreach (uint v in block)
        {
            s.Bytes.Add((byte)v); s.Bytes.Add((byte)(v >> 8)); s.Bytes.Add((byte)(v >> 16)); s.Bytes.Add((byte)(v >> 24));
        }
        foreach (Item it in items)
        {
            long now = baseAt + (long)it.At * w;
            long was = it.Symbol.Offset;
            foreach (Relocation r in it.Relocs) s.Relocs.Add(r with { Offset = (int)(now + (r.Offset - was)) });
            foreach (Symbol x in held.Where(x => ReferenceEquals(x, it.Symbol) || it.Aliases.Contains(x)))
                obj.Symbols.Add(new Symbol { Name = x.Name, Section = s, Offset = now, Size = x.Size, IsFunction = x.IsFunction, Global = x.Global });
        }
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
