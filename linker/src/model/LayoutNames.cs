#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A LAYOUT AS A NAME, for a kernel and its modules (KernelExports): what a
/// unit compiled against of another image's types -- a type's fields, base,
/// interfaces and slots -- said by a symbol whose name carries the layout's
/// fingerprint (ManagedTypeLayout, the record the link already holds units
/// to each other by). A static is not in it: its own symbol says its type
/// (Lowering.StaticSymbol), as a method's label says its signature.
///
///   z_&lt;type key&gt;$&lt;fingerprint&gt;
///
/// the key escaped as a descriptor's is (every character but a letter, a
/// digit or `_` as `$` and four hex digits), the fingerprint the first eight
/// bytes of the type's record's. A unit
/// for a ring defines one for each type it defines, at a byte of its own; a
/// module's unit imports one for each type it read from an index -- the
/// kernel's, or its own module's other units', or a module's it depends on.
/// The kernel changed where the module looks is then a name the module
/// imports and the kernel has not: refused at the module's link, and at its
/// load, naming the type. Changed where no module looks, it is the same
/// name, and binds.
///
/// A CLOSED INSTANTIATION of a generic type -- `List<Driver>`, keyed as the
/// monomorphiser names its copy, `List$Driver` -- has a name of its own, its
/// fingerprint its own layout's with its value-type arguments' folded in
/// (ManagedLayouts.Capture). Every unit that makes one makes it alike, and
/// none is its definition: every kernel unit that makes it defines its name,
/// the link keeping one (Linker, DefinedSection), and a module's unit imports
/// it where the kernel's file has a name for that instantiation at all -- one
/// the kernel never made is the module's alone, and binds to nothing.
/// </summary>
public static class LayoutNames
{
    /// <summary>A byte each defining unit has, where its names are.</summary>
    public const string DefinedSection = ".rodata.shapes";
    /// <summary>A word a name a module's unit imports, relocated against it.</summary>
    public const string ImportedSection = ".data.rel.ro.shapes";

    public const string Prefix = "z_";

    /// <summary>Each type's name, by its key, from its "type:" record; the others are said by their own symbols.</summary>
    public static Dictionary<string, string> Of(IEnumerable<ManagedTypeLayout> layouts)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (ManagedTypeLayout layout in layouts)
            if (layout.Name.StartsWith("type:", StringComparison.Ordinal))
                names[layout.Name[5..]] = Prefix + Escaped(layout.Name[5..]) + "$" + Convert.ToHexString(layout.Fingerprint, 0, 8).ToLowerInvariant();
        return names;
    }

    /// <summary>A layout name's escaped key, its fingerprint cut off: what two images' names of one type share.</summary>
    public static string KeyOf(string name) => name[..^17];

    /// <summary>What a layout name is of, as an error says it: "type Kernel.Driver's layout"; null for a name that is not one.</summary>
    public static string? Describe(string name)
    {
        int end = name.LastIndexOf('$');
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || end < 0 || name.Length - end != 17
            || !name.AsSpan(end + 1).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        string escaped = name[Prefix.Length..end];
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
        return "type " + key + "'s layout";
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
    /// byte of their own; and, for a module's unit, <paramref name="imported"/>'s
    /// -- what it read of another unit's, the kernel's or its own module's --
    /// imported, a word each relocated against its name, so the module's link
    /// and its loader both bind it or refuse it. Its closed
    /// <paramref name="instances"/> defined by a kernel's unit
    /// (<paramref name="kernelHas"/> null), and imported by a module's where
    /// the kernel has a name of that key.
    /// </summary>
    public static void Attach(ObjectFile obj, IEnumerable<ManagedTypeLayout> layouts, IReadOnlySet<string> defined, IReadOnlySet<string>? imported,
        IEnumerable<ManagedTypeLayout> instances, IReadOnlySet<string>? kernelHas, bool wide)
    {
        SortedSet<string> mine = new(StringComparer.Ordinal), theirs = new(StringComparer.Ordinal);
        foreach ((string type, string name) in Of(layouts))
        {
            if (defined.Contains("type:" + type)) mine.Add(name);
            else if (imported?.Contains("type:" + type) == true) theirs.Add(name);
        }
        foreach (string name in Of(instances).Values)
        {
            if (kernelHas is null) mine.Add(name);
            else if (kernelHas.Contains(KeyOf(name))) theirs.Add(name);
        }
        if (mine.Count > 0)
        {
            Section at = new(DefinedSection, SectionKind.ReadOnlyData) { Align = 1 };
            at.Bytes.Add(0);
            obj.Sections.Add(at);
            foreach (string name in mine) obj.Symbols.Add(new Symbol { Name = name, Section = at, Offset = 0, Size = 0 });
        }
        theirs.ExceptWith(mine);
        if (theirs.Count == 0) return;
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
