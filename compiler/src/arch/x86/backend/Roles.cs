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

    /// <summary>
    /// THE REGISTERS AN INSTRUCTION NAMES, with what it does with each, into
    /// `into` (emptied first), which is returned: its operands in order -- a
    /// memory operand's base and index only ever read -- then the registers
    /// it reads without naming them, then those it writes so (Implicit). A list the caller
    /// keeps for the whole pass: as an iterator it was an object for every
    /// instruction every walk looked at. One reading for both modes.
    /// </summary>
    internal static List<(MReg Reg, Role Role, int Operand, bool InMem)> RegsOf(MInstr i, List<(MReg Reg, Role Role, int Operand, bool InMem)> into)
    {
        into.Clear();
        // `xor v, v`, `xorps x, x` and `xorpd x, x` make zero whatever the
        // register held: a definition only, so no reload of a spilled v
        // before it, and no value of v's alive back from it.
        bool zeroing = i.Op is MOp.Xor or MOp.XorPs or MOp.XorF && i.Operands.Count == 2
            && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r:
                    into.Add((r, zeroing ? Role.Def : Of(i.Op, k), k, false));
                    break;
                case MMem m:
                    if (m.Base is not null) into.Add((m.Base, Role.Use, k, true));
                    if (m.Index is not null) into.Add((m.Index, Role.Use, k, true));
                    break;
            }
        }
        foreach (int g in Implicit.Uses(i).Ids) into.Add((MReg.Of(g), Role.Use, -1, false));
        foreach (int g in Implicit.Defs(i).Ids) into.Add((MReg.Of(g), Role.Def, -1, false));
        return into;
    }

    /// <summary>
    /// Whether an operand position may be a memory operand: a spill slot
    /// read or written in place. One table for both modes -- an opcode only
    /// one of them makes is in it for that one -- and long mode's two rules
    /// of its own: no 64-bit immediate goes to memory, and of a move between
    /// the two register files only the general register's side may be memory.
    /// </summary>
    internal static bool Foldable(MFunction m, MInstr i, int operand)
    {
        switch (i.Op)
        {
            case MOp.Mov:
                // Never a memory destination for a 64-bit immediate: there is no such form.
                return operand == 1 || !X86Mode.Long || i.Operands[1] is not MImm { FitsInt32: false };
            case MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp:
                return true;
            case MOp.Push or MOp.Test or MOp.Mul or MOp.MulWide or MOp.ImulWide or MOp.Div or MOp.Idiv or MOp.Neg or MOp.Not
                or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Rol or MOp.Ror or MOp.Shld or MOp.Shrd or MOp.Setcc or MOp.CallInd:
                return operand == 0;
            // (A conditional move reads its source whether it moves or not: a
            // spill slot, which is always there to read.)
            case MOp.Imul or MOp.Imul3 or MOp.Movzx or MOp.Movsx or MOp.Cmov:
                return operand == 1;
            // The scalar SSE forms: a move either way, every other source.
            // (The family's truncations, which a float's conversion to
            // thirty-two bits is, take xmm/m32 or xmm/m64 as their source.)
            case MOp.MovF:
                return true;
            case MOp.AddF or MOp.SubF or MOp.MulF or MOp.DivF or MOp.SqrtF or MOp.UcomiF or MOp.CvtFToF or MOp.CvtIntToF
                or MOp.CvtFToInt or MOp.Cvttss2si or MOp.Cvttsd2si or MOp.MovdToX:
                return operand == 1;
            case MOp.MovdFromX:
                return operand == 0;
            case MOp.MovGx:
                // The general register's side may be memory; the XMM side may
                // not. (By the function's record of which registers are XMM
                // ones: a renamed operand does not say, MReg.IsFloat.)
                return i.Operands[operand] is MReg r && !r.IsFloat && !m.XmmRegs.Contains(r.Id);
            default:
                return false;
        }
    }

    /// <summary>Whether an operand position accepts an immediate in place of a register.</summary>
    internal static bool FoldableImm(MOp op, int operand) => op switch
    {
        MOp.Mov or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp => operand == 1,
        MOp.Push => true,
        _ => false,
    };

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
            case MOp.GsLoadAt:
            case MOp.GsLoadReg:
            case MOp.GetGs:
            case MOp.ReadCs:
            case MOp.ReadFlags:
            case MOp.Bsf:
            case MOp.Bsr:
            case MOp.MmxFindW4:
            case MOp.MmxFindW8:
            case MOp.MmxEqual8:
            case MOp.MmxExtractW:
            case MOp.XmmExtractW:
            case MOp.Cvttss2si:
            case MOp.Cvttsd2si:
            case MOp.MovF:
            case MOp.SqrtF:
            case MOp.CvtFToF:
            case MOp.CvtIntToF:
            case MOp.MovdToX:
            case MOp.MovdFromX:
            case MOp.CallerReg:
            case MOp.XmmExtractV:
            case MOp.MovGx:
            case MOp.CvtFToInt:
                return operand == 0 ? Role.Def : Role.Use;
            case MOp.XmmShiftV:
            case MOp.XmmInsertV:
                return operand == 0 ? Role.UseDef : Role.Use;
            case MOp.XmmOpV:
                return operand == 2 ? Role.UseDef : Role.Use;
            case MOp.XmmOpVDef:
                return operand == 2 ? Role.Def : Role.Use;
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
            case MOp.Inc:
            case MOp.Dec:
            case MOp.Bswap:
            case MOp.Shl:
            case MOp.Ror:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Shld:
            case MOp.Shrd:
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.XorPs:
            case MOp.Punpckldq:
            case MOp.Rol:
            case MOp.XorF:
                return operand == 0 ? Role.UseDef : Role.Use;
            case MOp.Xchg:
            case MOp.FsLoad:
                return Role.UseDef;
            case MOp.FsSehChainStore:
                return Role.Use;
            case MOp.FsSehChain:
                return Role.Def;
            case MOp.Cmov:
                // A CONDITIONAL WRITE READS WHAT IT MAY KEEP: where the
                // condition fails the register's old value is the result, so
                // that value is live into the instruction.
                return operand == 0 ? Role.UseDef : Role.Use;
            case MOp.Xadd:
                // [mem], reg: the register receives the old value.
                return operand == 0 ? Role.Use : Role.UseDef;
            default:
                return Role.Use;
        }
    }
}
