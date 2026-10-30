#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A LAYOUT AS A NAME, for a kernel and its modules (KernelExports): what a
/// unit compiled against of another image's types -- a type's fields and
/// slots, a static's type -- said by a symbol whose name carries the layout's
/// fingerprint (ManagedTypeLayout, the record the link already holds units
/// to each other by). A unit for a ring defines one for each type it defines
/// (z_, at a byte of its own); a module's unit imports one for each type of
/// the kernel's, or another module's, it was compiled against. The kernel
/// changed where the module looks is then a name the module imports and the
/// kernel has not: refused at the module's link, and at its load, naming the
/// type. Changed where no module looks, it is the same name, and binds.
///
///   z_&lt;type key&gt;$&lt;fingerprint&gt;          a type's fields, base, interfaces and slots
///   zf_&lt;type key.field&gt;$&lt;fingerprint&gt;  a static field's type
///
/// The key escaped as a descriptor's is (every character but a letter, a
/// digit or `_` as `$` and four hex digits), the fingerprint its first eight
/// bytes in hex.
/// </summary>
public static class LayoutNames
{
    /// <summary>A byte each defining unit has, where its names are.</summary>
    public const string DefinedSection = ".rodata.shapes";
    /// <summary>A word a name a module's unit imports, relocated against it.</summary>
    public const string ImportedSection = ".data.rel.ro.shapes";

    public const string TypePrefix = "z_", StaticPrefix = "zf_";

    /// <summary>The name a layout record is said by, or null for one no image is held to by name (a method's: its label says it).</summary>
    public static string? Of(ManagedTypeLayout layout)
    {
        string prefix;
        string key;
        if (layout.Name.StartsWith("type:", StringComparison.Ordinal)) { prefix = TypePrefix; key = layout.Name[5..]; }
        else if (layout.Name.StartsWith("field:", StringComparison.Ordinal)) { prefix = StaticPrefix; key = layout.Name[6..]; }
        else return null;
        return prefix + Escaped(key) + "$" + Convert.ToHexString(layout.Fingerprint, 0, 8).ToLowerInvariant();
    }

    /// <summary>What a layout name is of, as an error says it: "type Kernel.Driver's layout", "static Kernel.Log.Lines's type"; null for a name that is not one.</summary>
    public static string? Describe(string name)
    {
        bool type = name.StartsWith(TypePrefix, StringComparison.Ordinal), field = name.StartsWith(StaticPrefix, StringComparison.Ordinal);
        int end = name.LastIndexOf('$');
        if (!type && !field || end < 0) return null;
        string escaped = name[(type ? TypePrefix.Length : StaticPrefix.Length)..end];
        StringBuilder key = new();
        for (int at = 0; at < escaped.Length; at++)
        {
            if (escaped[at] == '$' && at + 4 < escaped.Length && int.TryParse(escaped.AsSpan(at + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
            {
                key.Append((char)code);
                at += 4;
            }
            else key.Append(escaped[at]);
        }
        return type ? "type " + key + "'s layout" : "static " + key + "'s type";
    }

    private static string Escaped(string key)
    {
        StringBuilder escaped = new();
        foreach (char c in key)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') escaped.Append(c);
            else escaped.Append('$').Append(((int)c).ToString("x4"));
        }
        return escaped.ToString();
    }

    /// <summary>
    /// A unit's names: <paramref name="defined"/>'s records defined, at one
    /// byte of their own; and, for a module's unit (<paramref name="imports"/>),
    /// every other record imported, a word each relocated against its name,
    /// so the module's link and its loader both bind it or refuse it.
    /// </summary>
    public static void Attach(ObjectFile obj, IEnumerable<ManagedTypeLayout> layouts, IReadOnlySet<string> defined, bool imports, bool wide)
    {
        SortedSet<string> mine = new(StringComparer.Ordinal), theirs = new(StringComparer.Ordinal);
        foreach (ManagedTypeLayout layout in layouts)
            if (Of(layout) is string name) (defined.Contains(layout.Name) ? mine : theirs).Add(name);
        if (mine.Count > 0)
        {
            Section at = new(DefinedSection, SectionKind.ReadOnlyData) { Align = 1 };
            at.Bytes.Add(0);
            obj.Sections.Add(at);
            foreach (string name in mine) obj.Symbols.Add(new Symbol { Name = name, Section = at, Offset = 0, Size = 0 });
        }
        theirs.ExceptWith(mine);
        if (!imports || theirs.Count == 0) return;
        int word = wide ? 8 : 4;
        Section words = new(ImportedSection, SectionKind.Data) { Align = word };
        words.Bytes.AddRange(new byte[theirs.Count * word]);
        int k = 0;
        foreach (string name in theirs)
        {
            words.Relocs.Add(new Relocation(k++ * word, name, 0, wide ? RelocKind.Abs64 : RelocKind.Abs32));
            obj.Symbols.Add(new Symbol { Name = name });
        }
        obj.Sections.Add(words);
    }
}
