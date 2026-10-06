#nullable enable

namespace Corsac.Lang.Ir;

public sealed class Section
{
    public string Name { get; }
    public SectionKind Kind { get; }
    public ChunkedBytes Bytes { get; } = new();
    public int Align { get; set; } = 4;
    /// <summary>For an uninitialised section: how many zero bytes it stands for.</summary>
    public int ZeroBytes { get; set; }
    public ChunkedList<Relocation> Relocs { get; } = new();

    public Section(string name, SectionKind kind)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>
    /// CONTENT LEFT IN THE FILE IT WAS READ FROM: a unit's IR archive, which
    /// a link reads a record at a time and never holds whole, and the notes
    /// it reads and drops (ElfReader.ReadObjectFile, LeftInFile). Bytes is
    /// empty while this is set; Content reads it.
    /// </summary>
    public (string Path, long Offset, int Length)? FileBacked { get; set; }

    public int Size => Kind == SectionKind.Uninitialised ? ZeroBytes : FileBacked?.Length ?? HandedOver ?? Bytes.Count;

    /// <summary>
    /// How many bytes this section had when a link took them (HandOver): the
    /// list is let go and its size remembered. Null while the bytes are here.
    /// </summary>
    public int? HandedOver { get; private set; }

    /// <summary>
    /// The bytes, as an array the caller owns, and the list let go: a link
    /// that places every section of a large program held each one twice,
    /// in its list and in the placed copy it relocates.
    /// </summary>
    public byte[] HandOver()
    {
        byte[] made = Bytes.ToArray();
        HandedOver = made.Length;
        Bytes.Clear();
        return made;
    }

    /// <summary>The section's content, read from its file if it was left there.</summary>
    public byte[] Content()
    {
        if (HandedOver is not null) throw new InvalidOperationException("section '" + Name + "' was handed to a link");
        if (FileBacked is not (string path, long offset, int length)) return Bytes.ToArray();
        byte[] content = new byte[length];
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        file.Position = offset;
        file.ReadExactly(content);
        return content;
    }
}
