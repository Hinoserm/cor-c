using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>
/// The record types the registry file format has. The schema uses the same
/// codes the log records use, so an entry's type and a stored record's type
/// compare directly with no translation.
/// </summary>
public enum RegistryValueKind : byte
{
    None = 0,
    String = 1,
    Int = 2,
    Bool = 3,
    Binary = 4,
    Enum = 5,
}

/// <summary>One declared key. Its index in the table is the key id.</summary>
public sealed class RegistryEntry
{
    /// <summary>Relative to the domain and lowercase, such as "canvas/width".</summary>
    public string Key = "";
    public string Label = "";
    public string Description = "";
    public RegistryValueKind Kind;

    /// <summary>The key is the prefix of an open subtree, such as "recent/".</summary>
    public bool OpenSubtree;

    /// <summary>Index into the enum table, or -1 when the key is not an enumeration.</summary>
    public int Enum = -1;

    /// <summary>The default for an integer, boolean or enumeration key.</summary>
    public long Default;

    /// <summary>The default for a string or binary key.</summary>
    public byte[]? DefaultBytes;
}

public sealed class RegistryEnum
{
    public string Name = "";
    public bool FlagSet;
    public List<RegistryEnumMember> Members = new();
}

public sealed class RegistryEnumMember
{
    public long Value;

    /// <summary>Case as written: a member name is metadata, not a key.</summary>
    public string Name = "";
    public string Label = "";
    public string Description = "";
}

/// <summary>
/// One domain's declarations, and the bytes they become.
///
/// The format is docs/software/REGISTRY.md, "The schema section format", in
/// the OS repository. It is read by the kernel out of the file rather than
/// out of the running process, which is why the section it lands in is not
/// allocated at load time.
/// </summary>
public sealed class RegistrySchema
{
    public const uint Magic = 0x53475243;          // "CRGS", little-endian
    public const ushort Version = 1;
    public const int HeaderBytes = 64;
    public const int EntryBytes = 32;
    public const int EnumBytes = 16;
    public const int MemberBytes = 24;
    public const ushort NoEnum = 0xFFFF;

    public const string SectionName = ".corsac.registry";

    /// <summary>Lowercase and dotted, such as "corsac.paint".</summary>
    public string Domain = "";

    /// <summary>The domain admits only declared keys.</summary>
    public bool Closed;

    public List<RegistryEntry> Entries = new();
    public List<RegistryEnum> Enums = new();

    public byte[] Encode()
    {
        StringTable strings = new();

        // Offset 0 must be the empty string, so a zero offset reads as
        // "none" with no separate flag beside it.
        strings.Add("");

        // The members of every enumeration, contiguous and in table order:
        // an enumeration's members are the run beginning at its first index.
        List<RegistryEnumMember> members = new();
        int[] firstMember = new int[Enums.Count];

        for (int i = 0; i < Enums.Count; i++)
        {
            firstMember[i] = members.Count;
            members.AddRange(Enums[i].Members);
        }

        // Every string is interned before anything is laid out, because a
        // table entry holds an offset and the offsets have to exist first.
        uint domainAt = strings.Add(Domain);

        foreach (RegistryEntry entry in Entries)
        {
            strings.Add(entry.Key);
            strings.Add(entry.Label);
            strings.Add(entry.Description);
        }

        foreach (RegistryEnum choice in Enums)
        {
            strings.Add(choice.Name);
        }

        foreach (RegistryEnumMember member in members)
        {
            strings.Add(member.Name);
            strings.Add(member.Label);
            strings.Add(member.Description);
        }

        // A string or binary default lives in the string table too, as bytes
        // rather than as text, so an entry stays fixed width.
        Dictionary<RegistryEntry, uint> defaultAt = new();

        foreach (RegistryEntry entry in Entries)
        {
            if (entry.DefaultBytes is not null && entry.DefaultBytes.Length > 0)
            {
                defaultAt[entry] = strings.AddBytes(entry.DefaultBytes);
            }
        }

        int entryTable = HeaderBytes;
        int enumTable = entryTable + Entries.Count * EntryBytes;
        int memberTable = enumTable + Enums.Count * EnumBytes;
        int stringsAt = memberTable + members.Count * MemberBytes;

        strings.Seal();

        byte[] table = strings.Bytes();
        byte[] result = new byte[stringsAt + table.Length];

        Write32(result, 0, Magic);
        Write16(result, 4, Version);
        Write16(result, 6, HeaderBytes);
        Write32(result, 8, Closed ? 1u : 0u);
        Write32(result, 12, domainAt);
        Write32(result, 16, (uint)Entries.Count);
        Write32(result, 20, (uint)entryTable);
        Write32(result, 24, (uint)Enums.Count);
        Write32(result, 28, (uint)enumTable);
        Write32(result, 32, (uint)members.Count);
        Write32(result, 36, (uint)memberTable);
        Write32(result, 40, (uint)stringsAt);
        Write32(result, 44, (uint)table.Length);

        for (int i = 0; i < Entries.Count; i++)
        {
            RegistryEntry entry = Entries[i];
            int at = entryTable + i * EntryBytes;
            bool inline = entry.Kind is RegistryValueKind.String or RegistryValueKind.Binary;

            Write32(result, at + 0, strings.Offset(entry.Key));
            Write32(result, at + 4, strings.Offset(entry.Label));
            Write32(result, at + 8, strings.Offset(entry.Description));
            result[at + 12] = (byte)entry.Kind;
            result[at + 13] = (byte)(entry.OpenSubtree ? 1 : 0);
            Write16(result, at + 14, entry.Enum < 0 ? NoEnum : (ushort)entry.Enum);

            if (inline)
            {
                Write64(result, at + 16, defaultAt.TryGetValue(entry, out uint where) ? where : 0);
                Write32(result, at + 24, (uint)(entry.DefaultBytes?.Length ?? 0));
            }
            else
            {
                Write64(result, at + 16, (ulong)entry.Default);
            }
        }

        for (int i = 0; i < Enums.Count; i++)
        {
            RegistryEnum choice = Enums[i];
            int at = enumTable + i * EnumBytes;

            Write32(result, at + 0, strings.Offset(choice.Name));
            Write16(result, at + 4, (ushort)firstMember[i]);
            Write16(result, at + 6, (ushort)choice.Members.Count);
            Write32(result, at + 8, choice.FlagSet ? 1u : 0u);
        }

        for (int i = 0; i < members.Count; i++)
        {
            RegistryEnumMember member = members[i];
            int at = memberTable + i * MemberBytes;

            Write64(result, at + 0, (ulong)member.Value);
            Write32(result, at + 8, strings.Offset(member.Name));
            Write32(result, at + 12, strings.Offset(member.Label));
            Write32(result, at + 16, strings.Offset(member.Description));
        }

        table.CopyTo(result, stringsAt);
        return result;
    }

    static void Write16(byte[] into, int at, ushort value)
    {
        into[at] = (byte)value;
        into[at + 1] = (byte)(value >> 8);
    }

    static void Write32(byte[] into, int at, uint value)
    {
        into[at] = (byte)value;
        into[at + 1] = (byte)(value >> 8);
        into[at + 2] = (byte)(value >> 16);
        into[at + 3] = (byte)(value >> 24);
    }

    static void Write64(byte[] into, int at, ulong value)
    {
        Write32(into, at, (uint)value);
        Write32(into, at + 4, (uint)(value >> 32));
    }

    /// <summary>
    /// Concatenated, each terminated by a zero byte, and identical strings
    /// emitted once -- descriptions are by far the largest thing in a
    /// section, and repeats between entries are the common case.
    /// </summary>
    sealed class StringTable
    {
        readonly List<byte> _bytes = new();
        readonly Dictionary<string, uint> _at = new(StringComparer.Ordinal);
        bool _sealed;

        public uint Add(string text)
        {
            if (_at.TryGetValue(text, out uint found))
            {
                return found;
            }

            if (_sealed)
            {
                throw new global::System.InvalidOperationException($"registry schema: '{text}' was not interned before layout");
            }

            uint where = (uint)_bytes.Count;

            _bytes.AddRange(Encoding.UTF8.GetBytes(text));
            _bytes.Add(0);
            _at[text] = where;
            return where;
        }

        /// <summary>
        /// A binary default, which is bytes rather than text and so is never
        /// interned against the string keys -- two defaults that happen to
        /// share a spelling are still two values.
        /// </summary>
        public uint AddBytes(byte[] value)
        {
            uint where = (uint)_bytes.Count;

            _bytes.AddRange(value);
            _bytes.Add(0);
            return where;
        }

        /// <summary>
        /// No string may be added once the tables are being laid out: their
        /// offsets are already written into a buffer sized from these bytes.
        /// </summary>
        public void Seal() => _sealed = true;

        public uint Offset(string text) => _at[text];

        public byte[] Bytes() => _bytes.ToArray();
    }
}
