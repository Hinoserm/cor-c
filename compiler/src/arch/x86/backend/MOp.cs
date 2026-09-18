#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

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
    // Closed MM0 regions; no MMX value is live across an IR instruction or call.
    MmxLoad, MmxStore, MmxZero, Emms, Femms,

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
