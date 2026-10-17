#nullable enable
namespace Corsac.Lang.X64;

/// <summary>
/// The control-flow tidying after allocation, where the blocks are final:
///
///   - a jump to a block that holds nothing but another jump goes straight
///     to that jump's target (threading), however long the chain;
///   - `jcc next; jmp other` becomes `j!cc other` and falls through;
///   - a jump to the very next block is dropped: control falls into it.
///
/// The selector writes every branch as a conditional jump and an
/// unconditional one, so that nothing depends on block order; this is where
/// block order is known and the jumps it makes redundant go.
///
/// And one move: `mov r32, r32` onto itself, where the register's last
/// write in the block was already a 32-bit one (ZeroExtended).
/// </summary>
internal static class Peephole
{
    public static void Run(MFunction m)
    {
        foreach (MBlock b in m.Blocks)
        {
            DropZeroExtensions(b.Instrs);
        }
        Thread(m);
        for (int i = 0; i < m.Blocks.Count; i++)
        {
            MBlock b = m.Blocks[i];
            MBlock? next = i + 1 < m.Blocks.Count ? m.Blocks[i + 1] : null;
            List<MInstr> s = b.Instrs;
            if (next is null || s.Count == 0)
            {
                continue;
            }
            MInstr last = s[^1];
            if (last.Op == MOp.Jmp && ((MLabel)last.Operands[0]).Target == next)
            {
                s.RemoveAt(s.Count - 1);
                if (s.Count == 0)
                {
                    continue;
                }
                last = s[^1];
            }
            if (s.Count >= 2 && last.Op == MOp.Jmp && s[^2].Op == MOp.Jcc
                && ((MLabel)s[^2].Operands[0]).Target == next)
            {
                MInstr jcc = s[^2];
                MBlock other = ((MLabel)last.Operands[0]).Target;
                s.RemoveRange(s.Count - 2, 2);
                s.Add(new MInstr(MOp.Jcc, new MLabel(other)) { Cond = jcc.Cond.Negate(), Line = jcc.Line });
            }
        }
    }

    /// <summary>
    /// A 4-BYTE MOVE ONTO ITSELF CLEARS THE UPPER HALF, and that is all it
    /// does: the allocator keeps one where a coalesced copy was written
    /// `mov r32, r32`. Where the register's last write before it in the
    /// block was already a 32-bit result, the hardware zeroed that half then
    /// -- every 32-bit write to a general register does -- and the move
    /// goes: `movzx eax, word [m]` followed by `mov eax, eax` was every char
    /// load. After MOVSX, whose destination is 64 bits, the move is the
    /// zero-extension and stays.
    /// </summary>
    private static void DropZeroExtensions(List<MInstr> s)
    {
        for (int k = s.Count - 1; k >= 1; k--)
        {
            MInstr i = s[k];
            if (i.Op != MOp.Mov || i.Width != 4 || i.Operands.Count != 2
                || i.Operands[0] is not MReg { IsPhys: true, IsFloat: false } r || i.Operands[1] is not MReg c || c.Id != r.Id)
            {
                continue;
            }
            for (int j = k - 1; j >= 0; j--)
            {
                if (!WritesRegister(s[j], r.Id))
                {
                    continue;
                }
                if (ZeroExtended(s[j], r.Id))
                {
                    s.RemoveAt(k);
                }
                break;
            }
        }
    }

    /// <summary>Whether an instruction writes the register, named or not.</summary>
    private static bool WritesRegister(MInstr i, int reg)
    {
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg r && r.Id == reg && (Roles.Of(i, k) & Roles.Role.Def) != 0)
            {
                return true;
            }
        }
        return Roles.ImplicitDefs(i).Contains(reg);
    }

    /// <summary>
    /// Whether an instruction's write of the register is a 32-bit result,
    /// whose upper half the hardware zeroes: a 4-byte move or ALU operation
    /// into it, a shift by a count that is not zero, or MOVZX, whose
    /// destination is 32 bits. A shift by zero, CMOV, SETcc, a 64-bit form
    /// and anything not named here count as not.
    /// </summary>
    private static bool ZeroExtended(MInstr i, int reg)
    {
        if (i.Operands.Count == 0 || i.Operands[0] is not MReg d || d.Id != reg || Roles.ImplicitDefs(i).Contains(reg))
        {
            return false;
        }
        for (int k = 1; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg other && other.Id == reg && (Roles.Of(i, k) & Roles.Role.Def) != 0)
            {
                return false;
            }
        }
        return i.Op switch
        {
            MOp.Movzx => true,
            MOp.Mov or MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Adc or MOp.Sbb
                or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Not => i.Width == 4,
            MOp.Shl or MOp.Shr or MOp.Sar => i.Width == 4 && i.Operands.Count == 2 && i.Operands[1] is MImm { IsPlain: true } count && (count.Value & 31) != 0,
            _ => false,
        };
    }

    /// <summary>Retargets every jump past blocks that only jump on.</summary>
    private static void Thread(MFunction m)
    {
        MBlock Final(MBlock target)
        {
            // Bounded: a cycle of empty jumps (an infinite loop written
            // `while (true) { }`) is left where it is.
            for (int hops = 0; hops < 16; hops++)
            {
                if (target.Instrs.Count != 1 || target.Instrs[0].Op != MOp.Jmp)
                {
                    break;
                }
                MBlock onward = ((MLabel)target.Instrs[0].Operands[0]).Target;
                if (onward == target)
                {
                    break;
                }
                target = onward;
            }
            return target;
        }

        foreach (MBlock b in m.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                MInstr i = b.Instrs[k];
                if (i.Op is MOp.Jmp or MOp.Jcc)
                {
                    MBlock target = ((MLabel)i.Operands[0]).Target;
                    MBlock final = Final(target);
                    if (final != target)
                    {
                        i.Operands[0] = new MLabel(final);
                    }
                }
                else if (i.Op == MOp.JmpTable)
                {
                    for (int t = 0; t < i.Table!.Count; t++)
                    {
                        i.Table[t] = Final(i.Table[t]);
                    }
                }
            }
        }
    }
}
