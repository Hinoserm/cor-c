#nullable enable
namespace Corsac.Lang;

/// <summary>
/// THE PACKED OPERATIONS (Prelude's Packed, on Packed64 and Packed128), as
/// the parser and lowering both read them: what each is, which instruction
/// it is where the processor has it, and which of the processor's features
/// it can use at all -- what picks a packed block's variants
/// (Parser.ParsePackedBlock) and what each variant makes of the operation
/// (Lowering.PackedIntrinsic). The features are Processor's bits, which a
/// variant's mask and Sys.PackedChoose's are written in.
/// </summary>
public static class PackedOps
{
    public const int Mmx = 1 << 2, MmxExtensions = 1 << 3, Sse = 1 << 4, Sse2 = 1 << 5, ThreeDNow = 1 << 6;

    /// <summary>
    /// THE PROCESSORS A BLOCK'S VARIANTS ARE FOR, best first: the K8, the
    /// Pentium 4, the Athlon XP, the Pentium III, the Athlon, the K6-2, a
    /// Pentium MMX, and one with none of them. A block keeps each projected
    /// onto the features its body can use, without repeats, and the first
    /// whose features the processor has runs.
    /// </summary>
    public static readonly int[] Classes =
    [
        Mmx | MmxExtensions | Sse | Sse2 | ThreeDNow,
        Mmx | MmxExtensions | Sse | Sse2,
        Mmx | MmxExtensions | ThreeDNow | Sse,
        Mmx | MmxExtensions | Sse,
        Mmx | MmxExtensions | ThreeDNow,
        Mmx | ThreeDNow,
        Mmx,
        0,
    ];

    /// <summary>A two-operand integer operation's opcode, 0F xx, MMX's and SSE2's (66 0F xx) alike; whether it is one of the MMX extensions'; whether its lanes cross a 64-bit half (pack, unpack).</summary>
    public static readonly Dictionary<string, (byte Op, bool Ext, bool Crosses)> Binary = new(StringComparer.Ordinal)
    {
        ["Add8"] = (0xFC, false, false), ["Add16"] = (0xFD, false, false), ["Add32"] = (0xFE, false, false), ["Add64"] = (0xD4, false, false),
        ["Sub8"] = (0xF8, false, false), ["Sub16"] = (0xF9, false, false), ["Sub32"] = (0xFA, false, false), ["Sub64"] = (0xFB, false, false),
        ["AddSaturateU8"] = (0xDC, false, false), ["AddSaturateS8"] = (0xEC, false, false),
        ["AddSaturateU16"] = (0xDD, false, false), ["AddSaturateS16"] = (0xED, false, false),
        ["SubSaturateU8"] = (0xD8, false, false), ["SubSaturateS8"] = (0xE8, false, false),
        ["SubSaturateU16"] = (0xD9, false, false), ["SubSaturateS16"] = (0xE9, false, false),
        ["MultiplyLow16"] = (0xD5, false, false), ["MultiplyHigh16"] = (0xE5, false, false), ["MultiplyAdd16"] = (0xF5, false, false),
        ["And"] = (0xDB, false, false), ["Or"] = (0xEB, false, false), ["Xor"] = (0xEF, false, false), ["AndNot"] = (0xDF, false, false),
        ["Equal8"] = (0x74, false, false), ["Equal16"] = (0x75, false, false), ["Equal32"] = (0x76, false, false),
        ["Greater8"] = (0x64, false, false), ["Greater16"] = (0x65, false, false), ["Greater32"] = (0x66, false, false),
        ["PackSaturateU8"] = (0x67, false, true), ["PackSaturateS8"] = (0x63, false, true), ["PackSaturateS16"] = (0x6B, false, true),
        ["UnpackLow8"] = (0x60, false, true), ["UnpackLow16"] = (0x61, false, true), ["UnpackLow32"] = (0x62, false, true),
        ["UnpackHigh8"] = (0x68, false, true), ["UnpackHigh16"] = (0x69, false, true), ["UnpackHigh32"] = (0x6A, false, true),
        ["MultiplyHighU16"] = (0xE4, true, false), ["Average8"] = (0xE0, true, false), ["Average16"] = (0xE3, true, false),
        ["MinU8"] = (0xDA, true, false), ["MaxU8"] = (0xDE, true, false), ["Min16"] = (0xEA, true, false), ["Max16"] = (0xEE, true, false),
        ["SumAbsoluteDifferences8"] = (0xF6, true, false),
    };

    /// <summary>A shift by a count in a register (PSRLW mm, mm/m64 and the rest): its opcode (0F xx, 66 0F xx), by the constant form's group and /digit. Not the byte shifts, which have none.</summary>
    public static readonly Dictionary<(byte Group, byte Sub), byte> ShiftByRegister = new()
    {
        [(0x71, 2)] = 0xD1, [(0x72, 2)] = 0xD2, [(0x73, 2)] = 0xD3,
        [(0x71, 4)] = 0xE1, [(0x72, 4)] = 0xE2,
        [(0x71, 6)] = 0xF1, [(0x72, 6)] = 0xF2, [(0x73, 6)] = 0xF3,
    };

    /// <summary>A shift by a constant count: the group (0F 71, 72, 73) and its /digit. The byte shifts are SSE2's alone (66 0F 73 /7, /3).</summary>
    public static readonly Dictionary<string, (byte Group, byte Sub)> Shift = new(StringComparer.Ordinal)
    {
        ["ShiftLeft16"] = (0x71, 6), ["ShiftLeft32"] = (0x72, 6), ["ShiftLeft64"] = (0x73, 6),
        ["ShiftRightU16"] = (0x71, 2), ["ShiftRightU32"] = (0x72, 2), ["ShiftRightU64"] = (0x73, 2),
        ["ShiftRightS16"] = (0x71, 4), ["ShiftRightS32"] = (0x72, 4),
        ["ShiftLeftBytes"] = (0x73, 7), ["ShiftRightBytes"] = (0x73, 3),
    };

    /// <summary>
    /// A two-float operation, two floats in a Packed64 or four in a
    /// Packed128: SSE's opcode (0F xx, or 0F C2 with a predicate), 3DNow!'s
    /// suffix (0F 0F /r xx, 0 where it has none exact), and whether 3DNow!'s
    /// takes its operands the other way round (its compares are greater
    /// ones: a less is b's greater). 3DNow! is IEEE's only for ordinary
    /// values: it flushes a denormal result to zero, gives the largest
    /// number where SSE overflows to infinity, and takes no infinity or NaN;
    /// its min and max say nothing of a NaN, and its conversions round
    /// toward zero and saturate where SSE's round to nearest and give
    /// 0x80000000. Those are the scalar forms' where there is no SSE.
    /// </summary>
    public static readonly Dictionary<string, (byte Sse, int Predicate, byte ThreeDNow, bool Swapped)> Float = new(StringComparer.Ordinal)
    {
        ["AddF32"] = (0x58, -1, 0x9E, false), ["SubF32"] = (0x5C, -1, 0x9A, false), ["MulF32"] = (0x59, -1, 0xB4, false),
        ["DivF32"] = (0x5E, -1, 0x00, false), ["MinF32"] = (0x5D, -1, 0x00, false), ["MaxF32"] = (0x5F, -1, 0x00, false),
        ["EqualF32"] = (0xC2, 0, 0xB0, false), ["LessF32"] = (0xC2, 1, 0xA0, true), ["LessEqualF32"] = (0xC2, 2, 0x90, true),
    };

    /// <summary>
    /// The features an operation of this name can use, on either width: what
    /// a block's variants are told apart by. A name the table does not know
    /// uses none.
    /// </summary>
    public static int FeaturesOf(string name)
    {
        int features = Named(name);
        // SSE2'S INSTRUCTIONS ARE SSE'S REGISTERS: a variant with SSE2 has SSE
        // too, or the operations that want it would never find it (Lowering.
        // PackedIntrinsic asks for both).
        return (features & Sse2) != 0 ? features | Sse : features;
    }

    private static int Named(string name)
    {
        if (Binary.TryGetValue(name, out var b)) return Mmx | Sse2 | (b.Ext ? MmxExtensions : 0);
        if (Shift.ContainsKey(name)) return name.Contains("Bytes") ? Sse2 : Mmx | Sse2;
        if (Float.ContainsKey(name) || name == "SqrtF32") return Mmx | ThreeDNow | Sse;
        return name switch
        {
            "Shuffle16" or "Extract16" or "Insert16" => Mmx | MmxExtensions | Sse2,
            "Shuffle32" or "ShuffleLow16" or "ShuffleHigh16" => Sse2,
            "ShuffleF32" or "UnpackLowF32" or "UnpackHighF32" => Sse,
            "IntToFloat32" or "FloatToInt32" => Mmx | ThreeDNow | Sse2,
            // MOVNTQ; MOVNTDQ, SSE2's, for sixteen bytes at sixteen.
            "StoreNonTemporal" => Mmx | MmxExtensions | Sse2,
            "Fence" => Mmx | MmxExtensions,
            "Load32" or "Store32" => Mmx,
            // PREFETCHNTA (the MMX extensions'), 3DNow!'s PREFETCH and PREFETCHW.
            "Prefetch" or "PrefetchWrite" => MmxExtensions | ThreeDNow,
            _ => 0,
        };
    }
}
