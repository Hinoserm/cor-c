using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>
/// THE BUILD STAMP: what a kernel's declarations are, in thirty-two bytes
/// (KernelExports in the linker). A module is compiled against the kernel's
/// declaration index and nothing else of it -- the fields it reaches, the
/// slots it calls through, the signatures of what it calls -- so the index
/// is exactly what has to be the same for the module to be right, and this
/// is a hash of it.
///
/// OF WHAT THE INDEX SAYS, not of its bytes. Each record is its key and,
/// for a declaration, the hash the index already keeps of its tokens with
/// the bodies left out (SourceIndexBuilder's DeclarationHash); for the rest
/// -- the binding names, the extension and override lists, the interface
/// families -- its payload, which names keys and counts and nothing else.
/// So it changes when a field, a signature, a base class or an interface's
/// order changes, and not when a body does, or the tree is checked out
/// somewhere else: the file's own bytes carry every source's path and every
/// declaration's offset in it, and those move whenever anything above them
/// is edited.
/// </summary>
public static class DeclarationStamp
{
    /// <summary>
    /// EVERY INDEX HASHED, by where it is, when it was written and how long
    /// it is, each made once however many threads ask for it at once (one
    /// Lazy per key, as Go's singleflight and .NET's own ConcurrentDictionary
    /// + Lazy idiom do it). One entry would not do: a module stacked on a
    /// module on the kernel (nt on gui on the kernel) asks for two stamps in
    /// turn for every unit, and a single remembered one is always the other.
    /// A rewritten index is a new key; the old entry is a few dozen bytes.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, DateTime Written, long Length), Lazy<byte[]>> Made = new();

    public static byte[] Of(string indexPath)
    {
        string full = Path.GetFullPath(indexPath);
        FileInfo file = new(full);
        Lazy<byte[]> made = Made.GetOrAdd((full, file.LastWriteTimeUtc, file.Length),
            key => new Lazy<byte[]>(() => Hash(key.Path), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return made.Value; }
        catch
        {
            // A failed hash is not remembered: the next asker tries again.
            Made.TryRemove(new KeyValuePair<(string, DateTime, long), Lazy<byte[]>>((full, file.LastWriteTimeUtc, file.Length), made));
            throw;
        }
    }

    static byte[] Hash(string indexPath)
    {
        using DeclarationIndex index = new(indexPath);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("CORSAC build stamp 0\n"u8);
        foreach (DeclarationRecord record in index.WithPrefix(""))
        {
            byte[] key = Encoding.UTF8.GetBytes(record.Key);
            byte[] said = record.Key.StartsWith("T:", StringComparison.Ordinal)
                ? SourceDeclaration.Decode(record).DeclarationHash : record.Payload;
            Span<byte> lengths = stackalloc byte[8];
            BitConverter.TryWriteBytes(lengths, key.Length);
            BitConverter.TryWriteBytes(lengths[4..], said.Length);
            hash.AppendData(lengths);
            hash.AppendData(key);
            hash.AppendData(said);
        }
        return hash.GetHashAndReset();
    }
}
