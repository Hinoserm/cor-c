using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Metadata;

internal static class IrBinary
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static void Text(BinaryWriter writer, string? value)
    {
        if (value is null) { writer.Write(-1); return; }
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > 16384 || value.Contains('\0')) throw new InvalidDataException("Invalid IR text");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    public static string? Text(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == -1) return null;
        if (length < 0 || length > 16384 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid IR text length");
        string value = Utf8.GetString(reader.ReadBytes(length));
        if (value.Contains('\0')) throw new InvalidDataException("Invalid IR text");
        return value;
    }
    public static string Name(BinaryReader reader) => Text(reader) ?? throw new InvalidDataException("Null IR name");
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
