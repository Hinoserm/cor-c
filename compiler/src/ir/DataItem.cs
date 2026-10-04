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
    public bool Coalescible { get; init; }

    /// <summary>
    /// Writable, but never holding a reference: a static array of numbers
    /// (Lowering.StaticArrayData). It goes to a section of its own outside the
    /// statics the collector reads as roots (__data_start.._end): read there,
    /// a table of constants -- SHA-256's, a CRC's -- was a list of words that
    /// look like addresses on i386, where the heap is most of the address
    /// space, and each kept whatever object lay there and everything it
    /// reached. One compile process kept 466 MB through one such table.
    /// </summary>
    public bool NoReferences { get; init; }

    /// <summary>The class library's rather than the program's; see Function.FromLibrary.</summary>
    public bool FromLibrary { get; init; }

    /// <summary>Written in the system library's own sources; see Function.SystemCode.</summary>
    public bool SystemCode { get; init; }

    /// <summary>
    /// Where a static field's word was declared, and its name as written
    /// (`Type.Field`), for the unused-code report (UsesNotes): null for data
    /// nobody declared -- descriptors, strings, tables the compiler makes.
    /// </summary>
    public string? SourceFile { get; init; }
    public int Line { get; init; }
    public string? Display { get; init; }

    public List<DataReloc> Relocs { get; } = new();

    public DataItem(string name, byte[] bytes)
    {
        Name = name;
        Bytes = bytes;
    }
}
