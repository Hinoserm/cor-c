using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>Immutable disk-backed sorted index; lookup never loads its offset table.</summary>
public sealed class DeclarationIndex : IDisposable
{
    internal const int HeaderSize = 32;
    internal const int MaxKeyBytes = 4096;
    internal const int MaxPayloadBytes = 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly FileStream stream;
    private readonly BinaryReader reader;
    private readonly long table;
    private readonly object gate = new();
    public long Count { get; }

    public DeclarationIndex(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        reader = new BinaryReader(stream, Utf8, leaveOpen: true);
        try
        {
            if (stream.Length < HeaderSize || reader.ReadUInt32() != 0x58494443 || reader.ReadUInt32() != 3)
                throw new InvalidDataException("Unsupported declaration index");
            Count = reader.ReadInt64();
            table = reader.ReadInt64();
            long length = reader.ReadInt64();
            if (Count < 0 || table < HeaderSize || table > length || length != stream.Length
                || Count > (length - table) / 8 || Count * 8 != length - table)
                throw new InvalidDataException("Invalid declaration index directory");

            // THE DIRECTORY IS READ ONCE. Every record read used to seek to
            // the directory for its start and the next one's, then seek back
            // to the record -- three seeks and three reads for one record,
            // and a binary search does that for every probe. Compiling the
            // kernel made 1.1 million pread calls, which is most of the
            // reading this compiler does. The directory is eight bytes a
            // record and does not change while the file is open.
            offsets = new long[Count + 1];
            stream.Position = table;
            byte[] raw = new byte[checked((int)(Count * 8))];
            stream.ReadExactly(raw);
            for (long i = 0; i < Count; i++) offsets[i] = BitConverter.ToInt64(raw, checked((int)(i * 8)));
            offsets[Count] = table;
        }
        catch { reader.Dispose(); stream.Dispose(); throw; }
    }

    /// <summary>Where each record starts, and where the last one ends.</summary>
    private readonly long[] offsets = Array.Empty<long>();

    public IEnumerable<DeclarationRecord> Find(string key) => Range(key, false);
    public IEnumerable<DeclarationRecord> WithPrefix(string prefix) => Range(prefix, true);

    private IEnumerable<DeclarationRecord> Range(string key, bool prefix)
    {
        ArgumentNullException.ThrowIfNull(key);
        long first;
        lock (gate)
        {
            long low = 0, high = Count;
            while (low < high)
            {
                long middle = low + (high - low) / 2;
                string found = Read(middle, payload: false).Key;
                if (StringComparer.Ordinal.Compare(found, key) < 0) low = middle + 1;
                else high = middle;
            }
            first = low;
        }
        for (long i = first; i < Count; i++)
        {
            DeclarationRecord record;
            lock (gate)
            {
                record = Read(i, payload: false);
                if (prefix ? !record.Key.StartsWith(key, StringComparison.Ordinal) : record.Key != key) yield break;
                record = Read(i, payload: true);
            }
            yield return record;
        }
    }

    private DeclarationRecord Read(long number, bool payload)
    {
        long start = offsets[number], end = offsets[number + 1];
        if (start < HeaderSize || end < start || end > table || end - start < 72)
            throw new InvalidDataException("Invalid declaration record span");
        stream.Position = start;
        int keyLength = reader.ReadInt32(), size = reader.ReadInt32();
        CheckLengths(keyLength, size);
        if (72L + keyLength + size != end - start) throw new InvalidDataException("Declaration record length mismatch");
        byte[] keyDigest = ReadBytes(reader, 32);
        byte[] digest = ReadBytes(reader, 32);
        byte[] keyBytes = ReadBytes(reader, keyLength);
        if (!SHA256.HashData(keyBytes).SequenceEqual(keyDigest)) throw new InvalidDataException("Declaration key checksum mismatch");
        string key = Utf8.GetString(keyBytes);
        if (key.IndexOf('\0') >= 0) throw new InvalidDataException("NUL in declaration key");
        if (!payload) return new DeclarationRecord(key, Array.Empty<byte>());
        byte[] bytes = ReadBytes(reader, size);
        if (!Digest(keyBytes, bytes).SequenceEqual(digest)) throw new InvalidDataException("Declaration record checksum mismatch");
        return new DeclarationRecord(key, bytes);
    }

    internal static void CheckLengths(int key, int payload)
    {
        if (key <= 0 || key > MaxKeyBytes || payload < 0 || payload > MaxPayloadBytes)
            throw new InvalidDataException("Declaration record exceeds format limits");
    }

    internal static byte[] ReadBytes(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new InvalidDataException("Truncated declaration record");
        return bytes;
    }

    internal static byte[] Digest(byte[] key, byte[] payload)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(key);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    public void Dispose() { reader.Dispose(); stream.Dispose(); }
}
