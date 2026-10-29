#nullable enable

namespace Corsac.Lang.Ir;

public sealed class Section
{
    public string Name { get; }
    public SectionKind Kind { get; }
    /// <summary>
    /// The content, brought in from its file on first use if it was left
    /// there (FileBacked, Load): whatever reads it reads all of it, and a
    /// reader that knew nothing of files never saw a section empty.
    /// </summary>
    public ChunkedBytes Bytes
    {
        get
        {
            if (FileBacked is not null) Load();
            return _bytes;
        }
    }
    private readonly ChunkedBytes _bytes = new();
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
    /// a link reads a record at a time and never holds whole, the notes it
    /// reads and drops, and the code and data it lays out only at the end
    /// (ElfReader.ReadObjectFile, LeftInFile). Content, OpenRead and ReadAt
    /// read it where it is; Bytes brings it in.
    /// </summary>
    public (string Path, long Offset, int Length)? FileBacked { get; set; }

    /// <summary>
    /// The IR archive's directory read from this section, once (IrArchive.Read):
    /// held by the section itself, so it goes when the section does. Not a
    /// table beside it -- the class library's ConditionalWeakTable holds its
    /// keys strongly, and every archive a link read stayed for the process.
    /// An object here so that the model knows nothing of the link's types.
    /// </summary>
    internal object? ArchiveRead;

    public int Size => Kind == SectionKind.Uninitialised ? ZeroBytes : FileBacked?.Length ?? HandedOver ?? _bytes.Count;

    /// <summary>
    /// How many bytes this section had when a link took them (HandOver): the
    /// list is let go and its size remembered. Null while the bytes are here.
    /// </summary>
    public int? HandedOver { get; private set; }

    /// <summary>A section read for its size alone (ElfReader.ReadObjectMetadata): no bytes, that many said.</summary>
    public void SizeOnly(int bytes) => HandedOver = bytes;

    /// <summary>
    /// The bytes, taken by the caller and let go here: a link that places
    /// every section of a large program held each one twice, in the section
    /// and in the placed copy it relocates. The chunks themselves move, so
    /// nothing is copied and no section is ever one array.
    /// </summary>
    public ChunkedBytes HandOver()
    {
        ChunkedBytes bytes = Bytes;
        HandedOver = bytes.Count;
        return bytes.TakeAll();
    }

    /// <summary>
    /// The section's content as a stream, from its chunks or from the file it
    /// was left in: what a note reader parses, without the content ever being
    /// one array of its own (Content).
    /// </summary>
    public Stream OpenRead()
    {
        if (HandedOver is not null) throw new InvalidOperationException("section '" + Name + "' was handed to a link");
        if (FileBacked is (string path, long offset, int length)) return new Corsac.Lang.Elf.SectionReadStream(path, offset, length);
        return new Corsac.Lang.Elf.SectionReadStream(_bytes);
    }

    /// <summary>Content left in its file brought into the chunks, a chunk at a time, and the file let go of.</summary>
    public void Load()
    {
        // Once, whichever of the link's threads asks first.
        lock (_bytes)
        {
            if (FileBacked is not (string path, long offset, int length)) return;
            using Microsoft.Win32.SafeHandles.SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _bytes.AddFromFile(file, offset, length);
            FileBacked = null;
        }
    }

    /// <summary>
    /// Bytes at `at` into `into`, from the chunks or the file, without
    /// bringing a section left in its file in: false where the section has
    /// no such bytes (handed over, or too short).
    /// </summary>
    public bool ReadAt(long at, Span<byte> into)
    {
        if (HandedOver is not null || at < 0) return false;
        if (FileBacked is (string path, long offset, int length))
        {
            if (at + into.Length > length) return false;
            using Microsoft.Win32.SafeHandles.SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return RandomAccess.Read(file, into, offset + at) == into.Length;
        }
        lock (_bytes)
        {
            if (at + into.Length > _bytes.Count) return false;
            for (int k = 0; k < into.Length; k++) into[k] = _bytes[(int)at + k];
        }
        return true;
    }

    /// <summary>The section's content, read from its file if it was left there.</summary>
    public byte[] Content()
    {
        if (HandedOver is not null) throw new InvalidOperationException("section '" + Name + "' was handed to a link");
        if (FileBacked is not (string path, long offset, int length)) return _bytes.ToArray();
        byte[] content = new byte[length];
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        file.Position = offset;
        file.ReadExactly(content);
        return content;
    }
}
