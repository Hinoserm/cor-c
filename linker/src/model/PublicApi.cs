#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// WHAT A RING UNIT OFFERS OTHER IMAGES, AS C# SAYS: the symbol of each method
/// and static it defines that is public all the way out -- `public` (or
/// `protected`, for a module's class deriving from one) in a type that is
/// public, and its type's too (Lowering.RecordPublicApi) -- and, of the
/// program's own types, of each that is not: `internal` is the kernel's
/// assembly's alone, `private` its type's.
/// A section of its own (`.corsac.public`), each entry `+` (offered, its body
/// there to export), `~` (offered where the link keeps it: a static the link
/// may lay down or fold, the class library's surface) or `-` (withheld) and
/// the symbol, NUL-terminated.
///
/// A kernel linked with --exports=public keeps every public one out of line and in
/// its export table, however its own callers inlined it, and leaves every
/// other one out of the table, free to be inlined and pruned
/// (ObjectLinkCommand): the module ABI is the kernel's public surface, as a
/// .NET library's is, not what the build's own modules happened to import.
/// </summary>
public static class PublicApi
{
    public const string SectionName = ".corsac.public";

    public static void Attach(ObjectFile obj, IEnumerable<string> offered, IEnumerable<string> optional, IEnumerable<string> withheld)
    {
        Section? s = null;
        void Add(char mark, string symbol)
        {
            if (s is null) { s = new(SectionName, SectionKind.Note) { Align = 1 }; obj.Sections.Add(s); }
            s.Bytes.Add((byte)mark);
            s.Bytes.AddRange(Encoding.UTF8.GetBytes(symbol));
            s.Bytes.Add(0);
        }
        foreach (string symbol in offered) Add('+', symbol);
        foreach (string symbol in optional) Add('~', symbol);
        foreach (string symbol in withheld) Add('-', symbol);
    }

    /// <summary>The symbols an object offers, those it offers where they are kept, and those it withholds.</summary>
    public static (List<string> Offered, List<string> Optional, List<string> Withheld) Read(ObjectFile obj)
    {
        List<string> offered = new(), optional = new(), withheld = new();
        foreach (Section s in obj.Sections)
        {
            if (s.Name != SectionName) continue;
            int start = 0;
            for (int i = 0; i < s.Bytes.Count; i++)
            {
                if (s.Bytes[i] != 0) continue;
                if (i > start)
                {
                    string symbol = Encoding.UTF8.GetString(s.Bytes.Slice(start + 1, i - start - 1));
                    (s.Bytes[start] switch { (byte)'+' => offered, (byte)'~' => optional, _ => withheld }).Add(symbol);
                }
                start = i + 1;
            }
        }
        return (offered, optional, withheld);
    }
}
