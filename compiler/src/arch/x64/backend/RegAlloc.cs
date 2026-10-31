#nullable enable
namespace Corsac.Lang.X64;

/// <summary>What each operand of each opcode does to its register, and what each instruction touches unnamed.</summary>
internal static class Roles
{
    [Flags]
    public enum Role : byte
    {
        None = 0,
        Use = 1,
        Def = 2,
        UseDef = 3,
    }

    public static Role Of(MInstr i, int operand)
    {
        switch (i.Op)
        {
            case MOp.Mov:
            case MOp.Movsx:
            case MOp.Movzx:
            case MOp.Lea:
            case MOp.Pop:
            case MOp.Setcc:
            case MOp.Imul3:
            case MOp.MovF:
            case MOp.MovGx:
            case MOp.CallerReg:
            case MOp.CvtIntToF:
            case MOp.CvtFToInt:
            case MOp.CvtFToF:
            case MOp.SqrtF:
            case MOp.MovFromCr:
            case MOp.ReadFlags:
            case MOp.ReadCs:
            case MOp.Bsf:
            case MOp.Bsr:
                return operand == 0 ? Role.Def : Role.Use;
            case MOp.Add:
            case MOp.Sub:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Adc:
            case MOp.Sbb:
            case MOp.Imul:
            case MOp.Neg:
            case MOp.Not:
            case MOp.Bswap:
            case MOp.Shl:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Rol:
            case MOp.Ror:
            case MOp.Cmovcc:
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.XorF:
                return operand == 0 ? Role.UseDef : Role.Use;
            case MOp.Xchg:
                return Role.UseDef;
            case MOp.Xadd:
                return operand == 0 ? Role.Use : Role.UseDef;
            default:
                return Role.Use;
        }
    }

    private static readonly int[] CallerSavedGprs =
    {
        (int)Gpr.Rax, (int)Gpr.Rcx, (int)Gpr.Rdx, (int)Gpr.Rsi, (int)Gpr.Rdi,
        (int)Gpr.R8, (int)Gpr.R9, (int)Gpr.R10, (int)Gpr.R11,
    };

    private static readonly int[] SyscallArgs =
    {
        (int)Gpr.Rdi, (int)Gpr.Rsi, (int)Gpr.Rdx, (int)Gpr.R10, (int)Gpr.R8, (int)Gpr.R9,
    };

    /// <summary>Registers an instruction reads without naming them, by id.</summary>
    public static IEnumerable<int> ImplicitUses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.InlineAsm:
                foreach (int r in i.Asm!.Uses) yield return r;
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
                    yield return (int)Selector.IntArgRegs[k];
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
                    yield return SyscallArgs[k];
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
    public static IEnumerable<int> ImplicitDefs(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.InlineAsm:
                foreach (int r in i.Asm!.Defs) yield return r;
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
            case MOp.Call:
            case MOp.CallInd:
                // The callee may destroy every register the convention does
                // not make it preserve: nine general ones and all sixteen XMM.
                foreach (int r in CallerSavedGprs)
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

/// <summary>
/// x86-64 REGISTER ALLOCATION: the function lowered into the one allocator's
/// form (X64Target) and allocated there (Corsac.Lang.RegAlloc.Allocator), with
/// i386's method and everything it has learnt -- holes, short reloads carried
/// along a run, a register kept over the reads before a spill point, a loop's
/// lap cost; this file holds what is x86-64's alone: which registers an
/// instruction names and which of its operands may be memory.
/// </summary>
internal static class Allocator
{
    internal static readonly Gpr[] CalleeSaved = { Gpr.Rbx, Gpr.R12, Gpr.R13, Gpr.R14, Gpr.R15 };

    public static void Run(MFunction m) => Corsac.Lang.RegAlloc.Allocator.Run(new X64Target(m).Lower());

    internal static IEnumerable<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> RegsOf(MInstr i)
    {
        // `xor v, v` and `xorps x, x` make zero whatever v held: a definition only.
        bool zeroing = (i.Op is MOp.Xor or MOp.XorF) && i.Operands.Count == 2
            && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r:
                    yield return (r, zeroing ? Roles.Role.Def : Roles.Of(i, k), k, false);
                    break;
                case MMem m:
                    if (m.Base is not null)
                    {
                        yield return (m.Base, Roles.Role.Use, k, true);
                    }
                    if (m.Index is not null)
                    {
                        yield return (m.Index, Roles.Role.Use, k, true);
                    }
                    break;
            }
        }
        foreach (int g in Roles.ImplicitUses(i))
        {
            yield return (new MReg(g), Roles.Role.Use, -1, false);
        }
        foreach (int g in Roles.ImplicitDefs(i))
        {
            yield return (new MReg(g), Roles.Role.Def, -1, false);
        }
    }

    /// <summary>Whether an operand position may be a memory operand: a spill slot read or written in place.</summary>
    internal static bool Foldable(MInstr i, int operand)
    {
        switch (i.Op)
        {
            case MOp.Mov:
                // Never a memory destination for a 64-bit immediate: there is no such form.
                return operand == 1 || i.Operands[1] is not MImm { FitsInt32: false };
            case MOp.Add:
            case MOp.Sub:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Adc:
            case MOp.Sbb:
            case MOp.Cmp:
                return true;
            case MOp.Push:
            case MOp.Test:
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
            case MOp.Neg:
            case MOp.Not:
            case MOp.Shl:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Rol:
            case MOp.Ror:
            case MOp.Setcc:
            case MOp.CallInd:
                return operand == 0;
            case MOp.Imul:
            case MOp.Imul3:
            case MOp.Movsx:
            case MOp.Movzx:
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.SqrtF:
            case MOp.UcomiF:
            case MOp.CvtIntToF:
            case MOp.CvtFToInt:
            case MOp.CvtFToF:
                return operand == 1;
            case MOp.MovF:
                return true;
            case MOp.MovGx:
                // The general register's side may be memory; the XMM side may not.
                return i.Operands[operand] is MReg r && !r.IsFloat;
            default:
                return false;
        }
    }

}
