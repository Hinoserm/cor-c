#nullable enable
using System.Text;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Ir;

/// <summary>
/// WHAT A MODULE SAYS ABOUT ITSELF, in a note the kernel and the image
/// builder read without running it: `.corsac.modinfo`, a run of
/// `key=value` strings each ended by a NUL, as Linux's .modinfo is.
///
///   name=sb16                    the module, as `depends` and lsmod name it
///   ring=0                       the ring this image of it is for (--ring)
///   alias=pnp:CTL0031            a device it answers to, one each
///   alias=isa:sb16@220,240
///   depends=mpu401               a module that must be loaded first
///
/// The aliases and the dependencies are written in the source, as
/// attributes on the driver's class -- `[ModuleAlias("pnp:CTL0031")]`,
/// `[ModuleDependency("mpu401")]` -- and every unit compiled with one carries
/// its own entries; strings ended by a NUL join up as they are, so the link
/// concatenates the units' notes and then puts the module's own name and
/// ring in front and drops what is said twice. A module linked against
/// another (--link-shared) depends on it whether or not the source said so.
///
/// The text is for the image builder as much as the loader: it collects
/// every module's aliases into /lib/modules/BUILD/aliases, and a bus that
/// finds a device nobody claims looks its alias up there
/// (docs/software/DRIVERS.md in the OS repository, "Matching").
/// </summary>
public static class ModuleInfo
{
    public const string SectionName = ".corsac.modinfo";

    /// <summary>
    /// The symbols of a unit's [ModuleInitializer] methods, NUL-ended: read
    /// and dropped by the link, which calls each when the module is loaded
    /// (Linker.LinkModule). Never in an image.
    /// </summary>
    public const string InitializerSection = ".corsac.modinit";

    /// <summary>A note of <paramref name="entries"/>, each `key=value`.</summary>
    public static Section Note(string name, IEnumerable<string> entries)
    {
        Section note = new(name, SectionKind.Note) { Align = 1 };
        foreach (string entry in entries)
        {
            note.Bytes.AddRange(Encoding.UTF8.GetBytes(entry));
            note.Bytes.Add(0);
        }
        return note;
    }

    /// <summary>The NUL-ended strings of a note's bytes, in order.</summary>
    public static List<string> Entries(ReadOnlySpan<byte> bytes)
    {
        List<string> entries = new();
        while (bytes.Length > 0)
        {
            int end = bytes.IndexOf((byte)0);
            if (end < 0) throw new ElfFormatException("a " + SectionName + " entry is not ended by a NUL");
            if (end > 0) entries.Add(Encoding.UTF8.GetString(bytes[..end]));
            bytes = bytes[(end + 1)..];
        }
        return entries;
    }

    /// <summary>
    /// Every entry the inputs' notes called <paramref name="name"/> hold, in
    /// link order, each once; the notes themselves are taken out of the
    /// inputs, since what is made of them is the link's to write.
    /// </summary>
    public static List<string> Take(IEnumerable<(string Name, ObjectFile Object)> inputs, string name)
    {
        List<string> entries = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((string what, ObjectFile obj) in inputs)
        {
            foreach (Section section in obj.Sections.Where(section => section.Name == name))
            {
                try { foreach (string entry in Entries(section.Content())) if (seen.Add(entry)) entries.Add(entry); }
                catch (ElfFormatException error) { throw new ElfFormatException(what + ": " + error.Message); }
            }
            obj.Sections.RemoveAll(section => section.Name == name);
        }
        return entries;
    }

    /// <summary>The module's name: its file's, without the extension -- sb16.ko is sb16.</summary>
    public static string NameOf(string file)
    {
        string name = Path.GetFileName(file);
        int dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
