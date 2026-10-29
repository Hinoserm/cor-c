#nullable enable
using System.Buffers.Binary;
using System.Text;

namespace Corsac.Lang.Elf;

/// <summary>
/// WHAT A MODULE READ OF ITS KERNEL, BY NAME, and what the kernel says of
/// each name: the per-key check that keeps a module whose kernel changed in
/// nothing the module uses -- a new private method in a driver class, a
/// constant added anywhere else -- where the build stamp (KernelExports'
/// hash of the whole declaration index) refused every module, and its units
/// were compiled again for it.
///
/// One table, two places: the kernel's exports carry every key its
/// declaration index can answer (<see cref="KernelSection"/>), and each
/// module unit -- then each module, its units' merged -- every key its
/// compile read of the kernel's index, declarations and lookups alike
/// (<see cref="ReadsSection"/>). A name is an index record's key, or one of
/// the interface-plan prefixes ("I:&lt;assembly&gt;\n", "X:&lt;assembly&gt;\n")
/// that stand for every record under them; its hash the first 16 bytes of a
/// SHA-256 of what the index says there and nothing of where it was written
/// (DeclarationFingerprint, in the compiler). A name the kernel does not
/// have answers nothing: its hash is <see cref="Empty"/>, so a module that
/// asked and found nothing is kept while the kernel still has nothing.
///
/// THE LAYOUT, little-endian, for the kernel's binder as much as for this
/// linker (os/kernel/arch/x86/modules.cor): a header -- magic "CRD1", the
/// count, the name table's size, a reserved word -- then the entries sorted
/// by their names' bytes (unsigned, the shorter first where one is the
/// other's beginning), 24 bytes each -- the name's offset and length in the
/// name table, then the hash -- then the name table, UTF-8, no terminators
/// (a key holds '\n').
/// </summary>
public static class DeclarationReads
{
    /// <summary>The kernel's table, in its exports.</summary>
    public const string KernelSection = ".corsac.decls";
    /// <summary>A module unit's, and its module's, merged.</summary>
    public const string ReadsSection = ".corsac.reads";

    public const uint Magic = 0x31445243;  // "CRD1"
    public const int HashBytes = 16;
    private const int HeaderBytes = 16, EntryBytes = 8 + HashBytes;

    /// <summary>The answer of a name the index has nothing under: SHA-256 of nothing, its first 16 bytes.</summary>
    public static readonly byte[] Empty = Convert.FromHexString("e3b0c44298fc1c149afbf4c8996fb924");

    /// <summary>The names' order: their UTF-8 bytes, unsigned, the shorter first where one begins the other.</summary>
    public static int Compare(string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    private sealed class ByBytes : IComparer<string>
    {
        public static readonly ByBytes Instance = new();
        public int Compare(string? a, string? b) => DeclarationReads.Compare(a!, b!);
    }

    /// <summary>The table of <paramref name="entries"/>, one a name (the same name twice must hash the same).</summary>
    public static byte[] Write(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        SortedDictionary<string, byte[]> sorted = new(ByBytes.Instance);
        foreach ((string name, byte[] hash) in entries)
        {
            if (hash.Length != HashBytes) throw new ArgumentException("a declaration hash is " + HashBytes + " bytes");
            if (sorted.TryGetValue(name, out byte[]? was) && !was.AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("two answers for one key: " + Named(name));
            sorted[name] = hash;
        }
        using MemoryStream names = new();
        List<(int Offset, int Length, byte[] Hash)> rows = new();
        foreach ((string name, byte[] hash) in sorted)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            rows.Add(((int)names.Length, bytes.Length, hash));
            names.Write(bytes);
        }
        byte[] table = new byte[HeaderBytes + rows.Count * EntryBytes + names.Length];
        Span<byte> span = table;
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)rows.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)names.Length);
        for (int i = 0; i < rows.Count; i++)
        {
            Span<byte> row = span[(HeaderBytes + i * EntryBytes)..];
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)rows[i].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)rows[i].Length);
            rows[i].Hash.CopyTo(row[8..]);
        }
        names.ToArray().CopyTo(span[(HeaderBytes + rows.Count * EntryBytes)..]);
        return table;
    }

    /// <summary>A table's entries, by name.</summary>
    public static Dictionary<string, byte[]> Read(ReadOnlySpan<byte> table, string what)
    {
        if (table.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(table) != Magic)
            throw new InvalidDataException(what + ": not a declaration table (CRD1)");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table[4..]);
        uint namesSize = BinaryPrimitives.ReadUInt32LittleEndian(table[8..]);
        long namesAt = HeaderBytes + (long)count * EntryBytes;
        if (namesAt + namesSize != table.Length) throw new InvalidDataException(what + ": a declaration table of the wrong size");
        ReadOnlySpan<byte> names = table.Slice((int)namesAt, (int)namesSize);
        Dictionary<string, byte[]> entries = new((int)count, StringComparer.Ordinal);
        ReadOnlySpan<byte> before = default;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = table[(HeaderBytes + i * EntryBytes)..];
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(row), length = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            if ((long)offset + length > names.Length) throw new InvalidDataException(what + ": a declaration table's name past its end");
            ReadOnlySpan<byte> name = names.Slice((int)offset, (int)length);
            // In order, each once: a binder looks names up by halves.
            if (i > 0 && name.SequenceCompareTo(before) <= 0) throw new InvalidDataException(what + ": a declaration table out of order");
            before = name;
            entries[Encoding.UTF8.GetString(name)] = row.Slice(8, HashBytes).ToArray();
        }
        return entries;
    }

    /// <summary>
    /// What of <paramref name="reads"/> the kernel answers otherwise: each
    /// key read whose hash is not the kernel's (or, absent there, not
    /// <see cref="Empty"/>), as a line naming it.
    /// </summary>
    public static List<string> Differences(IReadOnlyDictionary<string, byte[]> reads, IReadOnlyDictionary<string, byte[]> kernel)
    {
        List<string> differences = new();
        foreach ((string name, byte[] hash) in reads)
        {
            byte[] theirs = kernel.TryGetValue(name, out byte[]? found) ? found : Empty;
            if (!hash.AsSpan().SequenceEqual(theirs))
                differences.Add(name.StartsWith("M:", StringComparison.Ordinal)
                    ? Named(name) + (ReferenceEquals(theirs, Empty) ? " is no longer a member" : " is now a member, which the unit was compiled without")
                    : (name.StartsWith("H:", StringComparison.Ordinal) ? "the header of " : "") + Named(name)
                      + (ReferenceEquals(theirs, Empty) ? " is no longer in the kernel" : " is not what it was"));
        }
        return differences;
    }

    /// <summary>A key as a log says it: past the assembly's identity, its parts joined by dots.</summary>
    public static string Named(string key) => key.StartsWith("E:", StringComparison.Ordinal) ? "the kernel's export " + key[2..]
        : key.StartsWith('@') ? "the kernel's small body " + key[1..]
        : key[(key.IndexOf('\n') + 1)..].Replace('\n', '.') is { Length: > 0 } name ? name : key.TrimEnd('\n');
}
