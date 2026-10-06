#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;

namespace Corsac.Lang.X86;

/// <summary>
/// Folds address arithmetic into the addressing mode, before allocation.
///
/// The selector names every address as one register plus a displacement,
/// so `a[i]` came out as
///
///     mov t, i ; shl t, 1 ; mov u, a ; add u, t ; mov r, [u+16]
///
/// five instructions and two registers held for a value x86 forms for
/// free: `mov r, [a+i*2+16]`. In a loop those two registers are what
/// pushed the base and the bound out to the frame.
///
/// Two steps, each within one block, on virtual registers only:
///
///   1. `mov t, X ; add t, Y|imm` whose t is read nowhere but as the base
///      of memory operands after it in the block: each becomes
///      [X + Y + disp] (or [X + disp+imm]) and the pair goes.
///   2. `mov y, Z ; shl y, k` (k = 1..3) whose y is read nowhere but as a
///      scale-1 index after it in the block: each becomes [.. + Z*2^k]
///      and the pair goes.
///
///   3. `mov t, [m] ; op d, t` whose t is read nowhere else: `op d, [m]`.
///
/// A fold is refused where X, Y or Z is written again before a use (the
/// IR is not SSA), or where something after the removed arithmetic might
/// read the flags it set.
/// </summary>
internal static class AddressFold
{
    public static void Run(MFunction m)
    {
        int n = m.NextVReg;
        int[] occurrences = new int[n];
        int[] defs = new int[n];
        foreach (MBlock b in m.Blocks)
        {
            foreach (MInstr i in b.Instrs)
            {
                Count(i, occurrences, defs);
            }
        }
        foreach (MBlock b in m.Blocks)
        {
            if (FoldBlock(b, occurrences, defs))
            {
                b.Instrs.RemoveAll(i => i.Op == MOp.Nop && i.Operands.Count == 1 && i.Operands[0] is MImm { Value: Removed });
            }
        }
    }

    /// <summary>The marker a removed instruction carries until the block is swept.</summary>
    private const long Removed = 0x41464C44;

    private static void Count(MInstr i, int[] occurrences, int[] defs)
    {
        bool zeroing = i.Op == MOp.Xor && i.Operands.Count == 2 && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r when !r.IsPhys:
                    occurrences[r.Id]++;
                    if (zeroing || (Roles.Of(i.Op, k) & Role.Def) != 0)
                    {
                        defs[r.Id]++;
                    }
                    break;
                case MMem mem:
                    if (mem.Base is { IsPhys: false } mb) occurrences[mb.Id]++;
                    if (mem.Index is { IsPhys: false } mx) occurrences[mx.Id]++;
                    break;
            }
        }
    }

    private static bool Writes(MInstr i, int v)
    {
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg r && r.Id == v && (Roles.Of(i.Op, k) & Role.Def) != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether removing the flag-setting instruction at `at` is safe: the
    /// first instruction after it that touches the flags writes them
    /// without reading them, or the block ends with nothing reading them.
    /// Anything not known to leave the flags alone counts as a reader.
    /// </summary>
    private static bool FlagsDead(List<MInstr> ins, int at)
    {
        for (int j = at + 1; j < ins.Count; j++)
        {
            switch (ins[j].Op)
            {
                case MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Push or MOp.Pop or MOp.Nop:
                    continue;
                case MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Neg:
                    return true;
                // A shift by a constant from 1 up writes the flags it leaves
                // (a count of 0, which only CL can give, leaves them all);
                // imul writes them too. `xs[i] * 4` shifted the element it
                // had just loaded, and the shift, taken for a reader, kept
                // every int array's element address out of the addressing
                // mode: four instructions and a register for [a+i*4+16].
                case MOp.Shl or MOp.Shr or MOp.Sar when ins[j].Operands.Count == 2 && ins[j].Operands[1] is MImm { Value: >= 1 and <= 31 }:
                    return true;
                case MOp.Imul or MOp.Imul3:
                    return true;
                case MOp.Call or MOp.Jmp or MOp.Ret or MOp.Epilogue:
                    return true;
                default:
                    return false;
            }
        }
        // A block falls through or jumps with its last instruction, which
        // was reached above; flags never flow across a block boundary here.
        return true;
    }

    private static bool Plain(MMem mem) => mem.Symbol is null && mem.Label is null && !mem.IsSpill;

    private static bool FoldBlock(MBlock b, int[] occurrences, int[] defs)
    {
        List<MInstr> ins = b.Instrs;
        bool any = false;

        // ---- 1. base = X + Y | X + imm ----
        for (int q = 1; q < ins.Count; q++)
        {
            MInstr add = ins[q];
            if (add.Op != MOp.Add || add.Lock || add.Width != 4 || add.Operands.Count != 2
                || add.Operands[0] is not MReg { IsPhys: false } t || defs[t.Id] != 2)
            {
                continue;
            }
            MOperand y = add.Operands[1];
            if (y is not (MReg { IsPhys: false } or MImm))
            {
                continue;
            }
            if (y is MReg yr && yr.Id == t.Id)
            {
                continue;
            }
            // The copy that starts t: the nearest instruction before the add that touches t.
            int p = q - 1;
            while (p >= 0 && !Touches(ins[p], t.Id))
            {
                p--;
            }
            if (p < 0)
            {
                continue;
            }
            MInstr copy = ins[p];
            if (copy.Op != MOp.Mov || copy.Width != 4 || copy.Operands.Count != 2
                || copy.Operands[0] is not MReg c0 || c0.Id != t.Id
                || copy.Operands[1] is not MReg { IsPhys: false } x || x.Id == t.Id)
            {
                continue;
            }
            if (!FlagsDead(ins, q))
            {
                continue;
            }
            // Every other occurrence of t: a plain memory base after the add, in this block.
            List<(int J, int K)> uses = new();
            bool ok = true;
            for (int j = q + 1; j < ins.Count && ok; j++)
            {
                MInstr u = ins[j];
                for (int k = 0; k < u.Operands.Count; k++)
                {
                    switch (u.Operands[k])
                    {
                        case MReg r when r.Id == t.Id:
                            ok = false;
                            break;
                        case MMem mem when mem.Index?.Id == t.Id:
                            ok = false;
                            break;
                        case MMem mem when mem.Base?.Id == t.Id:
                            if (!Plain(mem) || mem.Index is not null || (y is MImm && !FitsDisp(mem.Disp, ((MImm)y).Value)))
                            {
                                ok = false;
                            }
                            else
                            {
                                uses.Add((j, k));
                            }
                            break;
                    }
                }
            }
            // copy (1) + add (1) + the memory bases
            if (!ok || uses.Count == 0 || occurrences[t.Id] != 2 + uses.Count)
            {
                continue;
            }
            int last = uses[^1].J;
            if (Written(ins, p + 1, last, x.Id) || (y is MReg yv && Written(ins, q + 1, last, yv.Id)))
            {
                continue;
            }
            foreach ((int j, int k) in uses)
            {
                MMem mem = (MMem)ins[j].Operands[k];
                ins[j].Operands[k] = y is MImm imm
                    ? new MMem(x, checked(mem.Disp + (int)imm.Value))
                    : new MMem(x, mem.Disp) { Index = (MReg)y, Scale = 1 };
            }
            occurrences[x.Id] += uses.Count - 1;
            if (y is MReg ym)
            {
                occurrences[ym.Id] += uses.Count - 1;
            }
            occurrences[t.Id] = 0;
            ins[p] = Gone();
            ins[q] = Gone();
            any = true;
        }

        // ---- 1b. base = (anything) +/- imm, the add alone folded ----
        //
        // `mov t, [m] ; add t, 4 ; xchg [t], r`: t loaded from a static, a
        // field or a register the copy step cannot take, then offset by a
        // constant and used only as an address. The load stays, and the
        // constant goes into each address: `xchg [t+4], r`. A sub of a
        // constant is an add of its negation: the allocator's `sub edx, 4 ;
        // mov [edx], eax` is `mov [edx-4], eax`. The runtime's word-sized
        // addresses are all made so.
        for (int q = 1; q < ins.Count; q++)
        {
            MInstr add = ins[q];
            if (add.Op is not (MOp.Add or MOp.Sub) || add.Lock || add.Width != 4 || add.Operands.Count != 2
                || add.Operands[0] is not MReg { IsPhys: false } t || defs[t.Id] != 2 || add.Operands[1] is not MImm { IsPlain: true } amount)
            {
                continue;
            }
            long delta = add.Op == MOp.Add ? amount.Value : -amount.Value;
            int p = q - 1;
            while (p >= 0 && !Touches(ins[p], t.Id)) p--;
            if (p < 0) continue;
            MInstr start = ins[p];
            if (start.Op != MOp.Mov || start.Width != 4 || start.Operands.Count != 2
                || start.Operands[0] is not MReg s0 || s0.Id != t.Id || Touches(new MInstr(MOp.Nop, start.Operands[1]), t.Id))
            {
                continue;
            }
            if (!FlagsDead(ins, q)) continue;
            List<(int J, int K)> uses = new();
            bool ok = true;
            for (int j = q + 1; j < ins.Count && ok; j++)
            {
                MInstr u = ins[j];
                for (int k = 0; k < u.Operands.Count && ok; k++)
                {
                    switch (u.Operands[k])
                    {
                        case MReg r when r.Id == t.Id:
                            ok = false;
                            break;
                        case MMem mem when mem.Index?.Id == t.Id:
                            ok = false;
                            break;
                        case MMem mem when mem.Base?.Id == t.Id:
                            if (!Plain(mem) || !FitsDisp(mem.Disp, delta)) ok = false;
                            else uses.Add((j, k));
                            break;
                    }
                }
            }
            if (!ok || uses.Count == 0 || occurrences[t.Id] != 2 + uses.Count) continue;
            foreach ((int j, int k) in uses)
            {
                MMem mem = (MMem)ins[j].Operands[k];
                ins[j].Operands[k] = new MMem(mem.Base, checked(mem.Disp + (int)delta)) { Index = mem.Index, Scale = mem.Scale };
            }
            occurrences[t.Id]--;
            defs[t.Id]--;
            ins[q] = Gone();
            any = true;
        }

        // ---- 2. index = Z << k ----
        for (int q = 1; q < ins.Count; q++)
        {
            MInstr shl = ins[q];
            if (shl.Op != MOp.Shl || shl.Width != 4 || shl.Operands.Count != 2
                || shl.Operands[0] is not MReg { IsPhys: false } yv || defs[yv.Id] != 2
                || shl.Operands[1] is not MImm { Value: >= 1 and <= 3 } amount)
            {
                continue;
            }
            int p = q - 1;
            while (p >= 0 && !Touches(ins[p], yv.Id))
            {
                p--;
            }
            if (p < 0)
            {
                continue;
            }
            MInstr copy = ins[p];
            if (copy.Op != MOp.Mov || copy.Width != 4 || copy.Operands.Count != 2
                || copy.Operands[0] is not MReg c0 || c0.Id != yv.Id
                || copy.Operands[1] is not MReg { IsPhys: false } z || z.Id == yv.Id)
            {
                continue;
            }
            if (!FlagsDead(ins, q))
            {
                continue;
            }
            List<(int J, int K)> uses = new();
            bool ok = true;
            for (int j = q + 1; j < ins.Count && ok; j++)
            {
                MInstr u = ins[j];
                for (int k = 0; k < u.Operands.Count; k++)
                {
                    switch (u.Operands[k])
                    {
                        case MReg r when r.Id == yv.Id:
                            ok = false;
                            break;
                        case MMem mem when mem.Base?.Id == yv.Id:
                            ok = false;
                            break;
                        case MMem mem when mem.Index?.Id == yv.Id:
                            if (!Plain(mem) || mem.Scale != 1)
                            {
                                ok = false;
                            }
                            else
                            {
                                uses.Add((j, k));
                            }
                            break;
                    }
                }
            }
            if (!ok || uses.Count == 0 || occurrences[yv.Id] != 2 + uses.Count)
            {
                continue;
            }
            if (Written(ins, p + 1, uses[^1].J, z.Id))
            {
                continue;
            }
            foreach ((int j, int k) in uses)
            {
                MMem mem = (MMem)ins[j].Operands[k];
                ins[j].Operands[k] = new MMem(mem.Base, mem.Disp) { Index = z, Scale = 1 << (int)amount.Value };
            }
            occurrences[z.Id] += uses.Count - 1;
            occurrences[yv.Id] = 0;
            ins[p] = Gone();
            ins[q] = Gone();
            any = true;
        }
        // ---- 3. a load read once, as an ALU instruction's source ----
        //
        // `mov t, [m] ; and d, t` whose t is read nowhere else: `and d, [m]`,
        // one instruction and no register. A global's mask anded into an
        // allocation's address, a field added to a sum: the selector loads
        // every operand into a register first. Only where nothing between
        // the two can write memory or the address's registers.
        for (int q = 1; q < ins.Count; q++)
        {
            MInstr alu = ins[q];
            if (alu.Op is not (MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Imul)
                || alu.Width != 4 || alu.Lock || alu.Operands.Count != 2
                || alu.Operands[1] is not MReg { IsPhys: false } t || alu.Operands[0] is not MReg dst || dst.Id == t.Id
                || defs[t.Id] != 1 || occurrences[t.Id] != 2)
            {
                continue;
            }
            int p = q - 1;
            while (p >= 0 && !Touches(ins[p], t.Id)) p--;
            if (p < 0) continue;
            MInstr load = ins[p];
            if (load.Op != MOp.Mov || load.Width != 4 || load.Operands.Count != 2
                || load.Operands[0] is not MReg l0 || l0.Id != t.Id || load.Operands[1] is not MMem mem)
            {
                continue;
            }
            bool quiet = true;
            for (int j = p + 1; j < q && quiet; j++)
            {
                MInstr between = ins[j];
                quiet = between.Op is MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Add or MOp.Sub or MOp.And or MOp.Or
                        or MOp.Xor or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Cmp or MOp.Test or MOp.Setcc
                    && !between.Lock && !between.Operands.Any(o => o is MMem)
                    && (mem.Base is null || !Writes(between, mem.Base.Id))
                    && (mem.Index is null || !Writes(between, mem.Index.Id));
            }
            if (!quiet) continue;
            alu.Operands[1] = mem;
            occurrences[t.Id] = 0;
            ins[p] = Gone();
            any = true;
        }
        return any;
    }

    private static MInstr Gone() => new(MOp.Nop, new MImm(Removed));

    private static bool FitsDisp(int disp, long add) => disp + add is >= int.MinValue and <= int.MaxValue;

    private static bool Touches(MInstr i, int v)
    {
        foreach (MOperand o in i.Operands)
        {
            if (o is MReg r && r.Id == v) return true;
            if (o is MMem mem && (mem.Base?.Id == v || mem.Index?.Id == v)) return true;
        }
        return false;
    }

    /// <summary>Whether any instruction in [from, to) writes v.</summary>
    private static bool Written(List<MInstr> ins, int from, int to, int v)
    {
        for (int j = from; j < to; j++)
        {
            if (Writes(ins[j], v))
            {
                return true;
            }
        }
        return false;
    }
}
