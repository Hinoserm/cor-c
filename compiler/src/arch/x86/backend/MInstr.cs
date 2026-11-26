#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

public sealed class MInstr : MOperandList
{
    public MOp Op { get; init; }

    /// <summary>
    /// Whether control never runs on past this instruction: a jump that is
    /// not conditional, a return, an epilogue, and the trap after what never
    /// returns -- `ud2`, or `int3` with no operand (i386's, which has no ud2
    /// on a 486). An `int3` WITH an operand is a breakpoint the program runs
    /// on from (Opcode.Trap, both selectors), and ends nothing.
    /// </summary>
    public bool EndsFlow => Op is MOp.Jmp or MOp.Ret or MOp.JmpTable or MOp.JmpInd or MOp.Epilogue or MOp.Ud2 || Op == MOp.Int3 && Operands.Count == 0;
    /// <summary>Its operands, held in the instruction itself (MOperandList).</summary>
    public MOperandList Operands => this;

    /// <summary>Operand width in bytes: 1, 2, 4, or 8 for an x87 qword. Registers are always 32-bit except at width 1 and 2.</summary>
    public int Width { get; init; } = X86Mode.Word;

    /// <summary>
    /// The source's width where it is not the destination's: an integer's
    /// converted to a float (CvtIntToF -- long mode's encoder writes CVTSI2SD
    /// from a 64-bit register for eight; i386's has the one form and reads
    /// nothing here), a float's converted to an integer (CvtFToInt).
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>Long mode: a jump table's symbol (JmpTable).</summary>
    public string? TableSymbol { get; init; }

    /// <summary>Long mode: how many of a call's arguments are in XMM registers and how many in general ones (System V).</summary>
    public int FloatArgs { get; init; }
    public int IntArgs { get; init; }

    /// <summary>Long mode: a call into C's variadic convention, AL the count of vector arguments.</summary>
    public bool NativeAl { get; init; }

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

    /// <summary>
    /// For CvtIntToF, CvtFToF and SqrtF: its register zeroed (PXOR or XORPS,
    /// CpuModel.ZeroByPxor) first, in the same instruction as far as every pass is concerned.
    /// Each writes the low lane alone and keeps the rest, so it waits on
    /// whatever last wrote the register; a CPU that takes the zeroing for a value independent of the old one starts it at once
    /// (CpuModel.BreaksMerge, which says where). The encoder leaves it out
    /// where the register is also the source (Encoder.ZeroFirst; long mode's
    /// encoder asks CpuModel itself for every such instruction, BreakMergeL,
    /// whoever made it). One
    /// instruction, so the zeroing is the conversion's own register and no
    /// pass parts them or drops it as dead (Peephole.DeadXmmMoves); every
    /// rebuild of an instruction carries it (X86Target.Build, LoopRotate.Clone,
    /// Peephole's spill folding, Select.Owning).
    /// </summary>
    public bool ZeroFirst { get; set; }

    /// <summary>
    /// For a call: into C (Ir.NativeCall). Carried through the passes after
    /// selection so a call that is rewritten stays one; the collector needs
    /// nothing of it, since the thread's callee-saved registers were copied
    /// into its block (Tls.Spill) when it counted itself stopped, and C
    /// preserves them for the length of the call.
    /// </summary>
    public bool Native { get; init; }

    /// <summary>Whether a LOCK prefix goes in front (Xadd, Cmpxchg).</summary>
    public bool Lock { get; init; }

    /// <summary>
    /// The source named this instruction itself, after its own processor
    /// check: a machine intrinsic (Sys.Cpuid, Sys.ReadTsc, Sys.ReadMsr,
    /// Sys.WriteMsr, Sys.Invlpg) that a kernel calls only once it has asked
    /// the processor what it is. The emitted-code audit lets such an
    /// instruction through whatever --cpu says, the same promise `.runtime
    /// mmx` makes in the assembler; everything the compiler chose by itself
    /// is held to the profile. Carried by every pass that rebuilds an
    /// instruction.
    /// </summary>
    public bool Probed { get; init; }

    /// <summary>
    /// Selected inside a kept state (Selector's region walk, or a $inside
    /// copy's whole body: 1 the x87's save, 2 FXSAVE's), between the save of
    /// the thread's state and its give-back: the
    /// emitted-code audit lets it use the MMX registers -- and, at 2, SSE's --
    /// in freestanding code. Carried by every pass that rebuilds an
    /// instruction, as Probed is.
    /// </summary>
    public byte OwnsState { get; init; }

    /// <summary>For InlineAsm: the block, its plan and its slots (InlineAsmCode). Carried by every pass that rebuilds an instruction, as Probed is.</summary>
    public InlineAsmCode? Asm { get; init; }

    /// <summary>For JmpTable: the table's cases, in index order.</summary>
    public List<MBlock>? Table { get; init; }

    /// <summary>For a Call to a symbol: how the reference is made. Rel32 today; Plt32 in a PIC function later.</summary>
    public RelocKind CallReloc { get; init; } = RelocKind.Rel32;

    public MInstr(MOp op, params MOperand[] operands)
    {
        Op = op;
        Operands.AddRange(operands);
    }

    // ONE, TWO AND THREE OPERANDS WRITTEN OUT: nearly every instruction has
    // one of those counts, and through `params` each was an array made only
    // to be copied into Operands and dropped.
    public MInstr(MOp op)
    {
        Op = op;
    }

    public MInstr(MOp op, MOperand a)
    {
        Op = op;
        Operands.Add(a);
    }

    public MInstr(MOp op, MOperand a, MOperand b)
    {
        Op = op;
        Operands.Add(a);
        Operands.Add(b);
    }

    public MInstr(MOp op, MOperand a, MOperand b, MOperand c)
    {
        Op = op;
        Operands.Add(a);
        Operands.Add(b);
        Operands.Add(c);
    }

    public MReg Reg(int i) => (MReg)Operands[i];

    /// <summary>
    /// The same instruction on other operands: every property kept. What a
    /// pass that renames a register or folds an operand makes, so that
    /// nothing an instruction carries -- of either mode -- is dropped by a
    /// list of properties written out where it is rebuilt.
    /// </summary>
    public MInstr WithOperands(IReadOnlyList<MOperand> operands)
    {
        // (One list of what an instruction carries: Rebuilt's.)
        MInstr made = Rebuilt(Table);
        for (int k = 0; k < operands.Count; k++) made.Operands.Add(operands[k]);
        return made;
    }

    /// <summary>
    /// The same instruction with no operands yet and this jump table (its
    /// own, or the blocks an allocator laid it out to): what an allocator's
    /// target rebuilds an instruction from, so that a rebuild drops nothing
    /// either -- each target's list of fields had left out some of the
    /// other mode's.
    /// </summary>
    public MInstr Rebuilt(List<MBlock>? table) => new(Op)
    {
        Width = Width, SourceWidth = SourceWidth, TableSymbol = TableSymbol, FloatArgs = FloatArgs, IntArgs = IntArgs, NativeAl = NativeAl,
        Line = Line, Cond = Cond, ZeroFirst = ZeroFirst, Native = Native, Lock = Lock, Probed = Probed, OwnsState = OwnsState, Asm = Asm,
        Table = table, CallReloc = CallReloc,
    };
}
