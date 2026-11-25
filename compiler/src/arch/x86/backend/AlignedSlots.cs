#nullable enable
using Corsac.Lang.Frames;

namespace Corsac.Lang.X86;

/// <summary>
/// PACKED128 SPILL SLOTS SIXTEEN-BYTE ALIGNED, moved by MOVAPS. This
/// backend's calls keep ESP aligned to four only, so no EBP offset is
/// sixteen-aligned; MOVUPS is twice MOVAPS's work on the targets that have
/// SSE -- four uops to load and four to store on the Pentium III against two,
/// four and five macro-ops on the K8 against two (Agner Fog's instruction
/// tables) -- and Generate4's quarter rounds spill a vector every few
/// instructions. So the prologue, after its pushes, takes the slots' bytes
/// more and aligns ESP down (`sub esp, n ; and esp, -16`) and the slots are
/// addressed off ESP; the epilogue goes back to the saved registers by EBP
/// (`lea esp, [ebp - k]`), which sit where the collector and the unwinder
/// read them, at EBP minus the frame minus their pushes.
///
/// ESP MOVES BY KNOWN AMOUNTS BETWEEN THE PROLOGUE AND THE EPILOGUE -- a
/// call's arguments pushed and taken off -- and each slot is addressed off ESP
/// with what it has moved by there (Depths), as LLVM's frame-index elimination
/// adds a push sequence's SP adjustment: so a function that passes stack
/// arguments keeps its slots aligned too. Not where ESP moves by anything
/// else (Depths says what).
///
/// IN A FUNCTION WITH A HANDLER TOO: every way into a landing pad lands with
/// the aligned ESP. A throw reaches one by the handler chain alone
/// (SelectUnwind), which sets ESP from the record, and the record's ESP is
/// read inline where the try opens (Lowering.PushHandler, an iterator's or
/// async body's on each resume), after the prologue aligned it and at depth 0
/// -- Depths refuses a function with handlers that reads ESP anywhere else. A fault that becomes an exception (the
/// CORSAC kernel's UserAccessFault) is thrown and goes by the chain the same
/// way; a fault the kernel answers itself (Arch.Recover, a page mapped)
/// resumes the instruction with its own ESP.
/// Last but FrameCompact, which then packs the EBP frame without them, and
/// FramePointerFree. A leaf that gives EBP a value is left alone; any other
/// leaf given aligned slots has a frame again (MFunction.Frameless off): its
/// `and esp, -16` leaves ESP no fixed distance below the return address,
/// and EBP is the frame's base, as in a function that calls.
/// </summary>
internal static class AlignedSlots
{
    public static void Run(MFunction m)
    {
        if (m.XmmSpillWidth != 16 || m.FramePointerFree) return;
        static bool Spill(MOperand o, out MMem mem)
        {
            mem = null!;
            if (o is not MMem { IsSpill: true, Base: { IsPhys: true, Phys: Gpr.Ebp }, Index: null, Symbol: null, Label: null } x) return false;
            mem = x;
            return true;
        }
        SortedSet<int> slots = new();
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                if (i.Op == MOp.MovF && i.Width == 16 && i.Operands.Count == 2)
                    foreach (MOperand o in i.Operands)
                        if (Spill(o, out MMem mem)) slots.Add(mem.Disp);
        if (slots.Count == 0) return;
        // Every spill operand inside a slot names its start: the whole slot moves.
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach (MOperand o in i.Operands)
                    if (Spill(o, out MMem mem) && !slots.Contains(mem.Disp) && Inside(slots, mem.Disp)) return;
        foreach (Safepoint point in m.Safepoints.Values)
            foreach (int offset in point.SlotOffsets)
                if (slots.Contains(offset)) return;
        // A frame of under a page stays under one: nothing probes the stack,
        // and a frame of a page or more could step over a guard page. Counted
        // with the slots still in Frame.Size, so twice: on the safe side.
        if (m.Frame.Size + 4 * m.SavedRegs.Count + 16 * slots.Count + 15 >= 4096 && m.Frame.Size + 4 * m.SavedRegs.Count < 4096) return;
        // WHERE ESP IS, as bytes below where the prologue left it, before
        // each instruction (Depths): what a slot's offset off ESP adds there.
        // Every instruction naming a slot has one, or nothing moves: an
        // optimisation refuses, it never fails the compile.
        Dictionary<MInstr, int>? depth = Depths(m);
        if (depth is null) return;
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach (MOperand o in i.Operands)
                    if (Spill(o, out MMem mem) && slots.Contains(mem.Disp) && !depth.ContainsKey(i)) return;
        Dictionary<int, int> at = new();
        foreach (int disp in slots) at[disp] = 16 * at.Count;
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (Spill(i.Operands[k], out MMem mem) && at.TryGetValue(mem.Disp, out int to))
                    {
                        i.Operands[k] = new MMem(MReg.Of(Gpr.Esp), to + depth[i]) { IsSpill = true, Aligned = true };
                    }
        m.AlignedArea = 16 * at.Count;
        m.Frameless = false;
        m.Frame.Release(slots);
        foreach (MBlock b in m.Blocks) FoldReloads(b.Instrs, depth);
    }

    /// <summary>
    /// HOW FAR BELOW THE PROLOGUE'S ESP EACH INSTRUCTION RUNS, in bytes, or
    /// null where that is not one number: what LLVM's frame-index elimination
    /// calls the SP adjustment of a push sequence. A call's arguments pushed
    /// (PUSH, `sub esp, n`) and taken off after it (`add esp, n`, POP) move it
    /// by known amounts, so an aligned slot ESP-based at the prologue is still
    /// ESP-based at every instruction, its displacement that much more. Each
    /// block's in-depth is the same along every edge into it, the entry's 0
    /// and a landing pad's 0 (the handler record's ESP is the aligned one,
    /// read where the try opened); each jump's target at the depth at the jump.
    /// An inlined throw's `mov esp, [record]` on its way to a handler's
    /// indirect jump ends its path there (the handler is a landing pad, at 0).
    /// Null for anything else that moves ESP -- `and esp`, any other move into
    /// it, a negative depth, ESP read as a value at a nonzero depth in a
    /// function with handlers, a call into C (which may take its own
    /// arguments off, stdcall), a return mid-body, an asm block that needs the
    /// frame -- and for two edges that disagree.
    /// </summary>
    private static Dictionary<MInstr, int>? Depths(MFunction m)
    {
        Dictionary<MInstr, int> depth = new(ReferenceEqualityComparer.Instance);
        Dictionary<MBlock, int> into = new(ReferenceEqualityComparer.Instance);
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < m.Blocks.Count; k++) index[m.Blocks[k]] = k;
        Stack<MBlock> work = new();
        void Enter(MBlock b, int d, ref bool bad)
        {
            if (into.TryGetValue(b, out int had)) { if (had != d) bad = true; return; }
            into[b] = d;
            work.Push(b);
        }
        bool bad = false;
        if (m.Blocks.Count > 0) Enter(m.Blocks[0], 0, ref bad);
        foreach (MBlock b in m.Blocks)
            if (b.Source?.IsLandingPad == true) Enter(b, 0, ref bad);
        bool pads = m.Blocks.Any(b => b.Source?.IsLandingPad == true);
        while (work.Count > 0 && !bad)
        {
            MBlock b = work.Pop();
            int d = into[b];
            // ESP SET FROM A HANDLER RECORD on the way to a handler -- an
            // inlined throw's `mov esp, [rec+8] ; mov ebp, [rec+12] ; jmp
            // [rec+4]` -- leaves by the indirect jump for a landing pad, which
            // is entered at depth 0 (rooted above): nothing after it in the
            // block may name ESP or a slot, and the block goes nowhere else.
            bool leaving = false;
            foreach (MInstr i in b.Instrs)
            {
                if (leaving)
                {
                    if (i.Op == MOp.JmpInd) break;
                    foreach (MOperand o in i.Operands)
                        if (o is MReg { IsPhys: true, Phys: Gpr.Esp } || o is MMem mem && (mem.Base is { IsPhys: true, Phys: Gpr.Esp } || mem.IsSpill)) return null;
                    if (i.Op is MOp.Jmp or MOp.Jcc or MOp.JmpTable or MOp.Push or MOp.Pop or MOp.Call or MOp.CallInd or MOp.Ret) return null;
                    continue;
                }
                depth[i] = d;
                if (i.Op == MOp.Mov && i.Operands is [MReg { IsPhys: true, Phys: Gpr.Esp }, MMem])
                {
                    leaving = true;
                    continue;
                }
                // EACH JUMP AT ITS OWN DEPTH: a jump mid-block leaves at the
                // depth there, not the block's last.
                if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands[0] is MLabel label) Enter(label.Target, d, ref bad);
                else if (i.Op == MOp.JmpTable) foreach (MBlock t in i.Table!) Enter(t, d, ref bad);
                else if (i.Op == MOp.JmpInd) return null;
                switch (i.Op)
                {
                    case MOp.Push or MOp.Pushf: d += 4; continue;
                    case MOp.Pop or MOp.Popf: d -= 4; if (d < 0) return null; continue;
                    case MOp.Ret: return null;
                    case MOp.InlineAsm when i.Asm!.NeedsFrame: return null;
                    case MOp.Call or MOp.CallInd when i.Native: return null;
                    case MOp.Sub or MOp.Add when i.Operands is [MReg { IsPhys: true, Phys: Gpr.Esp }, MImm { Symbol: null } n]:
                        d += i.Op == MOp.Sub ? (int)n.Value : -(int)n.Value;
                        if (d < 0) return null;
                        continue;
                }
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not MReg { IsPhys: true, Phys: Gpr.Esp }) continue;
                    if ((Roles.Of(i.Op, k) & Roles.Role.Def) != 0) return null;
                    // ESP READ AS A VALUE where a handler record keeps it
                    // (Lowering.PushHandler's StackPointer): at depth 0, the
                    // aligned ESP a landing pad is entered with, or not at all.
                    if (pads && d != 0) return null;
                }
            }
            if (leaving)
            {
                if (b.Instrs.Count == 0 || b.Instrs[^1].Op != MOp.JmpInd) return null;
                continue;
            }
            if (!b.EndsUnconditionally && index[b] + 1 < m.Blocks.Count) Enter(m.Blocks[index[b] + 1], d, ref bad);
        }
        return bad ? null : depth;
    }

    /// <summary>Whether a slot starts in the fifteen bytes below an offset: the offset inside it.</summary>
    private static bool Inside(SortedSet<int> slots, int disp)
    {
        for (int d = disp - 15; d < disp; d++)
            if (slots.Contains(d)) return true;
        return false;
    }

    /// <summary>
    /// A RELOAD READ ONCE, BY AN SSE OPERATION, READ FROM THE SLOT ITSELF:
    /// `movaps x2, [esp+32] ; pxor x6, x2` is `pxor x6, [esp+32]`, which
    /// the slot's alignment now allows (an SSE operation's sixteen bytes of
    /// memory must be aligned) -- a load and a register less. Where the
    /// operation is the reload's register's next naming, as its source, and
    /// the register is written again before anything reads it, all in the
    /// block, no jump between and the slot not written between. A float's
    /// write counts as all of the register: nothing reads a scalar's top
    /// lanes (as ForwardXmmSpillLoads).
    /// </summary>
    private static void FoldReloads(List<MInstr> instrs, Dictionary<MInstr, int> depth)
    {
        static bool Names(MInstr i, int reg)
        {
            foreach (MOperand o in i.Operands)
                if (o is MReg r && r.Id == reg) return true;
            return false;
        }
        static bool Jumps(MInstr i) => i.Op is MOp.Jcc or MOp.Jmp or MOp.JmpTable or MOp.JmpInd;
        static bool NamesSlot(MInstr i, int disp)
        {
            foreach (MOperand o in i.Operands)
                if (o is MMem { Aligned: true } w && w.Disp == disp) return true;
            return false;
        }
        for (int k = 0; k < instrs.Count; k++)
        {
            MInstr load = instrs[k];
            if (load.Op != MOp.MovF || load.Width != 16 || load.Operands.Count != 2
                || load.Operands[0] is not MReg { IsXmm: true } x || load.Operands[1] is not MMem { Aligned: true } slot)
                continue;
            int n = k + 1;
            // Not across a move of ESP: the operand's displacement is the load's depth's.
            while (n < instrs.Count && !Names(instrs[n], x.Id) && !Jumps(instrs[n]) && !NamesSlot(instrs[n], slot.Disp)
                   && depth.GetValueOrDefault(instrs[n], -1) == depth.GetValueOrDefault(instrs[k], -2)) n++;
            if (n < instrs.Count && depth.GetValueOrDefault(instrs[n], -1) != depth.GetValueOrDefault(instrs[k], -2)) continue;
            if (n >= instrs.Count) continue;
            MInstr user = instrs[n];
            if (user.Op is not (MOp.XmmOpV or MOp.XmmOpVDef) || user.Operands[3] is not MReg { } source || source.Id != x.Id
                || user.Operands[2] is MReg { } d && d.Id == x.Id)
                continue;
            // Written again before any read, in the block: dead after the operation.
            bool dead = false;
            for (int after = n + 1; after < instrs.Count && !Jumps(instrs[after]); after++)
            {
                MInstr i = instrs[after];
                if (!Names(i, x.Id)) continue;
                bool read = false, written = false;
                for (int o = 0; o < i.Operands.Count; o++)
                    if (i.Operands[o] is MReg r && r.Id == x.Id)
                    {
                        Roles.Role role = Roles.Of(i.Op, o);
                        read |= (role & Roles.Role.Use) != 0;
                        written |= (role & Roles.Role.Def) != 0;
                    }
                dead = written && !read;
                break;
            }
            if (!dead) continue;
            user.Operands[3] = slot;
            instrs.RemoveAt(k);
            k--;
        }
    }
}
