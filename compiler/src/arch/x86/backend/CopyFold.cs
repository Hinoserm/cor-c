#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;

namespace Corsac.Lang.X86;

/// <summary>
/// A COPY THAT ONLY NAMES A VALUE AGAIN, folded before allocation: `mov a, b`
/// where the move is a's one definition, every read of a follows it in the
/// same block, and nothing writes b before the last of them. Then a holds
/// b's value wherever it is live, and two registers holding one value do not
/// interfere (Chaitin's "ultimate notion of interference", made exact by
/// Boissinot et al., CGO 2009: live ranges that intersect AND carry
/// different values; LLVM's coalescer joins such copies by value number).
/// The scan saw two live values where there was one: a long's low half
/// copied for its sign took a sixth register in `loops`' lap. a's reads
/// read b, and the move goes.
///
/// Not a byte register's copy of a value that may be in any register (the
/// byte constraint is a's), nor a copy of anything but a whole word.
/// </summary>
internal static class CopyFold
{
    public static int Run(MFunction m)
    {
        List<(MReg Reg, Role Role, int Operand, bool InMem)> regs = new();
        int n = m.NextVReg;
        int[] defs = new int[n];
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach ((MReg r, Role role, _, _) in Allocator.RegsOf(i, regs))
                    if (!r.IsPhys && (role & Role.Def) != 0) defs[r.Id]++;
        // Where each value is read, by block: a value read in two blocks is not folded.
        MBlock?[] readIn = new MBlock?[n];
        bool[] readElsewhere = new bool[n];
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach ((MReg r, Role role, _, _) in Allocator.RegsOf(i, regs))
                {
                    if (r.IsPhys || (role & Role.Use) == 0) continue;
                    if (readIn[r.Id] is null) readIn[r.Id] = b;
                    else if (!ReferenceEquals(readIn[r.Id], b)) readElsewhere[r.Id] = true;
                }

        int folded = 0;
        foreach (MBlock b in m.Blocks)
        {
            List<MInstr> ins = b.Instrs;
            for (int c = 0; c < ins.Count; c++)
            {
                MInstr copy = ins[c];
                if (copy.Op != MOp.Mov || copy.Width != 4 || copy.Lock || copy.Operands.Count != 2
                    || copy.Operands[0] is not MReg { IsPhys: false } a || copy.Operands[1] is not MReg { IsPhys: false } src
                    || a.Id == src.Id || defs[a.Id] != 1 || readElsewhere[a.Id] || readIn[a.Id] is not null && !ReferenceEquals(readIn[a.Id], b)
                    || m.ByteRegs.Contains(a.Id) && !m.ByteRegs.Contains(src.Id))
                {
                    continue;
                }
                // The last read of a in the block, and no write of b before it.
                int last = -1;
                bool clobbered = false, ok = true;
                for (int k = c + 1; k < ins.Count && ok; k++)
                {
                    bool readsA = false, writesB = false;
                    foreach ((MReg r, Role role, _, _) in Allocator.RegsOf(ins[k], regs))
                    {
                        if (r.IsPhys) continue;
                        if (r.Id == a.Id && (role & Role.Use) != 0) readsA = true;
                        if (r.Id == src.Id && (role & Role.Def) != 0) writesB = true;
                    }
                    // A read of a at or after a write of b: b no longer holds a's value
                    // (a write of b in the same instruction as a read comes after it).
                    if (readsA && clobbered) ok = false;
                    if (readsA) last = k;
                    if (writesB) clobbered = true;
                }
                if (!ok) continue;
                for (int k = c + 1; k <= last; k++) Rename(ins[k], a.Id, src);
                ins.RemoveAt(c);
                c--;
                folded++;
            }
        }
        return folded;
    }

    /// <summary>Every read of `from` in an instruction made a read of `to`, memory operands' registers too.</summary>
    private static void Rename(MInstr i, int from, MReg to)
    {
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg r && r.Id == from) i.Operands[k] = to;
            else if (i.Operands[k] is MMem mem && (mem.Base?.Id == from || mem.Index?.Id == from))
                i.Operands[k] = new MMem(mem.Base?.Id == from ? to : mem.Base, mem.Disp)
                {
                    Index = mem.Index?.Id == from ? to : mem.Index,
                    Scale = mem.Scale, Symbol = mem.Symbol, Reloc = mem.Reloc, Label = mem.Label, IsSpill = mem.IsSpill, Device = mem.Device,
                };
        }
    }
}
