#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>One compilation's worth of IR: what the backend is handed.</summary>
public sealed class Module
{
    public string Name { get; }
    public List<Function> Functions { get; } = new();
    public List<DataItem> Data { get; } = new();

    /// <summary>The function the program starts in, or null for a library.</summary>
    public string? Entry { get; set; }

    /// <summary>
    /// Whether any allocation survives escape analysis and so needs the heap
    /// and its collector. A program whose every object the compiler could
    /// place on the stack links neither.
    /// </summary>
    public bool NeedsHeap { get; set; } = true;

    /// <summary>Symbols this module uses and does not define; the linker resolves them.</summary>
    public HashSet<string> Imports { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Drops everything a shared object already contains, so that a program
    /// linked against one carries no second copy of it. Answers how many
    /// definitions went.
    ///
    /// The rule is the one the shared-library design states: a definition
    /// goes when the library HAS it, and what the library does not have --
    /// the program's own code, and a generic instantiation the library was
    /// never asked for -- is emitted here as before. A name the PROGRAM'S
    /// sources declared is never given away, whatever the library calls its
    /// own: that is a program shadowing a library type, and the program's is
    /// the one it meant.
    /// </summary>
    public int Provided(IReadOnlySet<string> provided)
    {
        ArgumentNullException.ThrowIfNull(provided);
        int gone = 0;
        gone += Functions.RemoveAll(f => f.FromLibrary && f.Name != Entry && provided.Contains(f.Name));
        gone += Data.RemoveAll(d => d.FromLibrary && provided.Contains(d.Name));
        return gone;
    }

    public Module(string name) => Name = name;

    public string Dump()
    {
        StringBuilder sb = new();
        sb.Append("module ").Append(Name).Append('\n');
        if (Entry is not null)
        {
            sb.Append("entry ").Append(Entry).Append('\n');
        }
        foreach (DataItem d in Data)
        {
            sb.Append("data ").Append(d.Name).Append(' ').Append(d.Bytes.Length).Append(" bytes");
            if (d.ReadOnly) sb.Append(" ro");
            if (d.Zero) sb.Append(" zero");
            if (d.Relocs.Count > 0) sb.Append(' ').Append(d.Relocs.Count).Append(" relocs");
            sb.Append('\n');
        }
        foreach (Function f in Functions)
        {
            sb.Append('\n');
            f.Dump(sb);
        }
        return sb.ToString();
    }
}
