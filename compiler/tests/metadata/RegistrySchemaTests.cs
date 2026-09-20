using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

/// <summary>
/// The bytes of a `.corsac.registry` section, checked field by field against
/// docs/software/REGISTRY.md, "The schema section format", in the OS
/// repository. The kernel reads these, so the layout is a contract between
/// two repositories and cannot drift on either side unnoticed.
/// </summary>
public static class RegistrySchemaTests
{
    public static void Run()
    {
        RegistrySchema schema = new() { Domain = "corsac.paint" };

        RegistryEnum units = new() { Name = "Units" };

        units.Members.Add(new RegistryEnumMember { Name = "Banana", Value = 0, Label = "Banana" });
        units.Members.Add(new RegistryEnumMember { Name = "Apple", Value = 5, Label = "Apple" });
        units.Members.Add(new RegistryEnumMember { Name = "Pear", Value = 18, Label = "Pear" });
        schema.Enums.Add(units);

        schema.Entries.Add(new RegistryEntry
        {
            Key = "canvas/width", Label = "Canvas width", Description = "How wide a new drawing starts.",
            Kind = RegistryValueKind.Int, Default = 800,
        });
        schema.Entries.Add(new RegistryEntry
        {
            Key = "canvas/units", Label = "Units", Kind = RegistryValueKind.Enum, Enum = 0, Default = 5,
        });
        schema.Entries.Add(new RegistryEntry
        {
            Key = "window/title", Label = "Title", Kind = RegistryValueKind.String,
            DefaultBytes = System.Text.Encoding.UTF8.GetBytes("Paint"),
        });

        byte[] bytes = schema.Encode();

        Check(Read32(bytes, 0) == RegistrySchema.Magic, "the section starts with CRGS");
        Check(Read16(bytes, 4) == RegistrySchema.Version, "the version is recorded");
        Check(Read16(bytes, 6) == RegistrySchema.HeaderBytes, "the header says how long it is");
        Check(Read32(bytes, 8) == 0, "an open domain has no flags set");

        int strings = (int)Read32(bytes, 40);

        Check(String(bytes, strings, Read32(bytes, 12)) == "corsac.paint", "the domain is named");
        Check(bytes[strings] == 0, "offset zero into the strings is the empty string");

        Check(Read32(bytes, 16) == 3, "every declared key is an entry");
        Check(Read32(bytes, 24) == 1, "the enumeration is emitted once");
        Check(Read32(bytes, 32) == 3, "its members are all there");

        int entries = (int)Read32(bytes, 20);
        int enums = (int)Read32(bytes, 28);
        int members = (int)Read32(bytes, 36);

        Check(entries == RegistrySchema.HeaderBytes, "the entries follow the header");
        Check(enums == entries + 3 * RegistrySchema.EntryBytes, "the enumerations follow the entries");
        Check(members == enums + RegistrySchema.EnumBytes, "the members follow the enumerations");
        Check(strings == members + 3 * RegistrySchema.MemberBytes, "the strings are last");

        // An integer key: its default is the value itself.
        Check(String(bytes, strings, Read32(bytes, entries)) == "canvas/width", "the first key is written");
        Check(String(bytes, strings, Read32(bytes, entries + 4)) == "Canvas width", "so is its label");
        Check(bytes[entries + 12] == (byte)RegistryValueKind.Int, "and its type");
        Check(Read16(bytes, entries + 14) == RegistrySchema.NoEnum, "a key that is not a choice says so");
        Check(Read64(bytes, entries + 16) == 800, "the default is the field's initialiser");

        // An enumeration key: the default is the member's underlying integer,
        // and the entry points at the enumeration rather than repeating it.
        int second = entries + RegistrySchema.EntryBytes;

        Check(bytes[second + 12] == (byte)RegistryValueKind.Enum, "a choice is an enumeration");
        Check(Read16(bytes, second + 14) == 0, "which it names by index");
        Check(Read64(bytes, second + 16) == 5, "and whose member is stored as its number");

        // A string key: the default is bytes in the string table, and the
        // entry carries the length because a binary default may hold a zero.
        int third = second + RegistrySchema.EntryBytes;

        Check(bytes[third + 12] == (byte)RegistryValueKind.String, "a string key says so");
        Check(Read32(bytes, third + 24) == 5, "its default's length is recorded");
        Check(String(bytes, strings, Read32(bytes, third + 16)) == "Paint", "and the default itself");

        // The enumeration's members are the contiguous run beginning at its
        // first member index, in declaration order.
        Check(Read16(bytes, enums + 4) == 0, "the members begin at the start of the table");
        Check(Read16(bytes, enums + 6) == 3, "and there are three of them");
        Check(Read64(bytes, members) == 0, "the first member keeps its value");
        Check(Read64(bytes, members + RegistrySchema.MemberBytes) == 5, "so does the second");
        Check(Read64(bytes, members + 2 * RegistrySchema.MemberBytes) == 18, "and the third");
        Check(String(bytes, strings, Read32(bytes, members + 8)) == "Banana", "a member keeps its name as written");

        // Identical strings are emitted once: a member whose label is its own
        // name is the common case, and a description is the large one.
        Check(Read32(bytes, members + 8) == Read32(bytes, members + 12),
              "a label identical to the name is stored once");

        // A key with no description points at the empty string rather than
        // carrying a flag of its own.
        Check(Read32(bytes, second + 8) == 0, "no description is offset zero");

        RegistrySchema closed = new() { Domain = "corsac.paint", Closed = true };

        Check(Read32(closed.Encode(), 8) == 1, "a closed domain sets its flag");
    }

    static void Check(bool value, string what)
    {
        if (!value)
        {
            throw new Exception("registry schema: " + what);
        }
    }

    static uint Read32(byte[] from, int at)
        => (uint)(from[at] | from[at + 1] << 8 | from[at + 2] << 16 | from[at + 3] << 24);

    static ushort Read16(byte[] from, int at) => (ushort)(from[at] | from[at + 1] << 8);

    static ulong Read64(byte[] from, int at) => Read32(from, at) | (ulong)Read32(from, at + 4) << 32;

    static string String(byte[] from, int strings, uint offset)
    {
        int at = strings + (int)offset;
        int end = at;

        while (end < from.Length && from[end] != 0)
        {
            end++;
        }
        return System.Text.Encoding.UTF8.GetString(from, at, end - at);
    }
}
