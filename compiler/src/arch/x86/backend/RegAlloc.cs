#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// THE FAMILY'S REGISTER ALLOCATION, both modes: the function's unused
/// values pruned, then lowered into the one allocator's form by the mode's
/// target (X86Target, LongTarget; what they share is FamilyTarget) and
/// allocated there (Corsac.Lang.RegAlloc.Allocator), which knows no
/// instruction set -- holes, short reloads carried along a run, a register
/// kept over the reads before a spill point, a loop's lap cost. Which
/// registers an instruction names and which of its operands may be memory
/// or an immediate is Roles' (RegsOf, Foldable, FoldableImm).
/// </summary>
internal static class Allocator
{
    public static void Run(MFunction m)
    {
        PruneUnusedValues(m);
        FamilyTarget target = X86Mode.Long ? new LongTarget(m) : new X86Target(m);
        Corsac.Lang.RegAlloc.Allocator.Run(target.Lower());
    }

    /// <summary>
    /// VALUES NOTHING READS, DROPPED BEFORE THE ALLOCATOR SEES THEM, in both
    /// modes: selection may make a value unused -- one half of a wide value
    /// on i386, a float constant's own load where every reader took the
    /// pool's word itself (Select.Sse's XSource), a copy whose readers were
    /// folded away -- and its dead copies would be spill traffic. A virtual
    /// register's alone (a machine register's write is somebody's argument
    /// or result, whether or not an instruction here says so). MOV changes
    /// no flags, and only register and immediate sources qualify: a memory
    /// read can be observable -- but the float pool's, read-only words.
    /// Arithmetic whose flags nothing reads goes with its result, at the
    /// width of a whole register write (four bytes, and long mode's eight).
    /// </summary>
    private static void PruneUnusedValues(MFunction m)
    {
        bool changed;
        List<(MReg Reg, Role Role, int Operand, bool InMem)> regs = new();
        // The sets and the predicate are made once and emptied each round,
        // not made again for every round and every block.
        HashSet<MInstr> deadFlags = new();
        HashSet<int> used = new();
        Predicate<MInstr> unused = i => i.Operands.Count > 0 && i.Operands[0] is MReg dest && !dest.IsMachine
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
                    if (i.Op == MOp.Cmov && !i.Lock && i.Operands[0] is MReg { IsMachine: false }) deadFlags.Add(i);
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

    private static bool PureArithmetic(MInstr i) => !i.Lock && (i.Width == 4 || i.Width == X86Mode.Word)
        && i.Operands.Count > 0 && i.Operands[0] is MReg d && !d.IsMachine
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
