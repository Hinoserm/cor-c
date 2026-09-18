#nullable enable
namespace Corsac;

// The CORSAC instruction set.
//
// Fixed 64-bit instructions, 16 integer and 16 floating-point registers,
// Harvard split: code is an array of words indexed by word address and is
// never writable from the data space.
//
//   63:47  opcode     17   8 bits unit + 9 bits operation
//   46:40  rd          7   128 addressable registers, 16 defined
//   39:33  rs1         7
//   32:26  rs2         7
//   25:19  rs3         7   fourth operand: select, fma, cas
//   18:3   immediate  16
//    2:0   size        3   operand width
//
// Forms that do not need every register field overlay the spare ones into the
// immediate, which is why those fields sit where they do: the wide immediate
// (30 bits), branch displacement (23 bits) and jump displacement (44 bits) are
// each a single contiguous run ending at bit 3.
//
// Sixteen registers is a decision about a JIT that does not exist yet. With r0
// costing no machine register, fifteen live registers map into roughly the
// twelve or thirteen x86-64 general registers a translator has spare, and the
// sixteen floating-point registers match x86-64's sixteen XMM exactly.

/// <summary>
/// How much authority the running code has. This is part of the instruction
/// set contract: every opcode declares the outermost ring allowed to execute
/// it, so the type belongs with ISA metadata rather than one CPU engine.
/// Smaller ring numbers are more privileged; ring 0 is the kernel and ring 7
/// is the default for ordinary programs.
/// </summary>
public enum ProtectionRing : byte
{
    Ring0 = 0,
    Ring1 = 1,
    Ring2 = 2,
    Ring3 = 3,
    Ring4 = 4,
    Ring5 = 5,
    Ring6 = 6,
    Ring7 = 7,
}

/// <summary>
/// The functional unit an opcode belongs to. Units 0x00-0x02 are mandatory;
/// everything else may be absent, in which case its opcodes trap and the
/// handler may emulate them.
/// </summary>
public enum Unit : byte
{
    CoreInt = 0x00,
    Memory  = 0x01,
    Control = 0x02,
    Fpu     = 0x03,
    Context = 0x04,
    Vector  = 0x05,
    Mmu     = 0x06,
    Iommu   = 0x07,
    /// <summary>Channel I/O: the bus, and everything hanging off it.</summary>
    Io      = 0x08,

    /// <summary>
    /// The interval timer, and it is MANDATORY: every processor has one.
    ///
    /// It is part of the processor rather than a card because preemption is
    /// per-processor. A single timer somewhere on the bus can interrupt one
    /// processor and would have to be asked to interrupt the others, so a
    /// scheduler built on it would have to know how many timers the machine had
    /// and which processors they could reach. With one in every socket a
    /// scheduler never asks: whatever it is running on can always interrupt
    /// itself.
    ///
    /// Appended rather than filed with its relatives, because a unit number is
    /// the top of an opcode: inserting one would renumber every instruction
    /// above it and silently repoint every image already built.
    /// </summary>
    Timer   = 0x09,

    /// <summary>
    /// Strings, and MANDATORY: the compiler emits these without asking, so
    /// there is no fallback path to keep working.
    ///
    /// Mandatory because the reason for it is CORRECTNESS before speed. A
    /// dictionary keyed by string compares its keys by address today, which is
    /// a wrong-answer bug rather than a slow path, and the instruction that
    /// fixes it has to be one every processor has. That the lexer and parser
    /// are the string-heaviest code we own -- and are where a self-hosting
    /// compiler spends its time -- is the second reason, not the first.
    /// </summary>
    String  = 0x0A,
    Decimal = 0x0B,
    Cache   = 0x0C,

    /// <summary>
    /// Optional debug, trace, watchpoint, and performance-monitoring unit.
    /// The external maintenance connector is a physical service interface to
    /// this unit; it is not an instruction or a host-program back door.
    /// </summary>
    Debug   = 0x0D,
    Crypto  = 0x0E,
    ManagedRuntime = 0x0F,
    BitManipulation = 0x10,
    Compression = 0x11,
}

/// <summary>
/// Operand width. This is a width field and not a precision field: the wide
/// values are how vector operations arrive, so the same opcode widens rather
/// than the instruction set growing.
/// </summary>
public enum Size : byte
{
    B    = 0,   // 8-bit
    H    = 1,   // 16-bit
    W    = 2,   // 32-bit, or f32
    D    = 3,   // 64-bit, or f64 -- the default for both banks
    V128 = 4,
    V256 = 5,
    V512 = 6,
    Bad  = 7,   // reserved; traps
}

/// <summary>
/// Opcodes, encoded as (unit &lt;&lt; 9) | operation. The hex reads as the unit in
/// the high byte-and-a-bit: unit 0 is 0x0000-0x01FF, unit 1 is 0x0200-0x03FF,
/// and so on.
/// </summary>
public enum Op : int
{
    // ---- unit 0x00, core integer ------------------------------------
    Halt  = 0x0000,
    Nop   = 0x0001,
    Wait  = 0x0002,   // the correct way to idle; everything we ship uses it
    Brk   = 0x0003,

    Add   = 0x0010,
    Sub   = 0x0011,
    Mul   = 0x0012,
    MulH  = 0x0013,   // high half of the product -- without this, fixed point
    MulHU = 0x0014,   // is impossible, and the base CPU has no f128
    Div   = 0x0015,
    DivU  = 0x0016,
    Mod   = 0x0017,
    ModU  = 0x0018,

    And   = 0x0020,
    Or    = 0x0021,
    Xor   = 0x0022,
    Shl   = 0x0023,
    Shr   = 0x0024,   // logical
    Sar   = 0x0025,   // arithmetic

    /// <summary>
    /// Rotate, within the operand width rather than within 64 bits.
    ///
    /// The machine had no rotate at all, which is a strange gap for a
    /// load-store design of this era: MD5, SHA, ChaCha, AES and Blowfish are
    /// all rotate-heavy, and without it every round costs a shift, a shift and
    /// an or. The width matters more than it looks -- <c>rol.w</c> rotates
    /// within 32 bits, and getting that wrong silently breaks every hash.
    /// </summary>
    Rol   = 0x0026,
    Ror   = 0x0027,

    /// <summary>rd = the low <c>imm</c> bits set. Saves a shift and a subtract
    /// at every bitfield site in the kernel.</summary>
    Msk   = 0x0028,

    Seq   = 0x0030,
    Sne   = 0x0031,
    Slt   = 0x0032,
    SltU  = 0x0033,

    CSel  = 0x0040,   // rd = rs3 != 0 ? rs1 : rs2 -- deletes a basic block
    Min   = 0x0041,
    Max   = 0x0042,
    MinU  = 0x0043,
    MaxU  = 0x0044,

    AddI  = 0x0050,
    MulI  = 0x0051,
    AndI  = 0x0052,
    OrI   = 0x0053,
    XorI  = 0x0054,
    ShlI  = 0x0055,
    ShrI  = 0x0056,
    SarI  = 0x0057,
    SltI  = 0x0058,
    SltIU = 0x0059,

    Zxt   = 0x0060,   // zero-extend to 64; sign-extension is implicit in size
    Lui   = 0x0061,
    // 0x0062 was ldk, which indexed a constant table held beside the machine.
    // With code and data in one address space a wide constant is data, and
    // loading one is an ordinary load. The number is left vacant rather than
    // reused, so an old image fails to decode instead of doing the wrong thing.

    /// <summary>
    /// rd = the address of THIS INSTRUCTION plus the immediate.
    ///
    /// The one thing missing before a program could be written without knowing
    /// where it would live. The machine could already LOAD relative to the
    /// program counter (ldpc), which reaches a program's own constants; what it
    /// could not do was produce an ADDRESS relative to it, and every reference
    /// to a static, a string literal or a method was therefore an absolute
    /// number that a loader had to go back and correct.
    ///
    /// With this, the distance from an instruction to the thing it names is
    /// fixed when the program is built and stays true wherever it is loaded --
    /// so the code needs no correcting at all, and, far more usefully, the code
    /// of two copies of one program is the SAME BYTES. Text that does not
    /// depend on where it was put is text several processes can share one
    /// physical copy of, which is what a shared library is.
    ///
    /// Relative to the instruction itself rather than to the one after it. Both
    /// conventions exist in the world; this one makes the arithmetic at build
    /// time `target - here` with nothing to remember.
    /// </summary>
    Adr   = 0x0063,

    /// <summary>
    /// rd = the library data base plus the immediate.
    ///
    /// The counterpart of adr for the one thing adr cannot reach. Shared
    /// library text is ONE physical copy read by every process, so every
    /// instruction in it sees the same program counter -- which means a
    /// pc-relative address is the same address for everybody, and a library
    /// with a mutable static would have every process writing over the others.
    ///
    /// So library data hangs off a register the KERNEL owns and swaps when it
    /// switches process, exactly as a real machine does thread-local storage:
    /// x86 keeps it in a segment register, ARM in TPIDR, and this in a control
    /// register. One copy of the code, one base register, a private block of
    /// statics per process, and the same instruction reaches a different word
    /// depending on who is running it.
    /// </summary>
    Adl   = 0x0064,

    // ---- unit 0x02, control and local PIC --------------------------
    // Mandatory polite spin-loop hint.  The retired SMP encoding at 0x0821
    // is deliberately not decoded; the architecture manual assigns Pause
    // here, independent of optional multiprocessor hardware.
    Pause = 0x0450,

    // ---- counting and bit manipulation ------------------------------
    //
    // Cheap combinational logic that every processor has, so it belongs in the
    // mandatory unit rather than behind a capability bit: an optional opcode is
    // one a compiler must have a software fallback for, and a fallback for
    // "count the set bits" is not a fallback, it is a loop nobody wanted.
    //
    // The Cray-1 and the CDC 6600 both shipped population count as a headline
    // feature, so the period argument is settled. Only the WIDE bit work --
    // matrix transpose, deposit and extract by mask -- is worth making optional.

    /// <summary>Population count. Allocators, hamming distance, constant-time
    /// compare.</summary>
    Popc  = 0x0065,

    /// <summary>Leading and trailing zero count. Allocator size classes, bignum
    /// normalisation, Math.ILogB. Of ZERO these answer the operand width in
    /// bits rather than something undefined, which is an x86 wart worth not
    /// inheriting.</summary>
    Clz   = 0x0066,
    Ctz   = 0x0067,

    /// <summary>
    /// Byte swap within the operand width.
    ///
    /// SHA is big-endian, the Configuration ROM is little-endian, and the bus
    /// unit converts byte order at the card boundary -- so this is done
    /// constantly in software today, several instructions at a time.
    /// </summary>
    Bswap = 0x0068,

    /// <summary>Bit reverse. FFT ordering, CRC, and what a DES permutation
    /// is made of.</summary>
    Brev  = 0x0069,

    /// <summary>
    /// Bitfield extract and insert, position and length packed in the
    /// immediate as <c>pos | (len &lt;&lt; 8)</c>.
    ///
    /// The kernel is full of <c>(x &gt;&gt; n) &amp; mask</c>: page-table entries, type
    /// descriptors, instruction decode and every bus-unit register are
    /// bitfields. A length of zero yields zero rather than trapping.
    /// </summary>
    Bfe   = 0x006A,
    Bfi   = 0x006B,

    /// <summary>Bit by index. The 68000 shipped exactly these three.</summary>
    Bset  = 0x006C,
    Bclr  = 0x006D,
    Btst  = 0x006E,

    /// <summary>Parity of the operand. DES key schedule, serial framing.</summary>
    Par   = 0x006F,

    /// <summary>
    /// Attempt to read one conditioned entropy sample without acquiring an
    /// optional-unit context and without faulting merely because no entropy
    /// source is present or ready. Rd receives the sample (zero on failure)
    /// and rs1 receives one on success or zero on failure. The two destination
    /// registers must be distinct.
    /// </summary>
    RandTry = 0x0070,

    // ---- unit 0x01, memory ------------------------------------------
    Ld    = 0x0200,   // sign-extending, width from the size field
    Ldu   = 0x0201,   // zero-extending
    St    = 0x0202,

    /// <summary>
    /// Stores relative to this instruction, using the selected width.
    ///
    /// The machine could read its own constants without knowing where it had
    /// been loaded and could not WRITE its own statics the same way, which is
    /// half a feature: every program came out position-independent except for
    /// the instructions that assigned to a static, and those were enough to
    /// make the whole image depend on its address again.
    ///
    /// rd is the SOURCE here, as it is in an ordinary store.
    /// </summary>
    StPc  = 0x0204,

    /// <summary>
    /// Load and store relative to the library data base. See <see cref="Adl"/>
    /// for why that register exists.
    ///
    /// A library's statics may use every scalar width.
    /// </summary>
    LdL   = 0x0205,
    StL   = 0x0206,

    Ldx   = 0x0210,   // register offset, index scaled by the size
    LdxU  = 0x0211,
    Stx   = 0x0212,

    /// <summary>
    /// rd = the selected-width value at Pc + imm. The only way to reach data
    /// from code that does not know where it was loaded.
    ///
    /// An absolute address does not fit in an instruction: the immediate is 30
    /// bits and the address space is 48, so a program living high — firmware,
    /// for one — cannot name its own constant pool at all. Relative to the
    /// program counter it always can, and the image becomes position
    /// independent as a side effect.
    /// </summary>
    LdPc  = 0x0203,

    Fld   = 0x0220,
    Fst   = 0x0221,

    PushM = 0x0230,   // mask bits select r0-r15
    PopM  = 0x0231,
    PushM2 = 0x0232,
    PopM2  = 0x0233,

    // Block operations, in the spirit of the mainframes this machine is
    // descended from. A string compare is one instruction there and a loop
    // here, and the loop is the reason interpreted string code is slow, so the
    // machine does what the hardware it is imitating did.
    MCp   = 0x0240,   // restartable memmove cursors in rd/rs1/rs2, state rs3
    MSet  = 0x0241,   // restartable byte fill, progress in rs3
    MSwap = 0x0244,   // restartably exchange two equal-length blocks
    MRev  = 0x0245,   // restartably reverse a block by element width
    MSum  = 0x0246,   // restartable checksum, selector and seed in rs3
    MFfs  = 0x0247,   // first set bit in a memory bitmap
    MPop  = 0x0248,   // population count over a memory bitmap

    // Atomics and ordering are part of the mandatory memory unit.  The five
    // encodings in each block differ only in ordering strength.
    Cas       = 0x0300,
    CasAq     = 0x0301,
    CasRel    = 0x0302,
    CasAr     = 0x0303,
    CasSc     = 0x0304,

    AmoAdd    = 0x0308,
    AmoAddAq  = 0x0309,
    AmoAddRel = 0x030A,
    AmoAddAr  = 0x030B,
    AmoAddSc  = 0x030C,
    AmoAnd    = 0x0310,
    AmoAndAq  = 0x0311,
    AmoAndRel = 0x0312,
    AmoAndAr  = 0x0313,
    AmoAndSc  = 0x0314,
    AmoOr     = 0x0318,
    AmoOrAq   = 0x0319,
    AmoOrRel  = 0x031A,
    AmoOrAr   = 0x031B,
    AmoOrSc   = 0x031C,
    AmoXor    = 0x0320,
    AmoXorAq  = 0x0321,
    AmoXorRel = 0x0322,
    AmoXorAr  = 0x0323,
    AmoXorSc  = 0x0324,
    AmoSwap    = 0x0328,
    AmoSwapAq  = 0x0329,
    AmoSwapRel = 0x032A,
    AmoSwapAr  = 0x032B,
    AmoSwapSc  = 0x032C,
    AmoMin    = 0x0330,
    AmoMinAq  = 0x0331,
    AmoMinRel = 0x0332,
    AmoMinAr  = 0x0333,
    AmoMinSc  = 0x0334,
    AmoMax    = 0x0338,
    AmoMaxAq  = 0x0339,
    AmoMaxRel = 0x033A,
    AmoMaxAr  = 0x033B,
    AmoMaxSc  = 0x033C,

    Fence = 0x0340,
    Drain = 0x0341,
    LdA   = 0x0348,
    LduA  = 0x0349,
    StR   = 0x034A,
    WaitEq = 0x034B,
    LdIo  = 0x0350,
    StIo  = 0x0351,

    // AmoClaim operates on one naturally aligned 64-bit word regardless of
    // the size field.
    AmoClaim = 0x0358,
    CasPair  = 0x0359,

    // The object model. These are core memory instructions and not an optional
    // accelerator: every CORSAC CPU has them, so a compiler may emit them
    // without asking and there is no emulation path to keep working. Each one
    // is a load, a store, an allocation or a BOUNDED search of memory the
    // program did not have to walk itself — which is why they live here rather
    // than apart.
    //
    // This said "two loads and a mask", which was true of the type tests when
    // the only one was a bit test against an ancestor mask. isa still is; isi
    // searches the interface array and is logarithmic in how many interfaces
    // the type implements. The criterion that keeps an instruction in this
    // group was always BOUNDED rather than SINGLE — an unbounded one would need
    // restart machinery, and none of these do.
    //
    // What they buy is not only speed. A bounds check and a null check that are
    // part of the instruction cannot be left out by a code generator having a
    // bad day, and that is the difference between a language with objects and a
    // language with pointers.

    Alloc  = 0x0250,   // rd = rs1 zeroed bytes from the arena
    AllocI = 0x0251,   // rd = imm zeroed bytes -- the size is a constant for objects
    NewArr = 0x0252,   // rd = array of rs1 elements of imm bytes, length word ahead of the payload
    ALen   = 0x0253,   // rd = element count of the array or string in rs1

    Ldel   = 0x0260,   // rd = rs1[rs2], element width from the size field
    LdelU  = 0x0261,
    Stel   = 0x0262,   // rs1[rs2] = rd
    FLdel  = 0x0263,   // the same, in the floating-point bank
    FStel  = 0x0264,

    LdFld  = 0x0270,   // rd = imm(rs1), faulting when rs1 is null
    StFld  = 0x0271,
    FLdFld = 0x0272,
    FStFld = 0x0273,

    /// <summary>rd = the vtable slot at byte offset imm of the object in rs1.</summary>
    LdVt   = 0x0274,

    // 0x0280 was isinst and 0x0281 was castcl. They took a type BIT in the
    // immediate, so an image built against one program's type numbering meant
    // something different read against another's -- which is the whole reason
    // format v6 exists. Replaced by IsA/IsI and CastA/CastI below.
    //
    // THE NUMBERS ARE VACATED, NOT REUSED, the same rule ldk was retired under.
    // An opcode number that changes meaning turns every image already on a disc
    // into a program that runs and quietly does the wrong thing; a number that
    // decodes to nothing at all fails where the fault actually is.
    NullCk = 0x0282,   // fault when rs1 is null

    // ---- unit 0x0A, strings --------------------------------------------
    //
    // Element width comes from the size field, so one opcode serves 8, 16 and
    // 32-bit characters rather than there being three of each. These two need
    // no string registers: both are told their length in rs3, which is what
    // makes them the part of the unit that can exist on its own.
    //
    // SCmp replaces MCmp, retired at size .b -- the old one compared BYTES and
    // could not be told that two 16-bit characters are one element.

    /// <summary>
    /// rd = negative, zero or positive as the rs3 elements at rs1 order before,
    /// with, or after those at rs2. Ordinal, which is what CompareOrdinal and
    /// every sort in the compiler wants.
    /// </summary>
    SCmp   = 0x1400,

    /// <summary>
    /// rd = 1 when the rs3 elements at rs1 equal those at rs2, else 0.
    ///
    /// Separate from SCmp because equality can stop at the first difference and
    /// ordering cannot, and because a dictionary probe asks this question far
    /// more often than it asks the other one.
    /// </summary>
    SEq    = 0x1401,

    /// <summary>
    /// rd = FNV-1a over the rs2 elements at rs1, seeded from string register
    /// imm. CLOSES TASK 70.
    ///
    /// Hashing BY VALUE is the whole point. A dictionary keyed by string hashes
    /// its keys by address today, so two equal strings built separately land in
    /// different buckets: a lookup that should find an entry misses it and
    /// inserts a second one, and both are then reachable depending on which
    /// reference you ask with. That is a wrong answer, not a slow path.
    ///
    /// The seed comes from a register rather than being wired in so that a
    /// process can choose it -- one fixed basis in the machine is a machine
    /// that cannot have two hash tables with independent collision behaviour,
    /// and it is how hash-flooding becomes possible.
    /// </summary>
    SHash  = 0x1402,

    /// <summary>rd = elements copied. rs1 destination, rs2 source, rs3 count.
    /// Overlap is defined: copies as if through a temporary.</summary>
    SCpy   = 0x1403,

    /// <summary>rs1 destination, rs2 the element, rs3 count.</summary>
    SFill  = 0x1404,

    /// <summary>rd = index of the element in rs2 within the rs3 elements at
    /// rs1, or -1. Replaces MFind, retired at .b.</summary>
    SFind  = 0x1410,

    /// <summary>SFind from the far end: LastIndexOf.</summary>
    SFindR = 0x1411,

    /// <summary>
    /// rd = index of the run at rs2 within the rs3 elements at rs1, or -1. The
    /// needle's length is the immediate, which is what keeps this to one
    /// instruction rather than needing a fifth operand.
    /// </summary>
    SSearch = 0x1412,

    /// <summary>
    /// rd = how many leading elements of the rs2 at rs1 are IN the set whose
    /// bitmap string register imm points at.
    ///
    /// The lexer's inner loop. Scanning an identifier is one of these rather
    /// than a load, a compare and a branch per character.
    /// </summary>
    SSpan  = 0x1420,

    /// <summary>The complement of SSpan: the run that is NOT in the set. Scan
    /// to a delimiter, which is the other half of the lexer's loop.</summary>
    SBrk   = 0x1421,

    /// <summary>
    /// Translate the rs2 elements at rs1 through the table string register imm
    /// points at, in place. Case folding, character classification, encoding
    /// conversion -- all the same instruction with a different table.
    /// </summary>
    STrans = 0x1430,

    /// <summary>Case-fold the rs2 elements at rs1 in place; imm is 0 for lower
    /// and 1 for upper. Invariant, which is what the library wants.</summary>
    SCase  = 0x1431,

    /// <summary>
    /// Parse an integer from the rs2 elements at rs1. rd is the value and rs3
    /// receives HOW MANY ELEMENTS WERE CONSUMED, which is what makes TryParse
    /// and the lexer's number scanner the same instruction: a caller that
    /// wanted the whole string checks the count against the length.
    /// </summary>
    SNum   = 0x1440,

    /// <summary>Format the integer in rs2 into the buffer at rs1, at most rs3
    /// elements. rd = elements written.</summary>
    SFmt   = 0x1441,

    /// <summary>String register imm = rs1.</summary>
    MTS    = 0x1450,

    /// <summary>rd = string register imm.</summary>
    MFS    = 0x1451,

    // ---- type tests that name a type by ADDRESS ------------------------
    //
    // IsInst names a type by a BIT INDEX in a whole-program table, which is
    // why a program's type count leaks into the layout of every descriptor and
    // why anything linking a library inherits that library's ceiling. A type's
    // descriptor already has an address, and an address is a relocation like
    // any other -- so naming the type by its descriptor makes a type test a
    // fact about two objects rather than about the program they were compiled
    // in. There is nothing left to count, and so nothing left to cap.
    //
    // Classes use Cohen's display: constant time, no walk, no per-program
    // numbering. Interfaces use a sorted array of descriptor addresses and a
    // binary search, because a type may implement many and they do not nest.
    IsA    = 0x0283,   // rd = rs1 when its CLASS chain includes the descriptor in rs2
    IsI    = 0x0284,   // rd = rs1 when it implements the INTERFACE in rs2
    CastA  = 0x0285,   // IsA, but a mismatch faults rather than answering null
    CastI  = 0x0286,   // IsI, but a mismatch faults rather than answering null

    // ---- unit 0x02, control -----------------------------------------
    Beq   = 0x0400,
    Bne   = 0x0401,
    Blt   = 0x0402,
    Bge   = 0x0403,
    BltU  = 0x0404,
    BgeU  = 0x0405,

    Jmp   = 0x0410,
    Call  = 0x0411,
    JmpR  = 0x0412,
    CallR = 0x0413,

    Sys   = 0x0420,
    Reti  = 0x0421,

    /// <summary>
    /// A supervisor call: user code asking the kernel for something.
    ///
    /// Separate from <c>sys</c> on purpose. Sys reaches the HOST — the world
    /// outside this machine altogether — and is the kernel's to use. Overloading
    /// one instruction to mean "call the host" or "call the kernel" depending on
    /// who is asking would make what it does depend on where it is, which is the
    /// sort of thing nobody reads correctly at three in the morning.
    ///
    /// It is a trap, because the only way into kernel mode has to be a vector
    /// the kernel installed, and that is what a trap is.
    /// </summary>
    Svc   = 0x0424,

    /// <summary>
    /// The program cannot continue: an exception nothing caught, an assertion
    /// that did not hold. Raises a trap carrying whatever rs1 names, so a kernel
    /// can end the thread and keep the machine.
    ///
    /// Available in user mode, because it is a program failing rather than a
    /// program seizing anything. A thread must always be able to say it is
    /// finished; the alternative is one that cannot fail safely.
    /// </summary>
    Panic = 0x0425,
    Rmci  = 0x0426,
    /// <summary>Enter a checked cross-ring service gate by descriptor number.</summary>
    Gate  = 0x0427,
    /// <summary>Return through the processor-sealed record of the active gate.</summary>
    Gret  = 0x0428,
    /// <summary>Validate and load the ring-0 physical protection latches.</summary>
    PProtLd = 0x0429,
    /// <summary>Lock the physical protection latches until processor reset.</summary>
    PProtLk = 0x042A,
    Mtc   = 0x0422,   // mtc rs, ctl -- control[ctl] = rs (the register field is a SOURCE here)
    Mfc   = 0x0423,   // mfc rd, ctl -- rd = control[ctl]
    CpuId = 0x0430,

    // The local interrupt controller's redirect table: sixty-four lines, each
    // with a priority, a vector and two flags. It is two instructions rather
    // than control registers because there are sixty-four of them and control
    // registers are named by an immediate — a table indexed by a REGISTER is
    // the thing a kernel actually needs, so that programming a line can be a
    // loop over lines rather than sixty-four pieces of straight-line code.
    //
    // It lives in the control unit beside the vector table base and the
    // priority level, because on this machine a trap and an interrupt are one
    // mechanism sharing one table. Putting the redirect table in a unit of its
    // own would split the two halves of a single idea.
    LpicSet = 0x0440,   // rd = the line's old configuration, rs1 = line, rs2 = new configuration
    LpicGet = 0x0441,   // rd = the configuration of line rs1
    Djnz    = 0x0451,
    JTbl    = 0x0452,

    // ---- unit 0x03, floating point ----------------------------------
    FAdd   = 0x0600,
    FSub   = 0x0601,
    FMul   = 0x0602,
    FDiv   = 0x0603,
    FSqrt  = 0x0604,
    FNeg   = 0x0605,
    FAbs   = 0x0606,
    FMin   = 0x0607,
    FMax   = 0x0608,
    Fma    = 0x0609,   // uses the fourth register field; geometry is full of it

    FEq    = 0x0610,   // comparisons write 0/1 to an INTEGER register, so there
    FLt    = 0x0611,   // are no FP branches and no FP condition codes
    FLe    = 0x0612,
    FCSel  = 0x0613,

    CvtI2F = 0x0620,
    CvtF2I = 0x0621,
    CvtFF  = 0x0622,   // f32 <-> f64, direction from the size field
    MovX2F = 0x0630,   // bit reinterpretation, NOT conversion
    MovF2X = 0x0631,

    Floor  = 0x0640,
    Ceil   = 0x0641,
    Round  = 0x0642,
    Trunc  = 0x0643,

    // Revision 1.5 defines FPCR/FPSR but omits an access mechanism.  These
    // otherwise unassigned Unit 03 operations are the implementation erratum
    // recorded in CORSAC_REV_1_5_IMPLEMENTATION_NOTES.txt.
    MFFCR  = 0x0650,
    MTFCR  = 0x0651,
    MFFSR  = 0x0652,
    MTFSR  = 0x0653,

    // ---- unit 0x08, channel I/O -------------------------------------
    // A command block in guest memory holds MANY operations, so one bus
    // transaction carries fifty tile reads. Batching is the default rather
    // than something a careful author remembers to do, which matters
    // because the far end may be a game server across a network.
    // Rev. 1.6's 64-byte header omits the device/channel fields its prose
    // says it contains.  The provisional rule is recorded in the CPU notes.
    Sio    = 0x1000,   // rd = tag; rs1 = device selector, rs2 = block address
    Siow   = 0x1001,   // same, but suspend until it completes -- the idiom
    Tio    = 0x1002,   // rd = status of the channel named by rs1
    Hio    = 0x1003,   // abort the channel named by rs1
    Probe  = 0x1004,   // rd = identity of device rs1, or 0 when absent
    MfIo   = 0x1010,   // rd = io[imm], a channel status register
    MtIo   = 0x1011,   // io[imm] = rd

    // ---- unit 0x09, the interval timer ------------------------------
    //
    // It counts INSTRUCTIONS RETIRED on its own processor, not seconds. A
    // machine whose behaviour depends on the host's clock cannot be debugged:
    // the same program with the same input would take a different path on a
    // busy afternoon, and the bug you were chasing would be gone. Instructions
    // are what a time slice is actually made of anyway.
    //
    // The consequence is worth stating plainly: a processor that is parked
    // retires nothing, so its timer does not advance. A timer preempts work; it
    // does not wake an idle processor. Waking one is what an interprocessor
    // interrupt is for, and this machine already has that.
    //
    // It asserts on the processor's OWN interrupt controller and never goes
    // near the router, because it has nowhere else to deliver: the whole point
    // is that this processor interrupts itself.
    // Two instructions, because a third would only be tmrset with a zero in it:
    // arming for no ticks is what stopping IS, and returning what was left
    // means a scheduler can stop the clock and find out how much of a slice it
    // took back in one instruction rather than racing between two.
    TmrSet = 0x1200,   // rd = prior value, timer[rs2 selector] = rs1
    TmrGet = 0x1201,   // rd = timer[rs1 selector], without disturbing it

    // ---- unit 0x0D, debug, trace, and performance ------------------
    TrcRd = 0x1A00,
    TrcMk = 0x1A01,
    Step  = 0x1A02,
    Watch = 0x1A03,

    // ---- unit 0x04, context and state assist ----------------------
    CtxSave = 0x0800,
    CtxLoad = 0x0801,

    // ---- unit 0x05, vector -----------------------------------------
    VLd    = 0x0A00,
    VSt    = 0x0A01,
    VLdS   = 0x0A02,
    VStS   = 0x0A03,
    VGath  = 0x0A04,
    VScat  = 0x0A05,
    VSplat = 0x0A10,
    VSel   = 0x0A11,
    VShuf  = 0x0A12,
    VCmp   = 0x0A13,
    VRed   = 0x0A20,
    VLen   = 0x0A21,
    MTV    = 0x0A30,
    MFV    = 0x0A31,

    // ---- unit 0x06, memory management -----------------------------
    PtWalk     = 0x0C00,
    PtProbe    = 0x0C01,
    PtMap      = 0x0C02,
    PtUnmap    = 0x0C03,
    PtCopy     = 0x0C04,
    PtNext     = 0x0C05,
    TlbInv     = 0x0C06,
    ProbeRange = 0x0C07,
    MCpAs      = 0x0C08,

    // ---- unit 0x0B, decimal ---------------------------------------
    DAdd = 0x1600, DSub = 0x1601, DMul = 0x1602, DDiv = 0x1603,
    DRem = 0x1604, DNeg = 0x1605, DAbs = 0x1606, DCmp = 0x1607,
    DRound = 0x1610, DTrunc = 0x1611, DFloor = 0x1612,
    DCeil = 0x1613, DScale = 0x1614,
    DFromI = 0x1620, DToI = 0x1621, DFromF = 0x1622,
    DToF = 0x1623, DPack = 0x1624, DUnpk = 0x1625,
    DNum = 0x1630, DFmt = 0x1631,
    MTD = 0x1640, MFD = 0x1641, DLd = 0x1642, DSt = 0x1643,
    BAdd = 0x1650, BSub = 0x1651, BMul = 0x1652, BDiv = 0x1653,
    BCmp = 0x1654, BZap = 0x1655, BShift = 0x1656,
    BPack = 0x1657, BUnpk = 0x1658, BCvB = 0x1659, BCvD = 0x165A,
    BEdit = 0x1660, BEditM = 0x1661,

    // ---- unit 0x0C, cache -----------------------------------------
    ICInv = 0x1800, DCFlush = 0x1801, DCInv = 0x1802,
    DCFlushInv = 0x1803, DCFlushAll = 0x1804, DCZero = 0x1805,
    Prefetch = 0x1810, PrefetchW = 0x1811, NTCpy = 0x1812,

    // ---- unit 0x0E, cryptography ----------------------------------
    HMD5 = 0x1C00, HSHA1 = 0x1C01, HSHA2 = 0x1C02,
    HInit = 0x1C03, HFin = 0x1C04, HashBlock = 0x1C05,
    ChaCha20Block = 0x1C06,
    F25519Mul = 0x1C07, F25519Square = 0x1C08, F25519Reduce = 0x1C09,
    Poly1305Block = 0x1C0A, GHash = 0x1C0B,
    DESKey = 0x1C10, DESEnc = 0x1C11, DESDec = 0x1C12,
    GFMul = 0x1C20, CLMul = 0x1C21, SBox = 0x1C22,
    MAC = 0x1C23, ModRed = 0x1C24, CTSel = 0x1C25,
    Rand = 0x1C30,
    AesEncRound = 0x1C40, AesEncLast = 0x1C41,
    AesDecRound = 0x1C42, AesDecLast = 0x1C43,
    AesKey128Next = 0x1C44, AesKey256Next = 0x1C45,

    // ---- unit 0x0F, managed runtime -------------------------------
    StRef = 0x1E00, StelR = 0x1E01, Box = 0x1E02, Unbox = 0x1E03,
    CallI = 0x1E10, CallD = 0x1E11,
    MonEnter = 0x1E20, MonExit = 0x1E21,
    AddO = 0x1E30, SubO = 0x1E31, MulO = 0x1E32,
    Unwind = 0x1E40,

    // ---- unit 0x10, bit manipulation ------------------------------
    BMTr = 0x2000, BMMul = 0x2001,
    Pdep = 0x2010, Pext = 0x2011, Ilv = 0x2020, Dilv = 0x2021,

    // ---- unit 0x11, compression -----------------------------------
    BitGet = 0x2200, BitPut = 0x2201, BitPeek = 0x2202,
    BitFlush = 0x2203,
    LCP = 0x2210, LZFind = 0x2211, LZHash = 0x2212, LZCopy = 0x2213,
    HuffDec = 0x2220, HuffEnc = 0x2221, HuffLen = 0x2222,
    LZWLook = 0x2230, LZWAdd = 0x2231, LZWExp = 0x2232,
    AHUpd = 0x2240, AHDec = 0x2241, AHEnc = 0x2242,
    /// <summary>Move compression table base Zselector to rd.</summary>
    MFZ = 0x2250,
    /// <summary>Move rs1 to compression table base Zselector.</summary>
    MTZ = 0x2251,
}

/// <summary>How an instruction's fields are read.</summary>
public enum Fmt : byte
{
    None,
    /// <summary>rd, rs1, rs2</summary>
    R,
    /// <summary>rd, rs1, rs2, rs3</summary>
    R4,
    /// <summary>rd, rs1 (unary)</summary>
    R1,
    /// <summary>rd, rs1, imm</summary>
    I,
    /// <summary>rd, off(rs1)</summary>
    M,
    /// <summary>rs1, rs2, displacement</summary>
    B,
    /// <summary>displacement only</summary>
    J,
    /// <summary>rd, wide immediate</summary>
    U,
    /// <summary>register-set bitmap in the immediate</summary>
    Mask,

    /// <summary>
    /// rd, rs1, rs2, imm -- three registers and a small literal.
    ///
    /// The string unit needs it and nothing did before: an operation over a
    /// run takes a pointer and a length, which is two registers, and then
    /// names a STRING REGISTER holding the set, table or seed it works
    /// against. That name is three bits and belongs in the immediate; putting
    /// it in rs3 would make a register field mean a small integer, which is
    /// how a disassembler starts lying about what an instruction reads.
    /// </summary>
    RS,

    /// <summary>
    /// rd, rs1, rs2, rs3, imm -- the only five-operand form, and it exists
    /// for one instruction. Searching for a run inside a run takes TWO
    /// pointer-and-length pairs, which is four values, and no rearrangement
    /// of them is fewer. Every field is already in the encoding and none of
    /// them overlap, so this costs nothing but a parser case.
    /// </summary>
    R4I,
    /// <summary>
    /// rd, rs1, rs2, rs3, string-register selector. The selector occupies the
    /// low three bits of the immediate while every general-register field
    /// remains available to the restartable span cursor convention.
    /// </summary>
    R4S,
    /// <summary>immediate only (sys, cpuid leaf)</summary>
    Imm,
    /// <summary>single register operand (jmpr, callr)</summary>
    Reg,
    /// <summary>single destination register operand</summary>
    OutReg,
    /// <summary>destination register, unit-register selector immediate</summary>
    OutImm,
    /// <summary>unit-register selector immediate, source register</summary>
    ImmReg,
}

public static class Isa
{
    /// <summary>
    /// Programmer-visible architecture revision, packed major.minor.  This is
    /// an ISA fact shared by every conforming engine, not a managed-Cpu model
    /// constant.
    /// </summary>
    // GA42-1000-0 Revision 1.7-SA is the complete programmer-visible
    // architecture. Earlier editions and overlays are superseded.
    public const int ArchitectureVersion = 0x0107;

    public const int RegCount   = 32;    // shipped; fields address 128
    public const int RegMax     = 128;

    // Rev. 1.6-SA defines f0-f15. The four-bit floating-register namespace is
    // architectural even though the integer file implements r0-r31.
    public const int FpRegCount = 16;

    // Unit 0x0B supplies eight 128-bit decimal registers D0-D7.
    public const int DecimalRegCount = 8;

    // Eight, named by an explicit operand rather than chosen implicitly.
    // Implicit registers are exactly why compilers avoided the 8086's REP
    // MOVSB. With eight and explicit naming a lexer holds every set it needs
    // live at once -- identifier-start, identifier-continue, whitespace,
    // digits, operators, a fold table, a hash seed -- and the token loop
    // reloads nothing. They are USER MODE and per-process: a program sets its
    // own character sets, so they are not control registers and the kernel
    // does not own them. The trap path saves them, 64 bytes.
    public const int StrRegCount = 8;

    // Unit 0x11 supplies eight explicit table/window base registers Z0-Z7.
    public const int CompressionRegCount = 8;

    /// <summary>
    /// What TlbInv's mode operand may say.
    ///
    /// An instruction's operand encoding, and therefore the assembler's
    /// business: `tlbinv ... all_nonglobal` has to become a number, and the
    /// number is architectural. These lived on the managed processor, which
    /// made assembling one instruction depend on the INTERPRETER -- so the
    /// assembler could not be compiled for the machine without carrying an
    /// emulator along with it.
    /// </summary>
    public const int TlbInvPageAsid = 0;
    public const int TlbInvAsid = 1;
    public const int TlbInvRangeAsid = 2;
    public const int TlbInvAllNonGlobal = 3;
    public const int TlbInvAll = 4;

    public const int RegZero = 0;
    public const int RegTmp  = 13;
    public const int RegSp   = 14;
    public const int RegLr   = 15;

    public const int ImmBits  = 16;
    public const int ImmMin   = -(1 << (ImmBits - 1));
    public const int ImmMax   = (1 << (ImmBits - 1)) - 1;

    /// <summary>I-form immediate widened over rs2 and rs3.</summary>
    public const int WideBits = 30;
    public const long WideMin = -(1L << (WideBits - 1));
    public const long WideMax = (1L << (WideBits - 1)) - 1;

    /// <summary>Branch displacement, widened over rs3.</summary>
    public const int BrBits = 23;
    public const int BrMin  = -(1 << (BrBits - 1));
    public const int BrMax  = (1 << (BrBits - 1)) - 1;

    /// <summary>Jump displacement, widened over every register field.</summary>
    public const int JmpBits = 44;
    public const long JmpMin = -(1L << (JmpBits - 1));
    public const long JmpMax = (1L << (JmpBits - 1)) - 1;

    /// <summary>
    /// Where a program's data begins.
    ///
    /// Not zero, and that is the whole point: address zero is null, and nothing
    /// may be jumpered there, so dereferencing null finds no board and faults.
    /// A vtable at address zero would make every object look null, which cost
    /// three rounds of debugging before this constant existed.
    /// </summary>
    public const long DataBase = 1L << 16;

    /// <summary>
    /// How many distinct classes and interfaces one image may contain.
    ///
    /// The run-time type test gives every type a bit in an ancestor mask sitting
    /// in front of the vtable, and the mask is as many words wide as the image
    /// needs -- so this ceiling exists to bound that mask (4096 bits is 512
    /// bytes per type) and to give a wild bit index something to be caught by,
    /// not because anything about the machine stops at 4096.
    /// </summary>
    // ---- the type descriptor -------------------------------------------
    //
    // FIXED SIZE, and that is the entire point. The old descriptor was
    // 24 + 8*MaskWords bytes, where MaskWords came from how many types the
    // whole program declared -- so a program that crossed a multiple of 64
    // types moved every field in every descriptor, and a library compiled
    // against one count read the wrong words in a program built with another.
    // A whole-program property had become a separate-compilation contract.
    //
    // Everything variable-length now lives OUT OF LINE behind an address, so
    // the layout is the same in every image ever built and a library never has
    // to be told how big its consumers are.
    //
    // The descriptor sits immediately before the vtable, so any object reaches
    // it directly from the first word of the Revision 1.5 object header.
    public const int DescriptorBytes = 64;
    public const int ObjectHeaderBytes = 16; // descriptor, synchronization word
    public const int ArrayHeaderBytes  = 24; // object header, then element count

    public const int DescName       = 0;    // address of the name string
    public const int DescSize       = 8;    // bytes per instance
    public const int DescDepth      = 16;   // how deep in the class chain; -1 for an interface
    public const int DescDisplay    = 24;   // address of the ancestor display
    public const int DescInterfaces = 32;   // address of the sorted interface array
    public const int DescSelf       = 40;   // this descriptor's own address
    public const int DescFlags      = 48;   // managed layout/classification flags
    public const int DescPayload    = 56;   // first payload byte for sequences
    public const long DescArray     = 1L << 0;
    public const long DescString    = 1L << 1;


    /// <summary>
    /// Where an image's code sits: immediately above its data, aligned.
    ///
    /// Code and data share ONE address space &mdash; the split between them is
    /// at the cache, not in the addressing. Deriving the code base from the
    /// data size rather than fixing it means the file format needs no new
    /// field and small machines are not forced to reserve a code region larger
    /// than their whole memory.
    /// </summary>
    public static long CodeBaseFor(int dataBytes) => (DataBase + dataBytes + 7) & ~7L;

    public const int UnitShift = 9;
    public const int UnitCount = 256;
    public const int OpsPerUnit = 512;

    public static Unit UnitOf(Op op) => (Unit)((int)op >> UnitShift);

    // ---- format table ---------------------------------------------------

    private static readonly Dictionary<Op, Fmt> Formats = BuildFormats();

    private static Dictionary<Op, Fmt> BuildFormats()
    {
        Dictionary<Op, Fmt> f = new();

        foreach (Op op in new[] { Op.Halt, Op.Nop, Op.Wait, Op.Brk, Op.Reti, Op.Rmci, Op.Gret,
                                  Op.PProtLk, Op.Drain, Op.Pause, Op.Step })
        {
            f[op] = Fmt.None;
        }

        foreach (Op op in new[]
        {
            Op.Add, Op.Sub, Op.Mul, Op.MulH, Op.MulHU, Op.Div, Op.DivU, Op.Mod, Op.ModU,
            Op.And, Op.Or, Op.Xor, Op.Shl, Op.Shr, Op.Sar,
            Op.Seq, Op.Sne, Op.Slt, Op.SltU,
            Op.Min, Op.Max, Op.MinU, Op.MaxU,
            Op.FAdd, Op.FSub, Op.FMul, Op.FDiv, Op.FMin, Op.FMax,
            Op.FEq, Op.FLt, Op.FLe,
            Op.AmoAdd, Op.AmoAddAq, Op.AmoAddRel, Op.AmoAddAr, Op.AmoAddSc,
            Op.AmoAnd, Op.AmoAndAq, Op.AmoAndRel, Op.AmoAndAr, Op.AmoAndSc,
            Op.AmoOr, Op.AmoOrAq, Op.AmoOrRel, Op.AmoOrAr, Op.AmoOrSc,
            Op.AmoXor, Op.AmoXorAq, Op.AmoXorRel, Op.AmoXorAr, Op.AmoXorSc,
            Op.AmoSwap, Op.AmoSwapAq, Op.AmoSwapRel, Op.AmoSwapAr, Op.AmoSwapSc,
            Op.AmoMin, Op.AmoMinAq, Op.AmoMinRel, Op.AmoMinAr, Op.AmoMinSc,
            Op.AmoMax, Op.AmoMaxAq, Op.AmoMaxRel, Op.AmoMaxAr, Op.AmoMaxSc,
            Op.MCp, Op.MSet,
            Op.Ldel, Op.LdelU, Op.Stel, Op.FLdel, Op.FStel,
            Op.TrcMk,
            Op.Rol, Op.Ror, Op.Bset, Op.Bclr, Op.Btst,
            Op.IsA, Op.IsI, Op.CastA, Op.CastI,
        })
        {
            f[op] = Fmt.R;
        }

        foreach (Op op in new[] { Op.CSel, Op.Fma, Op.FCSel, Op.JTbl,
                                  Op.Cas, Op.CasAq, Op.CasRel, Op.CasAr, Op.CasSc,
                                  Op.MSwap, Op.MRev, Op.MSum, Op.MFfs, Op.MPop, Op.AmoClaim,
                                  Op.WaitEq, Op.CasPair,
                                  Op.PProtLd, Op.TrcRd,
                                  Op.SCmp, Op.SEq, Op.SCpy, Op.SFill,
            Op.SFind, Op.SFindR, Op.SSearch, Op.SNum, Op.SFmt })
        {
            f[op] = Fmt.R4;
        }

        foreach (Op op in new[]
        {
            Op.Zxt, Op.FSqrt, Op.FNeg, Op.FAbs,
            Op.CvtI2F, Op.CvtF2I, Op.CvtFF, Op.MovX2F, Op.MovF2X,
            Op.Floor, Op.Ceil, Op.Round, Op.Trunc,
            Op.Alloc, Op.ALen,
            Op.Popc, Op.Clz, Op.Ctz, Op.Bswap, Op.Brev, Op.Par,
            Op.RandTry,
        })
        {
            f[op] = Fmt.R1;
        }

        foreach (Op op in new[]
        {
            Op.AddI, Op.MulI, Op.AndI, Op.OrI, Op.XorI,
            Op.ShlI, Op.ShrI, Op.SarI, Op.SltI, Op.SltIU,
            Op.NewArr,
            Op.Msk, Op.Bfe, Op.Bfi,
        })
        {
            f[op] = Fmt.I;
        }

        foreach (Op op in new[]
        {
            Op.Ld, Op.Ldu, Op.St, Op.Fld, Op.Fst,
            Op.LdA, Op.LduA, Op.StR, Op.LdIo, Op.StIo,
            Op.LdFld, Op.StFld, Op.FLdFld, Op.FStFld, Op.LdVt,

            // Library-relative, and sized like every other load and store.
            // Safe where the pc-relative pair was not: nothing synthesises
            // these and no image predates them.
            Op.LdL, Op.StL,
        })
        {
            f[op] = Fmt.M;
        }

        foreach (Op op in new[] { Op.Beq, Op.Bne, Op.Blt, Op.Bge, Op.BltU, Op.BgeU,
                                  Op.Djnz })
        {
            f[op] = Fmt.B;
        }

        f[Op.Jmp]   = Fmt.J;
        f[Op.Call]  = Fmt.J;
        f[Op.JmpR]  = Fmt.Reg;
        f[Op.CallR] = Fmt.Reg;
        f[Op.MFFCR] = Fmt.OutReg;
        f[Op.MTFCR] = Fmt.Reg;
        f[Op.MFFSR] = Fmt.OutReg;
        f[Op.MTFSR] = Fmt.Reg;
        f[Op.Sio]   = Fmt.R;    // rd = tag, rs1 = selector, rs2 = block
        f[Op.Siow]  = Fmt.R;
        f[Op.Tio]   = Fmt.R1;
        f[Op.Hio]   = Fmt.R1;
        f[Op.Probe] = Fmt.R1;
        f[Op.MfIo]  = Fmt.I;    // rd = channel[rs1].register[imm]
        f[Op.MtIo]  = Fmt.I;    // channel[rs1].register[imm] = rd

        f[Op.TmrSet] = Fmt.R;
        f[Op.TmrGet] = Fmt.R;

        f[Op.LpicSet] = Fmt.R;  // rd = the old configuration, rs1 = line, rs2 = the new one
        f[Op.LpicGet] = Fmt.R1; // rd = the configuration of line rs1

        f[Op.NullCk] = Fmt.Reg;
        f[Op.AllocI] = Fmt.I;

        f[Op.Sys]   = Fmt.Imm;
        f[Op.Fence] = Fmt.Imm;
        f[Op.Svc]   = Fmt.Imm;
        f[Op.Gate]  = Fmt.Imm;
        f[Op.Panic] = Fmt.Reg;
        f[Op.Mtc]   = Fmt.U;    // rd names the control register, rs1 the source
        f[Op.Mfc]   = Fmt.U;
        f[Op.Lui]   = Fmt.U;
        f[Op.Adr]   = Fmt.U;
        f[Op.Adl]   = Fmt.U;

        f[Op.CpuId] = Fmt.U;
        f[Op.HashBlock] = Fmt.R1;
        f[Op.ChaCha20Block] = Fmt.R1;
        f[Op.F25519Mul] = Fmt.R;
        f[Op.F25519Square] = Fmt.R1;
        f[Op.F25519Reduce] = Fmt.R1;
        f[Op.Poly1305Block] = Fmt.R4;
        f[Op.GHash] = Fmt.R;
        f[Op.AesEncRound] = Fmt.R;
        f[Op.AesEncLast] = Fmt.R;
        f[Op.AesDecRound] = Fmt.R;
        f[Op.AesDecLast] = Fmt.R;
        f[Op.AesKey128Next] = Fmt.R;
        f[Op.AesKey256Next] = Fmt.R4;
        f[Op.PushM]  = Fmt.Mask;
        f[Op.PopM]   = Fmt.Mask;
        f[Op.PushM2] = Fmt.Mask;
        f[Op.PopM2]  = Fmt.Mask;

        foreach (Op op in new[]
        {
            Op.MTS, Op.MFS,
        })
        {
            f[op] = Fmt.RS;
        }

        foreach (Op op in new[] { Op.SHash, Op.SSpan, Op.SBrk, Op.STrans })
        {
            f[op] = Fmt.R4S;
        }

        f[Op.SCase] = Fmt.R4I;

        // GA42-1000-0 Revision 1.1-SA Appendix A is authoritative.  These
        // assignments also override stale prototype forms above while their
        // execution paths are migrated.
        SetFormats(f, Fmt.None,
            Op.DCFlushAll, Op.Step);
        SetFormats(f, Fmt.R,
            Op.Alloc, Op.LpicGet,
            Op.VLd, Op.VSt, Op.TlbInv,
            Op.DAdd, Op.DSub, Op.DMul, Op.DDiv, Op.DRem, Op.DCmp,
            Op.MTD, Op.MFD,
            Op.ICInv, Op.DCFlush, Op.DCInv, Op.DCFlushInv, Op.DCZero,
            Op.TrcMk,
            Op.HMD5, Op.HSHA1, Op.HSHA2, Op.DESKey, Op.DESEnc, Op.DESDec,
            Op.GFMul, Op.CLMul,
            Op.AddO, Op.SubO, Op.MulO,
            Op.Pdep, Op.Pext, Op.Ilv, Op.Dilv,
            Op.TmrGet);
        SetFormats(f, Fmt.R4,
            Op.Ldx, Op.LdxU, Op.Stx,
            Op.MCp, Op.MSet, Op.NewArr,
            Op.Ldel, Op.LdelU, Op.Stel, Op.FLdel, Op.FStel,
            Op.SCmp, Op.SEq, Op.SHash, Op.SCpy, Op.SFill,
            Op.SFind, Op.SFindR, Op.SSearch, Op.SSpan, Op.SBrk,
            Op.STrans, Op.SCase, Op.SNum, Op.SFmt,
            Op.CtxSave, Op.CtxLoad,
            Op.VLdS, Op.VStS, Op.VGath, Op.VScat,
            Op.VSel, Op.VShuf, Op.VCmp, Op.VRed, Op.MTV, Op.MFV,
            Op.PtMap, Op.PtCopy, Op.ProbeRange, Op.MCpAs,
            Op.DPack, Op.DUnpk, Op.DNum, Op.DFmt,
            Op.BAdd, Op.BSub, Op.BMul, Op.BDiv, Op.BCmp, Op.BZap,
            Op.BShift, Op.BPack, Op.BUnpk, Op.BCvB, Op.BCvD,
            Op.BEdit, Op.BEditM,
            Op.NTCpy, Op.TrcRd, Op.HFin, Op.SBox, Op.MAC,
            Op.ModRed, Op.CTSel,
            Op.StRef, Op.StelR, Op.Box, Op.Unbox,
            Op.MonEnter, Op.MonExit,
            Op.CallI, Op.CallD, Op.Unwind,
            Op.BMTr, Op.BMMul,
            Op.LCP, Op.LZFind, Op.LZHash, Op.LZCopy,
            Op.HuffDec, Op.HuffEnc, Op.HuffLen,
            Op.LZWLook, Op.LZWAdd, Op.LZWExp,
            Op.AHUpd, Op.AHDec, Op.AHEnc);
        SetFormats(f, Fmt.OutImm, Op.MFS, Op.MFZ);
        SetFormats(f, Fmt.ImmReg, Op.MTS, Op.MTZ);
        // Watch consumes the status destination, three register operands,
        // and a literal arm/disarm option.
        f[Op.Watch] = Fmt.R4I;
        SetFormats(f, Fmt.R4I,
            Op.BitGet, Op.BitPut, Op.BitPeek, Op.BitFlush);
        SetFormats(f, Fmt.R1,
            Op.NullCk, Op.VSplat, Op.VLen,
            Op.PtWalk, Op.PtProbe, Op.PtUnmap, Op.PtNext,
            Op.DNeg, Op.DAbs, Op.DTrunc, Op.DFloor, Op.DCeil,
            Op.DFromI, Op.DToI, Op.DFromF, Op.DToF,
            Op.Rand,
            Op.Hio);
        SetFormats(f, Fmt.I,
            Op.AllocI, Op.DRound, Op.DScale, Op.HInit, Op.MfIo, Op.MtIo);
        SetFormats(f, Fmt.M,
            Op.LdPc, Op.StPc,
            Op.DLd, Op.DSt, Op.Prefetch, Op.PrefetchW);
        SetFormats(f, Fmt.Imm,
            Op.Brk, Op.Sys, Op.Fence, Op.Svc, Op.Panic, Op.Gate);

        return f;
    }

    private static void SetFormats(Dictionary<Op, Fmt> formats, Fmt format,
                                   params Op[] ops)
    {
        foreach (Op op in ops)
        {
            formats[op] = format;
        }
    }

    /// <summary>
    /// Flat tables indexed by opcode, built once.
    ///
    /// These replaced dictionary lookups that ran FIVE TIMES PER INSTRUCTION on
    /// the interpreter's hot path — decode is meant to be a shift and a mask,
    /// and hashing an enum five times to find out whether an opcode exists is
    /// not that. The space is 128 KB per table, paid once for the process.
    /// </summary>
    internal const int OpSpace = 1 << 17;

    /// <summary>
    /// Everything the interpreter needs to know about an opcode BEFORE it
    /// executes it, in one byte. The decode path asked five separate questions
    /// per instruction — two of them large switch expressions — and answering
    /// them all with a single indexed read is most of the difference between
    /// this interpreter being slow and being fast.
    /// </summary>
    [Flags]
    public enum OpFlags : byte
    {
        None      = 0,
        Defined   = 1 << 0,
        UsesSize  = 1 << 1,
        WritesFp  = 1 << 2,
        ReadsFp   = 1 << 3,
    }

    private static readonly byte[] FmtTable = new byte[OpSpace];
    private static readonly bool[] DefinedTable = new bool[OpSpace];
    private static readonly byte[] RegFieldTable = new byte[OpSpace];
    private static readonly bool[] UsesSizeTable = new bool[OpSpace];
    private static readonly byte[] FlagTable = new byte[OpSpace];
    private static readonly byte[] MaximumRingTable = new byte[OpSpace];

    public static OpFlags FlagsOf(Op op) => (OpFlags)FlagTable[(int)op & (OpSpace - 1)];

    /// <summary>
    /// Filled in a static constructor rather than by field initialisers,
    /// because these read Formats and RegFields and field initialisers run in
    /// declaration order — building a table from an array declared further down
    /// the file reads a null.
    /// </summary>
    /// <summary>
    /// Opcodes whose format would normally carry a width but which have only
    /// one. A block move moves bytes, an allocation is measured in bytes, and a
    /// vtable slot is always a word: for these the size field is reserved, and
    /// saying so here means a wrong one is a fault instead of being ignored.
    /// </summary>
    // This is architectural classification, not mutable data.  Keeping it a
    // pure predicate also makes it available while the ISA static tables are
    // being built; a HashSet here made the self-hosted path depend on a
    // generic collection being initialised during the same startup sequence.
    private static bool IsWidthless(Op op) => op is
        Op.MSum or Op.MFfs or Op.MPop or Op.AmoClaim
        or Op.Alloc or Op.AllocI or Op.NewArr or Op.ALen
        or Op.LdVt
        or Op.IsA or Op.IsI or Op.CastA or Op.CastI
        or Op.TmrSet or Op.TmrGet
        or Op.LpicSet or Op.LpicGet
        or Op.PProtLd
        or Op.TrcRd or Op.TrcMk or Op.Watch
        or Op.CtxSave or Op.CtxLoad
        or Op.PtWalk or Op.PtProbe or Op.PtMap or Op.PtUnmap or Op.PtCopy
        or Op.PtNext or Op.TlbInv or Op.ProbeRange or Op.MCpAs
        or Op.RandTry
        or Op.DAdd or Op.DSub or Op.DMul or Op.DDiv or Op.DRem or Op.DNeg or Op.DAbs
        or Op.DCmp or Op.DRound or Op.DTrunc or Op.DFloor or Op.DCeil or Op.DScale
        or Op.DFromI or Op.DToI or Op.DFromF or Op.DToF or Op.DPack or Op.DUnpk
        or Op.DNum or Op.DFmt or Op.MTD or Op.MFD or Op.DLd or Op.DSt
        or Op.BAdd or Op.BSub or Op.BMul or Op.BDiv or Op.BCmp or Op.BZap
        or Op.BShift or Op.BPack or Op.BUnpk or Op.BCvB or Op.BCvD or Op.BEdit or Op.BEditM
        or Op.ICInv or Op.DCFlush or Op.DCInv or Op.DCFlushInv or Op.DCZero
        or Op.Prefetch or Op.PrefetchW
        or Op.HMD5 or Op.HSHA1 or Op.HInit or Op.HFin
        or Op.DESKey or Op.DESEnc or Op.DESDec or Op.GFMul or Op.CLMul
        or Op.SBox or Op.MAC or Op.ModRed or Op.CTSel or Op.Rand
        or Op.StRef or Op.StelR or Op.Box or Op.Unbox or Op.CallI or Op.CallD
        or Op.MonEnter or Op.MonExit or Op.Unwind
        or Op.BitGet or Op.BitPut or Op.BitPeek or Op.BitFlush
        or Op.LCP or Op.LZFind or Op.LZHash or Op.LZCopy
        or Op.HuffDec or Op.HuffEnc or Op.HuffLen
        or Op.LZWLook or Op.LZWAdd or Op.LZWExp or Op.AHUpd or Op.AHDec or Op.AHEnc
        or Op.MTS or Op.MFS or Op.MFZ or Op.MTZ
        or Op.MFFCR or Op.MTFCR or Op.MFFSR or Op.MTFSR;

    static Isa()
    {
        // CPU-0002 defines ring 7 as the ordinary ceiling.  The small set of
        // supervisor operations below override it.  Keeping this beside the
        // other flat decode tables makes authority part of the ISA rather than
        // a private policy buried in one interpreter, and costs one indexed
        // byte load on the hot path.
        Array.Fill(MaximumRingTable, (byte)ProtectionRing.Ring7);
        SetMaximumRing(ProtectionRing.Ring0,
            Op.Halt, Op.Sys, Op.Rmci, Op.PProtLd, Op.PProtLk);
        SetMaximumRing(ProtectionRing.Ring1,
            Op.Reti, Op.LpicSet, Op.TmrSet,
            Op.CtxSave, Op.CtxLoad,
            Op.PtMap, Op.PtUnmap, Op.PtCopy, Op.TlbInv, Op.MCpAs,
            Op.DCFlushAll);
        SetMaximumRing(ProtectionRing.Ring2,
            Op.LdIo, Op.StIo, Op.LpicGet,
            Op.PtWalk, Op.PtProbe, Op.PtNext, Op.ProbeRange,
            Op.ICInv, Op.DCFlush, Op.DCInv, Op.DCFlushInv, Op.DCZero);
        SetMaximumRing(ProtectionRing.Ring3,
            Op.TrcRd, Op.Step, Op.Watch);

        foreach ((Op op, Fmt f) in Formats)
        {
            int i = (int)op & (OpSpace - 1);
            FmtTable[i] = (byte)f;
            DefinedTable[i] = true;
            RegFieldTable[i] = RegFields[(byte)f];

            bool usesSize = (f is Fmt.R or Fmt.R4 or Fmt.R1 or Fmt.I
                or Fmt.M or Fmt.B or Fmt.RS or Fmt.R4I or Fmt.R4S)
                && !IsWidthless(op);
            UsesSizeTable[i] = usesSize;

            OpFlags flags = OpFlags.Defined;

            if (usesSize)
            {
                flags |= OpFlags.UsesSize;
            }
            if (WritesFpBank(op))
            {
                flags |= OpFlags.WritesFp;
            }
            if (ReadsFpBank(op))
            {
                flags |= OpFlags.ReadsFp;
            }
            FlagTable[i] = (byte)flags;
        }

        // Both timer instructions retain the Appendix-A R encoding, but the
        // register selector occupies the immediate field. TmrSet consumes rd
        // and rs1; TmrGet consumes only rd. The other encoded register fields
        // are reserved and checked by the execution path.
        RegFieldTable[(int)Op.TmrSet] = 0b0111;
        RegFieldTable[(int)Op.TmrGet] = 0b0011;
        RegFieldTable[(int)Op.LpicGet] = 0b0011;
        RegFieldTable[(int)Op.Djnz] = 0b0010;
    }

    private static void SetMaximumRing(ProtectionRing ring, params Op[] ops)
    {
        foreach (Op op in ops)
        {
            MaximumRingTable[(int)op & (OpSpace - 1)] = (byte)ring;
        }
    }

    /// <summary>
    /// Outermost ring permitted to execute an opcode by CPU-0002 and later
    /// revisions.  Register, selector, capability, gate, and Machine Check
    /// checks are additional authority checks performed after this ceiling.
    /// </summary>
    public static ProtectionRing MaximumRingOf(Op op)
        => (ProtectionRing)MaximumRingTable[(int)op & (OpSpace - 1)];

    public static Fmt FormatOf(Op op) => (Fmt)FmtTable[(int)op & (OpSpace - 1)];

    /// <summary>
    /// Which of rd, rs1, rs2 and rs3 actually name registers, as bits 0..3.
    /// Wide immediates and displacements are encoded *over* the unused fields,
    /// so validating a field a format does not use would reject a perfectly
    /// good jump whose displacement happens to have high bits set.
    /// </summary>
    private static readonly byte[] RegFields = BuildRegFields();

    private static byte[] BuildRegFields()
    {
        byte[] f = new byte[Enum.GetValues<Fmt>().Length];
        f[(byte)Fmt.None] = 0b0000;
        f[(byte)Fmt.R]    = 0b0111;
        f[(byte)Fmt.R4]   = 0b1111;
        f[(byte)Fmt.R1]   = 0b0011;
        f[(byte)Fmt.I]    = 0b0011;
        f[(byte)Fmt.RS]   = 0b0111;
        f[(byte)Fmt.R4I]  = 0b1111;
        f[(byte)Fmt.R4S]  = 0b1111;
        f[(byte)Fmt.M]    = 0b0011;
        f[(byte)Fmt.B]    = 0b0110;
        f[(byte)Fmt.J]    = 0b0000;
        f[(byte)Fmt.U]    = 0b0001;
        f[(byte)Fmt.Mask] = 0b0000;
        f[(byte)Fmt.Imm]  = 0b0000;
        f[(byte)Fmt.Reg]  = 0b0010;
        f[(byte)Fmt.OutReg] = 0b0001;
        f[(byte)Fmt.OutImm] = 0b0001;
        f[(byte)Fmt.ImmReg] = 0b0010;
        return f;
    }

    public static int RegFieldMask(Op op) => RegFieldTable[(int)op & (OpSpace - 1)];

    public static bool IsDefined(Op op) => DefinedTable[(int)op & (OpSpace - 1)];

    /// <summary>True when the size field carries meaning; elsewhere a nonzero size traps.</summary>
    public static bool UsesSize(Op op) => UsesSizeTable[(int)op & (OpSpace - 1)];

    /// <summary>True when rd names a floating-point register.</summary>
    public static bool WritesFpBank(Op op) => op switch
    {
        Op.FAdd or Op.FSub or Op.FMul or Op.FDiv or Op.FSqrt or Op.FNeg or Op.FAbs
            or Op.FMin or Op.FMax or Op.Fma or Op.FCSel
            or Op.CvtI2F or Op.CvtFF or Op.MovX2F
            or Op.Floor or Op.Ceil or Op.Round or Op.Trunc or Op.Fld
            or Op.FLdel or Op.FLdFld => true,
        _ => false,
    };

    /// <summary>True when the source registers name floating-point registers.</summary>
    public static bool ReadsFpBank(Op op) => op switch
    {
        Op.FAdd or Op.FSub or Op.FMul or Op.FDiv or Op.FSqrt or Op.FNeg or Op.FAbs
            or Op.FMin or Op.FMax or Op.Fma or Op.FCSel
            or Op.FEq or Op.FLt or Op.FLe
            or Op.CvtF2I or Op.CvtFF or Op.MovF2X
            or Op.Floor or Op.Ceil or Op.Round or Op.Trunc or Op.Fst
            or Op.FStel or Op.FStFld => true,
        _ => false,
    };

    /// <summary>
    /// True when rd names a floating-point register while the source fields
    /// name integer ones. The element accessors are the only shape like this:
    /// the value is a float, and the array and the index plainly are not. The
    /// load and store forms already work this way by construction, since their
    /// base register comes out of an address field rather than a source field.
    /// </summary>
    public static bool FpValueIntAddress(Op op) => op is Op.FLdel or Op.FStel;

    // ---- mnemonics ------------------------------------------------------

    private static readonly Dictionary<string, Op> ByMnemonic = BuildMnemonics();

    private static Dictionary<string, Op> BuildMnemonics()
    {
        Dictionary<string, Op> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (Op op in Formats.Keys)
        {
            map[Mnemonic(op)] = op;
        }
        return map;
    }

    /// <summary>Assembler spelling. Dots in a few names keep convert and reinterpret distinct.</summary>
    public static string Mnemonic(Op op) => op switch
    {
        Op.CvtI2F => "cvt.i2f",
        Op.CvtF2I => "cvt.f2i",
        Op.CvtFF  => "cvt.ff",
        Op.MovX2F => "mov.x2f",
        Op.MovF2X => "mov.f2x",

        // SEQ COLLIDES, and silently. The core has had `seq` -- set if equal --
        // since the first day, and the string unit's SEQ is String EQuality;
        // both lower-case to the same word, and the later registration simply
        // replaced the earlier one in the table. Every `seq rd, rs1, rs2` in the
        // suite then failed to assemble, which was the lucky outcome: had the
        // operand counts matched, existing code would have quietly started
        // comparing memory instead of registers.
        //
        // The STRING one moves, because the core's spelling predates it and is
        // in every program already written. `seqs` keeps the plan's name and
        // says which unit it belongs to.
        Op.SEq    => "seqs",
        _         => op.ToString().ToLowerInvariant(),
    };

    public static bool TryParseOp(string text, out Op op) => ByMnemonic.TryGetValue(text, out op);

    // ---- encoding -------------------------------------------------------

    private static ulong Head(Op op) => (ulong)(uint)op << 47;

    public static ulong Encode(Op op, int rd = 0, int rs1 = 0, int rs2 = 0, int rs3 = 0,
                               long imm = 0, Size size = Size.D)
        => Head(op)
         | ((ulong)(uint)(rd  & 0x7F) << 40)
         | ((ulong)(uint)(rs1 & 0x7F) << 33)
         | ((ulong)(uint)(rs2 & 0x7F) << 26)
         | ((ulong)(uint)(rs3 & 0x7F) << 19)
         | (((ulong)imm & (ulong)0xFFFF) << 3)
         | ((ulong)size & (ulong)0x7);

    /// <summary>I-form with the immediate widened over rs2 and rs3: bits 32:3.</summary>
    public static ulong EncodeWide(Op op, int rd, int rs1, long imm, Size size = Size.D)
        => Head(op)
         | ((ulong)(uint)(rd  & 0x7F) << 40)
         | ((ulong)(uint)(rs1 & 0x7F) << 33)
         | (((ulong)imm & (ulong)0x3FFFFFFF) << 3)
         | ((ulong)size & (ulong)0x7);

    /// <summary>
    /// Widened vector immediate form.  A vectorized I-form keeps the scalar
    /// broadcast constant in bits 32:8 and uses the low immediate byte for the
    /// element/mask overlay.  Keeping this separate from <see cref="EncodeWide"/>
    /// prevents either operand from silently overwriting the other.
    /// </summary>
    public static ulong EncodeVectorWide(Op op, int rd, int rs1, long scalar,
                                         int overlay, Size size)
    {
        if (scalar < -(1L << 24) || scalar >= (1L << 24))
        {
            throw new ArgumentOutOfRangeException(nameof(scalar),
                "a vector broadcast immediate must fit in signed 25 bits");
        }
        if ((uint)overlay > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(overlay),
                "a vector operand overlay must fit in eight bits");
        }
        return Head(op)
             | ((ulong)(uint)(rd  & 0x7F) << 40)
             | ((ulong)(uint)(rs1 & 0x7F) << 33)
             | (((ulong)scalar & (ulong)0x1FFFFFF) << 8)
             | ((ulong)(uint)overlay << 3)
             | ((ulong)size & (ulong)0x7);
    }

    /// <summary>Branch with the displacement widened over rs3: bits 25:3.</summary>
    public static ulong EncodeBranch(Op op, int rs1, int rs2, long disp,
                                     Size size = Size.D)
        => Head(op)
         | ((ulong)(uint)(rs1 & 0x7F) << 33)
         | ((ulong)(uint)(rs2 & 0x7F) << 26)
         | (((ulong)disp & (ulong)0x7FFFFF) << 3)
         | ((ulong)size & (ulong)0x7);

    /// <summary>Jump with the displacement over every register field: bits 46:3.</summary>
    public static ulong EncodeJump(Op op, long disp)
        => Head(op) | (((ulong)disp & (ulong)0xFFFFFFFFFFF) << 3);

    // ---- decoding -------------------------------------------------------

    public static Op   OpOf(ulong w)  => (Op)(int)((w >> 47) & (ulong)0x1FFFF);
    public static int  Rd(ulong w)    => (int)((w >> 40) & (ulong)0x7F);
    public static int  Rs1(ulong w)   => (int)((w >> 33) & (ulong)0x7F);
    public static int  Rs2(ulong w)   => (int)((w >> 26) & (ulong)0x7F);
    public static int  Rs3(ulong w)   => (int)((w >> 19) & (ulong)0x7F);
    public static Size SizeOf(ulong w)=> (Size)(w & (ulong)0x7);

    /// <summary>
    /// The NARROW immediate: bits 18:3, the field Encode writes.
    ///
    /// Wide() reaches bits 32:3, which is this run EXTENDED OVER rs2 and
    /// rs3 -- right for a form with no second or third register, and quietly
    /// wrong for one that has them. A form carrying both registers and a
    /// small literal reads it here, or it reads the register NUMBERS as part
    /// of its operand: the string unit's substring search did exactly that
    /// and hunted for a needle several thousand characters long, which finds
    /// nothing and looks like an ordinary miss.
    /// </summary>
    public static int  Imm16(ulong w) => (int)((w >> 3) & (ulong)0xFFFF) << 16 >> 16;
    public static long Wide(ulong w)  => (long)((w >> 3) & (ulong)0x3FFFFFFF) << 34 >> 34;
    public static long VectorWide(ulong w) => (long)((w >> 8) & (ulong)0x1FFFFFF) << 39 >> 39;

    public static long BrDisp(ulong w)=> (long)((w >> 3) & (ulong)0x7FFFFF) << 41 >> 41;
    public static long JmpDisp(ulong w)=> (long)((w >> 3) & (ulong)0xFFFFFFFFFFF) << 20 >> 20;
    /// <summary>The register-set mask for pushm and popm.</summary>
    public static int  Mask16(ulong w)=> (int)((w >> 3) & (ulong)0xFFFF);

    // ---- size helpers ---------------------------------------------------

    /// <summary>Bit width of a scalar size, or 0 for the vector and reserved encodings.</summary>
    public static int BitsOf(Size s) => s switch
    {
        Size.B => 8,
        Size.H => 16,
        Size.W => 32,
        Size.D => 64,
        _      => 0,
    };

    /// <summary>Byte width of a scalar size, or 0 when it has none.</summary>
    public static int BytesOf(Size s) => BitsOf(s) >> 3;

    /// <summary>
    /// Narrows a result to its operand size, sign-extending back to 64 bits.
    /// Narrow results are always canonically represented so that a comparison
    /// at full width is correct without re-normalising first.
    /// </summary>
    public static long Narrow(long v, Size s) => s switch
    {
        Size.B => (sbyte)v,
        Size.H => (short)v,
        Size.W => (int)v,
        _      => v,
    };

    /// <summary>Zero-extends a value of the given size into 64 bits.</summary>
    public static long Widen(long v, Size s) => s switch
    {
        Size.B => (byte)v,
        Size.H => (ushort)v,
        Size.W => (uint)v,
        _      => v,
    };

    /// <summary>Shift amounts wrap within the operand width, as the hardware would.</summary>
    public static int ShiftMask(Size s) => BitsOf(s) - 1;

    public static bool IsVectorSize(Size size)
        => size is Size.V128 or Size.V256 or Size.V512;

    /// <summary>Scalar operations which Rev. 1.6 permits the Vector unit to widen.</summary>
    public static bool IsVectorizable(Op op) => op is
        Op.Add or Op.Sub or Op.Mul or Op.MulH or Op.MulHU
        or Op.Div or Op.DivU or Op.Mod or Op.ModU
        or Op.Min or Op.Max or Op.MinU or Op.MaxU
        or Op.And or Op.Or or Op.Xor or Op.Shl or Op.Shr or Op.Sar
        or Op.Rol or Op.Ror
        or Op.AddI or Op.MulI or Op.AndI or Op.OrI or Op.XorI
        or Op.ShlI or Op.ShrI or Op.SarI
        or Op.Popc or Op.Clz or Op.Ctz or Op.Bswap or Op.Brev or Op.Par
        or Op.FAdd or Op.FSub or Op.FMul or Op.FDiv or Op.FSqrt
        or Op.FNeg or Op.FAbs or Op.FMin or Op.FMax or Op.Fma
        or Op.Floor or Op.Ceil or Op.Round or Op.Trunc
        or Op.CvtI2F or Op.CvtF2I or Op.CvtFF or Op.MovX2F or Op.MovF2X;

    public static bool IsVectorInstruction(Op op)
        => ((int)op >> 9) == 0x05;

    // ---- registers ------------------------------------------------------

    public static string RegName(int r) => r switch
    {
        RegZero => "zero",
        RegSp   => "sp",
        RegLr   => "lr",
        _       => "r" + r.ToString(),
    };

    public static string FpRegName(int r) => "f" + r.ToString();

    /// <summary>Parses r0..r127 plus the zero/sp/lr aliases. Returns -1 when unrecognised.</summary>
    public static int ParseReg(string text)
    {
        if (text.Length == 0)
        {
            return -1;
        }

        if (text.Equals("zero", StringComparison.OrdinalIgnoreCase))
        {
            return RegZero;
        }

        if (text.Equals("sp", StringComparison.OrdinalIgnoreCase))
        {
            return RegSp;
        }

        if (text.Equals("lr", StringComparison.OrdinalIgnoreCase))
        {
            return RegLr;
        }

        if (text[0] is not ('r' or 'R'))
        {
            return -1;
        }

        if (!int.TryParse(text.AsSpan(1), out int n) || n < 0 || n >= RegMax)
        {
            return -1;
        }
        return n;
    }

    /// <summary>Parses f0..f127. Returns -1 when unrecognised.</summary>
    public static int ParseFpReg(string text)
    {
        if (text.Length < 2 || text[0] is not ('f' or 'F'))
        {
            return -1;
        }

        if (!int.TryParse(text.AsSpan(1), out int n) || n < 0 || n >= RegMax)
        {
            return -1;
        }
        return n;
    }

    /// <summary>Parses a size suffix: .b .h .w .d and the vector widths.</summary>
    public static bool TryParseSize(string text, out Size size)
    {
        switch (text.ToLowerInvariant())
        {
            case "b":    size = Size.B;    return true;
            case "h":    size = Size.H;    return true;
            case "w":    size = Size.W;    return true;
            case "d":    size = Size.D;    return true;
            case "v128": size = Size.V128; return true;
            case "v256": size = Size.V256; return true;
            case "v512": size = Size.V512; return true;
            default:     size = Size.D;    return false;
        }
    }

    public static string SizeSuffix(Size s) => s switch
    {
        Size.B    => "b",
        Size.H    => "h",
        Size.W    => "w",
        Size.D    => "d",
        Size.V128 => "v128",
        Size.V256 => "v256",
        Size.V512 => "v512",
        _         => "?",
    };
}
