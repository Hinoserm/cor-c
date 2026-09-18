using System.Text;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Ir;

/// <summary>Instruction permissions used to regenerate a unit, independent of its calling ABI.</summary>
public sealed record X86CodeGenerationContract(string Cpu, string Tune, string Fpu, bool Mmx, bool ThreeDNow, bool Extended, bool AutomaticPacked = true)
{
    public const string SectionName = ".corsac.cpu";
    private static readonly HashSet<string> Models = new(StringComparer.Ordinal)
        { "386", "486", "pentium", "pentium-mmx", "686", "k6", "k6-2", "k6-3", "k6-2+", "k6-3+" };

    private void Validate()
    {
        if (!Models.Contains(Cpu) || !Models.Contains(Tune) || Fpu is not ("none" or "387" or "x87")
            || (Fpu == "none" && (Mmx || ThreeDNow)) || (ThreeDNow && !Mmx)
            || (Extended && (!ThreeDNow || Cpu is not ("k6-2+" or "k6-3+"))))
            throw new ElfFormatException("invalid x86 code-generation contract");
    }

    public void Attach(ObjectFile obj)
    {
        Validate();
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("duplicate x86 code-generation contract");
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(Encoding.ASCII.GetBytes(string.Join('\n', "CCPU2", Cpu, Tune, Fpu,
            Mmx ? "1" : "0", ThreeDNow ? "1" : "0", Extended ? "1" : "0", AutomaticPacked ? "1" : "0")));
        obj.Sections.Add(section);
    }

    public static X86CodeGenerationContract? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Bytes.Count > 128 || sections[0].Relocs.Count != 0)
            throw new ElfFormatException("invalid x86 code-generation contract section");
        string[] fields = Encoding.ASCII.GetString(sections[0].Bytes.ToArray()).Split('\n');
        if (fields.Length != 8 || fields[0] != "CCPU2" || fields.Skip(4).Any(field => field is not ("0" or "1")))
            throw new ElfFormatException("unsupported x86 code-generation contract");
        var contract = new X86CodeGenerationContract(fields[1], fields[2], fields[3], fields[4] == "1", fields[5] == "1", fields[6] == "1", fields[7] == "1");
        contract.Validate(); return contract;
    }

    public string[] Arguments() => ["--cpu=" + Cpu, "--tune=" + Tune, "--fpu=" + Fpu,
        Mmx ? "--enable-mmx" : "--disable-mmx", ThreeDNow ? "--enable-3dnow" : "--disable-3dnow"];

    public static void ValidateTarget(IEnumerable<(string Name, ObjectFile Object)> inputs, X86CodeGenerationContract target)
    {
        target.Validate();
        static int Generation(string cpu) => cpu switch { "386" => 3, "486" => 4, "686" => 6, _ => 5 };
        foreach (var input in inputs)
        {
            X86CodeGenerationContract? required = Read(input.Object);
            if (required is null) continue; // Foreign objects carry no owned target declaration.
            if (Generation(required.Cpu) > Generation(target.Cpu) || (required.Fpu != "none" && target.Fpu == "none")
                || (required.Mmx && !target.Mmx) || (required.ThreeDNow && !target.ThreeDNow) || (required.Extended && !target.Extended))
                throw new ElfFormatException(input.Name + ": instruction requirements exceed selected CPU/FPU target " + target.Cpu);
        }
    }

    public static void ValidateRegeneration(ObjectFile original, ObjectFile regenerated)
    {
        if (Read(original) != Read(regenerated))
            throw new ElfFormatException("IR backend changed the unit's CPU/FPU instruction permissions");
    }
}
