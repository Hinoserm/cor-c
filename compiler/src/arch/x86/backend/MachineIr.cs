#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// The general registers, numbered as the hardware numbers them in a ModRM
/// byte. ESP and EBP are never allocated: EBP is the frame pointer in every
/// function and ESP is the stack pointer.
/// </summary>
public enum Gpr : byte
{
    Eax = 0, Ecx = 1, Edx = 2, Ebx = 3, Esp = 4, Ebp = 5, Esi = 6, Edi = 7,
}

/// <summary>A condition code, numbered as the low nibble of Jcc/SETcc.</summary>
public enum Cond : byte
{
    O = 0, No = 1, B = 2, Ae = 3, E = 4, Ne = 5, Be = 6, A = 7,
    S = 8, Ns = 9, P = 10, Np = 11, L = 12, Ge = 13, Le = 14, G = 15,
}

public static class CondExt
{
    /// <summary>The condition that is true exactly when this one is false.</summary>
    public static Cond Negate(this Cond c) => (Cond)((int)c ^ 1);

    /// <summary>The condition for the same comparison with its operands exchanged.</summary>
    public static Cond Swap(this Cond c) => c switch
    {
        Cond.B => Cond.A, Cond.A => Cond.B, Cond.Ae => Cond.Be, Cond.Be => Cond.Ae,
        Cond.L => Cond.G, Cond.G => Cond.L, Cond.Le => Cond.Ge, Cond.Ge => Cond.Le,
        _ => c,
    };

    public static string Mnemonic(this Cond c) => c switch
    {
        Cond.O => "o", Cond.No => "no", Cond.B => "b", Cond.Ae => "ae", Cond.E => "e", Cond.Ne => "ne",
        Cond.Be => "be", Cond.A => "a", Cond.S => "s", Cond.Ns => "ns", Cond.P => "p", Cond.Np => "np",
        Cond.L => "l", Cond.Ge => "ge", Cond.Le => "le", _ => "g",
    };
}

/// <summary>
/// Machine opcodes. One entry per instruction form the selector emits; the
/// encoder and the printer switch on this. Only what a 486 executes.
/// </summary>
public enum MOp : byte
{
    // data movement
    Mov, Movsx, Movzx, Lea, Xchg, Push, Pop,
    // arithmetic: two-operand, first operand read and written
    Add, Adc, Sub, Sbb, And, Or, Xor, Cmp, Test,
    Imul,           // r32, r/m32
    Imul3,          // r32, r/m32, imm
    Mul, ImulWide, Div, Idiv, // one operand, EDX:EAX implicit
    Neg, Not,
    Bswap,
    Shl, Shr, Sar, Ror,  // count is an immediate or CL
    Shld, Shrd,     // dest, source, count
    Cdq,
    Setcc,
    // control
    Jmp, Jcc, JmpTable, JmpInd, Call, CallInd, Ret,
    // atomics and strings
    Xadd, Cmpxchg, RepMovsb, RepMovsd, RepStosb, RepStosd, LockOrEsp,
    // x87: operand is a memory location whose width says single/double or dword/qword
    Fld, Fstp, Fild, Fistp, Fadd, Fsub, Fmul, Fdiv, Fchs, Fsqrt, Fucompp, Fnstsw, Sahf, Fnstcw, Fldcw,
    // odds and ends
    Int, Int3, Nop, Pause,
    /// <summary>Frame entry and exit; expanded once the frame is known.</summary>
    Prologue, Epilogue,
    /// <summary>Linux only: the system-call trap. Isolated so a bare-metal variant swaps it.</summary>
    SyscallTrap,

    // ---- what only a driver and a kernel may execute -------------------------
    //
    // None of these name their operand registers: the port is DX, the datum
    // AL/AX/EAX, and the string forms are the rep-prefixed pair's ESI/EDI/ECX,
    // exactly as the hardware fixes them. The selector moves values into those
    // registers first and the allocator learns the dependency from
    // ImplicitUses/ImplicitDefs, which is how SyscallTrap already works.
    /// <summary>in al/ax/eax, dx -- Width says which.</summary>
    In,
    /// <summary>out dx, al/ax/eax.</summary>
    Out,
    /// <summary>rep insw: ECX words from port DX to [EDI].</summary>
    RepInsw,
    /// <summary>rep outsw: ECX words from [ESI] to port DX.</summary>
    RepOutsw,
    Cli, Sti, Hlt,
    /// <summary>lidt/lgdt m48: the operand is the six-byte pseudo-descriptor.</summary>
    Lidt, Lgdt,
    /// <summary>invlpg m: drop one page's translation.</summary>
    Invlpg,
    /// <summary>mov r32, crN and mov crN, r32. Operand 1 is the immediate register number.</summary>
    MovFromCr, MovToCr,
    /// <summary>The data selector in AX into DS, ES, FS, GS and SS.</summary>
    LoadSegments,
    /// <summary>Position-independent code: the address of the GOT into the operand register.</summary>
    GotPc,

    // ---- the thread block ----------------------------------------------------
    //
    // Per-thread runtime state lives in a block whose address the operating
    // system keeps in a GDT entry reached through GS, glibc's convention: the
    // block's first word is its own address, so one segment-prefixed load
    // answers a flat address every other instruction can use.
    /// <summary>mov r32, gs:[0] -- this thread's block, by the self pointer.</summary>
    GsSelf,
    /// <summary>mov gs, r16 -- the selector of the descriptor that block sits behind.</summary>
    SetGs,
}

/// <summary>A machine operand. Registers are virtual until allocation.</summary>
public abstract class MOperand
{
}

/// <summary>
/// A 32-bit register. Virtual registers have Id &gt;= 8; physical ones use
/// their hardware number. A pair for an I64 is two of these, not adjacent.
/// </summary>
public sealed class MReg : MOperand
{
    public int Id { get; set; }

    public MReg(int id) => Id = id;

    public bool IsPhys => Id < 8;
    public Gpr Phys => (Gpr)Id;

    public static MReg Of(Gpr r) => new((int)r);

    public override string ToString() => IsPhys ? Phys.ToString().ToLowerInvariant() : $"v{Id}";
}

/// <summary>
/// A constant: a plain number, or the address of a symbol plus an addend
/// (an absolute relocation), or the address of a block in this function.
/// </summary>
public sealed class MImm : MOperand
{
    public long Value { get; init; }
    public string? Symbol { get; init; }
    public MBlock? Label { get; init; }

    public MImm(long value) => Value = value;

    public static MImm Sym(string symbol, long addend) => new(addend) { Symbol = symbol };
    public static MImm Of(MBlock label) => new(0) { Label = label };

    public bool IsPlain => Symbol is null && Label is null;
    public bool FitsSbyte => IsPlain && Value is >= -128 and <= 127;

    public override string ToString()
    {
        if (Label is not null)
        {
            return Label.Name;
        }
        if (Symbol is not null)
        {
            return Value == 0 ? Symbol : $"{Symbol}+{Value}";
        }
        return Value.ToString();
    }
}

/// <summary>
/// A memory reference: [Base + Index*Scale + Disp], where Disp may carry a
/// symbol (absolute address) or a block label (a jump table entry).
/// </summary>
public sealed class MMem : MOperand
{
    /// <summary>Private allocator storage, never an address-taken source-language slot.</summary>
    public bool IsSpill { get; init; }
    public MReg? Base { get; set; }
    public MReg? Index { get; set; }
    public int Scale { get; init; } = 1;
    public int Disp { get; init; }
    public string? Symbol { get; init; }

    /// <summary>
    /// How the symbol in the displacement is relocated. Abs32 in ordinary
    /// code; GotOff in a position-independent function, where the base
    /// register is EBX and the displacement is measured from the GOT.
    /// </summary>
    public RelocKind Reloc { get; init; } = RelocKind.Abs32;

    /// <summary>
    /// A block of the enclosing function, whose address is the
    /// displacement. The encoder resolves it against the function's own
    /// symbol, since a block has no symbol of its own.
    /// </summary>
    public MBlock? Label { get; init; }

    public MMem(MReg? @base, int disp)
    {
        Base = @base;
        Disp = disp;
    }

    public static MMem Frame(int offset) => new(MReg.Of(Gpr.Ebp), offset);
    public static MMem Spill(int offset) => new(MReg.Of(Gpr.Ebp), offset) { IsSpill = true };
    public static MMem Abs(string symbol, int disp) => new(null, disp) { Symbol = symbol };

    /// <summary>The same place reached from the GOT pointer: [EBX + symbol@GOTOFF + disp].</summary>
    public static MMem GotOff(MReg got, string symbol, int disp) => new(got, disp) { Symbol = symbol, Reloc = RelocKind.GotOff };

    public override string ToString()
    {
        List<string> parts = new();
        if (Base is not null)
        {
            parts.Add(Base.ToString());
        }
        if (Index is not null)
        {
            parts.Add(Scale == 1 ? Index.ToString() : $"{Index}*{Scale}");
        }
        if (Symbol is not null)
        {
            parts.Add(Symbol);
        }
        string s = string.Join("+", parts);
        if (Disp != 0 || parts.Count == 0)
        {
            s = parts.Count == 0 ? Disp.ToString() : Disp < 0 ? $"{s}-{-Disp}" : $"{s}+{Disp}";
        }
        return $"[{s}]";
    }
}

/// <summary>A branch target.</summary>
public sealed class MLabel : MOperand
{
    public MBlock Target { get; }
    public MLabel(MBlock target) => Target = target;
    public override string ToString() => Target.Name;
}

public sealed class MInstr
{
    public MOp Op { get; init; }
    public List<MOperand> Operands { get; } = new();

    /// <summary>Operand width in bytes: 1, 2, 4, or 8 for an x87 qword. Registers are always 32-bit except at width 1 and 2.</summary>
    public int Width { get; init; } = 4;

    /// <summary>
    /// The source line this came from, or 0 where there is none.
    ///
    /// Carried this far so the encoder can write down which byte of the image
    /// each line begins at, which is the whole of what a stack trace needs
    /// beyond the function names: `at Ns.Type.Method(args) in file.cor:line N`
    /// is a lookup of the return address in that table. Settable rather than
    /// init-only because the selector stamps it in one place, on its way out,
    /// rather than at every one of the hundred sites that build an MInstr.
    /// </summary>
    public int Line { get; set; }

    /// <summary>For Jcc and Setcc.</summary>
    public Cond Cond { get; init; }

    /// <summary>Whether a LOCK prefix goes in front (Xadd, Cmpxchg).</summary>
    public bool Lock { get; init; }

    /// <summary>For JmpTable: the table's cases, in index order.</summary>
    public List<MBlock>? Table { get; init; }

    /// <summary>For a Call to a symbol: how the reference is made. Rel32 today; Plt32 in a PIC function later.</summary>
    public RelocKind CallReloc { get; init; } = RelocKind.Rel32;

    public MInstr(MOp op, params MOperand[] operands)
    {
        Op = op;
        Operands.AddRange(operands);
    }

    public MReg Reg(int i) => (MReg)Operands[i];
}

/// <summary>
/// A machine basic block. Successors are the targets of the jumps it ends
/// with plus, unless it ends unconditionally, the block laid out after it.
/// </summary>
public sealed class MBlock
{
    public string Name { get; }
    public List<MInstr> Instrs { get; } = new();

    /// <summary>The IR block this heads, if any; null for a block the selector split off.</summary>
    public Block? Source { get; init; }

    /// <summary>Byte offset from the function start, once encoded.</summary>
    public int Offset { get; set; }

    public MBlock(string name) => Name = name;

    public override string ToString() => Name;

    /// <summary>Whether control cannot fall out of the bottom of this block.</summary>
    public bool EndsUnconditionally
    {
        get
        {
            if (Instrs.Count == 0)
            {
                return false;
            }
            MOp last = Instrs[^1].Op;
            return last is MOp.Jmp or MOp.Ret or MOp.JmpTable or MOp.JmpInd or MOp.Epilogue or MOp.Int3;
        }
    }
}

/// <summary>
/// What the collector must know at one call site: where, among the things
/// this frame owns, the live references are while the callee runs.
///
/// Registers are a mask by hardware number, and only the callee-saved
/// three can appear: a value live across a call is never left in a
/// caller-saved register, because the selector marks those busy at every
/// call. The slots are EBP-relative byte offsets, all negative.
/// </summary>
public sealed class Safepoint
{
    public uint Registers { get; set; }
    public List<int> SlotOffsets { get; } = new();
}

/// <summary>A function after selection: blocks, frame, and what the allocator decided.</summary>
public sealed class MFunction
{
    /// <summary>
    /// Machine registers holding half of a 64-bit integer or a pair the
    /// selector made for one. Never references, whatever the interim rule
    /// says about every other word.
    /// </summary>
    public HashSet<int> WideHalves { get; } = new();

    /// <summary>
    /// The live-reference description of each call in this function, keyed
    /// by the instruction the encoder will emit -- which is the instruction
    /// the ALLOCATOR produced, since it rebuilds every one it rewrites.
    /// </summary>
    public Dictionary<MInstr, Safepoint> Safepoints { get; } = new(ReferenceEqualityComparer.Instance);

    public Function Source { get; }
    public List<MBlock> Blocks { get; } = new();
    public Frame Frame { get; }
    public int NextVReg { get; set; } = 8;

    /// <summary>Virtual registers that must land in a register with an 8-bit form (EAX..EBX).</summary>
    public HashSet<int> ByteRegs { get; } = new();

    /// <summary>Callee-saved registers the allocator handed out; the prologue saves exactly these.</summary>
    public List<Gpr> SavedRegs { get; } = new();

    public MFunction(Function source)
    {
        Source = source;
        Frame = new Frame();
    }

    public MReg NewReg() => new(NextVReg++);

    /// <summary>The blocks control can reach from the one at <paramref name="index"/>: jump targets, then the next block unless it never falls through.</summary>
    public IEnumerable<MBlock> Successors(int index)
    {
        MBlock b = Blocks[index];
        foreach (MInstr i in b.Instrs)
        {
            switch (i.Op)
            {
                case MOp.Jmp:
                case MOp.Jcc:
                    yield return ((MLabel)i.Operands[0]).Target;
                    break;
                case MOp.JmpTable:
                    foreach (MBlock t in i.Table!)
                    {
                        yield return t;
                    }
                    break;
            }
        }
        if (!b.EndsUnconditionally && index + 1 < Blocks.Count)
        {
            yield return Blocks[index + 1];
        }
    }

    public MBlock NewBlock(string name, MBlock? after = null, Block? source = null)
    {
        MBlock b = new(name) { Source = source };
        if (after is null)
        {
            Blocks.Add(b);
        }
        else
        {
            Blocks.Insert(Blocks.IndexOf(after) + 1, b);
        }
        return b;
    }
}

/// <summary>
/// What each operand of each opcode does to its register, so liveness and
/// the allocator need not know the instruction set. An operand that is a
/// memory reference only ever reads its base and index registers.
/// </summary>
public static class Roles
{
    [Flags]
    public enum Role : byte
    {
        None = 0,
        Use = 1,
        Def = 2,
        UseDef = 3,
    }

    public static Role Of(MOp op, int operand)
    {
        switch (op)
        {
            case MOp.Mov:
            case MOp.Movsx:
            case MOp.Movzx:
            case MOp.Lea:
            case MOp.Pop:
            case MOp.Setcc:
            case MOp.Imul3:
            case MOp.MovFromCr:
            case MOp.GotPc:
            case MOp.GsSelf:
                return operand == 0 ? Role.Def : Role.Use;
            case MOp.Add:
            case MOp.Adc:
            case MOp.Sub:
            case MOp.Sbb:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Imul:
            case MOp.Neg:
            case MOp.Not:
            case MOp.Bswap:
            case MOp.Shl:
            case MOp.Ror:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Shld:
            case MOp.Shrd:
                return operand == 0 ? Role.UseDef : Role.Use;
            case MOp.Xchg:
                return Role.UseDef;
            case MOp.Xadd:
                // [mem], reg: the register receives the old value.
                return operand == 0 ? Role.Use : Role.UseDef;
            default:
                return Role.Use;
        }
    }

    /// <summary>Registers an instruction reads without naming them.</summary>
    public static IEnumerable<Gpr> ImplicitUses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Cdq:
                yield return Gpr.Eax;
                break;
            case MOp.Mul:
            case MOp.ImulWide:
                yield return Gpr.Eax;
                break;
            case MOp.Div:
            case MOp.Idiv:
                yield return Gpr.Eax;
                yield return Gpr.Edx;
                break;
            case MOp.Cmpxchg:
                yield return Gpr.Eax;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
                yield return Gpr.Esi;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosd:
                yield return Gpr.Eax;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.Sahf:
                yield return Gpr.Eax;
                break;
            case MOp.In:
                yield return Gpr.Edx;
                break;
            case MOp.Out:
                yield return Gpr.Edx;
                yield return Gpr.Eax;
                break;
            case MOp.LoadSegments:
                yield return Gpr.Eax;
                break;
            case MOp.RepInsw:
                yield return Gpr.Edx;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepOutsw:
                yield return Gpr.Edx;
                yield return Gpr.Esi;
                yield return Gpr.Ecx;
                break;
            case MOp.SyscallTrap:
                // Linux reads six arguments; the sixth is in EBP, which the
                // selector borrows around the trap when there is one.
                yield return Gpr.Eax;
                yield return Gpr.Ebx;
                yield return Gpr.Ecx;
                yield return Gpr.Edx;
                yield return Gpr.Esi;
                yield return Gpr.Edi;
                yield return Gpr.Ebp;
                break;
            case MOp.Epilogue:
                // The return value is already in place; keep it alive through the pops.
                yield return Gpr.Eax;
                yield return Gpr.Edx;
                break;
        }
    }

    /// <summary>Registers an instruction writes without naming them.</summary>
    public static IEnumerable<Gpr> ImplicitDefs(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Cdq:
                yield return Gpr.Edx;
                break;
            case MOp.Mul:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
                yield return Gpr.Eax;
                yield return Gpr.Edx;
                break;
            case MOp.Cmpxchg:
                yield return Gpr.Eax;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
                yield return Gpr.Esi;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosd:
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.Fnstsw:
                yield return Gpr.Eax;
                break;
            case MOp.Call:
            case MOp.CallInd:
                // cdecl: the callee may destroy these.
                yield return Gpr.Eax;
                yield return Gpr.Ecx;
                yield return Gpr.Edx;
                break;
            case MOp.SyscallTrap:
            case MOp.In:
                yield return Gpr.Eax;
                break;
            case MOp.RepInsw:
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepOutsw:
                yield return Gpr.Esi;
                yield return Gpr.Ecx;
                break;
        }
    }
}
