#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// Thrown when bytes claim to be an ELF object and are not one we can use,
/// or when an in-memory object cannot be expressed as one.
/// </summary>
public sealed class ElfFormatException : Exception
{
    public ElfFormatException(string message) : base(message)
    {
    }

    /// <summary>
    /// A section read of one input, its failure naming the input: the
    /// reader knows the bytes, not the file they came from. `source` is the
    /// object's path (ObjectFile.SourcePath), null for one made in memory.
    /// </summary>
    public static T Of<T>(string? source, Func<T> read)
    {
        try { return read(); }
        catch (ElfFormatException error) { throw new ElfFormatException((source ?? "an object made in memory") + ": " + error.Message); }
    }
}
