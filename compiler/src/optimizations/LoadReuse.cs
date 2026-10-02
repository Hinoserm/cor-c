#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Reuse successful ordinary reads on a single-predecessor dominated
/// path. Unlike SSA GVN, both the address and captured result must still name
/// the same values at the consumer. Writes, calls and synchronization forget
/// all memory; joins and loop headers do not inherit memory facts.</summary>
public sealed class LoadReuse : IPass
{
    public string Name => "load-reuse";
    private readonly record struct Entry(Instr Load, Block Block, int Index);
    private readonly record struct Key(object Base, long Offset, int Size, bool Signed, IrType Type);

    public void Run(Function function)
    {
        if (function.Async is not null) return;
        Cfg cfg = new(function);
        if (cfg.Roots.Count != 1) return;
        Defs defs = new(cfg);
        bool Stable(VReg register, Block from, int fromIndex, Block use, int useIndex)
        {
            if (defs.CanForward(register, from, fromIndex, use, useIndex)) return true;
            // Defs deliberately treats every CFG root like an exception
            // landing pad. Here multiple roots/async have been excluded;
            // a sole entry with no incoming edges cannot be re-entered.
            return defs.IsSingle(register) && defs.Site(register) is { } site
                && site.Block == function.Entry && cfg.Preds(function.Entry).Count == 0
                && (from != function.Entry || site.Index < fromIndex)
                && cfg.Dominates(function.Entry, from) && cfg.Dominates(function.Entry, use);
        }
        // By the graph's block order; a table only for a block that has
        // something in it and a successor it is the only way into.
        Dictionary<Key, Entry>?[] outgoing = new Dictionary<Key, Entry>?[function.Blocks.Count];
        foreach (Block block in cfg.ReversePostorder)
        {
            // One table a block: made empty, then made again from the
            // predecessor's, was two, and the first the collector's.
            var predecessors = cfg.Preds(block);
            Dictionary<Key, Entry>? memory = !cfg.IsRoot(block) && predecessors.Count == 1 && cfg.Dominates(predecessors[0], block)
                && outgoing[predecessors[0].Order] is { } inherited
                ? new(inherited) : null;
            for (int index = 0; index < block.Instrs.Count; index++)
            {
                Instr i = block.Instrs[index];
                if (i.Op == Opcode.Load && i.Dest is { } result && result.Type.IsInt()
                    && Address(i, out Key key))
                {
                    if (memory is not null && memory.TryGetValue(key, out Entry prior)
                        && Stable(prior.Load.Dest!, prior.Block, prior.Index + 1, block, index)
                        && (i.Operands[0] is not RegOperand address
                            || Stable(address.Reg, prior.Block, prior.Index, block, index)))
                    {
                        block.Instrs[index] = IrInfo.CopyOf(i, new RegOperand(prior.Load.Dest!));
                        continue;
                    }
                    memory ??= new();
                    if (memory.Count >= 64) memory.Clear();
                    memory[key] = new(i, block, index);
                }
                else if (!IrInfo.IsPure(i) && i.Op is not (Opcode.Branch or Opcode.Jump))
                    memory?.Clear();
            }
            if (memory is { Count: > 0 })
                foreach (Block next in cfg.Succs(block))
                    if (cfg.Preds(next).Count == 1) { outgoing[block.Order] = memory; break; }
        }
    }

    private static bool Address(Instr i, out Key key)
    {
        key = default;
        if (i.Size is not (1 or 2 or 4 or 8) || i.Operands.Count != 1) return false;
        object? identity = i.Operands[0] switch
        {
            RegOperand r => r.Reg, SlotOperand s => s.Slot, SymOperand s => s.Name, _ => null,
        };
        if (identity is null) return false;
        long offset = i.Offset;
        if (i.Operands[0] is SymOperand symbol)
        {
            try { offset = checked(offset + symbol.Offset); }
            catch (OverflowException) { return false; }
        }
        key = new(identity, offset, i.Size, i.Signed, i.Dest!.Type);
        return true;
    }
}
