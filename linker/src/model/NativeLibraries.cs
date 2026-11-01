#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// The C libraries an object's [DllImport] methods call into, in a section
/// of their own (`.corsac.native`): each name as the declaration wrote it,
/// NUL-terminated. The link reads them and makes the image a dynamic one
/// that needs them; the section itself goes no further.
/// </summary>
public static class NativeLibraries
{
    public const string SectionName = ".corsac.native";

    public static void Attach(ObjectFile obj, IEnumerable<string> libraries)
    {
        List<string> names = libraries.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return;
        }
        Section s = new(SectionName, SectionKind.Note) { Align = 1 };
        foreach (string name in names)
        {
            s.Bytes.AddRange(Encoding.UTF8.GetBytes(name));
            s.Bytes.Add(0);
        }
        obj.Sections.Add(s);
    }

    public static List<string> Read(ObjectFile obj)
    {
        List<string> names = new();
        foreach (Section s in obj.Sections)
        {
            if (s.Name != SectionName)
            {
                continue;
            }
            int start = 0;
            for (int i = 0; i < s.Bytes.Count; i++)
            {
                if (s.Bytes[i] == 0)
                {
                    if (i > start)
                    {
                        names.Add(Encoding.UTF8.GetString(s.Bytes.Slice(start, i - start)));
                    }
                    start = i + 1;
                }
            }
        }
        return names;
    }

    /// <summary>
    /// THE NATIVE NAMES AN OBJECT CALLS (`.corsac.nativesymbols`): each
    /// symbol a [DllImport] binds, a C library's or a DLL's, NUL-terminated.
    /// The loader's to find in whatever library has it, of either format: an
    /// executable's link judges every other import against the COR-C#
    /// libraries it needs (Linker.ExportsAtStart), and these not at all.
    /// </summary>
    public const string SymbolsSectionName = ".corsac.nativesymbols";

    public static void AttachSymbols(ObjectFile obj, IEnumerable<string> symbols)
    {
        List<string> names = symbols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return;
        Section s = new(SymbolsSectionName, SectionKind.Note) { Align = 1 };
        foreach (string name in names)
        {
            s.Bytes.AddRange(Encoding.UTF8.GetBytes(name));
            s.Bytes.Add(0);
        }
        obj.Sections.Add(s);
    }

    public static HashSet<string> ReadSymbols(IEnumerable<ObjectFile> objects)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ObjectFile obj in objects)
            foreach (Section s in obj.Sections)
            {
                if (s.Name != SymbolsSectionName) continue;
                int start = 0;
                for (int i = 0; i < s.Bytes.Count; i++)
                {
                    if (s.Bytes[i] != 0) continue;
                    if (i > start) names.Add(Encoding.UTF8.GetString(s.Bytes.Slice(start, i - start)));
                    start = i + 1;
                }
            }
        return names;
    }
}
