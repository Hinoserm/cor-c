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
    private readonly List<byte> _bytes = new() { 0 };
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
        return Encoding.UTF8.GetString(rest[..end]);
    }
}
