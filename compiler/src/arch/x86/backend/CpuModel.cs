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
    public static bool K8 => Name == "athlon64";

    /// <summary>
    /// AMD's Geode LX (AMD 33234H): one instruction a clock, in order, down
    /// an eight-stage pipe -- IMUL r32 four clocks (Table 8-27), SAL, LEA
    /// and most of the ALU one, SAR two. MOVZX, CDQ and PUSH [mem] are kept:
    /// one operation each to it, as GCC's Geode model has them. A constant
    /// multiply takes a recipe of three at most (Select.MulByConstant's
    /// default), under the four clocks IMUL would.
    /// </summary>
    public static bool GeodeLx => Name == "geode-lx";

    /// <summary>
    /// Either of them. What the K6's and the Pentium's rules replace -- MOVZX,
    /// PUSH [mem], CDQ -- each a DirectPath instruction of one clock or so
    /// there (AMD 22007's and 25112's tables), the Athlon keeps as it is.
    /// </summary>
    public static bool Athlon => K7 || K8;

    /// <summary>
    /// A function's alignment: 16 on the K6 (AMD 21924, "Aligning
    /// subroutines at 0-mod-16 ... optimizes instruction cache-fill
    /// efficiency"), and on the K7 and K8, which fetch and predecode an
    /// aligned sixteen-byte window a clock (AMD 22007, 25112: branch targets
    /// and functions at 16); Intel's 486 and Pentium rules ask for none
    /// beyond the words, and every function padded to 16 would cost a 486
    /// cache.
    /// </summary>
    public static int FunctionAlign(int asked) => Math.Max(asked, K6 || Athlon ? 16 : 4);
}
