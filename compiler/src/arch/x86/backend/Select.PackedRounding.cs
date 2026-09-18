using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private bool TryPackedRounding(List<Instr> instructions, int start, out int consumed)
    {
        consumed = 0;
        if (!_automaticPacked || !Target.Current.X86Profile.Mmx || start + 4 >= instructions.Count) return false;
        Instr first = instructions[start]; int width = first.Size;
        bool average = width == 1, rounded = instructions[start + 3].Op == Opcode.Add;
        if (width is not (1 or 2) || (average && !rounded) || (rounded && !Target.Current.X86Profile.ThreeDNow)) return false;
        int stride = rounded ? 6 : 5;
        if (start + stride - 1 >= instructions.Count || first.Operands.Count != 1 || instructions[start + 1].Operands.Count != 1
            || instructions[start + stride - 1].Operands.Count != 2) return false;
        FrameSlot? left = FrameStorage(first.Operands[0]), right = FrameStorage(instructions[start + 1].Operands[0]),
            destination = FrameStorage(instructions[start + stride - 1].Operands[0]);
        if (left is null || right is null || destination is null || left == destination || right == destination
            || left.Align < 4 || right.Align < 4 || destination.Align < 4) return false;
        long leftStart = first.Offset, rightStart = instructions[start + 1].Offset, destStart = instructions[start + stride - 1].Offset;
        if (leftStart < 0 || rightStart < 0 || destStart < 0 || leftStart > left.Bytes || rightStart > right.Bytes || destStart > destination.Bytes
            || ((leftStart | rightStart | destStart) & 3) != 0) return false;
        bool Single(Instr instruction) => instruction.Dest is { Type: IrType.I32 } value
            && _useCount.GetValueOrDefault(value.Id) == 1 && _definitions.GetValueOrDefault(value) == instruction;
        bool Ref(Operand operand, VReg? value) => operand is RegOperand register && register.Reg == value;
        int lanes = 0;
        while (start + lanes * stride + stride - 1 < instructions.Count && lanes * width < 128)
        {
            int at = start + lanes * stride;
            Instr a = instructions[at], b = instructions[at + 1], product = instructions[at + 2],
                shift = instructions[at + stride - 2], store = instructions[at + stride - 1];
            if (a.Op != Opcode.Load || b.Op != Opcode.Load || product.Op != (average ? Opcode.Add : Opcode.Mul)
                || shift.Op is not (Opcode.ShrS or Opcode.ShrU) || store.Op != Opcode.Store
                || a.Size != width || b.Size != width || store.Size != width || a.Signed == average || b.Signed == average
                || !Single(a) || !Single(b) || !Single(product) || !Single(shift)
                || a.Operands.Count != 1 || b.Operands.Count != 1 || product.Operands.Count != 2 || shift.Operands.Count != 2 || store.Operands.Count != 2
                || !Ref(product.Operands[0], a.Dest) || !Ref(product.Operands[1], b.Dest) || !Ref(store.Operands[1], shift.Dest)
                || shift.Operands[1] is not ImmOperand count || (count.Value & 31) != (average ? 1 : 16)
                || FrameStorage(a.Operands[0]) != left || FrameStorage(b.Operands[0]) != right || FrameStorage(store.Operands[0]) != destination
                || a.Offset != leftStart + lanes * width || b.Offset != rightStart + lanes * width || store.Offset != destStart + lanes * width
                || a.Offset > left.Bytes - width || b.Offset > right.Bytes - width || store.Offset > destination.Bytes - width) break;
            VReg? input = product.Dest;
            if (rounded)
            {
                Instr rounding = instructions[at + 3];
                if (rounding.Op != Opcode.Add || !Single(rounding) || rounding.Operands.Count != 2 || !Ref(rounding.Operands[0], input)
                    || rounding.Operands[1] is not ImmOperand increment || increment.Value != (average ? 1 : 32768)) break;
                input = rounding.Dest;
            }
            if (!Ref(shift.Operands[0], input)) break;
            lanes++;
        }
        int bytes = lanes * width / 8 * 8;
        if (bytes < 16) return false;
        MOp packed = average ? MOp.ThreeDNowAverageB : rounded ? MOp.ThreeDNowMulRoundW : MOp.MmxMulHighW;
        for (int offset = 0; offset < bytes; offset += 8)
        {
            Emit(MOp.MmxLoad, MMem.Frame(_m.Frame.SlotOffset(left) + (int)leftStart + offset));
            Emit(packed, MMem.Frame(_m.Frame.SlotOffset(right) + (int)rightStart + offset));
            Emit(MOp.MmxStore, MMem.Frame(_m.Frame.SlotOffset(destination) + (int)destStart + offset));
        }
        EndMmx(); consumed = bytes / width * stride; return true;
    }
}
