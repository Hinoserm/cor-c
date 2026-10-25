using System.Globalization;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.X86;

namespace Corsac.Lang.Ir;

/// <summary>Instruction permissions used to regenerate a unit, independent of its calling ABI.</summary>
public sealed record X86CodeGenerationContract(string Cpu, string Fpu, X86Feature Features, bool AutomaticPacked = true)
{
    public const string SectionName = ".corsac.cpu";

    private void Validate()
    {
        // K8 is long mode: SSE2 is its floating point, and nothing older mixes with it.
        if ((Cpu == "k8") != (Fpu == "sse2") || (Cpu == "k8" && Features != X86Feature.None))
            throw new ElfFormatException("invalid x86 code-generation contract");
        if (Cpu == "k8") return;
        // A 32-BIT PROFILE IS WHAT --cpu AND THE SWITCHES SAY: a model
        // X86Cpu knows, an FPU, features with nothing left that depends
        // on a missing one (X86Cpu.Settled: no 3DNow! without MMX, no SSE2
        // without SSE, nothing of the x87's without one), and a set Arguments
        // writes and X86Cpu.Parse reads back as it is -- which keeps the
        // extensions no switch grants, the five 3DNow! ones, to the models
        // that have them, and lets LTO restore the unit's permissions exactly.
        if (!X86Cpu.IsModel(Cpu) || Fpu is not ("none" or "387" or "x87")
            || X86Cpu.Settled(Features, Fpu) != Features || X86Cpu.Parse(Arguments()).Features != Features)
            throw new ElfFormatException("invalid x86 code-generation contract");
    }

    public void Attach(ObjectFile obj)
    {
        Validate();
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("duplicate x86 code-generation contract");
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(Encoding.ASCII.GetBytes(string.Join('\n', "CCPU1", Cpu, Fpu,
            ((ushort)Features).ToString("x4"), AutomaticPacked ? "1" : "0")));
        obj.Sections.Add(section);
    }

    public static X86CodeGenerationContract? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Bytes.Count > 128 || sections[0].Relocs.Count != 0)
            throw new ElfFormatException("invalid x86 code-generation contract section");
        string[] fields = Encoding.ASCII.GetString(sections[0].Bytes.ToArray()).Split('\n');
        if (fields.Length != 5 || fields[0] != "CCPU1" || fields[3].Length != 4 || !fields[3].All(Uri.IsHexDigit)
            || fields[4] is not ("0" or "1"))
            throw new ElfFormatException("unsupported x86 code-generation contract");
        var contract = new X86CodeGenerationContract(fields[1], fields[2],
            (X86Feature)ushort.Parse(fields[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture), fields[4] == "1");
        contract.Validate(); return contract;
    }

    /// <summary>The compiler's own options for this profile, which X86Cpu.Parse reads back to it.</summary>
    public string[] Arguments() => ["--cpu=" + Cpu, "--fpu=" + Fpu,
        .. Cpu == "k8" ? Array.Empty<string>() : X86Cpu.SwitchArguments(Cpu, Features)];

    public static void ValidateTarget(IEnumerable<(string Name, ObjectFile Object)> inputs, X86CodeGenerationContract target)
    {
        target.Validate();
        foreach (var input in inputs)
        {
            X86CodeGenerationContract? required = Read(input.Object);
            if (required is null) continue; // Foreign objects carry no owned target declaration.
            // A UNIT RUNS WHERE EVERYTHING IT MAY USE IS THERE: its features a
            // subset of the target's, not an earlier generation, because the
            // processors are no line -- the K6-2 has 3DNow! and no CMOV, the
            // Pentium Pro CMOV and no MMX. Code for an x87 needs one, and a
            // long-mode unit a long-mode target.
            if ((required.Features & ~target.Features) != 0 || (required.Fpu != "none" && target.Fpu == "none")
                || (required.Cpu == "k8" && target.Cpu != "k8"))
                throw new ElfFormatException(input.Name + ": instruction requirements exceed selected CPU/FPU target " + target.Cpu);
        }
    }

    /// <summary>Two units whose instruction permissions differ, or null when every unit's are one.</summary>
    public static (string First, string FirstCpu, string Second, string SecondCpu)? Disagreeing(IEnumerable<(string Name, ObjectFile Object)> inputs)
    {
        (string Name, X86CodeGenerationContract Contract)? first = null;
        foreach (var input in inputs)
        {
            if (Read(input.Object) is not { } contract) continue;
            if (first is not { } had) { first = (input.Name, contract); continue; }
            if (contract.Features != had.Contract.Features || contract.Fpu != had.Contract.Fpu || (contract.Cpu == "k8") != (had.Contract.Cpu == "k8"))
                return (had.Name, had.Contract.Cpu, input.Name, contract.Cpu);
        }
        return null;
    }

    public static void ValidateRegeneration(ObjectFile original, ObjectFile regenerated)
    {
        if (Read(original) != Read(regenerated))
            throw new ElfFormatException("IR backend changed the unit's CPU/FPU instruction permissions");
    }
}
