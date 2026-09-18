using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private bool TryPackedShifts(List<Instr> instructions, int start, out int consumed)
    {
        consumed = 0;
        if (!_automaticPacked || !Target.Current.X86Profile.Mmx || start + 5 >= instructions.Count) return false;
        Instr first = instructions[start], firstShift = instructions[start + 1], firstStore = instructions[start + 2];
        int width = first.Size;
        if (width is not (2 or 4) || first.Operands.Count != 1 || firstShift.Operands.Count != 2 || firstStore.Operands.Count != 2
            || firstShift.Op is not (Opcode.Shl or Opcode.ShrS or Opcode.ShrU) || firstShift.Operands[1] is not ImmOperand count) return false;
        // A signed word logically shifted as an int includes sign-extension bits.
        // PSRLW would discard those bits, so this is not a legal fold.
        if (width == 2 && first.Signed && firstShift.Op == Opcode.ShrU) return false;
        FrameSlot? source = FrameStorage(first.Operands[0]), destination = FrameStorage(firstStore.Operands[0]);
        if (source is null || destination is null || source == destination || source.Align < 4 || destination.Align < 4) return false;
        long srcStart = first.Offset, destStart = firstStore.Offset;
        if (srcStart < 0 || destStart < 0 || srcStart > source.Bytes || destStart > destination.Bytes || ((srcStart | destStart) & 3) != 0) return false;
        bool Single(Instr instruction) => instruction.Dest is { Type: IrType.I32 } value
            && _useCount.GetValueOrDefault(value.Id) == 1 && _definitions.GetValueOrDefault(value) == instruction;
        bool Ref(Operand operand, VReg? value) => operand is RegOperand register && register.Reg == value;
        int lanes = 0;
        while (start + lanes * 3 + 2 < instructions.Count && lanes * width < 128)
        {
            int at = start + lanes * 3;
            Instr load = instructions[at], shift = instructions[at + 1], store = instructions[at + 2];
            if (load.Op != Opcode.Load || shift.Op != firstShift.Op || store.Op != Opcode.Store || load.Size != width || store.Size != width
                || load.Signed != first.Signed || !Single(load) || !Single(shift) || load.Operands.Count != 1 || shift.Operands.Count != 2 || store.Operands.Count != 2
                || !Ref(shift.Operands[0], load.Dest) || !Ref(store.Operands[1], shift.Dest) || shift.Operands[1] is not ImmOperand otherCount
                || (otherCount.Value & 31) != (count.Value & 31) || FrameStorage(load.Operands[0]) != source || FrameStorage(store.Operands[0]) != destination
                || load.Offset != srcStart + lanes * width || store.Offset != destStart + lanes * width
                || load.Offset > source.Bytes - width || store.Offset > destination.Bytes - width) break;
            lanes++;
        }
        int bytes = lanes * width / 8 * 8;
        if (bytes < 16) return false;
        bool signed = firstShift.Op == Opcode.ShrS && (width == 4 || first.Signed);
        MOp packed = firstShift.Op == Opcode.Shl ? (width == 2 ? MOp.MmxShlW : MOp.MmxShlD)
            : signed ? (width == 2 ? MOp.MmxSarW : MOp.MmxSarD) : (width == 2 ? MOp.MmxShrW : MOp.MmxShrD);
        for (int offset = 0; offset < bytes; offset += 8)
        {
            Emit(MOp.MmxLoad, MMem.Frame(_m.Frame.SlotOffset(source) + (int)srcStart + offset));
            Emit(packed, Imm(count.Value & 31));
            Emit(MOp.MmxStore, MMem.Frame(_m.Frame.SlotOffset(destination) + (int)destStart + offset));
        }
        EndMmx(); consumed = bytes / width * 3; return true;
    }
}
