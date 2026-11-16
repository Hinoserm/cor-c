using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;

public sealed partial class Lowering
{
    /// <summary>The name a struct value's storage in the frame carries (StructInFrame): what FrameValuesKept follows.</summary>
    private const string FrameValueSlot = "value";

    /// <summary>
    /// A STRUCT VALUE'S FRAME STORAGE NEVER KEPT PAST THE FRAME: the address
    /// of a slot StructInFrame made -- and anything copied from it or offset
    /// from it -- stored into memory that is not this frame's, or returned,
    /// is an internal error at the method, not a pointer left dangling. Every
    /// place that keeps a struct by pointer copies it to the heap first
    /// (HeapStruct); one that does not is a lowering bug, and is said here
    /// rather than found as a corrupted struct at run time. Passing it to a
    /// call is the convention every struct argument has: the callee copies
    /// what it keeps.
    /// </summary>
    private void FrameValuesKept(Function f, Node at)
    {
        HashSet<VReg> framed = new();
        bool Framed(Operand o) => o switch
        {
            SlotOperand { Slot.Name: FrameValueSlot } => true,
            RegOperand r => framed.Contains(r.Reg),
            _ => false,
        };
        bool Frame(Operand o) => o is SlotOperand || o is RegOperand r && framed.Contains(r.Reg);
        // What holds such an address: a copy of one, or one offset by a constant.
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not VReg d || framed.Contains(d)) continue;
                    bool holds = i.Op switch
                    {
                        Opcode.Copy => i.Operands.Count == 1 && Framed(i.Operands[0]),
                        Opcode.Add => i.Operands.Count == 2 && (Framed(i.Operands[0]) && i.Operands[1] is ImmOperand
                                                              || Framed(i.Operands[1]) && i.Operands[0] is ImmOperand),
                        Opcode.Phi => i.Operands.Any(Framed),
                        _ => false,
                    };
                    if (holds) { framed.Add(d); grew = true; }
                }
        }
        if (framed.Count == 0 && !f.Slots.Any(s => s.Name == FrameValueSlot)) return;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                string? kept = i.Op switch
                {
                    Opcode.Store when i.Operands.Count == 2 && Framed(i.Operands[1]) && !Frame(i.Operands[0]) => "stored into memory the frame does not hold",
                    Opcode.Ret when i.Operands.Count == 1 && Framed(i.Operands[0]) => "returned",
                    _ => null,
                };
                if (kept is not null)
                    Error(at, $"internal: {f.Name}: a struct value's storage in the frame is {kept} ({i}); it must be copied to the heap first (HeapStruct)");
            }
    }
}
