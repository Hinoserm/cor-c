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
                        names.Add(Encoding.UTF8.GetString(s.Bytes.GetRange(start, i - start).ToArray()));
                    }
                    start = i + 1;
                }
            }
        }
        return names;
    }
}
