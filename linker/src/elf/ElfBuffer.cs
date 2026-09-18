#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>A growable little-endian byte sink with the few operations ELF layout needs.</summary>
internal sealed class ElfBuffer
{
    private readonly List<byte> _bytes = new();

    public int Length => _bytes.Count;

    public void U8(byte v)
    {
        _bytes.Add(v);
    }

    public void U16(ushort v)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(s, v);
        _bytes.AddRange(s);
    }

    public void U32(uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        _bytes.AddRange(s);
    }

    public void Bytes(ReadOnlySpan<byte> s)
    {
        _bytes.AddRange(s);
    }

    public void Zeros(int count)
    {
        _bytes.AddRange(new byte[count]);
    }

    /// <summary>Zero-fill up to an offset computed in advance. Overshooting is a layout bug, so it throws.</summary>
    public void PadTo(uint offset)
    {
        if (offset < _bytes.Count)
        {
            throw new InvalidOperationException($"layout error: at 0x{_bytes.Count:x}, asked to pad back to 0x{offset:x}");
        }
        Zeros((int)(offset - _bytes.Count));
    }

    public uint AlignTo(uint align)
    {
        uint at = Elf.AlignUp((uint)_bytes.Count, align);
        PadTo(at);
        return at;
    }

    public void PatchU32(int offset, uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        for (int i = 0; i < 4; i++)
        {
            _bytes[offset + i] = s[i];
        }
    }

    public byte[] ToArray()
    {
        return _bytes.ToArray();
    }
}
