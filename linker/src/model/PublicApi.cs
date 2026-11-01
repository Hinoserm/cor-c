#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// WHAT A UNIT OFFERS OTHER IMAGES, AS C# SAYS: the symbol of each method and
/// static it defines that is public all the way out -- `public` (or
/// `protected`, for a deriving class) in a type that is public, and its
/// type's too (Lowering.RecordPublicApi) -- and of each that is not:
/// `internal` is its assembly's alone, `private` its type's. The class
/// library's no differently from a program's. A section of its own
/// (`.corsac.public`), each entry `+` (offered, its body there to export),
/// `~` (offered where the link keeps it: a static the link may lay down or
/// fold, a descriptor), `-` (withheld) or `^` (reached: named by the unit's
/// copies of generic code, which another image compiles its own of, and so
/// shown wherever a unit of the image withholds it) and the symbol,
/// NUL-terminated.
///
/// Every image that exports reads it: a kernel's link (--exports) keeps every
/// public one out of line and in its table, however its own callers inlined
/// it; a shared library's and a module's leave every withheld one out of
/// .dynsym (Withheld, from their own units). An image's exports are its
/// public surface, as a .NET library's are, not what the build's other images
/// happened to import, and nothing widens them but its own generic code:
/// List&lt;T&gt; copied into a program for the program's struct calls what
/// List calls, as the library's own code it is, and C# lets it (`^`).
/// </summary>
public static class PublicApi
{
    public const string SectionName = ".corsac.public";

    public static void Attach(ObjectFile obj, IEnumerable<string> offered, IEnumerable<string> optional, IEnumerable<string> withheld, IEnumerable<string> reached)
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
        foreach (string symbol in reached) Add('^', symbol);
    }

    /// <summary>
    /// What a set of units withholds from the image linked of them: a name
    /// one unit withholds and none offers (`+` or `~`) or reaches from its
    /// copies of generic code (`^`) -- the same name is one symbol, and a
    /// unit that offers it shows it.
    /// </summary>
    public static IReadOnlySet<string> Withheld(IEnumerable<ObjectFile> objects)
    {
        HashSet<string> shown = new(StringComparer.Ordinal), hidden = new(StringComparer.Ordinal);
        foreach (ObjectFile obj in objects)
        {
            (List<string> offered, List<string> optional, List<string> withheld, List<string> reached) = Read(obj);
            shown.UnionWith(offered);
            shown.UnionWith(optional);
            shown.UnionWith(reached);
            hidden.UnionWith(withheld);
        }
        hidden.ExceptWith(shown);
        return hidden;
    }

    /// <summary>The symbols an object offers, those it offers where they are kept, those it withholds, and those its copies of generic code reach.</summary>
    public static (List<string> Offered, List<string> Optional, List<string> Withheld, List<string> Reached) Read(ObjectFile obj)
    {
        List<string> offered = new(), optional = new(), withheld = new(), reached = new();
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
                    (s.Bytes[start] switch
                    {
                        (byte)'+' => offered,
                        (byte)'~' => optional,
                        (byte)'-' => withheld,
                        (byte)'^' => reached,
                        // Never a name taken for withheld unsaid: an object this reader cannot read.
                        byte other => throw new Corsac.Lang.Elf.ElfFormatException(SectionName + ": an entry marked '" + (char)other + "', which is none of + ~ - ^"),
                    }).Add(symbol);
                }
                start = i + 1;
            }
        }
        return (offered, optional, withheld, reached);
    }
}
