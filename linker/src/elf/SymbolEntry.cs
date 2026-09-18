#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

internal readonly record struct SymbolEntry(
    uint Name,
    uint Value,
    uint Size,
    byte Info,
    byte Other,
    ushort Shndx)
{
    public byte Bind => (byte)(Info >> 4);
    public byte Type => (byte)(Info & 0xf);

    public static byte MakeInfo(byte bind, byte type)
    {
        return (byte)((bind << 4) | (type & 0xf));
    }

    public void WriteTo(ElfBuffer b)
    {
        b.U32(Name);
        b.U32(Value);
        b.U32(Size);
        b.U8(Info);
        b.U8(Other);
        b.U16(Shndx);
    }

    public static SymbolEntry Read(ReadOnlySpan<byte> s)
    {
        return new SymbolEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(s[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            s[12],
            s[13],
            BinaryPrimitives.ReadUInt16LittleEndian(s[14..]));
    }
}
