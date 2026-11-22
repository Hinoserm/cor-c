#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE CLASS AN OBJECT MADE IN THIS FRAME IS (Escape's promotion of a `new`
/// to a frame slot): read back from the slot's first word, where lowering
/// stored the vtable as the object was made, to call a method of it --
/// Runtime.FreeStorageInFrame inlined, its `owner.FreeStorage()` a load of
/// that word, a load of the method from the vtable, and a call through it.
/// No managed code writes an object's vtable word after its making, so where
/// every write of word 0 of the slot in the function is that one vtable, or
/// a memset of the slot to zero (the slot as it starts, which a guard tests
/// before any such call), the word holds that vtable or nothing, and the
/// method is the one the descriptor holds in that slot. Asked by the region
/// passes, which otherwise took the call for one through a method read out
/// of a descriptor -- called blind, every method of every class at that
/// slot run with anything (RegionSummary), and the frame-made List's own
/// call run on nobody knows what (RegionPointsTo) -- once throw outlining
/// let such frees be inlined (1298, 1319).
/// </summary>
internal static class FrameVtable
{
    /// <summary>
    /// The frame slot a call's method is read from the vtable word of, with
    /// the vtable's symbol and the offset of the method in it; null where the
    /// method is read any other way or the slot's word 0 is written with
    /// anything else. `single` answers the one instruction writing a register.
    /// </summary>
    public static (string Table, long At)? Of(Function f, Instr call, Func<VReg, Instr?> single)
    {
        if (call.Operands.Count < 1 || call.Operands[0] is not RegOperand { Reg: var target }) return null;
        if (single(target) is not { Op: Opcode.Load, Operands: [RegOperand { Reg: var table }] } method || method.Offset < 0) return null;
        if (single(table) is not { Op: Opcode.Load, Offset: 0 } header || SlotOf(header.Operands[0], single) is not { } slot) return null;
        return Stored(f, slot, single) is var (name, at) ? (name, at + method.Offset) : null;
    }

    /// <summary>
    /// WHETHER A CALL'S METHOD IS READ FROM THE VTABLE OF NULL: the receiver's
    /// first word loaded from address 0 -- a path inlining left where the
    /// object is the constant null. The load faults, and the call is never
    /// made: no call at all to the region passes, not one through a method
    /// read out of a descriptor (called blind, RegionSummary).
    /// </summary>
    public static bool OnNull(Instr call, Func<VReg, Instr?> single)
    {
        if (call.Operands.Count < 1 || call.Operands[0] is not RegOperand { Reg: var target }) return false;
        if (single(target) is not { Op: Opcode.Load, Operands: [RegOperand { Reg: var table }] }) return false;
        Operand from = RegOperand.Of(table);
        for (int hops = 0; hops < 8; hops++)
        {
            if (from is not RegOperand { Reg: var r } || single(r) is not { } d) return false;
            if (d.Op == Opcode.Copy && d.Operands is [var copied]) { from = copied; continue; }
            return d is { Op: Opcode.Load, Offset: 0, Operands: [ImmOperand { Value: 0 }] };
        }
        return false;
    }

    /// <summary>The slot an address is, directly or through copies; null for anything else.</summary>
    private static FrameSlot? SlotOf(Operand o, Func<VReg, Instr?> single)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (o is SlotOperand s) return s.Slot;
            if (o is not RegOperand { Reg: var r } || single(r) is not { Op: Opcode.Copy, Operands: [var from] }) return null;
            o = from;
        }
        return null;
    }

    /// <summary>
    /// The one vtable every write of the slot's word 0 stores (zeroing stores
    /// and memsets aside); null where any write could be anything else. The
    /// slot's address is followed through copies; where it is carried any
    /// other way -- a join, an address moved by less than a word or by an
    /// amount not known -- a write through it could not be seen, and nothing
    /// is said. A call handed it writes no object's vtable word: no managed
    /// code does after the making.
    /// </summary>
    private static (string Name, long At)? Stored(Function f, FrameSlot slot, Func<VReg, Instr?> single)
    {
        (string Name, long At)? found = null;
        int word = IrTypes.Word.Bytes();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                bool names = false;
                for (int k = 0; k < i.Operands.Count && !names; k++) names = SlotOf(i.Operands[k], single) is { } at && ReferenceEquals(at, slot);
                if (!names) continue;
                switch (i.Op)
                {
                    case Opcode.Copy or Opcode.Load or Opcode.Call or Opcode.CallIndirect:
                        continue;
                    // Moved a word or more on: a field's address, past word 0.
                    case Opcode.Add when i.Operands is [_, ImmOperand { Value: var by }] && by >= word:
                        continue;
                    case Opcode.Store or Opcode.MemSet or Opcode.MemCopy when SlotOf(i.Operands[0], single) is not { } into || !ReferenceEquals(into, slot):
                        // The slot's address stored as a value, or read from.
                        if (i.Op == Opcode.MemCopy) continue;
                        if (i.Op == Opcode.Store) continue;
                        return null;
                    case Opcode.Store when i.Offset >= word || i.Offset + i.Size <= 0:
                        continue;
                    case Opcode.Store when i.Offset == 0 && i.Operands[1] is SymOperand { Name: var name, Offset: var at }
                                           && name.StartsWith("t_", StringComparison.Ordinal):
                        if (found is { } had && (had.Name != name || had.At != at)) return null;
                        found = (name, at);
                        continue;
                    case Opcode.Store when i.Offset == 0 && i.Operands[1] is ImmOperand { Value: 0 }:
                        continue;
                    case Opcode.MemSet when i.Operands[1] is ImmOperand { Value: 0 }:
                        continue;
                    default:
                        return null;
                }
            }
        return found;
    }
}
