using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private bool TryPackedFloatArithmetic(List<Instr> instructions, int start, out int consumed)
    {
        consumed = 0;
        if (!_automaticPacked || !Target.Current.X86Profile.ThreeDNow || start + 5 >= instructions.Count) return false;
        Instr first = instructions[start], second = instructions[start + 2], math = instructions[start + 4];
        if (first.Size != 2 || second.Size != 2 || first.Operands.Count != 1 || second.Operands.Count != 1
            || math.Op is not (Opcode.FAdd or Opcode.FSub or Opcode.FMul)) return false;
        bool integerResult = instructions[start + 5].Op == Opcode.FToI;
        int stride = integerResult ? 7 : 6;
        if (start + stride - 1 >= instructions.Count || instructions[start + stride - 1].Operands.Count != 2) return false;
        // Products of two unsigned words may exceed Int32; do not substitute
        // PF2ID's saturation for the language/runtime overflow behavior.
        if (integerResult && math.Op == Opcode.FMul && !first.Signed && !second.Signed) return false;
        FrameSlot? left = FrameStorage(first.Operands[0]), right = FrameStorage(second.Operands[0]),
            destination = FrameStorage(instructions[start + stride - 1].Operands[0]);
        if (left is null || right is null || destination is null || left == destination || right == destination
            || left.Align < 4 || right.Align < 4 || destination.Align < 4) return false;
        long leftStart = first.Offset, rightStart = second.Offset, destStart = instructions[start + stride - 1].Offset;
        if (leftStart < 0 || rightStart < 0 || destStart < 0 || leftStart > left.Bytes || rightStart > right.Bytes || destStart > destination.Bytes
            || ((leftStart | rightStart | destStart) & 3) != 0) return false;
        bool Single(Instr instruction, IrType type) => instruction.Dest is { } value && value.Type == type
            && _useCount.GetValueOrDefault(value.Id) == 1 && _definitions.GetValueOrDefault(value) == instruction;
        bool Ref(Operand operand, VReg? value) => operand is RegOperand register && register.Reg == value;
        bool Conversion(Instr instruction, Instr load) => Single(instruction, IrType.F32) && instruction.Operands.Count == 1
            && Ref(instruction.Operands[0], load.Dest) && (instruction.Op == Opcode.IToF || (!load.Signed && instruction.Op == Opcode.UToF));
        int lanes = 0;
        while (start + lanes * stride + stride - 1 < instructions.Count && lanes < 32)
        {
            int at = start + lanes * stride;
            Instr a = instructions[at], ac = instructions[at + 1], b = instructions[at + 2], bc = instructions[at + 3],
                operation = instructions[at + 4], store = instructions[at + stride - 1];
            if (a.Op != Opcode.Load || b.Op != Opcode.Load || store.Op != Opcode.Store || operation.Op != math.Op
                || a.Size != 2 || b.Size != 2 || store.Size != 4 || a.Signed != first.Signed || b.Signed != second.Signed
                || !Single(a, IrType.I32) || !Single(b, IrType.I32) || !Conversion(ac, a) || !Conversion(bc, b) || !Single(operation, IrType.F32)
                || a.Operands.Count != 1 || b.Operands.Count != 1 || operation.Operands.Count != 2 || store.Operands.Count != 2
                || !Ref(operation.Operands[0], ac.Dest) || !Ref(operation.Operands[1], bc.Dest)
                || FrameStorage(a.Operands[0]) != left || FrameStorage(b.Operands[0]) != right || FrameStorage(store.Operands[0]) != destination
                || a.Offset != leftStart + lanes * 2 || b.Offset != rightStart + lanes * 2 || store.Offset != destStart + lanes * 4
                || a.Offset > left.Bytes - 2 || b.Offset > right.Bytes - 2 || store.Offset > destination.Bytes - 4) break;
            VReg? result = operation.Dest;
            if (integerResult)
            {
                Instr conversion = instructions[at + 5];
                if (conversion.Op != Opcode.FToI || !Single(conversion, IrType.I32) || conversion.Operands.Count != 1 || !Ref(conversion.Operands[0], result)) break;
                result = conversion.Dest;
            }
            if (!Ref(store.Operands[1], result)) break;
            lanes++;
        }
        lanes = lanes / 2 * 2;
        if (lanes < 4) return false;
        // 16-bit operands convert exactly. Their sum/difference/product cannot
        // overflow or underflow F32, and the exact product fits x87 precision
        // before rounding to F32. Signed zero is preserved by these operations.
        for (int lane = 0; lane < lanes; lane += 2)
        {
            Emit(MOp.MmxLoadD, MMem.Frame(_m.Frame.SlotOffset(left) + (int)leftStart + lane * 2)); ConvertPackedWords(first.Signed);
            Emit(MOp.MmxSaveToTwo);
            Emit(MOp.MmxLoadD, MMem.Frame(_m.Frame.SlotOffset(right) + (int)rightStart + lane * 2)); ConvertPackedWords(second.Signed);
            Emit(math.Op == Opcode.FAdd ? MOp.ThreeDNowAddFromTwo : math.Op == Opcode.FSub ? MOp.ThreeDNowSubFromTwo : MOp.ThreeDNowMulFromTwo);
            if (integerResult) Emit(MOp.ThreeDNowFloatToInt);
            Emit(MOp.MmxStore, MMem.Frame(_m.Frame.SlotOffset(destination) + (int)destStart + lane * 4));
        }
        EndMmx(); consumed = lanes * stride; return true;
    }
}
