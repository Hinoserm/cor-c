#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// A FUNCTION FROM ITS SELECTION TO THE CODE AS IT WILL BE ENCODED, the
/// same passes in the same order for both modes, each reading the mode
/// where it must (X86Mode.Long):
///
///   AddressFold        addresses folded into the operands that use them
///                      (i386's symbols absolute, long mode's RIP-relative)
///   IfConvert          a branch over a move made a conditional move, where
///                      the processor has one (`conditionalMoves`: every
///                      x86-64 does)
///   XmmLocalSplit      an XMM value's uses in one block parted from the rest
///   Allocator          the registers (FamilyTarget and the one allocator)
///   Layout, LoopRotate the blocks' order, a loop's test at its foot
///   Peephole           and the saves nothing needs (UnusedSaves)
///   AlignedSlots       i386's sixteen-byte slots for packed values, in an
///                      area it aligns
///   FrameCompact       the frame's slots closed up
///   FramePointerFree   a leaf's frame addressed off the stack pointer
///   AlignedSpills      long mode's packed spills, at sixteen in a framed
///                      function as they stand, marked for MOVAPS
///   FrameSlotLiveness  last, over the code as it will be encoded: which IR
///                      frame slot words each call's map names, and the
///                      zeroing of those a call may find unwritten
///
/// An InvalidOperationException from any of them is the function's error
/// (each backend's Compile reports it).
/// </summary>
internal static class FamilyPasses
{
    public static void Run(MFunction m, bool stackMaps, bool conditionalMoves)
    {
        AddressFold.Run(m, absolute: !X86Mode.Long, compareMemory: true);
        if (conditionalMoves) IfConvert.Run(m);
        XmmLocalSplit.Run(m);
        Allocator.Run(m);
        Layout.Run(m);
        LoopRotate.Run(m);
        Peephole.Run(m);
        Peephole.UnusedSaves(m);
        if (!X86Mode.Long) AlignedSlots.Run(m);
        FrameCompact.Run(m);
        FramePointerFree.Run(m);
        if (X86Mode.Long) AlignedSpills(m);
        if (stackMaps) m.SlotLiveness = FrameSlotLiveness.Measured(m);
    }

    /// <summary>
    /// LONG MODE'S PACKED SPILLS MOVED BY MOVAPS: a framed function's base
    /// register is a multiple of sixteen (the stack is at sixteen before
    /// every call, so eight off it at entry, and the prologue pushes one
    /// word), and a packed value's spill slot is at sixteen from it
    /// (FamilyTarget.NewSlot; FrameCompact keeps a slot's alignment). So
    /// each sixteen-byte move of a spill slot found there says so (MMem.
    /// Aligned), where i386 must make an aligned area first (AlignedSlots).
    /// Not in a function with no frame of the prologue's: its slots are
    /// off the stack pointer, and stay MOVUPS's.
    ///
    /// THE STACK AT SIXTEEN BEFORE EVERY CALL is what this stands on, and
    /// hosted code is entered no other way: the process's entry realigns
    /// (RealignsStack, itself left out), a thread starts on a stack the
    /// runtime's trampoline aligns (platforms/linux/threading.cor), the
    /// kernel enters a signal's handler as the ABI says, and every call
    /// this backend makes keeps the sixteen (Conventions.SystemV). Test
    /// 2336 runs spilling code on a thread, a pool's worker, in a catch
    /// handler entered by a throw, and at every depth of calls. FREESTANDING code is entered by whatever
    /// wrote its stubs -- an interrupt's frame with or without an error
    /// code, a context switch -- and is promised nothing: its packed
    /// spills stay MOVUPS's. Which it is, is the function's own word
    /// (MFunction.StackAtSixteen, from the unit's contract): a unit made
    /// again at the link is compiled by another process, which reads it
    /// from the unit.
    /// </summary>
    private static void AlignedSpills(MFunction m)
    {
        if (m.XmmSpillWidth != 16 || m.Frameless || m.FramePointerFree || m.ColdFrames || m.RealignsStack) return;
        if (!m.StackAtSixteen) return;
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
            {
                if (i.Op != MOp.MovF || i.Width != 16) continue;
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is MMem { IsSpill: true, Aligned: false, Index: null, Symbol: null, Label: null, Base: { IsPhys: true, Phys: Gpr.Ebp } } slot
                        && slot.Disp < 0 && (slot.Disp & 15) == 0)
                        i.Operands[k] = slot.AsAligned();
            }
        // A reload read once, by a packed operation, is read from the slot
        // itself, which its alignment allows (AlignedSlots.FoldReloads).
        foreach (MBlock b in m.Blocks) AlignedSlots.FoldReloads(b.Instrs, null);
    }
}
