using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;

public sealed partial class Lowering
{
    /// <summary>The name a struct value's storage in the frame carries (StructInFrame): what FrameValuesKept follows.</summary>
    internal const string FrameValueSlot = "value";

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
        if (!f.Slots.Any(s => s.Name == FrameValueSlot)) return;
        // WHAT HOLDS SUCH AN ADDRESS: a copy of one, one offset by a
        // constant either way, one widened or narrowed, a Phi with one among
        // its operands -- and a load from a slot of the frame such an address
        // was stored into (an address-taken struct local holds its value's
        // address in its slot).
        HashSet<VReg> framed = new();
        HashSet<FrameSlot> holding = new(ReferenceEqualityComparer.Instance);
        bool Framed(Operand o) => o switch
        {
            SlotOperand { Slot.Name: FrameValueSlot } => true,
            RegOperand r => framed.Contains(r.Reg),
            _ => false,
        };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Store && i.Operands is [SlotOperand { Slot: var into }, var stored] && Framed(stored) && holding.Add(into)) grew = true;
                    if (i.Dest is not VReg d || framed.Contains(d)) continue;
                    bool holds = i.Op switch
                    {
                        Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 => i.Operands.Count == 1 && Framed(i.Operands[0]),
                        Opcode.Add => i.Operands.Count == 2 && (Framed(i.Operands[0]) && i.Operands[1] is ImmOperand
                                                              || Framed(i.Operands[1]) && i.Operands[0] is ImmOperand),
                        Opcode.Sub => i.Operands.Count == 2 && Framed(i.Operands[0]) && i.Operands[1] is ImmOperand,
                        Opcode.Phi => i.Operands.Any(Framed),
                        Opcode.Load => i.Operands is [SlotOperand { Slot: var from }] && holding.Contains(from),
                        _ => false,
                    };
                    if (holds) { framed.Add(d); grew = true; }
                }
        }
        // WHAT IS THIS FRAME'S MEMORY ON EVERY PATH: a slot's address, or a
        // register every write of which makes one (a copy, a constant
        // offset, a Phi all of whose operands are) -- the greatest such set,
        // so a loop's Phi of two slot addresses is one. A Phi that may be a
        // heap address is not: a store through it may be into the heap.
        HashSet<VReg> frameOnly = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is VReg d && i.Op is Opcode.Copy or Opcode.Add or Opcode.Sub or Opcode.Phi) frameOnly.Add(d);
        foreach (VReg p in f.Params) frameOnly.Remove(p);
        bool FrameMemory(Operand o) => o is SlotOperand || o is RegOperand r && frameOnly.Contains(r.Reg);
        for (bool shrank = true; shrank;)
        {
            shrank = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not VReg d || !frameOnly.Contains(d)) continue;
                    bool still = i.Op switch
                    {
                        Opcode.Copy => i.Operands.Count == 1 && FrameMemory(i.Operands[0]),
                        Opcode.Add => i.Operands.Count == 2 && (FrameMemory(i.Operands[0]) && i.Operands[1] is ImmOperand
                                                              || FrameMemory(i.Operands[1]) && i.Operands[0] is ImmOperand),
                        Opcode.Sub => i.Operands.Count == 2 && FrameMemory(i.Operands[0]) && i.Operands[1] is ImmOperand,
                        Opcode.Phi => i.Operands.All(FrameMemory),
                        _ => false,
                    };
                    if (!still) { frameOnly.Remove(d); shrank = true; }
                }
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                string? kept = i.Op switch
                {
                    Opcode.Store when i.Operands.Count == 2 && Framed(i.Operands[1]) && !FrameMemory(i.Operands[0]) => "stored into memory the frame does not hold",
                    Opcode.Ret when i.Operands.Count == 1 && Framed(i.Operands[0]) => "returned",
                    _ => null,
                };
                if (kept is not null)
                    Error(at, $"internal: {f.Name}: a struct value's storage in the frame is {kept} ({i}); it must be copied to the heap first (HeapStruct)");
            }
    }
}
