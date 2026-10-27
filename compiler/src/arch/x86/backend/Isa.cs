#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// What each machine instruction asks of the processor: the one table the
/// emitted-code audit (Encoder.Audit) checks every instruction of every
/// function against, before a byte of it is written.
///
/// THE INSTRUCTIONS, NOT THE TARGET'S NAME. Selection asks the profile
/// before it chooses a newer instruction, but a selection path that forgets
/// to ask, or a pass that rebuilds an instruction, would otherwise put it
/// into a 486 image with no error at all; this table is what makes
/// "--cpu=486 emits nothing a 486 lacks" a property of the bytes, the same
/// in a unit compiled whole and in one regenerated at the link under its
/// own recorded profile (UnitBackend). An instruction that needs nothing
/// past the 386 has no row.
/// </summary>
internal static class Isa
{
    /// <summary>The features that live in the MMX registers: their state is the x87's, and the hosted ABI's to lend.</summary>
    private const X86Feature PackedState = X86Feature.Mmx | X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended;

    /// <summary>SSE's: the operating system enables it (CR4.OSFXSR) and saves its state, so only hosted code has it.</summary>
    private const X86Feature SseState = X86Feature.Sse | X86Feature.Sse2;

    /// <summary>What <paramref name="op"/> needs beyond the 386.</summary>
    public static X86Feature Requires(MOp op) => op switch
    {
        MOp.Bswap or MOp.Xadd or MOp.Cmpxchg or MOp.Invlpg => X86Feature.I486,
        MOp.Cpuid or MOp.Rdtsc or MOp.Rdmsr or MOp.Wrmsr or MOp.Cmpxchg8b => X86Feature.Pentium,
        MOp.MmxLoad or MOp.MmxStore or MOp.MmxZero or MOp.Emms
            or MOp.MmxAddB or MOp.MmxAddW or MOp.MmxAddD or MOp.MmxSubB or MOp.MmxSubW or MOp.MmxSubD
            or MOp.MmxAnd or MOp.MmxOr or MOp.MmxXor or MOp.MmxMulW or MOp.MmxMulHighW
            or MOp.MmxShlW or MOp.MmxShlD or MOp.MmxShrW or MOp.MmxShrD or MOp.MmxSarW or MOp.MmxSarD
            or MOp.MmxLoadD or MOp.MmxDuplicateLowWords or MOp.MmxWidenUnsignedWords or MOp.MmxSaveToTwo
            or MOp.MmxMultiplyAddW
            or MOp.MmxEqualB or MOp.MmxEqualW or MOp.MmxEqualD or MOp.MmxGreaterB or MOp.MmxGreaterW or MOp.MmxGreaterD
            => X86Feature.Mmx,
        MOp.Femms or MOp.ThreeDNowAverageB or MOp.ThreeDNowMulRoundW or MOp.ThreeDNowIntToFloat
            or MOp.ThreeDNowAddFromTwo or MOp.ThreeDNowSubFromTwo or MOp.ThreeDNowMulFromTwo or MOp.ThreeDNowFloatToInt
            => X86Feature.ThreeDNow,
        // PI2FW: one of the five the K6-2+ and K6-III+ added to 3DNow!.
        MOp.ThreeDNowShortToFloat => X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended,
        MOp.Cmov => X86Feature.Cmov,
        // FUCOMIP: the x87 half of what CPUID calls CMOV.
        MOp.Fucomipp => X86Feature.FloatCmov,
        MOp.Cvttss2si => X86Feature.Sse,
        MOp.Cvttsd2si => X86Feature.Sse | X86Feature.Sse2,
        // PAUSE is F3 90, REP NOP: a plain no-op to anything before the
        // Pentium 4, and so needs nothing.
        _ => X86Feature.None,
    };

    /// <summary>
    /// What <paramref name="i"/> needs beyond the 386: its opcode's, and the
    /// Pentium's for a move to or from CR4, which the 386 and the 486 do not
    /// have. The source's own control-register moves are Probed, as its
    /// INVLPG is (Select): a kernel that runs on a 486 too asks first.
    /// </summary>
    public static X86Feature Requires(MInstr i)
        => Requires(i.Op) | (i.Op is MOp.MovFromCr or MOp.MovToCr && i.Operands.Any(o => o is MImm { Value: 4 }) ? X86Feature.Pentium : X86Feature.None);

    /// <summary>
    /// Whether <paramref name="op"/> reads or writes the MMX registers, whose
    /// state is the x87's: code may touch it only where the hosted ABI owns
    /// that state for it (X86Backend.AutomaticPacked). Read off the table, so
    /// a new packed instruction is counted the moment it has its row.
    /// </summary>
    public static bool UsesPackedState(MOp op) => (Requires(op) & PackedState) != 0;

    /// <summary>Whether <paramref name="op"/> is an SSE instruction, which only code the hosted ABI selected may use (X86Backend.AutomaticPacked).</summary>
    public static bool UsesSseState(MOp op) => (Requires(op) & SseState) != 0;

    /// <summary>The features in <paramref name="features"/>, by the names a programmer knows them by.</summary>
    public static string Describe(X86Feature features)
    {
        List<string> names = new();
        if ((features & X86Feature.I486) != 0) names.Add("486");
        if ((features & X86Feature.Pentium) != 0) names.Add("Pentium");
        if ((features & X86Feature.Mmx) != 0) names.Add("MMX");
        if ((features & X86Feature.ThreeDNow) != 0) names.Add("3DNow!");
        if ((features & X86Feature.ThreeDNowExtended) != 0) names.Add("the K6-2+/K6-III+ 3DNow! extensions");
        if ((features & X86Feature.Cmov) != 0) names.Add("CMOV");
        if ((features & X86Feature.FloatCmov) != 0) names.Add("FCMOV/FCOMI");
        if ((features & X86Feature.MmxExtensions) != 0) names.Add("the MMX extensions");
        if ((features & X86Feature.Sse) != 0) names.Add("SSE");
        if ((features & X86Feature.Sse2) != 0) names.Add("SSE2");
        return string.Join(" and ", names);
    }
}
