#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// Block-local clean-up after register allocation, on physical registers.
///
/// The selector writes straightforward code and the allocator adds spill
/// traffic without looking at its neighbours; what is left over is local
/// and mechanical: a reload straight after the store that filled the slot,
/// a load whose register is overwritten before it is read, a conditional
/// jump around an unconditional one. Each rule here is a pattern over a
/// few adjacent instructions and never needs to know anything about the
/// rest of the function, which is what keeps this pass safe to run last.
/// </summary>
internal static class Peephole
{
    public static void Run(MFunction m)
    {
        ParameterHomes(m);
        ForwardSpillLoadsAcrossBlocks(m);
        DeadSpillStores(m);
        SpillStoresDeadAcrossBlocks(m);
        int[] liveOut = LiveOut(m, out int[] liveIn);
        Dictionary<MBlock, int> at = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < m.Blocks.Count; b++) at[m.Blocks[b]] = b;
        // WHAT A JUMP IN A BLOCK'S MIDDLE NEEDS: the registers live where it
        // goes. A walk from the block's end passing it must count them live
        // again -- a write after it (LoopRotate's copied test) is no reason to
        // drop a move the jump's way reads.
        int Jumped(MInstr i)
        {
            int live = 0;
            if (i.Op is MOp.Jcc or MOp.Jmp && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to })
                live = at.TryGetValue(to, out int t) ? liveIn[t] : 0xFF;
            else if (i.Op == MOp.JmpTable && i.Table is { } table)
                foreach (MBlock each in table) live |= at.TryGetValue(each, out int e) ? liveIn[e] : 0xFF;
            return live;
        }
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            MBlock block = m.Blocks[b];
            MBlock? next = b + 1 < m.Blocks.Count ? m.Blocks[b + 1] : null;
            ForwardStoreLoad(block.Instrs);
            ForwardSpillLoads(block.Instrs);
            DeadDefs(block.Instrs, liveOut[b], Jumped);
            RepeatedStores(block.Instrs, liveOut[b], Usable(m), Jumped);
            // Again: a run of stores of one constant now stores a register,
            // and a load of the slot just stored can read the register.
            ForwardStoreLoad(block.Instrs);
            MergePops(block.Instrs);
            RedundantTests(block.Instrs);
            ZeroWithXor(block.Instrs);
            SwapBack(block.Instrs);
            BeforeTrap(block.Instrs);
            ReadModifyWrite(block.Instrs, liveOut[b], Jumped);
            ChooseForCpu(block.Instrs, liveOut[b], Usable(m), Jumped);
            IncDec(block.Instrs);
            InvertJumpAroundJump(block.Instrs, next);
        }
        Unreachable(m);
    }

    // ---- a parameter spilled to its own slot ------------------------------------------

    /// <summary>
    /// `mov r, [ebp+P] ; mov [ebp-S], r` -- a parameter loaded and at once
    /// spilled to a slot of its own -- where S is the allocator's spill slot,
    /// the function never writes the parameter's word, takes no parameter's
    /// address and never holds EBP as a value, writes S nowhere else, reaches
    /// S and the eight bytes below it only as whole words (no address taken,
    /// no index, no other width), and no safepoint names S: the spill is the
    /// parameter's own slot, every read of S reads [ebp+P], and the store
    /// goes (and the load too, if nothing else reads r). About 44,000 of
    /// them over a kernel, its modules and its userland, each a store and a
    /// slot. A slot a safepoint names holds a reference the collector must
    /// find, and the maps name only the frame's own (negative) slots, so
    /// those stay.
    /// </summary>
    private static void ParameterHomes(MFunction m)
    {
        if (m.Blocks.Count == 0) return;
        static bool Ebp(MMem mem) => mem.Base is { IsPhys: true, Phys: Gpr.Ebp } && mem.Index is null && mem.Symbol is null && mem.Label is null;
        HashSet<int> mapped = new();
        foreach (Safepoint sp in m.Safepoints.Values) foreach (int at in sp.SlotOffsets) mapped.Add(at);
        // What every instruction does to the frame: the parameter bytes
        // written (eight from each write's start, whatever its width says --
        // a packed store's is eight and its Width the default four), each
        // slot's whole writes and plain whole reads, and every other kind of
        // reach into the frame: an address taken, an index, another width,
        // an instruction this does not know.
        HashSet<int> paramWritten = new();
        Dictionary<int, int> slotWrites = new();
        HashSet<int> plain = new();     // displacements read or written plainly and whole
        HashSet<int> other = new();     // displacements reached any other way
        foreach (MBlock block in m.Blocks)
            foreach (MInstr i in block.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    // EBP AS A VALUE -- copied, added to -- reaches the frame
                    // by arithmetic no operand shows: nothing is moved then.
                    if (i.Operands[k] is MReg { IsPhys: true, Phys: Gpr.Ebp }) return;
                    if (i.Operands[k] is not MMem mem || mem.Base is not { IsPhys: true, Phys: Gpr.Ebp }) continue;
                    bool writes = (Roles.Of(i.Op, k) & Roles.Role.Def) != 0 || !Understood(i);
                    if (mem.Disp >= 8)
                    {
                        // A parameter's address taken, or reached by index: nothing moves.
                        if (i.Op == MOp.Lea || !Ebp(mem)) return;
                        if (writes) for (int b = mem.Disp; b < mem.Disp + 8; b++) paramWritten.Add(b);
                        continue;
                    }
                    bool whole = i.Op != MOp.Lea && Ebp(mem) && i.Width == 4 && Understood(i)
                        && (IsMov(i) || i.Op is MOp.Push or MOp.Cmp or MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Test);
                    if (!whole) { other.Add(mem.Disp); continue; }
                    plain.Add(mem.Disp);
                    if (writes) slotWrites[mem.Disp] = slotWrites.GetValueOrDefault(mem.Disp) + 1;
                }
        List<MInstr> entry = m.Blocks[0].Instrs;
        Dictionary<int, int> home = new();
        for (int k = 1; k < entry.Count; k++)
        {
            MInstr store = entry[k], load = entry[k - 1];
            if (!IsMov(load) || !IsMov(store) || load.Operands[0] is not MReg r || load.Operands[1] is not MMem p || !Ebp(p) || p.Disp < 8
                || store.Operands[0] is not MMem { IsSpill: true } s || !Ebp(s) || s.Disp >= 0 || store.Operands[1] is not MReg r2 || r2.Id != r.Id)
            {
                continue;
            }
            if (mapped.Contains(s.Disp) || slotWrites.GetValueOrDefault(s.Disp) != 1) continue;
            bool clear = true;
            for (int b = p.Disp - 7; b < p.Disp + 4 && clear; b++) clear = !paramWritten.Contains(b);
            // Nothing else within reach of the slot's four bytes: no other
            // plain word overlapping it, and nothing reached any other way
            // from eight below it (a quadword at S-4 covers it) to its end.
            for (int b = s.Disp - 7; b < s.Disp + 4 && clear; b++) clear = !other.Contains(b);
            for (int b = s.Disp - 3; b < s.Disp + 4 && clear; b++) clear = b == s.Disp || !plain.Contains(b);
            if (!clear) continue;
            home[s.Disp] = p.Disp;
            entry.RemoveAt(k);
            k--;
        }
        if (home.Count == 0) return;
        foreach (MBlock block in m.Blocks)
            foreach (MInstr i in block.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is MMem mem && Ebp(mem) && home.TryGetValue(mem.Disp, out int to))
                        i.Operands[k] = new MMem(mem.Base, to);
    }

    // ---- what a trap makes dead ---------------------------------------------------------

    /// <summary>
    /// A block ending in INT3 (the selector's unreachable, after a call that
    /// does not return -- a throw, an index out of range) runs nothing after
    /// the call: an `add esp, n` cleaning up its arguments before the trap
    /// is never reached, and a second INT3 neither. 26,312 such cleanups and
    /// 78,093 doubled traps over a kernel, its modules and its userland. The
    /// call's return address still lies in the function, on the trap.
    /// </summary>
    private static void BeforeTrap(List<MInstr> instrs)
    {
        int n = instrs.Count;
        if (n < 2 || instrs[n - 1].Op != MOp.Int3 || instrs[n - 1].Operands.Count != 0) return;
        int k = n - 2;
        while (k >= 0 && (instrs[k].Op == MOp.Int3 && instrs[k].Operands.Count == 0
            || instrs[k].Op == MOp.Add && instrs[k].Width == 4 && instrs[k].Operands.Count == 2
               && instrs[k].Operands[0] is MReg { IsPhys: true, Phys: Gpr.Esp } && instrs[k].Operands[1] is MImm { IsPlain: true }))
        {
            instrs.RemoveAt(k);
            k--;
        }
    }

    // ---- a move straight back ---------------------------------------------------------

    /// <summary>
    /// `mov a, b ; mov b, a`: after the first the two hold one value, so the
    /// second moves nothing -- 4,331 of them over a kernel, its modules and
    /// its userland.
    /// </summary>
    private static void SwapBack(List<MInstr> instrs)
    {
        for (int k = instrs.Count - 1; k >= 1; k--)
        {
            MInstr second = instrs[k], first = instrs[k - 1];
            if (IsMov(first) && IsMov(second) && first.Operands[0] is MReg a && first.Operands[1] is MReg b
                && second.Operands[0] is MReg b2 && second.Operands[1] is MReg a2 && a.Id == a2.Id && b.Id == b2.Id)
            {
                instrs.RemoveAt(k);
            }
        }
    }

    // ---- blocks nothing reaches -----------------------------------------------------

    /// <summary>
    /// A block no jump, no fall-through, no jump table, no label taken and no
    /// landing pad reaches is never run, and goes: the layout and the
    /// peephole leave such ones behind -- a block of one `jmp X` laid out
    /// straight after another `jmp X`, 23,081 of them over a kernel, its
    /// modules and its userland. A landing pad is reached by the unwinder
    /// and a block whose address is taken by whatever takes it, so both are
    /// roots with the entry.
    /// </summary>
    private static void Unreachable(MFunction m)
    {
        int n = m.Blocks.Count;
        if (n < 2) return;
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < n; b++) index[m.Blocks[b]] = b;
        bool[] reached = new bool[n];
        Stack<int> work = new();
        void Root(MBlock? block)
        {
            if (block is not null && index.TryGetValue(block, out int at) && !reached[at]) { reached[at] = true; work.Push(at); }
        }
        Root(m.Blocks[0]);
        foreach (MBlock block in m.Blocks)
        {
            if (block.Source is { IsLandingPad: true }) Root(block);
            foreach (MInstr i in block.Instrs)
                foreach (MOperand o in i.Operands)
                {
                    if (o is MImm { Label: { } taken }) Root(taken);
                    else if (o is MMem { Label: { } table }) Root(table);
                }
        }
        List<MBlock> next = new();
        while (work.Count > 0)
        {
            m.SuccessorsInto(work.Pop(), next);
            foreach (MBlock to in next) Root(to);
        }
        if (reached.All(x => x)) return;
        List<MBlock> kept = new(n);
        for (int b = 0; b < n; b++) if (reached[b]) kept.Add(m.Blocks[b]);
        m.Blocks.Clear();
        m.Blocks.AddRange(kept);
    }

    // ---- which physical registers leave each block alive --------------------------

    /// <summary>
    /// A bit per register, live at the end of each block. The same
    /// use/def dataflow the allocator ran, now over the eight hardware
    /// registers, so dead-definition removal can see past a block's end
    /// instead of assuming everything is wanted there.
    /// </summary>
    private static int[] LiveOut(MFunction m, out int[] liveInOut)
    {
        // A BLOCK MAY LEAVE FROM ITS MIDDLE: a conditional jump with more
        // after it (LoopRotate copies a loop's test over a latch's jump, so
        // `jl body` is followed by the copy's `mov eax, ...`). Summed as one
        // use and one def a block, a register the copy writes after the jump
        // was killed for the jump's way too, and the body's register -- a
        // loop's `sp` -- was taken for dead in every block that reached it:
        // the move that kept it was removed. So each block is scanned from its
        // end, every jump on the way adding what is live where it goes.
        int nb = m.Blocks.Count;
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++)
        {
            index[m.Blocks[b]] = b;
        }
        int[] liveIn = new int[nb];
        int[] liveOut = new int[nb];
        int[][] succ = new int[nb][];
        List<MBlock> successors = new();
        for (int b = 0; b < nb; b++)
        {
            m.SuccessorsInto(b, successors);
            int[] to = new int[successors.Count];
            for (int t = 0; t < to.Length; t++) to[t] = index[successors[t]];
            succ[b] = to;
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int b = nb - 1; b >= 0; b--)
            {
                MBlock block = m.Blocks[b];
                int o = 0;
                foreach (int t in succ[b])
                {
                    o |= liveIn[t];
                }
                liveOut[b] = o;
                // At the end: only a fall-through's way; each jump adds its own below.
                int live = !block.EndsUnconditionally && b + 1 < nb ? liveIn[b + 1] : 0;
                List<MInstr> instrs = block.Instrs;
                for (int k = instrs.Count - 1; k >= 0; k--)
                {
                    MInstr i = instrs[k];
                    if (i.Op is MOp.Jcc or MOp.Jmp && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to } && index.TryGetValue(to, out int at))
                    {
                        live |= liveIn[at];
                        continue;
                    }
                    if (i.Op == MOp.JmpTable && i.Table is { } table)
                    {
                        foreach (MBlock t in table) if (index.TryGetValue(t, out int ta)) live |= liveIn[ta];
                    }
                    if (!Understood(i))
                    {
                        // Everything it might read is read; the pessimistic view.
                        live = 0xFF;
                        continue;
                    }
                    Masks(i, out long reads, out long writes);
                    live = (live & ~(int)writes) | (int)reads;
                }
                live &= 0xFF;
                if (live != liveIn[b])
                {
                    liveIn[b] = live;
                    changed = true;
                }
            }
        }
        liveInOut = liveIn;
        // And once more for the ends, now that every block's entry is settled.
        for (int b = 0; b < nb; b++)
        {
            int o = 0;
            foreach (int t in succ[b]) o |= liveIn[t];
            liveOut[b] = o;
        }
        return liveOut;
    }

    /// <summary>
    /// An instruction's explicit and implicit register reads and writes as two masks, bit per register number, in a loop: the
    /// iterators were two objects for every instruction asked about. A
    /// register numbered past 63 shares a bit, which only ever makes an
    /// answer more cautious.
    /// </summary>
    private static void Masks(MInstr i, out long reads, out long writes)
    {
        reads = 0;
        writes = 0;
        if (i.Op == MOp.Xor && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id)
        {
            writes = Bit(x.Id);
        }
        else
        {
            for (int k = 0; k < i.Operands.Count; k++)
            {
                switch (i.Operands[k])
                {
                    case MReg r:
                    {
                        Roles.Role role = Roles.Of(i.Op, k);
                        if ((role & Roles.Role.Use) != 0) reads |= Bit(r.Id);
                        if ((role & Roles.Role.Def) != 0) writes |= Bit(r.Id);
                        break;
                    }
                    case MMem m:
                        if (m.Base is not null) reads |= Bit(m.Base.Id);
                        if (m.Index is not null) reads |= Bit(m.Index.Id);
                        break;
                }
            }
        }
        foreach (Gpr g in Roles.ImplicitUses(i)) reads |= Bit(MReg.Of(g).Id);
        foreach (Gpr g in Roles.ImplicitDefs(i)) writes |= Bit(MReg.Of(g).Id);
    }

    private static long Bit(int id) => 1L << (id & 63);

    // ---- store, then reload from the same slot ----------------------------------

    private static bool SameMem(MMem a, MMem b)
        => a.Disp == b.Disp && a.Scale == b.Scale && a.Symbol == b.Symbol && a.Reloc == b.Reloc && a.Label == b.Label
           && (a.Base?.Id ?? -1) == (b.Base?.Id ?? -1) && (a.Index?.Id ?? -1) == (b.Index?.Id ?? -1);

    private static bool IsMov(MInstr i) => i.Op == MOp.Mov && i.Width == 4;

    /// <summary>
    /// `mov [slot], r` followed at once by `mov r2, [slot]` reads back what
    /// was just written: it becomes `mov r2, r`, or nothing when r2 is r.
    /// </summary>
    private static void ForwardStoreLoad(List<MInstr> instrs)
    {
        for (int k = 0; k + 1 < instrs.Count; k++)
        {
            MInstr store = instrs[k];
            MInstr load = instrs[k + 1];
            if (!IsMov(store) || !IsMov(load) || store.Operands[0] is not MMem sm || store.Operands[1] is not MReg sr
                || load.Operands[0] is not MReg lr || load.Operands[1] is not MMem lm || !SameMem(sm, lm))
            {
                continue;
            }
            // The store's address must not depend on the register the load overwrites.
            if (lm.Base?.Id == lr.Id || lm.Index?.Id == lr.Id)
            {
                continue;
            }
            if (lr.Id == sr.Id)
            {
                instrs.RemoveAt(k + 1);
                k--;
            }
            else
            {
                instrs[k + 1] = new MInstr(MOp.Mov, MReg.Of(lr.Id), MReg.Of(sr.Id));
            }
        }
    }

    // ---- a spill slot written and never read ---------------------------------------

    /// <summary>
    /// A store to an allocator's slot that no instruction of the function reads
    /// and no stack map names: work for nothing. The allocator stores a spilled
    /// register at every write of it, and a value written twice in two-address
    /// form -- `mov t, d ; add t, h` -- whose register then carried it to its
    /// only use, was stored twice to a slot nothing read: SHA-256's rounds,
    /// with more live values than registers, wrote three such words a round.
    /// A slot a stack map lists stays written, whatever reads it: the
    /// collector reads it at that call.
    /// </summary>
    private static void DeadSpillStores(MFunction m)
    {
        HashSet<int> read = new();
        foreach (Safepoint map in m.Safepoints.Values)
            foreach (int offset in map.SlotOffsets) read.Add(offset);
        bool any = false;
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not MMem mem || !mem.IsSpill) continue;
                    if (k == 0 && IsMov(i) && !i.Lock && PrivateSpill(mem)) { any = true; continue; }
                    read.Add(mem.Disp);
                }
        if (!any) return;
        // A fault inside a block can land in a handler of this function that
        // reloads a value from its slot: there the first store is not dead.
        bool handlers = m.Blocks.Any(b => b.Source is { IsLandingPad: true });
        foreach (MBlock b in m.Blocks)
        {
            b.Instrs.RemoveAll(i => IsMov(i) && !i.Lock && i.Operands.Count == 2 && i.Operands[0] is MMem mem
                && PrivateSpill(mem) && !read.Contains(mem.Disp));
            // And within a block, a store the next store to the same slot
            // overwrites before anything reads it -- the slot shared with
            // other values that do read it. Walked backwards: `written` holds
            // the slots stored again further on with no read between. Only
            // where the block makes no call, whose stack map may read a slot,
            // and the function has no handler.
            if (handlers || b.Instrs.Any(i => i.Op is MOp.Call or MOp.CallInd)) continue;
            HashSet<int> written = new();
            for (int k = b.Instrs.Count - 1; k >= 0; k--)
            {
                MInstr i = b.Instrs[k];
                if (IsMov(i) && !i.Lock && i.Operands.Count == 2 && i.Operands[0] is MMem store && PrivateSpill(store))
                {
                    if (!written.Add(store.Disp)) { b.Instrs.RemoveAt(k); continue; }
                    if (i.Operands[1] is MMem) written.Clear();
                    continue;
                }
                foreach (MOperand o in i.Operands)
                    if (o is MMem mem)
                    {
                        if (PrivateSpill(mem)) written.Remove(mem.Disp);
                        else if (mem.Base?.Id == (int)Gpr.Ebp) written.Clear();
                    }
            }
        }
    }

    /// <summary>
    /// A STORE TO A SLOT THAT NOTHING READS BEFORE IT IS WRITTEN AGAIN, on
    /// any path from it, across blocks: a backward walk of which allocator
    /// slots are read further on. DeadSpillStores sees a slot no instruction
    /// reads, or one stored twice inside a block; a slot shared by two values
    /// (FrameCompact) whose first is read only where forwarding found it in a
    /// register (ForwardSpillLoadsAcrossBlocks) kept its first store, a lap of
    /// fan's vertex loop writing x to a slot the array length took over a
    /// block later. A call reads what its stack map names; a jump reads what
    /// is read where it goes; any other EBP-based operand may read them all.
    /// Not in a function with a handler, which may reload a slot from inside
    /// any block.
    /// </summary>
    private static void SpillStoresDeadAcrossBlocks(MFunction m)
    {
        int nb = m.Blocks.Count;
        if (nb == 0 || m.Blocks.Any(b => b.Source is { IsLandingPad: true })) return;
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++) index[m.Blocks[b]] = b;
        bool any = false;
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                if (StoreSlot(i) is not null) { any = true; break; }
        if (!any) return;

        HashSet<int>[] liveIn = new HashSet<int>[nb];
        for (int b = 0; b < nb; b++) liveIn[b] = new HashSet<int>();
        HashSet<int> all = new();
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach (MOperand o in i.Operands)
                    if (o is MMem mem && PrivateSpill(mem)) all.Add(mem.Disp);

        // Walks block b backwards from what is read after it, each store
        // whose slot is not read further on removed when `remove` says so;
        // answers what is read at its start.
        HashSet<int> Walk(int b, bool remove)
        {
            MBlock block = m.Blocks[b];
            HashSet<int> live = new();
            if (!block.EndsUnconditionally && b + 1 < nb) live.UnionWith(liveIn[b + 1]);
            List<MInstr> instrs = block.Instrs;
            for (int k = instrs.Count - 1; k >= 0; k--)
            {
                MInstr i = instrs[k];
                if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to })
                {
                    if (index.TryGetValue(to, out int t)) live.UnionWith(liveIn[t]); else live.UnionWith(all);
                    continue;
                }
                if (i.Op == MOp.JmpTable && i.Table is { } table)
                {
                    foreach (MBlock each in table)
                        if (index.TryGetValue(each, out int e)) live.UnionWith(liveIn[e]); else live.UnionWith(all);
                    continue;
                }
                if (StoreSlot(i) is int slot)
                {
                    if (!live.Contains(slot))
                    {
                        if (remove) instrs.RemoveAt(k);
                        continue;
                    }
                    live.Remove(slot);
                    if (i.Operands[1] is MMem source && source.Base?.Id == (int)Gpr.Ebp) live.UnionWith(all);
                    continue;
                }
                if (m.Safepoints.TryGetValue(i, out Safepoint? map)) live.UnionWith(map.SlotOffsets);
                else if (i.Op is MOp.Call or MOp.CallInd) live.UnionWith(all);
                foreach (MOperand o in i.Operands)
                    if (o is MMem mem)
                    {
                        if (PrivateSpill(mem)) live.Add(mem.Disp);
                        else if (mem.Base?.Id == (int)Gpr.Ebp || mem.Index?.Id == (int)Gpr.Ebp) live.UnionWith(all);
                    }
            }
            return live;
        }

        bool changed = true;
        int passes = 0;
        while (changed)
        {
            if (++passes > 1000 + 16 * nb) throw new InvalidOperationException(m.Source.Name + ": dead spill stores did not settle");
            changed = false;
            for (int b = nb - 1; b >= 0; b--)
            {
                HashSet<int> now = Walk(b, remove: false);
                if (!now.SetEquals(liveIn[b])) { liveIn[b] = now; changed = true; }
            }
        }
        for (int b = 0; b < nb; b++) Walk(b, remove: true);
    }

    /// <summary>The allocator slot a plain store writes, or null.</summary>
    private static int? StoreSlot(MInstr i)
        => IsMov(i) && !i.Lock && i.Operands.Count == 2 && i.Operands[0] is MMem store && PrivateSpill(store) ? store.Disp : null;

    // ---- a register written and then written again before being read -------------

    // Only allocator-owned slots participate. Source-language stack references
    // may be address-taken or volatile; an EBP address alone is not provenance.
    private static bool PrivateSpill(MMem m) => m.IsSpill
        && m.Base?.Id == (int)Gpr.Ebp && m.Index is null
        && m.Symbol is null && m.Label is null;

    private static void ForwardSpillLoads(List<MInstr> instrs)
    {
        for (int k = 0; k < instrs.Count; k++)
        {
            MInstr load = instrs[k];
            if (!IsMov(load) || load.Lock || load.Operands[0] is not MReg dest
                || load.Operands[1] is not MMem slot || !PrivateSpill(slot))
                continue;
            int clobbered = 0;
            for (int p = k - 1; p >= 0 && p >= k - 16; p--)
            {
                MInstr prev = instrs[p];
                if (prev.Lock) break;
                MReg? value = null;
                MMem? memory = null;
                if (IsMov(prev) && prev.Operands[0] is MReg r && prev.Operands[1] is MMem source)
                { value = r; memory = source; }
                else if (IsMov(prev) && prev.Operands[0] is MMem target && prev.Operands[1] is MReg s)
                { value = s; memory = target; }
                if (memory is not null)
                {
                    if (!PrivateSpill(memory)) break;
                    if (SameMem(memory, slot))
                    {
                        if ((clobbered & (1 << value!.Id)) == 0)
                            instrs[k] = new MInstr(MOp.Mov, MReg.Of(dest.Id), MReg.Of(value.Id)) { Line = load.Line };
                        break;
                    }
                    // Be conservative about partial overlaps, including unusual frames.
                    if (Math.Abs((long)memory.Disp - slot.Disp) < 4) break;
                }
                else if (prev.Operands.Any(o => o is MMem) || !SpillTransparent(prev.Op)) break;
                Masks(prev, out _, out long written);
                clobbered |= (int)written;
                if ((clobbered & (1 << (int)Gpr.Ebp)) != 0) break;
            }
        }
    }

    /// <summary>
    /// A RELOAD OF WHAT A REGISTER STILL HOLDS, ACROSS BLOCKS. The allocator
    /// reloads a spilled value at every read where its own register does not
    /// hold it, and each reload's register carries the value only along a
    /// straight run (its Reach); a value read in two blocks of a lap, past an
    /// if's merge, was loaded twice, the register that took it the first
    /// time still holding it the second. A forward walk over the function
    /// says, for every allocator slot, which registers hold its current
    /// value on every path in (one of them loaded or stored it, and nothing
    /// has written it since): a reload of it from there becomes a move from
    /// one of them, or nothing; a read of it folded into an instruction
    /// reads the register. Allocator slots only: nothing takes their
    /// address. Nothing is held into a landing pad (an unwind restores
    /// registers to nothing) nor past a call (the collector may come to move
    /// what a slot holds, and the stack map names only the slot).
    /// </summary>
    private static void ForwardSpillLoadsAcrossBlocks(MFunction m)
    {
        int nb = m.Blocks.Count;
        if (nb == 0) return;
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++) index[m.Blocks[b]] = b;
        List<int>[] preds = new List<int>[nb];
        for (int b = 0; b < nb; b++) preds[b] = new List<int>();
        List<MBlock> succ = new();
        bool any = false;
        for (int b = 0; b < nb; b++)
        {
            m.SuccessorsInto(b, succ);
            foreach (MBlock t in succ) if (index.TryGetValue(t, out int ti)) preds[ti].Add(b);
            foreach (MInstr i in m.Blocks[b].Instrs) any |= i.Operands.Any(o => o is MMem mm && PrivateSpill(mm));
        }
        if (!any) return;

        // WHAT EACH EDGE CARRIES, null until a path reaches it: slot -> the
        // registers holding its value, as it was AT THE JUMP -- a block may
        // jump from its middle (LoopRotate's copied test, a short-circuit's
        // test) with registers the rest of it writes -- or at the end, for
        // the way it falls through.
        Dictionary<(int From, int To), Dictionary<int, int>> edges = new();
        Dictionary<int, int>? Entry(int b)
        {
            if (b == 0 || m.Blocks[b].Source?.IsLandingPad == true) return new();
            Dictionary<int, int>? met = null;
            foreach (int p in preds[b])
            {
                if (!edges.TryGetValue((p, b), out Dictionary<int, int>? there)) continue;
                if (met is null) { met = new(there); continue; }
                foreach (int slot in met.Keys.ToList())
                {
                    int both = there.TryGetValue(slot, out int regs) ? met[slot] & regs : 0;
                    if (both == 0) met.Remove(slot); else met[slot] = both;
                }
            }
            return met;
        }
        // Each edge's state, met with what an earlier walk gave it: a block's
        // ways out shrink as more of its ways in are known.
        bool Carry(int from, int to, Dictionary<int, int> state)
        {
            if (!edges.TryGetValue((from, to), out Dictionary<int, int>? was)) { edges[(from, to)] = new(state); return true; }
            if (was.Count == state.Count && was.All(kv => state.TryGetValue(kv.Key, out int v) && v == kv.Value)) return false;
            edges[(from, to)] = new(state);
            return true;
        }
        // ONE EDGE, SEVERAL WAYS ALONG IT in a walk of its block -- a jump to
        // the block it then falls into, a table naming a target twice: what
        // they carry is met, and the edge given that once. Each replacing
        // the last, the edge took the two states in turn, every pass a
        // change, and the walk never ended.
        Dictionary<int, Dictionary<int, int>> along = new();
        void Along(int to, Dictionary<int, int> state)
        {
            if (!along.TryGetValue(to, out Dictionary<int, int>? met)) { along[to] = new(state); return; }
            foreach (int slot in met.Keys.ToList())
            {
                int both = state.TryGetValue(slot, out int regs) ? met[slot] & regs : 0;
                if (both == 0) met.Remove(slot); else met[slot] = both;
            }
        }
        void Walk(int b, Dictionary<int, int> state, bool rewrite, ref bool changed)
        {
            along.Clear();
            List<MInstr> instrs = m.Blocks[b].Instrs;
            for (int k = 0; k < instrs.Count; k++)
            {
                MInstr i = instrs[k];
                if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands[0] is MLabel { Target: var t } && index.TryGetValue(t, out int ti))
                    Along(ti, state);
                else if (i.Op == MOp.JmpTable && i.Table is { } table)
                    foreach (MBlock each in table) if (index.TryGetValue(each, out int ei)) Along(ei, state);
                MInstr? now = StepHeld(i, state, rewrite);
                if (!rewrite) continue;
                if (now is null) { instrs.RemoveAt(k); k--; }
                else instrs[k] = now;
            }
            if (!m.Blocks[b].EndsUnconditionally && b + 1 < nb) Along(b + 1, state);
            foreach ((int to, Dictionary<int, int> met) in along) changed |= Carry(b, to, met);
        }
        bool again = true;
        int passes = 0;
        while (again)
        {
            // What each edge carries only shrinks, so this settles; a walk
            // that has not is a fault, said with its function, not a spin.
            if (++passes > 1000 + 16 * nb) throw new InvalidOperationException(m.Source.Name + ": forwarding spill loads did not settle");
            again = false;
            for (int b = 0; b < nb; b++)
                if (Entry(b) is { } state) Walk(b, state, rewrite: false, ref again);
        }
        // The edges as the walk left them: the rewrite reads them only.
        bool ignored = false;
        Dictionary<int, int>?[] entries = new Dictionary<int, int>?[nb];
        for (int b = 0; b < nb; b++) entries[b] = Entry(b);
        for (int b = 0; b < nb; b++)
            if (entries[b] is { } state) Walk(b, state, rewrite: true, ref ignored);
    }

    /// <summary>
    /// One instruction of ForwardSpillLoadsAcrossBlocks: rewritten from what
    /// `state` says is held (null when it does nothing), then its effect on
    /// `state`.
    /// </summary>
    private static MInstr? StepHeld(MInstr i, Dictionary<int, int> state, bool rewrite)
    {
        int Held(MMem slot) => state.TryGetValue(slot.Disp, out int regs) ? regs : 0;
        static int Lowest(int regs) { int r = 0; while ((regs & (1 << r)) == 0) r++; return r; }
        if (rewrite && !i.Lock && i.Width == 4)
        {
            // A reload of a value a register holds.
            if (IsMov(i) && i.Operands[0] is MReg dest && dest.Id < 8 && i.Operands[1] is MMem from && PrivateSpill(from) && Held(from) is var held && held != 0)
            {
                if ((held & (1 << dest.Id)) != 0) return null;
                i = new MInstr(MOp.Mov, MReg.Of(dest.Id), MReg.Of(Lowest(held))) { Line = i.Line };
            }
            // A read folded into an instruction that takes a register as well.
            else if (i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Imul or MOp.Push)
            {
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not MMem mem || !PrivateSpill(mem) || Roles.Of(i.Op, k) != Roles.Role.Use || Held(mem) is not (not 0 and var h)) continue;
                    MInstr copy = new(i.Op) { Width = i.Width, Cond = i.Cond, Lock = i.Lock, Table = i.Table, CallReloc = i.CallReloc, Line = i.Line, Native = i.Native, Probed = i.Probed };
                    for (int j = 0; j < i.Operands.Count; j++) copy.Operands.Add(j == k ? MReg.Of(Lowest(h)) : i.Operands[j]);
                    i = copy;
                    break;
                }
            }
        }
        // Its effect: a call or anything writing EBP lets go of everything.
        if (i.Op is MOp.Call or MOp.CallInd) { state.Clear(); return i; }
        Masks(i, out _, out long written);
        if ((written & Bit((int)Gpr.Ebp)) != 0) { state.Clear(); return i; }
        // A write to an allocator slot: what held it holds it no more.
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is not MMem mem) continue;
            if ((Roles.Of(i.Op, k) & Roles.Role.Def) == 0) continue;
            if (!PrivateSpill(mem)) continue;
            int lo = mem.Disp, hi = mem.Disp + Math.Max(i.Width, 1);
            foreach (int slot in state.Keys.ToList()) if (slot < hi && lo < slot + 4) state.Remove(slot);
        }
        // Registers written hold nothing they held.
        int regsWritten = (int)(written & 0xFF);
        if (regsWritten != 0)
            foreach (int slot in state.Keys.ToList())
            {
                int left = state[slot] & ~regsWritten;
                if (left == 0) state.Remove(slot); else state[slot] = left;
            }
        // Then what it leaves held: a load or a store of a whole slot by a move.
        if (IsMov(i) && !i.Lock)
        {
            if (i.Operands[0] is MReg to && to.Id < 8 && i.Operands[1] is MMem src && PrivateSpill(src))
                state[src.Disp] = (state.TryGetValue(src.Disp, out int h1) ? h1 : 0) | (1 << to.Id);
            else if (i.Operands[0] is MMem dst && PrivateSpill(dst) && i.Operands[1] is MReg value && value.Id < 8)
                state[dst.Disp] = 1 << value.Id;
            // A move between registers copies whatever its source held.
            else if (i.Operands[0] is MReg d2 && d2.Id < 8 && i.Operands[1] is MReg s2 && s2.Id < 8)
                foreach (int slot in state.Keys.ToList())
                    if ((state[slot] & (1 << s2.Id)) != 0) state[slot] |= 1 << d2.Id;
        }
        return i;
    }

    private static bool SpillTransparent(MOp op) => op is MOp.Mov or MOp.Movsx or MOp.Movzx
        or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor
        or MOp.Cmp or MOp.Test or MOp.Imul or MOp.Imul3 or MOp.Mul
        or MOp.Neg or MOp.Not or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd
        or MOp.Cdq or MOp.Setcc or MOp.Cmov or MOp.Nop;

    /// <summary>
    /// A side-effect-free instruction whose result register is overwritten
    /// before anything reads it, within the block, does nothing. Nothing is
    /// assumed about registers at the block's end, so a value flowing out
    /// of the block is never touched.
    /// </summary>
    /// <summary>
    /// THE SAME CONSTANT STORED AGAIN AND AGAIN -- a struct zeroed a word at a
    /// time, three `mov dword [ebp-2016], 0` of ten bytes each -- is put in a
    /// register nothing needs there, once, and each store writes the register:
    /// four bytes less on every store, and the register's load becomes `xor`
    /// where the flags allow (ZeroWithXor, next). Only a run of such stores
    /// with nothing between, and only a register dead before it that no store
    /// addresses through.
    /// </summary>
    /// <summary>
    /// The registers a rule may take for itself after allocation: the three
    /// the caller does not expect kept, and the callee-saved ones the prologue
    /// already saves. Any other callee-saved register is the caller's value,
    /// and writing it destroys that value -- `xor ebx, ebx` in String.FromChar,
    /// which saved nothing, took the closure its caller held in EBX.
    /// </summary>
    private static int Usable(MFunction m)
    {
        int usable = 1 << (int)Gpr.Eax | 1 << (int)Gpr.Ecx | 1 << (int)Gpr.Edx;
        foreach (Gpr g in m.SavedRegs) usable |= 1 << (int)g;
        return usable;
    }

    private static void RepeatedStores(List<MInstr> instrs, int liveOut, int usable, Func<MInstr, int> jumped)
    {
        int[] deadBefore = new int[instrs.Count];
        int dead = ~liveOut & 0xFF & ~(1 << (int)Gpr.Esp) & ~(1 << (int)Gpr.Ebp);
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            dead &= ~jumped(i);
            if (!Understood(i)) dead = 0;
            else
            {
                foreach ((MReg r, bool isDef) in Regs(i)) if (isDef && r.IsPhys) dead |= 1 << r.Id;
                foreach (Gpr g in Roles.ImplicitDefs(i)) dead |= 1 << (int)g;
                foreach ((MReg r, bool isDef) in Regs(i)) if (!isDef && r.IsPhys) dead &= ~(1 << r.Id);
                foreach (Gpr g in Roles.ImplicitUses(i)) dead &= ~(1 << (int)g);
                dead &= ~(1 << (int)Gpr.Esp) & ~(1 << (int)Gpr.Ebp);
            }
            deadBefore[k] = dead;
        }
        static bool ConstantStore(MInstr i, out long value)
        {
            value = 0;
            if (i.Op != MOp.Mov || i.Width != 4 || i.Operands.Count != 2 || i.Operands[0] is not MMem
                || i.Operands[1] is not MImm { IsPlain: true } imm) return false;
            value = imm.Value;
            return true;
        }
        // Every run first, against the unchanged indices; then each rewritten,
        // the last first, so an inserted load moves nothing not yet done.
        List<(int Start, int End, long Value)> runs = new();
        for (int k = 0; k < instrs.Count; k++)
        {
            if (!ConstantStore(instrs[k], out long value)) continue;
            int end = k + 1;
            while (end < instrs.Count && ConstantStore(instrs[end], out long next) && next == value) end++;
            if (end - k >= 2) runs.Add((k, end, value));
            k = end - 1;
        }
        for (int r = runs.Count - 1; r >= 0; r--)
        {
            (int k, int end, long value) = runs[r];
            int free = deadBefore[k] & usable;
            for (int j = k; j < end; j++)
                if (instrs[j].Operands[0] is MMem mem)
                {
                    if (mem.Base is { IsPhys: true } b) free &= ~(1 << b.Id);
                    if (mem.Index is { IsPhys: true } x) free &= ~(1 << x.Id);
                }
            if (free == 0) continue;
            Gpr reg = (Gpr)System.Numerics.BitOperations.TrailingZeroCount(free);
            for (int j = k; j < end; j++) instrs[j].Operands[1] = MReg.Of(reg);
            MInstr load = new(MOp.Mov, MReg.Of(reg), new MImm(value)) { Line = instrs[k].Line };
            instrs.Insert(k, load);
        }
    }

    /// <summary>
    /// A CALL'S ARGUMENTS ARE POPPED ONCE FOR SEVERAL CALLS. Each call is
    /// followed by `add esp, n` to drop what it was pushed; with EBP framing
    /// nothing in between needs ESP exact -- a push, another call -- so the
    /// drops of a run of calls are one drop, at the last of them. Each of the
    /// others was three bytes. The last stays where it was, so the flags it
    /// set and the stack depth after it are what they were; anything that
    /// reads or writes ESP (or an operand based on it, or that this does not
    /// understand) ends a run.
    /// </summary>
    private static void MergePops(List<MInstr> instrs)
    {
        static bool Pop(MInstr i, out long bytes)
        {
            bytes = 0;
            if (i.Op != MOp.Add || i.Width != 4 || i.Operands.Count != 2 || i.Operands[0] is not MReg { IsPhys: true } r
                || r.Phys != Gpr.Esp || i.Operands[1] is not MImm { IsPlain: true } imm) return false;
            bytes = imm.Value;
            return true;
        }
        static bool Neutral(MInstr i)
        {
            // Pushes and calls move ESP relative to itself, which is all they need.
            if (i.Op is MOp.Push or MOp.Call or MOp.CallInd) return i.Operands.All(o => !Names(o));
            if (!Understood(i) || i.Op is MOp.Pop or MOp.Prologue or MOp.Epilogue or MOp.Ret) return false;
            if (i.Operands.Any(Names)) return false;
            return !Roles.ImplicitUses(i).Contains(Gpr.Esp) && !Roles.ImplicitDefs(i).Contains(Gpr.Esp);
        }
        static bool Names(MOperand o) => o is MReg { IsPhys: true, Phys: Gpr.Esp }
            || o is MMem m && (m.Base is { IsPhys: true, Phys: Gpr.Esp } || m.Index is { IsPhys: true, Phys: Gpr.Esp });
        int pending = -1;       // the index of the run's last drop so far
        long total = 0;
        for (int k = 0; k < instrs.Count; k++)
        {
            MInstr i = instrs[k];
            if (Pop(i, out long bytes))
            {
                if (pending >= 0)
                {
                    instrs.RemoveAt(pending);
                    k--;
                    total += bytes;
                }
                else total = bytes;
                instrs[k] = new MInstr(MOp.Add, MReg.Of(Gpr.Esp), new MImm(total)) { Line = i.Line };
                pending = k;
                continue;
            }
            if (!Neutral(i)) pending = -1;
        }
    }

    private static void DeadDefs(List<MInstr> instrs, int liveOut, Func<MInstr, int> jumped)
    {
        HashSet<int> dead = new();
        for (int r = 0; r < 8; r++)
        {
            if ((liveOut & (1 << r)) == 0 && r != (int)Gpr.Esp && r != (int)Gpr.Ebp)
            {
                dead.Add(r);
            }
        }
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            int needed = jumped(i);
            if (needed != 0) for (int r = 0; r < 8; r++) if ((needed & (1 << r)) != 0) dead.Remove(r);
            if (Removable(i) && i.Operands[0] is MReg d && dead.Contains(d.Id))
            {
                instrs.RemoveAt(k);
                continue;
            }
            int copyAt = FindCopy(instrs, k, dead);
            if (copyAt >= 0)
            {
                // `mov A, B; ...; use A` with A dead afterwards is `...; use B`.
                instrs.RemoveAt(copyAt);
                k--;
                i = instrs[k];
            }
            // An instruction that is not understood makes every register live again.
            if (!Understood(i))
            {
                dead.Clear();
                continue;
            }
            // Writes first, then reads: a register both read and written
            // here (an indirect call through EAX, which the call then
            // clobbers) is live before this instruction.
            foreach ((MReg r, bool isDef) in Regs(i))
            {
                if (isDef)
                {
                    dead.Add(r.Id);
                }
            }
            foreach (Gpr g in Roles.ImplicitDefs(i))
            {
                dead.Add((int)g);
            }
            foreach ((MReg r, bool isDef) in Regs(i))
            {
                if (!isDef)
                {
                    dead.Remove(r.Id);
                }
            }
            foreach (Gpr g in Roles.ImplicitUses(i))
            {
                dead.Remove((int)g);
            }
        }
    }

    /// <summary>
    /// Looks a few instructions back from `k` for a copy `mov A, B` that
    /// ForwardCopy can fold into instruction k, with nothing in between
    /// touching A or B. Returns its index, or -1; on success instruction k
    /// has already been rewritten.
    /// </summary>
    private static int FindCopy(List<MInstr> instrs, int k, HashSet<int> deadAfter)
    {
        // A mask, not a set: one for every call was a set's worth of garbage
        // for every instruction the pass looked at.
        long touched = 0;
        for (int j = k - 1; j >= 0 && j >= k - 6; j--)
        {
            MInstr c = instrs[j];
            if (IsMov(c) && c.Operands[0] is MReg a && c.Operands[1] is MReg b
                && (touched & Bit(a.Id)) == 0 && (touched & Bit(b.Id)) == 0)
            {
                return ForwardCopy(c, instrs[k], deadAfter) ? j : -1;
            }
            // NOT ACROSS A JUMP: where it goes may read A, which the copy
            // would no longer have written (a block that leaves from its
            // middle, LoopRotate's).
            if (!Understood(c) || c.Op is MOp.Jcc or MOp.Jmp or MOp.JmpTable)
            {
                return -1;
            }
            Masks(c, out long reads, out long writes);
            touched |= reads | writes;
        }
        return -1;
    }

    /// <summary>
    /// If `copy` is `mov A, B` and `user` only reads A, and A is dead after
    /// `user`, rewrite `user` to read B instead and report that the copy
    /// can go. B must not be written by `user` before it is read, which
    /// holds for every instruction here since reads precede writes, and A
    /// must not be one the instruction reads implicitly by name.
    /// </summary>
    private static bool ForwardCopy(MInstr copy, MInstr user, HashSet<int> deadAfter)
    {
        if (!IsMov(copy) || copy.Operands[0] is not MReg a || copy.Operands[1] is not MReg b || a.Id == b.Id
            || !deadAfter.Contains(a.Id) || !Understood(user) || user.Op is MOp.Xchg or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd)
        {
            return false;
        }
        bool reads = false;
        for (int k = 0; k < user.Operands.Count; k++)
        {
            switch (user.Operands[k])
            {
                case MReg r when r.Id == a.Id:
                    if (Roles.Of(user.Op, k) != Roles.Role.Use || FixedByEncoding(user.Op, k))
                    {
                        return false;
                    }
                    reads = true;
                    break;
                case MMem m when m.Base?.Id == a.Id || m.Index?.Id == a.Id:
                    reads = true;
                    break;
            }
        }
        if (!reads || Roles.ImplicitUses(user).Any(g => (int)g == a.Id))
        {
            return false;
        }
        // Width-1 operands need a register with a byte form.
        if (user.Width == 1 && b.Id > (int)Gpr.Ebx)
        {
            return false;
        }
        for (int k = 0; k < user.Operands.Count; k++)
        {
            switch (user.Operands[k])
            {
                case MReg r when r.Id == a.Id:
                    user.Operands[k] = MReg.Of(b.Id);
                    break;
                case MMem m when m.Base?.Id == a.Id || m.Index?.Id == a.Id:
                    // Everything about the operand but the register survives.
                    // Losing Reloc here turned a `[got + sym@GOTOFF]` back
                    // into an absolute address, which a shared object can only
                    // honour with a text relocation -- and the whole point of
                    // position-independent code is not needing one.
                    user.Operands[k] = new MMem(m.Base?.Id == a.Id ? MReg.Of(b.Id) : m.Base, m.Disp)
                    {
                        Index = m.Index?.Id == a.Id ? MReg.Of(b.Id) : m.Index,
                        Scale = m.Scale,
                        Symbol = m.Symbol,
                        Reloc = m.Reloc,
                        Label = m.Label,
                        IsSpill = m.IsSpill,
                    };
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// Whether an operand names a register only for the allocator's
    /// benefit: the encoding implies it and ignores what is written there.
    /// A shift count is always CL (`D3 /n`, `0F A5`), so renaming it would
    /// leave the instruction shifting by whatever CL happened to hold.
    /// </summary>
    private static bool FixedByEncoding(MOp op, int operand) => op switch
    {
        MOp.Shl or MOp.Shr or MOp.Sar => operand == 1,
        MOp.Shld or MOp.Shrd => operand == 2,
        // A stub's registers are its calling convention: the slot in EAX,
        // the value in EDX (X86Backend.CardStub, BarrierStub).
        MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd => operand > 0,
        _ => false,
    };

    /// <summary>Writes only its first operand, a register, and sets no flags anything relies on.</summary>
    /// <remarks>
    /// Never a write to ESP or EBP: those change the machine state that
    /// frame addresses, the unwinder and the borrowed-EBP syscall rely on,
    /// whatever the liveness sets say.
    /// </remarks>
    /// A LOAD THROUGH A REGISTER STAYS, read or not: its address may be a
    /// null reference's, and the fault it takes there is the program's
    /// NullReferenceException (Runtime.NullFault), as the IR's own dead-code
    /// rule has it (IrInfo.IsPure). A frame's, a spill's or a static's is
    /// memory the program owns, and goes with the register it fills.
    private static bool Removable(MInstr i)
        => i.Op is MOp.Mov or MOp.Lea or MOp.Movzx or MOp.Movsx or MOp.Cmov
           && i.Operands[0] is MReg { Id: not ((int)Gpr.Esp or (int)Gpr.Ebp) }
           && (i.Op == MOp.Lea || i.Operands.Count < 2 || i.Operands[1] is not MMem m || CannotFault(m));

    /// <summary>
    /// Whether reading the operand can never fault: the frame, or a symbol's
    /// static storage. A read through any other register, or of a bare
    /// address, is kept though nothing reads what it loads -- it may be the
    /// NullReferenceException a call on null must raise, and a receiver the
    /// optimiser had proved null was a load of address 0 with no base that
    /// went as dead: `((Thing)null).ToString()` answered "" (test 1284).
    /// </summary>
    private static bool CannotFault(MMem m)
        => m.Base is { Id: (int)Gpr.Esp or (int)Gpr.Ebp }
           || m.Base is null && m.Index is null && (m.Symbol is not null || m.Label is not null);

    /// <summary>Whether the operand roles fully describe what the instruction reads and writes.</summary>
    private static bool Understood(MInstr i) => i.Op switch
    {
        // An unwind jump carries the exception in EAX by convention, unnamed.
        MOp.JmpInd or MOp.Ret or MOp.Prologue or MOp.Int or MOp.Int3 => false,
        _ => true,
    };

    private static IEnumerable<(MReg Reg, bool IsDef)> Regs(MInstr i)
    {
        if (i.Op == MOp.Xor && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id)
        {
            // Zeroing a register reads nothing that matters.
            yield return (x, true);
            yield break;
        }
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r:
                {
                    Roles.Role role = Roles.Of(i.Op, k);
                    if ((role & Roles.Role.Use) != 0)
                    {
                        yield return (r, false);
                    }
                    if ((role & Roles.Role.Def) != 0)
                    {
                        yield return (r, true);
                    }
                    break;
                }
                case MMem m:
                    if (m.Base is not null)
                    {
                        yield return (m.Base, false);
                    }
                    if (m.Index is not null)
                    {
                        yield return (m.Index, false);
                    }
                    break;
            }
        }
    }

    // ---- mov r, 0 -> xor r, r ---------------------------------------------------------

    // A conditional move reads them as a jump does: `mov t, 0` between the
    // compare and the cmov it feeds (IfConvert) is never an `xor`.
    private static bool ReadsFlags(MInstr i) => i.Op is MOp.Jcc or MOp.Setcc or MOp.Cmov or MOp.Adc or MOp.Sbb;

    // ---- test after an instruction that set the flags --------------------------------

    /// <summary>
    /// `and r, x ; test r, r ; je` tests twice: the and already set ZF and SF
    /// from r, and cleared CF and OF as the test does, so the test changes no
    /// flag and goes. After add, sub, neg or a shift by a constant only ZF, SF
    /// and PF are the test's (CF and OF are the arithmetic's), so the test goes
    /// only where every reader before the next flag write asks equal, sign or
    /// parity. An allocation's `if ((x &amp; Mask) == 0)`, a countdown's
    /// `if (--n != 0)`: one instruction each. The readers must be in the
    /// block, as the selector always leaves them; anything not known to leave
    /// the flags alone keeps the test.
    /// </summary>
    private static void RedundantTests(List<MInstr> instrs)
    {
        for (int k = 1; k < instrs.Count; k++)
        {
            MInstr test = instrs[k], before = instrs[k - 1];
            if (test.Op != MOp.Test || test.Width != 4 || test.Operands.Count != 2
                || test.Operands[0] is not MReg r || test.Operands[1] is not MReg r2 || r.Id != r2.Id
                || before.Width != 4 || before.Lock || before.Operands.Count < 1 || before.Operands[0] is not MReg written || written.Id != r.Id)
                continue;
            bool logical = before.Op is MOp.And or MOp.Or or MOp.Xor && before.Operands.Count == 2;
            bool arithmetic = before.Op is MOp.Add or MOp.Sub && before.Operands.Count == 2
                || before.Op == MOp.Neg && before.Operands.Count == 1
                || before.Op is MOp.Shl or MOp.Shr or MOp.Sar && before.Operands.Count == 2 && before.Operands[1] is MImm { Value: >= 1 and <= 31 };
            if (!logical && !arithmetic) continue;
            bool safe = true;
            for (int j = k + 1; j < instrs.Count && safe; j++)
            {
                MInstr after = instrs[j];
                if (after.Op is MOp.Jcc or MOp.Setcc or MOp.Cmov)
                {
                    if (!logical && after.Cond is not (Cond.E or Cond.Ne or Cond.S or Cond.Ns or Cond.P or Cond.Np)) safe = false;
                    continue;
                }
                if (after.Op is MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Nop or MOp.Jmp) continue;
                if (WritesFlags(after) && !ReadsFlags(after)) break;
                safe = false;
            }
            if (safe)
            {
                instrs.RemoveAt(k);
                k--;
            }
        }
    }

    private static bool WritesFlags(MInstr i) => i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or
        or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Imul or MOp.Imul3 or MOp.Mul or MOp.ImulWide or MOp.Div or MOp.Idiv or MOp.Neg
        or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd or MOp.Xadd or MOp.Cmpxchg or MOp.LockOrEsp or MOp.Sahf
        or MOp.Fucomipp;

    /// <summary>
    /// Zeroing a register with xor is two bytes against five, but it
    /// clobbers the flags, so only where nothing downstream in the block
    /// reads them before something else sets them. The selector never
    /// carries flags across a block boundary.
    /// </summary>
    private static void ZeroWithXor(List<MInstr> instrs)
    {
        bool flagsLive = false;
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            if (ReadsFlags(i))
            {
                flagsLive = true;
            }
            else if (WritesFlags(i))
            {
                flagsLive = false;
            }
            else if (!flagsLive && IsMov(i) && i.Operands[0] is MReg r && i.Operands[1] is MImm { IsPlain: true, Value: 0 })
            {
                instrs[k] = new MInstr(MOp.Xor, MReg.Of(r.Id), MReg.Of(r.Id));
            }
        }
    }

    // ---- load, operate, store back: one read-modify-write ----------------------------

    /// <summary>
    /// `mov r, [m] ; op r, x ; mov [m], r` with r dead after the store, x a
    /// register other than r or a constant, and m not addressed through r, is
    /// `op [m], x`: the same memory, the same flags (add, sub, and, or and xor
    /// set them from the result alike), one instruction and a register
    /// freed. Three clocks on a 486 and a Pentium either way; on a K6 one long
    /// decode (load, ALU, store) against three short ones (AMD 21924's
    /// table: ADD mem, reg is long), about a clock and a half of decoding
    /// either way. A local or a spilled count updated in a loop (`s += x`
    /// with s in its slot) was the three.
    /// </summary>
    private static void ReadModifyWrite(List<MInstr> instrs, int liveOut, Func<MInstr, int> jumped)
    {
        const int Frame = (1 << (int)Gpr.Esp) | (1 << (int)Gpr.Ebp);
        int dead = ~liveOut & 0xFF & ~Frame;
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            dead &= ~jumped(i);
            // dead says what holds after instruction k.
            if (k >= 2 && IsMov(i) && i.Operands[0] is MMem m && i.Operands[1] is MReg { IsPhys: true } r
                && r.Id < 8 && (dead & (1 << r.Id)) != 0 && m.Base?.Id != r.Id && m.Index?.Id != r.Id)
            {
                MInstr op = instrs[k - 1], load = instrs[k - 2];
                if (op.Op is MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor && op.Width == 4 && !op.Lock
                    && op.Operands.Count == 2 && op.Operands[0] is MReg o0 && o0.Id == r.Id
                    && (op.Operands[1] is MReg x && x.Id != r.Id || op.Operands[1] is MImm { IsPlain: true })
                    && IsMov(load) && load.Operands[0] is MReg l0 && l0.Id == r.Id
                    && load.Operands[1] is MMem lm && SameMem(lm, m))
                {
                    MInstr rmw = new(op.Op, m, op.Operands[1]) { Width = 4, Line = op.Line };
                    instrs.RemoveRange(k - 2, 3);
                    instrs.Insert(k - 2, rmw);
                    k -= 2;
                    i = rmw;
                }
            }
            if (!Understood(i))
            {
                dead = 0;
                continue;
            }
            foreach ((MReg rr, bool isDef) in Regs(i)) if (isDef && rr.Id < 8) dead |= 1 << rr.Id;
            foreach (Gpr g in Roles.ImplicitDefs(i)) dead |= 1 << (int)g;
            foreach ((MReg rr, bool isDef) in Regs(i)) if (!isDef && rr.Id < 8) dead &= ~(1 << rr.Id);
            foreach (Gpr g in Roles.ImplicitUses(i)) dead &= ~(1 << (int)g);
            dead &= ~Frame;
        }
    }

    // ---- add or subtract one: inc, dec ------------------------------------------------

    /// <summary>
    /// `add x, 1` and `sub x, 1` (and by -1) are INC and DEC where nothing
    /// after reads CF, which those leave alone: one byte for a register
    /// against three, and the same clock on the 486,
    /// the Pentium (in either pipe) and the K6 (Intel 242816, "Short
    /// Opcodes"). A reader of CF is a jump, set or move on below, above or
    /// their negations, or an ADC or SBB; anything else not known to leave
    /// the flags alone keeps the add. Last of all: nothing after it here
    /// reads the flags of an INC or DEC. A byte saved on memory (FF /0
    /// against 83 /0 ib), two on a register.
    /// </summary>
    private static void IncDec(List<MInstr> instrs)
    {
        for (int k = 0; k < instrs.Count; k++)
        {
            MInstr i = instrs[k];
            if (i.Op is not (MOp.Add or MOp.Sub) || i.Width != 4 || i.Lock || i.Operands.Count != 2
                || i.Operands[1] is not MImm { IsPlain: true, Value: 1 or -1 } one
                || i.Operands[0] is not (MReg or MMem))
            {
                continue;
            }
            bool safe = true;
            for (int j = k + 1; j < instrs.Count; j++)
            {
                MInstr after = instrs[j];
                if (after.Op is MOp.Jcc or MOp.Setcc or MOp.Cmov)
                {
                    if (after.Cond is Cond.B or Cond.Ae or Cond.Be or Cond.A) { safe = false; break; }
                    continue;
                }
                if (after.Op is MOp.Adc or MOp.Sbb) { safe = false; break; }
                if (after.Op is MOp.Call or MOp.CallInd or MOp.Ret or MOp.Epilogue) break;
                if (WritesFlags(after)) break;
                if (after.Op is MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Push or MOp.Pop or MOp.Nop or MOp.Jmp) continue;
                safe = false;
                break;
            }
            if (!safe) continue;
            bool up = (i.Op == MOp.Add) == (one.Value == 1);
            instrs[k] = new MInstr(up ? MOp.Inc : MOp.Dec, i.Operands[0]) { Width = 4, Line = i.Line };
        }
    }

    // ---- the instruction the selected processor runs best ---------------------------

    /// <summary>
    /// Each choice where the processors differ, made for the one --cpu names
    /// (CpuModel), with the registers and flags known dead after each
    /// instruction, as DeadDefs knows them:
    /// - every processor: `shl r, 1` is `add r, r` -- the same flags but AF;
    ///   the Pentium pairs an add in either pipe and a shift only in U, the
    ///   K6 has two adders and one shifter (AMD 21924, "Use ADD reg, reg
    ///   instead of SHL reg, 1"), the 486 adds in one clock and shifts in two.
    /// - the 486 and the Pentium: a byte zero-extended is `xor r, r ; mov
    ///   r8, src` (or `and r, 0FFh` from its own low byte) where the flags are
    ///   dead -- MOVZX is three clocks there, unpaired, behind a 0Fh prefix
    ///   the P54C decodes for a clock more (Intel 242816, "Zero-Extension of
    ///   Short"). The K6 keeps MOVZX, which it decodes short and which makes no
    ///   superset dependency (AMD 21924).
    /// - the 486 and the Pentium: `push [m]` is `mov r, [m] ; push r` with a
    ///   register dead there that the function may use (Usable) -- four
    ///   clocks against two on the 486, two unpaired against two pairable on
    ///   the Pentium (Intel 242816, "Push mem"). The K6 keeps PUSH [mem] (AMD
    ///   21924), and so does the 386, where the pair is a clock slower.
    /// - the Pentium and the K6: CDQ is `mov edx, eax ; sar edx, 31` where
    ///   the flags are dead -- unpaired on the Pentium (Agner Fog, example
    ///   4.12), vector decoded on the K6 (AMD 21924, "Balance the use of CWD,
    ///   CBW, CDQ"); the 486 takes three clocks either way.
    /// What an instruction becomes is walked in its place, last first, so the
    /// registers a load put before it reads are live above it.
    /// </summary>
    private static void ChooseForCpu(List<MInstr> instrs, int liveOut, int usable, Func<MInstr, int> jumped)
    {
        bool p5 = CpuModel.P5, scalar = CpuModel.Scalar, k6 = CpuModel.K6, i486 = CpuModel.I486;
        const int Frame = (1 << (int)Gpr.Esp) | (1 << (int)Gpr.Ebp);
        int dead = ~liveOut & 0xFF & ~Frame;
        bool flagsLive = false;
        // What holds above instruction i, from what holds below it.
        void Step(MInstr i)
        {
            if (!Understood(i))
            {
                dead = 0;
                flagsLive = true;
                return;
            }
            if (ReadsFlags(i)) flagsLive = true;
            else if (WritesFlags(i) || i.Op is MOp.Call or MOp.CallInd) flagsLive = false;
            foreach ((MReg r, bool isDef) in Regs(i)) if (isDef && r.Id < 8) dead |= 1 << r.Id;
            foreach (Gpr g in Roles.ImplicitDefs(i)) dead |= 1 << (int)g;
            foreach ((MReg r, bool isDef) in Regs(i)) if (!isDef && r.Id < 8) dead &= ~(1 << r.Id);
            foreach (Gpr g in Roles.ImplicitUses(i)) dead &= ~(1 << (int)g);
            dead &= ~Frame;
        }
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            dead &= ~jumped(i);
            // dead and flagsLive say what holds AFTER instruction k.
            MInstr[]? with = null;
            if (i.Op == MOp.Shl && i.Width == 4 && i.Operands.Count == 2 && i.Operands[0] is MReg sr && i.Operands[1] is MImm { IsPlain: true, Value: 1 })
            {
                with = [new MInstr(MOp.Add, MReg.Of(sr.Id), MReg.Of(sr.Id)) { Line = i.Line }];
            }
            else if ((p5 || scalar) && !flagsLive && i.Op == MOp.Movzx && i.Width == 1 && i.Operands.Count == 2
                && i.Operands[0] is MReg { IsPhys: true } zd && zd.Id < 4)
            {
                if (i.Operands[1] is MReg own && own.Id == zd.Id)
                {
                    with = [new MInstr(MOp.And, MReg.Of(zd.Id), new MImm(0xFF)) { Line = i.Line }];
                }
                else if (i.Operands[1] is MReg { IsPhys: true } from && from.Id < 4
                    || i.Operands[1] is MMem zm && zm.Base?.Id != zd.Id && zm.Index?.Id != zd.Id)
                {
                    with = [new MInstr(MOp.Xor, MReg.Of(zd.Id), MReg.Of(zd.Id)) { Line = i.Line },
                            new MInstr(MOp.Mov, MReg.Of(zd.Id), i.Operands[1]) { Width = 1, Line = i.Line }];
                }
            }
            else if ((p5 || i486) && i.Op == MOp.Push && i.Width == 4 && i.Operands.Count == 1 && i.Operands[0] is MMem pm
                && (dead & usable) != 0)
            {
                int free = dead & usable, scratch = 0;
                while ((free & (1 << scratch)) == 0) scratch++;
                with = [new MInstr(MOp.Mov, MReg.Of(scratch), pm) { Line = i.Line },
                        new MInstr(MOp.Push, MReg.Of(scratch)) { Line = i.Line }];
            }
            else if ((k6 || p5) && !flagsLive && i.Op == MOp.Cdq)
            {
                with = [new MInstr(MOp.Mov, MReg.Of((int)Gpr.Edx), MReg.Of((int)Gpr.Eax)) { Line = i.Line },
                        new MInstr(MOp.Sar, MReg.Of((int)Gpr.Edx), new MImm(31)) { Line = i.Line }];
            }
            if (with is null)
            {
                Step(i);
                continue;
            }
            instrs.RemoveAt(k);
            instrs.InsertRange(k, with);
            for (int j = with.Length - 1; j >= 0; j--) Step(with[j]);
        }
    }

    // ---- jcc over jmp ---------------------------------------------------------------

    /// <summary>
    /// `jcc T; jmp F` where T is the block laid out next is one jump too
    /// many: `j!cc F` and fall through.
    /// </summary>
    private static void InvertJumpAroundJump(List<MInstr> instrs, MBlock? next)
    {
        int n = instrs.Count;
        if (n < 2 || instrs[n - 1].Op != MOp.Jmp || instrs[n - 2].Op != MOp.Jcc)
        {
            return;
        }
        MBlock t = ((MLabel)instrs[n - 2].Operands[0]).Target;
        MBlock f = ((MLabel)instrs[n - 1].Operands[0]).Target;
        if (!ReferenceEquals(t, next))
        {
            return;
        }
        instrs[n - 2] = new MInstr(MOp.Jcc, new MLabel(f)) { Cond = instrs[n - 2].Cond.Negate() };
        instrs.RemoveAt(n - 1);
    }
}
