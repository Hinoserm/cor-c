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
    /// The padding before a loop's head at section offset `at`, where
    /// nothing runs it: on the 486 and the Pentium a label to the next 16
    /// when it is under 8 bytes away (AP-500 5.4.1; Intel 242816 3.4.1, the
    /// 486's two 16-byte prefetch buffers, the Pentium's fetch on 16-byte
    /// boundaries); on the K6 out of the last 8 bytes of a 32-byte line, so
    /// that the loop starts at least two instructions before its end (AMD
    /// 21924, "Code Alignment"). None for anything else.
    /// </summary>
    public static int LoopPad(int at)
    {
        if (Scalar || P5)
        {
            int need = (16 - at % 16) % 16;
            return need < 8 ? need : 0;
        }
        if (K6)
        {
            int into = at % 32;
            return into > 24 ? 32 - into : 0;
        }
        return 0;
    }

    /// <summary>Whether LoopPad ever pads: the section must then be 16 or 32 aligned for an offset in it to mean an address.</summary>
    public static int CodeAlign => K6 ? 32 : Scalar || P5 ? 16 : 4;

    /// <summary>
    /// A function's alignment: 16 on the K6 (AMD 21924, "Aligning
    /// subroutines at 0-mod-16 ... optimizes instruction cache-fill
    /// efficiency"); Intel's 486 and Pentium rules ask for none beyond the
    /// words, and every function padded to 16 would cost a 486 cache.
    /// </summary>
    public static int FunctionAlign(int asked) => Math.Max(asked, K6 ? 16 : 4);
}
