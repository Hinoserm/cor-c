#nullable enable

namespace Corsac.Lang.Ir;

/// <summary>
/// A place in a section to fill with a symbol's address, as Kind says.
///
/// SIXTEEN BYTES, not twenty-four: the offset and the kind share a word (the
/// kind in its low four bits) and the addend is a word of its own. A link
/// holds every object's relocations from the first phase to the layout --
/// the compiler's own, six million of them -- and a long addend and a kind of
/// its own made each half as large again. An offset past what the word holds
/// (2^27 either side) or an addend past 32 bits is refused, never cut short:
/// this machine's code and data address no further, and an x86-64 object's
/// addends are its own symbols' small offsets.
/// </summary>
public readonly record struct Relocation
{
    private readonly int _offsetKind;
    private readonly int _addend;

    public Relocation(int Offset, string Symbol, long Addend, RelocKind Kind)
    {
        _offsetKind = Pack(Offset, Kind);
        this.Symbol = Symbol;
        _addend = Narrow(Addend);
    }

    public int Offset
    {
        get => _offsetKind >> 4;
        init => _offsetKind = Pack(value, Kind);
    }

    public string Symbol { get; init; }

    public long Addend
    {
        get => _addend;
        init => _addend = Narrow(value);
    }

    public RelocKind Kind
    {
        get => (RelocKind)(_offsetKind & 0xF);
        init => _offsetKind = Pack(Offset, value);
    }

    public void Deconstruct(out int Offset, out string Symbol, out long Addend, out RelocKind Kind)
    {
        Offset = this.Offset;
        Symbol = this.Symbol;
        Addend = this.Addend;
        Kind = this.Kind;
    }

    private static int Pack(int offset, RelocKind kind)
    {
        if (offset < -(1 << 27) || offset >= 1 << 27)
            throw new InvalidOperationException($"a relocation at 0x{offset:x}, past the 2^27 bytes a section's relocation can be at");
        if ((byte)kind > 0xF) throw new InvalidOperationException("a relocation kind past the four bits it is held in: " + kind);
        return offset << 4 | (byte)kind;
    }

    private static int Narrow(long addend)
        => addend is >= int.MinValue and <= int.MaxValue ? (int)addend
            : throw new InvalidOperationException($"a relocation addend of 0x{addend:x}, past the 32 bits it is held in");
}
