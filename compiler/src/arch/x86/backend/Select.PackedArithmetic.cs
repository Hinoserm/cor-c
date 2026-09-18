using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private bool TryPackedArithmetic(List<Instr> instructions, int start, out int consumed)
    {
        consumed = 0;
        if (!_automaticPacked || !Target.Current.X86Profile.Mmx || start + 7 >= instructions.Count) return false;
        Instr firstLoad = instructions[start], firstOp = instructions[start + 2], firstStore = instructions[start + 3];
        int width = firstLoad.Size;
        MOp? packed = (firstOp.Op, width) switch
        {
            (Opcode.Add, 1) => MOp.MmxAddB, (Opcode.Add, 2) => MOp.MmxAddW, (Opcode.Add, 4) => MOp.MmxAddD,
            (Opcode.Sub, 1) => MOp.MmxSubB, (Opcode.Sub, 2) => MOp.MmxSubW, (Opcode.Sub, 4) => MOp.MmxSubD,
            (Opcode.And, 1 or 2 or 4) => MOp.MmxAnd, (Opcode.Or, 1 or 2 or 4) => MOp.MmxOr,
            (Opcode.Xor, 1 or 2 or 4) => MOp.MmxXor, (Opcode.Mul, 2) => MOp.MmxMulW, _ => null,
        };
        if (packed is null || firstLoad.Operands.Count != 1 || instructions[start + 1].Operands.Count != 1 || firstStore.Operands.Count != 2)
            return false;
        FrameSlot? left = FrameStorage(firstLoad.Operands[0]), right = FrameStorage(instructions[start + 1].Operands[0]),
            destination = FrameStorage(firstStore.Operands[0]);
        if (left is null || right is null || destination is null
            || left.Align < 4 || right.Align < 4 || destination.Align < 4) return false;
        long leftStart = firstLoad.Offset, rightStart = instructions[start + 1].Offset, destStart = firstStore.Offset;
        // Exact in-place lanes are independent. Shifted overlap is not: scalar
        // stores can feed subsequent loads, unlike an eight-byte packed load.
        if ((destination == left && destStart != leftStart)
            || (destination == right && destStart != rightStart)) return false;
        if (leftStart < 0 || rightStart < 0 || destStart < 0 || leftStart > left.Bytes || rightStart > right.Bytes || destStart > destination.Bytes
            || (leftStart | rightStart | destStart) % 4 != 0) return false;
        bool Single(Instr instruction) => instruction.Dest is { Type: IrType.I32 } value
            && _useCount.GetValueOrDefault(value.Id) == 1 && _definitions.GetValueOrDefault(value) == instruction;
        bool Ref(Operand operand, VReg? value) => operand is RegOperand register && register.Reg == value;
        int lanes = 0;
        while (start + lanes * 4 + 3 < instructions.Count && lanes * width < 128)
        {
            int at = start + lanes * 4;
            Instr a = instructions[at], b = instructions[at + 1], op = instructions[at + 2], store = instructions[at + 3];
            long offset = (long)lanes * width;
            if (a.Op != Opcode.Load || b.Op != Opcode.Load || store.Op != Opcode.Store || op.Op != firstOp.Op
                || a.Size != width || b.Size != width || store.Size != width || !Single(a) || !Single(b) || !Single(op)
                || a.Operands.Count != 1 || b.Operands.Count != 1 || op.Operands.Count != 2 || store.Operands.Count != 2
                || !Ref(op.Operands[0], a.Dest) || !Ref(op.Operands[1], b.Dest) || !Ref(store.Operands[1], op.Dest)
                || FrameStorage(a.Operands[0]) != left || FrameStorage(b.Operands[0]) != right || FrameStorage(store.Operands[0]) != destination
                || a.Offset != leftStart + offset || b.Offset != rightStart + offset || store.Offset != destStart + offset
                || a.Offset + width > left.Bytes || b.Offset + width > right.Bytes || store.Offset + width > destination.Bytes) break;
            lanes++;
        }
        // At least two packed operations amortize the x87/MMX state transition.
        int bytes = lanes * width / 8 * 8;
        if (bytes < 16) return false;
        for (int offset = 0; offset < bytes; offset += 8)
        {
            Emit(MOp.MmxLoad, MMem.Frame(_m.Frame.SlotOffset(left) + (int)leftStart + offset));
            Emit(packed.Value, MMem.Frame(_m.Frame.SlotOffset(right) + (int)rightStart + offset));
            Emit(MOp.MmxStore, MMem.Frame(_m.Frame.SlotOffset(destination) + (int)destStart + offset));
        }
        EndMmx(); consumed = bytes / width * 4; return true;
    }
}
