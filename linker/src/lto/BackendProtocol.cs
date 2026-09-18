using System.Text;

namespace Corsac.Lang.Lto;

/// <summary>Bounded framed requests over one persistent compiler-backend process.</summary>
public static class BackendProtocol
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static void WriteRequest(BinaryWriter writer, BackendRequest request)
    {
        writer.Write((byte)0x52); writer.Write(1);
        WriteText(writer, request.Input); WriteText(writer, request.Output); writer.Write(request.Imports.Count);
        foreach (IrImport import in request.Imports)
        { WriteText(writer, import.Symbol); writer.Write(import.Body.Length); writer.Write(import.Body); }
        writer.Flush();
    }
    public static BackendRequest? ReadRequest(BinaryReader reader)
    {
        int marker = reader.BaseStream.ReadByte();
        if (marker == -1) return null;
        if (marker != 0x52 || reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported backend protocol");
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
        return new(input, output, imports);
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
