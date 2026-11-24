#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// THE PROCESSOR THE CODE IS FOR, as the choices that depend on its timing
/// ask it: `--cpu` alone (Target.X86Profile.Name), no separate tuning. Each
/// choice names the manual it follows: Intel 242816 (Intel Architecture
/// Optimization Manual: the Pentium, the Pentium MMX, the Pentium Pro and
/// II), AMD 21924 (AMD-K6 Processor Code Optimization), and Agner Fog's
/// instruction tables and microarchitecture notes for the clocks and pairing.
/// </summary>
internal static class CpuModel
{
    private static string Name => Target.Current.X86Profile.Name;

    /// <summary>The Pentium and the Pentium MMX (P54C, P55C): two pipes, U and V, paired by Intel's rules.</summary>
    public static bool P5 => Name is "pentium" or "pentium-mmx";

    /// <summary>The 386 and the 486: one pipe, every instruction's clocks in a row.</summary>
    public static bool Scalar => Name is "386" or "486";

    /// <summary>The 486 alone, where a choice differs from the 386's.</summary>
    public static bool I486 => Name == "486";

    /// <summary>The AMD-K6 family, K6 to K6-III+: two short decoders, RISC86 operations, a fast multiplier.</summary>
    public static bool K6 => Name is "k6" or "k6-2" or "k6-3" or "k6-2+" or "k6-3+";

    /// <summary>
    /// The AMD K7, the Athlon and the Athlon XP (AMD 22007): three DirectPath
    /// decoders fed sixteen bytes a clock, IMUL r32 four clocks.
    /// </summary>
    public static bool K7 => Name is "athlon" or "athlon-xp";

    /// <summary>
    /// The AMD K8 running 32-bit code, the Athlon 64 and the Opteron (AMD
    /// 25112): the K7's three decoders and sixteen-byte fetch, IMUL r32 three
    /// clocks.
    /// </summary>
    public static bool K8 => Name is "athlon64" or "athlon64-sse3";

    /// <summary>AMD's K10 (Family 10h, Barcelona and Phenom; AMD 40546): the K8's pipeline with 128-bit SSE units.</summary>
    public static bool K10 => Name == "amdfam10";

    /// <summary>
    /// Intel's Core line, Core 2 to Skylake: XOR, SUB, PXOR, XORPS and XORPD of
    /// a register with itself are zero idioms (Agner Fog, microarchitecture
    /// 8.14 on, "Breaking dependency chains"), and every scalar SSE merge
    /// waits on the register's last write (GCC's SSE_PARTIAL_REG_DEPENDENCY
    /// and _FP_CONVERTS_DEPENDENCY, m_CORE_ALL).
    /// </summary>
    public static bool IntelCore => Name is "core2" or "nehalem" or "sandybridge" or "haswell" or "skylake" or "skylake-avx512";

    /// <summary>
    /// The psABI's levels above the baseline, which name instructions and no
    /// processor: tuned for what has them -- the Core line and the K10 on, whose
    /// rules here are alike (GCC tunes them generically, PTA_NO_TUNE). The
    /// baseline, x86-64 itself, is tuned as the K8 that defined it.
    /// </summary>
    public static bool LevelAboveBaseline => Name is "x86-64-v2" or "x86-64-v3" or "x86-64-v4";

    /// <summary>x86-64's baseline (x86-64-v1): the K8's long mode without 3DNow!, tuned as the K8.</summary>
    public static bool Baseline64 => Name == "x86-64";

    /// <summary>
    /// AMD's Geode LX (AMD 33234E/H): one instruction a clock, in order, down
    /// an eight-stage pipe -- IMUL r32 four clocks and IMUL by an immediate
    /// four to seven, SAL, LEA, MOVZX, CDQ and CMOVcc one, SAR two (Table
    /// 8-26). A constant multiply takes a recipe of four at most
    /// (Select.MulByConstant) -- no slower than IMUL's best four, faster than
    /// its seven; MOVZX and CDQ (one clock) and PUSH [mem] (one or two) are
    /// kept (Peephole.ChooseForCpu replaces them for others only).
    /// </summary>
    public static bool GeodeLx => Name == "geode-lx";

    /// <summary>
    /// Either of them. What the K6's and the Pentium's rules replace -- MOVZX,
    /// PUSH [mem], CDQ -- each a DirectPath instruction of one clock or so
    /// there (AMD 22007's and 25112's tables), the Athlon keeps as it is.
    /// </summary>
    public static bool Athlon => K7 || K8;

    /// <summary>
    /// WHETHER A SCALAR SSE INSTRUCTION'S MERGE IS BROKEN FIRST (MInstr.ZeroFirst):
    /// CVTSI2SS/SD, CVTSS2SD/CVTSD2SS and SQRTSS/SD write the low lane alone
    /// and keep the rest, so each waits on whatever last wrote its register.
    /// Zeroing the register first removes the wait where the processor takes
    /// the zeroing for a value of its own (a zero idiom):
    ///
    /// - The Pentium 4 takes PXOR of a register with itself so, and not XORPS
    ///   or XORPD (Agner Fog, microarchitecture 5.11: "The same applies to
    ///   the PXOR instruction ... but not to ... XORPS, XORPD"): every one of
    ///   these merges gets PXOR there, as GCC breaks them for its P4 tuning --
    ///   and none without SSE2 (--disable-sse2), PXOR of an XMM register
    ///   being SSE2's and the XORPS a P4 does not take only one more
    ///   instruction.
    /// - The K8 takes PXOR, XORPS and XORPD (Agner, chapter 20: "A false
    ///   dependence can be broken by ... PXOR, XORPS, XORPD"); XORPS, the
    ///   floating-point one AMD 25112 gives for clearing an XMM register
    ///   (9.13), keeps the zero in the domain the conversion reads. The K8
    ///   holds each XMM register as two independent 64-bit halves (Agner,
    ///   20.11), so a write of a whole half -- CVTSI2SD, CVTSS2SD, SQRTSD --
    ///   waits on nothing; a 32-bit write is a write of part of a half, and
    ///   "an instruction that writes part of a register merges the modified
    ///   portion with the current state of the other part" (AMD 25112, 4.7);
    ///   and 25112's own table of scalar conversions (Appendix E, Table 22)
    ///   puts XORPS before CVTSD2SS and CVTSI2SS and nothing before CVTSS2SD
    ///   or CVTSI2SD. Only the 32-bit ones get XORPS there,
    ///   in either mode; x86-64 code is for the K8 and later, as its backend
    ///   says (X64 Machine), whatever --cpu names, unless it names the P4.
    /// - The P6 family, the Pentium III among them, and the K7 take no such
    ///   idiom for an XMM register (Agner 6.8: XOR "doesn't break its
    ///   dependence on earlier instructions on PPro, P2, P3"; nothing claimed
    ///   for the K7): a zeroing there would only be one more instruction.
    /// </summary>
    ///
    /// THE LONG-MODE FAMILIES: Nocona is the Pentium 4's core (PXOR, every
    /// merge); the Core line and the K10 break every merge, Intel's by PXOR
    /// (GCC's SSE_LOAD0_BY_PXOR) and the K10, whose SSE units are 128 bits
    /// wide where the K8's were two halves, the same (GCC's amdfam10 is in
    /// both lists); the psABI's levels above the baseline as they; the
    /// baseline itself, x86-64, as the K8 whose set it is.
    /// </summary>
    public static bool BreaksMerge(int width) =>
        Name is "pentium4" or "nocona" ? Target.Current.X86Profile.Has(X86Feature.Sse2)
        : IntelCore || K10 || LevelAboveBaseline ? true
        : (K8 || Baseline64) && width == 4;

    /// <summary>The zeroing BreaksMerge asks for: PXOR where the processor takes it and not XORPS (the Pentium 4 and Nocona), or where GCC zeroes so (the Core line, the K10, the levels); XORPS on the K8 and its baseline, AMD 25112's own.</summary>
    public static bool ZeroByPxor => !(K8 || Baseline64);

    /// <summary>
    /// A function's alignment: 16 on the K6 (AMD 21924, "Aligning
    /// subroutines at 0-mod-16 ... optimizes instruction cache-fill
    /// efficiency"), and on the K7 and K8, which fetch and predecode an
    /// aligned sixteen-byte window a clock (AMD 22007, 25112: branch targets
    /// and functions at 16); Intel's 486 and Pentium rules ask for none
    /// beyond the words, and every function padded to 16 would cost a 486
    /// cache.
    /// </summary>
    public static int FunctionAlign(int asked) => Math.Max(asked, K6 || Athlon || K10 || IntelCore || LevelAboveBaseline || Baseline64 ? 16 : 4);
}
