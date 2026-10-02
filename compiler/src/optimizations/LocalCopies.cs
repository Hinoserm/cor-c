#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Forward current copies within blocks and unambiguous dominated edges.</summary>
public sealed class LocalCopies : IPass
{
    public string Name => "local-copies";

    public void Run(Function f)
    {
        // ONE TABLE, SCOPED (Cfg.WalkSolePredecessors): a block with one
        // predecessor, not a root, starts from that predecessor's copies as
        // they stood at its end -- every way in is through it -- and what it
        // learns is undone when the walk leaves it. A table copied for every
        // such block, kept for its successors, was this pass's whole cost.
        // ONE SCOPE A THREAD, kept: its tables are empty again when a walk
        // ends (every change is undone), and keep their storage for the next
        // function -- made per run, they and their growth were the collector's.
        new Cfg(f).WalkSolePredecessors(_scope ??= new Scope(), dominating: false);
    }

    [ThreadStatic] private static Scope? _scope;

    private sealed class Scope : Cfg.IScopedWalk
    {
        private readonly Dictionary<VReg, Operand> _copies = new();
        private readonly List<(VReg Key, Operand? Was)> _undo = new();
        private readonly List<VReg> _stale = new();

        public int Mark => _undo.Count;

        public void Undo(int mark)
        {
            for (int u = _undo.Count - 1; u >= mark; u--)
            {
                (VReg key, Operand? was) = _undo[u];
                if (was is not null) _copies[key] = was;
                else _copies.Remove(key);
            }
            _undo.RemoveRange(mark, _undo.Count - mark);
        }

        private void Forget(VReg key)
        {
            if (!_copies.TryGetValue(key, out Operand? was)) return;
            _undo.Add((key, was));
            _copies.Remove(key);
        }

        public void Visit(Corsac.Lang.Ir.Block block)
        {
            foreach (Instr i in block.Instrs)
            {
                // Phi operands refer to predecessor edges, not this position.
                if (i.Op == Opcode.Phi)
                {
                    foreach (var pair in _copies) _stale.Add(pair.Key);
                    foreach (VReg gone in _stale) Forget(gone);
                    _stale.Clear();
                    continue;
                }
                if (_copies.Count > 0) IrInfo.ReplaceUses(i, _copies);
                if (i.Dest is not { } dest) continue;
                // Entries are canonical one-hop values. Redefining their
                // source invalidates every captured alias before new facts.
                if (_copies.Count > 0)
                {
                    Forget(dest);
                    foreach (var pair in _copies)
                        if (pair.Value is RegOperand held && held.Reg == dest) _stale.Add(pair.Key);
                    foreach (VReg alias in _stale) Forget(alias);
                    _stale.Clear();
                }
                if (i.Op == Opcode.Copy && i.Operands.Count == 1 && i.Operands[0].Type == dest.Type
                    && (i.Operands[0] is ImmOperand || i.Operands[0] is RegOperand r && r.Reg != dest))
                {
                    _copies.TryGetValue(dest, out Operand? was);
                    _undo.Add((dest, was));
                    _copies[dest] = i.Operands[0];
                }
            }
        }
    }
}
