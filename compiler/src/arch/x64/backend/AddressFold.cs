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
///
/// AND A BASE READ IN OTHER BLOCKS TOO: t made in one block and read as a
/// base in blocks it dominates, of an X (and Y) written in one block only,
/// before t in it or in a block that dominates t's, or the frame pointer --
/// so X holds at every use what it held where t was made. The inlined allocator's fast path
/// made `lea t, [thread+128]`, stored through it, branched, and stored
/// through it again on the other side: a register held across the branch
/// for an address the thread's own register forms.
/// </summary>
internal sealed class AddressFold
{
    private readonly MFunction _m;
    private readonly int[] _occurrences;
    private readonly int[] _defs;
    // Where each register is written last, by block and instruction, and
    // the one block it is written in, -1 where it is written in more.
    private readonly (int Block, int Instr)[] _defAt;
    private readonly int[] _defBlock;
    private int[]? _idom;
    // Where each register occurs, by block, instruction and operand, made
    // when Beyond first asks: an occurrence a fold adds is noted, and one it
    // rewrites away or removes is passed over where read -- so a base read
    // in other blocks is found among its own occurrences, not by walking the
    // whole function for every candidate: a function of ten thousand blocks
    // spent half an hour here.
    private List<(int Block, MInstr I, int K)>[]? _sites;
    private readonly HashSet<MInstr> _removed = new(ReferenceEqualityComparer.Instance);

    /// <summary>What a removed instruction is replaced by until its block is swept.</summary>
    private readonly MInstr _gone = new(MOp.Nop);

    private AddressFold(MFunction m)
    {
        _m = m;
        _occurrences = new int[m.NextVReg];
        _defs = new int[m.NextVReg];
        _defAt = new (int, int)[m.NextVReg];
        _defBlock = new int[m.NextVReg];
        Array.Fill(_defBlock, -2);
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            List<MInstr> ins = m.Blocks[b].Instrs;
            for (int k = 0; k < ins.Count; k++)
            {
                Count(ins[k], b, k);
            }
        }
    }

    public static void Run(MFunction m)
    {
        AddressFold fold = new(m);
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            MBlock block = m.Blocks[b];
            bool bases = fold.FoldBases(block.Instrs, b);
            bool indexes = fold.FoldIndexes(block.Instrs, b);
            if (bases || indexes)
            {
                block.Instrs.RemoveAll(i => ReferenceEquals(i, fold._gone));
            }
        }
    }

    private void Count(MInstr i, int block, int at)
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
                        _defAt[r.Id] = (block, at);
                        _defBlock[r.Id] = _defBlock[r.Id] == -2 || _defBlock[r.Id] == block ? block : -1;
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

    private bool FoldBases(List<MInstr> ins, int block)
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
            // A COPY READ ONLY AS A BASE is the base it copies: a `ref` local
            // made of a field's address and copied for the join after its
            // arms. Folded first, it leaves the address read only as one.
            if (def.Op == MOp.Mov && def.Width == 8 && _defs[t.Id] == 1
                && def.Operands[1] is MReg { IsPhys: false, IsFloat: false } copied && copied.Id != t.Id)
            {
                x = copied;
            }
            else if (def.Op == MOp.Lea)
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
            if (!ok)
            {
                continue;
            }
            // Read in other blocks as well: each a base there, in a block this
            // one dominates, of an X and Y no write between can change.
            List<(int Block, MInstr I, int K)>? beyond = null;
            if (_occurrences[t.Id] != made + uses.Count)
            {
                beyond = Beyond(t, x, y, disp, block, start, made + uses.Count);
                if (beyond is null)
                {
                    continue;
                }
            }
            if (uses.Count == 0 && beyond is null)
            {
                continue;
            }
            int last = uses.Count > 0 ? uses[^1].J : ins.Count - 1;
            if (Written(ins, start + 1, last, x.Id) || (y is not null && Written(ins, q + 1, last, y.Id)))
            {
                continue;
            }
            List<(int Block, MInstr I, int K)> all = uses.Select(u => (block, ins[u.J], u.K)).ToList();
            if (beyond is not null) all.AddRange(beyond);
            foreach ((int where, MInstr at, int k) in all)
            {
                MMem mem = (MMem)at.Operands[k];
                at.Operands[k] = new MMem(x, checked((int)(mem.Disp + disp)))
                {
                    Index = y ?? mem.Index,
                    Scale = y is not null ? scale : mem.Scale,
                    Segment = mem.Segment,
                };
                Noted(where, at, k, mem);
            }
            _occurrences[x.Id] += all.Count - 1;
            if (y is not null)
            {
                _occurrences[y.Id] += all.Count - 1;
            }
            _occurrences[t.Id] = 0;
            _defs[t.Id] = 0;
            Remove(ins, q);
            Remove(ins, start);
            any = true;
        }
        return any;
    }

    // ---- 2. index = Z << k ------------------------------------------------------------

    private bool FoldIndexes(List<MInstr> ins, int block)
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
                Noted(block, ins[j], k, mem);
            }
            _occurrences[z.Id] += uses.Count - 1;
            _occurrences[yv.Id] = 0;
            _defs[yv.Id] = 0;
            Remove(ins, p);
            Remove(ins, q);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Every occurrence of t outside its own block, when each is a plain base
    /// that can take X+Y*s+d, in a block t's block strictly dominates, and X
    /// and Y each hold one value from before t is made on (Fixed) -- null
    /// otherwise. `counted`: t's occurrences in
    /// its own block, all after `start`, which with these must be all it has.
    /// </summary>
    private List<(int Block, MInstr I, int K)>? Beyond(MReg t, MReg x, MReg? y, long disp, int block, int start, int counted)
    {
        if (!Fixed(x, block, start) || y is not null && !Fixed(y, block, start))
        {
            return null;
        }
        List<(int Block, MInstr I, int K)> found = new();
        // An occurrence noted twice -- read again after a fold that put it back -- counts once.
        Dictionary<MInstr, long> seen = new(ReferenceEqualityComparer.Instance);
        foreach ((int b, MInstr u, int k) in Sites()[t.Id])
        {
            if (b == block || _removed.Contains(u))
            {
                continue;
            }
            long bit = 1L << k;
            seen.TryGetValue(u, out long had);
            if ((had & bit) != 0)
            {
                continue;
            }
            seen[u] = had | bit;
            switch (u.Operands[k])
            {
                case MReg r when r.Id == t.Id:
                    return null;
                case MMem mem when mem.Index?.Id == t.Id:
                    return null;
                case MMem mem when mem.Base?.Id == t.Id:
                    if (!Plain(mem) || (y is not null && mem.Index is not null) || !FitsDisp(mem.Disp, disp) || !Dominates(block, b))
                    {
                        return null;
                    }
                    found.Add((b, u, k));
                    break;
            }
        }
        return counted + found.Count == _occurrences[t.Id] ? found : null;
    }

    /// <summary>Every register's occurrences in the function as it stands.</summary>
    private List<(int Block, MInstr I, int K)>[] Sites()
    {
        if (_sites is not null)
        {
            return _sites;
        }
        _sites = new List<(int, MInstr, int)>[_m.NextVReg];
        for (int v = 0; v < _sites.Length; v++) _sites[v] = new();
        for (int b = 0; b < _m.Blocks.Count; b++)
        {
            foreach (MInstr u in _m.Blocks[b].Instrs)
            {
                for (int k = 0; k < u.Operands.Count; k++)
                {
                    switch (u.Operands[k])
                    {
                        case MReg { IsPhys: false } r:
                            _sites[r.Id].Add((b, u, k));
                            break;
                        case MMem mem:
                            if (mem.Base is { IsPhys: false } mb) _sites[mb.Id].Add((b, u, k));
                            if (mem.Index is { IsPhys: false } mx && mx.Id != mem.Base?.Id) _sites[mx.Id].Add((b, u, k));
                            break;
                    }
                }
            }
        }
        return _sites;
    }

    /// <summary>A memory operand rewritten from `old`: the registers it now names that `old` did not are noted.</summary>
    private void Noted(int block, MInstr at, int k, MMem old)
    {
        if (_sites is null)
        {
            return;
        }
        MMem now = (MMem)at.Operands[k];
        if (now.Base is { IsPhys: false } nb && nb.Id != old.Base?.Id && nb.Id != old.Index?.Id)
        {
            _sites[nb.Id].Add((block, at, k));
        }
        if (now.Index is { IsPhys: false } nx && nx.Id != now.Base?.Id && nx.Id != old.Base?.Id && nx.Id != old.Index?.Id)
        {
            _sites[nx.Id].Add((block, at, k));
        }
    }

    private void Remove(List<MInstr> ins, int at)
    {
        _removed.Add(ins[at]);
        ins[at] = _gone;
    }

    /// <summary>
    /// Whether a register holds one value from before `start` in `block` on:
    /// the frame pointer, or one written in one block only -- `mov v, [gs:0]
    /// ; and v, mask` -- whose writes come before, that block dominating
    /// this one or being it.
    /// </summary>
    private bool Fixed(MReg r, int block, int start)
    {
        if (r.IsPhys)
        {
            return r.Id == (int)Gpr.Rbp;
        }
        if (_defs[r.Id] < 1 || _defBlock[r.Id] < 0)
        {
            return false;
        }
        (int b, int at) = _defAt[r.Id];
        return b == block ? at < start : Dominates(b, block);
    }

    /// <summary>Whether block a dominates block b (and is not b).</summary>
    private bool Dominates(int a, int b)
    {
        if (_idom is null)
        {
            Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < _m.Blocks.Count; k++) index[_m.Blocks[k]] = k;
            _idom = BlockDominators.Of(_m.Blocks.Count, k => _m.Successors(k).Where(index.ContainsKey).Select(t => index[t]));
        }
        return BlockDominators.Strictly(_idom, a, b);
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
                case MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepRax or MOp.CallKeepCas or MOp.Jmp or MOp.Ret or MOp.Epilogue:
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
