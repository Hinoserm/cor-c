#nullable enable

namespace Corsac.Lang.Ir;

/// <summary>
/// A place in a section to fill with a symbol's address, as Kind says.
///
/// SIXTEEN BYTES, not twenty-four: the offset and the kind share a word --
/// the kind in its low four bits, whether the addend is wide in the next --
/// and the addend is a word of its own. A link holds every object's
/// relocations from the first phase to the layout -- the compiler's own, six
/// million of them -- and a long addend and a kind of its own made each half
/// as large again. An addend past 32 bits is rare (a long-mode stub's
/// `label - 0xffffffff80000000`, a `.dq` of one) and is kept whole in a table
/// of them (Wide), the word holding its place there. An offset past 2^26
/// either side -- a section of 64 MiB, which no unit's is -- is refused, never
/// cut short, with the InvalidOperationException the backends report as a
/// compile error.
/// </summary>
public readonly record struct Relocation
{
    private readonly int _offsetKind;
    private readonly int _addend;

    public Relocation(int Offset, string Symbol, long Addend, RelocKind Kind)
    {
        (int offsetKind, int addend) packed = Pack(Offset, Kind, Addend);
        _offsetKind = packed.offsetKind;
        _addend = packed.addend;
        this.Symbol = Symbol;
    }

    /// <summary>
    /// One read from an object file: a place it cannot hold is the file's
    /// fault, an ElfFormatException naming the section, as every other
    /// malformed thing a reader meets is -- a half-written object left by a
    /// killed build is then compiled again, not a crash (ProjectCompile).
    /// </summary>
    public static Relocation Read(ulong offset, string symbol, long addend, RelocKind kind, string section)
    {
        if (offset > int.MaxValue) throw new Corsac.Lang.Elf.ElfFormatException($"relocation in '{section}' at 0x{offset:x}, past what a section holds");
        try { return new Relocation((int)offset, symbol, addend, kind); }
        catch (InvalidOperationException error) { throw new Corsac.Lang.Elf.ElfFormatException($"relocation in '{section}': {error.Message}"); }
    }

    public int Offset
    {
        get => _offsetKind >> 5;
        init
        {
            (int offsetKind, int addend) packed = Pack(value, Kind, Addend);
            _offsetKind = packed.offsetKind;
            _addend = packed.addend;
        }
    }

    public string Symbol { get; init; }

    public long Addend
    {
        get => (_offsetKind & 0x10) != 0 ? Wide.Value(_addend) : _addend;
        init
        {
            (int offsetKind, int addend) packed = Pack(Offset, Kind, value);
            _offsetKind = packed.offsetKind;
            _addend = packed.addend;
        }
    }

    public RelocKind Kind
    {
        get => (RelocKind)(_offsetKind & 0xF);
        init
        {
            (int offsetKind, int addend) packed = Pack(Offset, value, Addend);
            _offsetKind = packed.offsetKind;
            _addend = packed.addend;
        }
    }

    public void Deconstruct(out int Offset, out string Symbol, out long Addend, out RelocKind Kind)
    {
        Offset = this.Offset;
        Symbol = this.Symbol;
        Addend = this.Addend;
        Kind = this.Kind;
    }

    private static (int OffsetKind, int Addend) Pack(int offset, RelocKind kind, long addend)
    {
        if (offset < -(1 << 26) || offset >= 1 << 26)
            throw new InvalidOperationException($"a relocation at 0x{offset:x}, past the 2^26 bytes a section's relocation can be at");
        if ((byte)kind > 0xF) throw new InvalidOperationException("a relocation kind past the four bits it is held in: " + kind);
        bool wide = addend is < int.MinValue or > int.MaxValue;
        return (offset << 5 | (wide ? 0x10 : 0) | (byte)kind, wide ? Wide.Place(addend) : (int)addend);
    }

    /// <summary>
    /// The addends too wide for a word, each once: a relocation holds its
    /// place here. The same value has the same place, so two relocations of
    /// one wide addend are equal as values.
    /// </summary>
    private static class Wide
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<long, int> Places = new();
        private static readonly List<long> Values = new();

        public static int Place(long addend)
        {
            lock (Gate)
            {
                if (Places.TryGetValue(addend, out int place)) return place;
                place = Values.Count;
                Values.Add(addend);
                Places.Add(addend, place);
                return place;
            }
        }

        public static long Value(int place) { lock (Gate) return Values[place]; }
    }
}
