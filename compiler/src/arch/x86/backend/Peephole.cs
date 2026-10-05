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
        int[] liveOut = LiveOut(m);
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            MBlock block = m.Blocks[b];
            MBlock? next = b + 1 < m.Blocks.Count ? m.Blocks[b + 1] : null;
            ForwardStoreLoad(block.Instrs);
            ForwardSpillLoads(block.Instrs);
            DeadDefs(block.Instrs, liveOut[b]);
            RepeatedStores(block.Instrs, liveOut[b], Usable(m));
            MergePops(block.Instrs);
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
    private static int[] LiveOut(MFunction m)
    {
        int nb = m.Blocks.Count;
        int[] use = new int[nb];
        int[] def = new int[nb];
        int[] live = new int[nb];
        Dictionary<MBlock, int> index = new();
        for (int b = 0; b < nb; b++)
        {
            index[m.Blocks[b]] = b;
        }
        for (int b = 0; b < nb; b++)
        {
            foreach (MInstr i in m.Blocks[b].Instrs)
            {
                if (!Understood(i))
                {
                    // Everything it might read is read; the pessimistic view.
                    use[b] |= 0xFF & ~def[b];
                    continue;
                }
                Masks(i, out long reads, out long writes);
                use[b] |= (int)reads & ~def[b];
                def[b] |= (int)writes;
            }
        }
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
                int o = 0;
                foreach (int t in succ[b])
                {
                    o |= use[t] | (live[t] & ~def[t]);
                }
                if (o != live[b])
                {
                    live[b] = o;
                    changed = true;
                }
            }
        }
        return live;
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

    private static void RepeatedStores(List<MInstr> instrs, int liveOut, int usable)
    {
        int[] deadBefore = new int[instrs.Count];
        int dead = ~liveOut & 0xFF & ~(1 << (int)Gpr.Esp) & ~(1 << (int)Gpr.Ebp);
        for (int k = instrs.Count - 1; k >= 0; k--)
        {
            MInstr i = instrs[k];
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

    private static void DeadDefs(List<MInstr> instrs, int liveOut)
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
            if (!Understood(c))
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
