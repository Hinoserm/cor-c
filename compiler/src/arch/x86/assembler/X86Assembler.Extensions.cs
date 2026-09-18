namespace Corsac.Asm;

public sealed partial class X86Assembler
{
    private static readonly Dictionary<string, byte> MmxOpcodes = new(StringComparer.Ordinal)
    {
        ["packsswb"] = 0x63, ["packssdw"] = 0x6b, ["packuswb"] = 0x67,
        ["paddb"] = 0xfc, ["paddw"] = 0xfd, ["paddd"] = 0xfe,
        ["paddsb"] = 0xec, ["paddsw"] = 0xed, ["paddusb"] = 0xdc, ["paddusw"] = 0xdd,
        ["pand"] = 0xdb, ["pandn"] = 0xdf, ["por"] = 0xeb, ["pxor"] = 0xef,
        ["pcmpeqb"] = 0x74, ["pcmpeqw"] = 0x75, ["pcmpeqd"] = 0x76,
        ["pcmpgtb"] = 0x64, ["pcmpgtw"] = 0x65, ["pcmpgtd"] = 0x66,
        ["pmaddwd"] = 0xf5, ["pmulhw"] = 0xe5, ["pmullw"] = 0xd5,
        ["psllw"] = 0xf1, ["pslld"] = 0xf2, ["psllq"] = 0xf3,
        ["psraw"] = 0xe1, ["psrad"] = 0xe2,
        ["psrlw"] = 0xd1, ["psrld"] = 0xd2, ["psrlq"] = 0xd3,
        ["psubb"] = 0xf8, ["psubw"] = 0xf9, ["psubd"] = 0xfa,
        ["psubsb"] = 0xe8, ["psubsw"] = 0xe9, ["psubusb"] = 0xd8, ["psubusw"] = 0xd9,
        ["punpckhbw"] = 0x68, ["punpckhwd"] = 0x69, ["punpckhdq"] = 0x6a,
        ["punpcklbw"] = 0x60, ["punpcklwd"] = 0x61, ["punpckldq"] = 0x62,
    };

    private static readonly Dictionary<string, byte> ThreeDNowOpcodes = new(StringComparer.Ordinal)
    {
        ["pi2fd"] = 0x0d, ["pf2id"] = 0x1d, ["pfacc"] = 0xae,
        ["pfadd"] = 0x9e, ["pfsub"] = 0x9a, ["pfsubr"] = 0xaa,
        ["pfmul"] = 0xb4, ["pfcmpeq"] = 0xb0, ["pfcmpge"] = 0x90, ["pfcmpgt"] = 0xa0,
        ["pfmax"] = 0xa4, ["pfmin"] = 0x94, ["pfrcp"] = 0x96,
        ["pfrcpit1"] = 0xa6, ["pfrcpit2"] = 0xb6, ["pfrsqrt"] = 0x97,
        ["pfrsqit1"] = 0xa7, ["pmulhrw"] = 0xb7, ["pavgusb"] = 0xbf,
        ["pf2iw"] = 0x1c, ["pi2fw"] = 0x0c, ["pfnacc"] = 0x8a,
        ["pfpnacc"] = 0x8e, ["pswapd"] = 0xbb,
    };

    private void Require(bool available, string mnemonic, string feature)
    {
        if (!available) throw Error(mnemonic + " requires " + feature + "; selected CPU is " + _cpu.Name);
    }

    private void MmxSource(Operand operand, int size, string mnemonic)
    {
        if (operand.Kind != OperandKind.Mmx && operand.Kind != OperandKind.Memory)
            throw Error(mnemonic + " requires an MMX register or memory source");
        if (operand.Kind == OperandKind.Memory && operand.SizeGiven && operand.Size != size)
            throw Error(mnemonic + " requires a " + (size * 8) + "-bit memory operand");
    }

    private bool ExtendedInstruction(string mn, string[] a)
    {
        if (mn is "cpuid" or "rdtsc" or "rdmsr" or "wrmsr" or "rsm")
        {
            Require(_cpu.Pentium, mn, "Pentium"); Need(a, 0, mn);
            Emit(0x0f, mn switch { "cpuid" => (byte)0xa2, "rdtsc" => (byte)0x31,
                "rdmsr" => (byte)0x32, "wrmsr" => (byte)0x30, _ => (byte)0xaa });
            return true;
        }
        if (mn == "cmpxchg8b")
        {
            Require(_cpu.Pentium, mn, "Pentium"); Need(a, 1, mn);
            Operand memory = P(a[0]);
            if (memory.Kind != OperandKind.Memory || (memory.SizeGiven && memory.Size != 8))
                throw Error("cmpxchg8b requires a 64-bit memory operand");
            Prefixes(0, memory); Emit(0x0f, 0xc7); EmitRM(1, memory); return true;
        }
        if (mn is "prefetch" or "prefetchw")
        {
            Require(_cpu.ThreeDNow, mn, "3DNow!"); Need(a, 1, mn);
            Operand memory = P(a[0]);
            if (memory.Kind != OperandKind.Memory) throw Error(mn + " requires memory");
            Prefixes(0, memory); Emit(0x0f, 0x0d); EmitRM(mn == "prefetch" ? 0 : 1, memory); return true;
        }
        if (mn is "emms" or "femms")
        {
            Require(mn == "emms" ? _cpu.Mmx : _cpu.ThreeDNow, mn, mn == "emms" ? "MMX" : "3DNow!");
            Need(a, 0, mn); Emit(0x0f, mn == "emms" ? (byte)0x77 : (byte)0x0e); return true;
        }
        if (mn is "movd" or "movq")
        {
            Require(_cpu.Mmx, mn, "MMX"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            bool load = d.Kind == OperandKind.Mmx;
            Operand mm = load ? d : s, other = load ? s : d;
            int size = mn == "movd" ? 4 : 8;
            if (mm.Kind != OperandKind.Mmx) throw Error(mn + " requires an MMX register");
            if (size == 8) MmxSource(other, 8, mn);
            else if (!other.IsRegOrMem || (other.Kind == OperandKind.Register && other.Size != 4)
                || (other.Kind == OperandKind.Memory && other.SizeGiven && other.Size != 4))
                throw Error("movd requires a 32-bit integer register or memory operand");
            Prefixes(0, MemOf(other));
            Emit(0x0f, size == 8 ? (load ? (byte)0x6f : (byte)0x7f) : (load ? (byte)0x6e : (byte)0x7e));
            EmitRM(mm.Reg, other); return true;
        }
        bool packed = MmxOpcodes.TryGetValue(mn, out byte opcode);
        bool now = ThreeDNowOpcodes.TryGetValue(mn, out byte suffix);
        if (!packed && !now) return false;
        Require(now ? _cpu.ThreeDNow : _cpu.Mmx, mn, now ? "3DNow!" : "MMX");
        if (mn is "pf2iw" or "pi2fw" or "pfnacc" or "pfpnacc" or "pswapd")
            Require(_cpu.ThreeDNowExtended, mn, "K6-2+/K6-III+ 3DNow! extensions");
        Need(a, 2, mn);
        Operand dest = P(a[0]), src = P(a[1]);
        if (dest.Kind != OperandKind.Mmx) throw Error(mn + " requires an MMX destination");
        if (!now && mn.StartsWith("ps") && mn.Length == 5 && (mn[2] == 'l' || mn[2] == 'r') && src.Kind == OperandKind.Immediate)
        {
            long count = Val(src.Value);
            if (count < 0 || count > 255) throw Error("packed shift count must fit an unsigned byte");
            Emit(0x0f, mn[4] switch { 'w' => (byte)0x71, 'd' => (byte)0x72, _ => (byte)0x73 });
            EmitRM(mn[2] == 'l' ? 6 : mn[3] == 'a' ? 4 : 2, dest); Emit((byte)count); return true;
        }
        MmxSource(src, 8, mn); Prefixes(0, MemOf(src));
        Emit(0x0f, now ? (byte)0x0f : opcode); EmitRM(dest.Reg, src);
        if (now) Emit(suffix);
        return true;
    }
}
