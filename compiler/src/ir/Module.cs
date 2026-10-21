#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>One compilation's worth of IR: what the backend is handed.</summary>
public sealed class Module
{
    /// <summary>
    /// The program's own declarations lowering never emitted -- a method no
    /// lowered code calls, a static nothing touches, a generic method whose
    /// copies (if any) other units make -- for the unused-code report
    /// (UsesCapture, UnusedReport): whether it is a method, its name as
    /// written, and where it was declared.
    /// </summary>
    public List<(bool Method, string Display, string File, int Line)> Unlowered { get; } = new();

    public string Name { get; }
    public List<Function> Functions { get; } = new();
    public List<DataItem> Data { get; } = new();

    /// <summary>
    /// What another unit's descriptors hold, as far as this one can know it
    /// from their declarations: a non-generic class's virtual slots. Never
    /// emitted; read by Devirtualize, so a call on an object made in sight
    /// whose class another unit defines (a StringBuilder's ToString) is direct.
    /// </summary>
    public Dictionary<string, DataItem> ShadowData { get; } = new(StringComparer.Ordinal);

    /// <summary>The function the program starts in, or null for a library.</summary>
    public string? Entry { get; set; }
    /// <summary>
    /// The program's Main, which the entry stub calls, or took into itself.
    /// A region opened there would last the whole run, so it is never a
    /// boundary (RegionPointsTo, RegionSummary).
    /// </summary>
    public string? Main { get; set; }
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
    /// What the objects linked in beside this module (--with) leave
    /// undefined: assembly writes a static and calls a routine by its mangled
    /// name, which nothing in the IR shows. Read and kept by the passes that
    /// drop what nothing names (DeadStatics, Inline.RemoveDeadFunctions).
    /// </summary>
    public HashSet<string> ObjectNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What the link's preinitialisation names (Lto.Preinit): the strings and
    /// descriptors the objects it lays down point at, which nothing in the IR
    /// may reach. Kept by the unit's reach, and nothing else -- unlike
    /// ObjectNames, it says nothing of what the image writes.
    /// </summary>
    public HashSet<string> PreinitNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The module is the whole image: the driver links it itself into an
    /// executable, with no shared object, kernel or separately compiled unit
    /// beside it, so every instruction that can write one of its statics is
    /// here, or in an object linked in with it whose names are
    /// <see cref="ObjectNames"/>. What a static holds can then be answered
    /// from this module alone (UnwrittenStatics).
    /// </summary>
    public bool ClosedImage { get; set; }

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
    /// What a unit leaves to the collector, for the link to say whether the
    /// program needs one (Lto.HeapRecords, Escape.HeapRecordsOf).
    /// </summary>
    public Corsac.Lang.Lto.HeapRecords? HeapRecords { get; set; }

    /// <summary>
    /// The link's answer, for a unit of a closed image it makes again: false
    /// when the program needs no collector (Lto.HeapDecision), and every
    /// allocation goes to the manual heap as a whole program's would (Escape);
    /// null when it has not decided.
    /// </summary>
    public bool? LinkHeap { get; set; }

    /// <summary>
    /// Whether the object this module becomes carries link-time hints: the
    /// driver says so before optimising. Without them nothing may call a
    /// symbol only the link defines, and there is nothing to leave hints for.
    /// </summary>
    public bool LeavesLinkHints { get; set; }

    /// <summary>
    /// The link running a unit's late passes again (UnitBackend): its field
    /// sites are named apart from the compile's, and their records go with
    /// the regenerated object for the link to define.
    /// </summary>
    public bool AtLink { get; set; }

    /// <summary>
    /// The allocation at every `new` put in place as each function is
    /// selected (AllocatorFastPaths.AtCodegen, the backend's Prepare) and not
    /// by the late passes (InlineAllocators): set by the driver for a unit
    /// whose backend does so.
    /// </summary>
    public bool AllocatorsAtCodegen { get; set; }

    /// <summary>
    /// The program declares it runs without a collector (--no-collector): a
    /// loader that hands over to a kernel and is overwritten. What the
    /// lifetime passes cannot prove is taken from the manual heap and never
    /// given back, each such allocation named in a note, and no collector
    /// is linked.
    /// </summary>
    public bool NoCollector { get; set; }

    /// <summary>
    /// Loops that may not end by themselves answer the collector at their
    /// back edges (BackEdgePolls): set by the driver where there is an
    /// operating system to run threads and a collector may run, and kept in a
    /// unit's settings for the link (IrUnitCodec.Settings.Polls). The
    /// inliner keeps Runtime.Poll for them wherever the collector stays.
    /// </summary>
    public bool SafepointPolls { get; set; }

    /// <summary>The program's own source calls a method of the collector's classes (Lowering), so it has one.</summary>
    public bool CallsCollector { get; set; }

    /// <summary>
    /// The link's answer, for a unit of a closed image it regenerates: every
    /// type something thrown that was not just made can be an instance of,
    /// ancestors included -- a catch of any other type frees what it caught.
    /// Null where nobody could say, and then every catch keeps it.
    /// </summary>
    public HashSet<string>? ForeignCatchable { get; set; }

    /// <summary>
    /// The link's answer, for a unit of a closed image it regenerates: the
    /// fields that own what they hold over the whole program (Lto.
    /// OwnedFieldSolver). The unit frees what a store into one replaces and
    /// gives the types it defines their owned-field maps. Null: none.
    /// </summary>
    public Corsac.Lang.Lto.OwnedFieldFacts? OwnedFields { get; set; }

    /// <summary>
    /// The link's region answer, for a unit of a closed image it regenerates
    /// (Lto.RegionSolver): the boundaries to open and the sites already
    /// marked (Instr.RegionSite) to make in a region. Null: none.
    /// </summary>
    public Corsac.Lang.Lto.RegionFacts? RegionFacts { get; set; }

    /// <summary>The unit's pointer constraints, for the link to solve (RegionSummary); written beside its IR.</summary>
    public Corsac.Lang.Lto.RegionHints? RegionHints { get; set; }

    /// <summary>The unit's interrupt facts, for the link's check of handlers across units (Lto.InterruptNotes). Null: none kept.</summary>
    public byte[]? InterruptFacts { get; set; }

    /// <summary>What the unit's static initialisers and constructors make, for the link to lay statics down made (Lto.Preinit). Null: nothing to say.</summary>
    public Corsac.Lang.Lto.Preinit.Notes? Preinit { get; set; }

    /// <summary>
    /// The link's answer, for a unit it regenerates: which parameters of the
    /// functions the unit calls, and of its own, escape, solved over every
    /// unit (Lto.LifetimeSolver). What the owned-elements rule judges its
    /// candidates with where the unit alone could only say "escapes". Null:
    /// the unit's own answers alone.
    /// </summary>
    public Dictionary<string, bool[]>? LinkEscapes { get; set; }

    /// <summary>
    /// WHAT THE LINK'S OWN RUN OF A UNIT'S LIFETIME PASS FOUND OF ITS
    /// FUNCTIONS, for the per-function rules that run after it (Escape.
    /// RunAtLink, LinkFacts.Unit): those that hand back their first argument
    /// (ReturnsFirst), those whose bodies it had (a call to anything else is
    /// unknown), those that hand back a copy of their argument's words, as
    /// (descriptor, base) stamps, and the unit's own descriptors. Without
    /// them each of those questions was answered by whatever the last run
    /// on the same thread had left -- or by nothing, and an iterator's
    /// GetEnumerator held the list it walks (1210's where-tolist).
    /// </summary>
    public HashSet<string>? LinkReturnsFirst { get; set; }
    public HashSet<string>? LinkBodies { get; set; }
    public Dictionary<string, (string Descriptor, long Base)[]>? LinkCopies { get; set; }
    public Dictionary<string, DataItem>? LinkTypeItems { get; set; }
    /// <summary>What each of those copying functions writes over in its argument: the words it is dirty at (Escape's copy fields).</summary>
    public Dictionary<string, long[]>? LinkCopyDirty { get; set; }

    /// <summary>
    /// Call sites no inliner may fold away: the allocations the link may yet
    /// place or free (Escape's pending hints), which it must still be able
    /// to tell from any other call when it reads this module's IR back.
    /// </summary>
    public HashSet<Instr> KeepCalls { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A unit's fields whose reads MarkOwnedElements kept from the inliner,
    /// by the kind of collection they read: the lifetime pass of the same
    /// compile judges them for the link (Escape, elements through a field).
    /// Empty in a whole program and when the link runs the late passes.
    /// </summary>
    public SortedDictionary<string, string> ElementFields { get; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// What calls each of this unit's [ModuleInitializer] methods, its type
    /// touched first, in the order they were declared: what a shared object's
    /// DT_INIT calls after __corsac_init (Lowering.ModuleInitializers,
    /// Linker.Initializers).
    /// </summary>
    public List<string> Initializers { get; } = new();

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
