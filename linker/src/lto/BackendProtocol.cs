using System.Text;

namespace Corsac.Lang.Lto;

/// <summary>Bounded framed requests over one persistent compiler-backend process.</summary>
public static class BackendProtocol
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static void WriteRequest(BinaryWriter writer, BackendRequest request)
    {
        writer.Write((byte)0x52); writer.Write(2);
        WriteText(writer, request.Input); WriteText(writer, request.Output); writer.Write(request.Imports.Count);
        foreach (IrImport import in request.Imports)
        { WriteText(writer, import.Symbol); writer.Write(import.Body.Length); writer.Write(import.Body); }
        writer.Write(request.Retained?.Count ?? -1);
        if (request.Retained is not null)
            foreach (string key in request.Retained.Order(StringComparer.Ordinal)) WriteText(writer, key);
        writer.Flush();
    }
    public static BackendRequest? ReadRequest(BinaryReader reader)
    {
        int marker = reader.BaseStream.ReadByte();
        if (marker == -1) return null;
        if (marker != 0x52 || reader.ReadInt32() != 2) throw new InvalidDataException("Unsupported backend protocol");
        string input = ReadText(reader), output = ReadText(reader);
        int count = reader.ReadInt32(), bytes = 0;
        if (count < 0 || count > 256) throw new InvalidDataException("Backend import count exceeds budget");
        List<IrImport> imports = new(count);
        for (int i = 0; i < count; i++)
        {
            string symbol = ReadText(reader); int length = reader.ReadInt32();
            if (length < 0 || length > 16 * 1024 * 1024 - bytes) throw new InvalidDataException("Backend import bytes exceed budget");
            byte[] body = reader.ReadBytes(length);
            if (body.Length != length) throw new EndOfStreamException("Truncated backend import");
            imports.Add(new(symbol, body)); bytes += length;
        }
        int retainedCount = reader.ReadInt32();
        if (retainedCount < -1 || retainedCount > 100000) throw new InvalidDataException("Invalid backend retention count");
        HashSet<string>? retained = retainedCount < 0 ? null : new(StringComparer.Ordinal);
        int nameBytes = 0;
        for (int i = 0; i < retainedCount; i++)
        {
            string key = ReadText(reader); nameBytes = checked(nameBytes + Utf8.GetByteCount(key));
            if (nameBytes > 16 * 1024 * 1024 || !retained!.Add(key)) throw new InvalidDataException("Invalid backend retention set");
        }
        return new(input, output, imports, retained);
    }
    public static void WriteResponse(BinaryWriter writer, string? error)
    { writer.Write(error is null ? 0 : 1); WriteText(writer, error ?? ""); writer.Flush(); }
    public static void ReadResponse(BinaryReader reader)
    {
        int status = reader.ReadInt32(); string error = ReadText(reader);
        if (status == 1) throw new InvalidDataException("Compiler backend: " + error);
        if (status != 0 || error.Length != 0) throw new InvalidDataException("Invalid backend response");
    }
    private static void WriteText(BinaryWriter writer, string text)
    {
        byte[] bytes = Utf8.GetBytes(text);
        if (bytes.Length > 65536 || text.Contains('\0')) throw new InvalidDataException("Invalid backend text");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string ReadText(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 65536) throw new InvalidDataException("Invalid backend text length");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Truncated backend text");
        string text = Utf8.GetString(bytes);
        if (text.Contains('\0')) throw new InvalidDataException("Invalid backend text");
        return text;
    }
}
