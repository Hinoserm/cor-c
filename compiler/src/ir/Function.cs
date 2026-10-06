#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A function in the IR. Parameters are virtual registers the backend fills
/// from wherever its convention puts them on entry.
/// </summary>
public sealed class Function
{
    public string Name { get; }
    public IrType Returns { get; internal set; }
    public List<VReg> Params { get; } = new();
    public List<Block> Blocks { get; } = new();
    public List<FrameSlot> Slots { get; } = new();

    /// <summary>Whether other objects may reference this by name.</summary>
    public bool Exported { get; set; } = true;
    /// <summary>A specialization whose equivalent definitions share one linked identity.</summary>
    public bool Coalescible { get; init; }

    /// <summary>
    /// Whether this came out of the class library's sources rather than the
    /// program's. What a shared object may supply in this one's place: a
    /// program that declares a type of its own shadowing a library type
    /// (see tests/lang/502) compiles a function under the very name the
    /// library exports, and that one is the program's and must stay.
    /// </summary>
    public bool FromLibrary { get; init; }

    /// <summary>
    /// Written in the system library's own sources (stdlib, runtime), as
    /// against FromLibrary, which a unit compiled with --lib says of all its
    /// code. Only the unused-code report reads it (UsesCapture); not kept
    /// in IR archives.
    /// </summary>
    public bool SystemCode { get; init; }

    /// <summary>What the unused-code report passes over: an auto-property's accessor, whose field is judged (MethodDecl.AutoAccessor).</summary>
    public bool Unjudged { get; init; }

    /// <summary>The full path of the source file, where SourceFile is its name; for the unused-code report, not kept in IR archives.</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// `[MethodImpl(MethodImplOptions.NoInlining)]`: every call stays a call,
    /// so the method keeps its own frame in a stack trace and its own entry
    /// for a profiler, as the attribute promises in .NET.
    /// </summary>
    public bool NoInlining { get; set; }

    /// <summary>
    /// The bytes of arguments the function takes off the stack itself as it
    /// returns: stdcall's rule, which COM's interfaces are called by on
    /// i386 -- `[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]`,
    /// what a COM-callable wrapper's vtable entries are (ComDeclarations).
    /// 0 for every other function: C's rule, the caller's to take back. The
    /// i386 backend returns with `ret n`; x86-64 has one convention and
    /// never sets it.
    /// </summary>
    public int CalleePops { get; set; }

    /// <summary>
    /// What an analysis keeps of this function between questions, with the
    /// instruction count it was made at (Escape.StampOf): asked again of a
    /// function that has grown since, it is made again.
    /// </summary>
    internal KeptIndex? AnalysisIndex { get; set; }

    /// <summary>
    /// Set on the body of an async method: the function suspends at its
    /// `__suspend`/`__resume` markers, and the async transform turns it into
    /// a resumable state machine before any backend sees it.
    /// </summary>
    public AsyncFrame? Async { get; set; }

    /// <summary>The source, for diagnostics.</summary>
    public string? SourceFile { get; init; }
    public int Line { get; init; }

    /// <summary>
    /// What to CALL this in a stack trace: `Type.Method`, the way a person
    /// wrote it, rather than the mangled label the linker knows it by.
    ///
    /// Null for the functions that have no name in the source -- the entry
    /// stub, the shared object stubs, a boxed value's ToString -- and those
    /// are printed by their label, which is the honest answer for a frame
    /// nobody wrote.
    /// </summary>
    public string? Display { get; init; }

    private int _nextReg;
    private int _nextSlot;
    private int _nextLabel;

    /// <summary>
    /// HOW MANY TIMES THE FUNCTION HAS BEEN EDITED IN PLACE, as the code that
    /// edits it says (Edited): an analysis kept of it (Opt.AnalysisCache) is
    /// the function's while this, its register count and its block and
    /// instruction counts are what they were. Only an edit that leaves the
    /// counts as they were -- an instruction replaced by another -- could go
    /// unseen without it; the lifetime rules say every edit they make.
    /// </summary>
    internal int Edits { get; private set; }

    /// <summary>Says that the function was just edited in place (Edits).</summary>
    internal void Edited() => Edits++;

    public Function(string name, IrType returns)
    {
        Name = name;
        Returns = returns;
    }

    public Block Entry => Blocks[0];

    public VReg NewReg(IrType type, string? name = null) => new(_nextReg++, type) { Name = name };

    public FrameSlot NewSlot(int bytes, int align, string? name = null)
    {
        FrameSlot s = new(_nextSlot++, bytes, align) { Name = name };
        Slots.Add(s);
        return s;
    }

    public Block NewBlock(string hint = "L")
    {
        Block b = new($"{hint}{_nextLabel++}");
        Blocks.Add(b);
        return b;
    }

    public int RegCount => _nextReg;

    public void Dump(StringBuilder sb)
    {
        sb.Append("function ").Append(Name).Append('(');
        sb.Append(string.Join(", ", Params.Select(p => $"{p}:{p.Type}")));
        sb.Append(") : ").Append(Returns).Append('\n');
        foreach (FrameSlot s in Slots)
        {
            sb.Append("  slot ").Append(s).Append(' ').Append(s.Bytes).Append(" align ").Append(s.Align).Append('\n');
        }
        foreach (Block b in Blocks)
        {
            sb.Append(b.Label).Append(b.IsLandingPad ? " (landing pad):\n" : ":\n");
            foreach (Instr i in b.Instrs)
            {
                sb.Append("    ").Append(i).Append('\n');
            }
        }
    }
}

/// <summary>An analysis's index of a function, and the instruction count it was made at (Function.AnalysisIndex): one reference, so a reader never sees half of one.</summary>
internal sealed record KeptIndex(int Instructions, object Index);
