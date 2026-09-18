#nullable enable
using System.Text;

namespace Corsac.Lang.X86;

/// <summary>
/// Prints allocated machine code as Intel-syntax assembly, close enough to
/// NASM that a human can set it beside `objdump -d -M intel` of the bytes.
/// Nothing consumes this; it exists to be read.
/// </summary>
internal static class AsmText
{
    public static void Print(StringBuilder sb, MFunction m)
    {
        sb.Append(m.Source.Exported ? "global " : "; local ").Append(m.Source.Name).Append('\n');
        sb.Append(m.Source.Name).Append(":\n");
        foreach (MBlock b in m.Blocks)
        {
            if (!ReferenceEquals(b, m.Blocks[0]))
            {
                sb.Append('.').Append(b.Name).Append(":\n");
            }
            foreach (MInstr i in b.Instrs)
            {
                foreach (string line in Lines(m, i))
                {
                    sb.Append("        ").Append(line).Append('\n');
                }
            }
        }
        sb.Append('\n');
    }

    private static string RegName(int id, int width) => width switch
    {
        1 => new[] { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" }[id],
        2 => new[] { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" }[id],
        _ => new[] { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" }[id],
    };

    private static string Size(int width) => width switch { 1 => "byte", 2 => "word", 8 => "qword", _ => "dword" };

    private static string Op(MOperand o, int width, bool sized)
    {
        switch (o)
        {
            case MReg r when r.IsPhys:
                return RegName(r.Id, width);
            case MReg r:
                return $"v{r.Id}";
            case MMem m:
                return sized ? $"{Size(width)} {m}" : m.ToString();
            case MImm imm:
                return imm.ToString();
            case MLabel l:
                return "." + l.Target.Name;
            default:
                return o.ToString() ?? "?";
        }
    }

    private static IEnumerable<string> Lines(MFunction m, MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Prologue:
                yield return "push ebp";
                yield return "mov ebp, esp";
                if (m.Frame.Size > 0)
                {
                    yield return $"sub esp, {m.Frame.Size}";
                }
                foreach (Gpr g in m.SavedRegs)
                {
                    yield return $"push {RegName((int)g, 4)}";
                }
                yield break;
            case MOp.Epilogue:
                for (int k = m.SavedRegs.Count - 1; k >= 0; k--)
                {
                    yield return $"pop {RegName((int)m.SavedRegs[k], 4)}";
                }
                yield return "leave";
                yield return "ret";
                yield break;
            case MOp.Jcc:
                yield return $"j{i.Cond.Mnemonic()} {Op(i.Operands[0], 4, false)}";
                yield break;
            case MOp.Setcc:
                yield return $"set{i.Cond.Mnemonic()} {Op(i.Operands[0], 1, true)}";
                yield break;
            case MOp.JmpTable:
                yield return $"jmp dword [{Op(i.Operands[0], 4, false)}*4 + table({string.Join(",", i.Table!.Select(t => "." + t.Name))})]";
                yield break;
            case MOp.JmpInd:
                yield return $"jmp {Op(i.Operands[0], 4, true)}";
                yield break;
            case MOp.Call:
            case MOp.CallInd:
                yield return $"call {Op(i.Operands[0], 4, true)}";
                yield break;
            case MOp.Cdq:
                yield return "cdq";
                yield break;
            case MOp.RepMovsb:
                yield return "rep movsb";
                yield break;
            case MOp.MmxLoad:
                yield return "movq mm0, " + Op(i.Operands[0], 8, true); yield break;
            case MOp.MmxStore:
                yield return "movq " + Op(i.Operands[0], 8, true) + ", mm0"; yield break;
            case MOp.MmxZero:
                yield return "pxor mm0, mm0"; yield break;
            case MOp.RepMovsd:
                yield return "rep movsd";
                yield break;
            case MOp.RepStosb:
                yield return "rep stosb";
                yield break;
            case MOp.RepStosd:
                yield return "rep stosd";
                yield break;
            case MOp.LockOrEsp:
                yield return "lock or dword [esp], 0";
                yield break;
            case MOp.Fnstsw:
                yield return "fnstsw ax";
                yield break;
            case MOp.Int:
                yield return $"int {Op(i.Operands[0], 1, false)}";
                yield break;
            case MOp.SyscallTrap:
                yield return "int 0x80";
                yield break;
            case MOp.In:
                yield return $"in {RegName((int)Gpr.Eax, i.Width)}, dx";
                yield break;
            case MOp.Out:
                yield return $"out dx, {RegName((int)Gpr.Eax, i.Width)}";
                yield break;
            case MOp.RepInsw:
                yield return "rep insw";
                yield break;
            case MOp.RepOutsw:
                yield return "rep outsw";
                yield break;
            case MOp.Lgdt:
            case MOp.Lidt:
            case MOp.Invlpg:
                yield return $"{Mnemonic(i)} {Op(i.Operands[0], 4, false)}";
                yield break;
            case MOp.MovFromCr:
                yield return $"mov {Op(i.Operands[0], 4, false)}, cr{Op(i.Operands[1], 4, false)}";
                yield break;
            case MOp.GotPc:
                yield return "call .+5";
                yield return $"pop {Op(i.Operands[0], 4, false)}";
                yield return $"add {Op(i.Operands[0], 4, false)}, _GLOBAL_OFFSET_TABLE_";
                yield break;
            case MOp.GsSelf:
                yield return $"mov {Op(i.Operands[0], 4, false)}, gs:[0]";
                yield break;
            case MOp.SetGs:
                yield return $"mov gs, {Op(i.Operands[0], 2, false)}";
                yield break;
            case MOp.LoadSegments:
                yield return "mov ds, ax";
                yield return "mov es, ax";
                yield return "mov fs, ax";
                yield return "mov gs, ax";
                yield return "mov ss, ax";
                yield break;
            case MOp.MovToCr:
                yield return $"mov cr{Op(i.Operands[1], 4, false)}, {Op(i.Operands[0], 4, false)}";
                yield break;
            case MOp.Pause:
                yield return "pause";
                yield break;
            case MOp.Shl:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Shld:
            case MOp.Shrd:
            {
                // The count is CL or an immediate, never a 32-bit register.
                List<string> parts = new();
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    bool count = k == i.Operands.Count - 1;
                    parts.Add(Op(i.Operands[k], count ? 1 : i.Width, k == 0));
                }
                yield return $"{Mnemonic(i)} {string.Join(", ", parts)}";
                yield break;
            }
            case MOp.Movzx:
            case MOp.Movsx:
                yield return $"{Mnemonic(i)} {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], i.Width, true)}";
                yield break;
        }

        string prefix = i.Lock ? "lock " : "";
        List<string> ops = new();
        bool anyReg = i.Operands.Any(o => o is MReg);
        for (int k = 0; k < i.Operands.Count; k++)
        {
            // Size a memory operand only when no register fixes the width.
            ops.Add(Op(i.Operands[k], i.Width, !anyReg || i.Op is MOp.Fld or MOp.Fstp or MOp.Fild or MOp.Fistp));
        }
        yield return ops.Count == 0 ? prefix + Mnemonic(i) : $"{prefix}{Mnemonic(i)} {string.Join(", ", ops)}";
    }

    private static string Mnemonic(MInstr i) => i.Op switch
    {
        MOp.Imul3 => "imul",
        MOp.ImulWide => "imul",
        MOp.Fnstcw => "fnstcw",
        _ => i.Op.ToString().ToLowerInvariant(),
    };
}
