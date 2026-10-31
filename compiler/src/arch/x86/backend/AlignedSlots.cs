#nullable enable
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
/// Only where nothing moves ESP between the prologue and the epilogue but a
/// call with nothing on the stack -- no push, no pop, no `sub esp` for
/// arguments, no operand already off ESP -- so ESP is where the prologue left
/// it at every slot's access: a leaf of packed rounds, like Generate4, whose
/// only calls are its bounds checks' throws; a function that passes stack
/// arguments keeps its slots on EBP and MOVUPS. Not in a function with a
/// handler either, as a precaution: SelectUnwind lands with the ESP the try
/// recorded, which would be the aligned one, but every way a handler is
/// entered (the kernel's fault recovery among them) is not proven so. Last
/// but FrameCompact, which then packs the EBP frame without them, and
/// FramePointerFree, whose functions are left alone.
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
        {
            if (b.Source is { IsLandingPad: true }) return;
            foreach (MInstr i in b.Instrs)
            {
                if (i.Op is MOp.Push or MOp.Pop or MOp.Pushfd or MOp.Popfd or MOp.Ret) return;
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    MOperand o = i.Operands[k];
                    if (o is MReg { IsPhys: true, Phys: Gpr.Esp } && (Roles.Of(i.Op, k) & Roles.Role.Def) != 0) return;
                    if (o is MMem { } x && (x.Base is { IsPhys: true, Phys: Gpr.Esp } || x.Index is { IsPhys: true, Phys: Gpr.Esp })) return;
                }
                if (i.Op == MOp.MovF && i.Width == 16 && i.Operands.Count == 2)
                    foreach (MOperand o in i.Operands)
                        if (Spill(o, out MMem mem)) slots.Add(mem.Disp);
            }
        }
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
        Dictionary<int, int> at = new();
        foreach (int disp in slots) at[disp] = 16 * at.Count;
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (Spill(i.Operands[k], out MMem mem) && at.TryGetValue(mem.Disp, out int to))
                        i.Operands[k] = new MMem(MReg.Of(Gpr.Esp), to) { IsSpill = true, Aligned = true };
        m.AlignedArea = 16 * at.Count;
        m.Frame.Release(slots);
        foreach (MBlock b in m.Blocks) FoldReloads(b.Instrs);
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
    private static void FoldReloads(List<MInstr> instrs)
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
            while (n < instrs.Count && !Names(instrs[n], x.Id) && !Jumps(instrs[n]) && !NamesSlot(instrs[n], slot.Disp)) n++;
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
