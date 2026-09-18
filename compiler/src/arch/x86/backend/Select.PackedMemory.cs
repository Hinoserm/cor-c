using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private FrameSlot? FrameStorage(Operand address)
    {
        for (int depth = 0; depth < 8 && address is RegOperand register; depth++)
        {
            if (!_definitions.TryGetValue(register.Reg, out Instr? definition) || definition is null
                || definition.Op != Opcode.Copy || definition.Operands.Count != 1) return null;
            address = definition.Operands[0];
        }
        return address is SlotOperand slot ? slot.Slot : null;
    }

    private bool PackedMemorySize(long bytes) => _automaticPacked && Target.Current.X86Profile.Mmx && bytes >= 32 && bytes <= 128;

    private void EndMmx() => Emit(Target.Current.X86Profile.ThreeDNow ? MOp.Femms : MOp.Emms);

    // Unrolled MOVQ regions trade bytes for throughput. Do not grow setup or
    // conditional allocation paths: they enlarged real crypto functions without
    // improving their measured hot path. Require the block to dominate a loop
    // backedge, not merely occur in a conditional arm inside a loop.
    private HashSet<Corsac.Lang.Ir.Block>? _repeatingMemory;
    private bool RepeatingPackedMemory => (_repeatingMemory ??= RepeatingRegions.Find(_f)).Contains(_sourceBlock);

    private bool SelectMmxFrameCopy(Instr instruction)
    {
        if (instruction.Operands[2] is not ImmOperand count || !PackedMemorySize(count.Value) || !RepeatingPackedMemory) return false;
        FrameSlot? destination = FrameStorage(instruction.Operands[0]), source = FrameStorage(instruction.Operands[1]);
        // Widen only proven ordinary, non-overlapping frame storage. Never
        // widen volatile/device accesses or touch a byte outside either object.
        if (destination is null || source is null || destination == source || destination.Align < 4 || source.Align < 4
            || count.Value > destination.Bytes || count.Value > source.Bytes) return false;
        int dest = _m.Frame.SlotOffset(destination), src = _m.Frame.SlotOffset(source);
        int offset = 0;
        while (offset + 8 <= count.Value)
        {
            Emit(MOp.MmxLoad, MMem.Frame(src + offset)); Emit(MOp.MmxStore, MMem.Frame(dest + offset)); offset += 8;
        }
        EndMmx();
        for (; offset < count.Value; offset++)
        {
            MReg tail = Temp(); EmitW(MOp.Movzx, 1, tail, MMem.Frame(src + offset));
            EmitW(MOp.Mov, 1, MMem.Frame(dest + offset), tail);
        }
        return true;
    }

    private bool SelectMmxFrameZero(Instr instruction)
    {
        if (instruction.Operands[2] is not ImmOperand count || !PackedMemorySize(count.Value)
            || instruction.Operands[1] is not ImmOperand fill || (byte)fill.Value != 0) return false;
        // The existing <=32-byte frame-zero path is unrolled scalar stores, so
        // packing 32 bytes shrinks code even outside a loop. Larger REP forms
        // are compact and stay that way unless the region is known to repeat.
        if (count.Value > 32 && !RepeatingPackedMemory) return false;
        FrameSlot? destination = FrameStorage(instruction.Operands[0]);
        if (destination is null || destination.Align < 4 || count.Value > destination.Bytes) return false;
        int dest = _m.Frame.SlotOffset(destination), offset = 0;
        Emit(MOp.MmxZero);
        while (offset + 8 <= count.Value) { Emit(MOp.MmxStore, MMem.Frame(dest + offset)); offset += 8; }
        EndMmx();
        for (; offset < count.Value; offset++) EmitW(MOp.Mov, 1, MMem.Frame(dest + offset), Imm(0));
        return true;
    }
}
