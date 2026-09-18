#nullable enable
using System.Buffers.Binary;
using System.Text;

namespace Corsac.Lang;

/// <summary>
/// The type table: what types an image contains.
///
/// REFLECTION TIER 1 (see docs/image-format.md, section 7). Asking an
/// OBJECT what type it is needs none of this -- every object carries its vtable
/// pointer at offset 0 and the descriptor sits immediately in front of the
/// vtable, so GetType() is two instructions. What this adds is the other
/// direction: asking the image what types exist at all.
///
/// It is also where tiers 2 and 3 go. Fields and methods by name are more rows
/// in this section; an invoke thunk per signature shape is a code address in a
/// row here. Defining the section now, with one row shape, is what makes those
/// additions rather than another format change.
///
/// STRIPPABLE, and that is a rule rather than a nicety: a 300 MB core store is
/// roomy and a small machine is not. Dropping it must fail loudly at whoever
/// reflects, not quietly return nothing.
/// </summary>
public static class Meta
{
    /// <summary>Bumped when a row changes shape and old readers must refuse.</summary>
    public const ushort Major = 1;

    /// <summary>Bumped when a field is APPENDED that an old reader can ignore.</summary>
    public const ushort Minor = 0;

    private static ReadOnlySpan<byte> Magic => "META"u8;

    /// <summary>One row: a type, as the image records it.</summary>
    public readonly record struct Row(string Name, int TypeId, int InstanceSize, long Descriptor);

    private const int RowBytes = 24;
    private const int HeaderBytes = 16;

    public static byte[] Write(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // The names go in a blob after the rows, so a row is a fixed size and
        // the table can be walked without reading a single name -- which is
        // what a reader looking for one type wants to do.
        List<byte> names = new();
        List<int> at = new(rows.Count);

        foreach (Row r in rows)
        {
            at.Add(names.Count);
            names.AddRange(Encoding.UTF8.GetBytes(r.Name));
            names.Add(0);
        }

        byte[] to = new byte[HeaderBytes + rows.Count * RowBytes + names.Count];
        Span<byte> s = to;

        Magic.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], Major);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], Minor);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], rows.Count);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], HeaderBytes + rows.Count * RowBytes);

        for (int i = 0; i < rows.Count; i++)
        {
            Span<byte> row = s.Slice(HeaderBytes + i * RowBytes, RowBytes);

            BinaryPrimitives.WriteInt32LittleEndian(row, at[i]);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], rows[i].TypeId);
            BinaryPrimitives.WriteInt32LittleEndian(row[8..], rows[i].InstanceSize);
            BinaryPrimitives.WriteInt64LittleEndian(row[12..], rows[i].Descriptor);
        }

        names.CopyTo(to, HeaderBytes + rows.Count * RowBytes);
        return to;
    }

    public static List<Row> Read(ReadOnlySpan<byte> bytes, string from)
    {
        List<Row> found = new();

        if (bytes.Length == 0)
        {
            return found;                       // stripped, or nothing to say
        }

        if (bytes.Length < HeaderBytes || !bytes[..4].SequenceEqual(Magic))
        {
            throw new AsmException(0, $"{from}: not a type table");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);

        if (major != Major)
        {
            throw new AsmException(0,
                $"{from}: the type table is format {major}, and this toolchain reads {Major}");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        int names = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);

        // COUNTS OFF A DISC ARE NOT BELIEVED, the same rule the rest of the
        // image reader follows. A damaged count would otherwise walk off the
        // end of the section and report itself as an index out of range.
        if (count < 0 || names < 0 || names > bytes.Length
            || (long)HeaderBytes + (long)count * RowBytes > bytes.Length)
        {
            throw new AsmException(0, $"{from}: the type table claims {count} types and cannot hold them");
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = bytes.Slice(HeaderBytes + i * RowBytes, RowBytes);
            int nameAt = BinaryPrimitives.ReadInt32LittleEndian(row);

            if (nameAt < 0 || names + nameAt >= bytes.Length)
            {
                throw new AsmException(0, $"{from}: a type name in the table is outside it");
            }

            int end = names + nameAt;

            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            found.Add(new Row(
                Encoding.UTF8.GetString(bytes[(names + nameAt)..end]),
                BinaryPrimitives.ReadInt32LittleEndian(row[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(row[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(row[12..])));
        }
        return found;
    }
}
