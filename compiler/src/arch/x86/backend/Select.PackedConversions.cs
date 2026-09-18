using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private void ConvertPackedWords(bool signed)
    {
        if (signed)
        {
            Emit(MOp.MmxDuplicateLowWords);
            if (Target.Current.X86Profile.ThreeDNowExtended) Emit(MOp.ThreeDNowShortToFloat);
            else { Emit(MOp.MmxSarD, Imm(16)); Emit(MOp.ThreeDNowIntToFloat); }
        }
        else { Emit(MOp.MmxWidenUnsignedWords); Emit(MOp.ThreeDNowIntToFloat); }
    }
    private bool TryPackedConversions(List<Instr> instructions, int start, out int consumed)
    {
        consumed = 0;
        if (!_automaticPacked || !Target.Current.X86Profile.ThreeDNow || start + 5 >= instructions.Count) return false;
        Instr first = instructions[start], convert = instructions[start + 1], firstStore = instructions[start + 2];
        if (first.Size != 2 || first.Operands.Count != 1 || firstStore.Operands.Count != 2
            || convert.Op is not (Opcode.IToF or Opcode.UToF) || (first.Signed && convert.Op == Opcode.UToF)) return false;
        FrameSlot? source = FrameStorage(first.Operands[0]), destination = FrameStorage(firstStore.Operands[0]);
        if (source is null || destination is null || source == destination || source.Align < 4 || destination.Align < 4) return false;
        long srcStart = first.Offset, destStart = firstStore.Offset;
        if (srcStart < 0 || destStart < 0 || srcStart > source.Bytes || destStart > destination.Bytes || ((srcStart | destStart) & 3) != 0) return false;
        bool Single(Instr instruction, IrType type) => instruction.Dest is { } value && value.Type == type
            && _useCount.GetValueOrDefault(value.Id) == 1 && _definitions.GetValueOrDefault(value) == instruction;
        bool Ref(Operand operand, VReg? value) => operand is RegOperand register && register.Reg == value;
        int lanes = 0;
        while (start + lanes * 3 + 2 < instructions.Count && lanes < 32)
        {
            int at = start + lanes * 3;
            Instr load = instructions[at], conversion = instructions[at + 1], store = instructions[at + 2];
            if (load.Op != Opcode.Load || conversion.Op != convert.Op || store.Op != Opcode.Store || load.Size != 2 || store.Size != 4
                || load.Signed != first.Signed || !Single(load, IrType.I32) || !Single(conversion, IrType.F32)
                || load.Operands.Count != 1 || conversion.Operands.Count != 1 || store.Operands.Count != 2
                || !Ref(conversion.Operands[0], load.Dest) || !Ref(store.Operands[1], conversion.Dest)
                || FrameStorage(load.Operands[0]) != source || FrameStorage(store.Operands[0]) != destination
                || load.Offset != srcStart + lanes * 2 || store.Offset != destStart + lanes * 4
                || load.Offset > source.Bytes - 2 || store.Offset > destination.Bytes - 4) break;
            lanes++;
        }
        lanes = lanes / 2 * 2;
        if (lanes < 4) return false;
        // Every signed/unsigned 16-bit input is exactly representable in F32.
        // Thus PI2FD's truncating conversion cannot change the C# result.
        for (int lane = 0; lane < lanes; lane += 2)
        {
            Emit(MOp.MmxLoadD, MMem.Frame(_m.Frame.SlotOffset(source) + (int)srcStart + lane * 2));
            ConvertPackedWords(first.Signed);
            Emit(MOp.MmxStore, MMem.Frame(_m.Frame.SlotOffset(destination) + (int)destStart + lane * 4));
        }
        EndMmx(); consumed = lanes * 3; return true;
    }
}
