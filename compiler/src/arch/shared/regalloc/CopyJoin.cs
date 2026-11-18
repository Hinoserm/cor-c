#nullable enable

namespace Corsac.Lang.RegAlloc;

/// <summary>
/// A VARIABLE WRITTEN ON TWO WAYS AND THE VALUE IT WAS COPIED FROM, MADE ONE
/// before allocation, on any machine:
///
///     x = ...                          x = ...
///     r = copy x                       ; (r is x)
///     if (c) { x = add x 17            if (c) { x = add x 17 }
///              r = copy x }            use x
///     use r
///
/// -- `v = a; if (c) v = a + 17;`, a clamp, a default replaced on one path.
/// The IR is not in SSA form, so such a variable is a register with several
/// definitions, every one a copy of the value just computed; and the scan
/// gave it a register of its own for as long as it lived, beside the
/// register of what it was copied from. A long's sum of such a value held
/// four registers for two words, and the sum itself went to the frame: read,
/// added to and written back every lap (allocation/small-object-branch; a
/// 64-bit count kept through the stack in the kernel's network path).
/// CopyFold joins a copy that is its register's one definition, read in its
/// own block; this joins the rest, wherever they are read.
///
/// Two registers may be one where neither is written while the other is
/// live, the copies between them aside (Chaitin's interference, with the
/// copy's exemption): then whenever both are live they hold one value. For
/// `r = copy x`: after every write of r that is no copy of x, x is dead;
/// after every write of x that is no copy of r, r is dead (above, both
/// writes of r are copies of x, and r is dead where x is added to). Liveness is the function's own (what is live into each
/// block, over the values some block reads before it writes them) and, in
/// a block, the instructions after the write up to the first that names
/// the register -- a read: live; a write: dead; the block's end: live out
/// of it or not. Registers joined are a class with one name, their
/// liveness the union of their members' (never less than the truth, which
/// is what a refusal needs: no member is written while another is live, so
/// a way to a read of one passes no write of another),
/// and every instruction is renamed once at the end; a copy of a register
/// onto itself is dropped.
///
/// Only a destination written more than once -- the variable; a register
/// written once is CopyFold's, or a value of its own. Not a register of
/// another class or with another limit (byte-capable), not one whose value
/// is a constant or comes from its home in the frame (an argument's word:
/// both are kept without a register, and a second definition would end
/// that), not two that one instruction both writes, not in a function with
/// a landing pad (entered by no edge: the liveness here does not see what a
/// pad reads), and not past a bound of work: a search that runs long is
/// answered "live".
/// </summary>
public static class CopyJoin
{
    /// <summary>The most instructions looked at after one write for whether a register is live there.</summary>
    private const int ScanMost = 192;
    /// <summary>The most writes of one class of joined registers looked at for a join.</summary>
    private const int WritesMost = 12;
    /// <summary>The most words one of the liveness tables may be (blocks by registers asked of).</summary>
    private const long TableMost = 1L << 20;

    public static int Run(AFunction f)
    {
        int fv = f.Registers.FirstVirtual, n = f.NextVReg, nb = f.Blocks.Count;
        if (nb == 0 || n <= fv) return 0;
        // Each register's count of writes; which are constants' or homes'; the candidates.
        int[] defs = new int[n];
        bool[] kept = new bool[n];
        bool any = false;
        foreach (ABlock b in f.Blocks)
        {
            if (b.LandingPad || b.AddressTaken) return 0;
            foreach (AInstr i in b.Instrs)
            {
                if (i.JumpsAnywhere) return 0;
                foreach (Named r in i.Regs)
                {
                    if (r.Reg < fv || (r.Role & Role.Def) == 0) continue;
                    defs[r.Reg]++;
                    if (i.Constant is not null || i.HomeSlot is not null) kept[r.Reg] = true;
                }
            }
        }
        // The registers a join may be asked of: a variable and what it is copied from.
        bool[] asked = new bool[n];
        foreach (ABlock b in f.Blocks)
            foreach (AInstr i in b.Instrs)
                if (i.WholeCopy && i.CopyDst >= fv && i.CopySrc >= fv && i.CopyDst != i.CopySrc && defs[i.CopyDst] > 1 && !kept[i.CopyDst] && !kept[i.CopySrc])
                {
                    asked[i.CopyDst] = asked[i.CopySrc] = true;
                    any = true;
                }
        if (!any) return 0;

        // WHAT IS LIVE INTO EACH BLOCK, as LoopSplit and the allocator have
        // it, over the registers asked of alone that some block reads before
        // it writes them: liveness is a register's own, and no other is ever
        // in a class. (Every register's, the tables were the allocator's
        // own again -- 180 MB for one function of 14 thousand blocks.) Not
        // past TableMost words of them: such a function is left as it is.
        int[] globalOf = new int[n];
        Array.Fill(globalOf, -1);
        int[] lastDef = new int[n];
        Array.Fill(lastDef, -1);
        int globals = 0;
        for (int b = 0; b < nb; b++)
            foreach (AInstr i in f.Blocks[b].Instrs)
            {
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && asked[r.Reg] && (r.Role & Role.Use) != 0 && lastDef[r.Reg] != b && globalOf[r.Reg] < 0) globalOf[r.Reg] = globals++;
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && (r.Role & Role.Def) != 0) lastDef[r.Reg] = b;
            }
        if ((long)nb * ((globals + 63) / 64) > TableMost) return 0;
        BitRows use = BitRows.Fresh(nb, globals), def = BitRows.Fresh(nb, globals), live = BitRows.Fresh(nb, globals);
        Dictionary<ABlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++) index[f.Blocks[b]] = b;
        int[][] succ = new int[nb][];
        List<int>[] pred = new List<int>[nb];
        for (int b = 0; b < nb; b++) pred[b] = new();
        List<ABlock> successors = new();
        for (int b = 0; b < nb; b++)
        {
            foreach (AInstr i in f.Blocks[b].Instrs)
            {
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && (r.Role & Role.Use) != 0 && globalOf[r.Reg] is >= 0 and var at && !def.Get(b, at)) use.Set(b, at);
                foreach (Named r in i.Regs)
                    if (r.Reg >= fv && (r.Role & Role.Def) != 0 && globalOf[r.Reg] is >= 0 and var at) def.Set(b, at);
            }
            f.SuccessorsInto(b, successors);
            succ[b] = new int[successors.Count];
            for (int k = 0; k < successors.Count; k++)
            {
                succ[b][k] = index[successors[k]];
                pred[succ[b][k]].Add(b);
            }
        }
        BitSet row = new(globals);
        Stack<int> work = new();
        bool[] owed = new bool[nb];
        for (int b = 0; b < nb; b++) { work.Push(b); owed[b] = true; }
        while (work.Count > 0)
        {
            int b = work.Pop();
            owed[b] = false;
            row.Clear();
            foreach (int t in succ[b]) row.Or(live, t);
            row.AndNot(def, b);
            row.Or(use, b);
            if (live.RowEquals(b, row)) continue;
            live.CopyRow(b, row);
            foreach (int p in pred[b]) if (!owed[p]) { owed[p] = true; work.Push(p); }
        }

        // THE CLASSES: each register's name (the source's of the join that
        // made the class, unless the variable alone had a place in the
        // liveness), and where the registers asked of are written.
        int[] name = new int[n];
        for (int v = 0; v < n; v++) name[v] = v;
        int Name(int v)
        {
            while (name[v] != v) { name[v] = name[name[v]]; v = name[v]; }
            return v;
        }
        Dictionary<int, List<(int Block, int At, bool Copy, int From)>> writes = new();
        for (int b = 0; b < nb; b++)
        {
            List<AInstr> ins = f.Blocks[b].Instrs;
            for (int k = 0; k < ins.Count; k++)
            {
                AInstr i = ins[k];
                foreach (Named r in i.Regs)
                {
                    if (r.Reg < fv || (r.Role & Role.Def) == 0 || !asked[r.Reg]) continue;
                    if (!writes.TryGetValue(r.Reg, out var at)) writes[r.Reg] = at = new();
                    bool copy = i.WholeCopy && i.CopyDst == r.Reg && i.CopySrc >= fv;
                    if (at.Count == 0 || at[^1].Block != b || at[^1].At != k) at.Add((b, k, copy, copy ? i.CopySrc : -1));
                }
            }
        }

        // Whether class `c` is live just after instruction `k` of block `b`.
        bool LiveAfter(int b, int k, int c)
        {
            List<AInstr> ins = f.Blocks[b].Instrs;
            int looked = 0;
            for (int j = k + 1; j < ins.Count; j++)
            {
                if (++looked > ScanMost) return true;
                // A way out of the block passed on the way: live where it goes.
                if (j - 1 > k && ins[j - 1].Targets is { } left)
                {
                    int leaving = globalOf[c];
                    if (leaving >= 0)
                        foreach (ABlock t in left) if (live.Get(index[t], leaving)) return true;
                }
                bool written = false;
                foreach (Named r in ins[j].Regs)
                {
                    if (r.Reg < fv || Name(r.Reg) != c) continue;
                    if ((r.Role & Role.Use) != 0) return true;
                    if ((r.Role & Role.Def) != 0) written = true;
                }
                if (written) return false;
            }
            int column = globalOf[c];
            if (column < 0) return false;
            foreach (int t in succ[b]) if (live.Get(t, column)) return true;
            return false;
        }

        int joined = 0;
        // The pairs found apart from being one, not asked again until a class has grown.
        HashSet<(int, int)> refused = new();
        for (int b = 0; b < nb; b++)
        {
            List<AInstr> ins = f.Blocks[b].Instrs;
            for (int k = 0; k < ins.Count; k++)
            {
                AInstr copy = ins[k];
                if (!copy.WholeCopy || copy.CopyDst < fv || copy.CopySrc < fv || defs[copy.CopyDst] <= 1) continue;
                int a = Name(copy.CopyDst), s = Name(copy.CopySrc);
                if (a == s) continue;
                if (kept[a] || kept[s] || f.Target.ClassOf(a) != f.Target.ClassOf(s) || f.Target.ByteOnly(a) != f.Target.ByteOnly(s)) continue;
                if (!writes.TryGetValue(a, out var aWrites) || !writes.TryGetValue(s, out var sWrites)) continue;
                if (aWrites.Count + sWrites.Count > WritesMost) continue;
                if (refused.Contains((a, s))) continue;
                bool apart = true;
                // After every write of the one that is no copy of the other, the other is dead.
                foreach ((int wb, int wk, bool isCopy, int from) in aWrites)
                {
                    if (isCopy && Name(from) == s) continue;
                    if (LiveAfter(wb, wk, s)) { apart = false; break; }
                }
                if (apart)
                    foreach ((int wb, int wk, bool isCopy, int from) in sWrites)
                    {
                        if (isCopy && Name(from) == a) continue;
                        // One instruction writing both writes two values.
                        bool both = false;
                        foreach (Named r in f.Blocks[wb].Instrs[wk].Regs)
                            if (r.Reg >= fv && (r.Role & Role.Def) != 0 && Name(r.Reg) == a) both = true;
                        if (both || LiveAfter(wb, wk, a)) { apart = false; break; }
                    }
                if (!apart)
                {
                    refused.Add((a, s));
                    continue;
                }
                // (What was refused is asked afresh: a class is another now.)
                refused.Clear();
                // Joined under the name that has a place in the liveness (the
                // source's, where both or neither have), which takes the
                // other's: live into a block where either was.
                int keep = globalOf[s] >= 0 || globalOf[a] < 0 ? s : a, gone = keep == s ? a : s;
                if (globalOf[keep] >= 0 && globalOf[gone] >= 0)
                    for (int x = 0; x < nb; x++)
                        if (live.Get(x, globalOf[gone])) live.Set(x, globalOf[keep]);
                name[gone] = keep;
                kept[keep] |= kept[gone];
                List<(int, int, bool, int)> all = writes[keep];
                all.AddRange(writes[gone]);
                writes.Remove(gone);
                joined++;
            }
        }
        if (joined == 0) return 0;

        // Every instruction named once, a copy of a register onto itself gone.
        Dictionary<int, int> renamed = new();
        for (int v = fv; v < n; v++)
            if (Name(v) != v) renamed[v] = Name(v);
        foreach (ABlock b in f.Blocks)
        {
            List<AInstr> ins = b.Instrs;
            int to = 0;
            for (int k = 0; k < ins.Count; k++)
            {
                AInstr i = ins[k];
                LoopSplit.Rename(i, renamed);
                if (i.WholeCopy && i.CopyDst == i.CopySrc && i.CopyDst >= fv) continue;
                ins[to++] = i;
            }
            if (to != ins.Count) ins.RemoveRange(to, ins.Count - to);
        }
        return joined;
    }
}
