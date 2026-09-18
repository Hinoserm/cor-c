#nullable enable
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Global value numbering and load forwarding, on SSA form, in one walk
/// down the dominator tree.
///
/// Values: a pure instruction whose opcode and operands match one already
/// computed in a dominating position is replaced by a copy of that
/// result. The table is scoped to the tree, so what is visible in a block
/// is exactly what dominates it, and in SSA a dominating definition is
/// always available. Commutative operands are ordered so <c>a+b</c> and
/// <c>b+a</c> meet.
///
/// Memory: a load whose address, offset and size match a store or load
/// with nothing in between that could have touched that memory takes the
/// stored value or the earlier load's result. "Nothing in between" is
/// judged within a block, and carried into a block whose only
/// predecessor is its dominator -- the arms of an if -- and reset at
/// every join and loop header, because a path around a loop or through
/// another arm can store without dominating. Two statics are distinct
/// memory, as are two frame slots and a static and a slot; an address in
/// a register could be anything, so a store through one forgets all, and
/// a store to anything forgets loads through registers. Calls, atomics,
/// fences, block moves and syscalls forget all. Lowering reloads the same
/// static several times in a row for one expression; this is where those
/// go.
/// </summary>
public sealed class Gvn : IPass
{
    public string Name => "gvn";

    private Function _f = null!;
    private Cfg _cfg = null!;

    /// <summary>A register standing for the same value as another: uses of the key become the value.</summary>
    private readonly Dictionary<VReg, Operand> _leader = new();

    /// <summary>Expression key to the register holding it, with an undo log per block.</summary>
    private readonly Dictionary<string, VReg> _exprs = new();

    private sealed record MemEntry(Operand Value, int Size, bool Signed, bool FromStore);

    public void Run(Function f)
    {
        _f = f;
        _cfg = new Cfg(f);
        _leader.Clear();
        _exprs.Clear();

        Dictionary<Block, Dictionary<string, MemEntry>> memAtEnd = new(ReferenceEqualityComparer.Instance);

        foreach (Block root in f.Blocks.Where(_cfg.IsRoot))
        {
            Stack<(Block Block, List<(string Key, VReg? Old)> Undo, bool Done)> walk = new();
            walk.Push((root, new List<(string, VReg?)>(), false));
            while (walk.Count > 0)
            {
                (Block b, List<(string Key, VReg? Old)> undo, bool done) = walk.Pop();
                if (done)
                {
                    for (int k = undo.Count - 1; k >= 0; k--)
                    {
                        if (undo[k].Old is null)
                        {
                            _exprs.Remove(undo[k].Key);
                        }
                        else
                        {
                            _exprs[undo[k].Key] = undo[k].Old!;
                        }
                    }
                    memAtEnd.Remove(b);
                    continue;
                }

                Dictionary<string, MemEntry> mem;
                Block? idom = _cfg.Idom(b);
                IReadOnlyList<Block> preds = _cfg.Preds(b);
                if (idom is not null && preds.Count == 1 && ReferenceEquals(preds[0], idom)
                    && memAtEnd.TryGetValue(idom, out Dictionary<string, MemEntry>? inherited))
                {
                    mem = new Dictionary<string, MemEntry>(inherited);
                }
                else
                {
                    mem = new Dictionary<string, MemEntry>();
                }

                VisitBlock(b, undo, mem);
                memAtEnd[b] = mem;

                walk.Push((b, undo, true));
                foreach (Block child in _cfg.DomChildren(b))
                {
                    walk.Push((child, new List<(string, VReg?)>(), false));
                }
            }
        }
    }

    private void VisitBlock(Block b, List<(string Key, VReg? Old)> undo, Dictionary<string, MemEntry> mem)
    {
        for (int k = 0; k < b.Instrs.Count; k++)
        {
            Instr i = b.Instrs[k];
            IrInfo.ReplaceUses(i, r => _leader.GetValueOrDefault(r));

            switch (i.Op)
            {
                case Opcode.Load:
                    {
                        string key = AddressKey(i.Operands[0], i.Offset);
                        if (mem.TryGetValue(key, out MemEntry? e) && e.Size == i.Size)
                        {
                            Operand? value = Forwardable(e, i);
                            if (value is not null)
                            {
                                b.Instrs[k] = IrInfo.CopyOf(i, value);
                                _leader[i.Dest!] = value;
                                continue;
                            }
                        }
                        mem[key] = new MemEntry(new RegOperand(i.Dest!), i.Size, i.Signed, false);
                        continue;
                    }

                case Opcode.Store:
                    {
                        Operand addr = i.Operands[0];
                        Forget(mem, addr, i.Offset, i.Size);
                        mem[AddressKey(addr, i.Offset)] = new MemEntry(i.Operands[1], i.Size, false, true);
                        continue;
                    }

                case Opcode.Call:
                case Opcode.CallIndirect:
                case Opcode.Syscall:
                case Opcode.MemCopy:
                case Opcode.MemSet:
                case Opcode.AtomicSwap:
                case Opcode.AtomicAdd:
                case Opcode.AtomicAnd:
                case Opcode.AtomicOr:
                case Opcode.AtomicXor:
                case Opcode.AtomicCas:
                case Opcode.Fence:
                    mem.Clear();
                    continue;
            }

            if (i.Dest is null || !IrInfo.IsPure(i) || i.Op is Opcode.Copy or Opcode.StackPointer or Opcode.FramePointer)
            {
                continue;
            }
            // Loads from statics and slots are pure by IsPure's reckoning
            // but were handled above; phis are keyed by their block.
            string ekey = ExprKey(b, i);
            if (_exprs.TryGetValue(ekey, out VReg? existing))
            {
                RegOperand value = new(existing);
                _leader[i.Dest] = value;
                // A phi must stay a phi at the top of its block; once its
                // uses are gone dead-code elimination removes it.
                if (i.Op != Opcode.Phi)
                {
                    b.Instrs[k] = IrInfo.CopyOf(i, value);
                }
            }
            else
            {
                undo.Add((ekey, null));
                _exprs[ekey] = i.Dest;
            }
        }
    }

    /// <summary>
    /// What a load may take from a remembered store or load: the exact
    /// same width, and for a store the stored value at the load's type.
    /// A narrow load of a wider store would need an extension; it is left
    /// to run.
    /// </summary>
    private static Operand? Forwardable(MemEntry e, Instr load)
    {
        IrType t = load.Dest!.Type;
        if (e.Value.Type != t)
        {
            return null;
        }
        if (e.FromStore)
        {
            return load.Size == t.Bytes() ? e.Value : null;
        }
        return e.Signed == load.Signed || load.Size == t.Bytes() ? e.Value : null;
    }

    /// <summary>A store to (addr + offset, size) happened: drop what it may have changed.</summary>
    private static void Forget(Dictionary<string, MemEntry> mem, Operand addr, long offset, int size)
    {
        if (addr is RegOperand or ImmOperand)
        {
            mem.Clear();        // could be anywhere, including a static or slot
            return;
        }
        string prefix = BaseKey(addr) + "+";
        List<string> dead = new();
        foreach (string key in mem.Keys)
        {
            if (key[0] is 'r' or 'i')
            {
                dead.Add(key);      // a register or absolute address may point anywhere, including here
                continue;
            }
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;           // another static or slot: distinct memory
            }
            int plus = key.LastIndexOf('+');
            long theirOffset = long.Parse(key.AsSpan(plus + 1));
            int theirSize = mem[key].Size;
            if (theirOffset < offset + size && offset < theirOffset + theirSize)
            {
                dead.Add(key);
            }
        }
        foreach (string key in dead)
        {
            mem.Remove(key);
        }
    }

    private static string BaseKey(Operand addr) => addr switch
    {
        SymOperand s => $"s{s.Name}+{s.Offset}",
        SlotOperand s => $"l{s.Slot.Id}",
        RegOperand r => $"r{r.Reg.Id}",
        ImmOperand i => $"i{i.Value}",
        _ => "?",
    };

    private static string AddressKey(Operand addr, long offset)
    {
        // A symbol's own offset folds into the displacement so @g+4 and
        // @g with +4 are the same place.
        if (addr is SymOperand s)
        {
            return $"s{s.Name}+{s.Offset + offset}";
        }
        return $"{BaseKey(addr)}+{offset}";
    }

    private static string OperandKey(Operand o) => o switch
    {
        RegOperand r => $"r{r.Reg.Id}",
        ImmOperand i => $"i{i.Type}:{IrInfo.Normalise(i.Value, i.Type)}",
        SymOperand s => $"s{s.Name}+{s.Offset}",
        SlotOperand s => $"l{s.Slot.Id}",
        _ => "?",
    };

    private static bool Commutative(Opcode op) => op is Opcode.Add or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor
        or Opcode.Eq or Opcode.Ne or Opcode.FAdd or Opcode.FMul or Opcode.FEq or Opcode.FNe;

    private static string ExprKey(Block b, Instr i)
    {
        StringBuilder sb = new();
        sb.Append(i.Op).Append('|').Append(i.Dest!.Type).Append('|');
        if (i.Op == Opcode.Phi)
        {
            sb.Append(b.Label).Append('|');
            for (int k = 0; k < i.Operands.Count; k++)
            {
                sb.Append(i.Targets[k].Label).Append('=').Append(OperandKey(i.Operands[k])).Append(',');
            }
            return sb.ToString();
        }
        if (i.Op == Opcode.LabelAddr)
        {
            sb.Append(i.Targets[0].Label);
            return sb.ToString();
        }
        List<string> keys = i.Operands.Select(OperandKey).ToList();
        if (Commutative(i.Op) && keys.Count == 2 && string.CompareOrdinal(keys[0], keys[1]) > 0)
        {
            (keys[0], keys[1]) = (keys[1], keys[0]);
        }
        sb.Append(string.Join(",", keys));
        return sb.ToString();
    }
}
