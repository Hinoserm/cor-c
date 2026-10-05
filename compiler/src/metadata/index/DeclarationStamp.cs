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
    /// The last index hashed, by where it is, when it was written and how
    /// long it is: a project's units are compiled in one process against one
    /// index, and every module unit checks it is the kernel's.
    /// </summary>
    static (string Path, DateTime Written, long Length, byte[] Stamp)? _last;
    static readonly object Gate = new();

    public static byte[] Of(string indexPath)
    {
        string full = Path.GetFullPath(indexPath);
        FileInfo file = new(full);
        lock (Gate)
        {
            if (_last is { } last && last.Path == full && last.Written == file.LastWriteTimeUtc && last.Length == file.Length) return last.Stamp;
        }
        byte[] stamp = Hash(full);
        lock (Gate) _last = (full, file.LastWriteTimeUtc, file.Length, stamp);
        return stamp;
    }

    static byte[] Hash(string indexPath)
    {
        using DeclarationIndex index = new(indexPath);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("CORSAC build stamp 1\n"u8);
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
