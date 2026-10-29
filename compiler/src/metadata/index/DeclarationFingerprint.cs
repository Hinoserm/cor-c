using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Metadata;

/// <summary>
/// WHAT A KERNEL'S DECLARATION INDEX SAYS UNDER ONE NAME, in 16 bytes
/// (DeclarationReads, in the linker, keeps the tables): the per-key form of
/// the build stamp (DeclarationStamp), by the same rule -- a type's records
/// by the hash the index keeps of their tokens with the bodies left out, any
/// other record by its payload, which names keys and counts -- so it moves
/// when a field, a signature, a base or a family moves, and not when a body
/// does or the tree is checked out elsewhere. An interface plan's prefix
/// ("I:&lt;assembly&gt;\n", "X:&lt;assembly&gt;\n") stands for every record
/// under it, each with its key, as the plan is read whole
/// (DeclarationCatalog.QueryFingerprint).
/// </summary>
public static class DeclarationFingerprint
{
    /// <summary>Whether a lookup's key is an interface plan's, answered by every record under it.</summary>
    public static bool IsPlan(string key) => (key.StartsWith("I:", StringComparison.Ordinal) || key.StartsWith("X:", StringComparison.Ordinal)) && key.EndsWith('\n');

    /// <summary>What <paramref name="index"/> says under <paramref name="key"/>: <see cref="DeclarationReads.Empty"/> where nothing.</summary>
    public static byte[] Of(DeclarationIndex index, string key)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool plan = IsPlan(key);
        foreach (DeclarationRecord record in plan ? index.WithPrefix(key) : index.Find(key))
        {
            if (plan) Append(hash, Encoding.UTF8.GetBytes(record.Key));
            Append(hash, Said(record));
        }
        return hash.GetHashAndReset()[..DeclarationReads.HashBytes];
    }

    /// <summary>A record as the stamp hashes it: a type's declaration hash, anything else's payload.</summary>
    private static byte[] Said(DeclarationRecord record)
        => record.Key.StartsWith("T:", StringComparison.Ordinal) ? SourceDeclaration.Decode(record).DeclarationHash : record.Payload;

    private static void Append(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BitConverter.TryWriteBytes(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    /// <summary>
    /// The kernel's table (DeclarationReads.KernelSection): every key its
    /// index holds, and every interface plan's prefix, each with what it
    /// says. Written beside the build stamp when the kernel's exports are.
    /// </summary>
    public static byte[] KernelTable(string indexPath)
    {
        using DeclarationIndex index = new(indexPath);
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (DeclarationRecord record in index.WithPrefix(""))
        {
            keys.Add(record.Key);
            if (record.Key.StartsWith("I:", StringComparison.Ordinal) || record.Key.StartsWith("X:", StringComparison.Ordinal))
            {
                int line = record.Key.IndexOf('\n');
                if (line >= 0) keys.Add(record.Key[..(line + 1)]);
            }
        }
        return DeclarationReads.Write(keys.Select(key => new KeyValuePair<string, byte[]>(key, Of(index, key))));
    }

    /// <summary>
    /// A module unit's table (DeclarationReads.ReadsSection): of the keys its
    /// compile loaded, those the kernel's index (<paramref name="kernel"/>,
    /// the bottom of the index it was compiled against) declares -- the rest
    /// are the module's own -- and every lookup it asked, answered by the
    /// kernel's index alone, nothing included.
    /// </summary>
    public static byte[] UnitReads(DeclarationIndex kernel, IEnumerable<string> loaded, IEnumerable<string> lookups)
    {
        Dictionary<string, byte[]> reads = new(StringComparer.Ordinal);
        foreach (string key in loaded)
        {
            byte[] hash = Of(kernel, key);
            if (!hash.AsSpan().SequenceEqual(DeclarationReads.Empty)) reads[key] = hash;
        }
        foreach (string key in lookups) reads[key] = Of(kernel, key);
        return DeclarationReads.Write(reads);
    }
}
