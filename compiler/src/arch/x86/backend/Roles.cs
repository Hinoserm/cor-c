#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// What each operand of each opcode does to its register, so liveness and
/// the allocator need not know the instruction set. An operand that is a
/// memory reference only ever reads its base and index registers.
/// </summary>
public static class Roles
{
    [Flags]
    public enum Role : byte
    {
        None = 0,
        Use = 1,
        Def = 2,
        UseDef = 3,
    }

    public static Role Of(MOp op, int operand)
    {
        switch (op)
        {
            case MOp.Mov:
            case MOp.Movsx:
            case MOp.Movzx:
            case MOp.Lea:
            case MOp.Pop:
            case MOp.Setcc:
            case MOp.Imul3:
            case MOp.MovFromCr:
            case MOp.GotPc:
            case MOp.GsSelf:
            case MOp.GetGs:
                return operand == 0 ? Role.Def : Role.Use;
            case MOp.Add:
            case MOp.Adc:
            case MOp.Sub:
            case MOp.Sbb:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Imul:
            case MOp.Neg:
            case MOp.Not:
            case MOp.Bswap:
            case MOp.Shl:
            case MOp.Ror:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Shld:
            case MOp.Shrd:
                return operand == 0 ? Role.UseDef : Role.Use;
            case MOp.Xchg:
                return Role.UseDef;
            case MOp.Xadd:
                // [mem], reg: the register receives the old value.
                return operand == 0 ? Role.Use : Role.UseDef;
            default:
                return Role.Use;
        }
    }

    // WHAT THESE ANSWER IS FIXED BY THE OPCODE, so it is a table shared by every
    // call. As iterators they made an object for every instruction the
    // register allocator looked at, twice.
    private static readonly Gpr[] NoneGpr_ = new Gpr[0];
    private static readonly Gpr[] Gpr_ImplicitUses0 = { Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitUses1 = { Gpr.Ecx, Gpr.Eax, Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitUses2 = { Gpr.Eax, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitUses3 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitUses4 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitUses5 = { Gpr.Eax, Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitUses6 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitUses7 = { Gpr.Esi, Gpr.Edi, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitUses8 = { Gpr.Eax, Gpr.Edi, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitUses9 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitUses10 = { Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitUses11 = { Gpr.Edx, Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitUses12 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitUses13 = { Gpr.Edx, Gpr.Edi, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitUses14 = { Gpr.Edx, Gpr.Esi, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitUses15 = { Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx, Gpr.Esi, Gpr.Edi, Gpr.Ebp };
    private static readonly Gpr[] Gpr_ImplicitUses16 = { Gpr.Eax, Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitDefs0 = { Gpr.Eax, Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitDefs1 = { Gpr.Eax, Gpr.Ebx, Gpr.Ecx, Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitDefs2 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitDefs3 = { Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitDefs4 = { Gpr.Eax, Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitDefs5 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitDefs6 = { Gpr.Esi, Gpr.Edi, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitDefs7 = { Gpr.Edi, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitDefs8 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitDefs9 = { Gpr.Eax, Gpr.Ecx, Gpr.Edx };
    private static readonly Gpr[] Gpr_ImplicitDefs10 = { Gpr.Eax };
    private static readonly Gpr[] Gpr_ImplicitDefs11 = { Gpr.Edi, Gpr.Ecx };
    private static readonly Gpr[] Gpr_ImplicitDefs12 = { Gpr.Esi, Gpr.Ecx };

    /// <summary>Registers an instruction reads without naming them.</summary>
    public static Gpr[] ImplicitUses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Rdmsr:
                return Gpr_ImplicitUses0;
            case MOp.Wrmsr:
                return Gpr_ImplicitUses1;
            case MOp.Cpuid:
                return Gpr_ImplicitUses2;
            case MOp.Cdq:
                return Gpr_ImplicitUses3;
            case MOp.Mul:
            case MOp.ImulWide:
                return Gpr_ImplicitUses4;
            case MOp.Div:
            case MOp.Idiv:
                return Gpr_ImplicitUses5;
            case MOp.Cmpxchg:
                return Gpr_ImplicitUses6;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
                return Gpr_ImplicitUses7;
            case MOp.RepStosb:
            case MOp.RepStosd:
                return Gpr_ImplicitUses8;
            case MOp.Sahf:
                return Gpr_ImplicitUses9;
            case MOp.In:
                return Gpr_ImplicitUses10;
            case MOp.Out:
                return Gpr_ImplicitUses11;
            case MOp.LoadSegments:
                return Gpr_ImplicitUses12;
            case MOp.RepInsw:
                return Gpr_ImplicitUses13;
            case MOp.RepOutsw:
                return Gpr_ImplicitUses14;
            case MOp.SyscallTrap:
                // Linux reads six arguments; the sixth is in EBP, which the
                // selector borrows around the trap when there is one.
                return Gpr_ImplicitUses15;
            case MOp.Epilogue:
                // The return value is already in place; keep it alive through the pops.
                // Only the registers it occupies: EDX read by every return of a
                // function that never writes it is live from entry, and busy in
                // the whole function (EAX too, in one that returns nothing).
                return i.Width switch { 8 => Gpr_ImplicitUses16, 4 => Gpr_ImplicitUses3, _ => NoneGpr_ };
            default:
                return NoneGpr_;
        }
    }

    /// <summary>Registers an instruction writes without naming them.</summary>
    public static Gpr[] ImplicitDefs(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Rdmsr:
            case MOp.Rdtsc:
                return Gpr_ImplicitDefs0;
            case MOp.Cpuid:
                return Gpr_ImplicitDefs1;
            case MOp.LoadCs:
                return Gpr_ImplicitDefs2;
            case MOp.Cdq:
                return Gpr_ImplicitDefs3;
            case MOp.Mul:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
                return Gpr_ImplicitDefs4;
            case MOp.Cmpxchg:
            // A reference exchange's stub answers in EAX (X86Backend).
            case MOp.CallKeepEax:
            case MOp.CallKeepEaxInd:
                return Gpr_ImplicitDefs5;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
                return Gpr_ImplicitDefs6;
            case MOp.RepStosb:
            case MOp.RepStosd:
                return Gpr_ImplicitDefs7;
            case MOp.Fnstsw:
                return Gpr_ImplicitDefs8;
            case MOp.Call:
            case MOp.CallInd:
                // cdecl: the callee may destroy these.
                return Gpr_ImplicitDefs9;
            case MOp.SyscallTrap:
            case MOp.In:
                return Gpr_ImplicitDefs10;
            case MOp.RepInsw:
                return Gpr_ImplicitDefs11;
            case MOp.RepOutsw:
                return Gpr_ImplicitDefs12;
            default:
                return NoneGpr_;
        }
    }
}
