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
    /// <summary>Other compilation units may call exported definitions, even when this unit owns Main.</summary>
    public bool PreserveExports { get; set; }

    /// <summary>
    /// Whether any allocation survives escape analysis and so needs the heap
    /// and its collector. A program whose every object the compiler could
    /// place on the stack links neither.
    /// </summary>
    public bool NeedsHeap { get; set; } = true;

    /// <summary>Symbols this module uses and does not define; the linker resolves them.</summary>
    public HashSet<string> Imports { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Runtime routines the program provides that the optimiser may add calls
    /// to -- the frees an owned object's lifetime ends in -- whether this
    /// module defines them or another unit of the program does. A source
    /// compiled on its own has the runtime's declarations but not its bodies,
    /// and asking only whether the body is here said no in every unit but
    /// the runtime's own: nothing was ever freed by the compiler in a
    /// separately compiled project.
    /// </summary>
    public HashSet<string> RuntimeHelpers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What the escape pass could not decide without the other units, for
    /// the link to finish (Escape's hints; Lto.LifetimeHints). Written into
    /// the object beside its IR.
    /// </summary>
    public Corsac.Lang.Lto.LifetimeHints? LifetimeHints { get; set; }

    /// <summary>
    /// Whether the object this module becomes carries link-time hints: the
    /// driver says so before optimising. Without them nothing may call a
    /// symbol only the link defines, and there is nothing to leave hints for.
    /// </summary>
    public bool LeavesLinkHints { get; set; }

    /// <summary>
    /// The program declares it runs without a collector (--no-collector): a
    /// loader that hands over to a kernel and is overwritten. What the
    /// lifetime passes cannot prove is taken from the manual heap and never
    /// given back, each such allocation named in a note, and no collector
    /// is linked.
    /// </summary>
    public bool NoCollector { get; set; }

    /// <summary>The program's own source calls a method of the collector's classes (Lowering), so it has one.</summary>
    public bool CallsCollector { get; set; }

    /// <summary>
    /// Call sites no inliner may fold away: the allocations the link may yet
    /// place or free (Escape's pending hints), which it must still be able
    /// to tell from any other call when it reads this module's IR back.
    /// </summary>
    public HashSet<Instr> KeepCalls { get; } = new(ReferenceEqualityComparer.Instance);

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
    /// <summary>
    /// The class library's definitions will be given away to shared objects
    /// (Provided): its code is here to be read, not emitted, so no pass may
    /// make a new definition out of it that no library exports.
    /// </summary>
    public bool LibraryCodeIsShared { get; set; }

    /// <summary>
    /// The C libraries this module's [DllImport] methods call into, by the
    /// name the declaration gave. The object records them (.corsac.native)
    /// and the link makes the program a dynamic one that needs them.
    /// </summary>
    public HashSet<string> NativeLibraries { get; } = new(StringComparer.Ordinal);

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
