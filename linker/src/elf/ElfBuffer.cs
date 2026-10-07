#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// A growable little-endian byte sink with the few operations ELF layout needs.
///
/// IN CHUNKS OF 64 KiB, not one list: a List&lt;byte&gt; doubles, copying
/// everything each time, and padding added a zeroed array of its own, so a
/// whole-kernel object briefly needed three times its size. Under the
/// compiler's heap limit (corc.csproj's GCHeapHardLimit) that aborted the
/// kernel-binds compile in the ELF writer now and then. Zeros cost nothing
/// here: a chunk is zero when made, and the length just moves on.
/// </summary>
internal sealed class ElfBuffer
{
    private const int ChunkShift = 16, ChunkBytes = 1 << ChunkShift;
    // A chunk only where something was written: zeros and spliced sections
    // make none.
    private readonly List<byte[]?> _chunks = new();
    private int _length;
    // A FILE-BACKED SECTION SPLICED IN, not copied (Splice): where it lies,
    // and what it is, read from its file as the bytes are written out or
    // hashed. A unit's IR archive, tens of megabytes, went through chunks
    // made for it only to be written out again, and every one of them was
    // the collector's.
    // A section's own bytes, in memory, are spliced the same way
    // (Share): referred to, not copied into chunks made only to be written
    // out once. Source is null for those, Array the bytes.
    private List<(int At, int Length, Section? Source, byte[]? Array)>? _splices;
    // What PatchU32 wrote over shared bytes, laid over them as they are
    // written out: the section's own bytes are left as they were.
    private List<(int At, int Seq, uint Value)>? _patches;
    private bool _patchesSorted = true;

    public int Length => _length;

    /// <summary>The chunk holding byte `offset`, made if it is not yet.</summary>
    private byte[] Chunk(int offset)
    {
        if (_splices is not null && SplicedAt(offset) >= 0) throw new InvalidOperationException($"layout error: 0x{offset:x} is in a spliced section");
        int index = offset >> ChunkShift;
        while (_chunks.Count <= index) _chunks.Add(null);
        return _chunks[index] ??= new byte[ChunkBytes];
    }

    /// <summary>A file-backed section's bytes, here, read from its file when the buffer is written or hashed.</summary>
    public void Splice(Section source)
    {
        if (source.FileBacked is not (_, _, int length)) throw new InvalidOperationException($"section {source.Name}: only a file-backed section is spliced");
        (_splices ??= new()).Add((_length, length, source, null));
        _length = checked(_length + length);
    }

    /// <summary>
    /// `length` bytes of `array` here, referred to rather than copied: the
    /// array must not change until the buffer is written or hashed.
    /// </summary>
    public void Share(byte[] array, int length)
    {
        if (length <= 0) return;
        (_splices ??= new()).Add((_length, length, null, array));
        _length = checked(_length + length);
    }

    /// <summary>The splice holding byte `offset` (they are in order), or -1.</summary>
    private int SplicedAt(int offset)
    {
        List<(int At, int Length, Section? Source, byte[]? Array)> splices = _splices!;
        int lo = 0, hi = splices.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (offset < splices[mid].At) hi = mid - 1;
            else if (offset >= splices[mid].At + splices[mid].Length) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>
    /// Every byte in order, a run at a time: a chunk's, zeros where none was
    /// made, a spliced section's from its file.
    /// </summary>
    private void Each(Action<byte[], int, int> sink)
    {
        byte[]? zeros = null;
        byte[]? piece = null;
        int splice = 0;
        if (_patches is not null && !_patchesSorted)
        {
            _patches.Sort((x, y) => x.At != y.At ? x.At.CompareTo(y.At) : x.Seq.CompareTo(y.Seq));
            _patchesSorted = true;
        }
        int patch = 0, patchCount = _patches?.Count ?? 0;
        int pos = 0;
        while (pos < _length)
        {
            if (_splices is not null && splice < _splices.Count && _splices[splice].At == pos)
            {
                (int _, int length, Section? source, byte[]? array) = _splices[splice++];
                if (source is null)
                {
                    // Shared bytes, with what was patched over them.
                    for (int done = 0; done < length;)
                    {
                        int n = Math.Min(ChunkBytes, length - done);
                        int first = patch;
                        while (patch < patchCount && _patches![patch].At < pos + done + n) patch++;
                        if (first == patch)
                        {
                            sink(array!, done, n);
                        }
                        else
                        {
                            piece ??= new byte[ChunkBytes];
                            Array.Copy(array!, done, piece, 0, n);
                            for (int k = first; k < patch; k++)
                            {
                                (int at, int _, uint value) = _patches![k];
                                for (int byteAt = 0; byteAt < 4; byteAt++)
                                {
                                    int into = at + byteAt - (pos + done);
                                    if (into >= 0 && into < n) piece[into] = (byte)(value >> (8 * byteAt));
                                }
                            }
                            // A patch straddling the piece's end is laid
                            // over the next one too.
                            if (patch > first && _patches![patch - 1].At + 4 > pos + done + n) patch--;
                            sink(piece, 0, n);
                        }
                        done += n;
                    }
                    pos += length;
                    continue;
                }
                using Stream from = source.OpenRead();
                piece ??= new byte[ChunkBytes];
                for (int left = length; left > 0;)
                {
                    int got = from.Read(piece, 0, Math.Min(piece.Length, left));
                    if (got <= 0) throw new IOException($"section {source.Name}: its file ended {left} bytes short");
                    sink(piece, 0, got);
                    left -= got;
                }
                pos += length;
                continue;
            }
            int next = _splices is not null && splice < _splices.Count ? _splices[splice].At : _length;
            int stop = Math.Min(next, ((pos >> ChunkShift) + 1) << ChunkShift);
            int index = pos >> ChunkShift;
            byte[] run = index < _chunks.Count && _chunks[index] is { } made ? made : zeros ??= new byte[ChunkBytes];
            sink(run, pos & (ChunkBytes - 1), stop - pos);
            pos = stop;
        }
    }

    public void U8(byte v)
    {
        Chunk(_length)[_length & (ChunkBytes - 1)] = v;
        _length++;
    }

    public void U16(ushort v)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(s, v);
        Bytes(s);
    }

    public void U64(ulong v)
    {
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(s, v);
        Bytes(s);
    }

    public void U32(uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        Bytes(s);
    }

    public void Bytes(ReadOnlySpan<byte> s)
    {
        while (s.Length > 0)
        {
            int at = _length & (ChunkBytes - 1);
            int n = Math.Min(s.Length, ChunkBytes - at);
            s[..n].CopyTo(Chunk(_length).AsSpan(at, n));
            s = s[n..];
            _length += n;
        }
    }

    /// <summary>
    /// A placed section's bytes moved in, each chunk let go once it is
    /// written: the image and the sections it is made of are never both held
    /// whole, which a link of a large program on a capped heap cannot afford.
    /// </summary>
    public void Take(ChunkedBytes source) => source.MoveTo((chunk, length) => Bytes(new ReadOnlySpan<byte>(chunk, 0, length)));

    public void Zeros(int count)
    {
        if (count < 0) throw new InvalidOperationException($"layout error: {count} zero bytes asked for");
        _length = checked(_length + count);
    }

    /// <summary>Zero-fill up to an offset computed in advance. Overshooting is a layout bug, so it throws.</summary>
    public void PadTo(uint offset)
    {
        if (offset < _length)
        {
            throw new InvalidOperationException($"layout error: at 0x{_length:x}, asked to pad back to 0x{offset:x}");
        }
        Zeros(checked((int)(offset - (uint)_length)));
    }

    public uint AlignTo(uint align)
    {
        uint at = Elf.AlignUp((uint)_length, align);
        PadTo(at);
        return at;
    }

    public void PatchU32(int offset, uint v)
    {
        if (offset < 0 || offset + 4 > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (_splices is not null && SplicedAt(offset) is int k and >= 0)
        {
            // All four bytes shared, though they may run on into the next
            // segment of the section.
            for (int byteAt = 0; byteAt < 4; byteAt++)
                if (SplicedAt(offset + byteAt) is not (int j and >= 0) || _splices[j].Source is not null)
                    throw new InvalidOperationException($"layout error: 0x{offset:x} is in a spliced section");
            _patches ??= new();
            if (_patches.Count > 0 && _patches[^1].At >= offset) _patchesSorted = false;
            _patches.Add((offset, _patches.Count, v));
            return;
        }
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        for (int i = 0; i < 4; i++)
        {
            Chunk(offset + i)[(offset + i) & (ChunkBytes - 1)] = s[i];
        }
    }

    public byte[] ToArray()
    {
        byte[] made = new byte[_length];
        int at = 0;
        Each((run, from, count) => { Array.Copy(run, from, made, at, count); at += count; });
        return made;
    }

    public void PatchU64(int offset, ulong v)
    {
        PatchU32(offset, (uint)v);
        PatchU32(offset + 4, (uint)(v >> 32));
    }

    public void PatchU16(int offset, ushort v)
    {
        if (offset < 0 || offset + 2 > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        Chunk(offset)[offset & (ChunkBytes - 1)] = (byte)v;
        Chunk(offset + 1)[(offset + 1) & (ChunkBytes - 1)] = (byte)(v >> 8);
    }

    /// <summary>
    /// FastHash of the bytes, fed a chunk at a time: what FastHash.Of of
    /// ToArray() gives, without the object ever being one array.
    /// </summary>
    public byte[] Hash()
    {
        FastHash hash = new();
        Each(hash.Append);
        return hash.Finish();
    }

    /// <summary>
    /// The bytes written out a chunk at a time: an object of any size goes to
    /// its file without ever being one array, which on a 32-bit heap is a
    /// contiguous run of address space a large unit's object can fail to get.
    /// </summary>
    public void WriteTo(Stream output) => Each(output.Write);
}
