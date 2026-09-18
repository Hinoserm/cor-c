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

    /// <summary>
    /// Whether this came out of the class library's sources rather than the
    /// program's. What a shared object may supply in this one's place: a
    /// program that declares a type of its own shadowing a library type
    /// (see tests/lang/502) compiles a function under the very name the
    /// library exports, and that one is the program's and must stay.
    /// </summary>
    public bool FromLibrary { get; init; }

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
