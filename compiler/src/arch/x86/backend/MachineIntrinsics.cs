#nullable enable

namespace Corsac.Lang.X86;

/// <summary>
/// The reserved callee names that stand for a machine instruction rather
/// than for a function.
///
/// Lowering emits a `Sys.PortOut8(...)` as a call to one of these instead of
/// as a new IR opcode, and the selector replaces the call with the
/// instruction. The point of the indirection is that everything in between --
/// dead-code elimination, common-subexpression elimination, store
/// elimination, the verifier -- already knows that a call may do anything and
/// therefore may not be moved, duplicated or removed, which is exactly the
/// rule these instructions need. A new opcode would have to be taught to each
/// pass, and a pass that had not been taught would be silently wrong.
///
/// The names begin with a prefix no mangled name can produce, and no symbol
/// is ever emitted for them: a target that does not recognise one is a target
/// that cannot do port I/O, and it will say so at selection.
/// </summary>
internal static class MachineIntrinsics
{
    public const string Prefix = "__x86.i.";

    public const string In8 = Prefix + "in8";
    public const string In16 = Prefix + "in16";
    public const string In32 = Prefix + "in32";
    public const string Out8 = Prefix + "out8";
    public const string Out16 = Prefix + "out16";
    public const string Out32 = Prefix + "out32";
    public const string InString16 = Prefix + "insw";
    public const string OutString16 = Prefix + "outsw";
    public const string Cli = Prefix + "cli";
    public const string Sti = Prefix + "sti";
    public const string Hlt = Prefix + "hlt";
    public const string Lidt = Prefix + "lidt";
    public const string Lgdt = Prefix + "lgdt";
    public const string Invlpg = Prefix + "invlpg";
    public const string ReadCr = Prefix + "readcr";
    public const string WriteCr = Prefix + "writecr";
    public const string LoadSegments = Prefix + "loadsegments";
    public const string ThreadBlock = Prefix + "threadblock";
    /// <summary>Windows: this thread's block put in the runtime's TLS slot (the TEB's), as TlsSetValue would.</summary>
    public const string SetTlsBlock = Prefix + "settls";
    /// <summary>Windows: the head of the thread's SEH chain, fs:[0].</summary>
    public const string SehChain = Prefix + "sehchain";
    /// <summary>Windows: a new head for the thread's SEH chain, into fs:[0].</summary>
    public const string SetSehChain = Prefix + "setsehchain";
    /// <summary>The card for a slot the generational barrier marks (CardMarks), set by the object's stub.</summary>
    public const string CardMark = Prefix + "cardmark";
    /// <summary>The snapshot barrier's slow path, slot and value, through the object's stub.</summary>
    public const string Barrier = Prefix + "barrier";
    /// <summary>
    /// A reference stored with its whole barrier, slot and value: the
    /// Marking test, the snapshot barrier, the store and the card, in one
    /// sequence of the image's own (X86Backend.RefStoreStub) that a thread is
    /// never stopped inside (CardMarks.FuseStores).
    /// </summary>
    public const string RefStore = Prefix + "refstore";
    /// <summary>The same for a store that takes no snapshot barrier, only the card: a new block's.</summary>
    public const string CardStore = Prefix + "cardstore";
    /// <summary>A reference exchanged as a sequence, slot and value, answering the old one (Sys.ExchangeReference).</summary>
    public const string RefExchange = Prefix + "refxchg";
    /// <summary>The same where the slot holds the expected one: slot, expected, value; answers what it held (Sys.CompareExchangeReference).</summary>
    public const string RefCompareExchange = Prefix + "refcas";
    public const string SetGs = Prefix + "setgs";
    /// <summary>A word of this processor's own area, gs-relative: one instruction, never split by a preemption (Sys.PerCpuRead).</summary>
    public const string PerCpuRead = Prefix + "percpuread";
    public const string PerCpuWrite = Prefix + "percpuwrite";
    public const string GetGs = Prefix + "getgs";
    /// <summary>
    /// A use of its operand and nothing else: `test r, r`. What keeps an
    /// object C was given a pointer into alive until the C call is over.
    /// </summary>
    public const string KeepAlive = Prefix + "keepalive";
    public const string ReadMsr = Prefix + "rdmsr";
    public const string WriteMsr = Prefix + "wrmsr";
    public const string Cpuid = Prefix + "cpuid";
    public const string ReadTsc = Prefix + "rdtsc";
    public const string ReadFlags = Prefix + "readflags";
    public const string ReadCodeSegment = Prefix + "readcs";
    public const string SwapGs = Prefix + "swapgs";
    public const string LoadTaskRegister = Prefix + "ltr";
    public const string LoadCodeSegment = Prefix + "loadcs";
    /// <summary>
    /// At the process entry, the loader's finaliser the System V ABI passes
    /// in EDX (i386) or RDX (x86-64); zero from a kernel that loaded a static
    /// program. Only as the entry stub's first instruction.
    /// </summary>
    public const string LoaderFini = Prefix + "loaderfini";
    /// <summary>
    /// The x87's precision control set to a double's 53 bits, at the process
    /// entry. Linux and a reset processor start at the extended format's 64,
    /// where every double operation rounds twice -- to 64 bits in the
    /// register, then to 53 at the store -- and about one in two thousand
    /// lands a last place away from the correctly rounded result .NET's SSE2
    /// arithmetic gives: 1 + (2^-53 + 2^-105) came out 1. Threads inherit the
    /// control word. Nothing on x86-64, whose doubles are SSE2's, nor without
    /// an FPU.
    /// </summary>
    public const string DoublePrecision = Prefix + "doubleprecision";
}
