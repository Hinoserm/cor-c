using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    // Compare followed by negation creates an all-bits-set mask, not C#'s
    // canonical boolean 1. Never substitute a packed mask for a plain bool.
    private bool TryPackedComparisons(List<Instr> instructions, int start, out int consumed)
    {
        consumed = 0;
        if (!_automaticPacked || !Target.Current.X86Profile.Mmx || start + 9 >= instructions.Count) return false;
        Instr first = instructions[start], second = instructions[start + 1], comparison = instructions[start + 2], store0 = instructions[start + 4];
        int width = first.Size;
        if (width is not (1 or 2 or 4) || comparison.Op is not (Opcode.Eq or Opcode.GtS)
            || first.Operands.Count != 1 || second.Operands.Count != 1 || store0.Operands.Count != 2) return false;
        FrameSlot? left = FrameStorage(first.Operands[0]), right = FrameStorage(second.Operands[0]), destination = FrameStorage(store0.Operands[0]);
        if (left is null || right is null || destination is null || destination == left || destination == right
            || left.Align < 4 || right.Align < 4 || destination.Align < 4) return false;
        long leftStart = first.Offset, rightStart = second.Offset, destinationStart = store0.Offset;
        if (leftStart < 0 || rightStart < 0 || destinationStart < 0
            || leftStart > left.Bytes || rightStart > right.Bytes || destinationStart > destination.Bytes
            || ((leftStart | rightStart | destinationStart) & 3) != 0) return false;
        bool Single(Instr i) => i.Dest is { Type: IrType.I32 } value && _useCount.GetValueOrDefault(value.Id) == 1
            && _definitions.GetValueOrDefault(value) == i;
        bool Ref(Operand o, VReg? value) => o is RegOperand r && r.Reg == value;
        int lanes = 0;
        while (start + lanes * 5 + 4 < instructions.Count && lanes * width < 128)
        {
            int at = start + lanes * 5;
            Instr a = instructions[at], b = instructions[at + 1], compare = instructions[at + 2], negate = instructions[at + 3], store = instructions[at + 4];
            if (a.Op != Opcode.Load || b.Op != Opcode.Load || compare.Op != comparison.Op || negate.Op != Opcode.Neg || store.Op != Opcode.Store
                || a.Size != width || b.Size != width || store.Size != width || !Single(a) || !Single(b) || !Single(compare) || !Single(negate)
                || a.Operands.Count != 1 || b.Operands.Count != 1 || compare.Operands.Count != 2 || negate.Operands.Count != 1 || store.Operands.Count != 2
                || !Ref(compare.Operands[0], a.Dest) || !Ref(compare.Operands[1], b.Dest) || !Ref(negate.Operands[0], compare.Dest) || !Ref(store.Operands[1], negate.Dest)
                || (width < 4 && (a.Signed != b.Signed || (compare.Op == Opcode.GtS && !a.Signed)))
                || FrameStorage(a.Operands[0]) != left || FrameStorage(b.Operands[0]) != right || FrameStorage(store.Operands[0]) != destination
                || a.Offset != leftStart + lanes * width || b.Offset != rightStart + lanes * width || store.Offset != destinationStart + lanes * width
                || a.Offset > left.Bytes - width || b.Offset > right.Bytes - width || store.Offset > destination.Bytes - width) break;
            lanes++;
        }
        int bytes = lanes * width / 8 * 8;
        if (bytes < 16) return false;
        MOp op = (comparison.Op, width) switch
        {
            (Opcode.Eq, 1) => MOp.MmxEqualB, (Opcode.Eq, 2) => MOp.MmxEqualW, (Opcode.Eq, 4) => MOp.MmxEqualD,
            (Opcode.GtS, 1) => MOp.MmxGreaterB, (Opcode.GtS, 2) => MOp.MmxGreaterW, _ => MOp.MmxGreaterD,
        };
        for (int offset = 0; offset < bytes; offset += 8)
        {
            Emit(MOp.MmxLoad, MMem.Frame(_m.Frame.SlotOffset(left) + (int)leftStart + offset));
            Emit(op, MMem.Frame(_m.Frame.SlotOffset(right) + (int)rightStart + offset));
            Emit(MOp.MmxStore, MMem.Frame(_m.Frame.SlotOffset(destination) + (int)destinationStart + offset));
        }
        EndMmx(); consumed = bytes / width * 5; return true;
    }
}
