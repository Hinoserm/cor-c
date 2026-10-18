namespace Corsac.Lang.X86;

/// <summary>
/// The instruction families a 32-bit profile may use beyond the 386's. A
/// processor is a set of these, not a point on one line: the K6-2 has 3DNow!
/// and no CMOV, the Pentium Pro CMOV and no MMX, so which code runs where is
/// a question of one set holding another (X86CodeGenerationContract.ValidateTarget).
/// </summary>
[Flags]
public enum X86Feature : ushort
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
}

/// <summary>Shared compiler/linker instruction permissions; tuning never grants instructions.</summary>
public sealed class X86Cpu
{
    // What lives in the x87's registers or its state: MMX aliases the stack,
    // FCMOV and FCOMI are x87 instructions, and SSE's state is saved with it.
    // --fpu=none takes all of it away.
    private const X86Feature NeedsFpu = X86Feature.Mmx | X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended
        | X86Feature.MmxExtensions | X86Feature.FloatCmov | X86Feature.Sse | X86Feature.Sse2;

    /// <summary>
    /// The --enable-NAME and --disable-NAME switches: the feature whose
    /// presence a switch decides, what enabling it brings with it, and what
    /// disabling it takes away. Enabling 3DNow! brings MMX and enabling SSE2
    /// brings SSE, as no processor has one without the other; SSE brings the
    /// MMX extensions, its own integer half.
    /// </summary>
    private static readonly (string Name, X86Feature Feature, X86Feature Enables, X86Feature Disables)[] Switches =
    [
        ("mmx", X86Feature.Mmx, X86Feature.Mmx,
            X86Feature.Mmx | X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended | X86Feature.MmxExtensions),
        ("3dnow", X86Feature.ThreeDNow, X86Feature.Mmx | X86Feature.ThreeDNow, X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended),
        ("cmov", X86Feature.Cmov, X86Feature.Cmov | X86Feature.FloatCmov, X86Feature.Cmov | X86Feature.FloatCmov),
        ("sse", X86Feature.Sse, X86Feature.MmxExtensions | X86Feature.Sse, X86Feature.Sse | X86Feature.Sse2),
        ("sse2", X86Feature.Sse2, X86Feature.MmxExtensions | X86Feature.Sse | X86Feature.Sse2, X86Feature.Sse2),
    ];

    public string Name { get; }
    public string Tune { get; }
    public string Fpu { get; }
    public X86Feature Features { get; }
    public bool Pentium => Has(X86Feature.Pentium);
    public bool Mmx => Has(X86Feature.Mmx);
    public bool ThreeDNow => Has(X86Feature.ThreeDNow);
    public bool ThreeDNowExtended => Has(X86Feature.ThreeDNowExtended);
    /// <summary>FCMOVcc and FCOMI/FUCOMI(P), where the profile has them (X86Feature.FloatCmov).</summary>
    public bool FloatCmov => Has(X86Feature.FloatCmov);
    public global::Corsac.Lang.Ir.X86CodeGenerationContract Contract => new(Name, Tune, Fpu, Features);

    private X86Cpu(string name, string tune, string fpu, X86Feature features)
    {
        Name = name; Tune = tune; Fpu = fpu; Features = features;
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
        "athlon64" => "athlon64",
        "6x86mx" or "m2" or "mii" or "cyrix-mii" => "6x86mx",
        _ => throw new ArgumentException("Unknown x86 CPU: " + name),
    };

    /// <summary>
    /// What a model may use, each processor written as the one before it and
    /// what it added; null for a name that is no model's. The 686 is the
    /// Pentium Pro. The athlon64 is a K8 running 32-bit code with an x87; the
    /// name k8 is the long-mode target's, whose floating point is SSE2.
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
        "athlon64" => Model("athlon-xp") | X86Feature.Sse2,
        // CYRIX'S 6x86MX (the MII): a Pentium MMX's set with CMOVcc, and no
        // FCMOVcc or FCOMI, which its x87 never had -- whatever CPUID's one
        // CMOV bit says, as a Pentium Pro's would.
        "6x86mx" => Model("pentium-mmx") | X86Feature.Cmov,
        _ => null,
    };

    /// <summary>The features the model <paramref name="name"/> (Normalize's answer, not an alias) may use.</summary>
    public static X86Feature Model(string name) => Defaults(name) ?? throw new ArgumentException("Unknown x86 CPU: " + name);

    /// <summary>Whether <paramref name="name"/> is a 32-bit model's own name.</summary>
    public static bool IsModel(string name) => Defaults(name) is not null;

    /// <summary>Whether <paramref name="option"/> is one of the --enable-NAME/--disable-NAME switches.</summary>
    public static bool IsSwitch(string option) => Switches.Any(s => option == "--enable-" + s.Name || option == "--disable-" + s.Name);

    /// <summary>
    /// <paramref name="features"/> with whatever depends on a missing feature
    /// taken away: 3DNow! and the MMX extensions need MMX, the 3DNow!
    /// extensions need 3DNow!, SSE2 needs SSE, FCMOV/FCOMI need CMOV, and
    /// without an FPU nothing of the x87's is left (NeedsFpu). A lone 387
    /// keeps the x87 but never had FCMOV or FCOMI.
    /// </summary>
    public static X86Feature Settled(X86Feature features, string fpu)
    {
        if (fpu == "none") features &= ~NeedsFpu;
        if (fpu == "387") features &= ~X86Feature.FloatCmov;
        if ((features & X86Feature.Mmx) == 0) features &= ~(X86Feature.ThreeDNow | X86Feature.ThreeDNowExtended | X86Feature.MmxExtensions);
        if ((features & X86Feature.ThreeDNow) == 0) features &= ~X86Feature.ThreeDNowExtended;
        if ((features & X86Feature.Sse) == 0) features &= ~X86Feature.Sse2;
        if ((features & X86Feature.Cmov) == 0) features &= ~X86Feature.FloatCmov;
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

    public static X86Cpu Parse(IEnumerable<string> arguments)
    {
        string[] args = arguments.ToArray();
        string cpu = "486", tune = "", fpu = "";
        // Each switch's last word: enabled, disabled, or not said.
        bool?[] said = new bool?[Switches.Length];
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (option.StartsWith("--cpu=") || option.StartsWith("--tune=") || option.StartsWith("--fpu="))
            {
                int equals = option.IndexOf('=');
                if (option[..equals] == "--cpu") cpu = Normalize(option[(equals + 1)..]);
                else if (option[..equals] == "--tune") tune = Normalize(option[(equals + 1)..]);
                else fpu = option[(equals + 1)..].ToLowerInvariant();
            }
            else if (option is "--cpu" or "--tune" or "--fpu")
            {
                if (++i == args.Length) throw new ArgumentException(option + " requires a CPU name");
                if (option == "--cpu") cpu = Normalize(args[i]);
                else if (option == "--tune") tune = Normalize(args[i]); else fpu = args[i].ToLowerInvariant();
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
        for (int i = 0; i < Switches.Length; i++)
            if (said[i] == false) features &= ~Switches[i].Disables;
        return new X86Cpu(cpu, tune.Length == 0 ? cpu : tune, fpu, Settled(features, fpu));
    }
}
