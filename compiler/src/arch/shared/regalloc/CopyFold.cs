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
/// not (the limit is a's), nor where a is read in the block before its copy
/// -- carried round a loop, it is some other lap's value there.
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
        Dictionary<int, int> renamed = new();
        foreach (ABlock b in f.Blocks)
        {
            List<AInstr> ins = b.Instrs;
            for (int c = 0; c < ins.Count; c++)
            {
                AInstr copy = ins[c];
                int a = copy.CopyDst, src = copy.CopySrc;
                if (!copy.WholeCopy || a < fv || src < fv || a == src || defs[a] != 1 || readElsewhere[a]
                    || readIn[a] is not null && !ReferenceEquals(readIn[a], b)
                    || f.Target.ByteOnly(a) && !f.Target.ByteOnly(src))
                {
                    continue;
                }
                bool ok = true;
                for (int k = 0; k < c && ok; k++)
                    foreach (Named r in ins[k].Regs)
                        if (r.Reg == a) ok = false;
                // The last read of a in the block, and no write of b before it.
                int last = -1;
                bool clobbered = false;
                for (int k = c + 1; k < ins.Count && ok; k++)
                {
                    bool readsA = false, writesB = false;
                    foreach (Named r in ins[k].Regs)
                    {
                        if (r.Reg == a && (r.Role & Role.Use) != 0) readsA = true;
                        if (r.Reg == src && (r.Role & Role.Def) != 0) writesB = true;
                    }
                    // A read of a at or after a write of b: b no longer holds a's value
                    // (a write of b in the same instruction as a read comes after it).
                    if (readsA && clobbered) ok = false;
                    if (readsA) last = k;
                    if (writesB) clobbered = true;
                }
                if (!ok) continue;
                renamed.Clear();
                renamed[a] = src;
                for (int k = c + 1; k <= last; k++) LoopSplit.Rename(ins[k], renamed);
                ins.RemoveAt(c);
                c--;
                folded++;
            }
        }
        return folded;
    }
}
