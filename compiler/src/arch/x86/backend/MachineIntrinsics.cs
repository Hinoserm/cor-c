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
    /// <summary>bsf / bsr: the lowest or highest set bit of a nonzero 32-bit value (Sys.BitScanForward, BitScanReverse).</summary>
    public const string BitScanForward = Prefix + "bsf";
    public const string BitScanReverse = Prefix + "bsr";
    /// <summary>Whether the processor has CPUID: EFLAGS.ID flipped and read back (Sys.CpuidPresent).</summary>
    public const string CpuidPresent = Prefix + "hascpuid";
    /// <summary>cpuid's EAX or EDX alone, of a leaf with subleaf 0 (Sys.CpuidEax, CpuidEdx).</summary>
    public const string CpuidEax = Prefix + "cpuideax";
    public const string CpuidEdx = Prefix + "cpuidedx";
    /// <summary>A 64-bit compare-exchange on i386: lock cmpxchg8b (Sys.Cas64).</summary>
    public const string Cas64 = Prefix + "cas64";
    /// <summary>
    /// The MMX registers' bulk and scanning instructions, for the hosted
    /// runtime's routines that reach them only after Processor says the
    /// processor has them (Runtime.StreamCopy, PackedStringFind): i386 only.
    /// </summary>
    public const string StreamPrefetch = Prefix + "prefetchnta";
    public const string StreamPrefetchLine = Prefix + "prefetch";
    public const string StreamCopy64 = Prefix + "streamcopy64";
    public const string StreamFill64 = Prefix + "streamfill64";
    public const string StreamEnd = Prefix + "streamend";
    public const string PackedFindChar4 = Prefix + "findchar4";
    public const string PackedFindChar8 = Prefix + "findchar8";
    public const string PackedEqual8 = Prefix + "equal8";
    public const string PackedEnd = Prefix + "emms";
    /// <summary>
    /// A packed block's taking and giving back of the x87, MMX and SSE state
    /// in freestanding code (Sys.PackedEnter, PackedLeave: using
    /// (Packed.Mmx())), at the save area's address: as lowering writes them
    /// (PackedSave, PackedRestore: the block's kind, number and variant's
    /// features), then as the backend settles them from what the block's
    /// code uses (Selector.SettlePackedSaves) -- the x87's save, FXSAVE, or
    /// none. The code between them may use the packed registers
    /// (Selector.PackedRegions).
    /// </summary>
    public const string PackedSavePrefix = Prefix + "packedsave.";
    public const string PackedRestorePrefix = Prefix + "packedrestore.";
    public static string PackedSave(int kind, int number, int features) => PackedSavePrefix + kind + "." + number + "." + features;
    public static string PackedRestore(int kind, int number, int features) => PackedRestorePrefix + kind + "." + number + "." + features;
    public const string PackedSaveX87 = Prefix + "packedsavex87";
    public const string PackedRestoreX87 = Prefix + "packedrestorex87";
    public const string PackedSaveFxsr = Prefix + "packedsavefxsr";
    public const string PackedRestoreFxsr = Prefix + "packedrestorefxsr";
    /// <summary>
    /// THE PACKED OPERATIONS (Lowering.PackedIntrinsic), each one MMX, SSE,
    /// SSE2 or 3DNow! instruction, named with what the instruction holds in
    /// its own bytes: a two-operand one by its opcode (the result's address,
    /// the first operand's, the second's); a shift by group, /digit and
    /// count; a shuffle by its order; a word's extraction or insertion by its
    /// place. The variant of the block they are in runs only where the
    /// processor has its features (Sys.PackedChoose), so they are Probed.
    /// </summary>
    public const string PackedOpPrefix = Prefix + "pk.";
    public static string PackedOp(byte op) => PackedOpPrefix + "op." + op.ToString("x2");
    public static string PackedShift(byte group, byte sub, int count) => PackedOpPrefix + "shift." + group.ToString("x2") + "." + sub + "." + count;
    public static string PackedShuffle(int order) => PackedOpPrefix + "shuffle." + order;
    public static string PackedExtract(int at) => PackedOpPrefix + "extract." + at;
    public static string PackedInsert(int at) => PackedOpPrefix + "insert." + at;
    public const string PackedStoreNonTemporal = PackedOpPrefix + "storent";
    /// <summary>Eight bytes moved through mm0 (Packed.Load, Packed.Store in a variant with MMX): one movq each way, not two 32-bit moves.</summary>
    public const string PackedMove = PackedOpPrefix + "mov";
    /// <summary>An SSE2 integer operation on sixteen bytes: its prefix and opcode (66 0F xx).</summary>
    public static string PackedWide(byte prefix, byte op) => PackedOpPrefix + "x." + prefix.ToString("x2") + "." + op.ToString("x2");
    public static string PackedWideShift(byte group, byte sub, int count) => PackedOpPrefix + "xshift." + group.ToString("x2") + "." + sub + "." + count;
    public static string PackedWideShuffle(byte prefix, int order) => PackedOpPrefix + "xshuffle." + prefix.ToString("x2") + "." + order;
    public static string PackedWideExtract(int at) => PackedOpPrefix + "xextract." + at;
    public static string PackedWideInsert(int at) => PackedOpPrefix + "xinsert." + at;
    /// <summary>An SSE float operation: on sixteen bytes or two floats' eight, its prefix and opcode, its immediate (a compare's predicate, a shuffle's order; -1 none), whether it takes one operand.</summary>
    public static string PackedFloat(bool wide, byte prefix, byte op, int immediate, bool unary)
        => PackedOpPrefix + (wide ? "xf." : "f2.") + prefix.ToString("x2") + "." + op.ToString("x2") + "." + immediate + "." + (unary ? 1 : 0);
    /// <summary>A 3DNow! operation on two floats: its suffix (0F 0F /r xx).</summary>
    public static string Packed3DNow(byte suffix) => PackedOpPrefix + "3d." + suffix.ToString("x2");
    public const string PackedFence = PackedOpPrefix + "fence";
    public const string PackedEndFast = Prefix + "femms";
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
