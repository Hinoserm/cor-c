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
    /// <summary>The packed operations' class (Lowering.PackedIntrinsic).</summary>
    public const string PackedType = "Packed";
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
    public const string FromWord = "FromWord";
    public const string CharsOf = "CharsOf";
    public const string Bits = "Bits";
    public const string FromBits = "FromBits";
    public const string SingleBits = "SingleBits";
    public const string FromSingleBits = "FromSingleBits";

    // The block primitives. These are what let String live in the standard
    // library as ordinary source rather than being emitted by the compiler
    // behind the author's back.
    public const string NewChars = "NewChars";
    public const string GetChar = "GetChar";
    public const string GetCharPair = "GetCharPair";
    public const string SetChar = "SetChar";
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
    public const string Cas32 = "Cas32";
    public const string AtomicAdd32 = "AtomicAdd32";
    public const string AtomicSwap32 = "AtomicSwap32";
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
    public const string CompilerFence = "CompilerFence";
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

    /// <summary>The template path of the class library's IEquatable&lt;T&gt;, declared at its top level: what its family is hashed from (CanonShape.FamilyOf).</summary>
    public const string EquatableTemplate = "IEquatable";
    public const string FromIntMethod = "FromInt";
    public const string FromUIntMethod = "FromUInt";
    public const string FromBoolMethod = "FromBool";
    public const string FromDoubleMethod = "FromDouble";
    public const string FromSingleMethod = "FromSingle";
    public const string TypeNameMethod = "TypeName";
    /// <summary>String's method answering a type's namespace from its full name (Type.Namespace).</summary>
    public const string TypeNamespaceMethod = "TypeNamespace";
    /// <summary>A type's full name as Type.ToString writes it: a generic's arguments without their assemblies.</summary>
    public const string TypeTextMethod = "TypeText";
    /// <summary>String's method spelling out a descriptor's name, its generic arguments' assemblies written short in the image (Lowering.DescriptorName).</summary>
    public const string TypeFullNameMethod = "TypeFullName";

    public const string Source = """
        public static class Sys {
            public static void PrintInt(long value) { }
            public static void Print(string text) { }
            // Bytes as they are, to the same output: a "text"u8 literal or a
            // buffer, with nothing encoded -- what .NET's Stream.Write of a
            // ReadOnlySpan<byte> is to its console.
            public static void Print(ReadOnlySpan<byte> bytes) { }
            public static void PrintChar(int ch) { }
            public static void PrintHex(long value) { }
            public static void PrintLine() { }
            public static void Exit(int code) { }
            public static long Word(object x) { return 0; }
            // WHERE A REFERENCE POINTS: an object's address, a nuint as every
            // address is. Word is its bits as a long, which for a double held
            // as a value is 64 of them -- more than a 32-bit nuint holds.
            public static nuint ObjectAddress(object o) { return 0; }
            // And back: the reference an address holds, as FromWord is Word's.
            public static object? ObjectAt(nuint address) { return null; }
            // The other way: a word read out of memory, taken as the reference
            // it holds -- what an atomic swap of a reference field hands back,
            // a block Runtime.Alloc made. An address, so a nuint, as every
            // address intrinsic takes (ReadAddress, ArrayData); ObjectAt by
            // its other name.
            public static object? FromWord(nuint word) { return null; }
            // A STRING'S UNITS AS THE char[] THEY ARE LAID OUT AS: the same
            // object -- header, count, UTF-16 units -- typed so a span can
            // view it (string.AsSpan) without a copy. Read only once anybody
            // may hold the string: the one writer is String.Create, which
            // hands a string still being made to the action that fills it.
            public static char[] CharsOf(string s) { return null!; }

            // THE BITS OF A REAL, and back again. A double lives in the other
            // register bank, so reinterpreting one is a MOVE between banks --
            // which is a machine instruction and cannot be spelt any other way.
            // System.BitConverter is written on top of these two.
            public static long Bits(double x) { return 0; }
            public static double FromBits(long x) { return 0; }
            public static int SingleBits(float x) { return 0; }
            public static float FromSingleBits(int x) { return 0; }

            // A STRING IS UTF-16 CODE UNITS, as C#'s is: a length word counting
            // them and that many two-byte units after it. These three are the
            // whole of what the machine offers for building one, and String is
            // written on top of them. What a string MEANS as bytes -- a path, a
            // line on a terminal -- is an encoding, and System.Text makes it.
            public static string NewChars(int length) { return ""; }
            public static char GetChar(string s, int at) { return (char)0; }
            // Two units at once, `at` and `at + 1`, as one 32-bit load: the
            // string routines compare a pair at a time and look at single
            // units only where a pair differs or one is left over.
            public static int GetCharPair(string s, int at) { return 0; }
            public static void SetChar(string s, int at, int value) { }
            // AND WITH A MACHINE-WORD CURSOR. An index is an int in C# because
            // an array's length is, and String keeps to that; these are the
            // machine underneath, where an address is sixty-four bits and a
            // walk over a string counts in the width the registers are. System
            // .Array does the same thing for the same reason -- Copy has both
            // an int and a long overload -- and without it every loop in a
            // program that counts in words casts at each step, or keeps a
            // second cursor beside the first.
            public static char GetChar(string s, long at) { return (char)0; }
            public static void SetChar(string s, long at, int value) { }
            // A string's cursors and count are in chars; a byte array's, bytes.
            public static void Copy(string dst, int dstAt, string src, int srcAt, int count) { }
            public static void Copy(byte[] dst, int dstAt, byte[] src, int srcAt, int count) { }
            // Like Copy, but the two source ranges must not overlap.  It gives
            // the portable runtime vector dispatch permission to stream forward
            // through V512 chunks; callers that might overlap retain Copy's
            // memmove semantics.
            public static void CopyNoOverlap(string dst, int dstAt, string src, int srcAt, int count) { }
            public static void CopyNoOverlap(byte[] dst, int dstAt, byte[] src, int srcAt, int count) { }
            public static int CompareBytes(byte[] a, int aAt, byte[] b, int bAt, int count) { return 0; }

            // Unit 0x0a. These are deliberately low-level: their cursor and
            // count arguments name an exact span, while String supplies the
            // friendly checked operations and the no-unit fallback.
            public static int StringCompare(string a, int aAt, string b, int bAt, int count) { return 0; }
            public static bool StringEqual(string a, int aAt, string b, int bAt, int count) { return false; }
            // Whether `count` bytes at `a` and at `b` are the same (Runtime.BytesEqual).
            public static bool BytesEqual(nuint a, nuint b, nuint count) { return false; }
            public static long StringHash(string s, int at, int count, long seed) { return 0; }
            public static int StringCopy(string dst, int dstAt, string src, int srcAt, int count) { return 0; }
            public static int StringFill(string dst, int dstAt, int value, int count) { return 0; }
            public static int StringFind(string s, int at, int value, int count) { return 0; }
            public static int StringFindReverse(string s, int at, int value, int count) { return 0; }
            public static int StringSearch(string s, int at, string what, int whatAt, int count) { return 0; }
            public static int StringLower(string s, int at, int count) { return 0; }
            public static int StringUpper(string s, int at, int count) { return 0; }
            public static long StringNumber(string s, int at, int count) { return 0; }
            public static int StringFormat(string dst, int at, long value, int capacity) { return 0; }

            // Raw memory, for the one program that has to manage it. AN
            // ADDRESS IS A nuint, here and everywhere in Sys: the target's
            // word, unsigned -- one register on i386, where a long is two, and
            // never negative, so a kernel's half above 0x80000000 compares as
            // it is. A length in memory is one too.
            public static void MemoryCopy(nuint dst, nuint src, nuint count) { }
            public static nuint Allocate(nuint byteCount) { return 0; }
            public static long ProbeRange(nuint address, nuint byteCount, long permissions, out long status) { status = 0; return 0; }
            public static long PageProbe(nuint address, out long status) { status = 0; return 0; }
            public static long PageWalk(nuint address, out long status) { status = 0; return 0; }
            public static long PageNext(long cursor, out long leafStatus) { leafStatus = 0; return 0; }
            public static long PageMap(nuint address, long pte, long options, out long status) { status = 0; return 0; }
            public static long PageUnmap(nuint address, out long status) { status = 0; return 0; }
            public static void PageCopy(ref nuint destination, ref nuint source, ref long pages, ref long status) { }
            public static void CrossAddressCopy(ref nuint primary, ref nuint secondary, ref nuint bytes, ref long status) { }
            public static void TraceRead(out nuint source, out nuint destination, out long kind, out long valid) { source = 0; destination = 0; kind = 0; valid = 0; }
            // `value` is the byte every one of `count` is set to.
            public static void MemorySet(nuint dst, int value, nuint count) { }
            // A packed block's state taken and given back -- its kind (4,
            // the compiler's own around a body, Parser.ImplicitKind), its
            // number in its method, and its variant's features (PackedOps'
            // bits) -- and which of its variants runs: the first whose
            // features the processor has, asked the first time and kept
            // (which the parser writes these for). `features`
            // is the variants' masks, a byte each, the first lowest.
            public static void PackedEnter(int kind, int block, int features) { }
            public static void PackedLeave(int kind, int block, int features) { }
            public static int PackedChoose(int block, ulong features, int count) { return 0; }
            // The same, the last variant -- no feature -- kept whatever --cpu's
            // floor drops (`keepLast` 1): what a cutoff chooses for little
            // work (Parser.PackedCutoff, PackedSmall).
            public static int PackedChoose(int block, ulong features, int count, int keepLast) { return 0; }
            // Whether `work` -- what a packed method's loop runs over, `step`
            // a turn -- is too little to win back the save of the thread's
            // state (Parser.PackedCutoff): a constant false where nothing is
            // saved for the method's own packed operations.
            public static bool PackedSmall(long work, int step) { return false; }
            // `count` bytes from `src` to `dst`, overlapping or not (memmove)
            // where `oneBlock` -- both in one array -- and forward where not:
            // for bytes that hold no reference (Span.Move's elements).
            public static void MemoryMove(nuint dst, nuint src, nuint count, bool oneBlock) { }
            // The same for bytes that are numbers never an address (Instr.
            // Number): characters, the 32-bit integers and smaller, floats.
            public static void NumbersMove(nuint dst, nuint src, nuint count, bool oneBlock) { }
            // Whether the elements of an array of T hold references, as the
            // collector reads the array, and the bytes from one to the next:
            // constants of each instantiation, the array itself not read (a
            // shared copy's T may hold one). RuntimeHelpers.
            // IsReferenceOrContainsReferences is the first.
            public static bool ElementsHoldReferences<T>(T[]? array) { return true; }
            public static bool ElementsAreNumbers<T>(T[]? array) { return false; }
            public static int ElementStride<T>(T[]? array) { return 0; }

            // Mandatory core arithmetic and bit operations. MultiplyHigh is
            // the upper half of the full 128-bit product; BitFieldInsert keeps
            // every bit outside its literal position/length field unchanged.
            public static long MultiplyHigh(long left, long right) { return 0; }
            public static long MultiplyHighUnsigned(long left, long right) { return 0; }
            public static long ReverseBits(long value) { return 0; }
            public static long PopulationCount(long value) { return 0; }
            public static long LeadingZeroCount(long value) { return 0; }
            public static long TrailingZeroCount(long value) { return 0; }
            public static long ByteSwap(long value) { return 0; }
            public static long RotateLeft(long value, long count) { return 0; }
            public static long RotateRight(long value, long count) { return 0; }
            public static ulong MinUnsigned(ulong left, ulong right) { return 0; }
            public static ulong MaxUnsigned(ulong left, ulong right) { return 0; }
            public static long Max(long left, long right) { return 0; }
            public static long SetBit(long value, long bit) { return 0; }
            public static long ClearBit(long value, long bit) { return 0; }
            public static bool TestBit(long value, long bit) { return false; }
            public static long LowBitMask(int bits) { return 0; }
            public static long CarrylessMultiply(long left, long right, out long high) { high = 0; return 0; }
            public static long GaloisFieldMultiply(long left, long right) { return 0; }
            public static long MultiplyAccumulate(long left, long right, long carry, out long high) { high = 0; return 0; }
            public static bool TryDesEncrypt(long block, int keyRegisterBase, out long result) { result = 0; return false; }
            public static bool TryDesDecrypt(long block, int keyRegisterBase, out long result) { result = 0; return false; }
            public static bool TryAesEncryptBlock(nuint state, nuint roundKeys, int rounds) { return false; }
            public static bool TryAesDecryptBlock(nuint state, nuint roundKeys, int rounds) { return false; }
            public static bool TryAesKey128Next(nuint output, nuint previous, int roundConstant) { return false; }
            public static bool TryAesKey256Next(nuint output, nuint earlier, nuint recent, int control) { return false; }
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
            public static bool TryMoveFromDecimal(int register, out long low, out long high) { low = 0; high = 0; return false; }
            public static bool TryMoveFromVectorLane(int register, long lane, int vectorBits, int elementBits, out long value) { value = 0; return false; }
            public static bool TryReadCompressionRegister(int register, out long value) { value = 0; return false; }
            public static bool TryDecimalToInteger(int register, out long value) { value = 0; return false; }
            public static bool TryDecimalCompare(int left, int right, out long result) { result = 0; return false; }
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
            public static bool TryVectorReduce(int source, long mask, long reduction, int vectorBits, int elementBits, out long value) { value = 0; return false; }

            // MEMORY BY WIDTH: a byte, a half word, a word and two, because
            // that is what a structure written down by somebody else is made
            // of. Reading them out of a word load with shifts and masks works
            // and turns every field into arithmetic that can be wrong without
            // saying so. Each answers exactly the width it reads, unsigned --
            // a mode, a link count, a block number, a length counts up from
            // zero, and a field whose top bit is set is not negative -- and is
            // a device's read or write: never answered by an earlier one.
            // Read64 and Write64 are two words on i386.
            public static byte Read8(nuint at) { return 0; }
            public static ushort Read16(nuint at) { return 0; }
            public static uint Read32(nuint at) { return 0; }
            public static ulong Read64(nuint at) { return 0; }
            public static void Write8(nuint at, byte value) { }
            public static void Write16(nuint at, ushort value) { }
            public static void Write32(nuint at, uint value) { }
            public static void Write64(nuint at, ulong value) { }
            // A POINTER-SIZED FIELD: an address kept in memory, or a length,
            // size or header the layout makes as wide as an address -- 4
            // bytes on i386, 8 in long mode, as an ordinary load and store,
            // which the optimiser may answer from an earlier one. NEVER the
            // default for a number: a field whose width is its own (a long, an
            // int, a DWORD, a 64-bit offset) is read at that width, by
            // Read8/16/32/64, whatever the machine.
            public static nuint ReadAddress(nuint at) { return 0; }
            public static void WriteAddress(nuint at, nuint value) { }
            // The low 32 bits, said: the one narrowing of an address or a
            // physical address to a uint.
            public static uint Low32(nuint value) { return 0; }
            public static uint Low32(ulong value) { return 0; }
            // A USE OF A REFERENCE, and nothing else: the collector counts the
            // object reachable up to here. What GC.KeepAlive is, for an object
            // whose address was handed somewhere the collector does not look
            // -- a request block, a system call -- and whose last use in the
            // code is therefore before the callee is done with it.
            public static void KeepAlive(object? value) { }

            public static nuint Stack() { return 0; }

            // The whole of what makes anything written in this language safe
            // for more than one processor. Without these a read, a change and a
            // write are three instructions with room between them, and two
            // processors get in each other's gaps.
            //
            // Cas answers with what was THERE, whether or not it matched, so a
            // caller learns why it failed rather than only that it did.
            // A WORD AT A TIME, its value a nuint: for a field that is an address
            // or a mask a pointer wide, and for nothing else. A field of its own
            // width -- a count, a state, a lock word -- is an int and uses the
            // 32-bit forms below; a 64-bit one has no atomic here.
            public static nuint Cas(nuint at, nuint expect, nuint value) { return 0; }
            public static nuint CasRelaxed(nuint at, nuint expect, nuint value) { return 0; }
            public static nuint CasAcquire(nuint at, nuint expect, nuint value) { return 0; }
            public static nuint CasRelease(nuint at, nuint expect, nuint value) { return 0; }
            public static nuint CasSequential(nuint at, nuint expect, nuint value) { return 0; }
            public static bool CasPair(nuint at, nuint expectedLow, nuint expectedHigh, nuint desiredLow, nuint desiredHigh, out nuint observedLow) { observedLow = 0; return false; }
            public static nuint AtomicAdd(nuint at, nuint value) { return 0; }
            public static nuint AtomicAddRelaxed(nuint at, nuint value) { return 0; }
            public static nuint AtomicAddAcquire(nuint at, nuint value) { return 0; }
            public static nuint AtomicAddRelease(nuint at, nuint value) { return 0; }
            public static nuint AtomicAddSequential(nuint at, nuint value) { return 0; }
            public static nuint AtomicAnd(nuint at, nuint value) { return 0; }
            public static nuint AtomicAndRelaxed(nuint at, nuint value) { return 0; }
            public static nuint AtomicAndAcquire(nuint at, nuint value) { return 0; }
            public static nuint AtomicAndRelease(nuint at, nuint value) { return 0; }
            public static nuint AtomicAndSequential(nuint at, nuint value) { return 0; }
            public static nuint AtomicOr(nuint at, nuint value) { return 0; }
            public static nuint AtomicOrRelaxed(nuint at, nuint value) { return 0; }
            public static nuint AtomicOrAcquire(nuint at, nuint value) { return 0; }
            public static nuint AtomicOrRelease(nuint at, nuint value) { return 0; }
            public static nuint AtomicOrSequential(nuint at, nuint value) { return 0; }
            public static nuint AtomicXor(nuint at, nuint value) { return 0; }
            public static nuint AtomicXorRelaxed(nuint at, nuint value) { return 0; }
            public static nuint AtomicXorAcquire(nuint at, nuint value) { return 0; }
            public static nuint AtomicXorRelease(nuint at, nuint value) { return 0; }
            public static nuint AtomicXorSequential(nuint at, nuint value) { return 0; }
            public static nuint AtomicSwap(nuint at, nuint value) { return 0; }
            // THE 32-BIT FORMS, for an int: the word forms work on a machine
            // word, which in long mode is the int and the four bytes after it
            // -- a neighbouring field changed, and a compare that fails on
            // what the neighbour holds. These touch four bytes on either
            // target (on i386 they are the word forms). Named, not overloads
            // by the value's type: an overload would make `Cas(p, 0, 1)` a
            // 32-bit compare of a word field in long mode.
            public static int Cas32(nuint at, int expect, int value) { return 0; }
            public static int AtomicAdd32(nuint at, int value) { return 0; }
            public static int AtomicSwap32(nuint at, int value) { return 0; }
            // A reference exchanged at `at` with the write barrier and the card
            // mark every reference store takes (Interlocked's reference
            // exchanges): what was there; and the same only where it holds
            // `expected`, answering what it held.
            public static nuint ExchangeReference(nuint at, nuint value) { return 0; }
            public static nuint CompareExchangeReference(nuint at, nuint expected, nuint value) { return 0; }

            // Golden unit-0x01 bulk-memory surface. These work on raw memory;
            // safe array and string operations remain in their libraries.
            // FirstSet answers the bit's index, or -1 where none is set.
            public static int FirstSet(nuint bitmap, nuint bits) { return 0; }
            public static nuint PopCountBlock(nuint bitmap, nuint bits) { return 0; }
            public static long Checksum(nuint at, nuint bytes, int kind, long seed) { return 0; }
            public static void SwapBytes(nuint first, nuint second, nuint count) { }
            public static void ReverseBytes(nuint low, nuint high, nuint pairs) { }

            // Orders this processor's accesses against every other one's. Free
            // on a single card and the difference between working and working
            // most of the time on several.
            public static void Fence() { }
            // Orders this processor's accesses as the optimiser sees them and
            // no more: nothing moves or is merged across it, and on a machine
            // whose loads and stores are already ordered (x86's) no
            // instruction is made. A read's acquire, a write's release.
            public static void CompilerFence() { }
            public static void Breakpoint(long detail) { }

            // Says this processor is spinning and getting nowhere, so the
            // machine may let somebody else make progress.
            public static void Pause() { }

            // Where a method IS, so it can be handed to something that will
            // call it later: firmware starting a processor, a handler installed
            // for an interrupt line, the entry point of a thread. Without this a
            // program written in this language can describe work but cannot hand
            // it to anything.
            //
            // The argument is a static method named and not called.
            public static nuint AddressOf(long method) { return 0; }
            public static nuint AddressOf(ref int value) { return 0; }
            // And of any variable passed by reference: what System.Threading.
            // Interlocked is built on, since a `ref` parameter is already the
            // address of the caller's variable and an atomic needs exactly that.
            public static nuint AddressOf(ref long value) { return 0; }
            public static nuint AddressOf<T>(ref T value) { return 0; }

            // A reference taken as T with no test: .NET's Unsafe.As, for code
            // that has proved what the object is where a cast's test could not
            // say it -- a shared generic copy, whose T is any reference's word,
            // tested a delegate against its canonical type and refused it.
            public static T As<T>(object? value) { return default!; }

            // Calls one: a method of this program's, by its address (a hook,
            // a chore, a callback), with this language's convention. Its
            // arguments are words, a nuint each -- one register or one push on
            // i386, where a long was two -- and so are the arguments of every
            // method it reaches, which declares (nuint, nuint, nuint) and
            // answers a nuint; the call is indirect, and nothing checks the callee.
            // CallLong reaches one that answers a long, a figure no word holds
            // on i386 (a clock's nanoseconds).
            public static nuint Call(nuint fn, nuint a, nuint b, nuint c) { return 0; }
            public static long CallLong(nuint fn, nuint a, nuint b, nuint c) { return 0; }
            // A C function by its address, with C's rules for the call -- a
            // word an argument, the stack sixteen-aligned on i386, the vector
            // count in AL on x86-64. What the runtime uses for what it finds
            // in the C library at run time -- pthread_create, exit -- and
            // what a kernel uses for its own assembly routines. Its arguments
            // and its answer are words, a nuint each. Code with this
            // language's convention is reached by Call instead.
            public static nuint CallNative(nuint fn) { return 0; }
            public static nuint CallNative(nuint fn, nuint a) { return 0; }
            public static nuint CallNative(nuint fn, nuint a, nuint b) { return 0; }
            public static nuint CallNative(nuint fn, nuint a, nuint b, nuint c) { return 0; }
            public static nuint CallNative(nuint fn, nuint a, nuint b, nuint c, nuint d) { return 0; }
            public static nuint CallNative(nuint fn, nuint a, nuint b, nuint c, nuint d, nuint e) { return 0; }
            public static nuint CallNative(nuint fn, nuint a, nuint b, nuint c, nuint d, nuint e, nuint f) { return 0; }
            public static nuint CallNative(nuint fn, nuint a, nuint b, nuint c, nuint d, nuint e, nuint f, nuint g) { return 0; }
            // The same for a routine that uses the x87, MMX or SSE, which
            // nothing in a separately assembled object shows the compiler:
            // freestanding, its caller keeps the state around it (Binder.
            // PackedStateOf). A plain CallNative runs on the thread's own
            // state -- given back around it where its caller keeps one, and
            // around a direct call of a method of this unit that reaches one
            // -- as a BIOS thunk, a CPU's bring-up or a return to user code
            // must.
            public static nuint CallNativeFloat(nuint fn) { return 0; }
            public static nuint CallNativeFloat(nuint fn, nuint a) { return 0; }
            public static nuint CallNativeFloat(nuint fn, nuint a, nuint b) { return 0; }
            public static nuint CallNativeFloat(nuint fn, nuint a, nuint b, nuint c) { return 0; }
            public static nuint CallNativeFloat(nuint fn, nuint a, nuint b, nuint c, nuint d) { return 0; }
            public static nuint CallNativeFloat(nuint fn, nuint a, nuint b, nuint c, nuint d, nuint e) { return 0; }
            public static nuint CallNativeFloat(nuint fn, nuint a, nuint b, nuint c, nuint d, nuint e, nuint f) { return 0; }
            public static nuint CallNativeFloat(nuint fn, nuint a, nuint b, nuint c, nuint d, nuint e, nuint f, nuint g) { return 0; }

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
            // every port constant anyone writes is small; the value of a port
            // is the width of the instruction -- a byte, a ushort, a uint --
            // zero-extended on the way in, as the register is, and written
            // whole on the way out.
            //
            // A target that has no I/O space refuses these at selection rather
            // than pretending: a driver is not portable and should not compile
            // as though it were.
            public static byte PortIn8(int port) { return 0; }
            public static ushort PortIn16(int port) { return 0; }
            public static uint PortIn32(int port) { return 0; }
            public static void PortOut8(int port, byte value) { }
            public static void PortOut16(int port, ushort value) { }
            public static void PortOut32(int port, uint value) { }

            // The block forms: `count` 16-bit words between the port and
            // memory at `address`, which is what an ATA sector transfer is and
            // the reason the instructions exist. One instruction moves the
            // whole sector; a loop around PortIn16 would be sixteen times the
            // code and several times the time.
            public static void PortInString16(int port, nuint address, int count) { }
            public static void PortOutString16(int port, nuint address, int count) { }

            // The interrupt flag and the halt. Cli/Sti bracket a critical
            // section a device interrupt must not enter; Hlt stops the
            // processor until one arrives, which is what an idle loop is.
            public static void Cli() { }
            public static void Sti() { }
            public static void Hlt() { }

            // The x87 told to round to a double's 53 bits, as the image's
            // entry tells it (MachineIntrinsics.DoublePrecision): for a
            // processor or a thread a kernel starts itself, whose FPU begins
            // at the extended format's 64 bits after a reset or FNINIT and
            // rounds every double twice. Nothing in long mode, whose doubles
            // are SSE2's, nor without an FPU.
            public static void DoublePrecision() { }
            // Whether Sys.Syscall is the kernel's KernelFiles.Syscall
            // (--kernel-syscalls): a call the kernel parks as it sees fit,
            // on a thread block that is a processor's, which no runtime
            // bracket may mark stopped (Platform.Blocking).
            public static bool KernelSyscalls() { return false; }

            // The descriptor tables. Each takes the address of a six-byte
            // pseudo-descriptor -- a 16-bit limit then a 32-bit base -- that
            // the caller has already built in memory.
            public static void Lidt(nuint address) { }
            public static void Lgdt(nuint address) { }

            // The control registers, by number: CR0 holds protected mode and
            // paging, CR2 the faulting address, CR3 the page directory. The
            // number has to be a constant, because the instruction encodes it.
            public static nuint ReadCr(int n) { return 0; }
            public static void WriteCr(int n, nuint value) { }

            // Drop one page's cached translation, after changing its entry.
            public static void Invlpg(nuint address) { }

            // The one data selector into DS, ES, FS, GS and SS: what a
            // protected-mode entry does immediately after the far jump that
            // set CS.
            public static void LoadSegments(int dataSelector) { }
            // CS, which no mov can load: a far return to the next instruction.
            public static void LoadCodeSegment(int codeSelector) { }
            // The task register (ltr): the TSS a privilege change finds its stack in.
            public static void LoadTaskRegister(int selector) { }
            // rdmsr / wrmsr: a model-specific register, all 64 bits of it --
            // on x86-64 how a kernel reaches EFER, the GS base and the
            // syscall entry.
            public static long ReadMsr(int msr) { return 0; }
            public static void WriteMsr(int msr, long value) { }
            // cpuid: EAX, EBX, ECX and EDX of the leaf, as four ints at `into`.
            public static void Cpuid(int leaf, int subleaf, nuint into) { }
            // rdtsc: the time-stamp counter.
            public static long ReadTsc() { return 0; }
            // bsf / bsr: the index of the lowest or highest set bit of a value
            // that is not zero (undefined for zero: ask first).
            public static int BitScanForward(uint value) { return 0; }
            public static int BitScanReverse(uint value) { return 0; }
            // 1 where the processor has CPUID (EFLAGS.ID can be flipped), else 0.
            public static int CpuidPresent() { return 0; }
            // cpuid of a leaf (subleaf 0), its EAX or its EDX: nothing written
            // to memory, so a caller asks with no frame or heap to put the four
            // registers in (Processor.Ask). i386 only.
            public static int CpuidEax(int leaf) { return 0; }
            public static int CpuidEdx(int leaf) { return 0; }
            // THE MMX REGISTERS' BULK AND SCANNING INSTRUCTIONS, i386 only, for
            // the hosted runtime's routines that ask Processor first (each is
            // an instruction the --cpu profile may not have): prefetchnta and
            // 3DNow!'s prefetch; a cache line copied, or filled with the eight
            // bytes at `pattern`, by movntq; sfence and emms after them; four
            // UTF-16 units at `at` against the four at `pattern` (a byte 0xFF
            // each that is equal), eight by pmovmskb (a bit each); eight bytes
            // alike (-1) or not; emms, or 3DNow!'s femms.
            public static void StreamPrefetch(nuint at) { }
            public static void StreamPrefetchLine(nuint at) { }
            public static void StreamCopy64(nuint to, nuint from) { }
            public static void StreamFill64(nuint to, nuint pattern) { }
            public static void StreamEnd() { }
            public static int PackedFindChar4(nuint at, nuint pattern) { return 0; }
            public static int PackedFindChar8(nuint at, nuint pattern) { return 0; }
            public static int PackedEqual8(nuint a, nuint b) { return 0; }
            public static void PackedEnd() { }
            public static void PackedEndFast() { }
            // A 64-bit compare-exchange at `at`: lock cmpxchg8b on i386, which
            // needs CPUID's CX8 bit (Processor.Cas64); answers what was there.
            public static long Cas64(nuint at, long expect, long value) { return 0; }
            // pushfd; pop (pushfq on x86-64): the flags register, which a
            // kernel asks for the interrupt flag (bit 9) before it turns
            // interrupts off and on again.
            public static nuint ReadFlags() { return 0; }
            // The code selector: its low two bits are the privilege level
            // the code runs at, which with EFLAGS.IOPL says whether it may
            // turn interrupts off (Platform.AtomicsEnter).
            public static int ReadCodeSegment() { return 0; }
            // swapgs: the kernel's GS base for the user's, on x86-64.
            public static void SwapGs() { }

            // Asks the kernel for something. The one door between a program and
            // the system it runs on: the number says which service, and what
            // comes back is that service's answer.
            //
            // A program in user mode can do nothing else that matters -- it
            // cannot touch a device, arm a clock, or reach the trap table -- so
            // everything it CAN do arrives through here.
            //
            // As Linux's: the registers are SyscallWord wide (--syscall-abi),
            // 32 bits for the i386 interface and 64 for x86-64's, and what
            // comes back is the raw register (Err says how to read it).
            public static SyscallWord Syscall(int number, SyscallWord a, SyscallWord b, SyscallWord c) { return 0; }
            public static SyscallWord Syscall(int number, SyscallWord a, SyscallWord b, SyscallWord c, SyscallWord d, SyscallWord e, SyscallWord f) { return 0; }

            // The GUI module's door, the same convention as Syscall with
            // another vector: a program's way into the GUI kernel (`int 0x81`).
            public static SyscallWord GuiCall(int number, SyscallWord a, SyscallWord b, SyscallWord c) { return 0; }
            // A Windows system call (`int 0x2E`): EAX the call's number and EDX
            // the address of its arguments, as ntdll's stubs make it; the
            // answer is its NTSTATUS. For a CORSAC program attached to the NT
            // kernel -- csrss -- which serves Windows programs from outside them.
            public static SyscallWord NtCall(int number, SyscallWord arguments) { return 0; }
            // The stack pointer as the program was entered, for a runtime that
            // needs to find what the loader left there: argc and argv.
            public static nuint EntryStack() { return 0; }

            // WHERE THE FRAME TABLE IS, and where this frame is, which is all
            // the runtime needs to turn a fault into the list of calls that
            // led to it. The table the code generator writes is described in
            // Lang/X86/FrameTable.cs; FramePointer is this function's own ebp,
            // and the chain of saved ones behind it is the stack of callers.
            public static nuint FrameTable() { return 0; }
            public static nuint FrameDirectory() { return 0; }
            public static void Rethrow(object exception) { }
            // READ, NEVER WRITTEN THROUGH, above the return address: a function's
            // argument words are where the register allocator keeps the values
            // loaded from them (its home slots), which a write there would
            // change under them.
            public static nuint FramePointer() { return 0; }
            // EVERY CALLEE-SAVED REGISTER PUSHED BY THIS FUNCTION'S PROLOGUE:
            // they count as written here, and nothing is emitted. What a
            // safepoint needs, so that a reference its caller left in one is
            // on the stack below the caller's frame (GcRoots, GcThreads.Spill).
            public static void SaveRegisters() { }

            // ---- the thread block ----------------------------------------
            //
            // One block of words per thread, holding what used to be one word
            // of .bss for the whole process: the exception handler chain's
            // head, the stack this thread is standing on, and its share of the
            // heap. The compiler and runtime/src/core/runtime.cor's class Tls agree on
            // the offsets; ThreadBlock is the address of the block itself.
            //
            // On Linux the operating system keeps that address in a GDT entry
            // and GS names it, so this is one load. With no operating system
            // there is one thread and one block, in .bss, and no GS is set up
            // at all.
            public static nuint ThreadBlock() { return 0; }
            // The block the program's first thread uses: static memory, so it
            // is there before anything that could allocate one.
            public static nuint MainThreadBlock() { return 0; }
            // Where ThreadBlock reads the block from when there is no segment
            // register to keep it in. A system library that has GS uses SetGs
            // instead, after making the descriptor.
            public static void SetThreadBlock(nuint block) { }
            // Windows: where ThreadBlock and SetThreadBlock find this thread's
            // block -- the word of FS to start from, and what to add to it to
            // reach the runtime's TLS slot (Selector.TlsBaseSymbol).
            public static void SetThreadBlockSlot(int block, int offset) { }
            public static void SetGs(int selector) { }
            // A word of this processor's area at `offset` from where GS is
            // based, read or written in one instruction (a kernel's per-CPU
            // data: Linux's this_cpu_read and this_cpu_write).
            public static nuint PerCpuRead(int offset) { return 0; }
            public static void PerCpuWrite(int offset, nuint value) { }
            public static int GetGs() { return 0; }
            // The bounds of the program's static data, for a collector that
            // scans statics as roots: the linker defines the symbols.
            // Whether a reference is a string, read from its descriptor: what
            // lets a container compare string keys by their text without the
            // language having a root object type to ask.
            // The address of the first element of an array. The header in front
            // of it is the target's business; code that needs the raw bytes asks
            // here instead of adding a number. NOT OF A STRING: its elements are
            // UTF-16 code units, and code that meant its bytes means an encoding.
            public static nuint ArrayData(object array) { return 0; }
            // The address of a string's first UTF-16 code unit, for code that
            // wants exactly those -- a Win32 W function's LPCWSTR.
            public static nuint StringData(string s) { return 0; }
            // The address of an array's first element, the array kept alive
            // until the function returns: what `T* p = stackalloc T[n]` is,
            // whose memory C# keeps for the whole call, not only p's block.
            public static nuint FrameData(object array) { return 0; }
            // The address of an object's synchronisation word, for a monitor.
            public static nuint SyncWord(object o) { return 0; }
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
            // The order of two values of one number type, as its CompareTo
            // has it, decided from their static type: signed or unsigned, an
            // enum's by its underlying type, a float's with NaN first.
            public static int CompareValues(object a, object b) { return 0; }
            // Whether two values of one number type are equal as its Equals
            // has it -- a float's 0 and -0 equal, NaN equal to NaN -- at the
            // values' own width; and the hash .NET's GetHashCode gives the
            // value's type, an int widened to a long (a reference's or a
            // pointer's: its word).
            public static bool EqualValues(object a, object b) { return false; }
            public static long HashValue(object x) { return 0; }
            // The same reference as a string, once IsString has said it is one.
            // A reinterpretation, not a conversion: no bytes move.
            public static string AsString(object x) { return ""; }
            public static nuint DataStart() { return 0; }
            public static nuint DataEnd() { return 0; }
            // THE KERNEL'S LINK IDENTITY: the address of the thirty-two bytes
            // `corc link --exports` writes into a kernel, the hash of its
            // loaded image, which its file's export table holds too. Only in
            // a kernel linked so.
            public static nuint LinkId() { return 0; }

        }

        // EIGHT BYTES OF PACKED LANES, as MMX holds them (Packed): bytes,
        // words, doublewords, the quadword or two floats, by the operation
        // that reads them. Bits is all eight, lowest lane lowest.
        public struct Packed64 {
            public ulong Bits;
        }
        // SIXTEEN, as SSE holds them: Lo the low eight, Hi the high.
        public struct Packed128 {
            public ulong Lo;
            public ulong Hi;
        }
        // THE PACKED OPERATIONS, written anywhere in a method: each one
        // instruction in its processor's version of the method where that
        // version has it (the compiler's own block around the body) --
        // MMX, the MMX extensions, 3DNow!, SSE, SSE2 -- and its lanes one at
        // a time where it has not (Lowering.PackedIntrinsic, PackedOps). On
        // Packed64 MMX's, on Packed128 SSE's; the F32 ones two floats or
        // four, through 3DNow! where there is no SSE only for AddF32, SubF32,
        // MulF32 and the compares, whose answers there are IEEE's for
        // ordinary values only (no denormal result, no infinity, no NaN:
        // PackedOps.Float). The orders and places are integer literals,
        // which the instruction holds in its own bytes; so is a byte shift's
        // count. Any other shift's count may be any int, taken unsigned: a
        // constant from 0 to 255 goes in the instruction's bytes, anything
        // else in a register (PSRLW mm, mm and the rest), and a count at or
        // past the lane's width empties it, or fills it with its sign. Load32 and
        // Store32 move four bytes (MOVD), the high four zero in; Prefetch and
        // PrefetchWrite ask for a cache line ahead (PREFETCHNTA, 3DNow!'s
        // PREFETCH and PREFETCHW) and are nothing where there is none.
        public static class Packed {
            public static int Features() { return 0; }
            public static Packed64 FromBits(ulong bits) { return new Packed64(); }
            public static Packed128 FromBits(ulong lo, ulong hi) { return new Packed128(); }
            public static Packed64 Load(nuint address) { return new Packed64(); }
            public static Packed128 Load128(nuint address) { return new Packed128(); }
            public static void Store(nuint address, Packed64 value) { }
            public static void Store(nuint address, Packed128 value) { }
            public static void StoreNonTemporal(nuint address, Packed64 value) { }
            public static void StoreNonTemporal(nuint address, Packed128 value) { }
            public static void Fence() { }
            public static Packed64 Load32(nuint address) { return new Packed64(); }
            public static void Store32(nuint address, Packed64 value) { }
            public static void Prefetch(nuint address) { }
            public static void PrefetchWrite(nuint address) { }
            public static Packed64 Add8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Add16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Add32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Add64(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Sub8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Sub16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Sub32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Sub64(Packed64 a, Packed64 b) { return a; }
            public static Packed64 AddSaturateU8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 AddSaturateS8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 AddSaturateU16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 AddSaturateS16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 SubSaturateU8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 SubSaturateS8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 SubSaturateU16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 SubSaturateS16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MultiplyLow16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MultiplyHigh16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MultiplyHighU16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MultiplyAdd16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 And(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Or(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Xor(Packed64 a, Packed64 b) { return a; }
            public static Packed64 AndNot(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Equal8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Equal16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Equal32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Greater8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Greater16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Greater32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 PackSaturateU8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 PackSaturateS8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 PackSaturateS16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 UnpackLow8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 UnpackLow16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 UnpackLow32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 UnpackHigh8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 UnpackHigh16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 UnpackHigh32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Average8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Average16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MinU8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MaxU8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Min16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 Max16(Packed64 a, Packed64 b) { return a; }
            public static Packed64 SumAbsoluteDifferences8(Packed64 a, Packed64 b) { return a; }
            public static Packed64 ShiftLeft16(Packed64 a, int count) { return a; }
            public static Packed64 ShiftLeft32(Packed64 a, int count) { return a; }
            public static Packed64 ShiftLeft64(Packed64 a, int count) { return a; }
            public static Packed64 ShiftRightU16(Packed64 a, int count) { return a; }
            public static Packed64 ShiftRightU32(Packed64 a, int count) { return a; }
            public static Packed64 ShiftRightU64(Packed64 a, int count) { return a; }
            public static Packed64 ShiftRightS16(Packed64 a, int count) { return a; }
            public static Packed64 ShiftRightS32(Packed64 a, int count) { return a; }
            public static Packed64 AddF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 SubF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MulF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 DivF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MinF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 MaxF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 EqualF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 LessF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 LessEqualF32(Packed64 a, Packed64 b) { return a; }
            public static Packed64 SqrtF32(Packed64 a) { return a; }
            public static Packed64 IntToFloat32(Packed64 a) { return a; }
            public static Packed64 FloatToInt32(Packed64 a) { return a; }
            public static Packed128 Add8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Add16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Add32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Add64(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Sub8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Sub16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Sub32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Sub64(Packed128 a, Packed128 b) { return a; }
            public static Packed128 AddSaturateU8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 AddSaturateS8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 AddSaturateU16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 AddSaturateS16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 SubSaturateU8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 SubSaturateS8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 SubSaturateU16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 SubSaturateS16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MultiplyLow16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MultiplyHigh16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MultiplyHighU16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MultiplyAdd16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 And(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Or(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Xor(Packed128 a, Packed128 b) { return a; }
            public static Packed128 AndNot(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Equal8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Equal16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Equal32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Greater8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Greater16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Greater32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 PackSaturateU8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 PackSaturateS8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 PackSaturateS16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 UnpackLow8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 UnpackLow16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 UnpackLow32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 UnpackHigh8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 UnpackHigh16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 UnpackHigh32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Average8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Average16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MinU8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MaxU8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Min16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 Max16(Packed128 a, Packed128 b) { return a; }
            public static Packed128 SumAbsoluteDifferences8(Packed128 a, Packed128 b) { return a; }
            public static Packed128 ShiftLeft16(Packed128 a, int count) { return a; }
            public static Packed128 ShiftLeft32(Packed128 a, int count) { return a; }
            public static Packed128 ShiftLeft64(Packed128 a, int count) { return a; }
            public static Packed128 ShiftRightU16(Packed128 a, int count) { return a; }
            public static Packed128 ShiftRightU32(Packed128 a, int count) { return a; }
            public static Packed128 ShiftRightU64(Packed128 a, int count) { return a; }
            public static Packed128 ShiftRightS16(Packed128 a, int count) { return a; }
            public static Packed128 ShiftRightS32(Packed128 a, int count) { return a; }
            public static Packed128 AddF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 SubF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MulF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 DivF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MinF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 MaxF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 EqualF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 LessF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 LessEqualF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 SqrtF32(Packed128 a) { return a; }
            public static Packed128 IntToFloat32(Packed128 a) { return a; }
            public static Packed128 FloatToInt32(Packed128 a) { return a; }
            public static Packed64 Shuffle16(Packed64 a, int order) { return a; }
            public static int Extract16(Packed64 a, int at) { return 0; }
            public static int Extract16(Packed128 a, int at) { return 0; }
            public static Packed64 Insert16(Packed64 a, int value, int at) { return a; }
            public static Packed128 Insert16(Packed128 a, int value, int at) { return a; }
            public static Packed128 Shuffle32(Packed128 a, int order) { return a; }
            public static Packed128 ShuffleLow16(Packed128 a, int order) { return a; }
            public static Packed128 ShuffleHigh16(Packed128 a, int order) { return a; }
            public static Packed128 ShiftLeftBytes(Packed128 a, int count) { return a; }
            public static Packed128 ShiftRightBytes(Packed128 a, int count) { return a; }
            public static Packed128 ShuffleF32(Packed128 a, Packed128 b, int order) { return a; }
            public static Packed128 UnpackLowF32(Packed128 a, Packed128 b) { return a; }
            public static Packed128 UnpackHighF32(Packed128 a, Packed128 b) { return a; }
        }
        public static class Math {
            public static double Sqrt(double x) { return 0.0; }
            public static double Abs(double x) { return 0.0; }
            public static double Floor(double x) { return 0.0; }
            public static double Ceiling(double x) { return 0.0; }
            public static double Round(double x) { return 0.0; }
            public static double Truncate(double x) { return 0.0; }
            public static double Min(double a, double b) { return 0.0; }
            public static double Max(double a, double b) { return 0.0; }
            public static double FusedMultiplyAdd(double x, double y, double z) { return 0.0; }
        }

        """;
}
