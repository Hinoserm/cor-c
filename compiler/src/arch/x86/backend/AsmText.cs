#nullable enable
using System.Text;

namespace Corsac.Lang.X86;

/// <summary>
/// Prints allocated machine code as Intel-syntax assembly, close enough to
/// NASM that a human can set it beside `objdump -d -M intel` of the bytes.
/// Nothing consumes this; it exists to be read. Both modes': a register by
/// the name its width has in the mode (RegName), the ways in and out of a
/// function as each mode's encoder writes them (Encoder.Prologue,
/// Encoder.Long's PrologueL), and long mode's own instructions.
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

    /// <summary>The module's data as the listing shows it, a section each: every item's bytes, and under them where an address goes (a doubleword's on i386, a quadword's in long mode).</summary>
    public static void PrintData(StringBuilder sb, Corsac.Lang.Ir.Module module)
    {
        string address = X86Mode.Long ? "dq" : "dd";
        foreach (string section in new[] { ".rodata", Corsac.Lang.Ir.Section.ObjectsName, ".data", ".bss" })
        {
            // (By its flags, not exactly by its section: a string object is
            // listed with the image's objects, as the object places it, but
            // numbers are listed under .data and relocated constants under
            // .rodata, where ObjectData.Place gives them sections of their own.)
            List<Corsac.Lang.Ir.DataItem> items = module.Data.Where(d => (d.Zero ? ".bss" : d.ReadOnly && d.HasHeader ? Corsac.Lang.Ir.Section.ObjectsName : d.ReadOnly ? ".rodata" : ".data") == section).ToList();
            if (items.Count == 0) continue;
            sb.Append("section ").Append(section).Append('\n');
            foreach (Corsac.Lang.Ir.DataItem d in items)
            {
                sb.Append("    align ").Append(d.Align).Append('\n');
                sb.Append(d.Name).Append(":\n");
                if (d.Zero)
                {
                    sb.Append("    resb ").Append(d.Bytes.Length).Append('\n');
                    continue;
                }
                sb.Append("    db ").Append(string.Join(", ", d.Bytes.Select(b => $"0x{b:x2}"))).Append('\n');
                foreach (Corsac.Lang.Ir.DataReloc r in d.Relocs)
                {
                    sb.Append("    ; +").Append(r.Offset).Append(": ").Append(address).Append(' ').Append(r.Symbol);
                    if (r.Addend != 0) sb.Append('+').Append(r.Addend);
                    sb.Append('\n');
                }
            }
            sb.Append('\n');
        }
    }

    private static readonly string[] Low8 = { "al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil" },
        Words = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" },
        Doubles = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" },
        Quads = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi" };

    /// <summary>
    /// A general register at a width. i386's byte registers 4 to 7 are AH
    /// to BH; long mode's are SPL to DIL (every byte instruction of its
    /// encoder's carries the REX that makes them so), its eight more R8 to
    /// R15 with b, w, d for the narrower, and a width the instruction does
    /// not give (0) is the whole register's.
    /// </summary>
    private static string RegName(int id, int width)
    {
        if (!X86Mode.Long)
            return width switch
            {
                1 => new[] { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" }[id],
                2 => Words[id],
                _ => Doubles[id],
            };
        if (id >= 8) return "r" + id + width switch { 1 => "b", 2 => "w", 4 => "d", _ => "" };
        return width switch { 1 => Low8[id], 2 => Words[id], 4 => Doubles[id], _ => Quads[id] };
    }

    /// <summary>Whether the encoder zeroes the instruction's register first (Encoder.ZeroFirst): asked, and not its own source.</summary>
    private static bool Zeroes(MInstr i) => i.ZeroFirst
        && !(i.Operands[1] is MReg { IsXmm: true } from && i.Operands[0] is MReg { IsXmm: true } to && from.XmmNumber == to.XmmNumber);

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
            case MReg r when r.IsXmm:
                return $"xmm{r.XmmNumber}";
            case MReg { Id: MReg.EbpDataId }:
                return RegName((int)Gpr.Ebp, X86Mode.Long ? 8 : 4);
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

    /// <summary>A 64-bit Windows frame's page probe, as the encoder writes it before the `sub` (Encoder.ProbeStack).</summary>
    private static void Probe(List<string> lines, int frame)
    {
        if (!X86Mode.Windows || frame < Encoder.ProbePage) return;
        lines.Add("mov r11, rsp ; the frame's pages touched in order, as Windows grows a stack");
        lines.Add($"mov r10d, {frame / Encoder.ProbePage}");
        lines.Add($".probe: sub r11, {Encoder.ProbePage}");
        lines.Add("test [r11], r11b");
        lines.Add("dec r10d");
        lines.Add("jnz .probe");
    }

    private static IEnumerable<string> Lines(MFunction m, MInstr i)
    {
        if (X86Mode.Long && LongLines(m, i) is { } own)
        {
            foreach (string line in own) yield return line;
            yield break;
        }
        switch (i.Op)
        {
            case MOp.Prologue:
                if (m.ReservedFrame)
                {
                    yield return m.Frame.Size == 0 ? "push eax ; EBP's word, not written" : $"sub esp, {m.Frame.Size + 4} ; the frame and EBP's word, not written";
                    foreach (Gpr g in m.SavedRegs) yield return $"push {RegName((int)g, 4)}";
                    yield break;
                }
                if (m.Frameless)
                {
                    foreach (Gpr g in m.SavedRegs) yield return $"push {RegName((int)g, 4)}";
                    if (m.Frame.Size > 0) yield return $"sub esp, {m.Frame.Size}";
                    yield break;
                }
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
                if (m.AlignedArea > 0)
                {
                    yield return $"sub esp, {m.AlignedArea}";
                    yield return "and esp, -16";
                }
                yield break;
            case MOp.Epilogue:
                if (m.ReservedFrame)
                {
                    for (int k = m.SavedRegs.Count - 1; k >= 0; k--) yield return $"pop {RegName((int)m.SavedRegs[k], 4)}";
                    yield return m.Frame.Size == 0 ? "pop ecx" : $"add esp, {m.Frame.Size + 4}";
                    yield return m.Source.CalleePops > 0 ? "ret " + m.Source.CalleePops : "ret";
                    yield break;
                }
                if (m.Frameless)
                {
                    if (m.Frame.Size > 0) yield return $"add esp, {m.Frame.Size}";
                    for (int k = m.SavedRegs.Count - 1; k >= 0; k--) yield return $"pop {RegName((int)m.SavedRegs[k], 4)}";
                    yield return m.Source.CalleePops > 0 ? "ret " + m.Source.CalleePops : "ret";
                    yield break;
                }
                if (m.AlignedArea > 0 && m.SavedRegs.Count > 0) yield return $"lea esp, [ebp - {m.Frame.Size + 4 * m.SavedRegs.Count}]";
                for (int k = m.SavedRegs.Count - 1; k >= 0; k--)
                {
                    yield return $"pop {RegName((int)m.SavedRegs[k], 4)}";
                }
                yield return "leave";
                yield return m.Source.CalleePops > 0 ? "ret " + m.Source.CalleePops : "ret";
                yield break;
            case MOp.CompilerFence:
                yield return "; compiler fence (nothing encoded)";
                yield break;
            case MOp.SaveRegs:
                yield return "; callee-saved registers pushed (nothing encoded)";
                yield break;
            case MOp.CallerReg:
                yield return $"; {Op(i.Operands[0], 4, false)} = the caller's {(((MImm)i.Operands[1]).Value switch { 0 => "ebx", 1 => "esi", _ => "edi" })}";
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
                if (m.ColdFrames && i.Op is MOp.Call or MOp.CallInd)
                {
                    if (m.ReservedFrame)
                    {
                        int x = m.Frame.Size + 4 * m.SavedRegs.Count;
                        yield return $"mov [esp + {x}], ebp";
                        yield return $"lea ebp, [esp + {x}] ; the frame, on the way to a call that never returns";
                    }
                    else
                    {
                        yield return "push ebp";
                        yield return "mov ebp, esp ; the frame, on the way to a call that never returns";
                    }
                }
                yield return $"call {Op(i.Operands[0], 4, true)}";
                yield break;
            case MOp.CallSafe:
                yield return "push eax";
                yield return "push ecx";
                yield return "push edx";
                yield return $"call {Op(i.Operands[0], 4, true)} ; a safepoint that keeps the registers";
                yield return "pop edx";
                yield return "pop ecx";
                yield return "pop eax";
                yield break;
            case MOp.Cdq:
                yield return "cdq";
                yield break;
            case MOp.Pushf:
                yield return "pushfd";
                yield break;
            case MOp.Popf:
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
            case MOp.MovF:
                yield return i.Operands[0] is MReg && i.Operands[1] is MReg
                    ? $"movaps {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 4, false)}"
                    : $"{(i.Width == 16 ? (i.Operands[0] is MMem { Aligned: true } || i.Operands[1] is MMem { Aligned: true } ? "movaps" : "movups") : i.Width == 8 ? "movsd" : "movss")} {Op(i.Operands[0], i.Width, i.Width != 16)}, {Op(i.Operands[1], i.Width, i.Width != 16)}";
                yield break;
            case MOp.XmmOpV:
            case MOp.XmmOpVDef:
                yield return $"sse {((MImm)i.Operands[0]).Value:x2} 0f {((MImm)i.Operands[1]).Value:x2}, {Op(i.Operands[2], 16, false)}, {Op(i.Operands[3], 16, false)}" + (i.Operands.Count > 4 ? $", {((MImm)i.Operands[4]).Value}" : "");
                yield break;
            case MOp.XmmShiftV:
                yield return $"sse 66 0f {((MImm)i.Operands[1]).Value:x2} /{((MImm)i.Operands[2]).Value}, {Op(i.Operands[0], 16, false)}, {((MImm)i.Operands[3]).Value}";
                yield break;
            case MOp.XmmExtractV:
                yield return $"pextrw {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 16, false)}, {((MImm)i.Operands[2]).Value}";
                yield break;
            case MOp.XmmInsertV:
                yield return $"pinsrw {Op(i.Operands[0], 16, false)}, {Op(i.Operands[1], 4, false)}, {((MImm)i.Operands[2]).Value}";
                yield break;
            case MOp.XmmStoreNtV:
                yield return $"movntdq {Op(i.Operands[0], 16, false)}, {Op(i.Operands[1], 16, false)}";
                yield break;
            case MOp.Movnti:
                yield return $"movnti {Op(i.Operands[0], i.Width, true)}, {Op(i.Operands[1], i.Width, false)}";
                yield break;
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.SqrtF:
            case MOp.UcomiF:
            {
                string name = i.Op switch { MOp.AddF => "add", MOp.SubF => "sub", MOp.MulF => "mul", MOp.DivF => "div", MOp.SqrtF => "sqrt", _ => "ucomi" };
                if (Zeroes(i)) yield return $"{(CpuModel.ZeroByPxor ? "pxor" : "xorps")} {Op(i.Operands[0], 4, false)}, {Op(i.Operands[0], 4, false)}";
                yield return $"{name}{(i.Width == 8 ? "sd" : "ss")} {Op(i.Operands[0], i.Width, false)}, {Op(i.Operands[1], i.Width, true)}";
                yield break;
            }
            case MOp.CvtFToF:
                if (Zeroes(i)) yield return $"{(CpuModel.ZeroByPxor ? "pxor" : "xorps")} {Op(i.Operands[0], 4, false)}, {Op(i.Operands[0], 4, false)}";
                yield return $"{(i.Width == 8 ? "cvtss2sd" : "cvtsd2ss")} {Op(i.Operands[0], 8, false)}, {Op(i.Operands[1], i.Width == 8 ? 4 : 8, true)}";
                yield break;
            case MOp.CvtIntToF:
                if (Zeroes(i)) yield return $"{(CpuModel.ZeroByPxor ? "pxor" : "xorps")} {Op(i.Operands[0], 4, false)}, {Op(i.Operands[0], 4, false)}";
                yield return $"{(i.Width == 8 ? "cvtsi2sd" : "cvtsi2ss")} {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 4, true)}";
                yield break;
            case MOp.XorPs:
                yield return $"xorps {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 4, false)}";
                yield break;
            case MOp.Punpckldq:
                yield return $"punpckldq {Op(i.Operands[0], 4, false)}, {Op(i.Operands[1], 4, false)}";
                yield break;
            case MOp.MovdToX:
            case MOp.MovdFromX:
                yield return $"movd {Op(i.Operands[0], 4, true)}, {Op(i.Operands[1], 4, true)}";
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
                yield return $"mov gs, {Op(i.Operands[0], X86Mode.Long ? 4 : 2, false)}";
                yield break;
            case MOp.Rdmsr: yield return "rdmsr"; yield break;
            case MOp.Wrmsr: yield return "wrmsr"; yield break;
            case MOp.Cpuid: yield return "cpuid"; yield break;
            case MOp.InlineAsm:
            {
                // A block the frame put out of reach lists as the error the
                // encoder reports for it (InlineAsmCode.Assembled), not a crash.
                string text;
                byte[] bytes;
                string? failed = null;
                try { (text, bytes) = i.Asm!.Assembled(i); }
                catch (InvalidOperationException e) { (text, bytes, failed) = ("", Array.Empty<byte>(), e.Message); }
                if (failed != null) { yield return "; asm block refused: " + failed; yield break; }
                yield return "; asm block";
                foreach (string line in text.Split('\n'))
                    if (line.Trim().Length > 0) yield return "; " + line.Trim();
                if (bytes.Length > 0) yield return ".db " + string.Join(", ", bytes.Select(b => "0x" + b.ToString("x2")));
                yield break;
            }
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
            case MOp.GetGs when X86Mode.Long:
                // Long mode's is one move: the upper half comes out nought.
                yield return $"mov {Op(i.Operands[0], 4, false)}, gs";
                yield break;
            case MOp.ReadCs when X86Mode.Long:
                yield return $"mov {Op(i.Operands[0], 4, false)}, cs";
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

    /// <summary>
    /// LONG MODE'S OWN: the instructions it writes otherwise than i386 does
    /// (Encoder.Long), each as its encoder's case writes it; null for one
    /// the family's lines are right for.
    /// </summary>
    private static List<string>? LongLines(MFunction m, MInstr i)
    {
        string Q(Gpr g) => RegName((int)g, 8);
        string O(int k, int width, bool sized = false) => Op(i.Operands[k], width, sized);
        List<string> lines = new();
        int frame = Encoder.FrameBytes(m);
        string SaveSlot(int k) => m.Frameless ? $"[rsp+{frame + 8 * (m.SavedRegs.Count - 1 - k)}]" : $"[rbp-{m.Frame.Size + 8 * (k + 1)}]";
        switch (i.Op)
        {
            case MOp.Prologue:
                if (m.ReservedFrame)
                {
                    lines.Add(frame == 0 ? "push rax ; RBP's word, not written" : $"sub rsp, {frame + 8} ; the frame and RBP's word, not written");
                    foreach (Gpr g in m.SavedRegs) lines.Add("push " + Q(g));
                    if (Encoder.ReservedPad(m) != 0) lines.Add("push rax ; the stack at sixteen");
                    return lines;
                }
                if (m.Frameless)
                {
                    foreach (Gpr g in m.SavedRegs) lines.Add("push " + Q(g));
                    if (frame > 0) { Probe(lines, frame); lines.Add($"sub rsp, {frame}"); }
                    return lines;
                }
                lines.Add("push rbp");
                lines.Add("mov rbp, rsp");
                if (m.RealignsStack) lines.Add("and rsp, -16");
                if (frame > 0) { Probe(lines, frame); lines.Add($"sub rsp, {frame}"); }
                for (int k = 0; k < m.SavedRegs.Count; k++) lines.Add($"mov {SaveSlot(k)}, {Q(m.SavedRegs[k])}");
                return lines;
            case MOp.Epilogue:
                if (m.ReservedFrame)
                {
                    if (Encoder.ReservedPad(m) != 0) lines.Add("pop rcx");
                    for (int k = m.SavedRegs.Count - 1; k >= 0; k--) lines.Add("pop " + Q(m.SavedRegs[k]));
                    lines.Add(frame == 0 ? "pop rcx" : $"add rsp, {frame + 8}");
                    lines.Add("ret");
                    return lines;
                }
                if (m.Frameless)
                {
                    if (frame > 0) lines.Add($"add rsp, {frame}");
                    for (int k = m.SavedRegs.Count - 1; k >= 0; k--) lines.Add("pop " + Q(m.SavedRegs[k]));
                    lines.Add("ret");
                    return lines;
                }
                for (int k = 0; k < m.SavedRegs.Count; k++) lines.Add($"mov {Q(m.SavedRegs[k])}, {SaveSlot(k)}");
                lines.Add("leave");
                lines.Add("ret");
                return lines;
            // A sign extension into the low half (To32) or the whole register; a zero extension always into the low half, which clears the upper.
            case MOp.Movsx when i.Width == 4:
                lines.Add($"movsxd {O(0, 8)}, {O(1, 4, true)}");
                return lines;
            case MOp.Movsx:
                lines.Add($"movsx {O(0, i.To32 ? 4 : 8)}, {O(1, i.Width, true)}");
                return lines;
            case MOp.Movzx:
                lines.Add($"movzx {O(0, 4)}, {O(1, i.Width, true)}");
                return lines;
            // movd or movq between the register files, by the width.
            case MOp.MovGx:
                lines.Add($"{(i.Width == 8 ? "movq" : "movd")} {O(0, i.Width)}, {O(1, i.Width)}");
                return lines;
            // xorps, whatever the value's width; a float truncated to an integer by its own width's instruction.
            case MOp.XorF:
                lines.Add($"xorps {O(0, 16)}, {O(1, 16)}");
                return lines;
            case MOp.CvtFToInt:
                lines.Add($"{(i.SourceWidth == 4 ? "cvttss2si" : "cvttsd2si")} {O(0, i.Width)}, {O(1, i.SourceWidth, true)}");
                return lines;
            // 99 by the width: the accumulator's sign through RDX (cqo) or EDX (cdq).
            case MOp.Cwd:
                lines.Add(i.Width == 8 ? "cqo" : "cdq");
                return lines;
            case MOp.XmmMaskV:
                lines.Add($"pmovmskb {O(0, 4)}, {O(1, 16)}");
                return lines;
            case MOp.RepMovsq: lines.Add("rep movsq"); return lines;
            case MOp.RepStosq: lines.Add("rep stosq"); return lines;
            case MOp.Syscall: lines.Add("syscall"); return lines;
            case MOp.Swapgs: lines.Add("swapgs"); return lines;
            case MOp.SoftInt: lines.Add($"int {O(0, 4)}"); return lines;
            case MOp.Push or MOp.Pop:
                lines.Add($"{Mnemonic(i)} {O(0, 8, true)}");
                return lines;
            // A cold-framed function's call makes its frame on the way (Encoder.Long's ColdFrameL).
            case MOp.Call or MOp.CallInd when m.ColdFrames:
            {
                if (m.ReservedFrame)
                {
                    int x = frame + 8 * m.SavedRegs.Count + Encoder.ReservedPad(m);
                    lines.Add($"mov [rsp+{x}], rbp");
                    lines.Add($"lea rbp, [rsp+{x}] ; the frame, on the way to a call that never returns");
                }
                else
                {
                    lines.Add("push rbp");
                    lines.Add("mov rbp, rsp ; the frame, on the way to a call that never returns");
                }
                lines.Add($"call {O(0, 8, i.Op == MOp.CallInd)}");
                return lines;
            }
            // A safepoint's call keeps the nine registers a call may change, the stack at sixteen under them.
            case MOp.CallSafe:
                lines.Add($"call {O(0, 8)} ; a safepoint that keeps the registers (nine pushed, eight bytes more, and back)");
                return lines;
            case MOp.Call or MOp.CallKeep or MOp.CallKeepRax or MOp.CallKeepCas:
                lines.Add($"call {O(0, 8)}{(i.Op is MOp.Call ? "" : " ; " + i.Op)}");
                return lines;
            case MOp.CallInd or MOp.CallKeepInd or MOp.JmpInd:
                lines.Add($"{(i.Op == MOp.JmpInd ? "jmp" : "call")} {O(0, 8, true)}");
                return lines;
            case MOp.Lea:
                lines.Add($"lea {O(0, i.Width == 4 ? 4 : 8)}, {O(1, 8)}");
                return lines;
            // The x87, MMX and 3DNow! instructions are i386's alone.
            default:
                return null;
        }
    }

    private static string Mnemonic(MInstr i) => i.Op switch
    {
        MOp.Imul3 => "imul",
        MOp.ImulWide => "imul",
        MOp.MulWide => "mul",
        MOp.Fnstcw => "fnstcw",
        _ => i.Op.ToString().ToLowerInvariant(),
    };
}
