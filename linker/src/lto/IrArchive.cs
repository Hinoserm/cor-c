using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Versioned per-definition IR locations. The linker never interprets compiler payloads.</summary>
public sealed class IrArchive
{
    public const string SectionName = ".corsac.ir";
    public const int MaximumBytes = 128 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] bytes;
    public IReadOnlyDictionary<string, IrArchiveEntry> Entries { get; }

    private IrArchive(byte[] bytes, Dictionary<string, IrArchiveEntry> entries)
    { this.bytes = bytes; Entries = entries; }

    public byte[] ReadBody(string key)
    {
        if (!Entries.TryGetValue(key, out IrArchiveEntry? entry)) throw new ElfFormatException("Missing IR record: " + key);
        ReadOnlySpan<byte> body = bytes.AsSpan(entry.Offset, entry.Length);
        if (!SHA256.HashData(body).SequenceEqual(entry.Hash)) throw new ElfFormatException("IR payload integrity mismatch: " + key);
        return body.ToArray();
    }

    public static void Attach(ObjectFile obj, IReadOnlyList<IrArchiveRecord> records)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate IR archive");
        if (records.Count > 100000) throw new ElfFormatException("Too many IR records");
        using MemoryStream directory = new();
        using BinaryWriter writer = new(directory, Utf8, leaveOpen: true);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int bodyBytes = 0;
        foreach (IrArchiveRecord record in records)
        {
            if (!seen.Add(record.Key) || record.Instructions < 0 || record.Calls.Count > 100000)
                throw new ElfFormatException("Invalid IR record: " + record.Key);
            WriteName(writer, record.Key); writer.Write(record.Importable); writer.Write(record.Instructions);
            writer.Write(record.Calls.Count);
            foreach (string call in record.Calls) WriteName(writer, call);
            writer.Write(bodyBytes); writer.Write(record.Payload.Length); writer.Write(SHA256.HashData(record.Payload));
            bodyBytes = checked(bodyBytes + record.Payload.Length);
            if (bodyBytes > MaximumBytes || directory.Length > MaximumBytes - bodyBytes - 84)
                throw new ElfFormatException("IR archive exceeds unit budget");
        }
        using MemoryStream stream = new();
        using BinaryWriter output = new(stream, Utf8, leaveOpen: true);
        output.Write(0x52494343u); output.Write(1); output.Write(records.Count);
        output.Write(checked((int)directory.Length)); output.Write(checked(84 + (int)directory.Length + bodyBytes));
        byte[] index = directory.ToArray();
        output.Write(NativeHash(obj)); output.Write(SHA256.HashData(index)); output.Write(index);
        foreach (IrArchiveRecord record in records) output.Write(record.Payload);
        Section section = new(SectionName, SectionKind.Note); section.Bytes.AddRange(stream.ToArray()); obj.Sections.Add(section);
    }

    public static IrArchive? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Bytes.Count > MaximumBytes) throw new ElfFormatException("Invalid IR archive count/size");
        byte[] bytes = sections[0].Bytes.ToArray();
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream, Utf8);
        try
        {
            if (reader.ReadUInt32() != 0x52494343 || reader.ReadInt32() != 1) throw new ElfFormatException("Unsupported IR archive");
            int count = reader.ReadInt32(), directoryBytes = reader.ReadInt32(), total = reader.ReadInt32();
            byte[] native = reader.ReadBytes(32);
            byte[] directoryHash = reader.ReadBytes(32);
            if (count < 0 || count > 100000 || total != bytes.Length || directoryBytes < 0 || directoryBytes > bytes.Length - 84
                || !NativeHash(obj).SequenceEqual(native)) throw new ElfFormatException("IR archive header/native integrity mismatch");
            if (!SHA256.HashData(bytes.AsSpan(84, directoryBytes)).SequenceEqual(directoryHash))
                throw new ElfFormatException("IR directory integrity mismatch");
            int bodyStart = 84 + directoryBytes, next = bodyStart;
            Dictionary<string, IrArchiveEntry> entries = new(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string key = ReadName(reader);
                byte flags = reader.ReadByte(); int instructions = reader.ReadInt32(), callCount = reader.ReadInt32();
                if (flags > 1 || instructions < 0 || callCount < 0 || callCount > 100000 || callCount > (bodyStart - stream.Position) / 5)
                    throw new ElfFormatException("Invalid IR import summary");
                List<string> calls = new(callCount);
                for (int call = 0; call < callCount; call++) calls.Add(ReadName(reader));
                int offset = reader.ReadInt32(), length = reader.ReadInt32(); byte[] hash = reader.ReadBytes(32);
                if (offset != next - bodyStart || length < 0 || length > bytes.Length - next || hash.Length != 32
                    || stream.Position > bodyStart || !entries.TryAdd(key, new(key, flags != 0, instructions, calls, next, length, hash)))
                    throw new ElfFormatException("Invalid IR body directory");
                next += length;
            }
            if (stream.Position != bodyStart || next != bytes.Length) throw new ElfFormatException("Unclaimed IR directory/payload bytes");
            return new(bytes, entries);
        }
        catch (EndOfStreamException) { throw new ElfFormatException("Truncated IR archive"); }
        catch (DecoderFallbackException) { throw new ElfFormatException("Invalid IR UTF-8"); }
    }

    private static byte[] NativeHash(ObjectFile obj)
    {
        ObjectFile native = new();
        native.Sections.AddRange(obj.Sections.Where(section => section.Name != SectionName));
        native.Symbols.AddRange(obj.Symbols);
        // Standard ELF serialization canonicalizes relocation addends and
        // local/global symbol order, so object round trips preserve the digest.
        return SHA256.HashData(ElfWriter.WriteObject(native));
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        byte[] encoded = Utf8.GetBytes(name);
        if (encoded.Length == 0 || encoded.Length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid IR name");
        writer.Write(encoded.Length); writer.Write(encoded);
    }

    private static string ReadName(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 1 || length > 16384 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new ElfFormatException("Invalid IR name length");
        string name = Utf8.GetString(reader.ReadBytes(length));
        if (name.Contains('\0')) throw new ElfFormatException("Invalid IR name");
        return name;
    }
}
