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

    /// <summary>Registers an instruction reads without naming them.</summary>
    public static IEnumerable<Gpr> ImplicitUses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Cdq:
                yield return Gpr.Eax;
                break;
            case MOp.Mul:
            case MOp.ImulWide:
                yield return Gpr.Eax;
                break;
            case MOp.Div:
            case MOp.Idiv:
                yield return Gpr.Eax;
                yield return Gpr.Edx;
                break;
            case MOp.Cmpxchg:
                yield return Gpr.Eax;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
                yield return Gpr.Esi;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosd:
                yield return Gpr.Eax;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.Sahf:
                yield return Gpr.Eax;
                break;
            case MOp.In:
                yield return Gpr.Edx;
                break;
            case MOp.Out:
                yield return Gpr.Edx;
                yield return Gpr.Eax;
                break;
            case MOp.LoadSegments:
                yield return Gpr.Eax;
                break;
            case MOp.RepInsw:
                yield return Gpr.Edx;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepOutsw:
                yield return Gpr.Edx;
                yield return Gpr.Esi;
                yield return Gpr.Ecx;
                break;
            case MOp.SyscallTrap:
                // Linux reads six arguments; the sixth is in EBP, which the
                // selector borrows around the trap when there is one.
                yield return Gpr.Eax;
                yield return Gpr.Ebx;
                yield return Gpr.Ecx;
                yield return Gpr.Edx;
                yield return Gpr.Esi;
                yield return Gpr.Edi;
                yield return Gpr.Ebp;
                break;
            case MOp.Epilogue:
                // The return value is already in place; keep it alive through the pops.
                yield return Gpr.Eax;
                yield return Gpr.Edx;
                break;
        }
    }

    /// <summary>Registers an instruction writes without naming them.</summary>
    public static IEnumerable<Gpr> ImplicitDefs(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Cdq:
                yield return Gpr.Edx;
                break;
            case MOp.Mul:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
                yield return Gpr.Eax;
                yield return Gpr.Edx;
                break;
            case MOp.Cmpxchg:
                yield return Gpr.Eax;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsd:
                yield return Gpr.Esi;
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosd:
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.Fnstsw:
                yield return Gpr.Eax;
                break;
            case MOp.Call:
            case MOp.CallInd:
                // cdecl: the callee may destroy these.
                yield return Gpr.Eax;
                yield return Gpr.Ecx;
                yield return Gpr.Edx;
                break;
            case MOp.SyscallTrap:
            case MOp.In:
                yield return Gpr.Eax;
                break;
            case MOp.RepInsw:
                yield return Gpr.Edi;
                yield return Gpr.Ecx;
                break;
            case MOp.RepOutsw:
                yield return Gpr.Esi;
                yield return Gpr.Ecx;
                break;
        }
    }
}
