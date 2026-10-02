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
        // ONE TABLE, SCOPED (Cfg.WalkSolePredecessors): a block whose sole
        // predecessor dominates it starts from that predecessor's table as it
        // ended, and what it adds is undone when the walk leaves it. A table
        // copied for every such block was this pass's whole allocation, and
        // the collector's.
        cfg.WalkSolePredecessors(new Scope(Stable), dominating: true);
    }

    private sealed class Scope : Cfg.IScopedWalk
    {
        private readonly Func<VReg, Block, int, Block, int, bool> _stable;
        private readonly Dictionary<Key, Entry> _memory = new();
        private readonly List<(Key Key, Entry Was, bool Had)> _undo = new();
        private readonly List<Key> _all = new();

        public Scope(Func<VReg, Block, int, Block, int, bool> stable) { _stable = stable; }

        public int Mark => _undo.Count;

        public void Undo(int mark)
        {
            for (int u = _undo.Count - 1; u >= mark; u--)
            {
                (Key key, Entry was, bool had) = _undo[u];
                if (had) _memory[key] = was;
                else _memory.Remove(key);
            }
            _undo.RemoveRange(mark, _undo.Count - mark);
        }

        private void Set(Key key, Entry entry)
        {
            bool had = _memory.TryGetValue(key, out Entry was);
            _undo.Add((key, was, had));
            _memory[key] = entry;
        }

        private void ForgetAll()
        {
            if (_memory.Count == 0) return;
            foreach (var pair in _memory) _all.Add(pair.Key);
            foreach (Key key in _all)
            {
                _undo.Add((key, _memory[key], true));
                _memory.Remove(key);
            }
            _all.Clear();
        }

        public void Visit(Block block)
        {
            for (int index = 0; index < block.Instrs.Count; index++)
            {
                Instr i = block.Instrs[index];
                if (i.Op == Opcode.Load && i.Dest is { } result && result.Type.IsInt()
                    && Address(i, out Key key))
                {
                    if (_memory.TryGetValue(key, out Entry prior)
                        && _stable(prior.Load.Dest!, prior.Block, prior.Index + 1, block, index)
                        && (i.Operands[0] is not RegOperand address
                            || _stable(address.Reg, prior.Block, prior.Index, block, index)))
                    {
                        block.Instrs[index] = IrInfo.CopyOf(i, new RegOperand(prior.Load.Dest!));
                        continue;
                    }
                    if (_memory.Count >= 64) ForgetAll();
                    Set(key, new(i, block, index));
                }
                else if (!IrInfo.IsPure(i) && i.Op is not (Opcode.Branch or Opcode.Jump))
                    ForgetAll();
            }
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
