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
        DeadSpillStores(m);
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
            MergePops(block.Instrs);
            RedundantTests(block.Instrs);
            ZeroWithXor(block.Instrs);
            InvertJumpAroundJump(block.Instrs, next);
        }
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
        for (int b = 0; b < nb; b++)
        {
            succ[b] = m.Successors(b).Select(t => index[t]).ToArray();
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
                instrs[k + 1] = new MInstr(MOp.Mov, new MReg(lr.Id), new MReg(sr.Id));
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
                            instrs[k] = new MInstr(MOp.Mov, new MReg(dest.Id), new MReg(value.Id)) { Line = load.Line };
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

    private static bool SpillTransparent(MOp op) => op is MOp.Mov or MOp.Movsx or MOp.Movzx
        or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor
        or MOp.Cmp or MOp.Test or MOp.Imul or MOp.Imul3 or MOp.Mul
        or MOp.Neg or MOp.Not or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd
        or MOp.Cdq or MOp.Setcc or MOp.Nop;

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
                    user.Operands[k] = new MReg(b.Id);
                    break;
                case MMem m when m.Base?.Id == a.Id || m.Index?.Id == a.Id:
                    // Everything about the operand but the register survives.
                    // Losing Reloc here turned a `[got + sym@GOTOFF]` back
                    // into an absolute address, which a shared object can only
                    // honour with a text relocation -- and the whole point of
                    // position-independent code is not needing one.
                    user.Operands[k] = new MMem(m.Base?.Id == a.Id ? new MReg(b.Id) : m.Base, m.Disp)
                    {
                        Index = m.Index?.Id == a.Id ? new MReg(b.Id) : m.Index,
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
        => i.Op is MOp.Mov or MOp.Lea or MOp.Movzx or MOp.Movsx
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

    private static bool ReadsFlags(MInstr i) => i.Op is MOp.Jcc or MOp.Setcc or MOp.Adc or MOp.Sbb;

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
                if (after.Op is MOp.Jcc or MOp.Setcc)
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
        or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd or MOp.Xadd or MOp.Cmpxchg or MOp.LockOrEsp or MOp.Sahf;

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
                instrs[k] = new MInstr(MOp.Xor, new MReg(r.Id), new MReg(r.Id));
            }
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
