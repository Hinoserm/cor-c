#nullable enable

namespace Corsac.Lang.RegAlloc;

/// <summary>
/// A COPY THAT ONLY NAMES A VALUE AGAIN, folded before allocation, on any
/// machine: a whole-register `a = b` where the copy is a's one definition,
/// every read of a follows it in the same block, and nothing writes b before
/// the last of them. Then a holds b's value wherever it is live, and two
/// registers holding one value do not interfere (Chaitin's "ultimate notion
/// of interference", made exact by Boissinot et al., CGO 2009: live ranges
/// that intersect AND carry different values; LLVM's coalescer joins such
/// copies by value number). The scan saw two live values where there was
/// one: a long's low half copied for its sign took a sixth register in
/// `loops`' lap. a's reads read b, and the copy goes.
///
/// Not a value limited to byte-capable registers copied from one that is
/// not (the limit is a's), nor where a is named in the block before its
/// copy -- carried round a loop, it is some other lap's value there.
/// Each block indexed once (where each value is first named, last read,
/// written), each copy then decided by a search, not a walk of the block.
/// </summary>
public static class CopyFold
{
    public static int Run(AFunction f)
    {
        int fv = f.Registers.FirstVirtual, n = f.NextVReg;
        int[] defs = new int[n];
        ABlock?[] readIn = new ABlock?[n];
        bool[] readElsewhere = new bool[n];
        foreach (ABlock b in f.Blocks)
            foreach (AInstr i in b.Instrs)
                foreach (Named r in i.Regs)
                {
                    if (r.Reg < fv) continue;
                    if ((r.Role & Role.Def) != 0) defs[r.Reg]++;
                    if ((r.Role & Role.Use) == 0) continue;
                    if (readIn[r.Reg] is null) readIn[r.Reg] = b;
                    else if (!ReferenceEquals(readIn[r.Reg], b)) readElsewhere[r.Reg] = true;
                }

        int folded = 0;
        Dictionary<int, int> first = new(), lastRead = new();
        Dictionary<int, List<int>> writes = new();
        Dictionary<int, int> renamed = new();
        List<AInstr> keep = new();
        foreach (ABlock b in f.Blocks)
        {
            List<AInstr> ins = b.Instrs;
            bool any = false;
            foreach (AInstr i in ins) if (i.WholeCopy) { any = true; break; }
            if (!any) continue;
            first.Clear(); lastRead.Clear(); writes.Clear();
            for (int k = 0; k < ins.Count; k++)
                foreach (Named r in ins[k].Regs)
                {
                    if (r.Reg < fv) continue;
                    first.TryAdd(r.Reg, k);
                    if ((r.Role & Role.Use) != 0) lastRead[r.Reg] = k;
                    if ((r.Role & Role.Def) != 0)
                    {
                        if (!writes.TryGetValue(r.Reg, out List<int>? at)) writes[r.Reg] = at = new();
                        if (at.Count == 0 || at[^1] != k) at.Add(k);
                    }
                }
            keep.Clear();
            for (int c = 0; c < ins.Count; c++)
            {
                AInstr copy = ins[c];
                int a = copy.CopyDst, src = copy.CopySrc;
                bool fold = copy.WholeCopy && a >= fv && src >= fv && a != src && defs[a] == 1 && !readElsewhere[a]
                    && (readIn[a] is null || ReferenceEquals(readIn[a], b))
                    && !(f.Target.ByteOnly(a) && !f.Target.ByteOnly(src))
                    && first[a] == c;
                int last = fold && lastRead.TryGetValue(a, out int l) ? l : -1;
                // No write of b after the copy and before a's last read (a write
                // in the instruction that last reads a comes after the read).
                if (fold && last > c && writes.TryGetValue(src, out List<int>? srcWrites))
                {
                    int lo = 0, hi = srcWrites.Count;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        if (srcWrites[mid] <= c) lo = mid + 1; else hi = mid;
                    }
                    if (lo < srcWrites.Count && srcWrites[lo] < last) fold = false;
                }
                if (!fold)
                {
                    keep.Add(copy);
                    continue;
                }
                renamed.Clear();
                renamed[a] = src;
                for (int k = c + 1; k <= last; k++) LoopSplit.Rename(ins[k], renamed);
                folded++;
            }
            if (keep.Count != ins.Count)
            {
                ins.Clear();
                ins.AddRange(keep);
            }
        }
        return folded;
    }
}
