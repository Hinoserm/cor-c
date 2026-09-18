#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// Bytes with a name: a string literal, a vtable and its descriptor, the
/// static block. The backend places it in a section by its flags.
/// </summary>
public sealed class DataItem
{
    public string Name { get; }
    public byte[] Bytes { get; set; }
    public int Align { get; init; } = 4;
    public bool ReadOnly { get; init; }
    /// <summary>Zero-filled and not stored in the file: .bss.</summary>
    public bool Zero { get; init; }
    public bool Exported { get; init; } = true;

    /// <summary>The class library's rather than the program's; see Function.FromLibrary.</summary>
    public bool FromLibrary { get; init; }

    public List<DataReloc> Relocs { get; } = new();

    public DataItem(string name, byte[] bytes)
    {
        Name = name;
        Bytes = bytes;
    }
}
