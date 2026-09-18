#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>Remove unchanged write-backs to private, in-bounds frame storage.
/// Readability alone does not justify deleting a store through a pointer:
/// it could fault on write, or refer to device memory. Escaped slots, calls,
/// unknown writes and synchronization remain conservative barriers.</summary>
public sealed class StoreBackElimination : IPass
{
    public string Name => "store-back";
    private readonly record struct Region(FrameSlot Slot, long Offset, int Size);

    public void Run(Function function)
    {
        if (function.Async is not null) return;
        HashSet<FrameSlot> escaped = new();
        foreach (Instr instruction in function.Blocks.SelectMany(block => block.Instrs))
            for (int n = 0; n < instruction.Operands.Count; n++)
                if (instruction.Operands[n] is SlotOperand slot
                    && !(n == 0 && instruction.Op is Opcode.Load or Opcode.Store))
                    escaped.Add(slot.Slot);

        foreach (var block in function.Blocks)
        {
            Dictionary<Region, VReg> loaded = new();
            for (int index = 0; index < block.Instrs.Count; index++)
            {
                Instr i = block.Instrs[index];
                if (i.Dest is { } dest)
                    foreach (Region key in loaded.Where(pair => pair.Value == dest).Select(pair => pair.Key).ToArray())
                        loaded.Remove(key);
                Region? region = null;
                if (i.Op is Opcode.Load or Opcode.Store && i.Operands[0] is SlotOperand address
                    && !escaped.Contains(address.Slot) && i.Size is 1 or 2 or 4 or 8
                    && i.Offset >= 0 && i.Offset <= address.Slot.Bytes - i.Size)
                    region = new(address.Slot, i.Offset, i.Size);
                if (i.Op == Opcode.Load && region is { } read && i.Dest is { } value
                    && value.Type.IsInt() && value.Type.Bytes() >= i.Size)
                {
                    if (loaded.Count >= 64) loaded.Clear();
                    loaded[read] = value;
                }
                else if (i.Op == Opcode.Store && region is { } write)
                {
                    if (i.Operands[1] is RegOperand source && loaded.TryGetValue(write, out VReg? prior)
                        && prior == source.Reg)
                    {
                        block.Instrs.RemoveAt(index--);
                        continue;
                    }
                    foreach (Region key in loaded.Keys.Where(key => key.Slot == write.Slot
                        && key.Offset < write.Offset + write.Size && write.Offset < key.Offset + key.Size).ToArray())
                        loaded.Remove(key);
                }
                else if (!IrInfo.IsPure(i)) loaded.Clear();
            }
        }
    }
}
