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
/// first is the one coming in. SSA construction's renaming (Cytron, Ferrante,
/// Rosen, Wegman and Zadeck, 1991), within a block. A float's write that
/// keeps the register's top lanes (MOVSS between registers, CVTSI2SS,
/// CVTSS2SD, SQRTSS: Roles' Def) counts as whole: nothing reads a scalar's
/// top lanes, and a Packed128 value is written only by Select.PackedValues'
/// MOVUPS, XORPS of itself and the packed operations, never a scalar one.
///
/// Not in a block with a jump before its end -- a value read where it goes
/// would be read under its old name. A HANDLER is no such jump: a throw
/// from the middle of a block lands where the registers are the thrower's,
/// so every value a pad reads lives in the frame (LandingPadHomes, before
/// selection): stored after each definition, the store reading the value
/// under the name it has there, and reloaded at the pad's top into the
/// variable's own. A variable live into a pad -- one it or a block after it
/// reads before writing, no reload made it -- keeps its one name, as it did
/// when no function with a handler was split at all.
/// </summary>
internal static class XmmLocalSplit
{
    public static void Run(MFunction m)
    {
        if (m.XmmRegs.Count == 0) return;
        List<int> whole = new();
        HashSet<int> padRead = PadRead(m, whole);
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
            {
                WrittenWhole(m, instrs[k], whole);
                foreach (int v in whole) if (!padRead.Contains(v)) lastWhole[v] = k;
            }
            if (lastWhole.Count == 0) continue;
            for (int k = 0; k < instrs.Count; k++)
            {
                MInstr i = instrs[k];
                WrittenWhole(m, i, whole);
                whole.RemoveAll(padRead.Contains);
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
                    }
                    if (name == v) current.Remove(v); else current[v] = name;
                    for (int o = 0; o < i.Operands.Count; o++)
                        if (i.Operands[o] is MReg { } r && r.Id == v && ((Roles.Of(i.Op, o) & Roles.Role.Def) != 0 || zeroing))
                            i.Operands[o] = MReg.Of(name);
                }
            }
        }
    }

    /// <summary>
    /// The XMM variables live into a landing pad: read, before a whole write,
    /// in the pad or in a block it reaches -- values taken from the registers
    /// of whatever threw, not from a reload. Liveness over the machine blocks
    /// (each block's reads before its whole writes, carried back along its
    /// successors until nothing changes), asked only where there is a pad. A
    /// computed jump goes by no edge: then every variable read anywhere.
    /// </summary>
    private static HashSet<int> PadRead(MFunction m, List<int> whole)
    {
        HashSet<int> read = new();
        if (!m.Blocks.Any(b => b.Source is { IsLandingPad: true })) return read;
        bool anywhere = m.Blocks.Any(b => b.Instrs.Exists(i => i.Op == MOp.JmpInd));
        int count = m.Blocks.Count;
        HashSet<int>[] uses = new HashSet<int>[count], kills = new HashSet<int>[count], liveIn = new HashSet<int>[count];
        for (int b = 0; b < count; b++)
        {
            HashSet<int> use = new(), kill = new();
            foreach (MInstr i in m.Blocks[b].Instrs)
            {
                if (!Zeroing(i))
                    for (int o = 0; o < i.Operands.Count; o++)
                        if (i.Operands[o] is MReg { } r && r.Id >= MReg.FirstVirtual && m.XmmRegs.Contains(r.Id)
                            && (Roles.Of(i.Op, o) & Roles.Role.Use) != 0 && !kill.Contains(r.Id))
                            use.Add(r.Id);
                WrittenWhole(m, i, whole);
                kill.UnionWith(whole);
            }
            (uses[b], kills[b], liveIn[b]) = (use, kill, new HashSet<int>(use));
            if (anywhere) read.UnionWith(use);
        }
        if (anywhere) return read;
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < count; b++) index[m.Blocks[b]] = b;
        List<MBlock> successors = new();
        HashSet<int> liveOut = new();
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int b = count - 1; b >= 0; b--)
            {
                m.SuccessorsInto(b, successors);
                liveOut.Clear();
                foreach (MBlock s in successors) liveOut.UnionWith(liveIn[index[s]]);
                foreach (int v in liveOut)
                    if (!kills[b].Contains(v) && liveIn[b].Add(v)) changed = true;
            }
        }
        for (int b = 0; b < count; b++)
            if (m.Blocks[b].Source is { IsLandingPad: true }) read.UnionWith(liveIn[b]);
        return read;
    }

    /// <summary>XORPS of a register with itself: a write of all of it that reads nothing.</summary>
    private static bool Zeroing(MInstr i)
        => i.Op == MOp.XorPs && i.Operands.Count == 2 && i.Operands[0] is MReg a && i.Operands[1] is MReg b && a.Id == b.Id;

    /// <summary>The XMM-class virtual registers an instruction writes whole: a Def it does not also read.</summary>
    private static void WrittenWhole(MFunction m, MInstr i, List<int> whole)
    {
        whole.Clear();
        if (Zeroing(i))
        {
            int z = ((MReg)i.Operands[0]).Id;
            if (z >= MReg.FirstVirtual && m.XmmRegs.Contains(z)) whole.Add(z);
            return;
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
    }
}
