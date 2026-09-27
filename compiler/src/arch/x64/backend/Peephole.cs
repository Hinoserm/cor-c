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
/// </summary>
internal static class Peephole
{
    public static void Run(MFunction m)
    {
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
