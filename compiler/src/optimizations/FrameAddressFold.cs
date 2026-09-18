#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Expose in-bounds compiler-owned frame accesses hidden behind
/// pointer copies and constant offsets. Memory operations remain in place;
/// the original address computation may die only through ordinary DCE.</summary>
public sealed class FrameAddressFold : IPass
{
    public string Name => "frame-address-fold";
    private readonly record struct Address(FrameSlot Slot, long Offset);

    public void Run(Function function)
    {
        if (function.Async is not null || IrTypes.Word != IrType.I32) return;
        Cfg cfg = new(function);
        Defs defs = new(cfg);
        Address? Resolve(Operand operand, Block useBlock, int useIndex, int depth)
        {
            if (operand is SlotOperand slot) return new(slot.Slot, 0);
            if (depth == 16 || operand is not RegOperand r || !defs.IsSingle(r.Reg)
                || defs.Site(r.Reg) is not { } site || !cfg.Dominates(site.Block, useBlock)
                || (site.Block == useBlock && site.Index >= useIndex)) return null;
            bool stable = defs.CanForward(r.Reg, site.Block, site.Index + 1, useBlock, useIndex)
                || (site.Block == function.Entry && cfg.Preds(function.Entry).Count == 0);
            if (!stable) return null;
            Instr definition = site.Block.Instrs[site.Index];
            if (definition.Op is Opcode.Copy or Opcode.ZExt32 or Opcode.Trunc64)
                return Resolve(definition.Operands[0], site.Block, site.Index, depth + 1);
            if (definition.Op is Opcode.Add or Opcode.Sub && definition.Operands[1] is ImmOperand displacement
                && Resolve(definition.Operands[0], site.Block, site.Index, depth + 1) is { } address)
            {
                try
                {
                    long offset = definition.Op == Opcode.Add ? checked(address.Offset + displacement.Value)
                        : checked(address.Offset - displacement.Value);
                    // Restrict every intermediate address to its own frame
                    // object, avoiding pointer truncation/wrap assumptions.
                    return offset >= 0 && offset <= address.Slot.Bytes ? new(address.Slot, offset) : null;
                }
                catch (OverflowException) { return null; }
            }
            return null;
        }
        foreach (Block block in function.Blocks)
        for (int index = 0; index < block.Instrs.Count; index++)
        {
            Instr i = block.Instrs[index];
            if (i.Op is not (Opcode.Load or Opcode.Store or Opcode.MemSet) || i.Operands.Count == 0
                || i.Operands[0] is SlotOperand || Resolve(i.Operands[0], block, index, 0) is not { } address) continue;
            if (i.Op == Opcode.MemSet)
            {
                if (address.Offset == 0 && i.Operands[2] is ImmOperand count && count.Value >= 0
                    && count.Value <= address.Slot.Bytes) i.Operands[0] = new SlotOperand(address.Slot);
                continue;
            }
            long offset;
            try { offset = checked(address.Offset + i.Offset); }
            catch (OverflowException) { continue; }
            if (i.Size <= 0 || offset < 0 || offset > address.Slot.Bytes - i.Size) continue;
            Instr replacement = new() { Op = i.Op, Dest = i.Dest, Size = i.Size,
                Signed = i.Signed, Offset = offset, Line = i.Line };
            replacement.Operands.Add(new SlotOperand(address.Slot));
            replacement.Operands.AddRange(i.Operands.Skip(1));
            block.Instrs[index] = replacement;
        }
    }
}
