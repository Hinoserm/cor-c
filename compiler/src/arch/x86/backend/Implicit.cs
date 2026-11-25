#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// LONG MODE'S CALLING CONVENTIONS, by register: what its selector, its
/// allocation and every pass that follows a value across a call agree on.
/// </summary>
internal static class LongAbi
{
    /// <summary>The integer arguments' registers, in order (System V's).</summary>
    internal static readonly Gpr[] IntArgRegs = { Gpr.Rdi, Gpr.Rsi, Gpr.Rdx, Gpr.Rcx, Gpr.R8, Gpr.R9 };
    /// <summary>How many float arguments go in XMM registers, XMM0 on.</summary>
    internal const int FloatArgRegs = 8;
    /// <summary>The registers a call keeps.</summary>
    internal static readonly Gpr[] CalleeSaved = { Gpr.Rbx, Gpr.R12, Gpr.R13, Gpr.R14, Gpr.R15 };
    /// <summary>The general registers a call may change, by id.</summary>
    internal static readonly int[] CallerSaved =
    {
        (int)Gpr.Rax, (int)Gpr.Rcx, (int)Gpr.Rdx, (int)Gpr.Rsi, (int)Gpr.Rdi,
        (int)Gpr.R8, (int)Gpr.R9, (int)Gpr.R10, (int)Gpr.R11,
    };
    /// <summary>A system call's arguments' registers, in order, by id (the kernel's: R10 where a call has RCX).</summary>
    internal static readonly int[] SyscallArgs =
    {
        (int)Gpr.Rdi, (int)Gpr.Rsi, (int)Gpr.Rdx, (int)Gpr.R10, (int)Gpr.R8, (int)Gpr.R9,
    };
}

/// <summary>
/// What each instruction touches without naming it, in long mode and its
/// calling conventions. What an operand does to its register is the
/// family's one table (Corsac.Lang.X86.Roles.Of).
/// </summary>
internal static class Implicit
{
    /// <summary>Registers an instruction reads without naming them, by id.</summary>
    public static IEnumerable<int> Uses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.InlineAsm:
                foreach (Gpr r in i.Asm!.Uses) yield return (int)r;
                break;
            case MOp.Cwd:
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Cmpxchg:
                yield return (int)Gpr.Rax;
                break;
            case MOp.Div:
            case MOp.Idiv:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsq:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosq:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.In:
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Out:
                yield return (int)Gpr.Rdx;
                yield return (int)Gpr.Rax;
                break;
            case MOp.RepInsw:
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.RepOutsw:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.LoadSegments:
                yield return (int)Gpr.Rax;
                break;
            case MOp.Rdmsr:
                yield return (int)Gpr.Rcx;
                break;
            case MOp.Wrmsr:
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cpuid:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.CallKeep:
            case MOp.CallKeepRax:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.CallKeepCas:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Call:
            case MOp.CallInd:
                if (i.NativeAl)
                {
                    yield return (int)Gpr.Rax;
                }
                for (int k = 0; k < i.IntArgs; k++)
                {
                    yield return (int)LongAbi.IntArgRegs[k];
                }
                for (int k = 0; k < i.FloatArgs; k++)
                {
                    yield return MReg.XmmBase + k;
                }
                break;
            case MOp.Syscall:
            case MOp.SoftInt:
                yield return (int)Gpr.Rax;
                for (int k = 0; k < i.IntArgs; k++)
                {
                    yield return LongAbi.SyscallArgs[k];
                }
                break;
            case MOp.JmpInd:
                // Unwind's exception travels in RAX to the landing pad.
                yield return (int)Gpr.Rax;
                break;
            case MOp.Epilogue:
                if (i.IntArgs > 0)
                {
                    yield return (int)Gpr.Rax;
                }
                if (i.FloatArgs > 0)
                {
                    yield return MReg.XmmBase;
                }
                break;
        }
    }

    /// <summary>Registers an instruction writes without naming them, by id.</summary>
    public static IEnumerable<int> Defs(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.InlineAsm:
                foreach (Gpr r in i.Asm!.Defs) yield return (int)r;
                for (int x = 0; x < 16; x++) if ((i.Asm.XmmClobbers & (1u << x)) != 0) yield return MReg.XmmBase + x;
                break;
            case MOp.RepInsw:
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.RepOutsw:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.LoadCs:
                yield return (int)Gpr.Rax;
                break;
            case MOp.Rdmsr:
            case MOp.Rdtsc:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cpuid:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rbx;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cwd:
                yield return (int)Gpr.Rdx;
                break;
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cmpxchg:
            case MOp.In:
                yield return (int)Gpr.Rax;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsq:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosq:
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.CallKeepRax:
            case MOp.CallKeepCas:
                // A store sequence's stub keeps every register but the one it
                // answers in (X64Backend.RefStoreStub).
                yield return (int)Gpr.Rax;
                break;
            case MOp.CallSafe:
                // The general registers pushed around it (MOp.CallSafe); the
                // XMM ones a callee may destroy.
                for (int k = 0; k < 16; k++)
                {
                    yield return MReg.XmmBase + k;
                }
                break;
            case MOp.Call:
            case MOp.CallInd:
                // The callee may destroy every register the convention does
                // not make it preserve: nine general ones and all sixteen XMM.
                foreach (int r in LongAbi.CallerSaved)
                {
                    yield return r;
                }
                for (int k = 0; k < 16; k++)
                {
                    yield return MReg.XmmBase + k;
                }
                break;
            case MOp.Syscall:
            case MOp.SoftInt:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.R11;
                break;
        }
    }

    // ---- the same two answers as masks, a bit per register id -------------------
    //
    // For the passes that ask of every instruction, often (Peephole.Masks):
    // no iterator made, no register an object. General registers are bits
    // 0 to 15, XMM ones 16 to 31. WHAT THESE SAY IS WHAT Uses AND Defs SAY,
    // register for register (X86FamilyTests holds the two together).

    private const long Rax = 1L << (int)Gpr.Rax, Rcx = 1L << (int)Gpr.Rcx, Rdx = 1L << (int)Gpr.Rdx, Rbx = 1L << (int)Gpr.Rbx,
        Rsi = 1L << (int)Gpr.Rsi, Rdi = 1L << (int)Gpr.Rdi, R11 = 1L << (int)Gpr.R11;
    private const long AllXmm = 0xFFFFL << MReg.XmmBase;
    private static readonly long CallerSavedMask = MaskOf(LongAbi.CallerSaved);
    private static readonly long[] IntArgMasks = Prefixes(Array.ConvertAll(LongAbi.IntArgRegs, r => (int)r));
    private static readonly long[] SyscallArgMasks = Prefixes(LongAbi.SyscallArgs);

    private static long MaskOf(int[] ids)
    {
        long mask = 0;
        foreach (int id in ids) mask |= 1L << id;
        return mask;
    }

    /// <summary>For each count n, the first n registers of a list as a mask.</summary>
    private static long[] Prefixes(int[] ids)
    {
        long[] masks = new long[ids.Length + 1];
        for (int n = 1; n <= ids.Length; n++) masks[n] = masks[n - 1] | 1L << ids[n - 1];
        return masks;
    }

    /// <summary>What the instruction reads and writes without naming it, as masks by register id.</summary>
    public static void Masks(MInstr i, out long uses, out long defs)
    {
        uses = 0;
        defs = 0;
        switch (i.Op)
        {
            case MOp.InlineAsm:
                foreach (Gpr r in i.Asm!.Uses) uses |= 1L << (int)r;
                foreach (Gpr r in i.Asm.Defs) defs |= 1L << (int)r;
                defs |= (long)(i.Asm.XmmClobbers & 0xFFFF) << MReg.XmmBase;
                break;
            case MOp.Cwd:
                uses = Rax; defs = Rdx;
                break;
            case MOp.MulWide:
            case MOp.ImulWide:
                uses = Rax; defs = Rax | Rdx;
                break;
            case MOp.Cmpxchg:
                uses = Rax; defs = Rax;
                break;
            case MOp.Div:
            case MOp.Idiv:
                uses = Rax | Rdx; defs = Rax | Rdx;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsq:
                uses = Rsi | Rdi | Rcx; defs = Rsi | Rdi | Rcx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosq:
                uses = Rax | Rdi | Rcx; defs = Rdi | Rcx;
                break;
            case MOp.In:
                uses = Rdx; defs = Rax;
                break;
            case MOp.Out:
                uses = Rdx | Rax;
                break;
            case MOp.RepInsw:
                uses = Rdi | Rcx | Rdx; defs = Rdi | Rcx;
                break;
            case MOp.RepOutsw:
                uses = Rsi | Rcx | Rdx; defs = Rsi | Rcx;
                break;
            case MOp.LoadSegments:
                uses = Rax;
                break;
            case MOp.LoadCs:
                defs = Rax;
                break;
            case MOp.Rdmsr:
                uses = Rcx; defs = Rax | Rdx;
                break;
            case MOp.Rdtsc:
                defs = Rax | Rdx;
                break;
            case MOp.Wrmsr:
                uses = Rcx | Rax | Rdx;
                break;
            case MOp.Cpuid:
                uses = Rax | Rcx; defs = Rax | Rbx | Rcx | Rdx;
                break;
            case MOp.CallKeep:
                uses = Rax | Rdx;
                break;
            case MOp.CallKeepRax:
                uses = Rax | Rdx; defs = Rax;
                break;
            case MOp.CallKeepCas:
                uses = Rax | Rcx | Rdx; defs = Rax;
                break;
            case MOp.CallSafe:
                defs = AllXmm;
                break;
            case MOp.Call:
            case MOp.CallInd:
                if (i.NativeAl) uses |= Rax;
                uses |= IntArgMasks[i.IntArgs];
                uses |= ((1L << i.FloatArgs) - 1) << MReg.XmmBase;
                defs = CallerSavedMask | AllXmm;
                break;
            case MOp.Syscall:
            case MOp.SoftInt:
                uses = Rax | SyscallArgMasks[i.IntArgs];
                defs = Rax | Rcx | R11;
                break;
            case MOp.JmpInd:
                // Unwind's exception travels in RAX to the landing pad.
                uses = Rax;
                break;
            case MOp.Epilogue:
                if (i.IntArgs > 0) uses |= Rax;
                if (i.FloatArgs > 0) uses |= 1L << MReg.XmmBase;
                break;
        }
    }
}
