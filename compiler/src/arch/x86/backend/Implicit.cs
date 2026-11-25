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
}
