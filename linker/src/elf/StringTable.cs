#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// An ELF string table: NUL-terminated names, offset 0 the empty string.
/// Duplicates share one entry; the writer is deterministic either way.
/// </summary>
internal sealed class StringTable
{
    // CHUNKS, not a list: an object's .strtab holds every symbol's name, and
    // a large unit's generic and nested names run to megabytes.
    private readonly ChunkedBytes _bytes = new() { 0 };
    private readonly Dictionary<string, uint> _at = new();

    public uint Add(string s)
    {
        if (s.Length == 0)
        {
            return 0;
        }
        if (_at.TryGetValue(s, out uint at))
        {
            return at;
        }
        at = (uint)_bytes.Count;
        _bytes.AddRange(Encoding.UTF8.GetBytes(s));
        _bytes.Add(0);
        _at[s] = at;
        return at;
    }

    public byte[] ToArray()
    {
        return _bytes.ToArray();
    }

    public int Length => _bytes.Count;

    /// <summary>The table written into an ELF image a chunk at a time, never as one array.</summary>
    public void WriteTo(ElfBuffer b)
    {
        for (int i = 0; i < _bytes.SegmentCount; i++)
        {
            (byte[] array, int length) = _bytes.Segment(i);
            b.Bytes(new ReadOnlySpan<byte>(array, 0, length));
        }
    }

    /// <summary>A name from a table held in chunks (ElfReader reads a large unit's so), as Read gives it.</summary>
    public static string Read(ChunkedBytes table, uint offset, string what)
    {
        if (offset >= table.Count)
        {
            throw new ElfFormatException($"{what}: string offset 0x{offset:x} is past the end of its string table");
        }
        int end = table.IndexOf(0, (int)offset, table.Count);
        if (end < 0)
        {
            throw new ElfFormatException($"{what}: unterminated string at 0x{offset:x}");
        }
        return string.Intern(table.GetString(Encoding.UTF8, (int)offset, end - (int)offset));
    }

    public static string Read(ReadOnlySpan<byte> table, uint offset, string what)
    {
        if (offset >= table.Length)
        {
            throw new ElfFormatException($"{what}: string offset 0x{offset:x} is past the end of its string table");
        }
        ReadOnlySpan<byte> rest = table[(int)offset..];
        int end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            throw new ElfFormatException($"{what}: unterminated string at 0x{offset:x}");
        }
        // ONE COPY OF EACH NAME: a link reads the same runtime and library
        // symbols from every object that calls them, and relocations name
        // them again; kept once each, the kernel's names are not its memory.
        return string.Intern(Encoding.UTF8.GetString(rest[..end]));
    }
}
