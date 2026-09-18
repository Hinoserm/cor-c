namespace Corsac.Lang.X86;

/// <summary>Instruction permission and tuning are separate, immutable per invocation.</summary>
public sealed class X86Cpu
{
    public string Name { get; }
    public string Tune { get; }
    public string Fpu { get; }
    public bool Pentium { get; }
    public bool Mmx { get; }
    public bool ThreeDNow { get; }
    public bool ThreeDNowExtended { get; }
    public global::Corsac.Lang.Ir.X86CodeGenerationContract Contract => new(Name, Tune, Fpu, Mmx, ThreeDNow, ThreeDNowExtended);

    private X86Cpu(string name, string tune, string fpu, bool pentium, bool mmx, bool now, bool extended)
    {
        Name = name; Tune = tune; Pentium = pentium;
        Fpu = fpu;
        Mmx = mmx; ThreeDNow = now; ThreeDNowExtended = extended;
    }

    private static string Normalize(string name) => name.ToLowerInvariant() switch
    {
        "386" or "i386" => "386", "486" or "i486" => "486",
        "586" or "i586" or "pentium" => "pentium",
        "pentium-mmx" or "pentium_mmx" => "pentium-mmx",
        "686" or "i686" or "pentium-pro" => "686",
        "k6" => "k6", "k6-2" => "k6-2",
        "k6-3" or "k6-iii" => "k6-3",
        "k6-2+" => "k6-2+", "k6-3+" or "k6-iii+" => "k6-3+",
        _ => throw new ArgumentException("Unknown x86 CPU: " + name),
    };

    public static X86Cpu Parse(IEnumerable<string> arguments)
    {
        string[] args = arguments.ToArray();
        string cpu = "486", tune = "", fpu = "";
        bool? mmx = null, now = null;
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
            else if (option == "--enable-mmx") mmx = true;
            else if (option == "--disable-mmx") mmx = false;
            else if (option == "--enable-3dnow") now = true;
            else if (option == "--disable-3dnow") now = false;
        }
        if (fpu.Length == 0) fpu = cpu == "386" ? "none" : "x87";
        if (fpu is not ("none" or "387" or "x87")) throw new ArgumentException("Unknown x86 FPU: " + fpu);
        if (fpu == "none" && (mmx == true || now == true)) throw new ArgumentException("Explicit MMX/3DNow! enablement conflicts with --fpu=none");
        bool hasNow = now ?? (cpu is "k6-2" or "k6-3" or "k6-2+" or "k6-3+");
        bool hasMmx = mmx ?? (cpu is "pentium-mmx" or "k6" or "k6-2" or "k6-3" or "k6-2+" or "k6-3+" || hasNow);
        if (fpu == "none") hasMmx = false;
        if (!hasMmx) hasNow = false;
        return new X86Cpu(cpu, tune.Length == 0 ? cpu : tune, fpu, cpu is not ("386" or "486"),
            hasMmx, hasNow, hasNow && cpu is ("k6-2+" or "k6-3+"));
    }
}
