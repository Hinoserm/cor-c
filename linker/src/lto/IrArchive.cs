using System.Buffers.Binary;
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
    /// <summary>Each object file an archive is read from, opened once for the process (a link).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle> Handles = new(StringComparer.Ordinal);
    // The archive's bytes, read where they are: a section in memory, or --
    // a unit a link reads (ElfReader.ReadObjectFile) -- the object's file,
    // so that no unit's IR is held whole, only the record being decoded.
    private readonly Func<int, int, byte[]> readAt;
    private readonly int total;
    public IReadOnlyDictionary<string, IrArchiveEntry> Entries { get; }

    private IrArchive(Func<int, int, byte[]> readAt, int total, Dictionary<string, IrArchiveEntry> entries)
    { this.readAt = readAt; this.total = total; Entries = entries; }

    public byte[] ReadBody(string key)
    {
        if (!Entries.TryGetValue(key, out IrArchiveEntry? entry)) throw new ElfFormatException("Missing IR record: " + key);
        if (entry.Offset > total - entry.Length) throw new ElfFormatException("IR archive changed after validation");
        byte[] body = readAt(entry.Offset, entry.Length);
        if (body.Length != entry.Length) throw new ElfFormatException("IR archive changed after validation");
        if (!FastHash.Of(body).SequenceEqual(entry.Hash)) throw new ElfFormatException("IR payload integrity mismatch: " + key);
        return body;
    }

    public static void Attach(ObjectFile obj, IReadOnlyList<IrArchiveRecord> records) => Attach(obj, records, null);

    /// <summary>
    /// The same, LETTING EACH BODY GO AS IT IS COPIED: the list's records
    /// are left with empty payloads. A unit's archive is its whole IR in
    /// bytes -- tens of megabytes for a large one -- and its records held it
    /// beside the section it was being copied into, so the last thing a
    /// compile did was hold its IR twice. A caller that reads nothing of the
    /// records afterwards (Driver.Compile) hands them over.
    /// </summary>
    public static void AttachConsuming(ObjectFile obj, List<IrArchiveRecord> records) => Attach(obj, records, records);

    private static void Attach(ObjectFile obj, IReadOnlyList<IrArchiveRecord> records, List<IrArchiveRecord>? consumed)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate IR archive");
        if (records.Count > 100000) throw new ElfFormatException("Too many IR records");
        // THE DIRECTORY IN CHUNKS too: a large unit's names of calls and
        // references run to megabytes, and a MemoryStream doubled to hold
        // them and then copied them out with ToArray.
        ChunkedBytes index = new();
        using ChunkedBytesWriteStream directory = new(index);
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
            IReadOnlyList<string> references = record.References ?? record.Calls;
            writer.Write(references.Count);
            foreach (string reference in references) WriteName(writer, reference);
            if (record.DecodeBytes < 0) throw new ElfFormatException("Invalid IR decode estimate");
            writer.Write(record.DecodeBytes);
            writer.Write(bodyBytes); writer.Write(record.BodyLength); writer.Write(record.BodyHash());
            bodyBytes = checked(bodyBytes + record.BodyLength);
            if (bodyBytes > MaximumBytes || directory.Length > MaximumBytes - bodyBytes - 84)
                throw new ElfFormatException("IR archive exceeds unit budget");
        }
        // Straight into the section: the header, the directory, then every
        // body. Written through a stream and copied out, a large unit's IR --
        // tens of megabytes -- was held three times over; the section's bytes
        // are chunks now, so nothing here is one large array.
        writer.Flush();
        int total = checked(84 + index.Count + bodyBytes);
        Section section = new(SectionName, SectionKind.Note);
        byte[] header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x52494343u);
        // VERSION 4: the records, the directory and the native object are
        // checked by FastHash, where version 3 used SHA-256 -- a fifth of a
        // native unit compile, and every link and backend read verified them
        // again. An archive of either version is refused by the other.
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), records.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), index.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), total);
        section.Bytes.AddRange(header);
        section.Bytes.AddRange(NativeHash(obj));
        section.Bytes.AddRange(index.Hash());
        section.Bytes.AddRange(index);
        using (ChunkedBytesWriteStream bodies = new(section.Bytes))
        {
            for (int k = 0; k < records.Count; k++)
            {
                records[k].WriteBodyTo(bodies);
                if (consumed is not null) consumed[k] = consumed[k] with { Payload = Array.Empty<byte>() };
            }
        }
        obj.Sections.Add(section);
    }

    public static IrArchive? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Size > MaximumBytes) throw new ElfFormatException("Invalid IR archive count/size");
        Section section = sections[0];
        int size = section.Size;
        // A FILE OPENED ONCE, read by position: a link reads thousands of
        // records, and opening the object again for each was a syscall pair
        // a record. Positional reads need no shared position, so the backend
        // workers can read one archive at once.
        Func<int, int, byte[]> readAt;
        if (section.FileBacked is (string path, long start, int _))
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle handle = Handles.GetOrAdd(path, key => File.OpenHandle(key, FileMode.Open, FileAccess.Read, FileShare.Read));
            readAt = (at, length) =>
            {
                byte[] read = new byte[length];
                int done = 0;
                while (done < length)
                {
                    int got = RandomAccess.Read(handle, read.AsSpan(done), start + at + done);
                    if (got <= 0) throw new ElfFormatException("IR archive truncated in its file");
                    done += got;
                }
                return read;
            };
        }
        else readAt = (at, length) => section.Bytes.Slice(at, length);
        // The header and directory only; the bodies stay where they are.
        if (size < 84) throw new ElfFormatException("Truncated IR archive");
        int indexBytes = BinaryPrimitives.ReadInt32LittleEndian(readAt(12, 4));
        if (indexBytes < 0 || indexBytes > size - 84) throw new ElfFormatException("IR archive header/native integrity mismatch");
        using MemoryStream stream = new(readAt(0, 84 + indexBytes), writable: false);
        using BinaryReader reader = new(stream, Utf8);
        int bytesCount = size;
        try
        {
            if (reader.ReadUInt32() != 0x52494343 || reader.ReadInt32() != 4) throw new ElfFormatException("Unsupported IR archive");
            int count = reader.ReadInt32(), directoryBytes = reader.ReadInt32(), total = reader.ReadInt32();
            byte[] native = reader.ReadBytes(32);
            byte[] directoryHash = reader.ReadBytes(32);
            if (count < 0 || count > 100000 || total != bytesCount || directoryBytes < 0 || directoryBytes > bytesCount - 84
                || !NativeHash(obj).SequenceEqual(native)) throw new ElfFormatException("IR archive header/native integrity mismatch");
            if (!HashDirectory(stream, directoryBytes).SequenceEqual(directoryHash))
                throw new ElfFormatException("IR directory integrity mismatch");
            int bodyStart = 84 + directoryBytes, next = bodyStart;
            Dictionary<string, IrArchiveEntry> entries = new(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string key = ReadName(reader);
                byte flags = reader.ReadByte(); int instructions = reader.ReadInt32(), callCount = reader.ReadInt32();
                if (flags > 1 || instructions < 0 || callCount < 0 || callCount > 100000 || callCount > (bodyStart - stream.Position) / 5)
                    throw new ElfFormatException("Invalid IR import summary");
                // Arrays, the empty one shared, and the references the calls
                // themselves when they are the same names: a link holds every
                // unit's directory -- the compiler's own, 366 thousand records
                // -- from the first phase to the last.
                string[] calls = callCount == 0 ? Array.Empty<string>() : new string[callCount];
                for (int call = 0; call < callCount; call++) calls[call] = ReadName(reader);
                int referenceCount = reader.ReadInt32();
                if (referenceCount < 0 || referenceCount > 100000 || referenceCount > (bodyStart - stream.Position) / 5)
                    throw new ElfFormatException("Invalid IR reference summary");
                string[] references = referenceCount == 0 ? Array.Empty<string>() : new string[referenceCount];
                for (int reference = 0; reference < referenceCount; reference++) references[reference] = ReadName(reader);
                if (references.SequenceEqual(calls)) references = calls;
                long decodeBytes = reader.ReadInt64();
                if (decodeBytes < 0) throw new ElfFormatException("Invalid IR decode estimate");
                int offset = reader.ReadInt32(), length = reader.ReadInt32(); byte[] hash = reader.ReadBytes(32);
                if (offset != next - bodyStart || length < 0 || length > bytesCount - next || hash.Length != 32
                    || stream.Position > bodyStart || !entries.TryAdd(key, new(key, flags != 0, instructions, calls, next, length, hash, references, decodeBytes)))
                    throw new ElfFormatException("Invalid IR body directory");
                next += length;
            }
            if (stream.Position != bodyStart || next != bytesCount) throw new ElfFormatException("Unclaimed IR directory/payload bytes");
            return new(readAt, size, entries);
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
        // Hashed from the chunks the object is built in: a large unit's code
        // made one array here only to be hashed.
        return ElfWriter.HashObject(native);
    }

    private static byte[] HashDirectory(Stream source, int length)
    {
        long start = source.Position;
        FastHash hash = new();
        byte[] buffer = new byte[Math.Min(8192, length)];
        while (length > 0)
        {
            int count = source.Read(buffer, 0, Math.Min(buffer.Length, length));
            if (count == 0) throw new ElfFormatException("Truncated IR directory");
            hash.Append(buffer, 0, count);
            length -= count;
        }
        source.Position = start;
        return hash.Finish();
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        // Encoded into a buffer kept a thread, not an array a name.
        int most = Utf8.GetMaxByteCount(name.Length);
        byte[] buffer = _nameBuffer is { } kept && kept.Length >= most ? kept : _nameBuffer = new byte[Math.Max(most, 256)];
        int length = Utf8.GetBytes(name, 0, name.Length, buffer, 0);
        if (length == 0 || length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid IR name");
        writer.Write(length); writer.Write(buffer, 0, length);
    }

    [ThreadStatic] private static byte[]? _nameBuffer;

    private static string ReadName(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 1 || length > 16384 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new ElfFormatException("Invalid IR name length");
        string name = string.Intern(Utf8.GetString(reader.ReadBytes(length)));
        if (name.Contains('\0')) throw new ElfFormatException("Invalid IR name");
        // Every unit's directory names the same callees: one copy of each.
        return string.Intern(name);
    }
}
