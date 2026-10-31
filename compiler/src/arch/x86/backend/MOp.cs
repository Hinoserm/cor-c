#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Machine opcodes. One entry per instruction form the selector emits; the
/// encoder and the printer switch on this. Newer forms are gated by the selected
/// CPU profile; the default remains 486 with x87.
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
    /// <summary>pushfd and popfd: the flags kept across what a measure inserts (SpillCount).</summary>
    Pushfd, Popfd,
    Setcc,
    /// <summary>
    /// cmovcc r32, r/m32, the P6's conditional move (X86Feature.Cmov): operand 0
    /// takes operand 1 when Cond holds and keeps its own value when it does
    /// not, so it is read as well as written. The load from a memory operand
    /// happens either way, and faults either way (IfConvert).
    /// </summary>
    Cmov,
    // control
    Jmp, Jcc, JmpTable, JmpInd, Call, CallInd, Ret,
    // atomics and strings
    Xadd, Cmpxchg, RepMovsb, RepMovsd, RepStosb, RepStosd, LockOrEsp,
    // x87: operand is a memory location whose width says single/double or dword/qword
    Fld, Fstp, Fild, Fistp, Fadd, Fsub, Fmul, Fdiv, Fchs, Fsqrt, Fucompp, Fnstsw, Sahf, Fnstcw, Fldcw,
    /// <summary>
    /// fucomip st(0), st(1); fstp st(0) -- Fucompp's comparison and its two
    /// pops, with the answer straight in ZF, PF and CF where fnstsw and sahf
    /// carried it through AH: the flags are the same, and EAX is untouched.
    /// Pentium Pro and later only (X86Cpu.FloatCmov).
    /// </summary>
    Fucomipp,
    /// <summary>
    /// cvttss2si r32, m32 (SSE) and cvttsd2si r32, m64 (SSE2): a float or a
    /// double in memory truncated into a general register. They chop
    /// whatever the rounding mode, so no control word changes.
    /// </summary>
    Cvttss2si, Cvttsd2si,
    // ---- scalar SSE and SSE2: floats in the XMM registers (Select.Sse) -------
    //
    // Width 4 is a float (SSE: the F3 forms), 8 a double (SSE2: F2, and 66
    // for the compare). The XMM operands are registers of the allocator's XMM
    // class (MReg.XmmBase on), or memory where Allocator.Foldable says so.
    /// <summary>movss/movsd xmm, m; m, xmm; movaps xmm, xmm between registers.</summary>
    MovF,
    /// <summary>addss/sd, subss/sd, mulss/sd, divss/sd: xmm op= xmm/m.</summary>
    AddF, SubF, MulF, DivF,
    /// <summary>sqrtss/sd xmm, xmm/m.</summary>
    SqrtF,
    /// <summary>ucomiss/ucomisd xmm, xmm/m: ZF, PF and CF as FUCOMIP leaves them.</summary>
    UcomiF,
    /// <summary>cvtss2sd (Width 8) and cvtsd2ss (Width 4): xmm, xmm/m.</summary>
    CvtFToF,
    /// <summary>cvtsi2ss (Width 4) and cvtsi2sd (Width 8): xmm, r/m32.</summary>
    CvtIntToF,
    /// <summary>xorps xmm, xmm: the sign flipped by a mask, or zero.</summary>
    XorPs,
    /// <summary>movd xmm, r/m32 and movd r/m32, xmm (SSE2): a float's bits between the files.</summary>
    MovdToX, MovdFromX,
    // odds and ends
    Int, Int3, Nop, Pause,
    /// <summary>
    /// Opcode.CompilerFence: no instruction (TSO orders loads and stores as
    /// the fence asks), but a barrier to every pass after selection that moves
    /// memory accesses -- the scheduler's (Effect.Barrier) among them.
    /// </summary>
    CompilerFence,
    /// <summary>
    /// Sys.SaveRegisters: no instruction, but EBX, ESI and EDI counted as
    /// written (Roles.ImplicitDefs), so the prologue pushes all three.
    /// </summary>
    SaveRegs,
    /// <summary>
    /// Sys.SpillRegisters: EBX, ESI and EDI as the caller left them into
    /// [operand], [operand+4], [operand+8] -- each saved by the prologue
    /// copied from its slot (push, pop), each not stored as it is.
    /// </summary>
    SpillRegs,
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
    // Closed MM0 regions; no MMX value is live across an IR instruction or call.
    MmxLoad, MmxStore, MmxZero, Emms, Femms,
    MmxAddB, MmxAddW, MmxAddD, MmxSubB, MmxSubW, MmxSubD,
    MmxAnd, MmxOr, MmxXor, MmxMulW,
    MmxMulHighW, ThreeDNowAverageB, ThreeDNowMulRoundW,
    MmxShlW, MmxShlD, MmxShrW, MmxShrD, MmxSarW, MmxSarD,
    MmxLoadD, MmxDuplicateLowWords, MmxWidenUnsignedWords, ThreeDNowIntToFloat, ThreeDNowShortToFloat,
    MmxSaveToTwo, ThreeDNowAddFromTwo, ThreeDNowSubFromTwo, ThreeDNowMulFromTwo, ThreeDNowFloatToInt,
    MmxMultiplyAddW,
    MmxEqualB, MmxEqualW, MmxEqualD, MmxGreaterB, MmxGreaterW, MmxGreaterD,

    // ---- the thread block ----------------------------------------------------
    //
    // Per-thread runtime state lives in a block whose address the operating
    // system keeps in a GDT entry reached through GS, glibc's convention: the
    // block's first word is its own address, so one segment-prefixed load
    // answers a flat address every other instruction can use.
    /// <summary>mov r32, gs:[0] -- this thread's block, by the self pointer.</summary>
    GsSelf,
    /// <summary>mov r32, gs:[disp32] / gs:[reg]: a word of this processor's own area (Sys.PerCpuRead).</summary>
    GsLoadAt,
    GsLoadReg,
    /// <summary>mov gs:[disp32], r32 / gs:[reg], r32: the same, written (Sys.PerCpuWrite).</summary>
    GsStoreAt,
    GsStoreReg,
    /// <summary>
    /// mov r32, fs:[r32] -- on Windows, a word of the thread's own block, at
    /// the offset the register holds: the step that finds the TLS slots
    /// (Select.TlsBaseSymbol).
    /// </summary>
    FsLoad,
    /// <summary>
    /// mov r32, fs:[0] -- on Windows, the innermost SEH registration of the
    /// thread, the head of the chain the TEB starts with.
    /// </summary>
    FsSehChain,
    /// <summary>mov fs:[0], r32 -- a registration made the innermost, or taken off.</summary>
    FsSehChainStore,
    /// <summary>mov gs, r16 -- the selector of the descriptor that block sits behind.</summary>
    SetGs,
    /// <summary>rdmsr / wrmsr: ECX the MSR, EDX:EAX the value.</summary>
    Rdmsr, Wrmsr,
    /// <summary>cpuid: EAX and ECX in; EAX, EBX, ECX, EDX out.</summary>
    Cpuid,
    /// <summary>rdtsc: EDX:EAX.</summary>
    Rdtsc,
    /// <summary>pushfd; pop r32 -- EFLAGS into a register.</summary>
    ReadFlags,
    /// <summary>push r32; popfd -- a register into EFLAGS (the CPUID probe's ID bit: Sys.CpuidPresent).</summary>
    WriteFlags,
    /// <summary>bsf / bsr r32, r/m32: the lowest or highest set bit's index; the source is never zero (Sys.BitScanForward).</summary>
    Bsf, Bsr,
    /// <summary>lock cmpxchg8b m64: EDX:EAX expected, ECX:EBX stored; EDX:EAX what was there (Sys.Cas64).</summary>
    Cmpxchg8b,
    /// <summary>movntq m64, mmN: the register (an MImm, mm0 where none) stored around the caches (the MMX extensions: Sys.StreamCopy64, Packed.StoreNonTemporal).</summary>
    MmxStoreNt,
    /// <summary>prefetchnta m8 (the MMX extensions), 3DNow!'s prefetch m8 and prefetchw m8: a cache line asked for ahead, the last to be written.</summary>
    PrefetchNta, PrefetchLine, PrefetchWrite,
    /// <summary>movd m32, mmN: the register's low four bytes stored (the register an MImm, mm0 where none).</summary>
    MmxStoreD,
    /// <summary>movntdq m128, xmmN: sixteen bytes stored around the caches; the address must be at sixteen. SSE2's.</summary>
    XmmStoreNt,
    /// <summary>sfence: the streaming stores before it seen before any store after.</summary>
    Sfence,
    /// <summary>
    /// The four UTF-16 units at m compared with the four at p:
    /// movq mm0, m; pcmpeqw mm0, p; packsswb mm0, mm0; movd r32, mm0 -- a
    /// byte of r 0xFF for each equal unit, lowest first (plain MMX:
    /// Sys.PackedFindChar4).
    /// </summary>
    MmxFindW4,
    /// <summary>
    /// The eight units at m against the four at p twice: two movq, two
    /// pcmpeqw, packsswb mm0, mm1, pmovmskb r32, mm0 -- a bit of r for each
    /// equal unit, lowest first (the MMX extensions: Sys.PackedFindChar8).
    /// </summary>
    MmxFindW8,
    /// <summary>
    /// The eight bytes at a and at b alike or not: movq mm0, a; pcmpeqd mm0, b;
    /// packssdw mm0, mm0; movd r32, mm0 -- -1 where all eight are (plain MMX:
    /// Sys.PackedEqual8).
    /// </summary>
    MmxEqual8,
    /// <summary>
    /// A packed block's save of the x87 state, at [eax] in 112 bytes, as the
    /// kernel's gfx3d_fpu_save makes it: fnstenv [eax]; the tag word all
    /// empty -- a thread at a system call -- keeps the 28-byte environment,
    /// flagged 0 at +108; otherwise fldenv and fnsave [eax] whole, flagged 1;
    /// then the program's own control word loaded, a double's 53 bits
    /// (0x027F, as the entry sets it: MachineIntrinsics.DoublePrecision). No
    /// MMX instruction, so a 486 runs it.
    /// </summary>
    PackedSaveX87,
    /// <summary>
    /// A PACKED OPERATION's instruction, mm0 combined with memory: Operands
    /// the opcode's second byte (0F xx, an MImm) and the memory (Lowering.
    /// PackedIntrinsic). One MOp for the forty-odd: which the processor must
    /// have is read off the opcode (Isa.Requires).
    /// </summary>
    MmxOp,
    /// <summary>PSLLW/D/Q, PSRLW/D/Q, PSRAW/D of mm0 by a constant: the group (0F 71, 72, 73), its /digit and the count, MImms.</summary>
    MmxShiftImm,
    /// <summary>PSHUFW mm0, m64, order: the memory and the order (an MImm). The MMX extensions'.</summary>
    MmxShuffleW,
    /// <summary>PEXTRW r32, mm0, place: the register and the place (an MImm). The MMX extensions'.</summary>
    MmxExtractW,
    /// <summary>PINSRW mm0, r32, place: the register and the place (an MImm). The MMX extensions'.</summary>
    MmxInsertW,
    /// <summary>MOVUPS xmmN, m128: which of xmm0 and xmm1 (an MImm), and the memory. SSE's, unaligned.</summary>
    XmmLoad,
    /// <summary>MOVUPS m128, xmm0.</summary>
    XmmStore,
    /// <summary>MOVLPS xmmN, m64: two floats' eight bytes into the low half.</summary>
    XmmLoadLow,
    /// <summary>MOVLPS m64, xmm0.</summary>
    XmmStoreLow,
    /// <summary>
    /// An SSE or SSE2 operation, xmm0 with xmm1 (a one-operand one, of xmm1
    /// into xmm0): its prefix (0, 66, F2, F3) and opcode (0F xx), and an
    /// immediate where it has one (CMPPS's predicate, SHUFPS's and PSHUFD's
    /// order), MImms. Which the processor must have is read off them
    /// (Isa.Requires).
    /// </summary>
    XmmOp,
    /// <summary>
    /// An SSE or SSE2 operation between named registers (Selector's packed
    /// registers): its prefix and opcode, the destination xmmD (an MImm), the
    /// source -- xmmS (an MImm) or memory -- and an immediate where it has
    /// one. Which the processor must have is read off them, as XmmOp's.
    /// </summary>
    XmmOpRR,
    /// <summary>PSLLW/D/Q, PSRLW/D/Q, PSRAW/D, PSLLDQ, PSRLDQ of xmm0 by a constant: the group (66 0F 71, 72, 73), its /digit and the count. SSE2's.</summary>
    XmmShiftImm,
    /// <summary>PEXTRW r32, xmm0, place. SSE2's.</summary>
    XmmExtractW,
    /// <summary>PINSRW xmm0, r32, place. SSE2's.</summary>
    XmmInsertW,
    /// <summary>A 3DNow! operation, mm0 with memory: its suffix (0F 0F /r xx, an MImm) and the memory.</summary>
    ThreeDNowOp,
    /// <summary>The save's give-back at [eax]: fldenv where +108 is 0, frstor where not.</summary>
    PackedRestoreX87,
    /// <summary>A save that keeps the XMM registers too: fxsave [eax] (16-aligned), fninit, MXCSR 0x1F80 loaded from [eax+512], and the program's control word (0x027F), as PackedSaveX87's.</summary>
    PackedSaveFxsr,
    /// <summary>fxrstor [eax].</summary>
    PackedRestoreFxsr,
    /// <summary>xor r, r; mov r16, cs -- the code selector, its low two bits the privilege level.</summary>
    ReadCs,
    /// <summary>ltr r16.</summary>
    Ltr,
    /// <summary>cs &lt;- r: push r; push the next instruction; retf. EAX destroyed.</summary>
    LoadCs,
    /// <summary>xor r32, r32; mov r16, gs -- the selector in GS, zero-extended on every processor.</summary>
    GetGs,
    /// <summary>
    /// call rel32 to one of the backend's own stubs, which keeps every
    /// register: its operands after the target are the registers it reads,
    /// and it destroys none, so nothing live is spilled around it.
    /// </summary>
    CallKeep,
    /// <summary>
    /// CallKeep through a register: `call r`, to a stub of another image
    /// (a module's to its kernel's store sequences, X86Backend.RefStoreStub),
    /// whose address a position-independent object reads from its GOT. Its
    /// first operand is the target; the rest are the registers the stub
    /// reads, as CallKeep's.
    /// </summary>
    CallKeepInd,
    /// <summary>
    /// CallKeep to a stub that answers in EAX (X86Backend's reference
    /// exchanges): every other register kept, EAX written.
    /// </summary>
    CallKeepEax,
    /// <summary>CallKeepEax through a register, as CallKeepInd is CallKeep's.</summary>
    CallKeepEaxInd,
    /// <summary>
    /// Add or subtract one, every flag but CF set as ADD and SUB set them:
    /// made only at the end, by Peephole.IncDec, where nothing reads CF.
    /// </summary>
    Inc, Dec,
}
