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

    /// <summary>What <paramref name="index"/> says under <paramref name="key"/>: <see cref="DeclarationReads.Empty"/> where nothing.</summary>
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
        return hash.GetHashAndReset()[..DeclarationReads.HashBytes];
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
        return said ? hash.GetHashAndReset()[..DeclarationReads.HashBytes] : DeclarationReads.Empty;
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
        return said ? hash.GetHashAndReset()[..DeclarationReads.HashBytes] : DeclarationReads.Empty;
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

    /// <summary>
    /// The kernel's table (DeclarationReads.KernelSection): every key its
    /// index holds, and every interface plan's prefix, each with what it
    /// says. Written beside the build stamp when the kernel's exports are.
    /// </summary>
    public static byte[] KernelTable(string indexPath)
    {
        using DeclarationIndex index = new(indexPath);
        HashSet<string> keys = new(StringComparer.Ordinal);
        // Each type by its header and by every member name it has, made here
        // from its records, decoded once for all of its names.
        Dictionary<string, List<SourceDeclaration>> types = new(StringComparer.Ordinal);
        foreach (DeclarationRecord record in index.WithPrefix(""))
        {
            keys.Add(record.Key);
            if (record.Key.StartsWith("T:", StringComparison.Ordinal))
            {
                if (!types.TryGetValue(record.Key, out List<SourceDeclaration>? parts)) types[record.Key] = parts = new();
                parts.Add(SourceDeclaration.Decode(record));
            }
            if (record.Key.StartsWith("I:", StringComparison.Ordinal) || record.Key.StartsWith("X:", StringComparison.Ordinal))
            {
                int line = record.Key.IndexOf('\n');
                if (line >= 0) keys.Add(record.Key[..(line + 1)]);
            }
        }
        IEnumerable<KeyValuePair<string, byte[]>> Typed()
        {
            foreach ((string key, List<SourceDeclaration> parts) in types)
            {
                yield return new(HeaderKey(key), HeaderHash(parts));
                foreach (string name in parts.SelectMany(part => part.MemberNames).Distinct(StringComparer.Ordinal))
                    yield return new(MemberKey(key, name), MemberHash(parts, name));
            }
        }
        return DeclarationReads.Write(keys.Select(key => new KeyValuePair<string, byte[]>(key, Of(index, key))).Concat(Typed()));
    }

    /// <summary>
    /// A module unit's table (DeclarationReads.ReadsSection): of the keys its
    /// compile loaded, those the kernel's index (<paramref name="kernel"/>,
    /// the bottom of the index it was compiled against) declares -- the rest
    /// are the module's own -- and every lookup it asked, answered by the
    /// kernel's index alone, nothing included; but a member name told absent
    /// of a type the kernel does not declare (MemberKey), which is the
    /// module's own type's. Should the kernel declare a type of that name
    /// later, the unit's simple-name lookup of it ("S:", kept here) answers
    /// otherwise, and the unit is refused for that.
    /// </summary>
    public static byte[] UnitReads(DeclarationIndex kernel, IEnumerable<string> loaded, IEnumerable<string> lookups)
    {
        Dictionary<string, byte[]> reads = new(StringComparer.Ordinal);
        foreach (string key in loaded)
        {
            byte[] hash = Of(kernel, key);
            if (!hash.AsSpan().SequenceEqual(DeclarationReads.Empty)) reads[key] = hash;
        }
        // A member name told absent of a type the kernel has not -- the
        // module's own, or one under it -- is not kept (see above).
        foreach (string key in lookups)
            if (!key.StartsWith("M:", StringComparison.Ordinal) || kernel.Find("T:" + key[2..key.LastIndexOf('\n')]).Any())
                reads[key] = Of(kernel, key);
        return DeclarationReads.Write(reads);
    }
}
