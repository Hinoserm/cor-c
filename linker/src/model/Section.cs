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
            if (_backing is not null) Load();
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
    public (string Path, long Offset, int Length)? FileBacked
    {
        // ONE REFERENCE, read and written whole: Load clears it under its
        // lock while another thread asks Size or ReadAt, and a tuple of three
        // fields could be read half cleared.
        get => _backing is { } b ? (b.Path, b.Offset, b.Length) : null;
        set => Volatile.Write(ref _backing, value is { } v
            ? new Backing(v.Path, v.Offset, v.Length, v.Path == Corsac.Lang.Elf.ElfReader.BackingPath ? Corsac.Lang.Elf.ElfReader.BackingHandle : null)
            : null);
    }

    /// <summary>
    /// Where a section left in its file is, and THE HANDLE IT IS READ BY:
    /// the one the object was parsed through (ElfReader.BackingHandle),
    /// shared by its sections and closed when they are gone. Its bytes are
    /// the ones its offsets were read from, whatever is later written at the
    /// path -- a file opened again by its name could be another object.
    /// </summary>
    private sealed class Backing(string path, long offset, int length, Microsoft.Win32.SafeHandles.SafeFileHandle? handle)
    {
        public string Path { get; } = path;
        public long Offset { get; } = offset;
        public int Length { get; } = length;
        // Opened by name only for a section given a place in a file by hand.
        public Microsoft.Win32.SafeHandles.SafeFileHandle Handle
            => handle ?? LazyInitializer.EnsureInitialized(ref handle, () => File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read))!;
    }
    private Backing? _backing;

    /// <summary>Left in the same file, at the same place, read by the same handle, as <paramref name="other"/>.</summary>
    public void LeftInFileAs(Section other) => Volatile.Write(ref _backing, other._backing);

    /// <summary>The handle a section left in its file is read by (Backing); null when it is in memory.</summary>
    internal Microsoft.Win32.SafeHandles.SafeFileHandle? FileHandle => _backing?.Handle;

    /// <summary>
    /// The IR archive's directory read from this section, once (IrArchive.Read):
    /// held by the section itself, so it goes when the section does. Not a
    /// table beside it -- the class library's ConditionalWeakTable holds its
    /// keys strongly, and every archive a link read stayed for the process.
    /// An object here so that the model knows nothing of the link's types.
    /// </summary>
    internal object? ArchiveRead;

    /// <summary>Bytes.Count, without bringing a section left in its file in.</summary>
    public int Length => _backing?.Length ?? _bytes.Count;

    public int Size => Kind == SectionKind.Uninitialised ? ZeroBytes : _backing?.Length ?? HandedOver ?? _bytes.Count;

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
        if (_backing is { } backing) return new Corsac.Lang.Elf.SectionReadStream(backing.Handle, backing.Offset, backing.Length);
        return new Corsac.Lang.Elf.SectionReadStream(_bytes);
    }

    /// <summary>Content left in its file brought into the chunks, a chunk at a time, and the file let go of.</summary>
    public void Load()
    {
        // Once, whichever of the link's threads asks first.
        lock (_bytes)
        {
            if (_backing is not { } backing) return;
            _bytes.AddFromFile(backing.Handle, backing.Offset, backing.Length);
            Volatile.Write(ref _backing, null);
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
        if (_backing is { } backing)
        {
            if (at + into.Length > backing.Length) return false;
            Microsoft.Win32.SafeHandles.SafeFileHandle handle = backing.Handle;
            for (int done = 0; done < into.Length;)
            {
                int got = RandomAccess.Read(handle, into[done..], backing.Offset + at + done);
                if (got <= 0) return false;
                done += got;
            }
            return true;
        }
        lock (_bytes)
        {
            if (at + into.Length > _bytes.Count) return false;
            _bytes.CopyTo((int)at, into, into.Length);
        }
        return true;
    }

    /// <summary>
    /// `length` bytes at `at`, as a new array, from the chunks or the file,
    /// without bringing a section left in its file in (ReadAt).
    /// </summary>
    public byte[] Read(long at, int length)
    {
        byte[] bytes = new byte[length];
        if (!ReadAt(at, bytes)) throw new InvalidOperationException($"section '{Name}' has no {length} bytes at 0x{at:x}");
        return bytes;
    }

    /// <summary>The section's content, read from its file if it was left there.</summary>
    public byte[] Content()
    {
        if (HandedOver is not null) throw new InvalidOperationException("section '" + Name + "' was handed to a link");
        if (_backing is not { } backing) return _bytes.ToArray();
        byte[] content = new byte[backing.Length];
        if (!ReadAt(0, content)) throw new IOException($"section '{Name}': its file ended before its {backing.Length} bytes");
        return content;
    }
}
