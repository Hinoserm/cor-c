#nullable enable
namespace Corsac.Lang;



/// <summary>
/// The built-in surface a program can reach without any library. Deliberately
/// tiny: it exists so a compiled program can prove it ran, and everything else
/// belongs in code written in the language itself.
/// </summary>
public static class Prelude
{
    public const string TypeName = "Sys";
    public const string MathType = "Math";
    public const string MachineType = "Machine";
    public const string Raw0 = "Raw0";
    public const string Raw1 = "Raw1";
    public const string Raw2 = "Raw2";
    public const string Raw3 = "Raw3";
    public const string Raw4 = "Raw4";
    public const string PrintInt = "PrintInt";
    public const string Print = "Print";
    public const string PrintChar = "PrintChar";
    public const string PrintLine = "PrintLine";
    public const string PrintHex = "PrintHex";
    public const string Exit = "Exit";
    public const string Word = "Word";
    public const string Bits = "Bits";
    public const string FromBits = "FromBits";

    // The block primitives. These are what let String live in the standard
    // library as ordinary source rather than being emitted by the compiler
    // behind the author's back.
    public const string NewBytes = "NewBytes";
    public const string GetByte = "GetByte";
    public const string SetByte = "SetByte";
    public const string Copy = "Copy";
    public const string CopyNoOverlap = "CopyNoOverlap";
    public const string CompareBytes = "CompareBytes";

    // Restartable string-unit primitives. Unit 0x0a is mandatory in the
    // current architecture, so the standard library reaches these directly;
    // keeping the calls as typed source still makes their ABI visible.
    public const string StringCompare = "StringCompare";
    public const string StringEqual = "StringEqual";
    public const string StringHash = "StringHash";
    public const string StringCopy = "StringCopy";
    public const string StringFill = "StringFill";
    public const string StringFind = "StringFind";
    public const string StringFindReverse = "StringFindReverse";
    public const string StringSearch = "StringSearch";
    public const string StringSpan = "StringSpan";
    public const string StringBreak = "StringBreak";
    public const string StringTranslate = "StringTranslate";
    public const string StringLower = "StringLower";
    public const string StringUpper = "StringUpper";
    public const string StringNumber = "StringNumber";
    public const string StringFormat = "StringFormat";

    // The raw ones, and the whole of what a heap written in this language needs.
    // A memory manager has to be able to read and write memory that is not any
    // object yet, and to hand back as a reference something it worked out as a
    // number. There is no way to write one without these and no way to write one
    // safely with them, which is why they are here rather than in a library:
    // somebody reading Sys can see the entire list of things that can go wrong.
    public const string Peek = "Peek";
    public const string Poke = "Poke";
    public const string MemoryCopy = "MemoryCopy";
    public const string MemoryCopyNoCache = "MemoryCopyNoCache";
    public const string CacheFlush = "CacheFlush";
    public const string CacheInvalidate = "CacheInvalidate";
    public const string CacheFlushInvalidate = "CacheFlushInvalidate";
    public const string CacheZero = "CacheZero";
    public const string InstructionCacheInvalidate = "InstructionCacheInvalidate";
    public const string CacheFlushAll = "CacheFlushAll";
    public const string Allocate = "Allocate";
    public const string ProbeDevice = "ProbeDevice";
    public const string HaltIo = "HaltIo";
    public const string ReadStringRegister = "ReadStringRegister";
    public const string ReadIoStatus = "ReadIoStatus";
    public const string WriteIoStatus = "WriteIoStatus";
    public const string ProbeRange = "ProbeRange";
    public const string PageProbe = "PageProbe";
    public const string PageWalk = "PageWalk";
    public const string PageNext = "PageNext";
    public const string PageMap = "PageMap";
    public const string PageUnmap = "PageUnmap";
    public const string PageCopy = "PageCopy";
    public const string CrossAddressCopy = "CrossAddressCopy";
    public const string LoadPhysicalProtection = "LoadPhysicalProtection";
    public const string LockPhysicalProtection = "LockPhysicalProtection";
    public const string EnterGate = "EnterGate";
    public const string ReturnGate = "ReturnGate";
    public const string ReturnInterrupt = "ReturnInterrupt";
    public const string ReturnMachineCheck = "ReturnMachineCheck";
    public const string TraceMarker = "TraceMarker";
    public const string TraceRead = "TraceRead";
    public const string ArmWatchpoint = "ArmWatchpoint";
    public const string DisarmWatchpoint = "DisarmWatchpoint";
    public const string ContextSave = "ContextSave";
    public const string ContextLoad = "ContextLoad";
    public const string MemorySet = "MemorySet";
    public const string PeekByte = "PeekByte";
    public const string PeekHalf = "PeekHalf";
    public const string PeekWord = "PeekWord";
    public const string PokeByte = "PokeByte";
    public const string PokeHalf = "PokeHalf";
    public const string PokeWord = "PokeWord";
    public const string Stack = "Stack";
    public const string Spill = "Spill";
    public const string Cas = "Cas";
    public const string CasRelaxed = "CasRelaxed";
    public const string CasAcquire = "CasAcquire";
    public const string CasRelease = "CasRelease";
    public const string CasSequential = "CasSequential";
    public const string CasPair = "CasPair";
    public const string AtomicAdd = "AtomicAdd";
    public const string AtomicAddRelaxed = "AtomicAddRelaxed";
    public const string AtomicAddAcquire = "AtomicAddAcquire";
    public const string AtomicAddRelease = "AtomicAddRelease";
    public const string AtomicAddSequential = "AtomicAddSequential";
    public const string AtomicAnd = "AtomicAnd";
    public const string AtomicAndRelaxed = "AtomicAndRelaxed";
    public const string AtomicAndAcquire = "AtomicAndAcquire";
    public const string AtomicAndRelease = "AtomicAndRelease";
    public const string AtomicAndSequential = "AtomicAndSequential";
    public const string AtomicOr = "AtomicOr";
    public const string AtomicOrRelaxed = "AtomicOrRelaxed";
    public const string AtomicOrAcquire = "AtomicOrAcquire";
    public const string AtomicOrRelease = "AtomicOrRelease";
    public const string AtomicOrSequential = "AtomicOrSequential";
    public const string AtomicXor = "AtomicXor";
    public const string AtomicXorRelaxed = "AtomicXorRelaxed";
    public const string AtomicXorAcquire = "AtomicXorAcquire";
    public const string AtomicXorRelease = "AtomicXorRelease";
    public const string AtomicXorSequential = "AtomicXorSequential";
    public const string AtomicSwap = "AtomicSwap";
    public const string AtomicSwapRelaxed = "AtomicSwapRelaxed";
    public const string AtomicSwapAcquire = "AtomicSwapAcquire";
    public const string AtomicSwapRelease = "AtomicSwapRelease";
    public const string AtomicSwapSequential = "AtomicSwapSequential";
    public const string AtomicMin = "AtomicMin";
    public const string AtomicMinRelaxed = "AtomicMinRelaxed";
    public const string AtomicMinAcquire = "AtomicMinAcquire";
    public const string AtomicMinRelease = "AtomicMinRelease";
    public const string AtomicMinSequential = "AtomicMinSequential";
    public const string AtomicMax = "AtomicMax";
    public const string AtomicMaxRelaxed = "AtomicMaxRelaxed";
    public const string AtomicMaxAcquire = "AtomicMaxAcquire";
    public const string AtomicMaxRelease = "AtomicMaxRelease";
    public const string AtomicMaxSequential = "AtomicMaxSequential";
    public const string AtomicClaim = "AtomicClaim";
    public const string LoadIndexed = "LoadIndexed";
    public const string LoadAcquire = "LoadAcquire";
    public const string LoadUnsignedByte = "LoadUnsignedByte";
    public const string LoadUnsignedHalf = "LoadUnsignedHalf";
    public const string LoadUnsignedWord = "LoadUnsignedWord";
    public const string LoadAcquireUnsignedByte = "LoadAcquireUnsignedByte";
    public const string LoadAcquireUnsignedHalf = "LoadAcquireUnsignedHalf";
    public const string LoadAcquireUnsignedWord = "LoadAcquireUnsignedWord";
    public const string StoreRelease = "StoreRelease";
    public const string Prefetch = "Prefetch";
    public const string PrefetchWrite = "PrefetchWrite";
    public const string LoadIndexedUnsignedByte = "LoadIndexedUnsignedByte";
    public const string LoadIndexedUnsignedHalf = "LoadIndexedUnsignedHalf";
    public const string LoadIndexedUnsignedWord = "LoadIndexedUnsignedWord";
    public const string StoreIndexedByte = "StoreIndexedByte";
    public const string StoreIndexedHalf = "StoreIndexedHalf";
    public const string StoreIndexedWord = "StoreIndexedWord";
    public const string StoreIndexed = "StoreIndexed";
    public const string FirstSet = "FirstSet";
    public const string PopCountBlock = "PopCountBlock";
    public const string Checksum = "Checksum";
    public const string SwapBytes = "SwapBytes";
    public const string ReverseBytes = "ReverseBytes";
    public const string MultiplyHigh = "MultiplyHigh";
    public const string MultiplyHighUnsigned = "MultiplyHighUnsigned";
    public const string ReverseBits = "ReverseBits";
    public const string ReverseBitsByte = "ReverseBitsByte";
    public const string ReverseBitsHalf = "ReverseBitsHalf";
    public const string ReverseBitsWord = "ReverseBitsWord";
    public const string PopulationCount = "PopulationCount";
    public const string LeadingZeroCount = "LeadingZeroCount";
    public const string TrailingZeroCount = "TrailingZeroCount";
    public const string ByteSwap = "ByteSwap";
    public const string RotateLeft = "RotateLeft";
    public const string RotateRight = "RotateRight";
    public const string RotateLeftWord = "RotateLeftWord";
    public const string RotateRightWord = "RotateRightWord";
    public const string MinUnsigned = "MinUnsigned";
    public const string MaxUnsigned = "MaxUnsigned";
    public const string Maximum = "Max";
    public const string SetBit = "SetBit";
    public const string ClearBit = "ClearBit";
    public const string TestBit = "TestBit";
    public const string LowBitMask = "LowBitMask";
    public const string Parity = "Parity";
    public const string BitFieldInsert = "BitFieldInsert";
    public const string BitFieldExtract = "BitFieldExtract";
    public const string BitDeposit = "BitDeposit";
    public const string BitExtract = "BitExtract";
    public const string Interleave = "Interleave";
    public const string Deinterleave = "Deinterleave";
    public const string ConstantTimeSelect = "ConstantTimeSelect";
    public const string CarrylessMultiply = "CarrylessMultiply";
    public const string GaloisFieldMultiply = "GaloisFieldMultiply";
    public const string MultiplyAccumulate = "MultiplyAccumulate";
    public const string TryDesKey = "TryDesKey";
    public const string TryDesEncrypt = "TryDesEncrypt";
    public const string TryDesDecrypt = "TryDesDecrypt";
    public const string TryHashBlock = "TryHashBlock";
    public const string TryChaCha20Block = "TryChaCha20Block";
    public const string TryField25519Multiply = "TryField25519Multiply";
    public const string TryField25519Square = "TryField25519Square";
    public const string TryField25519Reduce = "TryField25519Reduce";
    public const string TryPoly1305Block = "TryPoly1305Block";
    public const string TryGHashBlock = "TryGHashBlock";
    public const string TryAesEncryptBlock = "TryAesEncryptBlock";
    public const string TryAesDecryptBlock = "TryAesDecryptBlock";
    public const string TryAesKey128Next = "TryAesKey128Next";
    public const string TryAesKey256Next = "TryAesKey256Next";
    public const string CryptoAssistCapabilities = "CryptoAssistCapabilities";
    public const string TryHashInitialize = "TryHashInitialize";
    public const string TryHashMd5Block = "TryHashMd5Block";
    public const string TryHashSha1Block = "TryHashSha1Block";
    public const string TryHashSha2Block = "TryHashSha2Block";
    public const string TryHashFinish = "TryHashFinish";
    public const string TryRandom = "TryRandom";
    public const string TryModReduce = "TryModReduce";
    public const string TryMontgomeryReduce = "TryMontgomeryReduce";
    public const string TrySBox = "TrySBox";
    public const string TryBitGet = "TryBitGet";
    public const string TryBitPeek = "TryBitPeek";
    public const string TryBitPut = "TryBitPut";
    public const string TryBitFlush = "TryBitFlush";
    public const string TryBitMatrixTranspose = "TryBitMatrixTranspose";
    public const string TryBitMatrixMultiply = "TryBitMatrixMultiply";
    public const string TryMoveToDecimal = "TryMoveToDecimal";
    public const string TryMoveFromDecimal = "TryMoveFromDecimal";
    public const string TryMoveToVectorLane = "TryMoveToVectorLane";
    public const string TryMoveFromVectorLane = "TryMoveFromVectorLane";
    public const string TryWriteCompressionRegister = "TryWriteCompressionRegister";
    public const string TryReadCompressionRegister = "TryReadCompressionRegister";
    public const string TryDecimalNegate = "TryDecimalNegate";
    public const string TryDecimalAbsolute = "TryDecimalAbsolute";
    public const string TryDecimalLoad = "TryDecimalLoad";
    public const string TryDecimalStore = "TryDecimalStore";
    public const string TryDecimalFromInteger = "TryDecimalFromInteger";
    public const string TryDecimalToInteger = "TryDecimalToInteger";
    public const string TryDecimalCompare = "TryDecimalCompare";
    public const string TryDecimalAdd = "TryDecimalAdd";
    public const string TryDecimalSubtract = "TryDecimalSubtract";
    public const string TryDecimalMultiply = "TryDecimalMultiply";
    public const string TryDecimalDivide = "TryDecimalDivide";
    public const string TryDecimalRemainder = "TryDecimalRemainder";
    public const string TryDecimalTruncate = "TryDecimalTruncate";
    public const string TryDecimalFloor = "TryDecimalFloor";
    public const string TryDecimalCeiling = "TryDecimalCeiling";
    public const string TryDecimalRound = "TryDecimalRound";
    public const string TryDecimalScale = "TryDecimalScale";
    public const string TryDecimalFromFloat = "TryDecimalFromFloat";
    public const string TryDecimalToFloat = "TryDecimalToFloat";
    public const string TryDecimalPack = "TryDecimalPack";
    public const string TryDecimalUnpack = "TryDecimalUnpack";
    public const string TryDecimalParse = "TryDecimalParse";
    public const string TryDecimalFormat = "TryDecimalFormat";
    public const string TryPackedDecimalAdd = "TryPackedDecimalAdd";
    public const string TryPackedDecimalSubtract = "TryPackedDecimalSubtract";
    public const string TryPackedDecimalMultiply = "TryPackedDecimalMultiply";
    public const string TryPackedDecimalDivide = "TryPackedDecimalDivide";
    public const string TryPackedDecimalCompare = "TryPackedDecimalCompare";
    public const string TryPackedDecimalZap = "TryPackedDecimalZap";
    public const string TryPackedDecimalShift = "TryPackedDecimalShift";
    public const string TryPackedDecimalPack = "TryPackedDecimalPack";
    public const string TryPackedDecimalUnpack = "TryPackedDecimalUnpack";
    public const string TryPackedDecimalToBinary = "TryPackedDecimalToBinary";
    public const string TryPackedDecimalFromBinary = "TryPackedDecimalFromBinary";
    public const string TryPackedDecimalEdit = "TryPackedDecimalEdit";
    public const string TryPackedDecimalEditMarked = "TryPackedDecimalEditMarked";
    public const string TryLongestCommonPrefix = "TryLongestCommonPrefix";
    public const string TryLzCopy = "TryLzCopy";
    public const string TryLzHash = "TryLzHash";
    public const string TryLzFind = "TryLzFind";
    public const string TryHuffmanDecode = "TryHuffmanDecode";
    public const string TryHuffmanEncode = "TryHuffmanEncode";
    public const string TryHuffmanLengths = "TryHuffmanLengths";
    public const string TryLzwLookup = "TryLzwLookup";
    public const string TryLzwAdd = "TryLzwAdd";
    public const string TryLzwExpand = "TryLzwExpand";
    public const string TryAdaptiveHuffmanUpdate = "TryAdaptiveHuffmanUpdate";
    public const string TryAdaptiveHuffmanDecode = "TryAdaptiveHuffmanDecode";
    public const string TryAdaptiveHuffmanEncode = "TryAdaptiveHuffmanEncode";
    public const string TryBoxValue = "TryBoxValue";
    public const string TryUnboxValue = "TryUnboxValue";
    public const string TryMonitorEnter = "TryMonitorEnter";
    public const string TryMonitorExit = "TryMonitorExit";
    public const string TryInterfaceDispatch = "TryInterfaceDispatch";
    public const string TryVirtualDispatch = "TryVirtualDispatch";
    public const string TryUnwind = "TryUnwind";
    public const string TrySetVectorLength = "TrySetVectorLength";
    public const string TryVectorSplat = "TryVectorSplat";
    public const string TryVectorSelect = "TryVectorSelect";
    public const string TryVectorShuffle = "TryVectorShuffle";
    public const string TryVectorCompare = "TryVectorCompare";
    public const string TryVectorReduce = "TryVectorReduce";
    public const string TryVectorLoad = "TryVectorLoad";
    public const string TryVectorStore = "TryVectorStore";
    public const string TryVectorLoadStrided = "TryVectorLoadStrided";
    public const string TryVectorStoreStrided = "TryVectorStoreStrided";
    public const string TryVectorGather = "TryVectorGather";
    public const string TryVectorScatter = "TryVectorScatter";
    public const string Fence = "Fence";
    public const string Drain = "Drain";
    public const string Breakpoint = "Breakpoint";
    public const string SingleStep = "SingleStep";
    public const string ReadIoByte = "ReadIoByte";
    public const string ReadIoHalf = "ReadIoHalf";
    public const string ReadIoWord = "ReadIoWord";
    public const string ReadIo = "ReadIo";
    public const string WriteIoByte = "WriteIoByte";
    public const string WriteIoHalf = "WriteIoHalf";
    public const string WriteIoWord = "WriteIoWord";
    public const string WriteIo = "WriteIo";
    public const string WaitEq = "WaitEq";
    public const string Pause = "Pause";
    public const string Self = "Self";
    public const string FirmwareTable = "FirmwareTable";
    public const string FirmwareTableBytes = "FirmwareTableBytes";
    public const string BootStackBase = "BootStackBase";
    public const string BootStackBytes = "BootStackBytes";
    public const string AddressOf = "AddressOf";
    public const string Call = "Call";
    public const string Call5 = "Call5";
    public const string CallOption = "CallOption";
    public const string Unspill = "Unspill";
    public const string Resume = "Resume";
    public const string Arena = "Arena";
    public const string ArenaEnd = "ArenaEnd";
    public const string StaticBase = "StaticBase";
    public const string StaticTop = "StaticTop";

    // Driving a card, and stopping until one has something to say. A driver
    // written in this language needs all four: the two ways to issue a command
    // block, the channel register that carries the answer to the one that did
    // not wait, and the instruction that hands the processor back to the
    // machine until an interrupt arrives.
    public const string Io = "Io";
    public const string StartIo = "StartIo";
    public const string IoStatus = "IoStatus";
    public const string Idle = "Idle";
    public const string Syscall = "Syscall";
    public const string CpuId = "CpuId";
    public const string SetArena = "SetArena";
    public const string SetArenaEnd = "SetArenaEnd";
    public const string Elr = "Elr";
    public const string SetElr = "SetElr";
    public const string Cause = "Cause";
    public const string Detail = "Detail";
    public const string SetSavedRing = "SetSavedRing";
    public const string Idles = "Idles";
    public const string Cycles = "Cycles";

    // Translation. Everything a kernel needs to give a process an address
    // space of its own, and nothing a program can reach: the machine refuses
    // all six in user mode, on read as well as on write.
    public const string Ring = "Ring";
    public const string SavedRing = "SavedRing";
    public const string Level = "Level";
    public const string SetLevel = "SetLevel";
    public const string SetLibBase = "SetLibBase";
    public const string LibBase = "LibBase";

    // The two halves of stack banking. A kernel needs both: one to say where a
    // trap from user mode is to be taken, and one to read back the stack the
    // program was using -- and to set it when it resumes a DIFFERENT program
    // from the one that trapped.
    public const string UserSp = "UserSp";
    public const string StackPointer = "StackPointer";
    public const string SetUserSp = "SetUserSp";
    public const string SetKernelSp = "SetKernelSp";

    /// The architectural current-context software pointer. The present system
    /// profile uses CtlThreadPointer for the firmware-assigned per-processor
    /// block; a later profile may context-switch it as a thread pointer.
    public const string Mine = "Mine";
    public const string SetMine = "SetMine";
    public const string SetTimer = "SetTimer";
    public const string Lpic = "Lpic";
    public const string LpicGet = "LpicGet";
    public const string Units = "Units";
    public const string ClockHz = "ClockHz";
    public const string PageTable = "PageTable";
    public const string SetPageTable = "SetPageTable";
    public const string MmuCtl = "MmuCtl";
    public const string SetMmuCtl = "SetMmuCtl";
    public const string FaultAddr = "FaultAddr";
    public const string FaultStatus = "FaultStatus";
    public const string TlbFlush = "TlbFlush";

    /// <summary>The library type '+' and the comparisons on strings lower into.</summary>
    public const string StringType = "String";
    public const string ConcatMethod = "Concat";
    public const string CompareMethod = "Compare";
    public const string FromIntMethod = "FromInt";
    public const string FromUIntMethod = "FromUInt";
    public const string FromBoolMethod = "FromBool";
    public const string FromDoubleMethod = "FromDouble";

    public const string Source = """
        static class Sys {
            public static void PrintInt(long value) { }
            public static void Print(string text) { }
            public static void PrintChar(int ch) { }
            public static void PrintHex(long value) { }
            public static void PrintLine() { }
            public static void Exit(int code) { }
            public static long Word(object x) { return 0; }

            // THE BITS OF A REAL, and back again. A double lives in the other
            // register bank, so reinterpreting one is a MOVE between banks --
            // which is a machine instruction and cannot be spelt any other way.
            // System.BitConverter is written on top of these two.
            public static long Bits(double x) { return 0; }
            public static double FromBits(long x) { return 0; }

            // A string and a byte array are the same shape: a length word and
            // that many bytes. These five are the whole of what the machine
            // offers for building one, and String is written on top of them.
            public static string NewBytes(int length) { return ""; }
            public static int GetByte(string s, int at) { return 0; }
            public static void SetByte(string s, int at, int value) { }
            // AND WITH A MACHINE-WORD CURSOR. An index is an int in C# because
            // an array's length is, and String keeps to that; these are the
            // machine underneath, where an address is sixty-four bits and a
            // walk over bytes counts in the width the registers are. System
            // .Array does the same thing for the same reason -- Copy has both
            // an int and a long overload -- and without it every byte loop in a
            // program that counts in words casts at each step, or keeps a
            // second cursor beside the first.
            public static int GetByte(string s, long at) { return 0; }
            public static void SetByte(string s, long at, int value) { }
            public static void Copy(string dst, int dstAt, string src, int srcAt, int count) { }
            public static void Copy(byte[] dst, int dstAt, byte[] src, int srcAt, int count) { }
            // Like Copy, but the two source ranges must not overlap.  It gives
            // the portable runtime vector dispatch permission to stream forward
            // through V512 chunks; callers that might overlap retain Copy's
            // memmove semantics.
            public static void CopyNoOverlap(string dst, int dstAt, string src, int srcAt, int count) { }
            public static void CopyNoOverlap(byte[] dst, int dstAt, byte[] src, int srcAt, int count) { }
            public static int CompareBytes(string a, int aAt, string b, int bAt, int count) { return 0; }

            // Unit 0x0a. These are deliberately low-level: their cursor and
            // count arguments name an exact span, while String supplies the
            // friendly checked operations and the no-unit fallback.
            public static int StringCompare(string a, int aAt, string b, int bAt, int count) { return 0; }
            public static bool StringEqual(string a, int aAt, string b, int bAt, int count) { return false; }
            public static long StringHash(string s, int at, int count, long seed) { return 0; }
            public static int StringCopy(string dst, int dstAt, string src, int srcAt, int count) { return 0; }
            public static int StringFill(string dst, int dstAt, int value, int count) { return 0; }
            public static int StringFind(string s, int at, int value, int count) { return 0; }
            public static int StringFindReverse(string s, int at, int value, int count) { return 0; }
            public static int StringSearch(string s, int at, string what, int whatAt, int count) { return 0; }
            public static int StringSpan(string s, int at, int count, long bitmap) { return 0; }
            public static int StringBreak(string s, int at, int count, long bitmap) { return 0; }
            public static int StringTranslate(string s, int at, int count, long table) { return 0; }
            public static int StringLower(string s, int at, int count) { return 0; }
            public static int StringUpper(string s, int at, int count) { return 0; }
            public static long StringNumber(string s, int at, int count) { return 0; }
            public static int StringFormat(string dst, int at, long value, int capacity) { return 0; }

            // Raw memory, for the one program that has to manage it. Peek and
            // Poke are exactly as unsafe as they look; Ref turns an address into
            // a reference, which is how an allocator hands back what it found.
            public static long Peek(long at) { return 0; }
            public static void Poke(long at, long value) { }
            public static long LoadIndexed(long baseAddress, long index) { return 0; }
            public static long LoadAcquire(long address) { return 0; }
            public static long LoadUnsignedByte(long address) { return 0; }
            public static long LoadUnsignedHalf(long address) { return 0; }
            public static long LoadUnsignedWord(long address) { return 0; }
            public static long LoadAcquireUnsignedByte(long address) { return 0; }
            public static long LoadAcquireUnsignedHalf(long address) { return 0; }
            public static long LoadAcquireUnsignedWord(long address) { return 0; }
            public static void StoreRelease(long address, long value) { }
            public static void Prefetch(long address) { }
            public static void PrefetchWrite(long address) { }
            public static long LoadIndexedUnsignedByte(long baseAddress, long index) { return 0; }
            public static long LoadIndexedUnsignedHalf(long baseAddress, long index) { return 0; }
            public static long LoadIndexedUnsignedWord(long baseAddress, long index) { return 0; }
            public static void StoreIndexedByte(long baseAddress, long index, long value) { }
            public static void StoreIndexedHalf(long baseAddress, long index, long value) { }
            public static void StoreIndexedWord(long baseAddress, long index, long value) { }
            public static void StoreIndexed(long baseAddress, long index, long value) { }
            public static void MemoryCopy(long dst, long src, long count) { }
            public static void MemoryCopyNoCache(long destination, long source, long count) { }
            public static long CacheFlush(long address, long byteCount) { return 0; }
            public static long CacheInvalidate(long address, long byteCount) { return 0; }
            public static long CacheFlushInvalidate(long address, long byteCount) { return 0; }
            public static long CacheZero(long address, long byteCount) { return 0; }
            public static long InstructionCacheInvalidate(long address, long byteCount) { return 0; }
            public static void CacheFlushAll() { }
            public static long Allocate(long byteCount) { return 0; }
            public static long ProbeDevice(long selector) { return 0; }
            public static long HaltIo(long channel) { return 0; }
            public static long ReadStringRegister(int selector) { return 0; }
            public static long ReadIoStatus(long channel) { return 0; }
            public static void WriteIoStatus(long channel, long value) { }
            public static long ProbeRange(long address, long byteCount, long permissions, out long status) { status = 0; return 0; }
            public static long PageProbe(long address, out long status) { status = 0; return 0; }
            public static long PageWalk(long address, out long status) { status = 0; return 0; }
            public static long PageNext(long cursor, out long leafStatus) { leafStatus = 0; return 0; }
            public static long PageMap(long address, long pte, long options, out long status) { status = 0; return 0; }
            public static long PageUnmap(long address, out long status) { status = 0; return 0; }
            public static void PageCopy(ref long destination, ref long source, ref long pages, ref long status) { }
            public static void CrossAddressCopy(ref long primary, ref long secondary, ref long bytes, ref long status) { }
            public static long LoadPhysicalProtection(long descriptor, long count) { return 0; }
            public static void LockPhysicalProtection() { }
            public static void EnterGate(long selector) { }
            public static void ReturnGate() { }
            public static void ReturnInterrupt() { }
            public static void ReturnMachineCheck() { }
            public static long TraceMarker(long marker) { return 0; }
            public static void TraceRead(out long source, out long destination, out long kind, out long valid) { source = 0; destination = 0; kind = 0; valid = 0; }
            public static long ArmWatchpoint(long slot, long address, long control) { return 0; }
            public static long DisarmWatchpoint(long slot) { return 0; }
            public static long ContextSave(long descriptor, long groups, long generation) { return 0; }
            public static long ContextLoad(long descriptor, long groups, long generation) { return 0; }
            public static void MemorySet(long dst, long value, long count) { }

            // Mandatory core arithmetic and bit operations. MultiplyHigh is
            // the upper half of the full 128-bit product; BitFieldInsert keeps
            // every bit outside its literal position/length field unchanged.
            public static long MultiplyHigh(long left, long right) { return 0; }
            public static long MultiplyHighUnsigned(long left, long right) { return 0; }
            public static long ReverseBits(long value) { return 0; }
            public static long ReverseBitsByte(long value) { return 0; }
            public static long ReverseBitsHalf(long value) { return 0; }
            public static long ReverseBitsWord(long value) { return 0; }
            public static long PopulationCount(long value) { return 0; }
            public static long LeadingZeroCount(long value) { return 0; }
            public static long TrailingZeroCount(long value) { return 0; }
            public static long ByteSwap(long value) { return 0; }
            public static long RotateLeft(long value, long count) { return 0; }
            public static long RotateRight(long value, long count) { return 0; }
            public static long RotateLeftWord(long value, long count) { return 0; }
            public static long RotateRightWord(long value, long count) { return 0; }
            public static ulong MinUnsigned(ulong left, ulong right) { return 0; }
            public static ulong MaxUnsigned(ulong left, ulong right) { return 0; }
            public static long Max(long left, long right) { return 0; }
            public static long SetBit(long value, long bit) { return 0; }
            public static long ClearBit(long value, long bit) { return 0; }
            public static bool TestBit(long value, long bit) { return false; }
            public static long LowBitMask(int bits) { return 0; }
            public static long Parity(long value) { return 0; }
            public static long BitFieldInsert(long original, long value, int position, int length) { return 0; }
            public static long BitFieldExtract(long value, int position, int length) { return 0; }
            public static long BitDeposit(long value, long mask) { return 0; }
            public static long BitExtract(long value, long mask) { return 0; }
            public static long Interleave(long x, long y) { return 0; }
            public static long Deinterleave(long value, bool odd) { return 0; }
            public static long ConstantTimeSelect(long selected, long alternate, bool condition) { return 0; }
            public static long CarrylessMultiply(long left, long right, out long high) { high = 0; return 0; }
            public static long GaloisFieldMultiply(long left, long right) { return 0; }
            public static long MultiplyAccumulate(long left, long right, long carry, out long high) { high = 0; return 0; }
            public static bool TryDesKey(long key, int keyRegisterBase) { return false; }
            public static bool TryDesEncrypt(long block, int keyRegisterBase, out long result) { result = 0; return false; }
            public static bool TryDesDecrypt(long block, int keyRegisterBase, out long result) { result = 0; return false; }
            public static bool TryHashBlock(long stateAddress, long blockAddress, int bits) { return false; }
            public static bool TryChaCha20Block(long outputAddress, long stateAddress) { return false; }
            public static bool TryField25519Multiply(long output, long left, long right) { return false; }
            public static bool TryField25519Square(long output, long input) { return false; }
            public static bool TryField25519Reduce(long output, long input) { return false; }
            public static bool TryPoly1305Block(long accumulator, long r, long block, bool complete) { return false; }
            public static bool TryGHashBlock(long state, long block, long h) { return false; }
            public static bool TryAesEncryptBlock(long state, long roundKeys, int rounds) { return false; }
            public static bool TryAesDecryptBlock(long state, long roundKeys, int rounds) { return false; }
            public static bool TryAesKey128Next(long output, long previous, int roundConstant) { return false; }
            public static bool TryAesKey256Next(long output, long earlier, long recent, int control) { return false; }
            public static long CryptoAssistCapabilities() { return 0; }
            public static bool TryHashInitialize(int algorithm) { return false; }
            public static bool TryHashMd5Block(long blockAddress) { return false; }
            public static bool TryHashSha1Block(long blockAddress) { return false; }
            public static bool TryHashSha2Block(long blockAddress, int bits) { return false; }
            public static bool TryHashFinish(ref long outputCursor, long partialAddress, long partialBytes, long totalBytes) { return false; }
            public static bool TryRandom(out long value) { value = 0; return false; }
            public static bool TryModReduce(long high, long low, int hashRegister, out long result) { result = 0; return false; }
            public static bool TryMontgomeryReduce(long high, long low, int hashRegister, int keyRegister, out long result) { result = 0; return false; }
            public static bool TrySBox(long value, long byteCount, int keyRegisterBase, out long result) { result = 0; return false; }
            public static bool TryBitGet(ref long address, ref long cursor, long width, ref long progress, int option, out long value) { value = 0; return false; }
            public static bool TryBitPeek(ref long address, ref long cursor, long width, ref long progress, int option, out long value) { value = 0; return false; }
            public static bool TryBitPut(long value, ref long address, ref long cursor, long width, ref long progress, int option) { return false; }
            public static bool TryBitFlush(ref long address, ref long cursor, ref long progress, int option) { return false; }
            public static bool TryBitMatrixTranspose(long destinationDescriptor, long sourceDescriptor, ref long row, ref long state) { return false; }
            public static bool TryBitMatrixMultiply(long destinationDescriptor, long leftDescriptor, long rightDescriptor, ref long state) { return false; }
            public static bool TryMoveToDecimal(int register, long low, long high) { return false; }
            public static bool TryMoveFromDecimal(int register, out long low, out long high) { low = 0; high = 0; return false; }
            public static bool TryMoveToVectorLane(int register, long value, long lane, int vectorBits, int elementBits) { return false; }
            public static bool TryMoveFromVectorLane(int register, long lane, int vectorBits, int elementBits, out long value) { value = 0; return false; }
            public static bool TryWriteCompressionRegister(int register, long value) { return false; }
            public static bool TryReadCompressionRegister(int register, out long value) { value = 0; return false; }
            public static bool TryDecimalNegate(int destination, int source) { return false; }
            public static bool TryDecimalAbsolute(int destination, int source) { return false; }
            public static bool TryDecimalLoad(int register, long address) { return false; }
            public static bool TryDecimalStore(int register, long address) { return false; }
            public static bool TryDecimalFromInteger(int register, long value) { return false; }
            public static bool TryDecimalToInteger(int register, out long value) { value = 0; return false; }
            public static bool TryDecimalCompare(int left, int right, out long result) { result = 0; return false; }
            public static bool TryDecimalAdd(int destination, int left, int right) { return false; }
            public static bool TryDecimalSubtract(int destination, int left, int right) { return false; }
            public static bool TryDecimalMultiply(int destination, int left, int right) { return false; }
            public static bool TryDecimalDivide(int destination, int left, int right) { return false; }
            public static bool TryDecimalRemainder(int destination, int left, int right) { return false; }
            public static bool TryDecimalTruncate(int destination, int source) { return false; }
            public static bool TryDecimalFloor(int destination, int source) { return false; }
            public static bool TryDecimalCeiling(int destination, int source) { return false; }
            public static bool TryDecimalRound(int destination, int source, int scale, int mode) { return false; }
            public static bool TryDecimalScale(int destination, int source, int scale, int mode) { return false; }
            public static bool TryDecimalFromFloat(int register, double value) { return false; }
            public static bool TryDecimalToFloat(int register, out double value) { value = 0.0; return false; }
            public static bool TryDecimalPack(int register, long descriptor, ref long progress, out long status) { status = 0; return false; }
            public static bool TryDecimalUnpack(int register, ref long sourceCursor, long descriptor, ref long status) { return false; }
            public static bool TryDecimalParse(int register, ref long sourceCursor, long descriptor, ref long status) { return false; }
            public static bool TryDecimalFormat(int register, long descriptor, ref long progress, out long status) { status = 0; return false; }
            public static bool TryPackedDecimalAdd(long destination, long left, long right, ref long progress) { return false; }
            public static bool TryPackedDecimalSubtract(long destination, long left, long right, ref long progress) { return false; }
            public static bool TryPackedDecimalMultiply(long destination, long left, long right, ref long progress) { return false; }
            public static bool TryPackedDecimalDivide(long destination, long left, long right, ref long progress) { return false; }
            public static bool TryPackedDecimalCompare(long left, long right, ref long progress, out long result) { result = 0; return false; }
            public static bool TryPackedDecimalZap(long destination, long source, ref long progress) { return false; }
            public static bool TryPackedDecimalShift(long destination, long value, long shift, ref long progress) { return false; }
            public static bool TryPackedDecimalPack(long destination, long source, ref long progress) { return false; }
            public static bool TryPackedDecimalUnpack(long destination, long source, ref long progress) { return false; }
            public static bool TryPackedDecimalToBinary(long destination, long source, ref long progress) { return false; }
            public static bool TryPackedDecimalFromBinary(long destination, long source, ref long progress) { return false; }
            public static bool TryPackedDecimalEdit(long destination, long source, long picture, ref long progress) { return false; }
            public static bool TryPackedDecimalEditMarked(long destination, long source, long picture, ref long progress) { return false; }
            public static bool TryLongestCommonPrefix(ref long leftAddress, ref long leftOffset, ref long rightAddress, ref long rightOffset, ref long remaining, out long matched) { matched = 0; return false; }
            public static bool TryLzCopy(ref long destination, long offset, ref long remaining) { return false; }
            public static bool TryLzHash(long address, long bucketCount, int compressionRegister, out long hash) { hash = 0; return false; }
            public static bool TryLzFind(long address, ref long offset, long limitStatus, out long length) { length = 0; return false; }
            public static bool TryHuffmanDecode(ref long inputAddress, ref long bitOffset, ref long status, int table, int option, out long symbol) { symbol = 0; return false; }
            public static bool TryHuffmanEncode(ref long outputAddress, ref long bitOffset, long symbol, ref long status, int table, int option) { return false; }
            public static bool TryHuffmanLengths(long firstSymbol, long count, ref long status, int table, int option, out long built) { built = 0; return false; }
            public static bool TryLzwLookup(long prefix, long character, ref long status, int table, int option, out long code) { code = 0; return false; }
            public static bool TryLzwAdd(long prefix, long character, ref long status, int table, int option, out long code) { code = 0; return false; }
            public static bool TryLzwExpand(long code, ref long outputAddress, ref long status, int table, int option, out long written) { written = 0; return false; }
            public static bool TryAdaptiveHuffmanUpdate(long leaf, ref long status, int table, int option, out long updated) { updated = 0; return false; }
            public static bool TryAdaptiveHuffmanDecode(ref long inputAddress, ref long bitOffset, ref long status, int table, int option, out long symbol) { symbol = 0; return false; }
            public static bool TryAdaptiveHuffmanEncode(ref long outputAddress, ref long bitOffset, long symbol, ref long status, int table, int option) { return false; }
            public static bool TryBoxValue(long value, long descriptor, int mode, int width, out long boxed) { boxed = 0; return false; }
            public static bool TryUnboxValue(long boxed, long descriptor, int mode, int width, out long value) { value = 0; return false; }
            public static bool TryMonitorEnter(long objectAddress, long owner, long deadline, out long status) { status = 0; return false; }
            public static bool TryMonitorExit(long objectAddress, long owner, out long status) { status = 0; return false; }
            public static bool TryInterfaceDispatch(long objectAddress, long interfaceDescriptor, long cacheDescriptor, out long target) { target = 0; return false; }
            public static bool TryVirtualDispatch(long objectAddress, long classDescriptor, long slot, out long target) { target = 0; return false; }
            public static bool TryUnwind(ref long cursor, long exceptionObject, long targetDescriptor, ref long state) { return false; }
            public static bool TrySetVectorLength(long count, int vectorBits) { return false; }
            public static bool TryVectorSplat(int register, long value, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorSelect(int destination, int selected, int alternate, long mask, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorShuffle(int destination, int source, int indexes, long mask, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorCompare(int maskRegister, int left, int right, long control, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorReduce(int source, long mask, long reduction, int vectorBits, int elementBits, out long value) { value = 0; return false; }
            public static bool TryVectorLoad(int register, long address, int mask, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorStore(int register, long address, int mask, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorLoadStrided(int register, long address, long stride, int mask, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorStoreStrided(int register, long address, long stride, int mask, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorGather(int register, long address, int indexes, long mask, int vectorBits, int elementBits) { return false; }
            public static bool TryVectorScatter(int register, long address, int indexes, long mask, int vectorBits, int elementBits) { return false; }

            // A byte, a half word and a word, because that is what a structure
            // written down by somebody else is made of. Reading them out of a
            // word load with shifts and masks works and turns every field into
            // arithmetic that can be wrong without saying so.
            //
            // These do NOT sign-extend. Everything they are for -- a mode, a
            // link count, a block number, a length -- counts upwards from zero,
            // and a field that came back negative because its top bit was set
            // would be a bug a long way from here.
            public static long PeekByte(long at) { return 0; }
            public static long PeekHalf(long at) { return 0; }
            public static long PeekWord(long at) { return 0; }
            public static void PokeByte(long at, long value) { }
            public static void PokeHalf(long at, long value) { }
            public static void PokeWord(long at, long value) { }

            // Where the stack is now, and the arena bounds this program was
            // given. A heap has to know which memory is its to hand out.
            // Re-enters a method that suspended: its frame becomes the frame
            // pointer again and execution continues where it stopped. The
            // method's own epilogue returns here, because the link register
            // saved on the way in is this call's.
            public static void Resume(long frame, long at) { }

            public static long Stack() { return 0; }

            // Puts every register on the stack and takes them off again. A
            // collector that scans the stack for things pointing into the heap
            // has to scan the registers too, and the stack is the only place it
            // can look at them.
            public static void Spill() { }
            public static void Unspill() { }

            // The whole of what makes anything written in this language safe
            // for more than one processor. Without these a read, a change and a
            // write are three instructions with room between them, and two
            // processors get in each other's gaps.
            //
            // Cas answers with what was THERE, whether or not it matched, so a
            // caller learns why it failed rather than only that it did.
            public static long Cas(long at, long expect, long value) { return 0; }
            public static long CasRelaxed(long at, long expect, long value) { return 0; }
            public static long CasAcquire(long at, long expect, long value) { return 0; }
            public static long CasRelease(long at, long expect, long value) { return 0; }
            public static long CasSequential(long at, long expect, long value) { return 0; }
            public static bool CasPair(long at, long expectedLow, long expectedHigh, long desiredLow, long desiredHigh, out long observedLow) { observedLow = 0; return false; }
            public static long AtomicAdd(long at, long value) { return 0; }
            public static long AtomicAddRelaxed(long at, long value) { return 0; }
            public static long AtomicAddAcquire(long at, long value) { return 0; }
            public static long AtomicAddRelease(long at, long value) { return 0; }
            public static long AtomicAddSequential(long at, long value) { return 0; }
            public static long AtomicAnd(long at, long value) { return 0; }
            public static long AtomicAndRelaxed(long at, long value) { return 0; }
            public static long AtomicAndAcquire(long at, long value) { return 0; }
            public static long AtomicAndRelease(long at, long value) { return 0; }
            public static long AtomicAndSequential(long at, long value) { return 0; }
            public static long AtomicOr(long at, long value) { return 0; }
            public static long AtomicOrRelaxed(long at, long value) { return 0; }
            public static long AtomicOrAcquire(long at, long value) { return 0; }
            public static long AtomicOrRelease(long at, long value) { return 0; }
            public static long AtomicOrSequential(long at, long value) { return 0; }
            public static long AtomicXor(long at, long value) { return 0; }
            public static long AtomicXorRelaxed(long at, long value) { return 0; }
            public static long AtomicXorAcquire(long at, long value) { return 0; }
            public static long AtomicXorRelease(long at, long value) { return 0; }
            public static long AtomicXorSequential(long at, long value) { return 0; }
            public static long AtomicSwap(long at, long value) { return 0; }
            public static long AtomicSwapRelaxed(long at, long value) { return 0; }
            public static long AtomicSwapAcquire(long at, long value) { return 0; }
            public static long AtomicSwapRelease(long at, long value) { return 0; }
            public static long AtomicSwapSequential(long at, long value) { return 0; }
            public static long AtomicMin(long at, long value) { return 0; }
            public static long AtomicMinRelaxed(long at, long value) { return 0; }
            public static long AtomicMinAcquire(long at, long value) { return 0; }
            public static long AtomicMinRelease(long at, long value) { return 0; }
            public static long AtomicMinSequential(long at, long value) { return 0; }
            public static long AtomicMax(long at, long value) { return 0; }
            public static long AtomicMaxRelaxed(long at, long value) { return 0; }
            public static long AtomicMaxAcquire(long at, long value) { return 0; }
            public static long AtomicMaxRelease(long at, long value) { return 0; }
            public static long AtomicMaxSequential(long at, long value) { return 0; }
            public static int AtomicClaim(long at, long eligible, int direction) { return 0; }

            // Golden unit-0x01 bulk-memory surface. These work on raw memory;
            // safe array and string operations remain in their libraries.
            public static long FirstSet(long bitmap, long bits) { return 0; }
            public static long PopCountBlock(long bitmap, long bits) { return 0; }
            public static long Checksum(long at, long bytes, int kind, long seed) { return 0; }
            public static void SwapBytes(long first, long second, long count) { }
            public static void ReverseBytes(long low, long high, long pairs) { }

            // Orders this processor's accesses against every other one's. Free
            // on a single card and the difference between working and working
            // most of the time on several.
            public static void Fence() { }
            public static void Drain() { }
            public static void Breakpoint(long detail) { }
            public static void SingleStep() { }
            public static long ReadIoByte(long address) { return 0; }
            public static long ReadIoHalf(long address) { return 0; }
            public static long ReadIoWord(long address) { return 0; }
            public static long ReadIo(long address) { return 0; }
            public static void WriteIoByte(long address, long value) { }
            public static void WriteIoHalf(long address, long value) { }
            public static void WriteIoWord(long address, long value) { }
            public static void WriteIo(long address, long value) { }

            // Atomically checks a coherent word, registers its CFC watch, and
            // parks while it still equals expect. Deadline is an absolute CFC
            // TIME_COUNT value in microseconds, or zero for no deadline. The
            // returned reason never replaces rechecking the watched word.
            public static long WaitEq(long at, long expect, long deadline) { return 0; }

            // Says this processor is spinning and getting nowhere, so the
            // machine may let somebody else make progress.
            public static void Pause() { }

            // The block of memory firmware gave THIS processor and no other.
            // Where anything per-processor has to live, because statics are
            // shared by definition.
            public static long Self() { return 0; }
            public static long FirmwareTable() { return 0; }
            public static long FirmwareTableBytes() { return 0; }
            public static long BootStackBase() { return 0; }
            public static long BootStackBytes() { return 0; }

            // Where a method IS, so it can be handed to something that will
            // call it later: firmware starting a processor, a handler installed
            // for an interrupt line, the entry point of a thread. Without this a
            // program written in this language can describe work but cannot hand
            // it to anything.
            //
            // The argument is a static method named and not called.
            public static long AddressOf(long method) { return 0; }
            public static long AddressOf(ref int value) { return 0; }

            // Calls one. The other half: an address is no use without a way to
            // go there, and the things that hand addresses back -- a service
            // table, a card's driver ROM -- are not methods this program
            // declared.
            public static long Call(long fn, long a, long b, long c) { return 0; }

            // Five arguments, for calling a routine somebody else designed --
            // a card driver takes slot, block, address, count and position.
            public static long Call5(long fn, long a, long b, long c, long d, long e) { return 0; }

            // Calls one native OPCF version-1 entry. The frame is exactly
            // 0x60 bytes, stackTop names the top of the caller-owned private
            // option-ROM stack, and countAt receives the callee's r2 result.
            public static long CallOption(long fn, long frame, long stackTop, long countAt) { return 0; }
            public static long Arena() { return 0; }
            public static long ArenaEnd() { return 0; }
            public static long StaticBase() { return 0; }
            public static long StaticTop() { return 0; }

            // Where THIS PROCESSOR takes memory from. A kernel has to be able
            // to write them, because the arena is a fact about the program
            // running and the register is a fact about the processor: move a
            // program to another processor without carrying its arena and its
            // next allocation is refused, on a machine with plenty of memory.
            public static void SetArena(long at) { }
            public static void SetArenaEnd(long at) { }

            // ---- the two stacks a program has ----------------------------
            //
            // A trap from user mode is taken on the KERNEL's stack, and the
            // program's own is put aside so it can be handed back. The machine
            // does the swapping; these are how a kernel takes part in it.
            //
            // SetKernelSp says where the next trap from user mode is to be
            // taken -- which is a fact about the program a processor is
            // running, so it is written on every switch. UserSp reads back the
            // stack the program was on, to be written down with the rest of
            // its registers; SetUserSp is how a kernel resuming a DIFFERENT
            // program says which stack that one should come back to.
            public static long UserSp() { return 0; }
            public static long StackPointer() { return 0; }
            public static void SetUserSp(long at) { }
            public static void SetKernelSp(long at) { }

            // The architectural current-context software pointer. The system
            // profile currently leaves it on the firmware-assigned processor
            // block so protected entry can find its kernel dispatch state.
            public static long Mine() { return 0; }
            public static void SetMine(long at) { }

            // ---- driving a card ------------------------------------------
            //
            // A command block is a little program for one device: a header, a
            // list of operations, and somewhere to put the answers. These two
            // hand one to a card, and the difference between them is the whole
            // of what asynchrony is on this machine.
            //
            // Io waits. The answer is in the register before the instruction
            // finishes, and nothing needs to be told about it afterwards.
            //
            // StartIo does not. It answers with what the card said AT THE TIME,
            // which for a card that takes time is "in flight" -- and the caller
            // is expected to go away and do something else. What brings it back
            // is the interrupt the card raises when it is really finished, and
            // the answer waits in the channel register until somebody reads it
            // with IoStatus, because the register StartIo named belongs to
            // whatever that code went on to do next.
            public static long Io(long selector, long block) { return 0; }
            public static long StartIo(long selector, long block) { return 0; }
            public static long IoStatus(long selector) { return 0; }

            // ---- the x86 I/O space and the privileged instructions ---------
            //
            // A PC's devices are not memory: they answer on a separate
            // sixteen-bit address space that only IN and OUT reach, and a
            // driver written in this language has to be able to say so. These
            // are instructions, not calls: each becomes the one instruction it
            // names, with no frame, no spill and nothing between it and the
            // device.
            //
            // A port number is an int because the space is 16 bits wide and
            // every port constant anyone writes is small; the value of an 8-
            // or 16-bit port is likewise an int, zero-extended on the way in
            // and truncated by the instruction on the way out.
            //
            // A target that has no I/O space refuses these at selection rather
            // than pretending: a driver is not portable and should not compile
            // as though it were.
            public static int PortIn8(int port) { return 0; }
            public static int PortIn16(int port) { return 0; }
            public static int PortIn32(int port) { return 0; }
            public static void PortOut8(int port, int value) { }
            public static void PortOut16(int port, int value) { }
            public static void PortOut32(int port, int value) { }

            // The block forms: `count` 16-bit words between the port and
            // memory at `address`, which is what an ATA sector transfer is and
            // the reason the instructions exist. One instruction moves the
            // whole sector; a loop around PortIn16 would be sixteen times the
            // code and several times the time.
            public static void PortInString16(int port, long address, int count) { }
            public static void PortOutString16(int port, long address, int count) { }

            // The interrupt flag and the halt. Cli/Sti bracket a critical
            // section a device interrupt must not enter; Hlt stops the
            // processor until one arrives, which is what an idle loop is.
            public static void Cli() { }
            public static void Sti() { }
            public static void Hlt() { }

            // The descriptor tables. Each takes the address of a six-byte
            // pseudo-descriptor -- a 16-bit limit then a 32-bit base -- that
            // the caller has already built in memory.
            public static void Lidt(long address) { }
            public static void Lgdt(long address) { }

            // The control registers, by number: CR0 holds protected mode and
            // paging, CR2 the faulting address, CR3 the page directory. The
            // number has to be a constant, because the instruction encodes it.
            public static long ReadCr(int n) { return 0; }
            public static void WriteCr(int n, long value) { }

            // Drop one page's cached translation, after changing its entry.
            public static void Invlpg(long address) { }

            // The one data selector into DS, ES, FS, GS and SS: what a
            // protected-mode entry does immediately after the far jump that
            // set CS.
            public static void LoadSegments(int dataSelector) { }

            // Stops this processor until an interrupt arrives.
            //
            // Not a spin. A processor here costs nothing and takes no time from
            // the ones that are working, which is the difference between a
            // program that waits and a program that is merely not finished.
            public static void Idle() { }

            // How many times this processor has actually stopped.
            //
            // Not the same as how many times a program decided to wait: Idle
            // returns immediately when something has already happened, so
            // counting the decisions counts intentions. This counts events, and
            // it is what a kernel uses to know how busy a processor really is.
            public static long Idles() { return 0; }

            // How many instructions this processor has retired. The only way a
            // program can measure itself, and readable by anybody for the same
            // reason RDTSC is: knowing how long your own loop took is not a
            // privilege.
            public static long Cycles() { return 0; }

            // Asks the kernel for something. The one door between a program and
            // the system it runs on: the number says which service, and what
            // comes back is that service's answer.
            //
            // A program in user mode can do nothing else that matters -- it
            // cannot touch a device, arm a clock, or reach the trap table -- so
            // everything it CAN do arrives through here.
            public static long Syscall(long number, long a, long b, long c) { return 0; }
            public static long Syscall(long number, long a, long b, long c, long d, long e, long f) { return 0; }

            // The GUI kernel's two doors, the same convention as Syscall with
            // another vector: GuiCall is a program's way into ring 1
            // (`int 0x81`), GuiService is ring 1's way down to the kernel
            // (`int 0x82`, which ring 3 may not use).
            public static long GuiCall(long number, long a, long b, long c) { return 0; }
            public static long GuiService(long number, long a, long b, long c) { return 0; }
            // The stack pointer as the program was entered, for a runtime that
            // needs to find what the loader left there: argc and argv.
            public static long EntryStack() { return 0; }

            // WHERE THE FRAME TABLE IS, and where this frame is, which is all
            // the runtime needs to turn a fault into the list of calls that
            // led to it. The table the code generator writes is described in
            // Lang/X86/FrameTable.cs; FramePointer is this function's own ebp,
            // and the chain of saved ones behind it is the stack of callers.
            public static long FrameTable() { return 0; }
            public static long FrameDirectory() { return 0; }
            public static void Rethrow(object exception) { }
            public static long FramePointer() { return 0; }

            // ---- the thread block ----------------------------------------
            //
            // One block of words per thread, holding what used to be one word
            // of .bss for the whole process: the exception handler chain's
            // head, the stack this thread is standing on, and its share of the
            // heap. The compiler and lib/rt/runtime.cor's class Tls agree on
            // the offsets; ThreadBlock is the address of the block itself.
            //
            // On Linux the operating system keeps that address in a GDT entry
            // and GS names it, so this is one load. With no operating system
            // there is one thread and one block, in .bss, and no GS is set up
            // at all.
            public static long ThreadBlock() { return 0; }
            // The block the program's first thread uses: static memory, so it
            // is there before anything that could allocate one.
            public static long MainThreadBlock() { return 0; }
            // Where ThreadBlock reads the block from when there is no segment
            // register to keep it in. A system library that has GS uses SetGs
            // instead, after making the descriptor.
            public static void SetThreadBlock(long block) { }
            public static void SetGs(long selector) { }
            // The bounds of the program's static data, for a collector that
            // scans statics as roots: the linker defines the symbols.
            // Whether a reference is a string, read from its descriptor: what
            // lets a container compare string keys by their text without the
            // language having a root object type to ask.
            // The address of the first element of an array or the first byte
            // of a string. The header in front of it is the target's business;
            // code that needs the raw bytes asks here instead of adding a number.
            public static long ArrayData(object array) { return 0; }
            // The address of an object's synchronisation word, for a monitor.
            public static long SyncWord(object o) { return 0; }
            public static bool IsString(object x) { return false; }
            // Whether the value is an object that can be ASKED whether it is
            // equal to another: not null, and not a number. KeyEquals and
            // KeyHash are that asking, as .NET's default comparer does it --
            // text for a string, the key's own Equals and GetHashCode for an
            // object that has them.
            public static bool IsObject(object x) { return false; }
            public static bool KeyEquals(object a, object b) { return false; }
            public static int KeyHash(object x) { return 0; }
            // The order of two such keys: text for strings, the key's own
            // CompareTo, a tuple element by element; null before anything.
            public static int KeyCompare(object a, object b) { return 0; }
            // The same reference as a string, once IsString has said it is one.
            // A reinterpretation, not a conversion: no bytes move.
            public static string AsString(object x) { return ""; }
            public static long DataStart() { return 0; }
            public static long DataEnd() { return 0; }

            // ---- what a handler is standing in the middle of ---------------
            //
            // These reach the CURRENT interrupt or trap frame, which is banked
            // per priority, so a handler reads what IT was given rather than
            // what the thing it interrupted was given.
            //
            // Elr is where the interrupted code will resume, and writing it is
            // how a handler decides otherwise: stepping over a faulting
            // instruction, or -- with a different register frame -- resuming an
            // entirely different program. That is a context switch.
            // Which processor this is, geographically. Firmware copied this
            // card-owned CFC fact into the current processor block; CpuId is
            // deliberately reserved for CPU-owned capability leaves.
            public static long CpuId() { return 0; }

            public static long Elr() { return 0; }
            public static void SetElr(long at) { }
            public static long Cause() { return 0; }
            public static long Detail() { return 0; }

            // What privilege reti hands back. Only the kernel can write it,
            // which is the whole reason authority cannot be taken.
            public static void SetSavedRing(long ring) { }

            // ---- translation ---------------------------------------------
            //
            // How much authority the running code has: 0 for the kernel, 1 for
            // a program. Readable by anybody, because a thread asking what it
            // is allowed to do is not a threat, and a library that adapts
            // rather than faulting has to be able to ask -- the allocator uses
            // it to tell "there is no kernel, look at the machine" apart from
            // "there is one, ask it".
            public static long Ring() { return 0; }

            // The priority this processor is running at, and setting it.
            //
            // Raising it is how kernel code shuts an interrupt out for a few
            // instructions, and there is exactly one reason to need that: a
            // lock taken by ordinary code AND by an interrupt handler on the
            // same processor. The handler interrupts the holder, waits for a
            // lock its own processor is holding, and the machine stops. Every
            // kernel that has ever existed has this rule.
            //
            // Privileged, like everything else that changes how the machine
            // dispatches. A program in user mode does not need it: nothing it
            // wrote runs in interrupt context.
            public static long Level() { return 0; }
            public static void SetLevel(long to) { }

            // What privilege reti will hand back to -- which, inside a handler,
            // is what the thing it interrupted was running as.
            //
            // A preempting scheduler asks this before it does anything: a
            // processor that interrupted USER code can be switched away from,
            // and one that interrupted the KERNEL cannot, because the kernel it
            // interrupted may be holding a lock and switching away from a
            // processor holding one means nothing can ever take it again.
            public static long SavedRing() { return 0; }

            // ---- the interval timer ---------------------------------------
            //
            // Arms this processor's own timer for `ticks` instructions, pulling
            // the line in the low bits of `config`; add 256 for periodic. It
            // answers with what was LEFT on the timer before it was armed,
            // which is how a scheduler finds out how much of a slice went
            // unused. Arming for no time is what stopping is.
            //
            // Per processor and never near the router, which is the whole
            // reason it is a mandatory unit: a scheduler running on any
            // processor can always interrupt the one it is on.
            // Where this process keeps the statics of the shared libraries it
            // uses. Swapped by the kernel on the way into a process, because
            // shared library code is one copy that every process runs and the
            // instruction reaching a library static has to reach a different
            // word depending on who is running.
            public static long LibBase() { return 0; }
            public static void SetLibBase(long at) { }

            public static long SetTimer(long ticks, long config) { return 0; }

            // One line of THIS processor's own interrupt controller: its
            // priority, its vector and whether it is enabled, packed as the
            // controller wants them. Answers with what the line said before.
            public static long Lpic(long line, long config) { return 0; }
            public static long LpicGet(long line) { return 0; }

            // Which units are seated in the socket this code is executing in.
            // The MMU is bit 6, and asking is the whole of how a kernel finds
            // out whether the processor it is on can protect anything -- a
            // machine may have some that can and some that cannot, and the
            // answer differs between one instruction and the next if the
            // scheduler has moved you.
            public static long Units() { return 0; }
            public static long ClockHz() { return 0; }

            // The page table this processor is translating through, and the
            // modes it translates in. Writing the root flushes every cached
            // translation, because they all came from the old one.
            //
            // Privileged to READ as well as to write, which nothing else here
            // is: everything else describes the processor a thread is already
            // on, and this says where the kernel's tables live.
            public static long PageTable() { return 0; }
            public static void SetPageTable(long root) { }
            public static long MmuCtl() { return 0; }
            public static void SetMmuCtl(long bits) { }

            // What the unit refused, and why. The address is the one that could
            // not be translated; the status carries the reason in its low byte
            // and what was attempted above it.
            public static long FaultAddr() { return 0; }
            public static long FaultStatus() { return 0; }

            // Drops a cached translation: one page by address, or all of them
            // with -1. A kernel that edits a table entry another processor may
            // have cached has to say so to that processor too -- nothing in
            // this hardware makes translation caches coherent, exactly as
            // nothing does on the machines this one is pretending to be.
            public static void TlbFlush(long at) { }
        }

        static class Math {
            public static long Control() { return 0; }
            public static void SetControl(long value) { }
            public static long Status() { return 0; }
            public static void ClearStatus(long mask) { }
            public static double Sqrt(double x) { return 0.0; }
            public static double Abs(double x) { return 0.0; }
            public static double Floor(double x) { return 0.0; }
            public static double Ceil(double x) { return 0.0; }
            public static double Round(double x) { return 0.0; }
            public static double Trunc(double x) { return 0.0; }
            public static double Min(double a, double b) { return 0.0; }
            public static double Max(double a, double b) { return 0.0; }
            public static double Fma(double a, double b, double c) { return 0.0; }
        }

        // Every Rev. 1.6 instruction is reachable through these primitives,
        // including optional register files that have no CORS-C# source type.
        // `word` must be a literal complete instruction encoding.  Values are
        // evaluated into r1 through r4, and r1 is returned after execution.
        // Named Sys and Math intrinsics remain the normal, type-safe surface.
        static class Machine {
            public static long Raw0(long word) { return 0; }
            public static long Raw1(long word, long r1) { return 0; }
            public static long Raw2(long word, long r1, long r2) { return 0; }
            public static long Raw3(long word, long r1, long r2, long r3) { return 0; }
            public static long Raw4(long word, long r1, long r2, long r3, long r4) { return 0; }
        }
        """;
}
