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
/// arguments, no operand already off ESP -- and no handler (an unwind lands
/// in one with ESP at the saved registers), so ESP is where the prologue left
/// it at every slot's access. Last but FrameCompact, which then packs the
/// EBP frame without them, and FramePointerFree, whose functions are left
/// alone.
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
        foreach (Safepoint point in m.Safepoints.Values)
            foreach (int offset in point.SlotOffsets)
                if (slots.Contains(offset)) return;
        // A frame of under a page stays under one: nothing probes the stack.
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
    }
}
