#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

// WHAT AN INSTRUCTION WEIGHS: a large unit holds about four hundred
// thousand of them through code generation, and they were the biggest part
// of its peak. So the operands live in the instruction (OperandList, its
// base), the fields are declared so the bytes share words -- the layout
// follows declaration order -- and what few instructions say (DispatchType,
// Default, an Offset or a Size too big for its field) waits in InstrRare,
// made only when one is written.
public sealed class Instr : OperandList
{
    // Declared first, beside the base's operand count, so the three bytes
    // share its word.
    private readonly Opcode _op;
    private byte _flags;
    private byte _size;
    private VReg? _dest;
    // A call's callee (a string) or a branch's targets (a Block[], exactly as
    // many as there are; TargetList): no instruction has both, and one word
    // holds either -- 8 bytes of each of the million and a half instructions
    // the class library's unit holds through its late passes.
    private object? _link;
    // Field and Family, as one pair shared by every instruction that names
    // them (FieldNames): the same few thousand pairs over and over.
    private FieldNames? _names;
    private InstrRare? _rare;
    private int _offset;
    private int _line;

    private const byte SignedFlag = 1, NumberFlag = 2, RegionSiteFlag = 4, BigOffsetFlag = 8, BigSizeFlag = 16;

    /// <summary>
    /// A number no other instruction or block of the process ever has: what
    /// the pipeline's analyses are fingerprinted by (PipelineAnalyses). An
    /// identity hash is the address in a native process, and an instruction
    /// made where a freed one was hashed as that one: an analysis of the
    /// function as it was was taken for one of it as it is.
    /// </summary>
    public readonly int Serial = NextSerial();
    private static int _serials;
    internal static int NextSerial() => Interlocked.Increment(ref _serials);

    private InstrRare Rare => _rare ??= new();

    public Opcode Op { get => _op; init => _op = value; }
    public VReg? Dest { get => _dest; set => _dest = value; }

    /// <summary>The operands: this instruction itself, as the list it is (OperandList).</summary>
    public OperandList Operands => this;

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
    public int Size
    {
        get => (_flags & BigSizeFlag) != 0 ? _rare!.Size : _size;
        init
        {
            if (value is >= 0 and <= byte.MaxValue) { _size = (byte)value; _flags = (byte)(_flags & ~BigSizeFlag); }
            else { Rare.Size = value; _flags |= BigSizeFlag; }
        }
    }
    public bool Signed { get => (_flags & SignedFlag) != 0; init => SetFlag(SignedFlag, value); }

    /// <summary>Load and Store: a constant displacement from the address operand.</summary>
    public long Offset
    {
        get => (_flags & BigOffsetFlag) != 0 ? _rare!.Offset : _offset;
        init
        {
            if (value is >= int.MinValue and <= int.MaxValue) { _offset = (int)value; _flags = (byte)(_flags & ~BigOffsetFlag); }
            else { Rare.Offset = value; _flags |= BigOffsetFlag; }
        }
    }

    /// <summary>Call: who.</summary>
    public string? Callee
    {
        get => _link as string;
        init
        {
            if (_link is Block[] && value is not null) throw new InvalidOperationException("an instruction with targets given a callee");
            _link = value;
        }
    }

    /// <summary>The targets themselves (TargetList's), null for none.</summary>
    internal Block[]? TargetBlocks
    {
        get => _link as Block[];
        set
        {
            if (_link is string && value is not null) throw new InvalidOperationException("a call given targets");
            if (value is not null || _link is Block[]) _link = value;
        }
    }

    /// <summary>
    /// CallIndirect of a virtual method: the descriptor of the type that
    /// declares it. The call reaches that type's override or a subclass's,
    /// and nothing else (Escape.IndirectTargets). On a catch body's end
    /// (Runtime.CatchEnd): the type the catch takes, null for every type.
    /// </summary>
    public string? DispatchType
    {
        get => _rare?.DispatchType;
        set { if (value is not null) Rare.DispatchType = value; else if (_rare is not null) _rare.DispatchType = null; }
    }

    /// <summary>
    /// Load or Store of a field: which one ("Type::name"). What lets a whole
    /// program's every store into a field, and every read of it, be found
    /// (the owned-field rules).
    /// </summary>
    public string? Field { get => _names?.Field; set => _names = FieldNames.Of(value, _names?.Family); }

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
    public bool Number { get => (_flags & NumberFlag) != 0; set => SetFlag(NumberFlag, value); }

    /// <summary>
    /// Load or Store of a class's field at its offset from the start of an
    /// object of that class: which field, as one name for every
    /// specialisation of a generic class (Lowering.FieldFamily) -- what
    /// region inference keeps apart from every other field written at the
    /// same offset (RegionConstraint.Family). Left unset it says nothing:
    /// a raw read or write, a struct's field, an element, a copy.
    /// </summary>
    public string? Family { get => _names?.Family; set => _names = FieldNames.Of(_names?.Field, value); }

    /// <summary>
    /// An allocator call the link chose to make in the innermost open region
    /// (Lto.RegionSolver; RegionPointsTo.MarkSites): the late passes' copies
    /// of it are chosen too.
    /// </summary>
    public bool RegionSite { get => (_flags & RegionSiteFlag) != 0; set => SetFlag(RegionSiteFlag, value); }

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

    /// <summary>
    /// On an Unwind (as its DispatchType): what it throws is what a landing
    /// pad received, untouched but by type tests -- a finally's rethrow, a
    /// try's when none of its catches took it and none has a filter, an
    /// iterator's onward. It hands on what it was handed; whoever threw it
    /// said what it is (Escape.ThrowVerdict). Not an explicit `throw;`: the
    /// catch it is in may have kept what it rethrows.
    /// </summary>
    public const string HandedOn = "\u0001handed-on";

    /// <summary>The same, proved: whatever frees the collection gives back its elements first (Runtime.FreeOwnedElements).</summary>
    public const string OwnsElements = "\u0001owns-elements";

    /// <summary>Whether this call hands back a struct made for it (FreshStruct).</summary>
    public bool ReturnsFreshStruct => Op is Opcode.Call or Opcode.CallIndirect && Field == FreshStruct;

    /// <summary>Jump, Branch, Switch, LabelAddr: where. Phi: the predecessor each operand comes from.</summary>
    // A VIEW OF AN ARRAY OF EXACTLY THE TARGETS, held by the instruction and
    // null for the instructions that have none -- all but the branches and
    // the phis. A List each was a list and a backing array with room for
    // four, for a hundred and seventy thousand instructions live through a
    // large unit; the array alone is a third of that. WritableTargets is the
    // same view, kept for the writers that went by it when an empty list
    // was shared.
    public TargetList Targets => new(this);
    public TargetList WritableTargets => new(this);

    /// <summary>
    /// Where, as an instruction is made: `new Instr { Op = Opcode.Jump,
    /// InitialTargets = new[] { to } }`. The array is kept, not copied, so
    /// it must be one nobody else holds.
    /// </summary>
    public Block[] InitialTargets { init => TargetBlocks = value.Length == 0 ? null : value; }

    /// <summary>Points target `index` somewhere else (Targets is a view, and a view's indexer cannot be written through).</summary>
    public void SetTarget(int index, Block to)
    {
        if ((uint)index >= (uint)(TargetBlocks?.Length ?? 0)) throw new ArgumentOutOfRangeException(nameof(index));
        TargetBlocks![index] = to;
    }

    /// <summary>Switch: where an out-of-range index goes.</summary>
    public Block? Default
    {
        get => _rare?.Default;
        set { if (value is not null) Rare.Default = value; else if (_rare is not null) _rare.Default = null; }
    }

    /// <summary>
    /// Source line, for the dump and for the line table a stack trace reads.
    ///
    /// Settable so the builder can stamp it as instructions go by, rather than
    /// every one of the hundreds of places that build an Instr having to
    /// remember to pass it.
    /// </summary>
    public int Line { get => _line; set => _line = value; }

    private void SetFlag(byte flag, bool on) => _flags = on ? (byte)(_flags | flag) : (byte)(_flags & ~flag);

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

/// <summary>
/// What few instructions carry (Instr): a virtual call's declaring type or a
/// catch's, a switch's default, and an Offset or a Size that does not fit
/// the instruction's own narrow field.
/// </summary>
internal sealed class InstrRare
{
    public string? DispatchType;
    public Block? Default;
    public long Offset;
    public int Size;
}

/// <summary>
/// An instruction's Field and Family together, one object for each pair the
/// process meets: the instructions that read and write a field all name it
/// the same way, and two strings in every one of them were 16 bytes each.
/// </summary>
public sealed class FieldNames
{
    public readonly string? Field, Family;
    private FieldNames(string? field, string? family) { Field = field; Family = family; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string?, string?), FieldNames> _pairs = new();

    /// <summary>The pair's one object; null for neither.</summary>
    public static FieldNames? Of(string? field, string? family)
        => field is null && family is null ? null : _pairs.GetOrAdd((field, family), static key => new FieldNames(key.Item1, key.Item2));
}
