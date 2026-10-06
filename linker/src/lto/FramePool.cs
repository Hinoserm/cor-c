using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// ONE COPY OF EVERY NAME A FRAME TABLE SPELLS. Each unit's frame table
/// (FrameTable in the compiler) carries the names and files of its functions,
/// and every unit compiled on its own carries the generic bodies it uses: the
/// compiler's 376 tables spelled 4.6 MB of names, of which 2.2 MB were
/// different. At the link each table becomes 'CFR6', whose word at +12 is not
/// the offset of its own strings but the address of the image's pool
/// (__corsac_frame_pool), relocated, and whose names and files are names in
/// it; the strings leave the table, and the section closes up behind it.
///
/// AND EVERY LINE PROGRAM MORE THAN ONE FUNCTION CARRIES, once: the same
/// generic body compiled into forty units has forty copies of one program,
/// and the pool holds it after the names for every table to refer to
/// (FrameTableFormat.LinePrograms). The compiler's image shared four in ten
/// of its programs' pairs this way.
///
/// AND EACH NAME SPELLED IN SHARED TOKENS (FrameTableFormat.NamePool): the
/// different names were still mostly the same words -- generic arguments,
/// namespaces, parameter types -- and a name is now a few one-byte indices
/// of them. The 2.4 MB pool of the compiler's names is about a fifth of that.
/// </summary>
public static class FramePool
{
    public const string PoolSymbol = "__corsac_frame_pool";
    private const string TableSymbol = "__corsac_frames";

    public static void Run(List<(string Name, ObjectFile Object)> inputs)
    {
        FrameTableFormat.NamePool pool = new();
        bool longMode = inputs.Any(input => TargetContract.IsLongMode(input.Object));

        // FIRST EVERY NAME, then the pool, then the tables: a table's entries
        // hold where its names are in the pool, which is known only once the
        // pool has seen them all.
        List<(ObjectFile Object, Symbol Table, List<FrameTableFormat.Entry> Entries)> tables = new();
        FrameTableFormat.LinePrograms programs = new();
        foreach (var input in inputs)
        {
            ObjectFile obj = input.Object;
            Symbol? table = obj.Symbols.FirstOrDefault(s => s.Name == TableSymbol && s.IsDefined);
            if (table is null || table.Section is null || table.Size < FrameTableFormat.HeaderBytes) continue;
            ChunkedBytes b = table.Section.Bytes;
            int at = (int)table.Offset, size = (int)table.Size;
            if (U32(b, at) != FrameTableFormat.Local) continue;
            int strings = (int)U32(b, at + 12);
            var read = FrameTableFormat.Read(b, at, size);
            if (read is null) continue;                 // not a table this knows: leave it
            byte[] Text(int offset)
            {
                int start = at + strings + offset, end = start;
                while (end < at + size && b[end] != 0) end++;
                return b.Slice(start, end - start);
            }
            List<FrameTableFormat.Entry> entries = new(read.Count);
            foreach (var e in read)
            {
                entries.Add(e with { Name = pool.Add(Text(e.Name)), File = pool.Add(Text(e.File)) });
                programs.Count(e.Lines);
            }
            tables.Add((obj, table, entries));
        }
        if (tables.Count == 0) return;
        List<byte> built = new(pool.Build());
        int sharedAt = built.Count;
        built.AddRange(programs.Seal());

        foreach (var t in tables)
        {
            Section section = t.Table.Section!;
            ChunkedBytes b = section.Bytes;
            int at = (int)t.Table.Offset, size = (int)t.Table.Size;
            List<FrameTableFormat.Entry> entries = t.Entries.Select(e => e with { Name = pool.Offset(e.Name), File = pool.Offset(e.File) }).ToList();
            byte[] made = FrameTableFormat.Build(FrameTableFormat.Pooled, entries, null, programs, sharedAt);
            // Two bytes of name delta, or of where a shared program lies, can
            // become three where the pool is far larger than one table's own;
            // such a table keeps its names and its programs.
            if (made.Length > size) continue;
            for (int i = 0; i < 4; i++) made[FrameTableFormat.BaseOffset + i] = b[at + FrameTableFormat.BaseOffset + i];
            for (int i = 0; i < size; i++) b[at + i] = i < made.Length ? made[i] : (byte)0;
            section.Relocs.Add(new Relocation(at + FrameTableFormat.PoolOffset, PoolSymbol, 0, longMode ? RelocKind.Rel32 : RelocKind.Abs32));
            // The table's symbols shrink to it; what follows moves down by
            // whole alignment units, the rest of the old bytes left as zeros.
            int spare = (size - made.Length) / section.Align * section.Align;
            ObjectFile obj = t.Object;
            for (int i = 0; i < obj.Symbols.Count; i++)
            {
                Symbol s = obj.Symbols[i];
                if (s.Section == section && s.Offset == at && s.Size == size)
                    obj.Symbols[i] = new Symbol { Name = s.Name, Section = section, Offset = at, Size = size - spare, IsFunction = false, Global = s.Global };
            }
            if (spare > 0)
                DuplicateCutter.Splice(obj, section, new List<(long, long)> { (at + size - spare, at + size) }, new HashSet<Symbol>(ReferenceEqualityComparer.Instance));
        }
        ObjectFile holder = new();
        Section data = new(".rodata", SectionKind.ReadOnlyData) { Align = 4 };
        data.Bytes.AddRange(built);
        holder.Sections.Add(data);
        holder.Symbols.Add(new Symbol { Name = PoolSymbol, Section = data, Offset = 0, Size = built.Count, Global = true });
        // The pool carries the same target notes as the objects it serves.
        foreach (Section note in inputs[0].Object.Sections.Where(s => s.Name == TargetContract.SectionName))
        {
            Section copy = new(note.Name, note.Kind) { Align = note.Align };
            copy.Bytes.AddRange(note.Bytes);
            holder.Sections.Add(copy);
        }
        inputs.Add(("<frame name pool>", holder));
        Console.Error.WriteLine("frame names: " + tables.Count + " tables, one pool of " + built.Count + " bytes, " + (built.Count - sharedAt) + " of them shared line programs");
    }

    private static uint U32(IReadOnlyList<byte> b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
}
