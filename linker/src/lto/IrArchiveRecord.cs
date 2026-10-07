using System.IO.Compression;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// An opaque compiler payload with linker-readable import costs and calls.
///
/// PACKED WHILE IT WAITS (Packed): a unit's records are taken before its
/// late passes and held, every one, until the object is written after code
/// generation -- tens of megabytes of IR for a library compiled whole, alive
/// through the passes that make the compile's peak. Held deflated, a record
/// is a fraction of that; it is inflated once, as it is written into the
/// archive (IrArchive.Attach), whose bytes are the same either way.
/// `Unpacked` is the body's length when `Payload` is packed (0: it is the
/// body as it is), and `UnpackedHash` the body's SHA-256, which the archive's
/// directory records.
/// </summary>
public sealed record IrArchiveRecord(string Key, bool Importable, int Instructions,
    IReadOnlyList<string> Calls, byte[] Payload, IReadOnlyList<string>? References = null, long DecodeBytes = 0,
    int Unpacked = 0, byte[]? UnpackedHash = null)
{
    /// <summary>Bodies smaller than this are kept as they are: deflating them saves too little.</summary>
    private const int PackFrom = 512;

    /// <summary>A record whose body is held deflated where that makes it smaller.</summary>
    public static IrArchiveRecord Packed(string key, bool importable, int instructions, IReadOnlyList<string> calls, byte[] body,
        IReadOnlyList<string>? references = null, long decodeBytes = 0)
    {
        if (body.Length >= PackFrom)
        {
            // Made at half the body's size, about what it packs to: grown by
            // doubling from nothing, every array it outgrew was the collector's.
            using MemoryStream packed = new(body.Length / 2);
            using (DeflateStream deflate = new(packed, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(body, 0, body.Length);
            if (packed.Length < body.Length)
                return new(key, importable, instructions, calls, packed.ToArray(), references, decodeBytes, body.Length, FastHash.Of(body));
        }
        return new(key, importable, instructions, calls, body, references, decodeBytes);
    }

    /// <summary>The body's length as the archive records it.</summary>
    public int BodyLength => Unpacked != 0 ? Unpacked : Payload.Length;

    /// <summary>The body's hash as the archive's directory records it (FastHash, archive version 4).</summary>
    public byte[] BodyHash() => UnpackedHash ?? FastHash.Of(Payload);

    /// <summary>
    /// The body written to `into` as the archive holds it, inflated where it
    /// is held packed, a buffer's worth at a time: an array the body's size,
    /// made only to be copied into the archive's section and dropped, was a
    /// seventieth of what a native compile left the collector.
    /// </summary>
    public void WriteBodyTo(Stream into)
    {
        if (Unpacked == 0)
        {
            into.Write(Payload, 0, Payload.Length);
            return;
        }
        byte[] buffer = _inflated ??= new byte[64 * 1024];
        using MemoryStream packed = new(Payload, writable: false);
        using DeflateStream inflate = new(packed, CompressionMode.Decompress);
        int done = 0;
        while (done < Unpacked)
        {
            int got = inflate.Read(buffer, 0, Math.Min(buffer.Length, Unpacked - done));
            if (got <= 0) throw new InvalidDataException("IR record " + Key + " shorter than it was");
            into.Write(buffer, 0, got);
            done += got;
        }
    }

    [ThreadStatic] private static byte[]? _inflated;

    /// <summary>The body as it is written into the archive: inflated, where it is held packed.</summary>
    public byte[] Body()
    {
        if (Unpacked == 0) return Payload;
        byte[] body = new byte[Unpacked];
        using MemoryStream packed = new(Payload, writable: false);
        using DeflateStream inflate = new(packed, CompressionMode.Decompress);
        int done = 0;
        while (done < body.Length)
        {
            int got = inflate.Read(body, done, body.Length - done);
            if (got <= 0) throw new InvalidDataException("IR record " + Key + " shorter than it was");
            done += got;
        }
        return body;
    }
}
