namespace Corsac.Lang.Lto;

/// <summary>
/// AN ARCHIVE RECORD'S KEY: what it is -- a function (F), data (D), a shadow
/// (S), the unit's own record (M) -- and the name of it. Written as one text,
/// `F:name` (IrArchive, the record's key on disk), and held as the two: the
/// name is the one string the link holds for it, the symbol's own (both read
/// interned), where the text was a second copy of every name the link held
/// -- 66 MB of the compiler's own link at its heap decision.
/// </summary>
public readonly record struct IrKey(char Kind, string Name) : IComparable<IrKey>
{
    public const char Function = 'F', Data = 'D', Shadow = 'S', Unit = 'M';

    /// <summary>The unit's own record (M:unit).</summary>
    public static readonly IrKey UnitRecord = new(Unit, "unit");

    public static IrKey OfFunction(string name) => new(Function, name);
    public static IrKey OfData(string name) => new(Data, name);

    /// <summary>A key as written (`F:name`), its name interned as every name the link reads is.</summary>
    public static IrKey Parse(string text)
        => text.Length > 2 && text[1] == ':' ? new(text[0], string.Intern(text[2..])) : throw new FormatException("Invalid IR key: " + text);

    /// <summary>As written: the kind, a colon, the name.</summary>
    public override string ToString() => Kind + ":" + Name;

    /// <summary>In the order of the written text.</summary>
    public int CompareTo(IrKey other)
        => Kind != other.Kind ? Kind.CompareTo(other.Kind) : string.CompareOrdinal(Name, other.Name);
}

/// <summary>
/// A body's summary, not a deserialized compiler IR graph: where it lies is
/// its archive's (IrArchive), so one body every unit imported is one entry
/// for all of them.
/// </summary>
public sealed record IrArchiveEntry(IrKey Key, bool Importable, int Instructions,
    IReadOnlyList<string> Calls, int Length, BodyHash Hash, IReadOnlyList<string> References, long DecodeBytes)
{
    /// <summary>
    /// A REFERENCE ONLY COMPARED WITH: a descriptor a function names only to
    /// test an object's type against it (`GetType() == typeof(X)`, an exact
    /// type test), marked so. It makes no object of X, so the link's count of
    /// the types made leaves it out (VirtualTargets.MadeTypes); it is reached
    /// all the same (Named).
    /// </summary>
    public const char ComparedOnly = '=';

    /// <summary>The symbol a reference names, the mark taken off.</summary>
    public static string Named(string reference) => reference.Length > 1 && reference[0] == ComparedOnly ? reference[1..] : reference;
}

/// <summary>
/// A BODY'S 32-BYTE HASH, held in the entry itself: an array a record, the
/// compiler's link held 880 thousand of them -- 56 bytes each for 32 of hash.
/// </summary>
public readonly record struct BodyHash(ulong A, ulong B, ulong C, ulong D)
{
    public static BodyHash Of(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("a body hash is 32 bytes");
        return new(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
    }

    public bool Matches(ReadOnlySpan<byte> bytes) => bytes.Length == 32 && Of(bytes) == this;

    public byte[] ToArray()
    {
        byte[] bytes = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, A);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), B);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), C);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), D);
        return bytes;
    }
}
