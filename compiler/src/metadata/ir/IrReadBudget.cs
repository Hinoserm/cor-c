namespace Corsac.Lang.Metadata;

/// <summary>
/// Charge decoded nodes and arrays before allocating them, not a multiplier of file size.
///
/// WHAT THE DECODE HOLDS, at the sizes the objects have (IrFunctionCodec.DecodeCost):
/// a name met again is the string the decode already holds (IrBinary.Name
/// interns), so it is charged the first time and never after; a record's
/// bytes are let go once it is decoded (Release). Charged over twice an
/// instruction's size, a name at every occurrence and every record's bytes
/// as if all were held at once, a link's unit of the class library -- 213 MB
/// when decoded -- was charged 543 MB and refused.
/// </summary>
public sealed class IrReadBudget
{
    public long Limit { get; }
    public long Used { get; private set; }
    // The names charged so far, by their text.
    private HashSet<string>? _names;

    public IrReadBudget(long limit = 64L * 1024 * 1024)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        Limit = limit;
    }
    public void Charge(long count, int bytes, string kind)
    {
        if (count < 0 || bytes < 0 || count > (Limit - Used) / Math.Max(1, bytes))
            throw new InvalidDataException("IR decode budget exceeded by " + kind + "; accounted=" + Used + ", limit=" + Limit);
        Used += count * bytes;
    }

    /// <summary>
    /// What a name of `length` UTF-8 bytes costs the decode that meets it
    /// first: its string, a header and two bytes a character (at most a
    /// character a byte), and its place in the tables that find it again.
    /// What every occurrence was charged before, so no record costs more
    /// than the DecodeCost its object was written with.
    /// </summary>
    public static long NameBytes(int length) => 32L + 3L * length;

    /// <summary>`name`, `length` UTF-8 bytes of the record: charged if this decode has not met it before.</summary>
    public void ChargeName(string name, int length)
    {
        if (_names?.Contains(name) == true) return;
        Charge(1, checked((int)NameBytes(length)), "names");
        (_names ??= new(StringComparer.Ordinal)).Add(name);
    }

    /// <summary>`bytes` charged before and held no longer: a record's own bytes, once it is decoded.</summary>
    public void Release(long bytes)
    {
        if (bytes < 0 || bytes > Used) throw new ArgumentOutOfRangeException(nameof(bytes));
        Used -= bytes;
    }
}
