#nullable enable

namespace Corsac.Lang.Ir;

public sealed class Section
{
    public string Name { get; }
    public SectionKind Kind { get; }
    public List<byte> Bytes { get; } = new();
    public int Align { get; set; } = 4;
    /// <summary>For an uninitialised section: how many zero bytes it stands for.</summary>
    public int ZeroBytes { get; set; }
    public List<Relocation> Relocs { get; } = new();

    public Section(string name, SectionKind kind)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>
    /// CONTENT LEFT IN THE FILE IT WAS READ FROM: a unit's IR archive, which
    /// a link reads a record at a time and never holds whole (ElfReader.
    /// ReadObjectFile). Bytes is empty while this is set.
    /// </summary>
    public (string Path, long Offset, int Length)? FileBacked { get; set; }

    public int Size => Kind == SectionKind.Uninitialised ? ZeroBytes : FileBacked?.Length ?? Bytes.Count;

    /// <summary>The section's content, read from its file if it was left there.</summary>
    public byte[] Content()
    {
        if (FileBacked is not (string path, long offset, int length)) return Bytes.ToArray();
        byte[] content = new byte[length];
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        file.Position = offset;
        file.ReadExactly(content);
        return content;
    }
}
