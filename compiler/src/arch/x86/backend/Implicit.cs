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

    // ---- what a call, a system call and a return touch without naming it ----

    private static readonly RegSet KeepUses = RegSet.Of(Gpr.Rax, Gpr.Rdx), CasUses = RegSet.Of(Gpr.Rax, Gpr.Rcx, Gpr.Rdx),
        Rax = RegSet.Of(Gpr.Rax), SyscallDefs = RegSet.Of(Gpr.Rax, Gpr.Rcx, Gpr.R11);
    private static readonly RegSet AllXmm = new(XmmFrom(0, 16)), CallDefs = new(CallerSaved.Concat(XmmFrom(0, 16)).ToArray());
    /// <summary>A call's reads by [AL named][integer arguments][float arguments]: RAX where a native callee reads AL, then the arguments' registers in order.</summary>
    private static readonly RegSet[][][] CallUses = MakeCallUses();
    /// <summary>A system call's reads by argument count: its number in RAX, then its arguments' registers.</summary>
    private static readonly RegSet[] SyscallUses = MakeSyscallUses();
    /// <summary>A return's reads by [an integer result][a float result]: RAX, XMM0.</summary>
    private static readonly RegSet[][] ReturnUses =
    {
        new[] { RegSet.None, new RegSet(new[] { MReg.XmmBase }) },
        new[] { Rax, new RegSet(new[] { (int)Gpr.Rax, MReg.XmmBase }) },
    };

    private static int[] XmmFrom(int first, int count)
    {
        int[] ids = new int[count];
        for (int k = 0; k < count; k++) ids[k] = MReg.XmmBase + first + k;
        return ids;
    }

    private static RegSet[][][] MakeCallUses()
    {
        RegSet[][][] made = new RegSet[2][][];
        for (int al = 0; al < 2; al++)
        {
            made[al] = new RegSet[IntArgRegs.Length + 1][];
            for (int ints = 0; ints <= IntArgRegs.Length; ints++)
            {
                made[al][ints] = new RegSet[FloatArgRegs + 1];
                for (int floats = 0; floats <= FloatArgRegs; floats++)
                {
                    List<int> ids = new();
                    if (al == 1) ids.Add((int)Gpr.Rax);
                    for (int k = 0; k < ints; k++) ids.Add((int)IntArgRegs[k]);
                    for (int k = 0; k < floats; k++) ids.Add(MReg.XmmBase + k);
                    made[al][ints][floats] = new RegSet(ids.ToArray());
                }
            }
        }
        return made;
    }

    private static RegSet[] MakeSyscallUses()
    {
        RegSet[] made = new RegSet[SyscallArgs.Length + 1];
        for (int n = 0; n <= SyscallArgs.Length; n++)
        {
            int[] ids = new int[n + 1];
            ids[0] = (int)Gpr.Rax;
            for (int k = 0; k < n; k++) ids[k + 1] = SyscallArgs[k];
            made[n] = new RegSet(ids);
        }
        return made;
    }

    /// <summary>What the convention's own instructions read without naming it.</summary>
    internal static RegSet Uses(MInstr i) => i.Op switch
    {
        // A store sequence's stub takes its two words in RAX and RDX, an
        // exchange's its third in RCX (X64Backend.RefStoreStub).
        MOp.CallKeep or MOp.CallKeepRax => KeepUses,
        MOp.CallKeepCas => CasUses,
        MOp.Call or MOp.CallInd => CallUses[i.NativeAl ? 1 : 0][i.IntArgs][i.FloatArgs],
        MOp.Syscall or MOp.SoftInt => SyscallUses[i.IntArgs],
        // Unwind's exception travels in RAX to the landing pad.
        MOp.JmpInd => Rax,
        MOp.Epilogue => ReturnUses[i.IntArgs > 0 ? 1 : 0][i.FloatArgs > 0 ? 1 : 0],
        _ => RegSet.None,
    };

    /// <summary>What the convention's own instructions write without naming it.</summary>
    internal static RegSet Defs(MInstr i) => i.Op switch
    {
        // A store sequence's stub keeps every register but the one it
        // answers in (X64Backend.RefStoreStub).
        MOp.CallKeepRax or MOp.CallKeepCas => Rax,
        // The general registers pushed around it (MOp.CallSafe); the XMM
        // ones a callee may destroy.
        MOp.CallSafe => AllXmm,
        // The callee may destroy every register the convention does not
        // make it preserve: nine general ones and all sixteen XMM.
        MOp.Call or MOp.CallInd => CallDefs,
        MOp.Syscall or MOp.SoftInt => SyscallDefs,
        _ => RegSet.None,
    };
}

/// <summary>
/// i386'S CALLING CONVENTIONS, by register: C's, with every argument on the
/// stack. (An XMM register is written by every call too, said where a
/// function has that class: X86Target.LowerInstr.)
/// </summary>
internal static class Cdecl
{
    private static readonly RegSet Eax = RegSet.Of(Gpr.Eax), EaxEdx = RegSet.Of(Gpr.Eax, Gpr.Edx),
        CallDefs = RegSet.Of(Gpr.Eax, Gpr.Ecx, Gpr.Edx), CalleeSaved = RegSet.Of(Gpr.Ebx, Gpr.Esi, Gpr.Edi),
        EverySyscallUse = RegSet.Of(Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx, Gpr.Esi, Gpr.Edi, Gpr.Ebp);
    /// <summary>A system call's number and its first n arguments, by n up to five (the sixth borrows EBP: all of them).</summary>
    private static readonly RegSet[] SyscallUses =
    {
        RegSet.Of(Gpr.Eax), RegSet.Of(Gpr.Eax, Gpr.Ebx), RegSet.Of(Gpr.Eax, Gpr.Ebx, Gpr.Ecx),
        RegSet.Of(Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx), RegSet.Of(Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx, Gpr.Esi),
        RegSet.Of(Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx, Gpr.Esi, Gpr.Edi),
    };

    /// <summary>What the convention's own instructions read without naming it.</summary>
    internal static RegSet Uses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.SyscallTrap:
                // THE ARGUMENTS THE CALL HAS, and no more: its count is the
                // second operand (Select's SelectSyscall). All six read by
                // every trap kept ESI and EDI alive back to whatever last set
                // them -- a write's three arguments held both across every
                // loop between it and an mmap before it. The sixth is in EBP,
                // which the selector borrows around the trap when there is one.
                return i.Operands.Count > 1 && i.Operands[1] is MImm { Value: var args and >= 0 and <= 5 }
                    ? SyscallUses[args] : EverySyscallUse;
            case MOp.Epilogue:
                // The return value is already in place; keep it alive through the pops.
                // Only the registers it occupies: EDX read by every return of a
                // function that never writes it is live from entry, and busy in
                // the whole function (EAX too, in one that returns nothing).
                return i.Width switch { 8 => EaxEdx, 4 => Eax, _ => RegSet.None };
            default:
                return RegSet.None;
        }
    }

    /// <summary>What the convention's own instructions write without naming it.</summary>
    internal static RegSet Defs(MInstr i) => i.Op switch
    {
        // cdecl: the callee may destroy these.
        MOp.Call or MOp.CallInd => CallDefs,
        // A reference exchange's stub answers in EAX (X86Backend).
        MOp.CallKeepEax or MOp.CallKeepEaxInd => Eax,
        MOp.SyscallTrap => Eax,
        MOp.SaveRegs => CalleeSaved,
        _ => RegSet.None,
    };
}

/// <summary>
/// Registers an instruction touches without naming them: their ids in the
/// order the allocator is told them, and the same as a mask, a bit per id
/// (general registers bits 0 to 15, XMM ones 16 to 31) for the passes that
/// ask of every instruction, often. Made once and shared: nothing made per
/// instruction asked.
/// </summary>
internal sealed class RegSet
{
    public static readonly RegSet None = new(new int[0]);

    public readonly int[] Ids;
    public readonly long Mask;

    public RegSet(int[] ids)
    {
        Ids = ids;
        foreach (int id in ids) Mask |= 1L << id;
    }

    public static RegSet Of(params Gpr[] registers) => new(Array.ConvertAll(registers, r => (int)r));

    public bool Contains(int reg) => (uint)reg < 64u && (Mask >> reg & 1) != 0;
}

/// <summary>
/// WHAT EACH INSTRUCTION TOUCHES WITHOUT NAMING IT. ONE TABLE FOR BOTH MODES:
/// an instruction's own registers (a divide's, a string move's, CPUID's) are
/// the processor's, the same by number in either mode, and what a call, a
/// system call and a return touch is the mode's convention's (LongAbi,
/// Cdecl). What an operand does to its register is Roles.Of.
/// </summary>
internal static class Implicit
{
    private static readonly RegSet A = RegSet.Of(Gpr.Eax), C = RegSet.Of(Gpr.Ecx), D = RegSet.Of(Gpr.Edx),
        AD = RegSet.Of(Gpr.Eax, Gpr.Edx), AC = RegSet.Of(Gpr.Eax, Gpr.Ecx), DA = RegSet.Of(Gpr.Edx, Gpr.Eax),
        CAD = RegSet.Of(Gpr.Ecx, Gpr.Eax, Gpr.Edx), ABCD = RegSet.Of(Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx),
        Cmpxchg8bUses = RegSet.Of(Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx, Gpr.Esi),
        SiDiC = RegSet.Of(Gpr.Esi, Gpr.Edi, Gpr.Ecx), ADiC = RegSet.Of(Gpr.Eax, Gpr.Edi, Gpr.Ecx),
        DiC = RegSet.Of(Gpr.Edi, Gpr.Ecx), SiC = RegSet.Of(Gpr.Esi, Gpr.Ecx),
        DDiC = RegSet.Of(Gpr.Edx, Gpr.Edi, Gpr.Ecx), DSiC = RegSet.Of(Gpr.Edx, Gpr.Esi, Gpr.Ecx);

    /// <summary>Registers an instruction reads without naming them.</summary>
    public static RegSet Uses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.InlineAsm:
                return i.Asm!.UseSet;
            case MOp.Rdmsr:
                return C;
            case MOp.Wrmsr:
                return CAD;
            case MOp.Cpuid:
                return AC;
            case MOp.Cmpxchg8b:
                return Cmpxchg8bUses;
            case MOp.PackedSaveX87:
            case MOp.PackedRestoreX87:
            case MOp.PackedSaveFxsr:
            case MOp.PackedRestoreFxsr:
            case MOp.Cdq:
            case MOp.Cwd:
            case MOp.Mul:
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Cmpxchg:
            case MOp.Sahf:
            case MOp.LoadSegments:
                return A;
            case MOp.Div:
            case MOp.Idiv:
                return AD;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
            case MOp.RepMovsq:
                return SiDiC;
            case MOp.RepStosb:
            case MOp.RepStosd:
            case MOp.RepStosq:
                return ADiC;
            case MOp.In:
                return D;
            case MOp.Out:
                return DA;
            case MOp.RepInsw:
                return DDiC;
            case MOp.RepOutsw:
                return DSiC;
            default:
                return X86Mode.Long ? LongAbi.Uses(i) : Cdecl.Uses(i);
        }
    }

    /// <summary>Registers an instruction writes without naming them.</summary>
    public static RegSet Defs(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.InlineAsm:
                return i.Asm!.DefSet;
            case MOp.Rdmsr:
            case MOp.Rdtsc:
            case MOp.Mul:
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
            case MOp.Cmpxchg8b:
                return AD;
            case MOp.Cpuid:
                return ABCD;
            case MOp.LoadCs:
            case MOp.Cmpxchg:
            case MOp.Fnstsw:
            case MOp.In:
                return A;
            case MOp.Cdq:
            case MOp.Cwd:
                return D;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
            case MOp.RepMovsq:
                return SiDiC;
            case MOp.RepStosb:
            case MOp.RepStosd:
            case MOp.RepStosq:
            case MOp.RepInsw:
                return DiC;
            case MOp.RepOutsw:
                return SiC;
            default:
                return X86Mode.Long ? LongAbi.Defs(i) : Cdecl.Defs(i);
        }
    }
}
