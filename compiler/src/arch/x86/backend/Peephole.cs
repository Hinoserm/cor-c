#nullable enable
using Corsac.Lang.Frames;

namespace Corsac.Lang.X86;

/// <summary>
/// Clean-up after register allocation, on physical registers.
///
/// The selector writes straightforward code and the allocator adds spill
/// traffic without looking at its neighbours; what is left over is local
/// and mechanical: a reload straight after the store that filled the slot,
/// a load whose register is overwritten before it is read, a conditional
/// jump around an unconditional one. Most rules are a pattern over a few
/// adjacent instructions of one block. The few that see the whole function
/// -- the parameters' homes, the blocks nothing reaches, a compare made
/// again past a jump -- say what they need of it, and the last of them
/// leaves flags live into a block, which no rule before it allows.
/// </summary>
internal static partial class Peephole
{
    public static void Run(MFunction m)
    {
        if (X86Mode.Long)
        {
            RunLong(m);
            return;
        }
        ParameterHomes(m);
        foreach (MBlock block in m.Blocks) StoredAgainAtOnce(block.Instrs);
        // Found on the code as the allocator left it, read once the spill
        // slots' own rules are done with them (TwinSlots).
        Dictionary<int, int>? twins = TwinSlots(m);
        ForwardSpillLoadsAcrossBlocks(m);
        foreach (MBlock block in m.Blocks) ForwardXmmSpillLoads(block.Instrs);
        DeadSpillStores(m);
        SpillStoresDeadAcrossBlocks(m);
        if (twins is not null) ReadTwins(m, twins);
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
            DeadXmmMoves(block.Instrs);
            RepeatedStores(block.Instrs, liveOut[b], Usable(m), Jumped);
            // Again: a run of stores of one constant now stores a register,
            // and a load of the slot just stored can read the register.
            ForwardStoreLoad(block.Instrs);
            MergePops(block.Instrs);
            RedundantTests(block.Instrs);
            ExchangedTests(block.Instrs);
            if (BranchOnSetFlags(block.Instrs) | ProbeBeforeAccess(block.Instrs)) DeadDefs(block.Instrs, liveOut[b], Jumped);
            ZeroWithXor(block.Instrs);
            DeadTests(block.Instrs);
            SwapBack(block.Instrs);
            BeforeTrap(block.Instrs);
            ReadModifyWrite(block.Instrs, liveOut[b], Jumped);
            ChooseForCpu(block.Instrs, liveOut[b], Usable(m), Jumped);
            IncDec(block.Instrs);
            PostStepTest(block.Instrs, liveOut[b], Jumped);
            OperateInPlace(block.Instrs, liveOut[b], Jumped);
            CopyThenOperate(block.Instrs);
            // Again: ReadModifyWrite, ChooseForCpu and IncDec above take
            // instructions from between two moves and leave them a pair.
            SwapBack(block.Instrs);
            InvertJumpAroundJump(block.Instrs, next);
        }
        CopyBackAcrossJoin(m);
        // A WAY THAT IS ANOTHER'S ONLY WAY IN, made one block.
        MergeSoleJumps(m);
        // A store or load at the end of one block and a reload of the slot
        // near the start of the one merged into it are now in one block: the
        // reload goes or reads the register (ForwardStoreLoad,
        // ForwardSpillLoads), and a store goes with it when nothing else
        // reads the slot (DeadSpillStores).
        foreach (MBlock block in m.Blocks)
        {
            ForwardStoreLoad(block.Instrs);
            ForwardSpillLoads(block.Instrs);
        }
        DeadSpillStores(m);
        // Then a value copied twice copied once, in every block
        // (ForwardCopies), with the registers' liveness read again for both.
        {
            int[] outs = LiveOut(m, out int[] ins);
            Dictionary<MBlock, int> where = new(ReferenceEqualityComparer.Instance);
            for (int b = 0; b < m.Blocks.Count; b++) where[m.Blocks[b]] = b;
            int Goes(MInstr i)
            {
                int live = 0;
                if (i.Op is MOp.Jcc or MOp.Jmp && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to })
                    live = where.TryGetValue(to, out int t) ? ins[t] : 0xFF;
                else if (i.Op == MOp.JmpTable && i.Table is { } table)
                    foreach (MBlock each in table) live |= where.TryGetValue(each, out int e) ? ins[e] : 0xFF;
                return live;
            }
            for (int b = 0; b < m.Blocks.Count; b++) ForwardCopies(m.Blocks[b].Instrs, outs[b], Goes);
            for (int b = 0; b < m.Blocks.Count; b++) RenameCopies(m.Blocks[b].Instrs, outs[b], Goes);
        }
        Unreachable(m);
        // LAST: it leaves flags live into a block, which every rule above
        // takes as never so (ZeroWithXor, ChooseForCpu's xor).
        CompareAgainAfterJump(m);
    }

    // ---- a compare made again on the way a jump fell through --------------------------

    /// <summary>
    /// `cmp a, b ; jcc X ; cmp a, b` -- the second compare where the jump fell
    /// through (or with no jump between), nothing between writing the flags,
    /// a or b, or memory: the flags are already what it would set, and it
    /// goes. 11,047 of them over
    /// a kernel, its modules and its userland, most a loop's test followed by
    /// an index's bounds check (`cmp i, [a+8] ; jge out ; cmp i, [a+8] ; jae
    /// throw`). A memory operand read once instead of twice is a read no
    /// other thread's write came between, which any interleaving allows: the
    /// path is straight, and no loop is made of it. Followed from a jump's
    /// block into the next only when nothing else enters that one -- no jump,
    /// table, taken label or landing pad.
    /// </summary>
    private static void CompareAgainAfterJump(MFunction m)
    {
        int n = m.Blocks.Count;
        Dictionary<MBlock, int> entries = new(ReferenceEqualityComparer.Instance);
        void Enter(MBlock? b) { if (b is not null) entries[b] = entries.GetValueOrDefault(b) + 1; }
        foreach (MBlock block in m.Blocks)
        {
            if (block.Source is { IsLandingPad: true }) Enter(block);
            foreach (MInstr i in block.Instrs)
            {
                if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to }) Enter(to);
                else if (i.Op == MOp.JmpTable && i.Table is { } table) foreach (MBlock t in table) Enter(t);
                foreach (MOperand o in i.Operands)
                {
                    if (o is MImm { Label: { } taken }) Enter(taken);
                    else if (o is MMem { Label: { } at }) Enter(at);
                }
            }
        }
        MInstr? last = null;            // the compare whose flags still stand
        Cond? fell = null;              // the condition the last jump on those flags fell through on
        MInstr? fellAfter = null;       // ... and the compare it was of
        for (int b = 0; b < n; b++)
        {
            MBlock block = m.Blocks[b];
            // Into a block only by falling through from the one before.
            if (b == 0 || m.Blocks[b - 1].EndsUnconditionally || entries.GetValueOrDefault(block) != 0) last = null;
            List<MInstr> instrs = block.Instrs;
            for (int k = 0; k < instrs.Count; k++)
            {
                MInstr i = instrs[k];
                if (i.Op is MOp.Cmp or MOp.Test && !i.Lock)
                {
                    if (last is not null && SameCompare(last, i))
                    {
                        instrs.RemoveAt(k);
                        k--;
                        continue;
                    }
                    last = i;
                    continue;
                }
                if (last is null) continue;
                if (i.Op == MOp.Jcc)
                {
                    // A JUMP ON THE CONDITION THE LAST ONE FELL THROUGH ON is
                    // never taken: the second test of `if (x != 0) F()`
                    // around an inlined F that begins `if (x == 0) return`,
                    // its compare gone just above (IrqSpinLock.Leave's
                    // `je; je`).
                    if (fell is { } known && ReferenceEquals(fellAfter, last) && known == i.Cond)
                    {
                        instrs.RemoveAt(k);
                        k--;
                        continue;
                    }
                    fell = i.Cond; fellAfter = last;
                    continue;
                }
                // What leaves the flags be, and writes a register or memory
                // the compare may read, ends it; anything else ends it too.
                if (i.Op is MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Setcc or MOp.Cmov && !i.Lock && i.Operands.Count >= 1)
                {
                    if (i.Operands[0] is MMem) { if (ReadsMemory(last)) last = null; }
                    else if (i.Operands[0] is MReg w && CompareReads(last, w.Id)) last = null;
                    continue;
                }
                if (i.Op == MOp.Nop) continue;
                last = null;
            }
        }
    }

    private static bool ReadsMemory(MInstr compare)
    {
        for (int k = 0; k < compare.Operands.Count; k++) if (compare.Operands[k] is MMem) return true;
        return false;
    }

    private static bool CompareReads(MInstr compare, int reg)
    {
        foreach (MOperand o in compare.Operands)
        {
            if (o is MReg r && r.Id == reg) return true;
            if (o is MMem mem && ((mem.Base?.Id ?? -1) == reg || (mem.Index?.Id ?? -1) == reg)) return true;
        }
        return false;
    }

    private static bool SameCompare(MInstr a, MInstr b)
    {
        if (a.Op != b.Op || a.Width != b.Width || a.Operands.Count != b.Operands.Count) return false;
        for (int k = 0; k < a.Operands.Count; k++)
        {
            bool same = (a.Operands[k], b.Operands[k]) switch
            {
                (MReg x, MReg y) => x.Id == y.Id,
                (MMem x, MMem y) => SameMem(x, y),
                (MImm { IsPlain: true } x, MImm { IsPlain: true } y) => x.Value == y.Value,
                _ => false,
            };
            if (!same) return false;
        }
        return true;
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
        // What every instruction does to the frame: where each parameter
        // write starts (taken as eight bytes, whatever its width says -- a
        // packed store's is eight and its Width the default four), each
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
                    // (The allocator's homes, X86Target.Homes, rely instead on
                    // Sys.FramePointer's contract -- read through, never written
                    // above the return address -- and do not stand down for EBP
                    // as a value, as this pass does; it moves a store.)
                    if (i.Operands[k] is MReg { IsPhys: true, Phys: Gpr.Ebp }) return;
                    if (i.Operands[k] is not MMem mem || mem.Base is not { IsPhys: true, Phys: Gpr.Ebp }) continue;
                    bool writes = (Roles.Of(i.Op, k) & Roles.Role.Def) != 0 || !Understood(i);
                    if (mem.Disp >= 8)
                    {
                        // A parameter's address taken, or reached by index: nothing moves.
                        if (i.Op == MOp.Lea || !Ebp(mem)) return;
                        // Roles name registers only, and an fstp's or an MMX
                        // store's memory operand takes their default, a use;
                        // a float parameter's home is its own argument slot,
                        // stored with fstp. So a parameter is read only by the
                        // forms known to read it, and anything else writes it.
                        if (writes || !ReadsOnly(i, k)) paramWritten.Add(mem.Disp);
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
            // A write of eight bytes starting from P-7 to P+3 reaches P's four.
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
                        i.Operands[k] = new MMem(mem.Base, to) { Device = mem.Device };
    }

    // ---- a spill slot stored and stored again ---------------------------------------------

    /// <summary>
    /// `mov [S], r ; add r, x ; mov [S], r` -- A SPILLED VALUE WRITTEN IN TWO
    /// STEPS, stored after each: a two-address sum is a move and an add, two
    /// writes of one register, and the allocator stores a value it gave to
    /// the frame after every write. The first store is read by nothing:
    /// what stands between the two (a few instructions at most) is no call
    /// (whose stack map may name the slot), no jump, and names the frame
    /// only in whole words that are not S's -- no address taken off EBP, no
    /// index, EBP nowhere as a value. DeadSpillStores finds as much within a
    /// block, but stands down for a block with a call in it and a function
    /// with a handler -- most of a kernel; this asks only about the
    /// instructions between the two stores, and a handler reads no spill
    /// slot a fault there could have left half written (what a handler reads
    /// has a home of its own, LandingPadHomes; a slot it found as the frame
    /// held it is better without the half).
    /// </summary>
    private static void StoredAgainAtOnce(List<MInstr> instrs)
    {
        const int Window = 4;
        for (int k = 0; k + 1 < instrs.Count; k++)
        {
            MInstr first = instrs[k];
            if (!IsMov(first) || first.Lock || first.Operands is not [MMem slot, MReg { IsPhys: true }] || !PrivateSpill(slot)) continue;
            for (int j = k + 1; j < instrs.Count && j <= k + Window; j++)
            {
                MInstr i = instrs[j];
                if (IsMov(i) && !i.Lock && i.Operands is [MMem again, MReg { IsPhys: true }] && PrivateSpill(again) && again.Disp == slot.Disp)
                {
                    instrs.RemoveAt(k);
                    k--;
                    break;
                }
                if (!Understood(i) || i.Op is MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd or MOp.CallSafe
                    or MOp.Jmp or MOp.Jcc or MOp.JmpTable or MOp.JmpInd or MOp.Ret or MOp.Epilogue or MOp.InlineAsm or MOp.Lea or MOp.Push or MOp.Pop) break;
                bool reaches = false;
                foreach (MOperand o in i.Operands)
                {
                    if (o is MReg { IsPhys: true, Phys: Gpr.Ebp }) reaches = true;
                    if (o is not MMem mem) continue;
                    if (mem.Index is { IsPhys: true, Phys: Gpr.Ebp }) reaches = true;
                    if (mem.Base is not { IsPhys: true, Phys: Gpr.Ebp }) continue;
                    // Any reach of the frame but a plain word elsewhere.
                    if (mem.Index is not null || mem.Symbol is not null || mem.Label is not null || i.Width != 4
                        || mem.Disp > slot.Disp - 4 && mem.Disp < slot.Disp + 4) reaches = true;
                }
                if (reaches) break;
            }
        }
    }

    // ---- a value kept in two slots ------------------------------------------------------

    /// <summary>
    /// A VALUE A HANDLER READS IS STORED TWICE AT EVERY WRITE: in its home
    /// in the frame, where an unwind finds it (LandingPadHomes: an IR slot of
    /// its own, stored after each definition, read at the top of a landing
    /// pad), and -- the allocator having no register for it -- in a spill
    /// slot beside it:
    ///
    ///     mov [ebp-S], r          ; the allocator's
    ///     mov [ebp-H], r          ; the home's
    ///
    /// 11,286 such pairs in a K8 kernel, in every function with a try, a
    /// lock or a using that keeps more values than it has registers. Where
    /// EVERY store of the spill slot S stands beside a store of the same
    /// register to one home H (or just after a load of H into that
    /// register, a landing pad's), every change of S in place (`add [S],
    /// x`) is read back and stored to H at once, every store of H is of
    /// what S holds, and
    /// both are reached only as whole words by instructions this knows --
    /// H a word of an IR slot of one or two whose address nothing takes --
    /// the two hold
    /// one value wherever S is read: S is read as H, and its stores go.
    /// The map from S to H; null for none. Found before the rules for spill
    /// slots run (they forward S's loads from registers and drop its dead
    /// stores, which only H's twin can still be told by), applied after
    /// them (ReadTwins): a read of S left then was a read of S before, and
    /// nothing between touches H.
    /// </summary>
    private static Dictionary<int, int>? TwinSlots(MFunction m)
    {
        if (m.Blocks.Count == 0) return null;
        // A home is eight bytes, a long's two words or a word and nothing:
        // each word of one a place a spill slot may be the twin of.
        HashSet<int> homes = new();
        foreach ((int offset, int bytes) in m.Frame.SlotRanges())
        {
            if (offset >= 0 || bytes is not (4 or 8)) continue;
            homes.Add(offset);
            if (bytes == 8) homes.Add(offset + 4);
        }
        if (homes.Count == 0) return null;
        static bool Ebp(MMem mem) => mem.Base is { IsPhys: true, Phys: Gpr.Ebp } && mem.Index is null && mem.Symbol is null && mem.Label is null;
        // Every reach into the frame: a slot's plain whole stores of a
        // register and its whole words changed in place (where), and
        // anything else done to its bytes.
        HashSet<int> other = new();
        Dictionary<int, List<(MBlock Block, int At)>> stores = new(), changes = new();
        foreach (MBlock block in m.Blocks)
            for (int at = 0; at < block.Instrs.Count; at++)
            {
                MInstr i = block.Instrs[at];
                // An asm block that names the stack in its own text may reach
                // any slot with no operand to show it.
                if (i.Op == MOp.InlineAsm && i.Asm!.Analysis.UsesStack) return null;
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    // (EBP as a value -- a try's handler record keeps it for the
                    // unwind to put back, Sys.FramePointer hands it to what
                    // walks the frames -- is read through for the saved frame
                    // pointers and return addresses alone, never to a local:
                    // its contract (Prelude), which the allocator's homes rest
                    // on as well, X86Target.Homes. The collector reads every
                    // word of the stack as it stands, and the home still
                    // holds what the pair held.)
                    if (i.Operands[k] is not MMem mem) continue;
                    if (mem.Index is { IsPhys: true, Phys: Gpr.Ebp }) return null;
                    if (mem.Base is not { IsPhys: true, Phys: Gpr.Ebp }) continue;
                    bool whole = i.Op != MOp.Lea && Ebp(mem) && i.Width == 4 && !i.Lock && Understood(i);
                    if (!whole)
                    {
                        // Taken as sixteen bytes from where it starts, whatever its width says.
                        for (int b = mem.Disp; b < mem.Disp + 16; b++) other.Add(b);
                        continue;
                    }
                    if ((Roles.Of(i.Op, k) & Roles.Role.Def) == 0) continue;
                    Dictionary<int, List<(MBlock, int)>>? kind = IsMov(i) && k == 0 && i.Operands[1] is MReg { IsPhys: true } ? stores
                        : k == 0 && i.Op is MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Inc or MOp.Dec or MOp.Neg or MOp.Not ? changes : null;
                    if (kind is null)
                    {
                        for (int b = mem.Disp; b < mem.Disp + 4; b++) other.Add(b);
                        continue;
                    }
                    if (!kind.TryGetValue(mem.Disp, out var list)) kind[mem.Disp] = list = new();
                    list.Add((block, at));
                }
            }
        bool Clear(int slot)
        {
            for (int b = slot; b < slot + 4; b++) if (other.Contains(b)) return false;
            // No other word's write overlapping it.
            for (int b = slot - 3; b < slot + 4; b++) if (b != slot && (stores.ContainsKey(b) || changes.ContainsKey(b))) return false;
            return true;
        }
        // `mov [ebp+d], r` at `at`, for the r it stores; -1 for anything else.
        static int StoreOf(MBlock block, int at, int d)
            => at >= 0 && at < block.Instrs.Count && block.Instrs[at] is { Lock: false } i && IsMov(i)
               && i.Operands is [MMem mem, MReg { IsPhys: true } from] && Ebp(mem) && mem.Disp == d ? from.Id : -1;
        // `mov r, [ebp+d]` at `at`, for the r it loads; -1 for anything else.
        static int LoadOf(MBlock block, int at, int d)
            => at >= 0 && at < block.Instrs.Count && block.Instrs[at] is { Lock: false } i && IsMov(i)
               && i.Operands is [MReg { IsPhys: true } into, MMem mem] && Ebp(mem) && mem.Disp == d ? into.Id : -1;
        Dictionary<int, int>? twins = null;
        HashSet<int> taken = new();
        foreach ((int s, var spills) in stores.OrderBy(pair => pair.Key))
        {
            (MBlock firstBlock, int firstAt) = spills[0];
            if (firstBlock.Instrs[firstAt].Operands[0] is not MMem { IsSpill: true } || homes.Contains(s) || !Clear(s)) continue;
            // THE HOME: the one slot every store of S stands beside a store
            // of the same register to (either side), or was just loaded from.
            int h = 0;
            bool twin = true;
            foreach ((MBlock block, int at) in spills)
            {
                int r = ((MReg)block.Instrs[at].Operands[1]).Id;
                int beside = 0;
                foreach (int near in new[] { at - 1, at + 1 })
                    if (near >= 0 && near < block.Instrs.Count && block.Instrs[near] is { Lock: false } n && IsMov(n)
                        && n.Operands is [MMem mem, MReg { IsPhys: true } from] && from.Id == r && Ebp(mem) && homes.Contains(mem.Disp)) beside = mem.Disp;
                if (beside == 0 && at > 0 && block.Instrs[at - 1] is { Lock: false } load && IsMov(load)
                    && load.Operands is [MReg { IsPhys: true } into, MMem home] && into.Id == r && Ebp(home) && homes.Contains(home.Disp)) beside = home.Disp;
                if (beside == 0 || h != 0 && beside != h) { twin = false; break; }
                h = beside;
            }
            if (!twin || h == 0 || taken.Contains(h) || !Clear(h) || changes.ContainsKey(h) || !stores.TryGetValue(h, out var homed)) continue;
            // S changed in place is read back and stored to the home at once:
            // `add [S], x ; mov r, [S] ; mov [H], r`.
            if (changes.TryGetValue(s, out var changed))
                foreach ((MBlock block, int at) in changed)
                {
                    int r = LoadOf(block, at + 1, s);
                    if (r < 0 || StoreOf(block, at + 2, h) != r) { twin = false; break; }
                }
            // And every store of the home is of what S holds: beside a store
            // of that register to S, or of the register S was just read into.
            if (twin)
                foreach ((MBlock block, int at) in homed)
                {
                    int r = ((MReg)block.Instrs[at].Operands[1]).Id;
                    if (StoreOf(block, at - 1, s) == r || StoreOf(block, at + 1, s) == r || LoadOf(block, at - 1, s) == r) continue;
                    twin = false;
                    break;
                }
            if (!twin) continue;
            taken.Add(h);
            (twins ??= new())[s] = h;
        }
        return twins;
    }

    /// <summary>Each twinned spill slot read as its home, its stores gone and no safepoint naming it (TwinSlots).</summary>
    private static void ReadTwins(MFunction m, Dictionary<int, int> twins)
    {
        HashSet<int> homes = new(twins.Values);
        foreach (MBlock block in m.Blocks)
        {
            List<MInstr> instrs = block.Instrs;
            for (int k = 0; k < instrs.Count; k++)
            {
                MInstr i = instrs[k];
                if (IsMov(i) && i.Operands is [MMem { IsSpill: true } store, MReg] && PrivateSpill(store) && twins.ContainsKey(store.Disp))
                {
                    instrs.RemoveAt(k);
                    k--;
                    continue;
                }
                for (int o = 0; o < i.Operands.Count; o++)
                    if (i.Operands[o] is MMem { IsSpill: true } mem && PrivateSpill(mem) && twins.TryGetValue(mem.Disp, out int home))
                        i.Operands[o] = new MMem(mem.Base, home) { Device = mem.Device };
            }
            // `mov r, [H] ; mov [H], r`, what S read back and stored to its
            // home has become: the store puts back what was just read.
            for (int k = 1; k < instrs.Count; k++)
                if (IsMov(instrs[k]) && !instrs[k].Lock && instrs[k].Operands is [MMem to, MReg { IsPhys: true } from] && homes.Contains(to.Disp)
                    && to.Base is { IsPhys: true, Phys: Gpr.Ebp } && to.Index is null && to.Symbol is null && to.Label is null
                    && IsMov(instrs[k - 1]) && !instrs[k - 1].Lock && instrs[k - 1].Operands is [MReg { IsPhys: true } into, MMem back] && into.Id == from.Id
                    && back.Base is { IsPhys: true, Phys: Gpr.Ebp } && back.Index is null && back.Symbol is null && back.Label is null && back.Disp == to.Disp)
                {
                    instrs.RemoveAt(k);
                    k--;
                }
        }
        // The home is an IR slot, read by the collector as it stands: the
        // spill slot, written no more, is no word of any map.
        foreach (Safepoint safepoint in m.Safepoints.Values)
            safepoint.SlotOffsets.RemoveAll(twins.ContainsKey);
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

    // ---- a move straight back, across the place two ways meet ----------------------------

    /// <summary>
    /// `mov x, a` at the end of one way into a block that begins `mov a, x`:
    /// the pair SwapBack takes within a block, standing either side of a
    /// join, where the block's first move is the other ways' too and may not
    /// simply go. A value two ways make -- a call's result one way (EAX), a
    /// fast path's the other, in whichever register the allocator gave the
    /// merged value -- returned or passed on in EAX: `call slow ; mov ecx,
    /// eax` falling into `mov eax, ecx ; ... ret`, 1,850 of them in the
    /// compiler's own image, each two moves on the way that had the value
    /// where it was wanted all along.
    ///
    /// The block's first move is given to each way in instead -- put before
    /// the jump of one that ends in a jump there, at the end of one that
    /// falls in -- and where that way ends in the move the other way round,
    /// the two are a value copied and copied back: both go where x is dead
    /// once the block's move has read it, and the way's own stays where x
    /// is read again (it is what gives x its value). Only where every way
    /// in has the block as its one way on from its end (no conditional
    /// jump to it, no table, its address not taken, no landing pad), and
    /// no more bytes are added than taken away. Where another way does
    /// jump to it on a condition, a way ending in the copy back takes the
    /// block's last few instructions for its own instead (below): at most
    /// five, a few bytes more for two moves fewer on that way. One join a
    /// round, the registers' liveness read again after each, and at most
    /// 64 a function: what is left past that stays as it was.
    /// </summary>
    private static void CopyBackAcrossJoin(MFunction m)
    {
        int n = m.Blocks.Count;
        if (n < 2) return;
        static bool Copy(MInstr i, out MReg to, out MReg from)
        {
            to = from = null!;
            if (!IsMov(i) || i.Operands.Count != 2 || i.Lock || i.Operands[0] is not MReg { IsPhys: true } a || i.Operands[1] is not MReg { IsPhys: true } b || a.Id == b.Id) return false;
            to = a; from = b;
            return true;
        }
        // The cheap look first: a block beginning with a copy whose way in
        // from above ends in the copy back, or any block ending `copy back ;
        // jmp` to one. Most functions have none, and no liveness is asked.
        bool any = false;
        for (int j = 1; j < n && !any; j++)
            any = m.Blocks[j].Instrs.Count > 0 && Copy(m.Blocks[j].Instrs[0], out _, out _);
        if (!any) return;

        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < n; b++) index[m.Blocks[b]] = b;
        HashSet<MBlock> taken = new(ReferenceEqualityComparer.Instance);
        foreach (MBlock block in m.Blocks)
            foreach (MInstr i in block.Instrs)
                foreach (MOperand o in i.Operands)
                {
                    if (o is MImm { Label: { } label }) taken.Add(label);
                    else if (o is MMem { Label: { } table }) taken.Add(table);
                }
        bool changed = true;
        int rounds = 0;
        while (changed && rounds++ < 64)
        {
            changed = false;
            int[] liveOut = LiveOut(m, out int[] liveIn);
            for (int j = 1; j < n; j++)
            {
                MBlock join = m.Blocks[j];
                if (join.Instrs.Count == 0 || !Copy(join.Instrs[0], out MReg a, out MReg x)) continue;
                if (join.Source is { IsLandingPad: true } || taken.Contains(join)) continue;
                // x dead after the copy: read back from the block's end.
                int live = !join.EndsUnconditionally && j + 1 < n ? liveIn[j + 1] : 0;
                bool known = true;
                for (int k = join.Instrs.Count - 1; k >= 1 && known; k--)
                {
                    MInstr i = join.Instrs[k];
                    if (i.Op is MOp.Jcc or MOp.Jmp && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to })
                    {
                        if (index.TryGetValue(to, out int at)) live |= liveIn[at]; else known = false;
                        continue;
                    }
                    if (i.Op == MOp.JmpTable && i.Table is { } table)
                        foreach (MBlock t in table) { if (index.TryGetValue(t, out int ta)) live |= liveIn[ta]; else known = false; }
                    if (!Understood(i)) { live = 0xFF; continue; }
                    Masks(i, out long reads, out long writes);
                    live = (live & ~(int)writes) | (int)reads;
                }
                if (!known) continue;
                // Where x is read again after it, a way that ends in the
                // copy back keeps that copy -- x has its value by it -- and
                // only the block's own goes.
                bool wanted = (live & (int)Bit(x.Id)) != 0;
                // Every way in: the block above falling in, and each block
                // whose last instruction is a jump here -- and whether any
                // other jump comes here from anywhere (`sound` not).
                List<MBlock> ways = new();
                bool sound = true;
                for (int b = 0; b < n; b++)
                {
                    MBlock from = m.Blocks[b];
                    bool falls = b == j - 1 && !from.EndsUnconditionally;
                    bool jumps = false, other = false;
                    for (int k = 0; k < from.Instrs.Count; k++)
                    {
                        MInstr i = from.Instrs[k];
                        if (i.Op == MOp.JmpTable && i.Table is { } table && table.Any(t => ReferenceEquals(t, join))) other = true;
                        else if (i.Op is MOp.Jcc or MOp.Jmp && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to } && ReferenceEquals(to, join))
                        {
                            if (i.Op == MOp.Jmp && k == from.Instrs.Count - 1 && !jumps) jumps = true; else other = true;
                        }
                    }
                    if (other || falls && jumps) sound = false;
                    else if (falls || jumps) ways.Add(from);
                }
                if (ways.Count == 0 || ways.Any(w => ReferenceEquals(w, join))) continue;
                // Which ways end in the copy back (before the jump of one that jumps).
                int Last(MBlock way) => way.Instrs.Count - (way.EndsUnconditionally ? 2 : 1);
                bool Back(MBlock way) => Last(way) >= 0 && Copy(way.Instrs[Last(way)], out MReg to, out MReg from) && to.Id == x.Id && from.Id == a.Id;
                int backs = ways.Count(Back);
                if (backs == 0) continue;
                if (sound && ways.Count - backs <= (wanted ? 1 : backs + 1))
                {
                    foreach (MBlock way in ways)
                    {
                        if (Back(way)) { if (!wanted) way.Instrs.RemoveAt(Last(way)); }
                        else way.Instrs.Insert(way.EndsUnconditionally ? way.Instrs.Count - 1 : way.Instrs.Count, new MInstr(MOp.Mov, a, x) { Width = 4, Line = join.Instrs[0].Line });
                    }
                    join.Instrs.RemoveAt(0);
                    changed = true;
                    break;      // the liveness is of the code as it was
                }
                // ELSE THE BLOCK'S FEW LAST INSTRUCTIONS AGAIN, where another
                // way jumps here on a condition (its move cannot go before
                // that jump: the other way on may want the register): a way
                // ending in the copy back takes the rest of the block for its
                // own -- a result's `pop ; leave ; ret`, a jump on -- and
                // neither move is on it. A cached value's fast way jumping
                // to the return, the slow way's call falling into it.
                if (wanted || !join.EndsUnconditionally || join.Instrs.Count > 6) continue;
                bool plain = true;
                for (int k = 1; k < join.Instrs.Count && plain; k++)
                    // (A move between registers or of the frame's own
                    // slots alone: nothing that could fault is taken to
                    // another block, which may lie under another handler.)
                    plain = join.Instrs[k].Op is MOp.Pop or MOp.Epilogue or MOp.Ret
                        || IsMov(join.Instrs[k]) && join.Instrs[k].Operands.All(o => o is MReg or MImm { Symbol: null, Label: null }
                            || o is MMem { Base: { IsPhys: true, Phys: Gpr.Ebp }, Index: null, Symbol: null, Label: null })
                        || join.Instrs[k].Op == MOp.Jmp && k == join.Instrs.Count - 1 && join.Instrs[k].Operands.Count > 0 && join.Instrs[k].Operands[0] is MLabel;
                if (!plain) continue;
                bool taking = false;
                foreach (MBlock way in ways)
                {
                    if (!Back(way)) continue;
                    way.Instrs.RemoveRange(Last(way), way.Instrs.Count - Last(way));
                    for (int k = 1; k < join.Instrs.Count; k++) way.Instrs.Add(LoopRotate.Clone(join.Instrs[k]));
                    taking = true;
                }
                if (taking) { changed = true; break; }
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
                    // A block's address taken: as a constant (i386), by an
                    // LEA of its label or as a jump table's (both), as a
                    // label that is no jump's (long mode's unwind).
                    if (o is MImm { Label: { } taken }) Root(taken);
                    else if (o is MMem { Label: { } table }) Root(table);
                    else if (o is MLabel { Target: var named } && i.Op is not (MOp.Jmp or MOp.Jcc)) Root(named);
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
                    // THE SELECTOR'S UNREACHABLE (INT3, no operand): nothing
                    // runs after it, so nothing is live before it. A trap the
                    // program may run on from (INT3 1) reads and writes
                    // nothing: what is live after it is live before it. Taken
                    // for everything read, every block a bounds check or a
                    // throw ends in -- `call ...; int3; int3` -- kept EBX, ESI
                    // and EDI live into it from wherever they were set, and a
                    // read-modify-write or a dead store through one of them
                    // on the way there stayed what it was.
                    if (i.Op == MOp.Int3)
                    {
                        if (i.Operands.Count == 0) live = 0;
                        continue;
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
    /// answer more cautious; EBP holding a value has EBP's (Bit).
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

    /// <summary>
    /// A register's bit in this file's masks. EBP HOLDING A VALUE
    /// (MReg.EbpData, a frame-pointer-free leaf's) IS EBP'S BIT: until
    /// FramePointerFree, an operand based on EBP is the frame and the value
    /// in EBP is that id, past every machine register's. Given a bit of its
    /// own past the int masks, a write of it went unseen where they are
    /// narrowed (ForwardSpillLoads' clobbered set, `1 << 32` being `1 << 0`
    /// to C#), and a reload was forwarded from EBP across the write that
    /// replaced its value: GcThreads.CaptureBuffersInto, PIC in libcorsacrt
    /// (its GOT pointer the register too few), stored each buffer's table
    /// slot's own address as the buffer's base -- the walk met every live
    /// buffer as a broken chain, and every concurrent cycle was given up.
    /// One bit for the frame and the value only makes a rule more careful;
    /// what must tell them apart -- a frame moved, every slot let go -- asks
    /// WritesFrame.
    /// </summary>
    private static long Bit(int id) => 1L << (id == MReg.EbpDataId ? (int)Gpr.Ebp : id & 63);

    /// <summary>
    /// Whether an instruction moves the frame: writes EBP AS THE FRAME'S
    /// BASE, the machine register, explicitly or implicitly. Not EBP holding
    /// a value (MReg.EbpData), which shares its bit (Bit) but leaves every
    /// slot where it was.
    /// </summary>
    private static bool WritesFrame(MInstr i)
    {
        for (int k = 0; k < i.Operands.Count; k++)
            if (i.Operands[k] is MReg { Id: (int)Gpr.Ebp } && (Roles.Of(i.Op, k) & Roles.Role.Def) != 0) return true;
        foreach (Gpr g in Roles.ImplicitDefs(i)) if (g == Gpr.Ebp) return true;
        return false;
    }

    // ---- store, then reload from the same slot ----------------------------------

    // A DEVICE'S REGISTER IS NEVER THE SAME READ TWICE (MMem.Device): no
    // rule that reads one for two may match it.
    private static bool SameMem(MMem a, MMem b)
        => !a.Device && !b.Device && a.Disp == b.Disp && a.Scale == b.Scale && a.Symbol == b.Symbol && a.Reloc == b.Reloc && a.Label == b.Label
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
            if (!IsMov(store) || !IsMov(load) || store.Lock || load.Lock || store.Operands[0] is not MMem sm || store.Operands[1] is not MReg sr
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
                instrs[k + 1] = new MInstr(MOp.Mov, MReg.Of(lr.Id), MReg.Of(sr.Id)) { Line = load.Line };
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
                    if (k == 0 && StoreSlot(i) is not null) { any = true; continue; }
                    read.Add(mem.Disp);
                }
        if (!any) return;
        // A fault inside a block can land in a handler of this function that
        // reloads a value from its slot: there the first store is not dead.
        bool handlers = m.Blocks.Any(b => b.Source is { IsLandingPad: true });
        foreach (MBlock b in m.Blocks)
        {
            b.Instrs.RemoveAll(i => StoreSlot(i) is int slot && !read.Contains(slot));
            // And within a block, a store the next store to the same slot
            // overwrites before anything reads it -- the slot shared with
            // other values that do read it. Walked backwards: `written` holds
            // the slots stored again further on with no read between. Only
            // where the block makes no call, whose stack map may read a slot,
            // and the function has no handler.
            if (handlers || b.Instrs.Any(i => i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe)) continue;
            HashSet<int> written = new();
            for (int k = b.Instrs.Count - 1; k >= 0; k--)
            {
                MInstr i = b.Instrs[k];
                // A way out of the block reads what is read where it goes: a
                // store before it is not known dead by one after it. (Every
                // block's jumps are its last instructions today, with no
                // store after one; two values in one slot, Allocator
                // .TakeSlot, make the case worth not leaning on that.)
                if (i.Op is MOp.Jcc or MOp.Jmp or MOp.JmpTable or MOp.JmpInd) written.Clear();
                if (StoreSlot(i) is int stored)
                {
                    if (!written.Add(stored)) { b.Instrs.RemoveAt(k); continue; }
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
    /// (Allocator.TakeSlot) whose first is read only where forwarding found it in a
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
                else if (i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe) live.UnionWith(all);
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

    /// <summary>
    /// The allocator slot a plain store writes, or null: a word's MOV, or an
    /// XMM value's MovF -- the slot whole either way, every slot of a class
    /// one width (X86Target.NewSlot) and named only at its start. A Packed128
    /// made in two-address form, `movaps t, a ; paddd t, b`, was stored after
    /// each, the first for nothing: 63 of Generate4's 104 stores.
    /// </summary>
    private static int? StoreSlot(MInstr i)
        => (IsMov(i) && !i.Lock || i.Op == MOp.MovF && i.Operands.Count == 2 && i.Operands[1] is MReg)
           && i.Operands.Count == 2 && i.Operands[0] is MMem store && PrivateSpill(store) ? store.Disp : null;

    // ---- a register written and then written again before being read -------------

    // Only allocator-owned slots participate. Source-language stack references
    // may be address-taken or volatile; an EBP address alone is not provenance.
    private static bool PrivateSpill(MMem m) => m.IsSpill
        && m.Base?.Id == (int)Gpr.Ebp && m.Index is null
        && m.Symbol is null && m.Label is null;

    /// <summary>
    /// A reload of an allocator slot whose value a register still holds:
    /// looking back up to 16 instructions for the last load or store of the
    /// slot, through plain register arithmetic only (SpillTransparent; any
    /// other memory operand, jump, call or write of the frame stops it), the
    /// reload becomes a move from that register -- or goes, when it is the
    /// register it loads -- where nothing between wrote the register.
    /// </summary>
    internal static void ForwardSpillLoads(List<MInstr> instrs)
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
                        // The register already holds it: no move at all
                        // (it was left `mov eax, eax`).
                        if ((clobbered & (int)Bit(value!.Id)) == 0)
                        {
                            if (value.Id == dest.Id) { instrs.RemoveAt(k); k--; }
                            else instrs[k] = new MInstr(MOp.Mov, MReg.Of(dest.Id), MReg.Of(value.Id)) { Line = load.Line };
                        }
                        break;
                    }
                    // Be conservative about partial overlaps, including unusual frames.
                    if (Math.Abs((long)memory.Disp - slot.Disp) < 4) break;
                }
                else if (prev.Operands.Any(o => o is MMem) || !SpillTransparent(prev.Op)) break;
                Masks(prev, out _, out long written);
                clobbered |= (int)written;
                // THE FRAME MOVED: no slot is where it was. EBP's value
                // written is only one register clobbered (its bit, Bit), a
                // reload from it refused, a reload from any other still not.
                if (WritesFrame(prev)) break;
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
                    MInstr copy = new(i.Op) { Width = i.Width, Cond = i.Cond, Lock = i.Lock, Table = i.Table, CallReloc = i.CallReloc, Line = i.Line, Native = i.Native, Probed = i.Probed, OwnsState = i.OwnsState, Asm = i.Asm, ZeroFirst = i.ZeroFirst };
                    for (int j = 0; j < i.Operands.Count; j++) copy.Operands.Add(j == k ? MReg.Of(Lowest(h)) : i.Operands[j]);
                    i = copy;
                    break;
                }
            }
        }
        // Its effect: a call, an asm block (which may write any memory) or
        // anything moving the frame lets go of everything. EBP's value
        // written is a register written like any other, which no slot here
        // is held in (holders are ids below 8, and EbpData is 32): it lets
        // go of nothing.
        if (i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.InlineAsm) { state.Clear(); return i; }
        Masks(i, out _, out long written);
        if (WritesFrame(i)) { state.Clear(); return i; }
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

    /// <summary>
    /// AN XMM VALUE RELOADED FROM THE SLOT A REGISTER STILL HOLDS IT IN:
    /// `movups [s], x6 ; movaps x6', x5 ; ... ; movups x2, [s]` -- the
    /// allocator gave x6 to something else, so it reloaded for x2 what x6
    /// held all along. When nothing between writes the register or the
    /// slot, the load becomes a register move (MOVAPS, no memory). The store
    /// may then have nothing left to read it; DeadSpillStores, after this,
    /// takes it. Within a block, past only instructions whose register writes
    /// Masks sees -- operands' and Roles' implicit ones; no call, which
    /// clobbers XMM registers unnamed. A float's reload this way leaves the
    /// register's top lanes the source's, not zero, as SseCopy's move does:
    /// nothing reads a scalar's top lanes.
    /// </summary>
    internal static void ForwardXmmSpillLoads(List<MInstr> instrs)
    {
        for (int k = 0; k < instrs.Count; k++)
        {
            MInstr load = instrs[k];
            if (load.Op != MOp.MovF || load.Operands.Count != 2 || load.Operands[0] is not MReg { IsXmm: true } dest
                || load.Operands[1] is not MMem slot || !PrivateSpill(slot))
                continue;
            long clobbered = 0;
            for (int p = k - 1; p >= 0 && p >= k - 32; p--)
            {
                MInstr prev = instrs[p];
                if (prev.Lock || !XmmSpillTransparent(prev.Op)) break;
                // The slot stored from a register, or loaded into one: that register holds it.
                MReg? held = null;
                if (prev.Op == MOp.MovF && prev.Width == load.Width && prev.Operands.Count == 2)
                {
                    if (prev.Operands[0] is MMem to && SameMem(to, slot) && prev.Operands[1] is MReg { IsXmm: true } from) held = from;
                    else if (prev.Operands[1] is MMem src && SameMem(src, slot) && prev.Operands[0] is MReg { IsXmm: true } into) held = into;
                }
                if (held is not null)
                {
                    if ((clobbered & Bit(held.Id)) == 0)
                    {
                        if (held.Id == dest.Id) { instrs.RemoveAt(k); k--; }
                        else instrs[k] = new MInstr(MOp.MovF, MReg.Of(dest.Id), MReg.Of(held.Id)) { Width = load.Width, Line = load.Line };
                    }
                    break;
                }
                // A write of any slot that could overlap this one ends it; a read does not.
                bool near = false;
                for (int o = 0; o < prev.Operands.Count; o++)
                    if (prev.Operands[o] is MMem mem && PrivateSpill(mem) && Math.Abs((long)mem.Disp - slot.Disp) < 16
                        && (Roles.Of(prev.Op, o) & Roles.Role.Def) != 0)
                        near = true;
                if (near) break;
                Masks(prev, out _, out long written);
                clobbered |= written;
                // The frame moved (WritesFrame); EBP's value written moves no slot.
                if (WritesFrame(prev)) break;
            }
        }
    }

    /// <summary>An instruction whose register writes are all its operands' (Roles), and which writes memory only through them.</summary>
    private static bool XmmSpillTransparent(MOp op) => SpillTransparent(op)
        || op is MOp.MovF or MOp.AddF or MOp.SubF or MOp.MulF or MOp.DivF or MOp.SqrtF or MOp.UcomiF or MOp.CvtFToF or MOp.CvtIntToF
            or MOp.XorPs or MOp.MovdToX or MOp.MovdFromX or MOp.Punpckldq
            or MOp.XmmOpV or MOp.XmmOpVDef or MOp.XmmShiftV or MOp.XmmExtractV or MOp.XmmInsertV;

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
                // `mov A, B ; mov B, A` was the value carried there and back:
                // the use is now `mov B, B`, which does nothing, and goes too
                // (262 of them were left in the 32-bit compiler).
                if (IsMov(i) && i.Operands[0] is MReg { IsPhys: true } to && i.Operands[1] is MReg { IsPhys: true } from && to.Id == from.Id)
                {
                    instrs.RemoveAt(k);
                    continue;
                }
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
            // A machine register only, and never EBP: EBP holding a value
            // (MReg.EbpData, an id past the machine registers) never enters
            // `dead`, which starts without EBP and takes only ids below 8,
            // so a write of it is never taken for dead -- though the
            // liveness has it, in EBP's bit (Bit).
            foreach ((MReg r, bool isDef) in Regs(i))
            {
                if (isDef && r.Id < 8)
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
            || !deadAfter.Contains(a.Id) || !Understood(user) || user.Op is MOp.Xchg or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd or MOp.CallSafe)
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
                        Device = m.Device,
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
        MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd or MOp.CallSafe => operand > 0,
        // So are a call's that passes its arguments in registers (RegisterArgs):
        // EAX, EDX and ECX, each the argument of its place.
        MOp.Call or MOp.CallInd => operand > 0,
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
    /// rule has it (IrInfo.IsPure) -- whose one exception, a read of what the
    /// link made (Instr.Immutable), has gone before here when unused, so none
    /// is told apart at this level. A frame's, a spill's or a static's is
    /// memory the program owns, and goes with the register it fills.
    private static bool Removable(MInstr i)
        => i.Op is MOp.Mov or MOp.Lea or MOp.Movzx or MOp.Movsx or MOp.Cmov or MOp.Setcc
           && i.Operands[0] is MReg { Id: not ((int)Gpr.Esp or (int)Gpr.Ebp) }
           && (i.Op == MOp.Lea || i.Operands.Count < 2 || i.Operands[1] is not MMem m || CannotFault(m));

    /// <summary>
    /// Whether reading the operand can never fault: the frame, or a symbol's
    /// static storage. A read through any other register, or of a bare
    /// address, is kept though nothing reads what it loads -- it may be the
    /// NullReferenceException a call on null must raise, and a receiver the
    /// optimiser had proved null was a load of address 0 with no base that
    /// went as dead: `((Thing)null).ToString()` answered "" (test 1284).
    /// A device's register is never taken for one: read, it may clear, so
    /// a read nobody uses still happens (MMem.Device).
    /// </summary>
    private static bool CannotFault(MMem m)
        => !m.Device && (m.Base is { Id: (int)Gpr.Esp or (int)Gpr.Ebp }
           || m.Base is null && m.Index is null && (m.Symbol is not null || m.Label is not null));

    /// <summary>
    /// Whether operand k, a memory operand, is only read: a move's source, a
    /// push, a comparison, an arithmetic source, an x87 load or operand, a
    /// conversion's source. Anything not listed counts as a write.
    /// </summary>
    private static bool ReadsOnly(MInstr i, int k) => i.Op switch
    {
        MOp.Mov or MOp.Movsx or MOp.Movzx or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor
            or MOp.Imul or MOp.Imul3 or MOp.Cmov or MOp.Cvttss2si or MOp.Cvttsd2si => k >= 1,
        MOp.Cmp or MOp.Test or MOp.Push or MOp.Fld or MOp.Fild or MOp.Fadd or MOp.Fsub or MOp.Fmul or MOp.Fdiv
            or MOp.Fldcw or MOp.Mul or MOp.ImulWide or MOp.Div or MOp.Idiv or MOp.CallInd or MOp.MmxLoad or MOp.MmxLoadD => true,
        _ => false,
    };

    /// <summary>Whether the operand roles fully describe what the instruction reads and writes.</summary>
    private static bool Understood(MInstr i) => i.Op switch
    {
        // An unwind jump carries the exception in EAX by convention, unnamed.
        MOp.JmpInd or MOp.Ret or MOp.Prologue or MOp.Int or MOp.Int3 => false,
        _ => true,
    };

    private static IEnumerable<(MReg Reg, bool IsDef)> Regs(MInstr i)
    {
        if (i.Op is MOp.Xor or MOp.XorPs && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id)
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

    // ---- dead moves into the XMM registers --------------------------------------------

    /// <summary>
    /// A MOVE INTO AN XMM REGISTER WRITTEN AGAIN BEFORE IT IS READ, in a block:
    /// gone. A Packed128 variable is a register its every assignment copies
    /// into (Select.PackedValues), and one assigned twice with no read between
    /// left the first copy behind -- the ChaCha20 rounds had one in every
    /// line. Every register is taken for live at the block's end and at a
    /// jump out of its middle, which this walk knows nothing of; a call
    /// writes them all (none survives one). A block with an instruction that
    /// names its XMM register by number -- the packed registers', a state's
    /// save -- is left as it is.
    /// </summary>
    private static void DeadXmmMoves(List<MInstr> instrs)
    {
        foreach (MInstr i in instrs)
            if (i.Op is MOp.XmmOpRR or MOp.XmmOp or MOp.XmmLoad or MOp.XmmStore or MOp.XmmLoadLow or MOp.XmmStoreLow or MOp.XmmShiftImm
                or MOp.XmmExtractW or MOp.XmmInsertW or MOp.XmmStoreNt or MOp.PackedSaveFxsr or MOp.PackedRestoreFxsr)
                return;
        int dead = 0;
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            // A move, or a register zeroed (XORPS with itself), that nothing
            // reads -- not a load that may fault (CannotFault: DeadDefs' rule),
            // which may be the NullReferenceException the program must raise.
            if ((i.Op == MOp.MovF && (i.Operands[1] is not MMem from || CannotFault(from))
                 || i.Op == MOp.XorPs && i.Operands[1] is MReg same && same.Id == ((MReg)i.Operands[0]).Id)
                && i.Operands[0] is MReg { IsXmm: true } to && (dead & (1 << to.XmmNumber)) != 0)
            {
                instrs.RemoveAt(k);
                continue;
            }
            if (i.Op is MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd or MOp.CallSafe)
            {
                dead = 0xFF;
                continue;
            }
            if (i.Op is MOp.Jcc or MOp.Jmp or MOp.JmpInd or MOp.JmpTable or MOp.Ret or MOp.Epilogue)
            {
                dead = 0;
                continue;
            }
            foreach ((MReg r, bool isDef) in Regs(i)) if (isDef && r.IsXmm) dead |= 1 << r.XmmNumber;
            foreach ((MReg r, bool isDef) in Regs(i)) if (!isDef && r.IsXmm) dead &= ~(1 << r.XmmNumber);
        }
    }

    // ---- mov r, 0 -> xor r, r ---------------------------------------------------------

    // A conditional move reads them as a jump does: `mov t, 0` between the
    // compare and the cmov it feeds (IfConvert) is never an `xor`.
    private static bool ReadsFlags(MInstr i) => i.Op is MOp.Jcc or MOp.Setcc or MOp.Cmov or MOp.Adc or MOp.Sbb;

    /// <summary>What may read the flags besides (a PUSHF, an asm block): to a rule that removes a flag setter, a reader.</summary>
    private static bool MayReadFlags(MInstr i) => ReadsFlags(i) || i.Op is MOp.ReadFlags or MOp.InlineAsm or MOp.Pushf;

    // ---- test after an instruction that set the flags --------------------------------

    /// <summary>
    /// `and r, x ; test r, r ; je` tests twice: the and already set ZF and SF
    /// from r, and cleared CF and OF as the test does, so the test changes no
    /// flag and goes. After add, sub, neg or a shift by a constant only ZF, SF
    /// and PF are the test's (CF and OF are the arithmetic's), so the test goes
    /// only where every reader before the next flag write asks equal, sign or
    /// parity. An allocation's `if ((x &amp; Mask) == 0)`, a countdown's
    /// `if (--n != 0)`: one instruction each. The readers must be in the
    /// block, as the selector always leaves them (no flag is carried across
    /// a block's end, nor into a call or out of a return, where the scan
    /// stops with the test gone); anything else not known to leave the
    /// flags alone keeps the test.
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
                if (after.Op is MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Nop or MOp.Jmp || Isa.FlagNeutralScalar(after.Op)) continue;
                // What changes none of the six flags and reads none: the
                // interrupt flag's own two, a push or a pop of a register or
                // of memory, a PAUSE. (`pushf ; pop r ; and r, 200h ; test
                // r, r ; setne ; ... ; cli` -- IrqSpinLock.Enter's first
                // lines -- kept its test for the CLI after it.)
                if (after.Op is MOp.Cli or MOp.Sti or MOp.Push or MOp.Pop or MOp.Pause) continue;
                // A call or a return reads no flag of its caller's.
                if (after.Op is MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.Ret or MOp.Epilogue) break;
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

    /// <summary>
    /// `mov eax, K ; lock cmpxchg [m], r ; test eax, eax ; jne` (K nought;
    /// `cmp eax, K` for another) ASKS WHAT THE CMPXCHG JUST SAID: it sets ZF
    /// where the word was K and r went in, and leaves in EAX what the word
    /// held -- K exactly then, something else exactly when ZF is clear. So
    /// "was it K" after it is ZF as it stands, and the test goes where every
    /// reader before the next flag write asks equal or not equal (the
    /// compare's other flags are of another subtraction). The lock taken
    /// uncontended -- `CompareExchange(ref word, me, 0) == 0`, every
    /// IrqSpinLock.Enter -- ran the test on its way through. Through one
    /// `mov t, eax` the answer was moved by, tested as t.
    /// </summary>
    private static void ExchangedTests(List<MInstr> instrs)
    {
        for (int k = 1; k < instrs.Count; k++)
        {
            MInstr exchange = instrs[k];
            if (exchange.Op != MOp.Cmpxchg || exchange.Width != 4 || exchange.Operands.Count != 2 || exchange.Operands[0] is not MMem
                || exchange.Operands[1] is not MReg { Id: not (int)Gpr.Eax }) continue;
            // What EAX was set to, just before: nothing between that names it.
            if (instrs[k - 1] is not { Op: MOp.Mov, Width: 4, Lock: false, Operands: [MReg { Id: (int)Gpr.Eax }, MImm { IsPlain: true } expected] }) continue;
            int at = k + 1;
            int answer = (int)Gpr.Eax;
            if (at < instrs.Count && instrs[at] is { Op: MOp.Mov, Width: 4, Lock: false, Operands: [MReg moved, MReg { Id: (int)Gpr.Eax }] } && moved.Id != (int)Gpr.Eax)
            {
                answer = moved.Id;
                at++;
            }
            if (at >= instrs.Count) continue;
            MInstr test = instrs[at];
            bool asks = test.Width == 4 && !test.Lock && test.Operands.Count == 2 && test.Operands[0] is MReg tested && (tested.Id == answer || tested.Id == (int)Gpr.Eax)
                && (test.Op == MOp.Test && expected.Value == 0 && test.Operands[1] is MReg again && again.Id == tested.Id
                    || test.Op == MOp.Cmp && test.Operands[1] is MImm { IsPlain: true } against && against.Value == expected.Value);
            if (!asks) continue;
            bool safe = true;
            for (int j = at + 1; j < instrs.Count && safe; j++)
            {
                MInstr after = instrs[j];
                if (after.Op is MOp.Jcc or MOp.Setcc or MOp.Cmov)
                {
                    if (after.Cond is not (Cond.E or Cond.Ne)) safe = false;
                    continue;
                }
                if (after.Op is MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Nop or MOp.Jmp) continue;
                if (after.Op is MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.Ret or MOp.Epilogue) break;
                if (WritesFlags(after) && !MayReadFlags(after)) break;
                safe = false;
            }
            if (safe) instrs.RemoveAt(at);
        }
    }

    /// <summary>
    /// `setcc t ; movzx r, t ; test r, r ; jne L` JUMPS ON THE FLAGS THE
    /// SETCC READ: neither the setcc nor the widening writes them, so the
    /// test asks again what is still standing, and the jump is `jcc L`
    /// (`je`: the opposite). A condition kept as a value and branched on
    /// at once -- `bool ok = a < b; if (ok)` -- 2,665 times over a kernel.
    /// Where nothing else reads the value, the setcc and the widening are
    /// dead and go after (DeadDefs).
    /// </summary>
    private static bool BranchOnSetFlags(List<MInstr> instrs)
    {
        bool any = false;
        for (int k = 2; k + 1 < instrs.Count; k++)
        {
            if (instrs[k] is not { Op: MOp.Test, Width: 4, Lock: false } test || test.Operands.Count != 2
                || test.Operands[0] is not MReg r || test.Operands[1] is not MReg r2 || r.Id != r2.Id
                || instrs[k + 1] is not { Op: MOp.Jcc, Cond: Cond.E or Cond.Ne } jump
                || instrs[k - 1] is not { Op: MOp.Movzx, Width: 1 } widen || widen.Operands.Count != 2
                || widen.Operands[0] is not MReg wide || wide.Id != r.Id || widen.Operands[1] is not MReg narrow
                || instrs[k - 2] is not { Op: MOp.Setcc } set || set.Operands.Count != 1 || set.Operands[0] is not MReg byteReg || byteReg.Id != narrow.Id)
                continue;
            // Only this jump reads the test's flags: the selector's shape,
            // and anything else after it in the block keeps the test.
            bool alone = true;
            for (int j = k + 2; j < instrs.Count && alone; j++)
            {
                if (MayReadFlags(instrs[j])) alone = false;
                else if (WritesFlags(instrs[j]) || instrs[j].Op is MOp.Call or MOp.CallInd or MOp.CallSafe) break;
            }
            if (!alone) continue;
            instrs[k + 1] = new MInstr(MOp.Jcc, jump.Operands[0]) { Cond = jump.Cond == Cond.Ne ? set.Cond : set.Cond.Negate() };
            instrs.RemoveAt(k);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// A LOAD THAT ONLY PROBES ITS BASE FOR NULL, RIGHT BEFORE AN ACCESS
    /// THAT DOES: `mov r, [b] ; mov r, [b+0x78]` -- the object's first word
    /// read so that a null receiver faults, and thrown away by the next
    /// instruction, which reads the same object within the null page and
    /// faults as surely. 1,703 over a kernel, each a read of the object's
    /// header, often on another cache line than the field wanted.
    /// </summary>
    private static bool ProbeBeforeAccess(List<MInstr> instrs)
    {
        bool any = false;
        for (int k = 0; k + 1 < instrs.Count; k++)
        {
            if (instrs[k] is not { Op: MOp.Mov, Width: 4, Lock: false } probe || probe.Operands.Count != 2
                || probe.Operands[0] is not MReg dead || probe.Operands[1] is not MMem { Device: false } probed
                || !NullPage(probed) || probed.Base is not MReg b || b.Id == dead.Id) continue;
            MInstr next = instrs[k + 1];
            // The load that follows writes the whole register (a narrow
            // `mov` would leave the probe's upper bytes to be read).
            if (next.Op is not (MOp.Mov or MOp.Movzx or MOp.Movsx) || next.Lock || next.Operands.Count != 2 || next.Op == MOp.Mov && next.Width != 4
                || next.Operands[0] is not MReg written || written.Id != dead.Id
                || next.Operands[1] is not MMem { Device: false } read || !NullPage(read) || read.Base is not MReg b2 || b2.Id != b.Id) continue;
            instrs.RemoveAt(k);
            k--;
            any = true;
        }
        return any;
    }

    /// <summary>A plain `[base + d]`, d within the page a null base faults in.</summary>
    private static bool NullPage(MMem m) => m.Base is not null && m.Index is null && m.Label is null && m.Symbol is null && m.Disp >= 0 && m.Disp < 4096 && !m.IsSpill;

    /// <summary>
    /// A TEST WHOSE FLAGS NOTHING READS, GONE -- and a conditional jump right
    /// after the same jump. `Sys.KeepAlive(x)` is selected as `test x, x`,
    /// a use of the register that keeps the object alive to that point for
    /// the allocator and the stack maps; allocation over, it is an
    /// instruction that does nothing, and every lock's Enter and Leave ran
    /// one or two (the kernel's IrqSpinLock, on every packet's path). So is
    /// the compare left when its branch was folded. Only a test or a compare
    /// of registers and constants -- one that reads memory may be a null
    /// check's fault -- and only where no reader follows in the block before
    /// the next write (the selector never carries flags across a block
    /// boundary, as ZeroWithXor relies on).
    /// </summary>
    private static void DeadTests(List<MInstr> instrs)
    {
        bool flagsLive = false;
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            if (MayReadFlags(i))
            {
                // `jcc L ; jcc L` on one condition: the second never jumps.
                if (i.Op == MOp.Jcc && k > 0 && instrs[k - 1] is { Op: MOp.Jcc } first && first.Cond == i.Cond
                    && first.Operands.Count == 1 && i.Operands.Count == 1
                    && first.Operands[0] is MLabel a && i.Operands[0] is MLabel b && ReferenceEquals(a.Target, b.Target))
                {
                    instrs.RemoveAt(k);
                    continue;
                }
                flagsLive = true;
            }
            else if (i.Op is MOp.Test or MOp.Cmp && !i.Lock && i.Operands.Count == 2
                     && i.Operands[0] is MReg && i.Operands[1] is MReg or MImm { IsPlain: true })
            {
                if (!flagsLive) instrs.RemoveAt(k);
                else flagsLive = false;
            }
            else if (WritesFlags(i) || i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe)
            {
                flagsLive = false;
            }
        }
    }

    /// <summary>
    /// Whether an instruction sets the flags whatever it is given: what a
    /// rule may take for the end of the flags before it. NOT A SHIFT BY A
    /// COUNT IN CL (nor a double shift by one): by nought it changes no
    /// flag, and what read the flags after it read the ones from before --
    /// taken for a writer, a test before such a shift went as dead, a
    /// register before it was zeroed with XOR, under a reader that still
    /// wanted them. Not a writer and not known to leave them alone, it ends
    /// every rule's scan the careful way.
    /// </summary>
    private static bool WritesFlags(MInstr i) => i.Op is MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd
        ? i.Operands.Count >= 2 && i.Operands[^1] is MImm { IsPlain: true, Value: >= 1 and <= 31 }
        : WritesFlagsAlways(i);

    private static bool WritesFlagsAlways(MInstr i) => i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or
        or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Imul or MOp.Imul3 or MOp.Mul or MOp.ImulWide or MOp.Div or MOp.Idiv or MOp.Neg
        or MOp.Xadd or MOp.Cmpxchg or MOp.LockOrEsp or MOp.Sahf
        or MOp.Fucomipp or MOp.UcomiF or MOp.Bsf or MOp.Bsr or MOp.WriteFlags or MOp.Cmpxchg8b or MOp.PackedSaveX87 or MOp.PackedRestoreX87 or MOp.PackedSaveFxsr;

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
            if (MayReadFlags(i))
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
                    MInstr rmw = new(op.Op, m, op.Operands[1]) { Width = 4, Line = op.Line, Probed = op.Probed, OwnsState = op.OwnsState, Native = op.Native };
                    instrs.RemoveRange(k - 2, 3);
                    instrs.Insert(k - 2, rmw);
                    k -= 2;
                    i = rmw;
                }
            }
            // `mov r, [m] ; test r, r` with r dead after the test IS `cmp
            // dword [m], 0`: the same read (and the same fault, where m is a
            // null receiver's), ZF, SF and PF of the same word, CF and OF
            // clear either way; one instruction for two, a byte less off the
            // frame, and no register written. A spilled value tested and
            // branched on -- 6,600 times off EBP in the K8 kernel into EAX
            // alone -- and a field read only to ask whether it is null.
            //
            // Not on the Pentium where m has a displacement, as a frame
            // slot's has: an instruction with a displacement and an immediate
            // pairs with nothing there (Agner Fog, "Pentium", imperfect
            // pairing), two clocks alone against a load and a test that each
            // pair -- the test with the jump after it.
            if (k >= 1 && i.Op == MOp.Test && i.Width == 4 && !i.Lock && i.Operands.Count == 2
                && i.Operands[0] is MReg { IsPhys: true } t0 && i.Operands[1] is MReg t1 && t0.Id == t1.Id && t0.Id < 8
                && (dead & (1 << t0.Id)) != 0
                && instrs[k - 1] is { Lock: false } reload && IsMov(reload) && reload.Operands.Count == 2
                && reload.Operands[0] is MReg into && into.Id == t0.Id && reload.Operands[1] is MMem asked
                && !(CpuModel.P5 && (asked.Disp != 0 || asked.Symbol is not null || asked.Label is not null)))
            {
                MInstr compare = new(MOp.Cmp, asked, new MImm(0)) { Width = 4, Line = reload.Line, Probed = reload.Probed, OwnsState = reload.OwnsState, Native = reload.Native };
                instrs.RemoveRange(k - 1, 2);
                instrs.Insert(k - 1, compare);
                k -= 1;
                i = compare;
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

    // ---- a count tested as it was before a step ------------------------------------

    /// <summary>
    /// `while (n-- > 0)`: `mov t, n ; dec n ; test t, t ; jg L` -- the old
    /// value kept to be tested, and a register for it -- with t dead after
    /// the test, is `dec n ; jge L`: DEC sets SF, ZF and OF from n - 1, and
    /// n > 0 is n - 1 >= 0 as JGE reads it (SF = OF), the one value it
    /// wraps from (n the least int, n - 1 overflowing, OF set) included; n
    /// <= 0 is JL. Stepping up, n < 0 is n + 1 <= 0 (JLE) and n >= 0 is JG.
    /// Two instructions and a register of every lap of such a loop. Only
    /// where nothing after the jump reads the flags the test set.
    /// </summary>
    private static void PostStepTest(List<MInstr> instrs, int liveOut, Func<MInstr, int> jumped)
    {
        int dead = ~liveOut & 0xFF;
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            dead &= ~jumped(i);
            // dead says what holds after instruction k.
            if (k >= 2 && k + 1 < instrs.Count && i.Op == MOp.Test && i.Width == 4 && i.Operands.Count == 2
                && i.Operands[0] is MReg { IsPhys: true } t && i.Operands[1] is MReg t2 && t2.Id == t.Id && t.Id < 8
                && (dead & (1 << t.Id)) != 0
                && instrs[k + 1] is { Op: MOp.Jcc } jump
                && (k + 2 >= instrs.Count || !ReadsFlags(instrs[k + 2]) && Understood(instrs[k + 2]))
                && instrs[k - 1] is var step && Step(step) is var (x, down) && x.Id != t.Id
                && instrs[k - 2] is var copy && IsMov(copy) && copy.Operands[0] is MReg c0 && c0.Id == t.Id
                && copy.Operands[1] is MReg c1 && c1.Id == x.Id
                && (down ? jump.Cond is Cond.G or Cond.Le : jump.Cond is Cond.L or Cond.Ge))
            {
                Cond after = jump.Cond switch { Cond.G => Cond.Ge, Cond.Le => Cond.L, Cond.L => Cond.Le, _ => Cond.G };
                instrs[k + 1] = new MInstr(MOp.Jcc, jump.Operands[0]) { Cond = after, Line = jump.Line };
                instrs.RemoveAt(k);
                instrs.RemoveAt(k - 2);
                k -= 2;
                dead = 0;
                continue;
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
        }

        // A step of one on a register: which, and whether down.
        static (MReg Reg, bool Down)? Step(MInstr i)
        {
            if (i.Width != 4 || i.Lock || i.Operands.Count == 0 || i.Operands[0] is not MReg { IsPhys: true } r || r.Id >= 8) return null;
            if (i.Op == MOp.Dec && i.Operands.Count == 1) return (r, true);
            if (i.Op == MOp.Inc && i.Operands.Count == 1) return (r, false);
            if (i.Op is MOp.Sub or MOp.Add && i.Operands.Count == 2 && i.Operands[1] is MImm { IsPlain: true } one && one.Value is 1 or -1)
                return (r, (i.Op == MOp.Sub) == (one.Value == 1));
            return null;
        }
    }

    // ---- an operation made where its result is wanted ------------------------------

    /// <summary>
    /// `op c, a ; mov a, c` with op commutative (add, and, or, xor, imul)
    /// and c dead after the move: `op a, c` -- the result made where it is
    /// wanted, a move fewer. And then `mov c, [m] ; op a, c` with c dead
    /// after: `op a, [m]`, the memory read by the operation itself (one
    /// instruction and a register; the same read, and its fault, if any, at
    /// the same place). A return of `t[i] + v` was `mov ecx, [...] ; add ecx,
    /// eax ; mov eax, ecx`, and is `add eax, [...]`.
    /// </summary>
    private static void OperateInPlace(List<MInstr> instrs, int liveOut, Func<MInstr, int> jumped)
    {
        int count = instrs.Count;
        if (count < 2) return;
        int[] after = new int[count];
        int live = liveOut & 0xFF;
        for (int k = count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            live |= jumped(i);
            after[k] = live;
            if (!Understood(i)) { live = 0xFF; continue; }
            Masks(i, out long reads, out long writes);
            live = (live & ~(int)writes | (int)reads) & 0xFF;
        }
        bool[] gone = new bool[count];
        for (int k = 0; k + 1 < count; k++)
        {
            MInstr op = instrs[k], move = instrs[k + 1];
            if (gone[k] || op.Op is not (MOp.Add or MOp.And or MOp.Or or MOp.Xor or MOp.Imul) || op.Width != 4 || op.Lock || op.Operands.Count != 2
                || op.Operands[0] is not MReg { IsPhys: true } c || op.Operands[1] is not MReg { IsPhys: true } a || c.Id >= 8 || a.Id >= 8 || c.Id == a.Id
                || !IsMov(move) || move.Operands[0] is not MReg m0 || m0.Id != a.Id || move.Operands[1] is not MReg m1 || m1.Id != c.Id
                || (after[k + 1] & (1 << c.Id)) != 0) continue;
            // The flags: the move sets none, so what reads them after reads
            // the same operation's, made into the other register.
            MOperand source = c;
            int line = op.Line;
            bool probed = op.Probed, native = op.Native;
            byte owns = op.OwnsState;
            int load = k - 1;
            while (load >= 0 && gone[load]) load--;
            // (Read before a is written, so an address through a is as good.)
            if (load >= 0 && IsMov(instrs[load]) && instrs[load].Operands[0] is MReg l0 && l0.Id == c.Id && instrs[load].Operands[1] is MMem mem
                && mem.Base?.Id != c.Id && mem.Index?.Id != c.Id && !instrs[load].Lock)
            {
                source = mem;
                // A fault on the read is said at the read's line.
                line = instrs[load].Line;
                // And what the read was marked: it is this instruction's now.
                probed |= instrs[load].Probed;
                owns = Math.Max(owns, instrs[load].OwnsState);
                native |= instrs[load].Native;
                gone[load] = true;
            }
            instrs[k] = new MInstr(op.Op, a, source) { Width = 4, Line = line, Probed = probed, OwnsState = owns, Native = native };
            gone[k + 1] = true;
        }
        for (int k = count - 1; k >= 0; k--) if (gone[k]) instrs.RemoveAt(k);
    }

    // ---- a copy whose source dies, the copy's register renamed ----------------------

    /// <summary>
    /// `mov b, a` where a is dead after it: the value is the same in both, and
    /// a is free. Where b's value then lives out its life in this block -- no
    /// jump with it live, no call (the collector's map of a call says which
    /// registers hold references), nothing that names a, nothing that names
    /// b implicitly (MUL's EAX, REP MOVS's ESI), nothing not understood -- b
    /// is a in all of it and the move goes: what a coalescing allocator does
    /// for a copy whose ends do not interfere, done on the registers it
    /// chose. A quarter of the K8 kernel's 216,000 register moves have a
    /// source that dies there. Not where b is read as a byte and a has no
    /// byte form (ESI, EDI); never ESP or EBP.
    /// </summary>
    private static void RenameCopies(List<MInstr> instrs, int liveOut, Func<MInstr, int> jumped)
    {
        // One a round, the liveness read again after each: 64 a block at most.
        for (int round = 0; round < 64 && RenameCopy(instrs, liveOut, jumped); round++) { }
    }

    private static bool RenameCopy(List<MInstr> instrs, int liveOut, Func<MInstr, int> jumped)
    {
        int count = instrs.Count;
        if (count < 2) return false;
        int[] after = new int[count];
        int live = liveOut & 0xFF;
        for (int k = count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            live |= jumped(i);
            after[k] = live;
            if (!Understood(i)) { live = 0xFF; continue; }
            Masks(i, out long reads, out long writes);
            live = (live & ~(int)writes | (int)reads) & 0xFF;
        }
        const int ByteForm = (1 << (int)Gpr.Eax) | (1 << (int)Gpr.Ecx) | (1 << (int)Gpr.Edx) | (1 << (int)Gpr.Ebx);
        for (int k = 0; k + 1 < instrs.Count; k++)
        {
            MInstr copy = instrs[k];
            if (!IsMov(copy) || copy.Lock || copy.Operands[0] is not MReg { IsPhys: true } b || copy.Operands[1] is not MReg { IsPhys: true } a
                || a.Id >= 8 || b.Id >= 8 || a.Id == b.Id || a.Id is (int)Gpr.Esp or (int)Gpr.Ebp || b.Id is (int)Gpr.Esp or (int)Gpr.Ebp
                || (after[k] & (1 << a.Id)) != 0) continue;
            long bitA = 1L << a.Id, bitB = 1L << b.Id;
            // b's value from here to where it dies, every instruction of it.
            int end = -1;
            bool ok = true;
            for (int j = k + 1; j < instrs.Count && ok; j++)
            {
                MInstr i = instrs[j];
                if (!Understood(i) || i.Op is MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd or MOp.CallSafe
                    or MOp.InlineAsm or MOp.Jmp or MOp.JmpTable or MOp.JmpInd or MOp.Epilogue) { ok = false; break; }
                Masks(i, out long reads, out long writes);
                if (((reads | writes) & bitA) != 0) { ok = false; break; }
                foreach (Gpr g in Roles.ImplicitUses(i)) if ((int)g == b.Id) ok = false;
                foreach (Gpr g in Roles.ImplicitDefs(i)) if ((int)g == b.Id) ok = false;
                if (!ok) break;
                bool readsB = (reads & bitB) != 0, writesB = (writes & bitB) != 0;
                // A new value of b: the old one's life ended before it.
                if (writesB && !readsB) { end = j - 1; break; }
                if (i.Op == MOp.Jcc && (jumped(i) & (int)bitB) != 0) { ok = false; break; }
                // A register an instruction's form fixes: a shift's count (CL),
                // a port's DX or AL, an exchange's pair.
                if ((readsB || writesB) && (i.Op is MOp.In or MOp.Out or MOp.Xchg or MOp.Cmpxchg or MOp.Xadd
                    || i.Op is MOp.Shl or MOp.Shr or MOp.Sar or MOp.Ror or MOp.Shld or MOp.Shrd && i.Operands.Count > 0 && i.Operands[^1] is MReg)) { ok = false; break; }
                // Where a has no byte form, b only as a whole word.
                if ((readsB || writesB) && (ByteForm & (int)bitA) == 0 && (i.Width != 4 || i.Op is MOp.Movzx or MOp.Movsx or MOp.Setcc)) { ok = false; break; }
                if ((after[j] & (int)bitB) == 0) { end = j; break; }
            }
            if (!ok || end < 0) continue;
            for (int j = k + 1; j <= end; j++) instrs[j] = Renamed(instrs[j], b.Id, a);
            instrs.RemoveAt(k);
            return true;
        }
        return false;
    }

    /// <summary>The same instruction with register `from` named `to` in every operand (a register, a memory operand's base or index).</summary>
    private static MInstr Renamed(MInstr i, int from, MReg to)
    {
        bool any = false;
        MOperand[] ops = new MOperand[i.Operands.Count];
        for (int o = 0; o < ops.Length; o++)
        {
            MOperand op = i.Operands[o];
            if (op is MReg { IsPhys: true } r && r.Id == from) { op = to; any = true; }
            else if (op is MMem mem && (mem.Base?.Id == from || mem.Index?.Id == from))
            {
                op = new MMem(mem.Base?.Id == from ? to : mem.Base, mem.Disp)
                {
                    Index = mem.Index?.Id == from ? to : mem.Index, Scale = mem.Scale, Symbol = mem.Symbol, Reloc = mem.Reloc,
                    Label = mem.Label, IsSpill = mem.IsSpill, Device = mem.Device, Aligned = mem.Aligned,
                };
                any = true;
            }
            ops[o] = op;
        }
        if (!any) return i;
        MInstr made = new(i.Op) { Width = i.Width, Cond = i.Cond, Lock = i.Lock, Table = i.Table, CallReloc = i.CallReloc, Line = i.Line, Native = i.Native, Probed = i.Probed, OwnsState = i.OwnsState, Asm = i.Asm, ZeroFirst = i.ZeroFirst };
        made.Operands.AddRange(ops);
        return made;
    }

    // ---- a copy and an operation on it, one LEA -----------------------------------

    /// <summary>
    /// `mov d, a ; add d, b` (b a register, d itself -- a doubled, which a
    /// `shl d, 1` is by now, ChooseForCpu -- or a number), `sub d, n`, `inc
    /// d` or `dec d`, with the flags the second sets read by nothing after
    /// it: `lea d, [a + b]` (`[a + a]`, `[a + n]`, `[a - n]`, `[a + 1]`, `[a -
    /// 1]`). x86 has no three-operand add but the address unit's: one
    /// instruction for two -- a clock on the 486 for a number, two with an
    /// index (as the pair's two, a byte less), paired on the Pentium, one on
    /// the K8 -- two bytes less for a number added. 27,000 such pairs in the
    /// K8 kernel. Not from EBP (a frame's address, which FrameCompact would
    /// take for a slot), and on the Pentium not the register form where the
    /// instruction before writes a register it adds: an address generation
    /// interlock where the move and the add paired.
    /// </summary>
    private static void CopyThenOperate(List<MInstr> instrs)
    {
        for (int k = 0; k + 1 < instrs.Count; k++)
        {
            MInstr copy = instrs[k], op = instrs[k + 1];
            if (!IsMov(copy) || copy.Operands[0] is not MReg { IsPhys: true } d || copy.Operands[1] is not MReg { IsPhys: true } a
                || d.Id >= 8 || a.Id >= 8 || d.Id == a.Id || d.Id == (int)Gpr.Esp || op.Width != 4 || op.Lock
                || op.Operands.Count == 0 || op.Operands[0] is not MReg o0 || o0.Id != d.Id || !FlagsDeadAfter(instrs, k + 1)) continue;
            if (a.Id == (int)Gpr.Ebp) continue;
            MMem? address = op.Op switch
            {
                // d itself: a doubled.
                MOp.Add when op.Operands[1] is MReg { IsPhys: true } same && same.Id == d.Id && a.Id != (int)Gpr.Esp
                    => new MMem(MReg.Of((Gpr)a.Id), 0) { Index = MReg.Of((Gpr)a.Id), Scale = 1 },
                // ESP added: the base (it may be no index).
                MOp.Add when op.Operands[1] is MReg { IsPhys: true, Id: (int)Gpr.Esp } && a.Id != (int)Gpr.Esp
                    => new MMem(MReg.Of(Gpr.Esp), 0) { Index = MReg.Of((Gpr)a.Id), Scale = 1 },
                MOp.Add when op.Operands[1] is MReg { IsPhys: true } b && b.Id < 8 && b.Id != (int)Gpr.Esp
                    => new MMem(MReg.Of((Gpr)a.Id), 0) { Index = MReg.Of((Gpr)b.Id), Scale = 1 },
                MOp.Add when op.Operands[1] is MImm { IsPlain: true } n && n.Value is >= int.MinValue and <= int.MaxValue
                    => new MMem(MReg.Of((Gpr)a.Id), (int)n.Value),
                MOp.Sub when op.Operands[1] is MImm { IsPlain: true } m && m.Value is > int.MinValue and <= int.MaxValue
                    => new MMem(MReg.Of((Gpr)a.Id), (int)-m.Value),
                MOp.Inc when op.Operands.Count == 1 => new MMem(MReg.Of((Gpr)a.Id), 1),
                MOp.Dec when op.Operands.Count == 1 => new MMem(MReg.Of((Gpr)a.Id), -1),
                _ => null,
            };
            if (address is null) continue;
            if (CpuModel.P5 && address.Index is not null && k > 0)
            {
                Masks(instrs[k - 1], out _, out long written);
                if ((written & ((1L << address.Base!.Id) | (1L << address.Index.Id))) != 0) continue;
            }
            instrs[k] = new MInstr(MOp.Lea, d, address) { Width = 4, Line = op.Line };
            instrs.RemoveAt(k + 1);
        }
    }

    /// <summary>Whether nothing reads the flags instruction `at` leaves before something sets them again (or the block ends: no flag is live into a block here).</summary>
    private static bool FlagsDeadAfter(List<MInstr> instrs, int at)
    {
        for (int j = at + 1; j < instrs.Count; j++)
        {
            MInstr i = instrs[j];
            if (ReadsFlags(i) || !Understood(i)) return false;
            if (WritesFlags(i) || i.Op is MOp.Jmp or MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.Epilogue) return true;
            if (i.Op is MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Push or MOp.Pop or MOp.Nop or MOp.Inc or MOp.Dec) continue;
            return false;
        }
        return true;
    }

    // ---- one block's only way in, the end of another ----------------------------

    /// <summary>
    /// A block ending `jmp C` where nothing else reaches C -- no other jump,
    /// no fall-through, no table, its address not taken, no landing pad: C's
    /// instructions are the block's, in the jump's place (and a jump to what
    /// C fell into, where that is not now the block's own next), and C
    /// goes. And a block nothing reaches but its own fall-through from the
    /// one before is that one's tail. The layout
    /// leaves such ones: a loop's way out jumping to the function's last
    /// block, `mov ecx, esi ; jmp X` and X `mov eax, ecx ; ... ret`, two
    /// moves and a jump where, made one block, ForwardCopies leaves one move.
    /// </summary>
    private static void MergeSoleJumps(MFunction m)
    {
        for (int round = 0; round < 8; round++)
        {
            int n = m.Blocks.Count;
            Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
            for (int b = 0; b < n; b++) index[m.Blocks[b]] = b;
            int[] ways = new int[n];
            bool[] pinned = new bool[n];
            pinned[0] = true;
            List<MBlock> next = new();
            for (int b = 0; b < n; b++)
            {
                MBlock block = m.Blocks[b];
                if (block.Source is { IsLandingPad: true }) pinned[b] = true;
                foreach (MInstr i in block.Instrs)
                    foreach (MOperand o in i.Operands)
                    {
                        if (o is MImm { Label: { } taken } && index.TryGetValue(taken, out int t)) pinned[t] = true;
                        else if (o is MMem { Label: { } table } && index.TryGetValue(table, out int u)) pinned[u] = true;
                    }
                foreach (MInstr i in block.Instrs)
                    if (i.Op == MOp.JmpTable && i.Table is { } targets)
                        foreach (MBlock to in targets) if (index.TryGetValue(to, out int v)) pinned[v] = true;
                m.SuccessorsInto(b, next);
                foreach (MBlock to in next) if (index.TryGetValue(to, out int w)) ways[w]++;
            }
            HashSet<MBlock> emptied = new(ReferenceEqualityComparer.Instance);
            // A FALL-THROUGH ONLY: the next block, which no jump names and
            // nothing else reaches, is this one's tail -- where a value is
            // handed on (a variable a loop writes, copied at its way in)
            // after the block that made it.
            for (int b = 0; b + 1 < n; b++)
            {
                MBlock block = m.Blocks[b], after = m.Blocks[b + 1];
                if (emptied.Contains(block) || emptied.Contains(after) || block.EndsUnconditionally || pinned[b + 1] || ways[b + 1] != 1
                    || after.Cold != block.Cold
                    || after.Instrs.Count == 0 || block.Instrs.Any(i => i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to } && ReferenceEquals(to, after))) continue;
                block.Instrs.AddRange(after.Instrs);
                after.Instrs.Clear();
                emptied.Add(after);
                ways[b + 1] = 0;
                pinned[b + 1] = true;
            }
            for (int b = 0; b < n; b++)
            {
                if (emptied.Contains(m.Blocks[b])) continue;
                List<MInstr> instrs = m.Blocks[b].Instrs;
                if (instrs.Count == 0 || instrs[^1] is not { Op: MOp.Jmp } jump || jump.Operands.Count == 0
                    || jump.Operands[0] is not MLabel { Target: var target } || !index.TryGetValue(target, out int c)
                    || c == b || pinned[c] || ways[c] != 1 || target.Instrs.Count == 0
                    // (Not across the layout's line between the hot code
                    // and the cold: a throw's block stays out of the way.)
                    || target.Cold != m.Blocks[b].Cold) continue;
                // C falls into the block after it: that way kept, by a jump
                // there -- none where C is this block's own next (the jump
                // to it was the encoder's to drop), and C's next is then
                // this block's. A C that falls off the end is no C to take.
                MBlock? onward = null;
                if (!target.EndsUnconditionally)
                {
                    if (c + 1 >= n || emptied.Contains(m.Blocks[c + 1])) continue;
                    if (c != b + 1) onward = m.Blocks[c + 1];
                }
                // Not one the block's own instructions jump to on the way (a
                // conditional jump to C before the jmp counts a way of its own).
                if (instrs.Take(instrs.Count - 1).Any(i => i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to } && ReferenceEquals(to, target))) continue;
                instrs.RemoveAt(instrs.Count - 1);
                instrs.AddRange(target.Instrs);
                if (onward is not null) instrs.Add(new MInstr(MOp.Jmp, new MLabel(onward)) { Line = jump.Line });
                target.Instrs.Clear();
                emptied.Add(target);
                // C is empty now, and goes at the round's end.
                ways[c] = 0;
                pinned[c] = true;
            }
            if (emptied.Count == 0) return;
            // The blocks emptied here out before the next round counts (an
            // empty block of the layout's own falls through, and stays).
            m.Blocks.RemoveAll(emptied.Contains);
        }
    }

    // ---- a value copied twice, copied once --------------------------------------

    /// <summary>
    /// `mov r1, X ; ... ; mov r2, r1` with r1 read nowhere between and dead
    /// after the second, and X -- a register or a number -- written nowhere
    /// between: `mov r2, X`, the first gone. A number the arguments pass
    /// wrote into a parameter (`mov ecx, 100 ; ... ; mov esi, ecx`), a value
    /// handed through a register a join no longer stands between
    /// (MergeSoleJumps: `mov ecx, esi ; mov eax, ecx`).
    /// </summary>
    private static void ForwardCopies(List<MInstr> instrs, int liveOut, Func<MInstr, int> jumped)
    {
        int count = instrs.Count;
        if (count < 2) return;
        // What is live after each instruction.
        int[] after = new int[count];
        int live = liveOut & 0xFF;
        for (int k = count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
            live |= jumped(i);
            after[k] = live;
            if (!Understood(i)) { live = 0xFF; continue; }
            Masks(i, out long reads, out long writes);
            live = (live & ~(int)writes | (int)reads) & 0xFF;
        }
        bool[] gone = new bool[count];
        for (int j = 1; j < count; j++)
        {
            MInstr second = instrs[j];
            // (Never ESP or EBP on either side: a push or a pop moves ESP
            // with no operand that says so.)
            if (!IsMov(second) || second.Operands[0] is not MReg { IsPhys: true } r2 || second.Operands[1] is not MReg { IsPhys: true } r1
                || r1.Id >= 8 || r2.Id >= 8 || r1.Id == r2.Id || (after[j] & (1 << r1.Id)) != 0
                || r1.Id is (int)Gpr.Esp or (int)Gpr.Ebp || r2.Id is (int)Gpr.Esp or (int)Gpr.Ebp) continue;
            int k = j - 1;
            for (; k >= 0; k--)
            {
                MInstr i = instrs[k];
                if (gone[k]) continue;
                if (!Understood(i) || i.Op is MOp.Jcc or MOp.Jmp or MOp.JmpTable) { k = -1; break; }
                Masks(i, out long reads, out long writes);
                if ((writes & (1L << r1.Id)) != 0) break;
                if ((reads & (1L << r1.Id)) != 0) { k = -1; break; }
            }
            if (k < 0) continue;
            MInstr first = instrs[k];
            if (!IsMov(first) || first.Operands[0] is not MReg f0 || f0.Id != r1.Id) continue;
            MOperand x = first.Operands[1];
            if (x is MReg { IsPhys: true } xr)
            {
                if (xr.Id >= 8 || xr.Id is (int)Gpr.Esp or (int)Gpr.Ebp) continue;
                bool written = false;
                for (int b = k + 1; b < j && !written; b++)
                {
                    if (gone[b]) continue;
                    Masks(instrs[b], out _, out long w);
                    written = (w & (1L << xr.Id)) != 0;
                }
                if (written) continue;
                if (xr.Id == r2.Id) { gone[j] = true; gone[k] = true; continue; }
            }
            else if (x is not MImm) continue;
            instrs[j] = new MInstr(MOp.Mov, r2, x) { Width = 4, Line = second.Line };
            gone[k] = true;
        }
        for (int k = count - 1; k >= 0; k--) if (gone[k]) instrs.RemoveAt(k);
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
                if (after.Op is MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.Ret or MOp.Epilogue) break;
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
    ///   superset dependency (AMD 21924), and so does the Geode LX, where it is
    ///   one clock (AMD 33234, Table 8-26).
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
            if (MayReadFlags(i)) flagsLive = true;
            else if (WritesFlags(i) || i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe) flagsLive = false;
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
