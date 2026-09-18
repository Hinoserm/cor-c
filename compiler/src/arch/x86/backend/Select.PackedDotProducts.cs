using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private bool TryPackedDotProducts(List<Instr> instructions, int start, out int consumed)
    {
        consumed = 0;
        if (!_automaticPacked || !Target.Current.X86Profile.Mmx || start + 7 >= instructions.Count) return false;
        Instr first = instructions[start], second = instructions[start + 1], firstStore = instructions[start + 7];
        if (first.Operands.Count != 1 || second.Operands.Count != 1 || firstStore.Operands.Count != 2) return false;
        FrameSlot? left = FrameStorage(first.Operands[0]), right = FrameStorage(second.Operands[0]), destination = FrameStorage(firstStore.Operands[0]);
        if (left is null || right is null || destination is null || left == destination || right == destination
            || left.Align < 4 || right.Align < 4 || destination.Align < 4) return false;
        long leftStart = first.Offset, rightStart = second.Offset, destStart = firstStore.Offset;
        if (leftStart < 0 || rightStart < 0 || destStart < 0 || leftStart > left.Bytes || rightStart > right.Bytes || destStart > destination.Bytes
            || ((leftStart | rightStart | destStart) & 3) != 0) return false;
        bool Single(Instr instruction) => instruction.Dest is { Type: IrType.I32 } value
            && _useCount.GetValueOrDefault(value.Id) == 1 && _definitions.GetValueOrDefault(value) == instruction;
        bool Ref(Operand operand, VReg? value) => operand is RegOperand register && register.Reg == value;
        bool Load(Instr instruction, FrameSlot source, long offset) => instruction.Op == Opcode.Load && instruction.Size == 2
            && instruction.Signed && Single(instruction) && instruction.Operands.Count == 1 && FrameStorage(instruction.Operands[0]) == source
            && instruction.Offset == offset && offset <= source.Bytes - 2;
        bool Product(Instr instruction, Instr a, Instr b) => instruction.Op == Opcode.Mul && Single(instruction) && instruction.Operands.Count == 2
            && Ref(instruction.Operands[0], a.Dest) && Ref(instruction.Operands[1], b.Dest);
        int lanes = 0;
        while (start + lanes * 8 + 7 < instructions.Count && lanes < 32)
        {
            int at = start + lanes * 8;
            Instr a = instructions[at], b = instructions[at + 1], ab = instructions[at + 2], c = instructions[at + 3],
                d = instructions[at + 4], cd = instructions[at + 5], sum = instructions[at + 6], store = instructions[at + 7];
            if (!Load(a, left, leftStart + lanes * 4) || !Load(b, right, rightStart + lanes * 4)
                || !Load(c, left, leftStart + lanes * 4 + 2) || !Load(d, right, rightStart + lanes * 4 + 2)
                || !Product(ab, a, b) || !Product(cd, c, d) || sum.Op != Opcode.Add || !Single(sum) || sum.Operands.Count != 2
                || !Ref(sum.Operands[0], ab.Dest) || !Ref(sum.Operands[1], cd.Dest) || store.Op != Opcode.Store || store.Size != 4
                || store.Operands.Count != 2 || !Ref(store.Operands[1], sum.Dest) || FrameStorage(store.Operands[0]) != destination
                || store.Offset != destStart + lanes * 4 || store.Offset > destination.Bytes - 4) break;
            lanes++;
        }
        lanes = lanes / 2 * 2;
        if (lanes < 4) return false;
        for (int lane = 0; lane < lanes; lane += 2)
        {
            Emit(MOp.MmxLoad, MMem.Frame(_m.Frame.SlotOffset(left) + (int)leftStart + lane * 4));
            Emit(MOp.MmxMultiplyAddW, MMem.Frame(_m.Frame.SlotOffset(right) + (int)rightStart + lane * 4));
            Emit(MOp.MmxStore, MMem.Frame(_m.Frame.SlotOffset(destination) + (int)destStart + lane * 4));
        }
        EndMmx(); consumed = lanes * 8; return true;
    }
}
