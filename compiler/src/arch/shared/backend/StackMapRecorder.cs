#nullable enable
using Corsac.Lang.Frames;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Backends;

/// <summary>
/// ONE OBJECT'S STACK MAPS, as its functions are placed and then as the
/// table (StackMapTable): each backend's Generate carried its own copy,
/// and they differed only in the layout's words and in how the table
/// names its base. A function with a call gets a record, its start
/// counted from the first such function's (the table's base), its sites
/// in the order of the code (StackMapRecords); the table is one an
/// object, with one relocation, the base.
/// </summary>
internal sealed class StackMapRecorder(StackMapLayout layout)
{
    private readonly List<StackMapTable.Function> _functions = new();
    private string? _base;
    private int _baseAt;

    /// <summary>
    /// A placed function's record, where it has a call: `label` its
    /// symbol, `start` its offset in the text, `sites` each call with the
    /// offset of its return from the start. A function that gives the
    /// frame's base register a value (`framePointerFree`) has no frame a
    /// map could count from: its calls -- none but a fault's way out --
    /// have no map, and its frame is read whole. Its IR frame slots
    /// (`slotRanges`) are in each call's own map where their liveness is
    /// known, and every word of them at every call where it is not.
    /// </summary>
    public void Function<TCall>(string label, int start, int frameSize, uint saved, bool framePointerFree,
        IEnumerable<(TCall Call, int Ret)> sites, IReadOnlyDictionary<TCall, Safepoint> safepoints,
        IEnumerable<(int Offset, int Bytes)> slotRanges, FrameSlotLiveness.Result? liveness) where TCall : notnull
    {
        StackMapTable.Function? record = null;
        List<int>? slotWords = null;
        foreach ((TCall call, int ret) in sites)
        {
            if (record is null)
            {
                if (frameSize > 0xFFFFFF) throw new InvalidOperationException("a frame over 16 MB has no stack map");
                if (_base is null) { _base = label; _baseAt = start; }
                // Every IR slot word, where the liveness left the function
                // whole: made once a function, and only then.
                slotWords = liveness is null ? SlotWords(slotRanges, layout.WordBytes) : new List<int>();
                record = StackMapRecords.Function(layout, start - _baseAt, frameSize, framePointerFree ? 0 : saved, slotWords, liveness);
                _functions.Add(record);
            }
            if (ret > 0x7FFFFF) throw new InvalidOperationException("a function over 8 MB has no stack map");
            record.Sites.Add(StackMapRecords.Site(layout, call, ret,
                framePointerFree ? null : safepoints.GetValueOrDefault(call), slotWords!, liveness));
        }
    }

    /// <summary>
    /// Every word of a function's IR frame slots below its frame's base, as
    /// offsets from it: what a call's map names whole where their liveness
    /// is not known. Gathered, sorted and made unique in one list.
    /// </summary>
    public static List<int> SlotWords(IEnumerable<(int Offset, int Bytes)> slotRanges, int word)
    {
        List<int> words = new();
        foreach ((int offset, int bytes) in slotRanges)
            for (int at = offset & -word; at < offset + bytes; at += word)
                if (at < 0) words.Add(at);
        words.Sort();
        int unique = 0;
        for (int k = 0; k < words.Count; k++)
            if (unique == 0 || words[k] != words[unique - 1]) words[unique++] = words[k];
        words.RemoveRange(unique, words.Count - unique);
        return words;
    }

    /// <summary>
    /// The table, read by the collector to know, at each call a frame is
    /// stopped in, which of its words and saved registers hold live
    /// references.
    ///
    /// VARINTS AND SHARED BITMAPS: the layout is StackMapTable's, in the
    /// linker's object model, where it is written down in full; the link
    /// decodes and re-encodes it with the same code when it cuts duplicate
    /// bodies, and runtime/src/core/gc.cor (MapSite) reads it. In short: a
    /// header, a checkpoint every thirty-two call sites for the binary
    /// search, then per function its start, frame size, saved registers and
    /// frame-slot bitmap, and per call site its return address from the one
    /// before and its live registers and spill-slot bitmap, all as varints;
    /// a bitmap of up to sixteen words is held inline, a larger one once in
    /// a pool, the most used first.
    ///
    /// ONE RELOCATION, NOT ONE PER ENTRY. Every function of the object is in
    /// its one text section, so the distance from the first function with a
    /// call site to any call is known here, and is what is written; a
    /// reader adds the base, exactly as the frame table's reader does.
    ///
    /// Into a section of its own named `section`, between the
    /// symbols `start` and `end` (each object owns a complete table: the
    /// image's directory of them finds it, ManagedDirectory). Its base
    /// either the distance from the header's word to the first function
    /// with a call (`pcRelative`: an image loaded anywhere describes
    /// itself without the loader, so the section is read-only in a shared
    /// object too), or that function's address (`absolute`, the word's
    /// relocation), which in a shared object the loader writes: the
    /// section is then writable data, as a frame table and jump tables
    /// are there.
    /// </summary>
    public void Emit(ObjectFile obj, HashSet<string> defined, string section, string start, string end,
        bool pcRelative, RelocKind absolute, bool positionIndependent)
    {
        bool written = !pcRelative && positionIndependent;
        Section s = new(written ? ".data.rel.ro" + section : section, written ? SectionKind.Data : SectionKind.ReadOnlyData)
        {
            Align = layout.WordBytes,
        };
        obj.Sections.Add(s);
        StackMapTable.Encode(_functions, s.Bytes, layout);
        if (_base is not null) s.Relocs.Add(new Relocation(StackMapTable.BaseOffset, _base, 0, pcRelative ? RelocKind.Rel32 : absolute));
        obj.Symbols.Add(new Symbol { Name = start, Section = s, Offset = 0, Size = s.Bytes.Count, Global = false });
        obj.Symbols.Add(new Symbol { Name = end, Section = s, Offset = s.Bytes.Count, Size = 0, Global = false });
        defined.Add(start);
        defined.Add(end);
    }
}
