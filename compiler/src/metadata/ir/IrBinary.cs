using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Metadata;

internal static class IrBinary
{
    public static readonly UTF8Encoding Utf8 = new(false, true);

    // A STREAM AND A WRITER KEPT A THREAD (Writer, Written), for the records
    // written a function and a data item at a time: a stream made for each
    // grew by doubling to the record's size, and every array it outgrew was
    // the collector's -- a thirtieth of what a native compile left it. Not
    // while one is open already on this thread (a record written inside
    // another's), and let go when a record left it larger than KeptBytes.
    [ThreadStatic] private static MemoryStream? _stream;
    [ThreadStatic] private static BinaryWriter? _writer;
    [ThreadStatic] private static bool _open;
    [ThreadStatic] private static byte[]? _text;
    private const int KeptBytes = 1 << 20;

    /// <summary>A writer onto an empty stream, this thread's kept one when it is free; give it to Written for the bytes.</summary>
    public static BinaryWriter Writer()
    {
        if (_open) return new BinaryWriter(new MemoryStream(), Utf8, leaveOpen: false);
        _open = true;
        MemoryStream stream = _stream ??= new MemoryStream();
        stream.SetLength(0);
        stream.Position = 0;
        return _writer ??= new BinaryWriter(stream, Utf8, leaveOpen: true);
    }

    /// <summary>What `writer` (from Writer) holds; the kept stream is free again.</summary>
    public static byte[] Written(BinaryWriter writer)
    {
        writer.Flush();
        MemoryStream stream = (MemoryStream)writer.BaseStream;
        byte[] bytes = stream.ToArray();
        if (!ReferenceEquals(writer, _writer))
        {
            writer.Dispose();
            return bytes;
        }
        if (stream.Capacity > KeptBytes)
        {
            _stream = null;
            _writer = null;
        }
        _open = false;
        return bytes;
    }

    public static void Text(BinaryWriter writer, string? value)
    {
        if (value is null) { writer.Write(-1); return; }
        // Encoded into a buffer kept a thread, not an array a string.
        int most = Utf8.GetMaxByteCount(value.Length);
        byte[] text = _text is { } kept && kept.Length >= most ? kept : _text = new byte[Math.Max(most, 256)];
        int length = Utf8.GetBytes(value, 0, value.Length, text, 0);
        if (length > 16384 || value.Contains('\0')) throw new InvalidDataException("Invalid IR text");
        writer.Write(length); writer.Write(text, 0, length);
    }
    public static string? Text(BinaryReader reader, IrReadBudget? budget = null)
    {
        int length = reader.ReadInt32();
        if (length == -1) return null;
        if (length < 0 || length > 16384 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid IR text length");
        budget?.Charge(32L + 3L * length, 1, "text");
        string value = Utf8.GetString(reader.ReadBytes(length));
        if (value.Contains('\0')) throw new InvalidDataException("Invalid IR text");
        return value;
    }
    /// <summary>
    /// A name: a symbol, a callee, a label. INTERNED, and read through a
    /// buffer of this thread's: the same names stand in every unit's IR, and a
    /// link that decoded all of them held each one a hundred times over, every
    /// copy beside the byte array it was decoded from.
    /// </summary>
    public static string Name(BinaryReader reader, IrReadBudget? budget = null)
    {
        int length = reader.ReadInt32();
        if (length == -1) throw new InvalidDataException("Null IR name");
        if (length < 0 || length > 16384 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid IR text length");
        budget?.Charge(32L + 3L * length, 1, "text");
        byte[] buffer = _nameBuffer ??= new byte[16384];
        int got = 0;
        while (got < length)
        {
            int n = reader.Read(buffer, got, length - got);
            if (n <= 0) throw new InvalidDataException("Truncated IR name");
            got += n;
        }
        string value = Utf8.GetString(buffer, 0, length);
        if (value.Contains('\0')) throw new InvalidDataException("Invalid IR text");
        return string.Intern(value);
    }

    [ThreadStatic] private static byte[]? _nameBuffer;
    public static bool Flag(BinaryReader reader) => reader.ReadByte() switch
    { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid IR flag") };
    public static int Count(BinaryReader reader, int maximum = 1000000)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid IR record count");
        return count;
    }
    public static IrType Type(BinaryReader reader)
    {
        IrType type = (IrType)reader.ReadByte();
        if (!Enum.IsDefined(type)) throw new InvalidDataException("Unknown IR type");
        return type;
    }
    public static void End(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing IR payload bytes");
    }
}
