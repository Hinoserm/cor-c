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
///   AlignedSlots       i386's sixteen-byte slots for packed values (long
///                      mode selects no packed operation yet)
///   FrameCompact       the frame's slots closed up
///   FramePointerFree   a leaf's frame addressed off the stack pointer
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
        if (stackMaps) m.SlotLiveness = FrameSlotLiveness.Measured(m);
    }
}
