#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// i386 REGISTER ALLOCATION: the function's dead wide halves pruned, then
/// lowered into the one allocator's form (X86Target) and allocated there
/// (Corsac.Lang.RegAlloc.Allocator), which knows no instruction set; this
/// file holds what is i386's alone, the pruning. Which registers an
/// instruction names and which of its operands may be memory or an
/// immediate is the family's (Roles.RegsOf, Foldable, FoldableImm).
/// </summary>
internal static class Allocator
{
    public static void Run(MFunction m)
    {
        PruneUnusedValues(m);
        X86Target target = new(m);
        Corsac.Lang.RegAlloc.Allocator.Run(target.Lower());
    }

    private static void PruneUnusedValues(MFunction m)
    {
        // Selection may make one half of a wide value unused. Do not create
        // spill traffic for its dead copies. MOV changes no flags, and only
        // register/immediate sources qualify: memory reads can be observable.
        bool changed;
        List<(MReg Reg, Role Role, int Operand, bool InMem)> regs = new();
        // The sets and the predicate are made once and emptied each round,
        // not made again for every round and every block.
        HashSet<MInstr> deadFlags = new();
        HashSet<int> used = new();
        Predicate<MInstr> unused = i => i.Operands.Count > 0 && i.Operands[0] is MReg dest && !dest.IsPhys
            && !used.Contains(dest.Id) && (deadFlags.Contains(i)
                || (i.Op == MOp.Mov && !i.Lock && i.Operands.Count == 2 && i.Operands[1] is not MMem)
                // An XMM copy, or a load of the float pool's read-only words --
                // by its address, or off the GOT in position-independent code
                // (Pic: [got + pool@GOTOFF]): nothing seen.
                || (i.Op == MOp.MovF && i.Operands.Count == 2 && i.Operands[0] is MReg
                    && (i.Operands[1] is MReg || i.Operands[1] is MMem { Index: null, Symbol: { } pool }
                        && pool.StartsWith(X86Backend.FloatPoolPrefix, StringComparison.Ordinal))));
        do
        {
            deadFlags.Clear();
            foreach (MBlock block in m.Blocks)
            {
                // No assumptions about flags on another block's edges.
                bool flagsLive = true;
                for (int k = block.Instrs.Count - 1; k >= 0; k--)
                {
                    MInstr i = block.Instrs[k];
                    if ((!flagsLive || i.Op == MOp.Not) && PureArithmetic(i)) deadFlags.Add(i);
                    // A CONDITIONAL MOVE LEAVES NOTHING BUT ITS REGISTER: it sets
                    // no flag, and its sources are registers or memory that
                    // cannot fault (IfConvert). Read only by itself -- the half
                    // of a long nothing reads, kept by cmov after cmov -- it goes.
                    if (i.Op == MOp.Cmov && !i.Lock && i.Operands[0] is MReg { IsPhys: false }) deadFlags.Add(i);
                    // Fucomipp and UcomiF write every status flag, as cmp does: ZF, PF and CF
                    // from the comparison, OF, SF and AF cleared.
                    // BSF and BSR leave ZF and every other flag undefined; POPFD
                    // writes them all.
                    if (i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or
                        or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Neg or MOp.Fucomipp or MOp.UcomiF
                        or MOp.Bsf or MOp.Bsr or MOp.WriteFlags) flagsLive = false;
                    if (i.Op is MOp.Adc or MOp.Sbb or MOp.Jcc or MOp.Setcc or MOp.Cmov) flagsLive = true;
                    else if (!KnownFlagEffect(i.Op)) flagsLive = true;
                }
            }
            used.Clear();
            foreach (MBlock block in m.Blocks)
            foreach (MInstr instruction in block.Instrs)
                foreach (var operand in Roles.RegsOf(instruction, regs))
                    if ((operand.Role & Role.Use) != 0
                        // A dead two-address update does not make its own
                        // discarded value live. Kept flag-producing updates
                        // still contribute that input, preserving their chain.
                        && !(operand.Operand == 0 && deadFlags.Contains(instruction)
                            && (operand.Role & Role.Def) != 0)) used.Add(operand.Reg.Id);
            changed = false;
            foreach (MBlock block in m.Blocks)
                changed |= block.Instrs.RemoveAll(unused) != 0;
        } while (changed);
    }

    private static bool PureArithmetic(MInstr i) => !i.Lock && i.Width == 4
        && i.Operands.Count > 0 && i.Operands[0] is MReg d && !d.IsPhys
        && RegistersAndImmediates(i)
        && i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor
            or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Not or MOp.Shl or MOp.Shr or MOp.Sar
            or MOp.Ror or MOp.Shld or MOp.Shrd;

    private static bool RegistersAndImmediates(MInstr i)
    {
        foreach (MOperand o in i.Operands)
        {
            if (o is not (MReg or MImm)) return false;
        }
        return true;
    }

    // Partial/conditional flag writers do not kill liveness: a zero shift
    // preserves flags, and rotate/multiply do not define all condition bits.
    // Unlisted instructions (notably calls and traps) remain barriers.
    private static bool KnownFlagEffect(MOp op) => op is
        MOp.Mov or MOp.Movsx or MOp.Movzx or MOp.Lea or MOp.Xchg or MOp.Push or MOp.Pop
        or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Test
        or MOp.Imul or MOp.Imul3 or MOp.Mul or MOp.ImulWide or MOp.Div or MOp.Idiv or MOp.Neg or MOp.Not or MOp.Bswap
        or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Ror or MOp.Shld or MOp.Shrd or MOp.Cdq
        or MOp.Setcc or MOp.Cmov or MOp.Jcc or MOp.Jmp or MOp.JmpInd or MOp.JmpTable or MOp.Ret or MOp.Nop or MOp.Pause
        or MOp.RepMovsb or MOp.RepMovsd or MOp.RepStosb or MOp.RepStosd or MOp.Fucomipp or MOp.UcomiF
        or MOp.Bsf or MOp.Bsr or MOp.WriteFlags
        // The scalar SSE forms leave every flag as it was.
        || Isa.FlagNeutralScalar(op);
}
