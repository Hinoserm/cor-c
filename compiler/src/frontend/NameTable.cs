#nullable enable

namespace Corsac.Lang;

/// <summary>
/// ONE STRING PER NAME, looked up by its span of the source without cutting it
/// out first -- Roslyn's StringTable, for the same reason. A token's text was a
/// fresh substring every time: a unit's heap held `public` eight thousand times
/// and `int`, `return` and `if` as many again, most of a unit's string bytes.
///
/// A table fills up and is then only read, or is written by one lexer alone:
/// the keyword table is complete before any lexer runs, and every lexer has
/// its own table for the names of its file.
/// </summary>
internal sealed class NameTable
{
    private string?[] _slots;
    private int _count;
    private readonly NameTable? _shared;

    public NameTable(int capacity = 256, NameTable? shared = null)
    {
        int size = 16;
        while (size < capacity * 2) size <<= 1;
        _slots = new string?[size];
        _shared = shared;
    }

    /// <summary>The table's string for these characters of <paramref name="source"/>, made on first sight.</summary>
    public string Get(string source, int start, int length)
    {
        uint hash = Hash(source, start, length);
        if (_shared?.Find(source, start, length, hash) is string known) return known;
        int mask = _slots.Length - 1;
        for (int at = (int)hash & mask; ; at = (at + 1) & mask)
        {
            string? held = _slots[at];
            if (held is null)
            {
                string made = source.Substring(start, length);
                _slots[at] = made;
                if (++_count * 2 > _slots.Length) Grow();
                return made;
            }
            if (Same(held, source, start, length)) return held;
        }
    }

    /// <summary>Takes <paramref name="name"/> as the table's string for its text.</summary>
    public void Add(string name) => Get(name, 0, name.Length);

    private string? Find(string source, int start, int length, uint hash)
    {
        int mask = _slots.Length - 1;
        for (int at = (int)hash & mask; ; at = (at + 1) & mask)
        {
            string? held = _slots[at];
            if (held is null) return null;
            if (Same(held, source, start, length)) return held;
        }
    }

    private void Grow()
    {
        string?[] old = _slots;
        _slots = new string?[old.Length * 2];
        int mask = _slots.Length - 1;
        foreach (string? name in old)
        {
            if (name is null) continue;
            int at = (int)Hash(name, 0, name.Length) & mask;
            while (_slots[at] is not null) at = (at + 1) & mask;
            _slots[at] = name;
        }
    }

    private static uint Hash(string source, int start, int length)
    {
        uint hash = 2166136261;
        for (int i = start; i < start + length; i++) hash = (hash ^ source[i]) * 16777619;
        return hash ^ (hash >> 15);
    }

    private static bool Same(string held, string source, int start, int length)
    {
        if (held.Length != length) return false;
        for (int i = 0; i < length; i++)
        {
            if (held[i] != source[start + i]) return false;
        }
        return true;
    }
}
