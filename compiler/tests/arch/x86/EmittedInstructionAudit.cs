#nullable enable
using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Tests.X86;

/// <summary>
/// The emitted-code audit (Isa, Encoder.Audit): an instruction the selected
/// processor lacks is refused when its function is encoded, whatever put it
/// there, and one the source named after its own processor check is let
/// through. The machine instructions are built by hand, as a selection bug
/// would build them, so nothing before the encoder has filtered them out.
/// </summary>
internal static class EmittedInstructionAudit
{
    public static void Run(Action<bool, string> check)
    {
        X86Cpu selected = Target.X86.X86Profile;
        try
        {
            Target.X86.X86Profile = X86Cpu.Parse(["--cpu=486"]);
            string? mmx = Encode(new MInstr(MOp.MmxZero), automaticPacked: true);
            check(mmx is not null && mmx.StartsWith("audited: MmxZero") && mmx.Contains("needs MMX") && mmx.Contains("--cpu=486"),
                "an MMX instruction under --cpu=486 is refused by the emitted-code audit (" + mmx + ")");
            check(Encode(new MInstr(MOp.Cpuid), automaticPacked: true) is { } cpuid && cpuid.Contains("needs Pentium"),
                "a CPUID the compiler chose is refused under --cpu=486");
            check(Encode(new MInstr(MOp.Cpuid) { Probed = true }, automaticPacked: true) is null,
                "a CPUID the source named after its own check is let through under --cpu=486");

            Target.X86.X86Profile = X86Cpu.Parse(["--cpu=pentium-mmx"]);
            check(Encode(new MInstr(MOp.MmxZero), automaticPacked: true) is null,
                "an MMX instruction under --cpu=pentium-mmx, in hosted code, is let through");
            check(Encode(new MInstr(MOp.MmxZero), automaticPacked: false) is { } owned && owned.Contains("x87 state"),
                "an MMX instruction in code that does not own the x87 state is refused whatever the processor");
            check(Encode(new MInstr(MOp.Femms), automaticPacked: true) is { } femms && femms.Contains("needs 3DNow!"),
                "FEMMS under --cpu=pentium-mmx is refused: it is 3DNow!'s");

            // THROUGH THE BACKEND: the refusal is an error of the compile,
            // naming the function, and not a crash. A 386 has no XADD, and
            // an atomic add is chosen by selection, never Probed.
            Target.X86.X86Profile = X86Cpu.Parse(["--cpu=386", "--fpu=387"]);
            Module module = new("audit");
            Function f = new("fetch_add", IrType.I32) { Exported = true };
            module.Functions.Add(f);
            Builder b = new(f, f.NewBlock("entry"));
            VReg old = b.Emit(Opcode.AtomicAdd, b.Reg(IrType.I32), new SymOperand("counter"), new ImmOperand(1, IrType.I32)).Dest!;
            b.Ret(RegOperand.Of(old));
            module.Data.Add(new DataItem("counter", new byte[4]) { Zero = true });
            List<string> errors = new();
            new X86Backend().Generate(module, errors);
            check(errors.Any(e => e.StartsWith("fetch_add: Xadd") && e.Contains("needs 486") && e.Contains("--cpu=386")),
                "an XADD under --cpu=386 is a compile error naming its function (" + string.Join("; ", errors) + ")");
        }
        finally
        {
            Target.X86.X86Profile = selected;
        }
    }

    /// <summary>One instruction and a RET as a function of their own, encoded: the audit's refusal, or null.</summary>
    private static string? Encode(MInstr instruction, bool automaticPacked)
    {
        MFunction m = new(new Function("audited", IrType.Void)) { AutomaticPacked = automaticPacked };
        MBlock entry = m.NewBlock("entry");
        entry.Instrs.Add(instruction);
        entry.Instrs.Add(new MInstr(MOp.Ret));
        try
        {
            new Encoder(new Section(".text", SectionKind.Code)).Encode(m);
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }
}
