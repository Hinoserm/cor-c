#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

public sealed class Instr
{
    public Opcode Op { get; init; }
    public VReg? Dest { get; set; }
    public List<Operand> Operands { get; } = new();

    /// <summary>
    /// What a Store writes. Operands[1], but for an object's header written by
    /// a shared generic copy (Builder.StoreDescribed): Operands[1] is then the
    /// descriptor every analysis judges the object by -- the copy's own, which
    /// has the same shape -- and Operands[2] the one read at run time for the
    /// instantiation at hand, which is what is written and what a later read
    /// of the word sees.
    /// </summary>
    public Operand StoredValue => Operands.Count > 2 ? Operands[2] : Operands[1];

    /// <summary>Load and Store: how many bytes, and whether a narrow load sign-extends.</summary>
    public int Size { get; init; }
    public bool Signed { get; init; }

    /// <summary>Load and Store: a constant displacement from the address operand.</summary>
    public long Offset { get; init; }

    /// <summary>Call: who.</summary>
    public string? Callee { get; init; }

    /// <summary>
    /// CallIndirect of a virtual method: the descriptor of the type that
    /// declares it. The call reaches that type's override or a subclass's,
    /// and nothing else (Escape.IndirectTargets). On a catch body's end
    /// (Runtime.CatchEnd): the type the catch takes, null for every type.
    /// </summary>
    public string? DispatchType { get; set; }

    /// <summary>
    /// Load or Store of a field: which one ("Type::name"). What lets a whole
    /// program's every store into a field, and every read of it, be found
    /// (the owned-field rules).
    /// </summary>
    public string? Field { get; set; }

    /// <summary>
    /// Load: the word read is a field's, an element's or a cell's of a
    /// number type, never an address (Lowering.NeverAddress). MemCopy: the
    /// bytes moved are a string's characters or a byte array's bytes
    /// (Sys.Copy, Sys.CopyNoOverlap), never an address. Call, CallIndirect:
    /// what it answers is of a number type, as its method declares it,
    /// never an address, whatever the callee hands back. Store: the word
    /// written is the handler chain's, a frame's or a landing's address,
    /// never a reference (Lowering.ChainWrite). Left unset it says
    /// nothing, so a pass making a load or a copy of its own need not know.
    /// </summary>
    public bool Number { get; set; }

    /// <summary>
    /// Load or Store of a class's field at its offset from the start of an
    /// object of that class: which field, as one name for every
    /// specialisation of a generic class (Lowering.FieldFamily) -- what
    /// region inference keeps apart from every other field written at the
    /// same offset (RegionConstraint.Family). Left unset it says nothing:
    /// a raw read or write, a struct's field, an element, a copy.
    /// </summary>
    public string? Family { get; set; }

    /// <summary>
    /// An allocator call the link chose to make in the innermost open region
    /// (Lto.RegionSolver; RegionPointsTo.MarkSites): the late passes' copies
    /// of it are chosen too.
    /// </summary>
    public bool RegionSite { get; set; }

    /// <summary>
    /// What a call's Field says when its result is a struct its callee made
    /// for it: a struct value is returned as a block nobody else holds (every
    /// `return` copies what it did not just make), whoever the callee is, so
    /// the caller may give it back when the value is dead (Escape).
    /// </summary>
    public const string FreshStruct = "\u0001fresh-struct";

    /// <summary>
    /// A call of an interface's Invoke -- a Func, an Action, a delegate type
    /// -- through its slot. A closure handed in as the receiver cannot be let
    /// go by the body it runs: a lambda has no name for its own closure, and
    /// a method group's passes on its target, not itself (Escape's invoke-only
    /// parameters).
    /// </summary>
    public const string DelegateInvoke = "\u0001invoke";

    /// <summary>An allocation of a List or Dictionary used only through the calls OwnedElements knows: a candidate.</summary>
    public const string OwnsCandidate = "\u0001owns-candidate";

    /// <summary>The same, proved: whatever frees the collection gives back its elements first (Runtime.FreeOwnedElements).</summary>
    public const string OwnsElements = "\u0001owns-elements";

    /// <summary>Whether this call hands back a struct made for it (FreshStruct).</summary>
    public bool ReturnsFreshStruct => Op is Opcode.Call or Opcode.CallIndirect && Field == FreshStruct;

    /// <summary>Jump, Branch, Switch, LabelAddr: where. Phi: the predecessor each operand comes from.</summary>
    public List<Block> Targets { get; } = new();

    /// <summary>Switch: where an out-of-range index goes.</summary>
    public Block? Default { get; set; }

    /// <summary>
    /// Source line, for the dump and for the line table a stack trace reads.
    ///
    /// Settable so the builder can stamp it as instructions go by, rather than
    /// every one of the hundreds of places that build an Instr having to
    /// remember to pass it.
    /// </summary>
    public int Line { get; set; }

    public bool IsTerminator => Op is Opcode.Ret or Opcode.Jump or Opcode.Branch
                                   or Opcode.Switch or Opcode.Unreachable or Opcode.Unwind;

    public override string ToString()
    {
        StringBuilder sb = new();
        if (Dest is not null)
        {
            sb.Append(Dest).Append(" = ");
        }
        sb.Append(Op.ToString().ToLowerInvariant());
        if (Op is Opcode.Load or Opcode.Store)
        {
            sb.Append(Signed ? ".s" : ".u").Append(Size);
        }
        // What the analyses read of it: a number, never an address (Number).
        if (Number)
        {
            sb.Append(".num");
        }
        if (Callee is not null)
        {
            sb.Append(' ').Append(Callee);
        }
        foreach (Operand o in Operands)
        {
            sb.Append(' ').Append(o);
        }
        if (Offset != 0)
        {
            sb.Append(" +").Append(Offset);
        }
        foreach (Block b in Targets)
        {
            sb.Append(" ->").Append(b.Label);
        }
        if (Default is not null)
        {
            sb.Append(" default->").Append(Default.Label);
        }
        return sb.ToString();
    }
}
