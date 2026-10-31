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

    /// <summary>A packed instruction's register named at operand `k` (an MImm), mm0 or xmm0 where none is.</summary>
    private static string Mm(MInstr i, int k) => "mm" + (i.Operands.Count > k && i.Operands[k] is MImm { Value: var n } ? n : 0);
    private static string Xmm(MInstr i, int k) => "xmm" + (i.Operands.Count > k && i.Operands[k] is MImm { Value: var n } ? n : 0);

    /// <summary>A packed instruction's source: a register (an MImm) or memory.</summary>
    private static string PackedSource(MOperand o, string file, int width) => o is MImm { Value: var n } ? file + n : Op(o, width, false);

    /// <summary>One instruction as the listing prints it, its lines joined (the allocator's trace).</summary>
    public static string Line(MFunction m, MInstr i) => string.Join(" ; ", Lines(m, i));

    private static string Op(MOperand o, int width, bool sized)
    {
        switch (o)
        {
            case MReg r when r.IsPhys:
                return RegName(r.Id, width);
            case MReg { Id: MReg.EbpDataId }:
                return "ebp";
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
                yield return m.FramePointerFree ? "mov ebp, esp ; 8B EC: no frame pointer after" : "mov ebp, esp";
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
                if (m.FramePointerFree)
                {
                    if (m.Frame.Size > 0) yield return $"add esp, {m.Frame.Size}";
                    yield return "pop ebp";
                }
                else yield return "leave";
                yield return m.Source.CalleePops > 0 ? "ret " + m.Source.CalleePops : "ret";
                yield break;
            case MOp.CompilerFence:
                yield return "; compiler fence (nothing encoded)";
                yield break;
            case MOp.SaveRegs:
                yield return "; callee-saved registers pushed (nothing encoded)";
                yield break;
            case MOp.Jcc:
                yield return $"j{i.Cond.Mnemonic()} {Op(i.Operands[0], 4, false)}";
                yield break;
            case MOp.Setcc:
                yield return $"set{i.Cond.Mnemonic()} {Op(i.Operands[0], 1, true)}";
                yield break;
            case MOp.Cmov:
                yield return $"cmov{i.Cond.Mnemonic()} {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 4, false)}";
                yield break;
            case MOp.JmpTable:
                yield return $"jmp dword [{Op(i.Operands[0], 4, false)}*4 + table({string.Join(",", i.Table!.Select(t => "." + t.Name))})]";
                yield break;
            case MOp.JmpInd:
                yield return $"jmp {Op(i.Operands[0], 4, true)}";
                yield break;
            case MOp.Int3:
                // A breakpoint's marker operand is the compiler's, not the instruction's.
                yield return "int3";
                yield break;
            case MOp.Call:
            case MOp.CallInd:
            case MOp.CallKeep:
            case MOp.CallKeepInd:
            case MOp.CallKeepEax:
            case MOp.CallKeepEaxInd:
                yield return $"call {Op(i.Operands[0], 4, true)}";
                yield break;
            case MOp.Cdq:
                yield return "cdq";
                yield break;
            case MOp.Pushfd:
                yield return "pushfd";
                yield break;
            case MOp.Popfd:
                yield return "popfd";
                yield break;
            case MOp.RepMovsb:
                yield return "rep movsb";
                yield break;
            case MOp.MmxLoad:
                yield return $"movq {Mm(i, 1)}, " + Op(i.Operands[0], 8, true); yield break;
            case MOp.MmxStore:
                yield return "movq " + Op(i.Operands[0], 8, true) + $", {Mm(i, 1)}"; yield break;
            case MOp.MmxZero:
                yield return "pxor mm0, mm0"; yield break;
            case MOp.MmxLoadD:
                yield return $"movd {Mm(i, 1)}, " + Op(i.Operands[0], 4, true); yield break;
            case MOp.MmxStoreD:
                yield return "movd " + Op(i.Operands[0], 4, true) + $", {Mm(i, 1)}"; yield break;
            case MOp.MmxDuplicateLowWords:
                yield return "punpcklwd mm0, mm0"; yield break;
            case MOp.MmxWidenUnsignedWords:
                yield return "pxor mm1, mm1"; yield return "punpcklwd mm0, mm1"; yield break;
            case MOp.ThreeDNowIntToFloat:
                yield return "pi2fd mm0, mm0"; yield break;
            case MOp.ThreeDNowShortToFloat:
                yield return "pi2fw mm0, mm0"; yield break;
            case MOp.MmxSaveToTwo:
                yield return "movq mm2, mm0"; yield break;
            case MOp.ThreeDNowAddFromTwo:
                yield return "pfadd mm0, mm2"; yield break;
            case MOp.ThreeDNowSubFromTwo:
                yield return "pfsubr mm0, mm2"; yield break;
            case MOp.ThreeDNowMulFromTwo:
                yield return "pfmul mm0, mm2"; yield break;
            case MOp.ThreeDNowFloatToInt:
                yield return "pf2id mm0, mm0"; yield break;
            case MOp.MmxShlW: case MOp.MmxShlD: case MOp.MmxShrW: case MOp.MmxShrD: case MOp.MmxSarW: case MOp.MmxSarD:
                string shift = i.Op switch { MOp.MmxShlW => "psllw", MOp.MmxShlD => "pslld", MOp.MmxShrW => "psrlw",
                    MOp.MmxShrD => "psrld", MOp.MmxSarW => "psraw", _ => "psrad" };
                yield return shift + " mm0, " + Op(i.Operands[0], 4, false); yield break;
            case MOp.MmxAddB: case MOp.MmxAddW: case MOp.MmxAddD:
            case MOp.MmxEqualB: case MOp.MmxEqualW: case MOp.MmxEqualD:
            case MOp.MmxGreaterB: case MOp.MmxGreaterW: case MOp.MmxGreaterD:
            case MOp.MmxSubB: case MOp.MmxSubW: case MOp.MmxSubD:
            case MOp.MmxAnd: case MOp.MmxOr: case MOp.MmxXor: case MOp.MmxMulW: case MOp.MmxMulHighW: case MOp.MmxMultiplyAddW:
            case MOp.ThreeDNowAverageB: case MOp.ThreeDNowMulRoundW:
                string packed = i.Op switch { MOp.MmxAddB => "paddb", MOp.MmxAddW => "paddw", MOp.MmxAddD => "paddd",
                    MOp.MmxEqualB => "pcmpeqb", MOp.MmxEqualW => "pcmpeqw", MOp.MmxEqualD => "pcmpeqd",
                    MOp.MmxGreaterB => "pcmpgtb", MOp.MmxGreaterW => "pcmpgtw", MOp.MmxGreaterD => "pcmpgtd",
                    MOp.MmxSubB => "psubb", MOp.MmxSubW => "psubw", MOp.MmxSubD => "psubd",
                    MOp.MmxAnd => "pand", MOp.MmxOr => "por", MOp.MmxXor => "pxor", MOp.MmxMulHighW => "pmulhw",
                    MOp.ThreeDNowAverageB => "pavgusb", MOp.ThreeDNowMulRoundW => "pmulhrw", MOp.MmxMultiplyAddW => "pmaddwd", _ => "pmullw" };
                yield return packed + " mm0, " + Op(i.Operands[0], 8, true); yield break;
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
            case MOp.Fucomipp:
                yield return "fucomip st0, st1";
                yield return "fstp st0";
                yield break;
            case MOp.Cvttss2si:
                yield return $"cvttss2si {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 4, true)}";
                yield break;
            case MOp.Cvttsd2si:
                yield return $"cvttsd2si {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 8, true)}";
                yield break;
            case MOp.Int:
                yield return $"int {Op(i.Operands[0], 1, false)}";
                yield break;
            case MOp.SyscallTrap:
                yield return i.Operands.Count > 0 ? $"int {Op(i.Operands[0], 1, false)}" : "int 0x80";
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
            case MOp.GsLoadAt:
            case MOp.GsLoadReg:
                yield return $"mov {Op(i.Operands[0], 4, false)}, gs:[{Op(i.Operands[1], 4, false)}]";
                yield break;
            case MOp.GsStoreAt:
            case MOp.GsStoreReg:
                yield return $"mov gs:[{Op(i.Operands[0], 4, false)}], {Op(i.Operands[1], 4, false)}";
                yield break;
            case MOp.FsLoad:
                yield return $"mov {Op(i.Operands[0], 4, false)}, fs:[{Op(i.Operands[0], 4, false)}]";
                yield break;
            case MOp.FsSehChain:
                yield return $"mov {Op(i.Operands[0], 4, false)}, fs:[0]";
                yield break;
            case MOp.FsSehChainStore:
                yield return $"mov fs:[0], {Op(i.Operands[0], 4, false)}";
                yield break;
            case MOp.SetGs:
                yield return $"mov gs, {Op(i.Operands[0], 2, false)}";
                yield break;
            case MOp.Rdmsr: yield return "rdmsr"; yield break;
            case MOp.Wrmsr: yield return "wrmsr"; yield break;
            case MOp.Cpuid: yield return "cpuid"; yield break;
            case MOp.Rdtsc: yield return "rdtsc"; yield break;
            case MOp.MmxStoreNt:
                yield return $"movntq {Op(i.Operands[0], 8, false)}, {Mm(i, 1)}";
                yield break;
            case MOp.PrefetchNta:
                yield return $"prefetchnta {Op(i.Operands[0], 1, false)}";
                yield break;
            case MOp.PrefetchLine:
                yield return $"prefetch {Op(i.Operands[0], 1, false)}";
                yield break;
            case MOp.PrefetchWrite:
                yield return $"prefetchw {Op(i.Operands[0], 1, false)}";
                yield break;
            case MOp.XmmStoreNt:
                yield return $"movntdq {Op(i.Operands[0], 16, false)}, {Xmm(i, 1)}";
                yield break;
            case MOp.MmxFindW4:
                yield return $"movq mm0, {Op(i.Operands[1], 8, false)}";
                yield return $"pcmpeqw mm0, {Op(i.Operands[2], 8, false)}";
                yield return "packsswb mm0, mm0";
                yield return $"movd {Op(i.Operands[0], 4, false)}, mm0";
                yield break;
            case MOp.MmxFindW8:
            {
                MMem at = (MMem)i.Operands[1];
                MMem next = new(at.Base, at.Disp + 8) { Index = at.Index, Scale = at.Scale, Symbol = at.Symbol, Reloc = at.Reloc, Label = at.Label };
                yield return $"movq mm0, {Op(at, 8, false)}";
                yield return $"movq mm1, {Op(next, 8, false)}";
                yield return $"pcmpeqw mm0, {Op(i.Operands[2], 8, false)}";
                yield return $"pcmpeqw mm1, {Op(i.Operands[2], 8, false)}";
                yield return "packsswb mm0, mm1";
                yield return $"pmovmskb {Op(i.Operands[0], 4, false)}, mm0";
                yield break;
            }
            case MOp.MmxOp:
                yield return $"mmx 0f {((MImm)i.Operands[0]).Value:x2}, {Mm(i, 2)}, {PackedSource(i.Operands[1], "mm", 8)}";
                yield break;
            case MOp.XmmLoad:
                yield return $"movups xmm{((MImm)i.Operands[0]).Value}, {Op(i.Operands[1], 16, false)}";
                yield break;
            case MOp.XmmStore:
                yield return $"movups {Op(i.Operands[0], 16, false)}, {Xmm(i, 1)}";
                yield break;
            case MOp.XmmLoadLow:
                yield return $"movlps xmm{((MImm)i.Operands[0]).Value}, {Op(i.Operands[1], 8, false)}";
                yield break;
            case MOp.XmmStoreLow:
                yield return $"movlps {Op(i.Operands[0], 8, false)}, {Xmm(i, 1)}";
                yield break;
            case MOp.XmmOp:
                yield return $"sse {((MImm)i.Operands[0]).Value:x2} 0f {((MImm)i.Operands[1]).Value:x2}, xmm0, xmm1" + (i.Operands.Count > 2 ? $", {((MImm)i.Operands[2]).Value}" : "");
                yield break;
            case MOp.XmmOpRR:
                yield return $"sse {((MImm)i.Operands[0]).Value:x2} 0f {((MImm)i.Operands[1]).Value:x2}, {Xmm(i, 2)}, {PackedSource(i.Operands[3], "xmm", 16)}" + (i.Operands.Count > 4 ? $", {((MImm)i.Operands[4]).Value}" : "");
                yield break;
            case MOp.XmmShiftImm:
                yield return $"sse 66 0f {((MImm)i.Operands[0]).Value:x2} /{((MImm)i.Operands[1]).Value}, {Xmm(i, 3)}, {((MImm)i.Operands[2]).Value}";
                yield break;
            case MOp.XmmExtractW:
                yield return $"pextrw {Op(i.Operands[0], 4, false)}, {Xmm(i, 2)}, {((MImm)i.Operands[1]).Value}";
                yield break;
            case MOp.XmmInsertW:
                yield return $"pinsrw {Xmm(i, 2)}, {Op(i.Operands[0], 4, false)}, {((MImm)i.Operands[1]).Value}";
                yield break;
            case MOp.ThreeDNowOp:
                yield return $"3dnow 0f 0f {((MImm)i.Operands[0]).Value:x2}, {Mm(i, 2)}, {PackedSource(i.Operands[1], "mm", 8)}";
                yield break;
            case MOp.MmxShiftImm:
                yield return $"mmx 0f {((MImm)i.Operands[0]).Value:x2} /{((MImm)i.Operands[1]).Value}, {Mm(i, 3)}, {((MImm)i.Operands[2]).Value}";
                yield break;
            case MOp.MmxShuffleW:
                yield return $"pshufw {Mm(i, 2)}, {PackedSource(i.Operands[0], "mm", 8)}, {((MImm)i.Operands[1]).Value}";
                yield break;
            case MOp.MmxExtractW:
                yield return $"pextrw {Op(i.Operands[0], 4, false)}, {Mm(i, 2)}, {((MImm)i.Operands[1]).Value}";
                yield break;
            case MOp.MmxInsertW:
                yield return $"pinsrw {Mm(i, 2)}, {Op(i.Operands[0], 4, false)}, {((MImm)i.Operands[1]).Value}";
                yield break;
            case MOp.PackedSaveX87:
                yield return "fnstenv [eax]";
                yield return "cmp word [eax+8], 0xFFFF";
                yield return "jne 1f";
                yield return "mov dword [eax+108], 0";
                yield return "jmp 2f";
                yield return "1: fldenv [eax]";
                yield return "fnsave [eax]";
                yield return "mov dword [eax+108], 1";
                yield return "2: push 0x027F";
                yield return "fldcw [esp]";
                yield return "add esp, 4";
                yield break;
            case MOp.PackedRestoreX87:
                yield return "cmp dword [eax+108], 0";
                yield return "jne 1f";
                yield return "fldenv [eax]";
                yield return "jmp 2f";
                yield return "1: frstor [eax]";
                yield return "2:";
                yield break;
            case MOp.PackedSaveFxsr:
                yield return "fxsave [eax]";
                yield return "fninit";
                yield return "mov dword [eax+512], 0x1F80";
                yield return "ldmxcsr [eax+512]";
                yield return "push 0x027F";
                yield return "fldcw [esp]";
                yield return "add esp, 4";
                yield break;
            case MOp.PackedRestoreFxsr:
                yield return "fxrstor [eax]";
                yield break;
            case MOp.MmxEqual8:
                yield return $"movq mm0, {Op(i.Operands[1], 8, false)}";
                yield return $"pcmpeqd mm0, {Op(i.Operands[2], 8, false)}";
                yield return "packssdw mm0, mm0";
                yield return $"movd {Op(i.Operands[0], 4, false)}, mm0";
                yield break;
            case MOp.WriteFlags:
                yield return $"push {Op(i.Operands[0], 4, false)}";
                yield return "popfd";
                yield break;
            case MOp.ReadFlags:
                yield return "pushfd";
                yield return $"pop {Op(i.Operands[0], 4, false)}";
                yield break;
            case MOp.Ltr: yield return $"ltr {Op(i.Operands[0], 2, false)}"; yield break;
            case MOp.LoadCs:
                yield return $"push {Op(i.Operands[0], 4, false)}";
                yield return "call .+5";
                yield return "add dword [esp], 5";
                yield return "retf";
                yield break;
            case MOp.GetGs:
                yield return $"xor {Op(i.Operands[0], 4, false)}, {Op(i.Operands[0], 4, false)}";
                yield return $"mov {Op(i.Operands[0], 2, false)}, gs";
                yield break;
            case MOp.ReadCs:
                yield return $"xor {Op(i.Operands[0], 4, false)}, {Op(i.Operands[0], 4, false)}";
                yield return $"mov {Op(i.Operands[0], 2, false)}, cs";
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
