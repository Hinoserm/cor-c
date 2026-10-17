#nullable enable
using Role = Corsac.Lang.X64.Roles.Role;

namespace Corsac.Lang.X64;

/// <summary>
/// Folds address arithmetic into the addressing mode, before allocation:
/// the i386 backend's pass (X86/AddressFold.cs) for long mode.
///
/// The selector names every address as one register plus a displacement,
/// so a string's `s[i]` came out as
///
///     movsxd w, i ; mov y, w ; shl y, 1 ; lea t, [s+y] ; movzx r, [t+24]
///
/// five instructions and two registers held for an address x86-64 forms
/// for free: `movsxd w, i ; movzx r, [s+w*2+24]`.
///
/// Two steps, each within one block, on virtual registers only -- with the
/// frame pointer, which nothing in a body writes, as a lea's base too:
///
///   1. `lea t, [X+Y*s+d]`, or `mov t, X ; add t, Y|imm` (or a sub of a
///      constant), whose t is read nowhere but as the base of memory
///      operands after it in the block: each becomes [X+Y*s+d+disp] and
///      the arithmetic goes.
///   2. `mov y, Z ; shl y, k` (64-bit, k = 1..3) whose y is read nowhere but
///      as a scale-1 index after it in the block: each becomes [.. + Z*2^k]
///      and the pair goes.
///
/// ONLY THE 64-BIT SHIFT IS AN INDEX. `shl r32` drops the bits a carry
/// would put in the upper half, and the scaled index in an address keeps
/// them. Lowering widens an index before it scales it (ElementOffset), so
/// an element's shift is of the word, and the widening before it stays.
///
/// A fold is refused where X, Y or Z is written again before a use (the
/// IR is not SSA), or where something after a removed add or shift might
/// read the flags it set.
/// </summary>
internal sealed class AddressFold
{
    private readonly int[] _occurrences;
    private readonly int[] _defs;

    /// <summary>What a removed instruction is replaced by until its block is swept.</summary>
    private readonly MInstr _gone = new(MOp.Nop);

    private AddressFold(MFunction m)
    {
        _occurrences = new int[m.NextVReg];
        _defs = new int[m.NextVReg];
        foreach (MBlock b in m.Blocks)
        {
            foreach (MInstr i in b.Instrs)
            {
                Count(i);
            }
        }
    }

    public static void Run(MFunction m)
    {
        AddressFold fold = new(m);
        foreach (MBlock b in m.Blocks)
        {
            bool bases = fold.FoldBases(b.Instrs);
            bool indexes = fold.FoldIndexes(b.Instrs);
            if (bases || indexes)
            {
                b.Instrs.RemoveAll(i => ReferenceEquals(i, fold._gone));
            }
        }
    }

    private void Count(MInstr i)
    {
        // `xor v, v` makes zero whatever v held: a definition only.
        bool zeroing = i.Op is MOp.Xor or MOp.XorF && i.Operands.Count == 2
            && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r when !r.IsPhys:
                    _occurrences[r.Id]++;
                    if (zeroing || (Roles.Of(i, k) & Role.Def) != 0)
                    {
                        _defs[r.Id]++;
                    }
                    break;
                case MMem mem:
                    if (mem.Base is { IsPhys: false } mb) _occurrences[mb.Id]++;
                    if (mem.Index is { IsPhys: false } mx) _occurrences[mx.Id]++;
                    break;
            }
        }
    }

    // ---- 1. base = X + Y*s + d ----------------------------------------------------

    private bool FoldBases(List<MInstr> ins)
    {
        // Last first: `lea t, [a+b] ; mov u, t ; sub u, 8 ; mov [u], r` folds
        // u into [t-8] before t is asked about, and then t into [a+b-8].
        bool any = false;
        for (int q = ins.Count - 1; q >= 0; q--)
        {
            MInstr def = ins[q];
            if (def.Operands.Count != 2 || def.Operands[0] is not MReg { IsPhys: false } t)
            {
                continue;
            }

            // What t is: X + Y*scale + disp, made from `start` to q.
            MReg x;
            MReg? y = null;
            int scale = 1;
            long disp = 0;
            int start = q;
            if (def.Op == MOp.Lea)
            {
                // The frame pointer is a base like any: a slot's address taken
                // into a register and read through it is the slot itself.
                if (_defs[t.Id] != 1 || def.Operands[1] is not MMem { Base: { } lb } lm || !Plain(lm) || lm.Segment != 0
                    || lb.IsPhys && lb.Id != (int)Gpr.Rbp || lb.Id == t.Id || lm.Index is { } li && (li.IsPhys || li.Id == t.Id))
                {
                    continue;
                }
                x = lb;
                y = lm.Index;
                scale = lm.Scale;
                disp = lm.Disp;
            }
            else if (def.Op is MOp.Add or MOp.Sub && def.Width == 8 && !def.Lock && _defs[t.Id] == 2
                     && (def.Op == MOp.Add && def.Operands[1] is MReg { IsPhys: false } || def.Operands[1] is MImm { IsPlain: true, FitsInt32: true }))
            {
                if (def.Operands[1] is MReg same && same.Id == t.Id)
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
                if (copy.Op != MOp.Mov || copy.Width != 8 || copy.Operands.Count != 2
                    || copy.Operands[0] is not MReg c0 || c0.Id != t.Id
                    || copy.Operands[1] is not MReg { IsPhys: false } cx || cx.Id == t.Id
                    || !FlagsDead(ins, q))
                {
                    continue;
                }
                x = cx;
                if (def.Operands[1] is MReg addend)
                {
                    y = addend;
                }
                else
                {
                    // A sub of a constant is an add of its negation.
                    long amount = ((MImm)def.Operands[1]).Value;
                    disp = def.Op == MOp.Add ? amount : -amount;
                }
                start = p;
            }
            else
            {
                continue;
            }

            // Every other occurrence of t: a plain memory base after it, in this block.
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
                            if (!Plain(mem) || (y is not null && mem.Index is not null) || !FitsDisp(mem.Disp, disp))
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
            int made = start == q ? 1 : 2;
            if (!ok || uses.Count == 0 || _occurrences[t.Id] != made + uses.Count)
            {
                continue;
            }
            int last = uses[^1].J;
            if (Written(ins, start + 1, last, x.Id) || (y is not null && Written(ins, q + 1, last, y.Id)))
            {
                continue;
            }
            foreach ((int j, int k) in uses)
            {
                MMem mem = (MMem)ins[j].Operands[k];
                ins[j].Operands[k] = new MMem(x, checked((int)(mem.Disp + disp)))
                {
                    Index = y ?? mem.Index,
                    Scale = y is not null ? scale : mem.Scale,
                    Segment = mem.Segment,
                };
            }
            _occurrences[x.Id] += uses.Count - 1;
            if (y is not null)
            {
                _occurrences[y.Id] += uses.Count - 1;
            }
            _occurrences[t.Id] = 0;
            _defs[t.Id] = 0;
            ins[q] = _gone;
            ins[start] = _gone;
            any = true;
        }
        return any;
    }

    // ---- 2. index = Z << k ------------------------------------------------------------

    private bool FoldIndexes(List<MInstr> ins)
    {
        bool any = false;
        for (int q = 1; q < ins.Count; q++)
        {
            MInstr shl = ins[q];
            if (shl.Op != MOp.Shl || shl.Width != 8 || shl.Operands.Count != 2
                || shl.Operands[0] is not MReg { IsPhys: false } yv || _defs[yv.Id] != 2
                || shl.Operands[1] is not MImm { IsPlain: true, Value: >= 1 and <= 3 } amount)
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
            if (copy.Op != MOp.Mov || copy.Width != 8 || copy.Operands.Count != 2
                || copy.Operands[0] is not MReg c0 || c0.Id != yv.Id
                || copy.Operands[1] is not MReg { IsPhys: false } z || z.Id == yv.Id
                || !FlagsDead(ins, q))
            {
                continue;
            }
            List<(int J, int K)> uses = new();
            bool ok = true;
            for (int j = q + 1; j < ins.Count && ok; j++)
            {
                MInstr u = ins[j];
                for (int k = 0; k < u.Operands.Count && ok; k++)
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
            if (!ok || uses.Count == 0 || _occurrences[yv.Id] != 2 + uses.Count)
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
                ins[j].Operands[k] = new MMem(mem.Base, mem.Disp) { Index = z, Scale = 1 << (int)amount.Value, Segment = mem.Segment };
            }
            _occurrences[z.Id] += uses.Count - 1;
            _occurrences[yv.Id] = 0;
            _defs[yv.Id] = 0;
            ins[p] = _gone;
            ins[q] = _gone;
            any = true;
        }
        return any;
    }

    // ---- what the steps ask --------------------------------------------------------------

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
            MInstr i = ins[j];
            switch (i.Op)
            {
                case MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Push or MOp.Pop or MOp.Nop
                    or MOp.Not or MOp.Bswap or MOp.MovF or MOp.MovGx:
                    continue;
                case MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Neg
                    or MOp.Imul or MOp.Imul3 or MOp.UcomiF:
                    return true;
                // A shift by a count the width does not mask to zero writes
                // the flags; one by zero leaves them all.
                case MOp.Shl or MOp.Shr or MOp.Sar
                    when i.Operands.Count == 2 && i.Operands[1] is MImm { IsPlain: true } count && (count.Value & (i.Width == 8 ? 63 : 31)) != 0:
                    return true;
                // A call leaves the flags as its callee did, and a jump or a
                // return takes nothing of them across: flags never flow
                // from one block to the next here.
                case MOp.Call or MOp.CallInd or MOp.Jmp or MOp.Ret or MOp.Epilogue:
                    return true;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>An address of registers and a displacement: no symbol, no block, no spill slot.</summary>
    private static bool Plain(MMem mem) => mem.Symbol is null && mem.Label is null && !mem.IsSpill;

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

    private static bool Writes(MInstr i, int v)
    {
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg r && r.Id == v && (Roles.Of(i, k) & Role.Def) != 0)
            {
                return true;
            }
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
