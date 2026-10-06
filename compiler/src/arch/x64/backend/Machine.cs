#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X64;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// The sixteen general registers, numbered as the hardware numbers them: the
/// low three bits go in a ModRM or SIB field and the fourth in a REX prefix.
/// RSP is the stack pointer and RBP the frame pointer in every function, and
/// neither is ever allocated.
/// </summary>
public enum Gpr : byte
{
    Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rsp = 4, Rbp = 5, Rsi = 6, Rdi = 7,
    R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15,
}

/// <summary>
/// A register operand. Ids 0..15 are the general registers, 16..31 the SSE
/// registers XMM0..XMM15, and everything from 32 up a virtual register the
/// allocator has yet to place. A virtual register belongs to one class for
/// its whole life -- integer or floating point -- and only ever lands in a
/// register of that class.
/// </summary>
public sealed class MReg : MOperand
{
    public const int XmmBase = 16;
    public const int FirstVirtual = 32;

    public int Id { get; set; }

    /// <summary>Whether this is (or will be allocated to) an XMM register.</summary>
    public bool IsFloat { get; }

    public MReg(int id, bool isFloat = false)
    {
        Id = id;
        IsFloat = isFloat || (id >= XmmBase && id < FirstVirtual);
    }

    public bool IsPhys => Id < FirstVirtual;

    /// <summary>The hardware number, 0..15, of a physical register of either class.</summary>
    public int Hw => Id & 15;

    public static MReg Of(Gpr r) => new((int)r);

    public static MReg Xmm(int n) => new(XmmBase + n, true);

    public override string ToString()
    {
        if (!IsPhys)
        {
            return IsFloat ? $"f{Id}" : $"v{Id}";
        }
        return IsFloat ? $"xmm{Hw}" : ((Gpr)Id).ToString().ToLowerInvariant();
    }
}

/// <summary>A machine operand. Registers are virtual until allocation.</summary>
public abstract class MOperand
{
}

/// <summary>
/// A memory reference: [Base + Index*Scale + Disp], or [RIP + Symbol + Disp]
/// when it names a symbol. There is no absolute form: every named thing is
/// reached relative to the instruction, which is what the small code model
/// and a position-independent image both want.
/// </summary>
public sealed class MMem : MOperand
{
    /// <summary>Private allocator storage, never an address-taken source-language slot.</summary>
    public bool IsSpill { get; init; }
    public MReg? Base { get; set; }
    public MReg? Index { get; set; }
    public int Scale { get; init; } = 1;
    public int Disp { get; init; }

    /// <summary>RIP-relative to this symbol when set; Base and Index are then null.</summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// With Symbol: the symbol's GOT slot rather than the symbol, RIP-relative
    /// (R_X86_64_GOTPCREL) -- the eight-byte address another image defines,
    /// which only a `mov r64` reads.
    /// </summary>
    public bool Got { get; init; }

    /// <summary>A block of the enclosing function, RIP-relative: a jump table or a handler address.</summary>
    public MBlock? Label { get; init; }

    /// <summary>
    /// A segment override: 0 for none, else the prefix byte (0x65 for GS).
    /// GS holds the thread block: FS is glibc's thread pointer, and a
    /// program that calls into C must leave it alone.
    /// </summary>
    public byte Segment { get; init; }

    public MMem(MReg? @base, int disp)
    {
        Base = @base;
        Disp = disp;
    }

    public static MMem Frame(int offset) => new(MReg.Of(Gpr.Rbp), offset);
    public static MMem Spill(int offset) => new(MReg.Of(Gpr.Rbp), offset) { IsSpill = true };
    public static MMem Rip(string symbol, int disp) => new(null, disp) { Symbol = symbol };
    public static MMem RipGot(string symbol) => new(null, 0) { Symbol = symbol, Got = true };

    public bool IsRipRelative => Symbol is not null || Label is not null;

    public MMem WithDisp(int delta) => new(Base, Disp + delta)
    {
        Index = Index, Scale = Scale, Symbol = Symbol, Label = Label, IsSpill = IsSpill, Segment = Segment,
    };

    public override string ToString()
    {
        List<string> parts = new();
        if (Symbol is not null)
        {
            parts.Add("rip");
            parts.Add(Symbol);
        }
        if (Label is not null)
        {
            parts.Add("rip");
            parts.Add(Label.Name);
        }
        if (Base is not null)
        {
            parts.Add(Base.ToString());
        }
        if (Index is not null)
        {
            parts.Add(Scale == 1 ? Index.ToString() : $"{Index}*{Scale}");
        }
        string s = string.Join("+", parts);
        if (Disp != 0 || parts.Count == 0)
        {
            s = parts.Count == 0 ? Disp.ToString() : Disp < 0 ? $"{s}-{-Disp}" : $"{s}+{Disp}";
        }
        string segment = Segment == 0x64 ? "fs:" : Segment == 0x65 ? "gs:" : "";
        return $"{segment}[{s}]";
    }
}

/// <summary>
/// A constant: a plain number, or the address of a symbol plus an addend.
/// A symbol's address is never an immediate in an instruction -- it is made
/// with `lea reg, [rip + symbol]` -- so a symbolic MImm appears only where
/// the selector is about to turn it into exactly that.
/// </summary>
public sealed class MImm : MOperand
{
    public long Value { get; init; }
    public string? Symbol { get; init; }
    public MImm(long value) => Value = value;
    public static MImm Sym(string symbol, long addend) => new(addend) { Symbol = symbol };
    public bool IsPlain => Symbol is null;
    public bool FitsSbyte => IsPlain && Value is >= -128 and <= 127;
    public bool FitsInt32 => IsPlain && Value is >= int.MinValue and <= int.MaxValue;
    public override string ToString() => Symbol is null ? Value.ToString() : Value == 0 ? Symbol : $"{Symbol}+{Value}";
}

/// <summary>A branch target.</summary>
public sealed class MLabel : MOperand
{
    public MBlock Target { get; }
    public MLabel(MBlock target) => Target = target;
    public override string ToString() => Target.Name;
}

/// <summary>A condition code, numbered as the low nibble of Jcc/SETcc.</summary>
public enum Cond : byte
{
    O = 0, No = 1, B = 2, Ae = 3, E = 4, Ne = 5, Be = 6, A = 7,
    S = 8, Ns = 9, P = 10, Np = 11, L = 12, Ge = 13, Le = 14, G = 15,
}

public static class CondExt
{
    public static Cond Negate(this Cond c) => (Cond)((int)c ^ 1);

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
/// Machine opcodes: one per instruction form the selector emits. The width
/// of an integer instruction is the MInstr's Width (1, 2, 4 or 8); the SSE
/// arithmetic forms take 4 for single precision and 8 for double.
///
/// THE K8 SET. Everything here was in the first AMD64 processors: SSE2 for
/// floating point, CMOV, CMPXCHG8B (and the 64-bit CMPXCHG), MFENCE. Nothing
/// from SSE3 on, no CMPXCHG16B, no POPCNT/LZCNT, and no LAHF/SAHF, which the
/// earliest K8 steppings did not execute in 64-bit mode.
/// </summary>
public enum MOp : byte
{
    // ---- integer --------------------------------------------------------------
    Mov,            // r/m, r | r, r/m | r/m, imm32 (sign-extended) | r, imm64 (movabs)
    Movsx,          // r, r/m8/16/32 -- Width is the SOURCE width; the destination is 64 bits
    Movzx,          // r, r/m8/16 -- Width is the SOURCE width; the destination is 32 bits, zero-extended
    Lea,            // r64, m
    Push, Pop,      // r64 | imm
    Add, Sub, And, Or, Xor, Cmp, Test,
    Adc, Sbb,
    Imul,           // r, r/m
    Imul3,          // r, r/m, imm32
    MulWide,        // r/m: RDX:RAX = RAX * operand, unsigned
    ImulWide,       // r/m: RDX:RAX = RAX * operand, signed
    Div, Idiv,      // r/m: RAX, RDX = RDX:RAX / operand
    Cwd,            // sign-extend RAX into RDX: cdq at Width 4, cqo at Width 8
    Neg, Not,
    Bswap,
    Shl, Shr, Sar, Rol, Ror,   // count is an immediate or CL
    Setcc,          // r8
    Cmovcc,         // r, r/m

    // ---- control ----------------------------------------------------------------
    Jmp, Jcc, JmpTable, JmpInd, Call, CallInd, Ret,

    // ---- atomics and strings -----------------------------------------------------
    Xchg, Xadd, Cmpxchg, Mfence,
    RepMovsb, RepMovsq, RepStosb, RepStosq,

    // ---- SSE2 ----------------------------------------------------------------------
    /// <summary>movss / movsd: xmm, xmm/m | m, xmm.</summary>
    MovF,
    /// <summary>movd / movq between a general register and an XMM register (Width 4 or 8).</summary>
    MovGx,
    AddF, SubF, MulF, DivF, SqrtF,
    /// <summary>ucomiss / ucomisd: sets ZF, PF, CF as an unsigned compare would.</summary>
    UcomiF,
    /// <summary>xorps / xorpd, the whole register: zeroing, and sign flips against a mask.</summary>
    XorF,
    /// <summary>cvtsi2ss / cvtsi2sd: xmm, r/m. Width is the destination's (4 or 8); SourceWidth the integer's.</summary>
    CvtIntToF,
    /// <summary>cvttss2si / cvttsd2si: r, xmm/m, truncating. Width is the result's; SourceWidth the float's.</summary>
    CvtFToInt,
    /// <summary>cvtss2sd (Width 8) / cvtsd2ss (Width 4).</summary>
    CvtFToF,

    // ---- odds and ends -------------------------------------------------------------
    Int3, Ud2, Nop, Pause,
    /// <summary>The system call instruction. Number in RAX, arguments RDI RSI RDX R10 R8 R9.</summary>
    Syscall,
    /// <summary>Frame entry and exit; expanded once the frame is known.</summary>
    Prologue, Epilogue,

    // ---- what only a driver and a kernel may execute ---------------------------------
    In, Out, Cli, Sti, Hlt,

    // ---- a kernel's --------------------------------------------------------------------------
    RepInsw, RepOutsw,  // rep insw / outsw: RDI or RSI, RCX words, port DX
    Lidt, Lgdt, Invlpg, // [r]
    Ltr,                // r16: the task register
    MovFromCr,          // r64, crN (the number an immediate)
    MovToCr,            // crN, r64
    LoadSegments,       // ds, es, fs, gs, ss <- ax
    LoadCs,             // cs <- r (a far return to the next instruction; RAX destroyed)
    Rdmsr, Wrmsr,       // ECX the MSR, EDX:EAX the value
    Cpuid,              // EAX, ECX in; EAX, EBX, ECX, EDX out
    Rdtsc,              // EDX:EAX
    ReadFlags,          // r64 <- RFLAGS: pushfq; pop r64
    Swapgs,
    SoftInt,            // int imm8: a trap gate other than Linux's, with syscall's registers
}

public sealed class MInstr
{
    public MOp Op { get; init; }
    public List<MOperand> Operands { get; } = new();

    /// <summary>Operand width in bytes: 1, 2, 4 or 8; for SSE arithmetic 4 (single) or 8 (double).</summary>
    public int Width { get; init; } = 8;

    /// <summary>For the conversions: the width of the source operand.</summary>
    public int SourceWidth { get; init; }

    public int Line { get; set; }
    public Cond Cond { get; init; }
    public bool Lock { get; init; }
    public List<MBlock>? Table { get; init; }

    /// <summary>For JmpTable: the symbol its table of block addresses is written under.</summary>
    public string? TableSymbol { get; init; }

    /// <summary>For a call: the XMM registers holding arguments, so they count as read.</summary>
    public int FloatArgs { get; init; }

    /// <summary>For a call: the general registers holding arguments, so they count as read.</summary>
    public int IntArgs { get; init; }

    /// <summary>For a call into C: AL holds the count of vector arguments, so RAX counts as read.</summary>
    public bool NativeAl { get; init; }

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
    public Block? Source { get; init; }
    public int Offset { get; set; }
    public MBlock(string name) => Name = name;
    public override string ToString() => Name;

    public bool EndsUnconditionally
    {
        get
        {
            if (Instrs.Count == 0)
            {
                return false;
            }
            MOp last = Instrs[^1].Op;
            return last is MOp.Jmp or MOp.Ret or MOp.JmpTable or MOp.JmpInd or MOp.Epilogue or MOp.Ud2;
        }
    }
}

/// <summary>What the collector must know at one call site; the x86 table's shape, 8-byte slots.</summary>
public sealed class Safepoint
{
    public uint Registers { get; set; }
    public List<int> SlotOffsets { get; } = new();
}

/// <summary>A function after selection: blocks, frame, and what the allocator decided.</summary>
public sealed class MFunction
{
    public Dictionary<MInstr, Safepoint> Safepoints { get; } = new(ReferenceEqualityComparer.Instance);
    public Function Source { get; }
    public List<MBlock> Blocks { get; } = new();
    public Frame Frame { get; } = new();
    public int NextVReg { get; set; } = MReg.FirstVirtual;

    /// <summary>Which virtual registers are floating point, by id.</summary>
    public HashSet<int> FloatRegs { get; } = new();

    /// <summary>Virtual registers holding plain integers (never references), for the stack maps.</summary>
    public HashSet<int> NotReferences { get; } = new();

    /// <summary>Callee-saved registers the allocator handed out; the prologue saves exactly these.</summary>
    public List<Gpr> SavedRegs { get; } = new();

    /// <summary>
    /// Every callee-saved register is saved, used or not. A function that
    /// enters the collector needs this: a caller's reference may live only in
    /// one of them, and the scan reads memory. See GcRoots.Enter.
    /// </summary>
    public bool SavesEverything { get; set; }

    /// <summary>
    /// The process entry: the loader jumps to it with RSP sixteen-byte aligned
    /// and no return address pushed, so after `push rbp` the frame is eight
    /// bytes off the alignment every call assumes. Its prologue realigns.
    /// </summary>
    public bool RealignsStack { get; set; }

    /// <summary>Bytes of arguments this function passes on the stack at its widest call.</summary>
    public int OutgoingBytes { get; set; }

    public MFunction(Function source) => Source = source;

    public MReg NewReg() => new(NextVReg++);

    public MReg NewFloat()
    {
        MReg r = new(NextVReg++, true);
        FloatRegs.Add(r.Id);
        return r;
    }

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
/// The stack frame of one function, as offsets from RBP.
///
///   [RBP+16..]  incoming arguments beyond the six in registers, 8 bytes each
///   [RBP+8]     return address
///   [RBP+0]     caller's RBP
///   [RBP-N..-1] IR frame slots, then spill slots
///   below that  the callee-saved registers this function uses, then the
///               space outgoing stack arguments are pushed into
///
/// As on x86 the locals sit directly under RBP so every offset is known the
/// moment it is handed out, and the saved registers go under them, since
/// which ones is only known once allocation is done.
/// </summary>
public sealed class Frame
{
    private int _size;
    private readonly Dictionary<FrameSlot, int> _slots = new();

    public int Size => (_size + 7) & ~7;

    public int Allocate(int bytes, int align)
    {
        if (align < 8)
        {
            align = 8;
        }
        _size = (_size + bytes + align - 1) / align * align;
        return -_size;
    }

    public int SlotOffset(FrameSlot slot)
    {
        if (!_slots.TryGetValue(slot, out int off))
        {
            off = Allocate(Math.Max(slot.Bytes, 8), slot.Align);
            _slots[slot] = off;
        }
        return off;
    }

    /// <summary>A spill slot for one register of either class: eight bytes.</summary>
    public int Spill() => Allocate(8, 8);
}
