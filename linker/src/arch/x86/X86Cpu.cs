namespace Corsac.Lang.X86;

/// <summary>
/// The instruction families a profile may use beyond the 386's. A processor
/// is a set of these, not a point on one line: the K6-2 has 3DNow! and no
/// CMOV, the Pentium Pro CMOV and no MMX, so which code runs where is a
/// question of one set holding another (X86CodeGenerationContract.ValidateTarget).
/// The long-mode families from LongMode on: x86-64 code runs only where it is.
/// </summary>
[Flags]
public enum X86Feature : uint
{
    /// <summary>The 386's instructions and nothing more.</summary>
    None = 0,
    /// <summary>The 486's additions: BSWAP, XADD, CMPXCHG and INVLPG.</summary>
    I486 = 1 << 0,
    /// <summary>The Pentium's: CPUID, RDTSC, RDMSR, WRMSR and CMPXCHG8B.</summary>
    Pentium = 1 << 1,
    /// <summary>MMX: eight 64-bit registers over the x87 stack and the packed integer operations on them.</summary>
    Mmx = 1 << 2,
    /// <summary>3DNow!: packed single floats in the MMX registers, FEMMS, PREFETCH and PREFETCHW (K6-2 on).</summary>
    ThreeDNow = 1 << 3,
    /// <summary>
    /// The five 3DNow! extensions, PF2IW, PI2FW, PFNACC, PFPNACC and PSWAPD:
    /// the K6-2+, the K6-III+ and the Athlon have them.
    /// </summary>
    ThreeDNowExtended = 1 << 4,
    /// <summary>Integer CMOVcc, from the Pentium Pro and the Athlon on.</summary>
    Cmov = 1 << 5,
    /// <summary>
    /// FCMOVcc, FCOMI and FUCOMI: the x87 half of what CPUID calls CMOV, and
    /// so it needs an x87. Kept apart from Cmov because a processor can have
    /// one without the other.
    /// </summary>
    FloatCmov = 1 << 6,
    /// <summary>
    /// AMD's MMX extensions, which are the integer half of SSE: PAVGB, PMINUB,
    /// PMAXSW, PSHUFW, PMOVMSKB, MOVNTQ, PREFETCHh and SFENCE.
    /// </summary>
    MmxExtensions = 1 << 7,
    /// <summary>SSE: the XMM registers and single precision in them, scalar and packed (Pentium III, Athlon XP).</summary>
    Sse = 1 << 8,
    /// <summary>SSE2: double precision and integers in the XMM registers (Pentium 4, Athlon 64).</summary>
    Sse2 = 1 << 9,
    /// <summary>
    /// LONG MODE (AMD64, Intel 64; CPUID 8000_0001h EDX bit 29): the processor
    /// runs x86-64 code. Every model with it has CMOV, CMPXCHG8B, MMX, SSE and
    /// SSE2 (the psABI's baseline, x86-64-v1).
    /// </summary>
    LongMode = 1 << 10,
    /// <summary>SSE3 (CPUID 1 ECX 0): the K8 from revision E (Opteron 2xx dual-core, Athlon 64 X2), the Pentium 4 Prescott and Nocona.</summary>
    Sse3 = 1 << 11,
    /// <summary>SSSE3 (CPUID 1 ECX 9): Core 2 on.</summary>
    Ssse3 = 1 << 12,
    /// <summary>SSE4.1 (CPUID 1 ECX 19): Penryn and Nehalem on, AMD from Bulldozer.</summary>
    Sse41 = 1 << 13,
    /// <summary>SSE4.2 (CPUID 1 ECX 20): Nehalem on.</summary>
    Sse42 = 1 << 14,
    /// <summary>AMD's SSE4a, EXTRQ/INSERTQ/MOVNTSD/MOVNTSS (CPUID 8000_0001h ECX 6): the K10 on.</summary>
    Sse4a = 1 << 15,
    /// <summary>POPCNT (CPUID 1 ECX 23): Nehalem and the K10 on.</summary>
    Popcnt = 1 << 16,
    /// <summary>LZCNT (CPUID 8000_0001h ECX 5, AMD's ABM): the K10 and Haswell on.</summary>
    Lzcnt = 1 << 17,
    /// <summary>CMPXCHG16B (CPUID 1 ECX 13): the K8 from revision F, Nocona's later steppings, Core 2 on.</summary>
    Cmpxchg16b = 1 << 18,
    /// <summary>LAHF and SAHF in 64-bit mode (CPUID 8000_0001h ECX 0), which the first AMD64 and Intel 64 processors lacked.</summary>
    Lahf64 = 1 << 19,
    /// <summary>MOVBE (CPUID 1 ECX 22): Atom, Haswell on.</summary>
    Movbe = 1 << 20,
    /// <summary>BMI1 (CPUID 7 EBX 3): ANDN, BEXTR, BLSI, BLSMSK, BLSR, TZCNT.</summary>
    Bmi1 = 1 << 21,
    /// <summary>BMI2 (CPUID 7 EBX 8): BZHI, MULX, PDEP, PEXT, RORX, SARX, SHLX, SHRX.</summary>
    Bmi2 = 1 << 22,
    /// <summary>AVX (CPUID 1 ECX 28, and the operating system's XCR0 bits 1 and 2): the VEX encodings and the YMM registers.</summary>
    Avx = 1 << 23,
    /// <summary>AVX2 (CPUID 7 EBX 5).</summary>
    Avx2 = 1 << 24,
    /// <summary>FMA3 (CPUID 1 ECX 12).</summary>
    Fma = 1 << 25,
    /// <summary>F16C (CPUID 1 ECX 29): half-precision conversions.</summary>
    F16c = 1 << 26,
    /// <summary>AVX-512 F, CD, BW, DQ and VL together (CPUID 7 EBX 16, 28, 30, 17, 31; XCR0 bits 5 to 7): x86-64-v4's set.</summary>
    Avx512 = 1 << 27,
}

/// <summary>Shared compiler/linker instruction permissions; tuning never grants instructions.</summary>
public sealed class X86Cpu
{
    // What lives in the x87's registers or its state: MMX aliases the stack,
    // FCMOV and FCOMI are x87 instructions, and SSE's state is saved with it.
    // --fpu=none takes all of it away.
    private const X86Feature NeedsFpu = X86Feature.Mmx | X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended
        | X86Feature.MmxExtensions | X86Feature.FloatCmov | X86Feature.Sse | X86Feature.Sse2
        | X86Feature.Sse3 | X86Feature.Ssse3 | X86Feature.Sse41 | X86Feature.Sse42 | X86Feature.Sse4a
        | X86Feature.Avx | X86Feature.Avx2 | X86Feature.Fma | X86Feature.F16c | X86Feature.Avx512;

    // What each SSE generation and the VEX families stand on, from the bottom:
    // disabling one takes away everything above it (Switches).
    private const X86Feature AboveSse2 = X86Feature.Sse3 | X86Feature.Ssse3 | X86Feature.Sse41 | X86Feature.Sse42 | X86Feature.Sse4a
        | X86Feature.Avx | X86Feature.Avx2 | X86Feature.Fma | X86Feature.F16c | X86Feature.Avx512;
    private const X86Feature AboveAvx = X86Feature.Avx2 | X86Feature.Fma | X86Feature.F16c | X86Feature.Avx512;

    /// <summary>
    /// The --enable-NAME and --disable-NAME switches: the feature whose
    /// presence a switch decides, what enabling it brings with it, and what
    /// disabling it takes away. Enabling 3DNow! brings MMX and enabling SSE2
    /// brings SSE, as no processor has one without the other; SSE brings the
    /// MMX extensions, its own integer half, and MMX with them.
    /// </summary>
    private static readonly (string Name, X86Feature Feature, X86Feature Enables, X86Feature Disables)[] Switches =
    [
        ("mmx", X86Feature.Mmx, X86Feature.Mmx,
            X86Feature.Mmx | X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended | X86Feature.MmxExtensions),
        ("3dnow", X86Feature.ThreeDNow, X86Feature.Mmx | X86Feature.ThreeDNow, X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended),
        // The five 3DNow! extensions alone (the K6-2+, K6-III+, Athlon), and
        // AMD's MMX extensions -- SSE's integer half, which SSE goes with.
        ("3dnow-ext", X86Feature.ThreeDNowExtended, X86Feature.Mmx | X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended, X86Feature.ThreeDNowExtended),
        ("mmx-ext", X86Feature.MmxExtensions, X86Feature.Mmx | X86Feature.MmxExtensions, X86Feature.MmxExtensions | X86Feature.Sse | X86Feature.Sse2),
        ("cmov", X86Feature.Cmov, X86Feature.Cmov | X86Feature.FloatCmov, X86Feature.Cmov | X86Feature.FloatCmov),
        ("sse", X86Feature.Sse, X86Feature.Mmx | X86Feature.MmxExtensions | X86Feature.Sse, X86Feature.Sse | X86Feature.Sse2 | AboveSse2),
        ("sse2", X86Feature.Sse2, X86Feature.Mmx | X86Feature.MmxExtensions | X86Feature.Sse | X86Feature.Sse2, X86Feature.Sse2 | AboveSse2),
        // THE LONG-MODE GENERATIONS (GCC's -msse3 .. -mavx512f, the same
        // names): each brings what it stands on, each disable takes away what
        // stands on it.
        ("sse3", X86Feature.Sse3, SseUpTo(X86Feature.Sse3), AboveSse2),
        ("ssse3", X86Feature.Ssse3, SseUpTo(X86Feature.Ssse3), AboveSse2 & ~(X86Feature.Sse3 | X86Feature.Sse4a)),
        ("sse4.1", X86Feature.Sse41, SseUpTo(X86Feature.Sse41), X86Feature.Sse41 | X86Feature.Sse42 | X86Feature.Avx | AboveAvx),
        ("sse4.2", X86Feature.Sse42, SseUpTo(X86Feature.Sse42), X86Feature.Sse42 | X86Feature.Avx | AboveAvx),
        ("sse4a", X86Feature.Sse4a, SseUpTo(X86Feature.Sse3) | X86Feature.Sse4a, X86Feature.Sse4a),
        ("popcnt", X86Feature.Popcnt, X86Feature.Popcnt, X86Feature.Popcnt),
        ("lzcnt", X86Feature.Lzcnt, X86Feature.Lzcnt, X86Feature.Lzcnt),
        ("cx16", X86Feature.Cmpxchg16b, X86Feature.Cmpxchg16b, X86Feature.Cmpxchg16b),
        ("sahf", X86Feature.Lahf64, X86Feature.Lahf64, X86Feature.Lahf64),
        ("movbe", X86Feature.Movbe, X86Feature.Movbe, X86Feature.Movbe),
        ("bmi", X86Feature.Bmi1, X86Feature.Bmi1, X86Feature.Bmi1),
        ("bmi2", X86Feature.Bmi2, X86Feature.Bmi2, X86Feature.Bmi2),
        ("avx", X86Feature.Avx, SseUpTo(X86Feature.Sse42) | X86Feature.Avx, X86Feature.Avx | AboveAvx),
        ("avx2", X86Feature.Avx2, SseUpTo(X86Feature.Sse42) | X86Feature.Avx | X86Feature.Avx2, X86Feature.Avx2 | X86Feature.Avx512),
        ("fma", X86Feature.Fma, SseUpTo(X86Feature.Sse42) | X86Feature.Avx | X86Feature.Fma, X86Feature.Fma | X86Feature.Avx512),
        ("f16c", X86Feature.F16c, SseUpTo(X86Feature.Sse42) | X86Feature.Avx | X86Feature.F16c, X86Feature.F16c),
        ("avx512", X86Feature.Avx512, SseUpTo(X86Feature.Sse42) | X86Feature.Avx | X86Feature.Avx2 | X86Feature.Fma | X86Feature.F16c | X86Feature.Avx512, X86Feature.Avx512),
    ];

    /// <summary>The SSE generations up to and including <paramref name="top"/>, with the MMX they stand on.</summary>
    private static X86Feature SseUpTo(X86Feature top)
    {
        X86Feature all = X86Feature.Mmx | X86Feature.MmxExtensions | X86Feature.Sse | X86Feature.Sse2;
        foreach (X86Feature step in new[] { X86Feature.Sse3, X86Feature.Ssse3, X86Feature.Sse41, X86Feature.Sse42 })
        {
            all |= step;
            if (step == top) break;
        }
        return all;
    }

    /// <summary>
    /// The processor code is made for, and optimised for: one name, no
    /// separate tuning. A choice that depends on a processor's timing asks
    /// this; newer processors are the runtime's to detect and patch for.
    /// </summary>
    public string Name { get; }
    public string Fpu { get; }
    public X86Feature Features { get; }
    /// <summary>
    /// What --disable-NAME (and --fpu=none) forbids outright, beyond leaving
    /// it out of what --cpu promises: no code of it at all, not even a
    /// packed block's variant picked at run time on a processor that has it
    /// (Lowering's Sys.PackedChoose). --cpu alone is a floor; a disable is a ban.
    /// </summary>
    public X86Feature Banned { get; }
    /// <summary>The --disable-NAME switches Banned came from, to hand on with the profile's own arguments: a unit compiled from a contract alone (X86CodeGenerationContract.Arguments) keeps no disable of a feature its model lacks.</summary>
    public IReadOnlyList<string> BanArguments { get; }
    /// <summary>
    /// THE LEVEL-1 INSTRUCTION CACHE, in kilobytes, by which the compiler
    /// weighs code size against speed (Lowering.InlinesLargeExpansions): none
    /// on the 386 (external, if any); 8 on the 486 (unified) and the Pentium;
    /// 16 on the Pentium MMX; 8 on the Pentium Pro and 16 on the Pentium II
    /// and III; the Pentium 4's trace cache of 12K micro-ops, counted as 12;
    /// 32 on the K6 family; 64 on the Athlon, Athlon XP and Athlon 64, on
    /// the Geode LX, and on the 6x86MX (unified). From each one's datasheet
    /// (Intel 241428, 243502, 242690; AMD 20695, 21016, 22007, 25112, 33234;
    /// Cyrix 94329).
    /// </summary>
    public int InstructionCacheKb => Name switch
    {
        "386" => 0,
        "486" or "pentium" or "686" => 8,
        "pentium4" => 12,
        "pentium-mmx" or "pentium2" or "pentium3" => 16,
        "k6" or "k6-2" or "k6-3" or "k6-2+" or "k6-3+" => 32,
        "athlon" or "athlon-xp" or "athlon64" or "athlon64-sse3" or "amdfam10" or "geode-lx" or "6x86mx" => 64,
        // Intel's 64-bit families: Nocona the Pentium 4's trace cache; Core 2
        // to Skylake 32 KB (Intel 248966). The psABI levels name no processor
        // and are tuned as the K8 is, their 32 KB the smaller of the two lines.
        "nocona" => 12,
        "core2" or "nehalem" or "sandybridge" or "haswell" or "skylake" or "skylake-avx512"
            or "x86-64" or "x86-64-v2" or "x86-64-v3" or "x86-64-v4" => 32,
        // A model added without its cache is a decision not made: refused.
        _ => throw new InvalidOperationException($"no instruction cache size for --cpu={Name}"),
    };

    /// <summary>
    /// THE LAST CACHE A THREAD'S DATA STAYS IN, in kilobytes: the level-two
    /// cache of a processor with nothing beyond it, the level-two and the
    /// board's level-three where the K6-III's family has one, and a share
    /// of a shared level three -- what a copy that runs past it would only
    /// evict (the streaming copy's threshold, Selector._streamBytes, is
    /// three quarters of it, glibc's per-thread rule for
    /// non_temporal_threshold). None of its own on the 386; the boards' of
    /// the 486, the Pentium, the 6x86MX and the K6 and K6-2, by the sizes
    /// most of them carried (256 KB, 512 KB on Super Socket 7); 256 on the
    /// Pentium Pro, the Pentium III's Coppermine, the Athlon's Thunderbird
    /// and the Athlon XP's Palomino and Thoroughbred; 512 on the Pentium
    /// II, the Pentium 4's Northwood and the Athlon 64's Winchester and
    /// Venice; 128 on the Geode LX; 1 MB on Nocona; a Core 2's 4 MB shared
    /// by two cores, 2 MB a core; the Phenom's 512 KB with a quarter of
    /// its 6 MB; Nehalem to Skylake's 8 MB of a quad core's eight threads,
    /// 1 MB a thread. The psABI levels: v1 tuned as the K8, the others as
    /// the Intel line of their instructions. From each one's datasheet
    /// (Intel 242769, 243502, 244453, 249199, 302352, 320390; AMD 21918,
    /// 22007, 24319, 25112, 31116, 33234; Cyrix 94329).
    /// </summary>
    public int LastLevelCacheKb => Name switch
    {
        "386" => 0,
        "486" or "pentium" or "pentium-mmx" or "686" or "pentium3" or "6x86mx" or "athlon" or "athlon-xp" => 256,
        "geode-lx" => 128,
        "pentium2" or "pentium4" or "k6" or "k6-2" or "k6-2+" or "athlon64" or "athlon64-sse3" or "x86-64" => 512,
        "k6-3" or "k6-3+" or "nocona" or "nehalem" or "sandybridge" or "haswell" or "skylake" or "skylake-avx512"
            or "x86-64-v2" or "x86-64-v3" or "x86-64-v4" => 1024,
        "core2" or "amdfam10" => 2048,
        // A model added without its cache is a decision not made: refused.
        _ => throw new InvalidOperationException($"no last-level cache size for --cpu={Name}"),
    };

    /// <summary>Whether the processor runs x86-64 code (X86Feature.LongMode): what --target x86-64 asks of --cpu.</summary>
    public bool LongMode => Has(X86Feature.LongMode);

    public bool Pentium => Has(X86Feature.Pentium);
    public bool Mmx => Has(X86Feature.Mmx);
    public bool ThreeDNow => Has(X86Feature.ThreeDNow);
    public bool ThreeDNowExtended => Has(X86Feature.ThreeDNowExtended);
    /// <summary>FCMOVcc and FCOMI/FUCOMI(P), where the profile has them (X86Feature.FloatCmov).</summary>
    public bool FloatCmov => Has(X86Feature.FloatCmov);
    public global::Corsac.Lang.Ir.X86CodeGenerationContract Contract => new(Name, Fpu, Features);

    /// <summary>The contract of a unit compiled for this profile in long mode: SSE2 its floating point (X86CodeGenerationContract.LongMode).</summary>
    public global::Corsac.Lang.Ir.X86CodeGenerationContract LongModeContract(bool automaticPacked = true) => new(Name, "sse2", Features, automaticPacked);

    /// <summary>
    /// The profile --target x86-64 takes from the options: --cpu (x86-64's
    /// baseline when none is given), which has to be a model that runs
    /// x86-64 code; a refusal otherwise, naming why.
    /// </summary>
    public static X86Cpu ParseLongMode(IEnumerable<string> arguments)
    {
        X86Cpu cpu = Parse(arguments, "x86-64");
        if (!cpu.LongMode) throw new ArgumentException(X86Cpu.Model(cpu.Name).HasFlag(X86Feature.LongMode)
            ? $"--cpu={cpu.Name} without what x86-64 code stands on (CMOV, MMX, SSE, SSE2): a --disable- switch or --fpu=none took it away"
            : $"--cpu={cpu.Name} runs no x86-64 code: --target x86-64 takes a long-mode processor (athlon64, athlon64-sse3, amdfam10, nocona, core2 .. skylake-avx512, x86-64, x86-64-v2 .. v4)");
        if (cpu.Fpu != "x87") throw new ArgumentException("--fpu is for the i386 target: x86-64's floating point is SSE2");
        return cpu;
    }

    private X86Cpu(string name, string fpu, X86Feature features, X86Feature banned = X86Feature.None, IReadOnlyList<string>? banArguments = null)
    {
        Name = name; Fpu = fpu; Features = features; Banned = banned; BanArguments = banArguments ?? Array.Empty<string>();
    }

    /// <summary>Whether every feature in <paramref name="feature"/> may be used.</summary>
    public bool Has(X86Feature feature) => (Features & feature) == feature;

    private static string Normalize(string name) => name.ToLowerInvariant() switch
    {
        "386" or "i386" => "386", "486" or "i486" => "486",
        "586" or "i586" or "pentium" => "pentium",
        "pentium-mmx" or "pentium_mmx" => "pentium-mmx",
        "686" or "i686" or "pentium-pro" => "686",
        "pentium2" or "pentium-ii" => "pentium2",
        "pentium3" or "pentium-iii" => "pentium3",
        "pentium4" => "pentium4",
        "k6" => "k6", "k6-2" => "k6-2",
        "k6-3" or "k6-iii" => "k6-3",
        "k6-2+" => "k6-2+", "k6-3+" or "k6-iii+" => "k6-3+",
        "athlon" or "k7" or "athlon-tbird" => "athlon",
        "athlon-xp" or "athlon-4" or "athlon-mp" => "athlon-xp",
        // THE K8 BY REVISION, as GCC names them: the first ones -- the FX-51
        // and the single-core Opterons of revisions C and D, SledgeHammer and
        // ClawHammer -- without SSE3; from revision E -- the dual-core
        // Opteron 2xx (Italy) such as the 280 and 290, Denmark, the Athlon 64
        // X2 -- with it. Neither has CMPXCHG16B (revision F's) or, as GCC
        // reads them, LAHF/SAHF in long mode (the silicon has them from
        // revision D, but CPUID says so only where the BIOS set the bit AMD's
        // erratum 110 describes, AMD 25759: GCC leaves them out, and so do we).
        "athlon64" or "k8" or "opteron" or "athlon-fx" or "athlon64-fx" => "athlon64",
        "athlon64-sse3" or "k8-sse3" or "opteron-sse3" => "athlon64-sse3",
        "amdfam10" or "barcelona" or "k10" or "phenom" => "amdfam10",
        "nocona" => "nocona", "core2" => "core2",
        "nehalem" or "corei7" or "westmere" => "nehalem",
        "sandybridge" or "ivybridge" => "sandybridge",
        "haswell" or "broadwell" => "haswell",
        "skylake" => "skylake", "skylake-avx512" => "skylake-avx512",
        "x86-64" or "x86-64-v1" => "x86-64",
        "x86-64-v2" => "x86-64-v2", "x86-64-v3" => "x86-64-v3", "x86-64-v4" => "x86-64-v4",
        "6x86mx" or "m2" or "mii" or "cyrix-mii" => "6x86mx",
        "geode-lx" or "geodelx" => "geode-lx",
        _ => throw new ArgumentException("Unknown x86 CPU: " + name),
    };

    /// <summary>
    /// What a model may use, each processor written as the one before it and
    /// what it added; null for a name that is no model's. The 686 is the
    /// Pentium Pro. A model with LongMode runs x86-64 code too: the same name
    /// is its profile for either target (--target x86 or x86-64), whose
    /// floating point is the x87 or SSE2.
    /// </summary>
    private static X86Feature? Defaults(string name) => name switch
    {
        "386" => X86Feature.None,
        "486" => X86Feature.I486,
        "pentium" => Model("486") | X86Feature.Pentium,
        "pentium-mmx" or "k6" => Model("pentium") | X86Feature.Mmx,
        "686" => Model("pentium") | X86Feature.Cmov | X86Feature.FloatCmov,
        "pentium2" => Model("686") | X86Feature.Mmx,
        "pentium3" => Model("pentium2") | X86Feature.MmxExtensions | X86Feature.Sse,
        "pentium4" => Model("pentium3") | X86Feature.Sse2,
        "k6-2" or "k6-3" => Model("k6") | X86Feature.ThreeDNow,
        "k6-2+" or "k6-3+" => Model("k6-2") | X86Feature.ThreeDNowExtended,
        "athlon" => Model("k6-2+") | X86Feature.MmxExtensions | X86Feature.Cmov | X86Feature.FloatCmov,
        "athlon-xp" => Model("athlon") | X86Feature.Sse,
        "athlon64" => Model("athlon-xp") | X86Feature.Sse2 | X86Feature.LongMode,
        "athlon64-sse3" => Model("athlon64") | X86Feature.Sse3,
        // THE K10 (GCC's amdfam10, AMD 31116): SSE4a, POPCNT and LZCNT (ABM),
        // CMPXCHG16B and LAHF/SAHF in long mode.
        "amdfam10" => Model("athlon64-sse3") | X86Feature.Sse4a | X86Feature.Popcnt | X86Feature.Lzcnt
            | X86Feature.Cmpxchg16b | X86Feature.Lahf64,
        // INTEL'S 64-BIT FAMILIES, GCC's sets (i386.h's PTA_ lists): no 3DNow!.
        "nocona" => Model("pentium4") | X86Feature.LongMode | X86Feature.Sse3 | X86Feature.Cmpxchg16b,
        "core2" => Model("nocona") | X86Feature.Ssse3 | X86Feature.Lahf64,
        "nehalem" => Model("core2") | X86Feature.Sse41 | X86Feature.Sse42 | X86Feature.Popcnt,
        "sandybridge" => Model("nehalem") | X86Feature.Avx,
        "haswell" => Model("sandybridge") | X86Feature.Avx2 | X86Feature.Fma | X86Feature.F16c
            | X86Feature.Bmi1 | X86Feature.Bmi2 | X86Feature.Lzcnt | X86Feature.Movbe,
        "skylake" => Model("haswell"),
        "skylake-avx512" => Model("skylake") | X86Feature.Avx512,
        // THE psABI's MICRO-ARCHITECTURE LEVELS (x86-64 psABI, "Micro-architecture
        // levels"): instructions only. v1 is long mode's own set -- the K8's
        // without 3DNow! -- and each level adds its list.
        "x86-64" => Model("686") | X86Feature.Mmx | X86Feature.MmxExtensions | X86Feature.Sse | X86Feature.Sse2 | X86Feature.LongMode,
        "x86-64-v2" => Model("x86-64") | X86Feature.Cmpxchg16b | X86Feature.Lahf64 | X86Feature.Popcnt
            | X86Feature.Sse3 | X86Feature.Ssse3 | X86Feature.Sse41 | X86Feature.Sse42,
        "x86-64-v3" => Model("x86-64-v2") | X86Feature.Avx | X86Feature.Avx2 | X86Feature.Bmi1 | X86Feature.Bmi2
            | X86Feature.F16c | X86Feature.Fma | X86Feature.Lzcnt | X86Feature.Movbe,
        "x86-64-v4" => Model("x86-64-v3") | X86Feature.Avx512,
        // CYRIX'S 6x86MX (the MII): a Pentium MMX's set with CMOVcc, and no
        // FCMOVcc or FCOMI, which its x87 never had -- whatever CPUID's one
        // CMOV bit says, as a Pentium Pro's would.
        "6x86mx" => Model("pentium-mmx") | X86Feature.Cmov,
        // AMD'S GEODE LX (LX 600 to LX 900, AMD 33234E/H): the Pentium Pro's set
        // with the K6-2+'s MMX, 3DNow! and its extensions, and the MMX
        // extensions -- an Athlon's instructions with none of its SSE.
        // FCMOVcc, FCOMI and FUCOMI are its x87's (33234H Table 8-29), as
        // CPUID's CMOV bit says; Linux's flags there are cmov, mmx, mmxext,
        // 3dnow and 3dnowext. It reports family 5, model 10, has no PAE, and
        // no long NOP (0F 1F), which nothing here emits.
        "geode-lx" => Model("k6-2+") | X86Feature.MmxExtensions | X86Feature.Cmov | X86Feature.FloatCmov,
        _ => null,
    };

    /// <summary>The features the model <paramref name="name"/> (Normalize's answer, not an alias) may use.</summary>
    public static X86Feature Model(string name) => Defaults(name) ?? throw new ArgumentException("Unknown x86 CPU: " + name);

    /// <summary>Whether <paramref name="name"/> is a model's own name (Normalize's answer), either target's.</summary>
    public static bool IsModel(string name) => Defaults(name) is not null;

    /// <summary>Whether <paramref name="option"/> is one of the --enable-NAME/--disable-NAME switches.</summary>
    public static bool IsSwitch(string option) => Switches.Any(s => option == "--enable-" + s.Name || option == "--disable-" + s.Name);

    /// <summary>
    /// <paramref name="features"/> with whatever depends on a missing feature
    /// taken away: 3DNow! and the MMX extensions need MMX, SSE the MMX
    /// extensions (its integer half), the 3DNow!
    /// extensions need 3DNow!, SSE2 needs SSE, FCMOV/FCOMI need CMOV, and
    /// without an FPU nothing of the x87's is left (NeedsFpu). A lone 387
    /// keeps the x87 but never had FCMOV or FCOMI. Long mode needs x86-64's
    /// baseline (CMOV, MMX, SSE, SSE2); each SSE generation the one below it,
    /// SSE4a SSE3, AVX SSE4.2, the VEX families AVX, AVX-512 AVX2, FMA, F16C.
    /// </summary>
    public static X86Feature Settled(X86Feature features, string fpu)
    {
        if (fpu == "none") features &= ~NeedsFpu;
        if (fpu == "387") features &= ~X86Feature.FloatCmov;
        if ((features & X86Feature.Mmx) == 0) features &= ~(X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended | X86Feature.MmxExtensions);
        if ((features & X86Feature.ThreeDNow) == 0) features &= ~X86Feature.ThreeDNowExtended;
        if ((features & X86Feature.Sse) == 0) features &= ~X86Feature.Sse2;
        if ((features & X86Feature.MmxExtensions) == 0) features &= ~(X86Feature.Sse | X86Feature.Sse2);
        if ((features & X86Feature.Cmov) == 0) features &= ~X86Feature.FloatCmov;
        // LONG MODE STANDS ON THE BASELINE x86-64 code is compiled for -- the
        // Pentium's set, CMOV, MMX, SSE and SSE2 (the psABI's x86-64-v1) --
        // and a profile without one of them is no long-mode one.
        const X86Feature longModeNeeds = X86Feature.I486 | X86Feature.Pentium | X86Feature.Cmov | X86Feature.Mmx | X86Feature.Sse | X86Feature.Sse2;
        if ((features & longModeNeeds) != longModeNeeds) features &= ~X86Feature.LongMode;
        // Each SSE generation on the one below it, the VEX families on AVX.
        if ((features & X86Feature.Sse2) == 0) features &= ~AboveSse2;
        if ((features & X86Feature.Sse3) == 0) features &= ~(AboveSse2 & ~X86Feature.Sse3);
        if ((features & X86Feature.Ssse3) == 0) features &= ~(X86Feature.Sse41 | X86Feature.Sse42 | X86Feature.Avx | AboveAvx);
        if ((features & X86Feature.Sse41) == 0) features &= ~(X86Feature.Sse42 | X86Feature.Avx | AboveAvx);
        if ((features & X86Feature.Sse42) == 0) features &= ~(X86Feature.Avx | AboveAvx);
        if ((features & X86Feature.Avx) == 0) features &= ~AboveAvx;
        if ((features & (X86Feature.Avx2 | X86Feature.Fma | X86Feature.F16c)) != (X86Feature.Avx2 | X86Feature.Fma | X86Feature.F16c)) features &= ~X86Feature.Avx512;
        return features;
    }

    /// <summary>
    /// The switches that make <paramref name="features"/> out of the model
    /// <paramref name="cpu"/>'s own set: --disable-NAME for each switch's
    /// feature the model has and the set lacks, --enable-NAME for each one
    /// the set has beyond the model. Parse reads them back to the same set.
    /// </summary>
    public static IEnumerable<string> SwitchArguments(string cpu, X86Feature features)
    {
        X86Feature model = Model(cpu);
        foreach (var s in Switches)
            if ((model & s.Feature) != 0 && (features & s.Feature) == 0) yield return "--disable-" + s.Name;
        foreach (var s in Switches)
            if ((model & s.Feature) == 0 && (features & s.Feature) != 0) yield return "--enable-" + s.Name;
    }

    /// <summary>
    /// The profile the options name. <paramref name="defaultCpu"/> is the
    /// target's when no --cpu is given: the 486 for i386, x86-64 for long mode.
    /// </summary>
    public static X86Cpu Parse(IEnumerable<string> arguments, string defaultCpu = "486")
    {
        string[] args = arguments.ToArray();
        string cpu = defaultCpu, fpu = "";
        // Each switch's last word: enabled, disabled, or not said.
        bool?[] said = new bool?[Switches.Length];
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (option.StartsWith("--cpu=") || option.StartsWith("--fpu="))
            {
                int equals = option.IndexOf('=');
                if (option[..equals] == "--cpu") cpu = Normalize(option[(equals + 1)..]);
                else fpu = option[(equals + 1)..].ToLowerInvariant();
            }
            else if (option is "--cpu" or "--fpu")
            {
                if (++i == args.Length) throw new ArgumentException(option + " requires a CPU name");
                if (option == "--cpu") cpu = Normalize(args[i]);
                else fpu = args[i].ToLowerInvariant();
            }
            else
            {
                int which = Array.FindIndex(Switches, s => option == "--enable-" + s.Name || option == "--disable-" + s.Name);
                if (which >= 0) said[which] = option.StartsWith("--enable-");
            }
        }
        if (fpu.Length == 0) fpu = cpu == "386" ? "none" : "x87";
        if (fpu is not ("none" or "387" or "x87")) throw new ArgumentException("Unknown x86 FPU: " + fpu);
        // AN EXPLICIT ENABLE IS A PROMISE the processor has the extension, and
        // one of the x87's cannot be kept on a machine without an x87: refused
        // rather than quietly dropped. CMOV alone is integer and stays.
        for (int i = 0; i < Switches.Length; i++)
            if (fpu == "none" && said[i] == true && (Switches[i].Feature & NeedsFpu) != 0)
                throw new ArgumentException("Explicit MMX/3DNow!/SSE enablement conflicts with --fpu=none");
        // A DISABLE IS THE STRONGER: an enable that would bring what another
        // switch disables is not applied at all, so --enable-3dnow brings no
        // MMX back after --disable-mmx, nor --enable-sse2 the MMX extensions
        // after --disable-sse.
        X86Feature features = Model(cpu), disabled = X86Feature.None;
        for (int i = 0; i < Switches.Length; i++)
            if (said[i] == false) disabled |= Switches[i].Feature;
        for (int i = 0; i < Switches.Length; i++)
            if (said[i] == true && (Switches[i].Enables & disabled) == 0) features |= Switches[i].Enables;
        X86Feature banned = fpu == "none" ? NeedsFpu : X86Feature.None;
        for (int i = 0; i < Switches.Length; i++)
            if (said[i] == false) { features &= ~Switches[i].Disables; banned |= Switches[i].Disables; }
        string[] bans = Switches.Where((s, i) => said[i] == false).Select(s => "--disable-" + s.Name).ToArray();
        return new X86Cpu(cpu, fpu, Settled(features, fpu), banned, bans);
    }
}
