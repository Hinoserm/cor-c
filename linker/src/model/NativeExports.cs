#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// THE FUNCTIONS AN IMAGE OFFERS TO C, by the names C calls them: each
/// `[UnmanagedCallersOnly(EntryPoint = "...")]` method, as .NET's native AOT
/// exports them. A section of their own (`.corsac.exports`), each entry the
/// export's name and the function's symbol, both NUL-terminated. A Windows
/// DLL's link makes them its export directory; the section itself goes no
/// further.
/// </summary>
public static class NativeExports
{
    public const string SectionName = ".corsac.exports";

    public static void Attach(ObjectFile obj, IEnumerable<(string Export, string Symbol)> exports)
    {
        List<(string Export, string Symbol)> entries = exports.ToList();
        if (entries.Count == 0)
        {
            return;
        }
        Section s = new(SectionName, SectionKind.Note) { Align = 1 };
        foreach ((string export, string symbol) in entries)
        {
            s.Bytes.AddRange(Encoding.UTF8.GetBytes(export));
            s.Bytes.Add(0);
            s.Bytes.AddRange(Encoding.UTF8.GetBytes(symbol));
            s.Bytes.Add(0);
        }
        obj.Sections.Add(s);
    }

    public static List<(string Export, string Symbol)> Read(ObjectFile obj)
    {
        List<(string Export, string Symbol)> entries = new();
        foreach (Section s in obj.Sections)
        {
            if (s.Name != SectionName)
            {
                continue;
            }
            List<string> words = new();
            int start = 0;
            for (int i = 0; i < s.Bytes.Count; i++)
            {
                if (s.Bytes[i] == 0)
                {
                    words.Add(Encoding.UTF8.GetString(s.Bytes.Slice(start, i - start)));
                    start = i + 1;
                }
            }
            for (int i = 0; i + 1 < words.Count; i += 2)
            {
                entries.Add((words[i], words[i + 1]));
            }
        }
        return entries;
    }
}
