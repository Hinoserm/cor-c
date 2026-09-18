#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// The type of a value in the IR. Only what a machine register can hold:
/// everything with structure is an address, and an address is I32 on a
/// 32-bit target and I64 on a 64-bit one -- see <see cref="IrTypes.Word"/>.
/// </summary>
public enum IrType : byte
{
    Void,
    I32,
    I64,
    F32,
    F64,
}

public static class IrTypes
{
    /// <summary>The type of a reference or pointer on the current target.</summary>
    public static IrType Word => Target.Current.WordSize == 8 ? IrType.I64 : IrType.I32;

    public static bool IsFloat(this IrType t) => t is IrType.F32 or IrType.F64;
    public static bool IsInt(this IrType t) => t is IrType.I32 or IrType.I64;

    public static int Bytes(this IrType t) => t switch
    {
        IrType.Void => 0,
        IrType.I32 or IrType.F32 => 4,
        _ => 8,
    };

    /// <summary>The IR type a source type is carried in.</summary>
    public static IrType Of(Type t)
    {
        if (t.Symbol is { Kind: TypeKind.Enum })
        {
            return IrType.I32;      // an enum is its int, whatever the word is
        }

        if (t.IsPointer || t.IsNullableValue || t.IsReference || t.IsArray || t.Symbol is not null)
        {
            return Word;
        }

        return t.Prim switch
        {
            Prim.Void => IrType.Void,
            Prim.I64 or Prim.U64 => IrType.I64,
            Prim.NInt or Prim.NUInt => Word,
            Prim.F32 => IrType.F32,
            Prim.F64 => IrType.F64,
            Prim.String or Prim.Type or Prim.Any or Prim.NullLiteral => Word,
            _ => IrType.I32,
        };
    }
}

/// <summary>
/// A virtual register. Unlimited in number; the allocator maps them onto the
/// machine. Not SSA: one may be assigned in several places, and the
/// allocator computes liveness rather than assuming a single definition.
/// </summary>
public sealed class VReg
{
    public int Id { get; }
    public IrType Type { get; }

    /// <summary>The source name, when there is one, for the dump and for gdb later.</summary>
    public string? Name { get; init; }

    internal VReg(int id, IrType type)
    {
        Id = id;
        Type = type;
    }

    public override string ToString() => Name is null ? $"%{Id}" : $"%{Id}.{Name}";
}

/// <summary>An operand: a virtual register, a constant, or the address of a symbol.</summary>
public abstract class Operand
{
    public abstract IrType Type { get; }
}

public sealed class RegOperand : Operand
{
    public VReg Reg { get; }
    public RegOperand(VReg reg) => Reg = reg;
    public override IrType Type => Reg.Type;
    public override string ToString() => Reg.ToString();
}

/// <summary>An integer constant. Floating-point constants live in data and are loaded.</summary>
public sealed class ImmOperand : Operand
{
    public long Value { get; }
    private readonly IrType _type;
    public ImmOperand(long value, IrType type)
    {
        Value = value;
        _type = type;
    }
    public override IrType Type => _type;
    public override string ToString() => Value.ToString();
}

/// <summary>
/// The address of a named thing: a function, a static, a string literal, a
/// descriptor. Always word-typed. The backend turns it into a relocation.
/// </summary>
public sealed class SymOperand : Operand
{
    public string Name { get; }
    public long Offset { get; }
    public SymOperand(string name, long offset = 0)
    {
        Name = name;
        Offset = offset;
    }
    public override IrType Type => IrTypes.Word;
    public override string ToString() => Offset == 0 ? $"@{Name}" : $"@{Name}+{Offset}";
}

/// <summary>
/// The address of a frame slot: memory the backend reserves in the stack
/// frame. For locals whose address is taken, for exception-handler records,
/// and for anything else that must have an address.
/// </summary>
public sealed class SlotOperand : Operand
{
    public FrameSlot Slot { get; }
    public SlotOperand(FrameSlot slot) => Slot = slot;
    public override IrType Type => IrTypes.Word;
    public override string ToString() => $"&{Slot}";
}

/// <summary>A block of frame memory, sized and aligned; the backend places it.</summary>
public sealed class FrameSlot
{
    public int Id { get; }
    public int Bytes { get; }
    public int Align { get; }
    public string? Name { get; init; }

    internal FrameSlot(int id, int bytes, int align)
    {
        Id = id;
        Bytes = bytes;
        Align = align;
    }

    public override string ToString() => Name is null ? $"slot{Id}" : $"slot{Id}.{Name}";
}

public enum Opcode : byte
{
    // ---- moves and constants ---------------------------------------------
    /// <summary>dest = src. Integer or float, same type.</summary>
    Copy,
    /// <summary>
    /// SSA join: dest takes Operands[k] when control arrived from Targets[k],
    /// one pair per predecessor of the block. Only the optimiser's SSA
    /// passes produce this, at the top of a block before any other
    /// instruction, and they remove every one again before the backend
    /// runs: instruction selection never sees a Phi.
    /// </summary>
    Phi,

    // ---- integer arithmetic, dest and both operands the same type ---------
    Add, Sub, Mul,
    /// <summary>Signed and unsigned division and remainder. Trap on zero is the target's.</summary>
    DivS, DivU, RemS, RemU,
    And, Or, Xor,
    /// <summary>Shift count is always I32, masked by the operand width as C# specifies.</summary>
    Shl, ShrS, ShrU,
    Neg, Not,
    /// <summary>Reverse all bytes of an I32 or I64 integer.</summary>
    ByteSwap,

    // ---- integer comparison: dest is I32, 0 or 1; operands same type -----
    Eq, Ne, LtS, LeS, GtS, GeS, LtU, LeU, GtU, GeU,

    // ---- floating point, dest and operands the same float type -----------
    FAdd, FSub, FMul, FDiv, FNeg,
    /// <summary>Square root, which every x87 and most FPUs have as one instruction.</summary>
    FSqrt,
    /// <summary>dest is I32. Unordered compares false, except FNe which is true.</summary>
    FEq, FNe, FLt, FLe, FGt, FGe,

    // ---- conversions ------------------------------------------------------
    /// <summary>Narrow an integer to 8 or 16 bits and extend it back, signed or not. Operand and dest same type.</summary>
    SExt8, SExt16, ZExt8, ZExt16,
    /// <summary>I64 -> I32, keeping the low half.</summary>
    Trunc64,
    /// <summary>I32 -> I64.</summary>
    SExt32, ZExt32,
    /// <summary>Between F32 and F64.</summary>
    FConv,
    /// <summary>Signed integer to float: operand I32 or I64, dest F32 or F64.</summary>
    IToF,
    /// <summary>Unsigned integer to float.</summary>
    UToF,
    /// <summary>Float to signed integer, truncating toward zero. Dest I32 or I64.</summary>
    FToI,
    /// <summary>Float to unsigned integer, truncating.</summary>
    FToU,
    /// <summary>Reinterpret bits: I64 &lt;-&gt; F64, I32 &lt;-&gt; F32.</summary>
    Bits,

    // ---- memory -----------------------------------------------------------
    /// <summary>dest = *(address + Offset). Size is 1, 2, 4 or 8; Signed says how a narrow load extends.</summary>
    Load,
    /// <summary>*(address + Offset) = value. Operands: address, value.</summary>
    Store,
    /// <summary>Copy Size bytes: operands destination address, source address. Size may be a register.</summary>
    MemCopy,
    /// <summary>Fill: operands destination, byte value (I32), count.</summary>
    MemSet,

    // ---- atomics, all sequentially consistent ------------------------------
    /// <summary>dest = old; *(addr) = value. Operands: address, value.</summary>
    AtomicSwap,
    /// <summary>dest = old; *(addr) += value.</summary>
    AtomicAdd,
    AtomicAnd, AtomicOr, AtomicXor,
    /// <summary>dest = old; if old == expect then *(addr) = value. Operands: address, expect, value.</summary>
    AtomicCas,
    Fence,

    // ---- calls ------------------------------------------------------------
    /// <summary>Call Callee (a symbol) with Operands as arguments; dest is the result or null.</summary>
    Call,
    /// <summary>Call through Operands[0]; the rest are arguments.</summary>
    CallIndirect,

    // ---- terminators ------------------------------------------------------
    /// <summary>Return Operands[0], or nothing.</summary>
    Ret,
    /// <summary>Unconditional jump to Targets[0].</summary>
    Jump,
    /// <summary>If Operands[0] (I32) is nonzero go to Targets[0], else Targets[1].</summary>
    Branch,
    /// <summary>Jump table: Operands[0] (I32) indexes Targets; out of range goes to Default.</summary>
    Switch,
    /// <summary>Control never reaches here: after a throw or an exit.</summary>
    Unreachable,

    // ---- exceptions and the things only the backend can do -----------------
    /// <summary>
    /// Restore the stack and frame pointers from a handler record whose
    /// address is Operands[0] and jump to the handler address in it, with
    /// Operands[1] (the exception) in the register the handler expects.
    /// </summary>
    Unwind,
    /// <summary>
    /// The address of the handler block Targets[0], as a word, for storing
    /// into a handler record. Lands in dest.
    /// </summary>
    LabelAddr,
    /// <summary>The current stack pointer, for a handler record. Dest is word.</summary>
    StackPointer,
    /// <summary>The current frame pointer, for a handler record.</summary>
    FramePointer,
    /// <summary>
    /// An operating-system call: Operands[0] is the number, the rest are
    /// arguments, dest is the result. How it is made is the backend's.
    /// </summary>
    Syscall,
    /// <summary>A breakpoint trap.</summary>
    Trap,
    /// <summary>Spin-loop hint.</summary>
    Pause,
}

public sealed class Instr
{
    public Opcode Op { get; init; }
    public VReg? Dest { get; set; }
    public List<Operand> Operands { get; } = new();

    /// <summary>Load and Store: how many bytes, and whether a narrow load sign-extends.</summary>
    public int Size { get; init; }
    public bool Signed { get; init; }

    /// <summary>Load and Store: a constant displacement from the address operand.</summary>
    public long Offset { get; init; }

    /// <summary>Call: who.</summary>
    public string? Callee { get; init; }

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

public sealed class Block
{
    public string Label { get; }
    public List<Instr> Instrs { get; } = new();

    /// <summary>
    /// Whether control can arrive here by an Unwind rather than a branch --
    /// a catch or finally landing pad. The backend must assume every
    /// register is dead on entry to such a block.
    /// </summary>
    public bool IsLandingPad { get; set; }

    internal Block(string label) => Label = label;

    public Instr? Terminator => Instrs.Count > 0 && Instrs[^1].IsTerminator ? Instrs[^1] : null;

    public IEnumerable<Block> Successors
    {
        get
        {
            Instr? t = Terminator;
            if (t is null)
            {
                yield break;
            }
            foreach (Block b in t.Targets)
            {
                yield return b;
            }
            if (t.Default is not null)
            {
                yield return t.Default;
            }
        }
    }

    public override string ToString() => Label;
}

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

/// <summary>
/// Where an async method's state lives: the state machine object is the
/// function's first parameter, its resumption index is a 4-byte field, and
/// everything that must survive a suspension -- registers live across it and
/// frame memory -- is laid out from FieldsStart. The object's final size is
/// written into the data item named SizeSymbol, which the kickoff reads.
/// </summary>
public sealed class AsyncFrame
{
    public required VReg StateMachine { get; init; }
    public required int StateOffset { get; init; }
    public required int FieldsStart { get; init; }
    public required string SizeSymbol { get; init; }

    /// <summary>The markers lowering puts around the continuation registration.</summary>
    public const string Suspend = "__suspend";
    public const string Resume = "__resume";
}

/// <summary>
/// A relocation inside a data item: the word at Offset holds the address of
/// Symbol plus Addend.
/// </summary>
public readonly record struct DataReloc(int Offset, string Symbol, long Addend);

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

/// <summary>
/// Builds instructions into a function one at a time, keeping track of the
/// current block. Lowering talks to this rather than constructing Instr by
/// hand, so an instruction's shape is decided in one place.
/// </summary>
public sealed class Builder
{
    public Function Function { get; }
    public Block Block { get; private set; }

    public Builder(Function f, Block entry)
    {
        Function = f;
        Block = entry;
    }

    public void SetBlock(Block b) => Block = b;

    /// <summary>
    /// The source line everything appended from here on belongs to.
    ///
    /// Set as the lowering walks the statements, and stamped on each
    /// instruction on its way into a block: a statement becomes a dozen
    /// instructions and they all come from the line it was written on.
    /// </summary>
    public int Line { get; set; }

    /// <summary>Whether the current block already ended: nothing more may be appended.</summary>
    public bool Closed => Block.Terminator is not null;

    private Instr Append(Instr i)
    {
        if (i.Line == 0)
        {
            i.Line = Line;
        }
        if (!Closed)
        {
            Block.Instrs.Add(i);
        }
        return i;
    }

    private static RegOperand R(VReg r) => new(r);

    public VReg Copy(VReg src)
    {
        VReg d = Function.NewReg(src.Type);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { R(src) } });
        return d;
    }

    public void CopyTo(VReg dest, Operand src)
        => Append(new Instr { Op = Opcode.Copy, Dest = dest, Operands = { src } });

    public VReg Const(long value, IrType type)
    {
        VReg d = Function.NewReg(type);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { new ImmOperand(value, type) } });
        return d;
    }

    public VReg Address(string symbol, long offset = 0)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { new SymOperand(symbol, offset) } });
        return d;
    }

    public VReg SlotAddress(FrameSlot slot)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { new SlotOperand(slot) } });
        return d;
    }

    public VReg Binary(Opcode op, Operand a, Operand b, IrType result)
    {
        VReg d = Function.NewReg(result);
        Append(new Instr { Op = op, Dest = d, Operands = { a, b } });
        return d;
    }

    public VReg Binary(Opcode op, VReg a, VReg b) => Binary(op, R(a), R(b), ResultOf(op, a.Type));
    public VReg Binary(Opcode op, VReg a, long imm) => Binary(op, R(a), new ImmOperand(imm, a.Type), ResultOf(op, a.Type));

    public VReg Unary(Opcode op, Operand a, IrType result)
    {
        VReg d = Function.NewReg(result);
        Append(new Instr { Op = op, Dest = d, Operands = { a } });
        return d;
    }

    public VReg Unary(Opcode op, VReg a) => Unary(op, R(a), ResultOf(op, a.Type));

    public static IrType ResultOf(Opcode op, IrType operand) => op switch
    {
        Opcode.Eq or Opcode.Ne or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS
            or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU
            or Opcode.FEq or Opcode.FNe or Opcode.FLt or Opcode.FLe or Opcode.FGt or Opcode.FGe => IrType.I32,
        Opcode.Trunc64 => IrType.I32,
        Opcode.SExt32 or Opcode.ZExt32 => IrType.I64,
        _ => operand,
    };

    public VReg Load(IrType type, Operand address, long offset = 0, int size = 0, bool signed = true)
    {
        VReg d = Function.NewReg(type);
        Append(new Instr
        {
            Op = Opcode.Load, Dest = d, Operands = { address },
            Offset = offset, Size = size == 0 ? type.Bytes() : size, Signed = signed,
        });
        return d;
    }

    public VReg Load(IrType type, VReg address, long offset = 0, int size = 0, bool signed = true)
        => Load(type, R(address), offset, size, signed);

    public void Store(Operand address, Operand value, long offset = 0, int size = 0)
        => Append(new Instr
        {
            Op = Opcode.Store, Operands = { address, value },
            Offset = offset, Size = size == 0 ? value.Type.Bytes() : size,
        });

    public void Store(VReg address, VReg value, long offset = 0, int size = 0)
        => Store(R(address), R(value), offset, size);

    public VReg? Call(string callee, IrType returns, params Operand[] args)
    {
        VReg? d = returns == IrType.Void ? null : Function.NewReg(returns);
        Instr i = new() { Op = Opcode.Call, Dest = d, Callee = callee };
        i.Operands.AddRange(args);
        Append(i);
        return d;
    }

    public VReg? Call(string callee, IrType returns, IEnumerable<VReg> args)
        => Call(callee, returns, args.Select(a => (Operand)R(a)).ToArray());

    public VReg? CallIndirect(Operand target, IrType returns, IEnumerable<Operand> args)
    {
        VReg? d = returns == IrType.Void ? null : Function.NewReg(returns);
        Instr i = new() { Op = Opcode.CallIndirect, Dest = d };
        i.Operands.Add(target);
        i.Operands.AddRange(args);
        Append(i);
        return d;
    }

    public VReg Syscall(Operand number, IEnumerable<Operand> args)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Instr i = new() { Op = Opcode.Syscall, Dest = d };
        i.Operands.Add(number);
        i.Operands.AddRange(args);
        Append(i);
        return d;
    }

    public void Ret(Operand? value = null)
    {
        Instr i = new() { Op = Opcode.Ret };
        if (value is not null)
        {
            i.Operands.Add(value);
        }
        Append(i);
    }

    public void Jump(Block target)
        => Append(new Instr { Op = Opcode.Jump, Targets = { target } });

    public void Branch(Operand cond, Block ifTrue, Block ifFalse)
        => Append(new Instr { Op = Opcode.Branch, Operands = { cond }, Targets = { ifTrue, ifFalse } });

    public void Branch(VReg cond, Block ifTrue, Block ifFalse) => Branch(R(cond), ifTrue, ifFalse);

    public void Switch(Operand index, IReadOnlyList<Block> targets, Block fallback)
    {
        Instr i = new() { Op = Opcode.Switch, Operands = { index }, Default = fallback };
        i.Targets.AddRange(targets);
        Append(i);
    }

    public void Unreachable() => Append(new Instr { Op = Opcode.Unreachable });

    public Instr Emit(Opcode op, VReg? dest, params Operand[] operands)
    {
        Instr i = new() { Op = op, Dest = dest };
        i.Operands.AddRange(operands);
        return Append(i);
    }

    public VReg LabelAddress(Block target)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Append(new Instr { Op = Opcode.LabelAddr, Dest = d, Targets = { target } });
        return d;
    }

    public VReg Reg(IrType type, string? name = null) => Function.NewReg(type, name);
}
