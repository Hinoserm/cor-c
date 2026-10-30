#nullable enable

namespace Corsac.Lang.Ir;

/// <summary>
/// What a backend produces from a module: a relocatable object the linker
/// combines into an executable. One per target.
/// </summary>
public sealed class ObjectFile
{
    /// <summary>
    /// Read without its code and data (ElfReader.ReadObjectMetadata), for a
    /// unit backend: its IR archive's hash of them is the link's to check,
    /// which has the whole object, not this reader's.
    /// </summary>
    public bool NativeOmitted { get; set; }

    public List<Section> Sections { get; } = new();
    /// <summary>The file it was read from, unchanged since (ElfReader.ReadObjectFile); null for one made in memory.</summary>
    public string? SourcePath { get; set; }
    /// <summary>
    /// The link's pool of what its units' IR archives say alike, given to
    /// each input it reads (ObjectLinkCommand): whoever reads the archive
    /// first -- the preinit solve, the IR optimiser -- shares through it
    /// (Lto.IrArchive.Read). Null for an object read outside a link.
    /// </summary>
    public Lto.IrArchivePool? ArchivePool { get; set; }
    public List<Symbol> Symbols { get; } = new();
    internal HashSet<string> SuppressedDefinitions { get; } = new(StringComparer.Ordinal);

    public Section Section(string name)
    {
        foreach (Section section in Sections)
        {
            if (section.Name == name)
            {
                return section;
            }
        }
        throw new KeyNotFoundException(name);
    }
}
