using Corsac.Lang.Ir;

namespace Corsac.Lang.Metadata;

public static class IrDataCodec
{
    public static byte[] Write(DataItem item)
    {
        if (item.Zero && item.Bytes.Any(value => value != 0)) throw new InvalidDataException("Nonzero bytes in zero-filled IR data");
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, IrBinary.Utf8, leaveOpen: true);
        writer.Write(1); IrBinary.Text(writer, item.Name); writer.Write(item.Align);
        writer.Write(item.ReadOnly); writer.Write(item.Zero); writer.Write(item.Exported);
        writer.Write(item.FromLibrary); writer.Write(item.Coalescible);
        writer.Write(item.Bytes.Length);
        if (!item.Zero) writer.Write(item.Bytes);
        writer.Write(item.Relocs.Count);
        foreach (DataReloc relocation in item.Relocs)
        { writer.Write(relocation.Offset); IrBinary.Text(writer, relocation.Symbol); writer.Write(relocation.Addend); }
        return stream.ToArray();
    }

    public static DataItem Read(byte[] payload, long maximumBytes = 64L * 1024 * 1024, IrReadBudget? budget = null)
    {
        budget ??= new();
        budget.Charge(256L + payload.Length, 1, "data payload");
        using MemoryStream stream = new(payload, writable: false);
        using BinaryReader reader = new(stream, IrBinary.Utf8);
        try
        {
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported IR data version");
            string name = IrBinary.Name(reader, budget); int align = reader.ReadInt32();
            bool readOnly = IrBinary.Flag(reader), zero = IrBinary.Flag(reader), exported = IrBinary.Flag(reader);
            bool library = IrBinary.Flag(reader), coalescible = IrBinary.Flag(reader);
            int size = reader.ReadInt32();
            if (align < 1 || align > 4096 || (align & (align - 1)) != 0 || size < 0 || size > maximumBytes
                || !zero && size > stream.Length - stream.Position) throw new InvalidDataException("Invalid IR data layout");
            budget.Charge(size, 1, "data bytes");
            byte[] bytes = zero ? new byte[size] : reader.ReadBytes(size);
            DataItem item = new(name, bytes) { Align = align, ReadOnly = readOnly, Zero = zero, Exported = exported, FromLibrary = library, Coalescible = coalescible };
            int relocations = IrBinary.Count(reader);
            budget.Charge(relocations, 48, "data relocations");
            for (int i = 0; i < relocations; i++)
            {
                int offset = reader.ReadInt32(); string symbol = IrBinary.Name(reader, budget); long addend = reader.ReadInt64();
                if (zero || offset < 0 || offset > size - 4) throw new InvalidDataException("Invalid IR data relocation");
                item.Relocs.Add(new(offset, symbol, addend));
            }
            IrBinary.End(reader); return item;
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Truncated IR data"); }
        catch (System.Text.DecoderFallbackException) { throw new InvalidDataException("Invalid IR UTF-8"); }
    }
}
