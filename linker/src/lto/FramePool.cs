using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// ONE COPY OF EVERY NAME A FRAME TABLE SPELLS. Each unit's frame table
/// (FrameTable in the compiler) carries the names and files of its functions,
/// and every unit compiled on its own carries the generic bodies it uses: the
/// compiler's 376 tables spelled 4.6 MB of names, of which 2.2 MB were
/// different. At the link each table becomes version 2 ('CFR2'), whose word at
/// +12 is not the offset of its own strings but the address of the image's
/// pool (__corsac_frame_pool), relocated, and whose names and files are
/// offsets into it; the strings leave the table, and the section closes up
/// behind it. The entries and line programs are unchanged.
/// </summary>
public static class FramePool
{
    public const string PoolSymbol = "__corsac_frame_pool";
    private const string TableSymbol = "__corsac_frames";
    private const uint Version1 = 0x4D524643;       // 'CFRM'
    private const uint Version2 = 0x32524643;       // 'CFR2'

    public static void Run(List<(string Name, ObjectFile Object)> inputs)
    {
        List<byte> pool = new();
        Dictionary<string, int> placed = new(StringComparer.Ordinal);
        int Place(byte[] text)
        {
            string key = Convert.ToBase64String(text);
            if (placed.TryGetValue(key, out int at)) return at;
            at = pool.Count;
            pool.AddRange(text);
            pool.Add(0);
            placed[key] = at;
            return at;
        }
        bool longMode = inputs.Any(input => TargetContract.IsLongMode(input.Object));
        int rewritten = 0;
        foreach (var input in inputs)
        {
            ObjectFile obj = input.Object;
            Symbol? table = obj.Symbols.FirstOrDefault(s => s.Name == TableSymbol && s.IsDefined);
            if (table is null || table.Section is null || table.Size < 20) continue;
            Section section = table.Section;
            List<byte> b = section.Bytes;
            int at = (int)table.Offset, size = (int)table.Size;
            if (U32(b, at) != Version1) continue;
            int count = (int)U32(b, at + 4), strings = (int)U32(b, at + 12), lines = (int)U32(b, at + 16);
            if (strings > size || lines > strings || lines < 20) continue;
            byte[] Text(int offset)
            {
                int start = at + strings + offset, end = start;
                while (end < at + size && b[end] != 0) end++;
                return b.GetRange(start, end - start).ToArray();
            }
            // The entries, with their names and files in the pool.
            List<byte> entries = new();
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
            for (int i = 0; i < count; i++)
            {
                uint delta = Next(), length = Next(), name = Next(), file = Next(), program = Next();
                Uleb(entries, delta);
                Uleb(entries, length);
                Uleb(entries, (uint)Place(Text((int)name)));
                Uleb(entries, (uint)Place(Text((int)file)));
                Uleb(entries, program);
            }
            if (p != at + lines) continue;          // not a table this knows: leave it
            byte[] programs = b.GetRange(at + lines, strings - lines).ToArray();
            List<byte> made = new();
            Put(made, Version2);
            Put(made, (uint)count);
            Put(made, U32(b, at + 8));              // the code base: its relocation stays where it is
            Put(made, 0);                           // the pool, relocated below
            Put(made, (uint)(20 + entries.Count));
            made.AddRange(entries);
            made.AddRange(programs);
            if (made.Count > size) continue;
            for (int i = 0; i < size; i++) b[at + i] = i < made.Count ? made[i] : (byte)0;
            section.Relocs.Add(new Relocation(at + 12, PoolSymbol, 0, longMode ? RelocKind.Rel32 : RelocKind.Abs32));
            // The table's symbols shrink to it; what follows moves down by
            // whole alignment units, the rest of the old bytes left as zeros.
            int spare = (size - made.Count) / section.Align * section.Align;
            for (int i = 0; i < obj.Symbols.Count; i++)
            {
                Symbol s = obj.Symbols[i];
                if (s.Section == section && s.Offset == at && s.Size == size)
                    obj.Symbols[i] = new Symbol { Name = s.Name, Section = section, Offset = at, Size = size - spare, IsFunction = false, Global = s.Global };
            }
            if (spare > 0)
                DuplicateCutter.Splice(obj, section, new List<(long, long)> { (at + size - spare, at + size) }, new HashSet<Symbol>(ReferenceEqualityComparer.Instance));
            rewritten++;
        }
        if (rewritten == 0) return;
        ObjectFile holder = new();
        Section data = new(".rodata", SectionKind.ReadOnlyData) { Align = 1 };
        data.Bytes.AddRange(pool);
        holder.Sections.Add(data);
        holder.Symbols.Add(new Symbol { Name = PoolSymbol, Section = data, Offset = 0, Size = pool.Count, Global = true });
        // The pool carries the same target notes as the objects it serves.
        foreach (Section note in inputs[0].Object.Sections.Where(s => s.Name == TargetContract.SectionName))
        {
            Section copy = new(note.Name, note.Kind) { Align = note.Align };
            copy.Bytes.AddRange(note.Bytes);
            holder.Sections.Add(copy);
        }
        inputs.Add(("<frame name pool>", holder));
        Console.Error.WriteLine("frame names: " + rewritten + " tables, one pool of " + pool.Count + " bytes");
    }

    private static uint U32(List<byte> b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    private static void Put(List<byte> b, uint v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }

    private static void Uleb(List<byte> into, uint v)
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
