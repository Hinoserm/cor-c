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
/// The steps, each within one block, on virtual registers only. Three are
/// described here; the code has more, each with its own comment: 0 (a copy
/// used as a base), S (a symbol's address used as a base) and 4 (a load
/// only compared, compared in memory).
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
///
/// Step 1's t may be read as a base in other blocks too, those its block
/// dominates, where X (and Y) are written in one block only, before t in it
/// or in a block that dominates t's: X then holds at every use what it held
/// where t was made. The inlined allocator's fast path made `mov t, thread
/// ; add t, 64`, stored through t, branched, and stored through it again.
/// </summary>
internal static class AddressFold
{
    /// <summary>
    /// Where each register is written: in one block only (its index), or -1,
    /// and the last write's place there; and where each occurs (Sites).
    /// </summary>
    private sealed class WriteSites
    {
        public required MFunction M;
        public required int[] Block;
        public required int[] At;
        private int[]? _idom;

        public bool Dominates(int a, int b)
        {
            if (_idom is null)
            {
                Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
                for (int k = 0; k < M.Blocks.Count; k++) index[M.Blocks[k]] = k;
                _idom = BlockDominators.Of(M.Blocks.Count, k => M.Successors(k).Where(index.ContainsKey).Select(t => index[t]));
            }
            return BlockDominators.Strictly(_idom, a, b);
        }

        /// <summary>Whether a virtual register holds one value from before `start` in `block` on.</summary>
        public bool Fixed(int v, int block, int start)
            => Block[v] >= 0 && (Block[v] == block ? At[v] < start : Dominates(Block[v], block));

        // Where each register occurs, by block, instruction and operand, made
        // when Beyond first asks, so a base read in other blocks is found
        // among its own occurrences and not by walking the whole function for
        // every candidate (a function of ten thousand blocks took half an
        // hour so). A block is indexed again once folded and a fold's rewrite
        // in another block is noted; an occurrence rewritten away or removed
        // is passed over where read. Beyond never reads its own block's.
        private List<(int Block, MInstr I, int K)>[]? _sites;
        private readonly HashSet<MInstr> _removed = new(ReferenceEqualityComparer.Instance);

        public List<(int Block, MInstr I, int K)>[] Sites()
        {
            if (_sites is null)
            {
                _sites = new List<(int, MInstr, int)>[M.NextVReg];
                for (int v = 0; v < _sites.Length; v++) _sites[v] = new();
                for (int b = 0; b < M.Blocks.Count; b++) Add(b);
            }
            return _sites;
        }

        /// <summary>A block's occurrences as it stands, once it is folded.</summary>
        public void Indexed(int b)
        {
            if (_sites is not null) Add(b);
        }

        private void Add(int b)
        {
            foreach (MInstr u in M.Blocks[b].Instrs)
            {
                for (int k = 0; k < u.Operands.Count; k++) Note(b, u, k);
            }
        }

        /// <summary>The registers operand k of `at` names now.</summary>
        public void Note(int b, MInstr at, int k)
        {
            if (_sites is null) return;
            switch (at.Operands[k])
            {
                case MReg { IsPhys: false } r:
                    _sites[r.Id].Add((b, at, k));
                    break;
                case MMem mem:
                    if (mem.Base is { IsPhys: false } mb) _sites[mb.Id].Add((b, at, k));
                    if (mem.Index is { IsPhys: false } mx && mx.Id != mem.Base?.Id) _sites[mx.Id].Add((b, at, k));
                    break;
            }
        }

        public bool IsRemoved(MInstr i) => _removed.Contains(i);

        public void Remove(List<MInstr> ins, int at) => Replace(ins, at, Gone());

        /// <summary>An instruction put in another's place: the old one's occurrences are passed over from now on.</summary>
        public void Replace(List<MInstr> ins, int at, MInstr with)
        {
            _removed.Add(ins[at]);
            ins[at] = with;
        }
    }

    /// <summary>
    /// `absolute`: a symbol's address may be a 32-bit displacement (i386, not
    /// position-independent code, which Pic has rewritten by now).
    /// `compareMemory`: a load only compared may be compared in memory (step
    /// 4). On the Pentium such a compare -- a displacement and an immediate
    /// -- gives up the V pipe, at most a clock; the register it frees is a
    /// spill's store and reload saved on a machine of six. One image runs on
    /// the 486, the Pentium and the K6 alike (--cpu 486), so it is done for all.
    /// </summary>
    public static void Run(MFunction m, bool absolute = false, bool compareMemory = false)
    {
        int n = m.NextVReg;
        int[] occurrences = new int[n];
        int[] defs = new int[n];
        WriteSites writes = new() { M = m, Block = new int[n], At = new int[n] };
        Array.Fill(writes.Block, -2);
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            List<MInstr> ins = m.Blocks[b].Instrs;
            for (int k = 0; k < ins.Count; k++)
            {
                Count(ins[k], occurrences, defs, writes, b, k);
            }
        }
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            if (FoldBlock(m.Blocks[b], occurrences, defs, writes, b, absolute, compareMemory, m.Frame))
            {
                m.Blocks[b].Instrs.RemoveAll(i => i.Op == MOp.Nop && i.Operands.Count == 1 && i.Operands[0] is MImm { Value: Removed });
            }
            writes.Indexed(b);
        }
    }

    /// <summary>The marker a removed instruction carries until the block is swept.</summary>
    private const long Removed = 0x41464C44;

    private static void Count(MInstr i, int[] occurrences, int[] defs, WriteSites writes, int block, int at)
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
                        writes.Block[r.Id] = writes.Block[r.Id] == -2 || writes.Block[r.Id] == block ? block : -1;
                        writes.At[r.Id] = at;
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
                // A call through a register leaves the flags as any call
                // does, its callee's: an element passed to a virtual or an
                // interface call kept its address out of the mode for it.
                case MOp.Call or MOp.CallInd or MOp.Jmp or MOp.Ret or MOp.Epilogue:
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

    /// <summary>
    /// Every occurrence of t outside its own block, when each is a plain
    /// base with no index that can take X+Y (or X+imm) in a block t's block
    /// strictly dominates, and X and Y hold one value from before t is made
    /// on -- null otherwise. `counted`: t's occurrences in its own block,
    /// which with these must be all it has.
    /// </summary>
    private static List<(int Block, MInstr I, int K)>? Beyond(WriteSites writes, MReg t, MReg x, MOperand y, int block, int start, int counted, int[] occurrences)
    {
        if (!writes.Fixed(x.Id, block, start) || y is MReg yr && !writes.Fixed(yr.Id, block, start))
        {
            return null;
        }
        List<(int Block, MInstr I, int K)> found = new();
        // An occurrence noted more than once counts once.
        Dictionary<MInstr, long> seen = new(ReferenceEqualityComparer.Instance);
        foreach ((int b, MInstr u, int k) in writes.Sites()[t.Id])
        {
            if (b == block || writes.IsRemoved(u))
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
                    if (!Plain(mem) || mem.Index is not null || (y is MImm imm && !FitsDisp(mem.Disp, imm.Value)) || !writes.Dominates(block, b))
                    {
                        return null;
                    }
                    found.Add((b, u, k));
                    break;
            }
        }
        return counted + found.Count == occurrences[t.Id] ? found : null;
    }

    private static bool FoldBlock(MBlock b, int[] occurrences, int[] defs, WriteSites writes, int block, bool absolute, bool compareMemory, Frame frameOf)
    {
        List<MInstr> ins = b.Instrs;
        bool any = false;

        // ---- S. base = a symbol's address ----
        //
        // `mov t, sym` whose t is read nowhere but as the base of plain memory
        // operands after it in this block: each becomes [sym + disp], with its
        // index if it has one -- i386 forms an absolute address for free. A
        // static read through its address (Sys.Peek(Sys.AddressOf(ref s)),
        // the allocator's class tables read as words at every inlined `new`)
        // was `mov t, sym ; mov t, [t]`: 8,100 of them in a kernel.
        if (absolute)
        {
            for (int q = 0; q < ins.Count; q++)
            {
                MInstr load = ins[q];
                if (load.Op != MOp.Mov || load.Width != 4 || load.Operands.Count != 2
                    || load.Operands[0] is not MReg { IsPhys: false } t || defs[t.Id] != 1
                    || load.Operands[1] is not MImm { Symbol: not null, Label: null, Value: >= int.MinValue and <= int.MaxValue } sym)
                {
                    continue;
                }
                List<(int J, int K)> uses = new();
                bool ok = true;
                for (int j = q + 1; j < ins.Count && ok; j++)
                {
                    MInstr use = ins[j];
                    for (int k = 0; k < use.Operands.Count; k++)
                    {
                        switch (use.Operands[k])
                        {
                            case MReg r when r.Id == t.Id:
                            case MMem m1 when m1.Index?.Id == t.Id:
                            case MMem m2 when m2.Base?.Id == t.Id && (!Plain(m2) || !FitsDisp(m2.Disp, sym.Value)):
                                ok = false;
                                break;
                            case MMem mem when mem.Base?.Id == t.Id:
                                uses.Add((j, k));
                                break;
                        }
                    }
                }
                if (!ok || uses.Count == 0 || uses.Count != occurrences[t.Id] - 1)
                {
                    continue;
                }
                foreach ((int j, int k) in uses)
                {
                    MMem mem = (MMem)ins[j].Operands[k];
                    ins[j].Operands[k] = new MMem(null, checked(mem.Disp + (int)sym.Value)) { Index = mem.Index, Scale = mem.Scale, Symbol = sym.Symbol, Device = mem.Device };
                }
                occurrences[t.Id] = 0;
                writes.Remove(ins, q);
                any = true;
            }
        }

        // ---- 0. base = X, a copy ----
        //
        // `mov u, t` whose u is read nowhere but as a base, here or in the
        // blocks this one dominates, is t: a `ref` local made of a field's
        // address and copied for the join after its arms. Last first, so a
        // copy of a copy is t's before t is asked about in step 1.
        for (int q = ins.Count - 1; q >= 0; q--)
        {
            MInstr copy = ins[q];
            if (copy.Op != MOp.Mov || copy.Width != 4 || copy.Operands.Count != 2
                || copy.Operands[0] is not MReg { IsPhys: false } u || defs[u.Id] != 1
                || copy.Operands[1] is not MReg { IsPhys: false } t || t.Id == u.Id)
            {
                continue;
            }
            List<(int J, int K)> uses = new();
            bool ok = true;
            for (int j = q + 1; j < ins.Count && ok; j++)
            {
                MInstr use = ins[j];
                for (int k = 0; k < use.Operands.Count; k++)
                {
                    switch (use.Operands[k])
                    {
                        case MReg r when r.Id == u.Id:
                        case MMem m1 when m1.Index?.Id == u.Id:
                        case MMem m2 when m2.Base?.Id == u.Id && !Plain(m2):
                            ok = false;
                            break;
                        case MMem mem when mem.Base?.Id == u.Id:
                            uses.Add((j, k));
                            break;
                    }
                }
            }
            if (!ok)
            {
                continue;
            }
            List<(int Block, MInstr I, int K)>? beyond = null;
            if (occurrences[u.Id] != 1 + uses.Count)
            {
                beyond = Beyond(writes, u, t, new MImm(0), block, q, 1 + uses.Count, occurrences);
                if (beyond is null)
                {
                    continue;
                }
            }
            int last = uses.Count > 0 ? uses[^1].J : ins.Count - 1;
            if ((uses.Count == 0 && beyond is null) || Written(ins, q + 1, last, t.Id))
            {
                continue;
            }
            List<(int Block, MInstr I, int K)> all = uses.Select(x => (block, ins[x.J], x.K)).ToList();
            if (beyond is not null) all.AddRange(beyond);
            foreach ((int where, MInstr at, int k) in all)
            {
                MMem mem = (MMem)at.Operands[k];
                at.Operands[k] = new MMem(t, mem.Disp) { Index = mem.Index, Scale = mem.Scale, Device = mem.Device };
                writes.Note(where, at, k);
            }
            occurrences[t.Id] += all.Count - 1;
            occurrences[u.Id] = 0;
            writes.Remove(ins, q);
            any = true;
        }

        // ---- F. base = a frame address ----
        //
        // `lea t, [ebp+d]` whose t is read nowhere but as the base of plain
        // memory operands after it in this block: each becomes [ebp+d+disp]
        // (with its index) -- a local's address taken only to be written
        // through, `lea eax, [ebp-48h] ; mov dword [eax], 0`: 24,065 frame
        // LEAs over a kernel, its modules and its userland, and an address
        // generation interlock each on the 486 and the Pentium. FrameCompact
        // places [ebp+d+disp] in the block it lies in, as it does any interior
        // offset, and leaves a frame alone that it cannot place.
        for (int q = 0; q < ins.Count; q++)
        {
            MInstr lea = ins[q];
            if (lea.Op != MOp.Lea || lea.Operands.Count != 2 || lea.Operands[0] is not MReg { IsPhys: false } t || defs[t.Id] != 1
                || lea.Operands[1] is not MMem { Base: { IsPhys: true, Phys: Gpr.Ebp } ebp, Index: null, Symbol: null, Label: null, IsSpill: false } frame)
            {
                continue;
            }
            List<(int J, int K)> uses = new();
            bool ok = true;
            for (int j = q + 1; j < ins.Count && ok; j++)
            {
                MInstr use = ins[j];
                for (int k = 0; k < use.Operands.Count; k++)
                {
                    switch (use.Operands[k])
                    {
                        case MReg r when r.Id == t.Id:
                        case MMem m1 when m1.Index?.Id == t.Id:
                        // Only within the block the LEA names: FrameCompact moves
                        // each operand by the block its displacement falls in,
                        // and an element past the block's end, or an index the
                        // displacement does not show, would be moved by another's.
                        case MMem m2 when m2.Base?.Id == t.Id && (!Plain(m2) || m2.Index is not null || !FitsDisp(m2.Disp, frame.Disp)
                            || !SameAllocation(frameOf, frame.Disp, frame.Disp + m2.Disp)):
                            ok = false;
                            break;
                        case MMem mem when mem.Base?.Id == t.Id:
                            uses.Add((j, k));
                            break;
                    }
                }
            }
            if (!ok || uses.Count == 0 || uses.Count != occurrences[t.Id] - 1)
            {
                continue;
            }
            foreach ((int j, int k) in uses)
            {
                MMem mem = (MMem)ins[j].Operands[k];
                ins[j].Operands[k] = new MMem(ebp, mem.Disp + frame.Disp) { Index = mem.Index, Scale = mem.Scale, Device = mem.Device };
            }
            occurrences[t.Id] = 0;
            writes.Remove(ins, q);
            any = true;
        }

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
            // copy (1) + add (1) + the memory bases, here and in the blocks
            // this one dominates.
            if (!ok)
            {
                continue;
            }
            List<(int Block, MInstr I, int K)>? beyond = null;
            if (occurrences[t.Id] != 2 + uses.Count)
            {
                beyond = Beyond(writes, t, x, y, block, p, 2 + uses.Count, occurrences);
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
            if (Written(ins, p + 1, last, x.Id) || (y is MReg yv && Written(ins, q + 1, last, yv.Id)))
            {
                continue;
            }
            List<(int Block, MInstr I, int K)> all = uses.Select(u => (block, ins[u.J], u.K)).ToList();
            if (beyond is not null) all.AddRange(beyond);
            foreach ((int where, MInstr at, int k) in all)
            {
                MMem mem = (MMem)at.Operands[k];
                at.Operands[k] = y is MImm imm
                    ? new MMem(x, checked(mem.Disp + (int)imm.Value)) { Device = mem.Device }
                    : new MMem(x, mem.Disp) { Index = (MReg)y, Scale = 1, Device = mem.Device };
                writes.Note(where, at, k);
            }
            occurrences[x.Id] += all.Count - 1;
            if (y is MReg ym)
            {
                occurrences[ym.Id] += all.Count - 1;
            }
            occurrences[t.Id] = 0;
            writes.Remove(ins, p);
            writes.Remove(ins, q);
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
                ins[j].Operands[k] = new MMem(mem.Base, checked(mem.Disp + (int)delta)) { Index = mem.Index, Scale = mem.Scale, Device = mem.Device };
            }
            occurrences[t.Id]--;
            defs[t.Id]--;
            writes.Remove(ins, q);
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
                ins[j].Operands[k] = new MMem(mem.Base, mem.Disp) { Index = z, Scale = 1 << (int)amount.Value, Device = mem.Device };
            }
            occurrences[z.Id] += uses.Count - 1;
            occurrences[yv.Id] = 0;
            writes.Remove(ins, p);
            writes.Remove(ins, q);
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
                quiet = between.Op is MOp.Nop or MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Add or MOp.Sub or MOp.And or MOp.Or
                        or MOp.Xor or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Cmp or MOp.Test or MOp.Setcc
                    && !between.Lock && !between.Operands.Any(o => o is MMem)
                    && (mem.Base is null || !Writes(between, mem.Base.Id))
                    && (mem.Index is null || !Writes(between, mem.Index.Id));
            }
            if (!quiet) continue;
            alu.Operands[1] = mem;
            occurrences[t.Id] = 0;
            writes.Remove(ins, p);
            any = true;
        }

        // ---- 4. a load only compared, compared in memory ----
        //
        // `mov t, [m] ; cmp t, imm` and `mov t, [m] ; test t, t` whose t is
        // read nowhere else: `cmp dword [m], imm` and `cmp dword [m], 0` --
        // the same flags (a test and a compare with zero both clear CF and
        // OF), a byte less, and no register. A type's initialiser test at
        // every use (`cmp [flag], 1`), a field tested for null. The same
        // clocks on a 486 and a K6; on a Pentium at most the V pipe, for a
        // register (Run's compareMemory).
        for (int q = 1; q < ins.Count && compareMemory; q++)
        {
            MInstr cmp = ins[q];
            if (cmp.Width != 4 || cmp.Lock || cmp.Operands.Count != 2 || cmp.Operands[0] is not MReg { IsPhys: false } t
                || defs[t.Id] != 1)
            {
                continue;
            }
            MImm against;
            if (cmp.Op == MOp.Cmp && cmp.Operands[1] is MImm { IsPlain: true } imm && occurrences[t.Id] == 2) against = imm;
            else if (cmp.Op == MOp.Test && cmp.Operands[1] is MReg same && same.Id == t.Id && occurrences[t.Id] == 3) against = new MImm(0);
            else continue;
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
                quiet = between.Op is MOp.Nop or MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Add or MOp.Sub or MOp.And or MOp.Or
                        or MOp.Xor or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Cmp or MOp.Test or MOp.Setcc
                    && !between.Lock && !between.Operands.Any(o => o is MMem)
                    && (mem.Base is null || !Writes(between, mem.Base.Id))
                    && (mem.Index is null || !Writes(between, mem.Index.Id));
            }
            if (!quiet) continue;
            writes.Replace(ins, q, new MInstr(MOp.Cmp, mem, against) { Width = 4, Line = cmp.Line });
            occurrences[t.Id] = 0;
            writes.Remove(ins, p);
            any = true;
        }

        // ---- 5. a call through a pointer loaded only to be called ----
        //
        // `mov t, [m] ; call t` whose t is read nowhere else: `call [m]` --
        // a virtual or interface slot, a delegate's target, a GOT slot. The
        // same clocks on a Pentium (CALL r/m, 2, unpaired either way) and the
        // same vector decode on a K6 (AMD 21924's table, CALL mreg32 and
        // mem32 alike), an instruction, two bytes and a register fewer:
        // 189,429 of them over a kernel, its modules and its userland. Not a
        // CallKeep's, whose register means more than the target.
        for (int q = 1; q < ins.Count; q++)
        {
            MInstr call = ins[q];
            if (call.Op != MOp.CallInd || call.Operands.Count != 1 || call.Operands[0] is not MReg { IsPhys: false } t
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
                quiet = between.Op is MOp.Nop or MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Add or MOp.Sub or MOp.And or MOp.Or
                        or MOp.Xor or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Cmp or MOp.Test or MOp.Setcc
                    && !between.Lock && !between.Operands.Any(o => o is MMem)
                    && (mem.Base is null || !Writes(between, mem.Base.Id))
                    && (mem.Index is null || !Writes(between, mem.Index.Id));
            }
            if (!quiet) continue;
            call.Operands[0] = mem;
            occurrences[t.Id] = 0;
            writes.Remove(ins, p);
            any = true;
        }
        return any;
    }

    private static MInstr Gone() => new(MOp.Nop, new MImm(Removed));

    /// <summary>Whether two frame displacements lie in one of the frame's allocations (FrameCompact's blocks).</summary>
    private static bool SameAllocation(Frame frame, int a, int b)
    {
        foreach (var block in frame.Allocations)
        {
            int end = block.Offset + Math.Max(block.Bytes, 1);
            if (a >= block.Offset && a < end) return b >= block.Offset && b < end;
        }
        return false;
    }

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
