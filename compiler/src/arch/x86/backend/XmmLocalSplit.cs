#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// AN XMM VARIABLE'S VALUES INSIDE A BLOCK, EACH ITS OWN REGISTER but the
/// last. A variable is one register wherever it is live, so where a value
/// of it is made in two-address form -- `b = t1 | t2` selected as `movaps
/// b, t1 ; por b, t2` -- the move stays unless the allocator happened to
/// give b the register t1 dies in: ChaCha20's quarter round, b's twelfth
/// rotate, paid a MOVAPS a time it lost. Every value a block writes whole
/// (an operand's Def, not a read-and-write, or XORPS of itself) that the
/// block writes whole again before it ends is read only inside the block,
/// up to that write: it becomes a register of its own there, which the
/// allocator's hint can then put in t1's. The block's last value keeps the
/// variable's register, for what is read after; what is read before the
/// first is the one coming in. The renaming of a web split at its kills,
/// as SSA construction does it, within a block (Briggs, Cooper, Harvey and
/// Simpson's live-range splitting at definitions).
///
/// Not in a block with a jump before its end -- a value read where it goes
/// would be read under its old name -- nor in a function with a handler,
/// which may read a variable from the middle of a block.
/// </summary>
internal static class XmmLocalSplit
{
    public static int Run(MFunction m)
    {
        if (m.XmmRegs.Count == 0 || m.Blocks.Any(b => b.Source is { IsLandingPad: true })) return 0;
        int made = 0;
        Dictionary<int, int> lastWhole = new(), current = new();
        foreach (MBlock b in m.Blocks)
        {
            List<MInstr> instrs = b.Instrs;
            bool jumps = false;
            for (int k = 0; k + 1 < instrs.Count; k++)
                if (instrs[k].Op is MOp.Jcc or MOp.Jmp or MOp.JmpTable or MOp.JmpInd) { jumps = true; break; }
            if (jumps) continue;
            lastWhole.Clear();
            current.Clear();
            for (int k = 0; k < instrs.Count; k++)
                foreach (int v in WrittenWhole(m, instrs[k])) lastWhole[v] = k;
            for (int k = 0; k < instrs.Count; k++)
            {
                MInstr i = instrs[k];
                List<int> whole = WrittenWhole(m, i);
                bool zeroing = Zeroing(i);
                // Reads first, under the name the value read has now.
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    if (i.Operands[o] is not MReg { } r || !current.TryGetValue(r.Id, out int now)) continue;
                    bool read = (Roles.Of(i.Op, o) & Roles.Role.Use) != 0 && !zeroing;
                    if (read || !whole.Contains(r.Id)) i.Operands[o] = MReg.Of(now);
                }
                // Then each whole write: a new name unless it is the block's last.
                foreach (int v in whole)
                {
                    int name = v;
                    if (lastWhole[v] != k)
                    {
                        name = m.NewReg().Id;
                        m.XmmRegs.Add(name);
                        made++;
                    }
                    if (name == v) current.Remove(v); else current[v] = name;
                    for (int o = 0; o < i.Operands.Count; o++)
                        if (i.Operands[o] is MReg { } r && r.Id == v && ((Roles.Of(i.Op, o) & Roles.Role.Def) != 0 || zeroing))
                            i.Operands[o] = MReg.Of(name);
                }
            }
        }
        return made;
    }

    /// <summary>XORPS of a register with itself: a write of all of it that reads nothing.</summary>
    private static bool Zeroing(MInstr i)
        => i.Op == MOp.XorPs && i.Operands.Count == 2 && i.Operands[0] is MReg a && i.Operands[1] is MReg b && a.Id == b.Id;

    /// <summary>The XMM-class virtual registers an instruction writes whole: a Def it does not also read.</summary>
    private static List<int> WrittenWhole(MFunction m, MInstr i)
    {
        List<int> whole = new();
        if (Zeroing(i))
        {
            int z = ((MReg)i.Operands[0]).Id;
            if (z >= MReg.FirstVirtual && m.XmmRegs.Contains(z)) whole.Add(z);
            return whole;
        }
        for (int o = 0; o < i.Operands.Count; o++)
        {
            if (i.Operands[o] is not MReg { } r || r.Id < MReg.FirstVirtual || !m.XmmRegs.Contains(r.Id)) continue;
            Roles.Role role = Roles.Of(i.Op, o);
            if (role != Roles.Role.Def) continue;
            // Not where another operand of it reads the same register as well.
            bool readToo = false;
            for (int p = 0; p < i.Operands.Count; p++)
                if (p != o && i.Operands[p] is MReg { } q && q.Id == r.Id && (Roles.Of(i.Op, p) & Roles.Role.Use) != 0) readToo = true;
            if (!readToo && !whole.Contains(r.Id)) whole.Add(r.Id);
        }
        return whole;
    }
}
