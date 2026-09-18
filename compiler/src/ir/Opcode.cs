#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

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
