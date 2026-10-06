#nullable enable
using System.Buffers.Binary;
using System.Collections;
using System.Security.Cryptography;

namespace Corsac.Lang.Ir;

/// <summary>
/// A section's content, held in chunks of 64 KiB rather than one array.
///
/// WHY NOT A LIST: the collector does not move objects, so on a capped heap
/// one large array must find a free run of its whole size, and a heap with a
/// third of itself free in smaller holes can still fail it. A List&lt;byte&gt;
/// that a unit's .text grows into doubles to megabytes, and each doubling
/// needs the old array and the new one at once. Here growth past the first
/// chunk only ever asks for another 64 KiB, and nothing is copied.
///
/// SMALL SECTIONS STAY SMALL: most sections (notes, contracts, a handful of
/// constants) are tens of bytes, so the first chunk starts small and doubles
/// up to the chunk size, as a list would; only content past it is chunked.
///
/// Bytes past Count are always zero (Truncate clears what it drops), which is
/// what lets AddZeros just move the count on.
/// </summary>
public sealed class ChunkedBytes : IReadOnlyList<byte>
{
    private const int ChunkShift = 16, ChunkSize = 1 << ChunkShift, ChunkMask = ChunkSize - 1;
    private const int FirstChunk = 16;
    private readonly List<byte[]> _chunks = new();
    private int _count;

    public int Count => _count;

    /// <summary>How many bytes the chunks made so far can hold (Reserve keeps it).</summary>
    private int _capacity;

    /// <summary>Room for `needed` bytes: the first chunk grown, or more chunks made.</summary>
    private void Reserve(int needed)
    {
        if (needed <= _capacity) return;
        if (needed <= ChunkSize)
        {
            int size = _chunks.Count == 0 ? FirstChunk : _chunks[0].Length * 2;
            while (size < needed) size *= 2;
            byte[] grown = new byte[Math.Min(size, ChunkSize)];
            if (_chunks.Count == 0) _chunks.Add(grown);
            else
            {
                Array.Copy(_chunks[0], grown, _count);
                _chunks[0] = grown;
            }
            _capacity = grown.Length;
            return;
        }
        // Past one chunk: the first is made whole, then whole chunks follow.
        if (_chunks.Count == 0) _chunks.Add(new byte[ChunkSize]);
        else if (_chunks[0].Length < ChunkSize)
        {
            byte[] whole = new byte[ChunkSize];
            Array.Copy(_chunks[0], whole, _count);
            _chunks[0] = whole;
        }
        while ((long)_chunks.Count << ChunkShift < needed) _chunks.Add(new byte[ChunkSize]);
        _capacity = _chunks.Count << ChunkShift;
    }

    public byte this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _chunks[index >> ChunkShift][index & ChunkMask];
        }
        set
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            _chunks[index >> ChunkShift][index & ChunkMask] = value;
        }
    }

    public void Add(byte value)
    {
        if (_count == _capacity) Reserve(checked(_count + 1));
        _chunks[_count >> ChunkShift][_count & ChunkMask] = value;
        _count++;
    }

    public void AddRange(byte[] bytes) => AddRange(new ReadOnlySpan<byte>(bytes));

    public void AddRange(ReadOnlySpan<byte> bytes)
    {
        Reserve(checked(_count + bytes.Length));
        while (bytes.Length > 0)
        {
            int at = _count & ChunkMask;
            int n = Math.Min(bytes.Length, _chunks[_count >> ChunkShift].Length - at);
            bytes[..n].CopyTo(_chunks[_count >> ChunkShift].AsSpan(at, n));
            bytes = bytes[n..];
            _count += n;
        }
    }

    public void AddRange(ChunkedBytes other)
    {
        for (int i = 0; i < other.SegmentCount; i++)
        {
            (byte[] array, int length) = other.Segment(i);
            AddRange(new ReadOnlySpan<byte>(array, 0, length));
        }
    }

    public void AddRange(List<byte> bytes) => AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes));

    public void AddRange(IEnumerable<byte> bytes)
    {
        foreach (byte b in bytes) Add(b);
    }

    /// <summary>`count` zero bytes, for padding: free, since past Count is zero.</summary>
    public void AddZeros(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        Reserve(checked(_count + count));
        _count += count;
    }

    /// <summary>
    /// Drops everything from `length` on, zeroing it so that later growth
    /// reads zeros (List.RemoveRange of a tail, which is all the backend does:
    /// it throws a function's code away to encode it again).
    /// </summary>
    public void Truncate(int length)
    {
        if (length < 0 || length > _count) throw new ArgumentOutOfRangeException(nameof(length));
        for (int at = length; at < _count;)
        {
            int n = Math.Min(_count - at, ChunkSize - (at & ChunkMask));
            Array.Clear(_chunks[at >> ChunkShift], at & ChunkMask, n);
            at += n;
        }
        _count = length;
    }

    /// <summary>One byte taken out, the rest moved down (List.RemoveAt).</summary>
    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        for (int i = index; i < _count - 1; i++) this[i] = this[i + 1];
        Truncate(_count - 1);
    }

    /// <summary>Empties the content and lets every chunk go.</summary>
    public void Clear()
    {
        _chunks.Clear();
        _chunks.TrimExcess();
        _count = 0;
        _capacity = 0;
    }

    /// <summary>Bytes [index, index + count) copied into `destination` at `destinationIndex`.</summary>
    public void CopyTo(int index, Span<byte> destination, int count)
    {
        if (index < 0 || count < 0 || index > _count - count || count > destination.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        int done = 0;
        while (done < count)
        {
            int at = index + done;
            int n = Math.Min(count - done, ChunkSize - (at & ChunkMask));
            _chunks[at >> ChunkShift].AsSpan(at & ChunkMask, n).CopyTo(destination.Slice(done, n));
            done += n;
        }
    }

    public void CopyTo(byte[] destination, int destinationIndex) => CopyTo(0, destination.AsSpan(destinationIndex), _count);

    /// <summary>Bytes [index, index + count) as a new array (List.GetRange(..).ToArray()).</summary>
    public byte[] Slice(int index, int count)
    {
        byte[] made = new byte[count];
        CopyTo(index, made, count);
        return made;
    }

    /// <summary>
    /// The whole content as one array. ONE CONTIGUOUS COPY: callers take it
    /// only where an API truly needs an array, as late and as briefly as they
    /// can.
    /// </summary>
    public byte[] ToArray() => Slice(0, _count);

    public void WriteTo(Stream output)
    {
        for (int i = 0; i < SegmentCount; i++)
        {
            (byte[] array, int length) = Segment(i);
            output.Write(array, 0, length);
        }
    }

    /// <summary>How many runs of contiguous bytes the content is held in.</summary>
    public int SegmentCount => (_count + ChunkMask) >> ChunkShift;

    /// <summary>Run `i`: its array and how many of its bytes are content.</summary>
    public (byte[] Chunk, int Length) Segment(int i) => (_chunks[i], Math.Min(_chunks[i].Length, _count - (i << ChunkShift)));

    /// <summary>FastHash of the content, fed a chunk at a time (FastHash.Of of ToArray()).</summary>
    public byte[] Hash()
    {
        FastHash hash = new();
        for (int i = 0; i < SegmentCount; i++)
        {
            (byte[] array, int length) = Segment(i);
            hash.Append(array, 0, length);
        }
        return hash.Finish();
    }

    public int ReadInt32(int offset) => offset >= 0 && (offset & ChunkMask) <= ChunkSize - 4 && offset <= _count - 4
        ? BinaryPrimitives.ReadInt32LittleEndian(_chunks[offset >> ChunkShift].AsSpan(offset & ChunkMask, 4))
        : BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4));

    public void WriteInt32(int offset, int value)
    {
        this[offset] = (byte)value;
        this[offset + 1] = (byte)(value >> 8);
        this[offset + 2] = (byte)(value >> 16);
        this[offset + 3] = (byte)(value >> 24);
    }

    public uint ReadUInt32(int offset) => unchecked((uint)ReadInt32(offset));

    public void WriteUInt32(int offset, uint value) => WriteInt32(offset, unchecked((int)value));

    public long ReadInt64(int offset) => (uint)ReadInt32(offset) | (long)ReadInt32(offset + 4) << 32;

    public void WriteInt64(int offset, long value)
    {
        WriteInt32(offset, (int)value);
        WriteInt32(offset + 4, (int)(value >> 32));
    }

    /// <summary>Bytes [index, index + count) decoded, straight from the chunk when they lie in one.</summary>
    public string GetString(System.Text.Encoding encoding, int index, int count)
    {
        if (index < 0 || count < 0 || index > _count - count) throw new ArgumentOutOfRangeException(nameof(index));
        if ((index & ChunkMask) + count <= ChunkSize) return encoding.GetString(_chunks[index >> ChunkShift], index & ChunkMask, count);
        return encoding.GetString(Slice(index, count));
    }

    /// <summary>Where `value` next is in [start, end), or -1.</summary>
    public int IndexOf(byte value, int start, int end)
    {
        if (start < 0 || end > _count) throw new ArgumentOutOfRangeException(nameof(start));
        for (int at = start; at < end;)
        {
            int n = Math.Min(end - at, ChunkSize - (at & ChunkMask));
            int found = Array.IndexOf(_chunks[at >> ChunkShift], value, at & ChunkMask, n);
            if (found >= 0) return (at & ~ChunkMask) + found;
            at += n;
        }
        return -1;
    }

    /// <summary>
    /// The content moved to a new instance, chunks and all, and this one left
    /// empty: a link takes a section's bytes this way rather than copying
    /// them (Section.HandOver).
    /// </summary>
    public ChunkedBytes TakeAll()
    {
        ChunkedBytes taken = new();
        taken._chunks.AddRange(_chunks);
        taken._count = _count;
        taken._capacity = _capacity;
        Clear();
        return taken;
    }

    /// <summary>A copy, in chunks of its own.</summary>
    public ChunkedBytes Clone()
    {
        ChunkedBytes copy = new();
        copy.AddRange(this);
        return copy;
    }

    /// <summary>
    /// Each run handed to `write` and let go as soon as it is written, then
    /// the whole emptied: content copied out this way is never held twice,
    /// for longer than one chunk.
    /// </summary>
    public void MoveTo(Action<byte[], int> write)
    {
        for (int i = 0; i < SegmentCount; i++)
        {
            (byte[] chunk, int length) = Segment(i);
            write(chunk, length);
            _chunks[i] = Array.Empty<byte>();
        }
        Clear();
    }

    /// <summary>
    /// `count` bytes appended from a file at `offset`, read straight into the
    /// chunks: an object's sections come in without the file ever being one
    /// array (ElfReader.ReadObjectFile).
    /// </summary>
    public void AddFromFile(Microsoft.Win32.SafeHandles.SafeFileHandle file, long offset, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        Reserve(checked(_count + count));
        int done = 0;
        while (done < count)
        {
            int at = _count & ChunkMask;
            int n = Math.Min(count - done, _chunks[_count >> ChunkShift].Length - at);
            Span<byte> into = _chunks[_count >> ChunkShift].AsSpan(at, n);
            int filled = 0;
            while (filled < n)
            {
                int got = RandomAccess.Read(file, into[filled..], offset + done + filled);
                if (got <= 0) throw new EndOfStreamException("file ends before its section does");
                filled += got;
            }
            done += n;
            _count += n;
        }
    }

    /// <summary>A struct, as List's is, so a foreach over the bytes allocates nothing.</summary>
    public struct Enumerator : IEnumerator<byte>
    {
        private readonly ChunkedBytes _bytes;
        private int _at;
        internal Enumerator(ChunkedBytes bytes) { _bytes = bytes; _at = -1; }
        public byte Current => _bytes._chunks[_at >> ChunkShift][_at & ChunkMask];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_at < _bytes._count;
        public void Reset() { _at = -1; }
        public void Dispose() { }
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<byte> IEnumerable<byte>.GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);
}
