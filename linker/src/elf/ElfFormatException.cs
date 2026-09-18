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
}
