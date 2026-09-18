#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

internal readonly record struct SectionHeader(
    uint Name,
    uint Type,
    uint Flags,
    uint Addr,
    uint Offset,
    uint Size,
    uint Link,
    uint Info,
    uint AddrAlign,
    uint EntSize)
{
    public void WriteTo(ElfBuffer b)
    {
        b.U32(Name);
        b.U32(Type);
        b.U32(Flags);
        b.U32(Addr);
        b.U32(Offset);
        b.U32(Size);
        b.U32(Link);
        b.U32(Info);
        b.U32(AddrAlign);
        b.U32(EntSize);
    }

    public static SectionHeader Read(ReadOnlySpan<byte> s)
    {
        return new SectionHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(s[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[36..]));
    }
}
