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
    private readonly List<byte[]> _chunks = new();
    private int _length;

    public int Length => _length;

    /// <summary>The chunk holding byte `offset`, made if it is not yet.</summary>
    private byte[] Chunk(int offset)
    {
        int index = offset >> ChunkShift;
        while (_chunks.Count <= index) _chunks.Add(new byte[ChunkBytes]);
        return _chunks[index];
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
        for (int i = 0; i < _chunks.Count && (i << ChunkShift) < _length; i++)
        {
            int from = i << ChunkShift;
            int n = Math.Min(ChunkBytes, _length - from);
            _chunks[i].AsSpan(0, n).CopyTo(made.AsSpan(from, n));
        }
        return made;
    }
}
