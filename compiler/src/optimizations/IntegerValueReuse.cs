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
        // A block's table as it ends, for the successors that inherit it;
        // one with no such successor keeps none.
        Dictionary<Corsac.Lang.Ir.Block, Dictionary<Key, Value>> atEnd = new();
        // ONE ALIAS TABLE, emptied per block, and the operand each alias
        // becomes made once per alias, not once per use.
        Dictionary<VReg, RegOperand> aliases = new();
        Func<VReg, Operand?> alias = r => aliases.TryGetValue(r, out RegOperand? source) ? source : null;
        List<VReg> staleAliases = new();
        List<Key> staleValues = new();
        foreach (var block in cfg.ReversePostorder)
        {
            Dictionary<Key, Value>? values = null;
            aliases.Clear();
            var predecessors = cfg.Preds(block);
            if (!cfg.IsRoot(block) && predecessors.Count == 1 && cfg.Dominates(predecessors[0], block)
                && atEnd.TryGetValue(predecessors[0], out var inherited))
                // The only successor takes the table itself: nobody else reads it.
                values = cfg.Succs(predecessors[0]).Count == 1 ? inherited : new(inherited);
            values ??= new();
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr i = block.Instrs[k];
                if (i.Op == Opcode.Phi) { values.Clear(); aliases.Clear(); continue; }
                // A CSE result is available to later expressions immediately,
                // not only after another whole pipeline round. Aliases are
                // canonical snapshots and are invalidated on either write.
                if (aliases.Count != 0) IrInfo.ReplaceUses(i, alias);
                Key? key = KeyOf(i);
                RegOperand? reused = null;
                if (key is { } found && values.TryGetValue(found, out Value existing))
                {
                    reused = aliases.GetValueOrDefault(existing.Result) ?? new RegOperand(existing.Result);
                    block.Instrs[k] = IrInfo.CopyOf(i, reused);
                }
                if (i.Dest is not { } dest) continue;
                aliases.Remove(dest);
                if (aliases.Count != 0)
                {
                    foreach (var pair in aliases)
                        if (pair.Value.Reg == dest) staleAliases.Add(pair.Key);
                    foreach (VReg stale in staleAliases) aliases.Remove(stale);
                    staleAliases.Clear();
                }
                if (reused is not null && reused.Reg != dest)
                {
                    if (aliases.Count >= 256) aliases.Clear();
                    aliases[dest] = reused;
                }
                if (values.Count != 0)
                {
                    foreach (var pair in values)
                        if (pair.Value.Result == dest || pair.Value.Reads(dest)) staleValues.Add(pair.Key);
                    foreach (Key stale in staleValues) values.Remove(stale);
                    staleValues.Clear();
                }
                if (key is { } made)
                {
                    (VReg? first, VReg? second) = Inputs(i);
                    // The key describes values before this instruction. If
                    // it overwrites an input, that key is no longer current.
                    if (first == dest || second == dest) continue;
                    if (values.Count >= 128) values.Clear();
                    values[made] = new(dest, first, second);
                }
            }
            if (cfg.Succs(block).Any(next => cfg.Preds(next).Count == 1)) atEnd[block] = values;
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
