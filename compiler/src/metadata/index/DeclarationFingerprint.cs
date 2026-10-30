using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>
/// WHAT A DECLARATION INDEX SAYS UNDER ONE NAME, in 16 bytes: the per-key form of
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
    public const int HashBytes = 16;

    /// <summary>The answer of a name the index has nothing under: SHA-256 of nothing, its first 16 bytes.</summary>
    public static readonly byte[] Empty = Convert.FromHexString("e3b0c44298fc1c149afbf4c8996fb924");

    /// <summary>Whether a lookup's key is an interface plan's, answered by every record under it.</summary>
    public static bool IsPlan(string key) => (key.StartsWith("I:", StringComparison.Ordinal) || key.StartsWith("X:", StringComparison.Ordinal)) && key.EndsWith('\n');

    /// <summary>
    /// A type's header as a key of the tables ("H:", then its "T:" key's
    /// rest): what a unit that never declared its members read of it
    /// (UnitDependencies, Dependence.Header).
    /// </summary>
    public static string HeaderKey(string typeKey) => "H:" + typeKey[2..];

    /// <summary>
    /// One member name of a type ("M:", its "T:" key's rest, a line, the
    /// name): said where the type has a member of that name, nothing where
    /// not -- what a unit told it had none holds the kernel to.
    /// </summary>
    public static string MemberKey(string typeKey, string name) => "M:" + typeKey[2..] + "\n" + name;

    /// <summary>What <paramref name="index"/> says under <paramref name="key"/>: <see cref="Empty"/> where nothing.</summary>
    public static byte[] Of(DeclarationIndex index, string key)
    {
        if (key.StartsWith("H:", StringComparison.Ordinal) || key.StartsWith("M:", StringComparison.Ordinal)) return OfType(index, key);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool plan = IsPlan(key);
        // The families by the slots they take (InterfaceFamilies.PlanSaid):
        // a method more or less in one interface moves no other's.
        if (plan && key.StartsWith("I:", StringComparison.Ordinal))
            Append(hash, InterfaceFamilies.PlanSaid(index.WithPrefix(key)));
        else foreach (DeclarationRecord record in plan ? index.WithPrefix(key) : index.Find(key))
        {
            if (plan) Append(hash, Encoding.UTF8.GetBytes(record.Key));
            Append(hash, Said(record));
        }
        return hash.GetHashAndReset()[..HashBytes];
    }

    /// <summary>A header key's answer, or a member name key's (HeaderKey, MemberKey).</summary>
    private static byte[] OfType(DeclarationIndex index, string key)
    {
        bool member = key[0] == 'M';
        int line = key.LastIndexOf('\n');
        string typeKey = "T:" + (member ? key[2..line] : key[2..]);
        string name = member ? key[(line + 1)..] : "";
        SourceDeclaration[] records = index.Find(typeKey).Select(SourceDeclaration.Decode).ToArray();
        return member ? MemberHash(records, name) : HeaderHash(records);
    }

    private static byte[] HeaderHash(IEnumerable<SourceDeclaration> records)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool said = false;
        foreach (SourceDeclaration declared in records) { Append(hash, declared.HeaderHash); said = true; }
        return said ? hash.GetHashAndReset()[..HashBytes] : Empty;
    }

    /// <summary>A member name's answer of a type's records: its name once for each record that has it, or nothing.</summary>
    private static byte[] MemberHash(IEnumerable<SourceDeclaration> records, string name)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool said = false;
        foreach (SourceDeclaration declared in records)
        {
            if (!Holds(declared.MemberNames, name)) continue;
            Append(hash, Encoding.UTF8.GetBytes(name));
            said = true;
        }
        return said ? hash.GetHashAndReset()[..HashBytes] : Empty;
    }

    /// <summary>Whether a sorted list of member names (SourceDeclaration.MemberNames) holds one.</summary>
    internal static bool Holds(IReadOnlyList<string> sorted, string name)
    {
        int low = 0, high = sorted.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2, order = string.CompareOrdinal(sorted[mid], name);
            if (order == 0) return true;
            if (order < 0) low = mid + 1; else high = mid - 1;
        }
        return false;
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
}
