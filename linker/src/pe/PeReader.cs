#nullable enable
using System.Buffers.Binary;
using System.Text;

namespace Corsac.Lang.Pe;

/// <summary>
/// What a PE32 DLL exports, by name: the export directory's name table, read
/// through the section table. Enough for a compile against a runtime DLL
/// (--windows --dynamic), which needs only the names the DLL defines, as a
/// compile against a shared object needs only its dynamic symbols
/// (ElfReader.ExportsOf).
/// </summary>
public static class PeReader
{
    /// <summary>Whether the bytes are a PE image: "MZ", then "PE\0\0" where e_lfanew says.</summary>
    public static bool IsPe(byte[] bytes)
        => bytes.Length > 0x40 && bytes[0] == 'M' && bytes[1] == 'Z'
            && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C)) is int pe && pe > 0 && pe + 4 <= bytes.Length
            && bytes[pe] == 'P' && bytes[pe + 1] == 'E' && bytes[pe + 2] == 0 && bytes[pe + 3] == 0;

    /// <summary>The names the DLL exports, in the export directory's order (sorted, as Windows wants).</summary>
    public static List<string> ExportsOf(byte[] bytes)
    {
        if (!IsPe(bytes)) throw new InvalidDataException("not a PE image");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        int coff = pe + 4;
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(coff + 2));
        int optionalBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(coff + 16));
        int optional = coff + PeFormat.CoffHeaderBytes;
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional)) != 0x10B) throw new InvalidDataException("not a PE32 image");
        // The data directories begin 96 bytes into PE32's optional header; the export directory is the first.
        uint exportRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optional + 96));
        List<string> names = new();
        if (exportRva == 0) return names;
        int table = optional + optionalBytes;
        int FileOffset(uint rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = table + i * PeFormat.SectionHeaderBytes;
                uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 8));
                uint address = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 12));
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 20));
                if (rva >= address && rva < address + virtualSize) return checked((int)(raw + (rva - address)));
            }
            throw new InvalidDataException($"RVA 0x{rva:x} is in no section");
        }
        int directory = FileOffset(exportRva);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(directory + 24));
        int nameTable = FileOffset(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directory + 32)));
        for (int i = 0; i < count; i++)
        {
            int at = FileOffset(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(nameTable + i * 4)));
            int end = Array.IndexOf(bytes, (byte)0, at);
            names.Add(Encoding.ASCII.GetString(bytes, at, end - at));
        }
        return names;
    }
}
