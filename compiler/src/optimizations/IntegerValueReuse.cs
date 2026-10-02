#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Integer expression reuse on unambiguous paths without assuming SSA.</summary>
public sealed class IntegerValueReuse : IPass
{
    public string Name => "integer-value-reuse";
    /// <summary>A value's register and the at most two registers it was made of.</summary>
    private readonly record struct Value(VReg Result, VReg? First, VReg? Second)
    {
        public bool Reads(VReg r) => First == r || Second == r;
    }

    /// <summary>
    /// An expression: its operation, its type and its two operands, each a
    /// register or a constant of a type, compared by value. It was a string
    /// spelled out of all of them, a dozen allocations for every arithmetic
    /// instruction of every function on every round.
    /// </summary>
    private readonly record struct Key(Opcode Op, IrType Type, IrType FirstType, bool FirstReg, long First,
        IrType SecondType, bool SecondReg, long Second);

    public void Run(Function f)
    {
        Cfg cfg = new(f);
        // ONE TABLE, SCOPED. A block with one predecessor (not a root) starts
        // from what that predecessor's table held at its end: every way in is
        // through it (LocalCopies). Walked as the tree those predecessors make
        // (Cfg.WalkSolePredecessors), each change logged and undone when the
        // walk leaves the block's subtree -- a table copied for every such
        // block, and kept for its successors, was the pass's whole allocation
        // and none of it freed. Per run, not per pass: the pipeline runs one
        // pass object on many functions at once (FunctionWorkers).
        // ONE SCOPE A THREAD, kept: its tables are empty again when a walk
        // ends (every change is undone), and keep their storage for the next
        // function -- made per run, they and their growth were the collector's.
        cfg.WalkSolePredecessors(_scope ??= new Scope(), dominating: false);
    }

    [ThreadStatic] private static Scope? _scope;

    private sealed class Scope : Cfg.IScopedWalk
    {

        // The scoped table and its log: (key, what it held, whether it held one).
        private readonly Dictionary<Key, Value> _values = new();
        private readonly List<(Key Key, Value Was, bool Had)> _undo = new();
        private readonly Dictionary<VReg, RegOperand> _aliases = new();
        private readonly List<VReg> _staleAliases = new();
        private readonly List<Key> _staleValues = new();
        public int Mark => _undo.Count;

        private void Set(Key key, Value value)
        {
            bool had = _values.TryGetValue(key, out Value was);
            _undo.Add((key, was, had));
            _values[key] = value;
        }

        private void Forget(Key key)
        {
            if (!_values.TryGetValue(key, out Value was)) return;
            _undo.Add((key, was, true));
            _values.Remove(key);
        }

        private void ForgetAll()
        {
            foreach (var pair in _values) _staleValues.Add(pair.Key);
            foreach (Key stale in _staleValues) Forget(stale);
            _staleValues.Clear();
        }

        public void Undo(int mark)
        {
            for (int u = _undo.Count - 1; u >= mark; u--)
            {
                (Key key, Value was, bool had) = _undo[u];
                if (had) _values[key] = was;
                else _values.Remove(key);
            }
            _undo.RemoveRange(mark, _undo.Count - mark);
        }

        public void Visit(Corsac.Lang.Ir.Block block)
        {
            // ONE ALIAS TABLE, emptied per block, and the operand each alias
            // becomes made once per alias, not once per use.
            Dictionary<VReg, RegOperand> aliases = _aliases;
            aliases.Clear();
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr i = block.Instrs[k];
                if (i.Op == Opcode.Phi) { ForgetAll(); aliases.Clear(); continue; }
                // A CSE result is available to later expressions immediately,
                // not only after another whole pipeline round. Aliases are
                // canonical snapshots and are invalidated on either write.
                if (aliases.Count != 0) IrInfo.ReplaceUses(i, aliases);
                Key? key = KeyOf(i);
                RegOperand? reused = null;
                if (key is { } found && _values.TryGetValue(found, out Value existing))
                {
                    reused = aliases.GetValueOrDefault(existing.Result) ?? new RegOperand(existing.Result);
                    block.Instrs[k] = IrInfo.CopyOf(i, reused);
                }
                if (i.Dest is not { } dest) continue;
                aliases.Remove(dest);
                if (aliases.Count != 0)
                {
                    foreach (var pair in aliases)
                        if (pair.Value.Reg == dest) _staleAliases.Add(pair.Key);
                    foreach (VReg stale in _staleAliases) aliases.Remove(stale);
                    _staleAliases.Clear();
                }
                if (reused is not null && reused.Reg != dest)
                {
                    if (aliases.Count >= 256) aliases.Clear();
                    aliases[dest] = reused;
                }
                if (_values.Count > 0)
                {
                    foreach (var pair in _values)
                        if (pair.Value.Result == dest || pair.Value.Reads(dest)) _staleValues.Add(pair.Key);
                    foreach (Key stale in _staleValues) Forget(stale);
                    _staleValues.Clear();
                }
                if (key is { } made)
                {
                    (VReg? first, VReg? second) = Inputs(i);
                    // The key describes values before this instruction. If
                    // it overwrites an input, that key is no longer current.
                    if (first == dest || second == dest) continue;
                    if (_values.Count >= 128) ForgetAll();
                    Set(made, new(dest, first, second));
                }
            }
        }
    }

    /// <summary>The registers an instruction reads, each once: at most two here.</summary>
    private static (VReg? First, VReg? Second) Inputs(Instr i)
    {
        VReg? a = null, b = null;
        foreach (Operand o in i.Operands)
        {
            if (o is not RegOperand r) continue;
            if (a is null) a = r.Reg;
            else if (r.Reg != a) b = r.Reg;
        }
        return (a, b);
    }

    private static Key? KeyOf(Instr i)
    {
        if (i.Dest?.Type is not (IrType.I32 or IrType.I64)) return null;
        foreach (Operand o in i.Operands) if (o is not (RegOperand or ImmOperand)) return null;
        // No memory, calls, division traps, floating point, flags or atomics.
        bool binary = i.Op is Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor
            or Opcode.Shl or Opcode.ShrS or Opcode.ShrU or Opcode.Eq or Opcode.Ne
            or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU;
        bool unary = i.Op is Opcode.Neg or Opcode.Not or Opcode.ByteSwap or Opcode.SExt8 or Opcode.SExt16
            or Opcode.ZExt8 or Opcode.ZExt16 or Opcode.SExt32 or Opcode.ZExt32 or Opcode.Trunc64;
        if ((!binary || i.Operands.Count != 2) && (!unary || i.Operands.Count != 1)) return null;
        Operand first = i.Operands[0];
        Operand? second = binary ? i.Operands[1] : null;
        // Either order of a commutative pair is one expression: the operands
        // are put in a fixed order, which one being immaterial.
        if (second is not null && i.Op is Opcode.Add or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor or Opcode.Eq or Opcode.Ne
            && Order(first, second) > 0) (first, second) = (second, first);
        return new Key(i.Op, i.Dest.Type, first.Type, first is RegOperand, Word(first),
            second?.Type ?? IrType.Void, second is RegOperand, second is null ? 0 : Word(second));
    }

    private static long Word(Operand o) => o is RegOperand r ? r.Reg.Id : ((ImmOperand)o).Value;

    private static int Order(Operand a, Operand b)
    {
        if (a.Type != b.Type) return a.Type.CompareTo(b.Type);
        if ((a is RegOperand) != (b is RegOperand)) return a is RegOperand ? -1 : 1;
        return Word(a).CompareTo(Word(b));
    }
}
