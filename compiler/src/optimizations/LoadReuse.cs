#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Reuse successful ordinary reads on a single-predecessor dominated
/// path. Unlike SSA GVN, both the address and captured result must still name
/// the same values at the consumer. Calls and synchronization forget all
/// memory, and so does a write, but where the write and a read are told
/// apart (Managed below): a static's own cell and an object's field or an
/// array's element are never the same word. Joins and loop headers do not
/// inherit memory facts.</summary>
public sealed class LoadReuse : IPass
{
    public string Name => "load-reuse";
    private readonly record struct Entry(Instr Load, Block Block, int Index, bool Managed);
    private readonly record struct Key(object Base, long Offset, int Size, bool Signed, IrType Type);

    /// <summary>The Size of an array length's key: no load has it.</summary>
    private const int Length = -1;

    // ONE GRAPH WHILE ITS EDGES STAND, in a pipeline or not (Devirtualize's
    // rounds run it on their own): outside one, every walk made its own Cfg
    // -- three a run, and twenty thousand runs a unit (PipelineAnalyses).
    public void Run(Function function)
    {
        bool kept = PipelineAnalyses.Begin(function);
        try { RunInScope(function); }
        finally { if (kept) PipelineAnalyses.End(); }
    }

    private void RunInScope(Function function)
    {
        if (function.Async is not null) return;
        // NO LOAD TO REUSE, NO GRAPH: a function without one gets no Cfg and
        // no Defs. The blocks are numbered as the Cfg would have numbered
        // them, for whatever reads Order next.
        if (!AnyCandidate(function))
        {
            for (int k = 0; k < function.Blocks.Count; k++) function.Blocks[k].Order = k;
            return;
        }
        Cfg cfg = PipelineAnalyses.CfgOf(function);
        if (cfg.Roots.Count != 1) return;
        Defs defs = PipelineAnalyses.DefsOf(function);
        bool Stable(VReg register, Block from, int fromIndex, Block use, int useIndex)
        {
            if (defs.CanForward(register, from, fromIndex, use, useIndex)) return true;
            // (Defs answers for the entry as for any block now; this is
            // what answered when it took every root for a landing pad, and
            // still does for a sole entry nothing jumps back to.)
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
        // ONE SCOPE A THREAD, kept: its tables are empty again when a walk
        // ends (every change is undone), and keep their storage for the next
        // function -- made per run, they and their growth were the collector's.
        // AN OBJECT'S MEMORY, AS AN ADDRESS SAYS IT: a field's (the load or
        // store names one) or an array's element (its base, through adds and
        // copies, a register this function asks the length of -- an array
        // checked before it is indexed). Neither is ever a static's own cell
        // (s_, ts_: data items of their own, which no object lies over), so
        // a write to the one leaves a read of the other where it was. A
        // pointer the program made of a static (Sys.AddressOf) is neither.
        HashSet<VReg> arrays = new();
        foreach (Block b in function.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.ArrayLength && i.Operands.Count == 1 && i.Operands[0] is RegOperand { Reg: var array }) arrays.Add(array);
        // An address made of a static's (a copy of its symbol, through adds):
        // never an object's, whatever the access says of itself.
        bool SymbolBased(VReg r, int depth)
        {
            if (depth == 0 || !defs.IsSingle(r) || defs.Site(r) is not { } site) return false;
            Instr d = site.Block.Instrs[site.Index];
            if (d.Op == Opcode.Copy && d.Operands is [SymOperand]) return true;
            if (d.Op is not (Opcode.Add or Opcode.Copy)) return false;
            foreach (Operand o in d.Operands)
                if (o is RegOperand { Reg: var from } && SymbolBased(from, depth - 1)) return true;
            return false;
        }
        bool ArrayBased(VReg r, int depth)
        {
            if (arrays.Contains(r)) return true;
            if (depth == 0 || !defs.IsSingle(r) || defs.Site(r) is not { } site) return false;
            Instr d = site.Block.Instrs[site.Index];
            if (d.Op is not (Opcode.Add or Opcode.Copy)) return false;
            foreach (Operand o in d.Operands)
                if (o is RegOperand { Reg: var from } && ArrayBased(from, depth - 1)) return true;
            return false;
        }
        Scope scope = _scope ??= new Scope();
        scope.Stable = Stable;
        scope.Managed = i => i.Operands.Count > 0 && i.Operands[0] is RegOperand { Reg: var r } && !SymbolBased(r, 6)
            && (i.Field is not null || ArrayBased(r, 6));
        cfg.WalkSolePredecessors(scope, dominating: true);
        scope.Stable = null!;
        scope.Managed = null!;
    }

    [ThreadStatic] private static Scope? _scope;

    private sealed class Scope : Cfg.IScopedWalk
    {
        public Func<VReg, Block, int, Block, int, bool> Stable = null!;
        public Func<Instr, bool> Managed = null!;
        private readonly Dictionary<Key, Entry> _memory = new();
        private readonly List<(Key Key, Entry Was, bool Had)> _undo = new();
        private readonly List<Key> _all = new();

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

        /// <summary>A static's own cell: its symbol's name.</summary>
        private static bool StaticCell(object identity)
            => identity is string name && (name.StartsWith("s_", StringComparison.Ordinal) || name.StartsWith("ts_", StringComparison.Ordinal));

        /// <summary>
        /// A write: what it may have changed forgotten. Through an object's
        /// field or an array's element, every read but a static cell's; to a
        /// static's cell, that symbol's reads and every read through a
        /// register but an object's field or element (a frame slot, another
        /// symbol stay); anything else, all.
        /// </summary>
        private void Written(Instr store)
        {
            if (store.Operands.Count > 0 && store.Operands[0] is RegOperand && Managed(store))
            {
                Forget(pair => !StaticCell(pair.Key.Base));
                return;
            }
            if (store.Operands.Count > 0 && store.Operands[0] is SymOperand { Name: var cell } && StaticCell(cell))
            {
                Forget(pair => pair.Key.Base is string name ? name == cell : pair.Key.Base is VReg && !pair.Value.Managed);
                return;
            }
            ForgetAll();
        }

        private void Forget(Func<KeyValuePair<Key, Entry>, bool> gone)
        {
            if (_memory.Count == 0) return;
            foreach (var pair in _memory) if (pair.Key.Size != Length && gone(pair)) _all.Add(pair.Key);
            foreach (Key key in _all)
            {
                _undo.Add((key, _memory[key], true));
                _memory.Remove(key);
            }
            _all.Clear();
        }

        private void ForgetAll()
        {
            if (_memory.Count == 0) return;
            // An array's length outlives every write and call (Length below).
            foreach (var pair in _memory) if (pair.Key.Size != Length) _all.Add(pair.Key);
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
                // AN ARRAY'S LENGTH NEVER CHANGES once it is made, so a read
                // of it is the same as one before it whatever happened between
                // -- a store, a call, a collection. `for (i = 0; i < a.Length;
                // i++) a[i]` read it twice a lap, for the loop's test and
                // again for the element's bounds check, and a read through a
                // register forgot every other load this pass remembered.
                // Only an array being made has its length written
                // (InitArrayLength), and that forgets it.
                if (i.Op == Opcode.ArrayLength && i.Dest is { } length && i.Operands.Count == 1
                    && i.Operands[0] is RegOperand { Reg: var array })
                {
                    Key lengthKey = new(array, 0, Length, false, length.Type);
                    if (_memory.TryGetValue(lengthKey, out Entry known)
                        && Stable(known.Load.Dest!, known.Block, known.Index + 1, block, index)
                        && Stable(array, known.Block, known.Index, block, index))
                    {
                        block.Instrs[index] = IrInfo.CopyOf(i, RegOperand.Of(known.Load.Dest!));
                        continue;
                    }
                    Set(lengthKey, new(i, block, index, true));
                    continue;
                }
                if (i.Op == Opcode.InitArrayLength && i.Operands.Count > 0 && i.Operands[0] is RegOperand { Reg: var made })
                {
                    foreach (var pair in _memory) if (pair.Key.Size == Length && pair.Key.Base == (object)made) _all.Add(pair.Key);
                    foreach (Key gone in _all) { _undo.Add((gone, _memory[gone], true)); _memory.Remove(gone); }
                    _all.Clear();
                }
                if (i.Op == Opcode.Load && !i.Device && i.Dest is { } result && result.Type.IsInt()
                    && Address(i, out Key key))
                {
                    if (_memory.TryGetValue(key, out Entry prior)
                        && Stable(prior.Load.Dest!, prior.Block, prior.Index + 1, block, index)
                        && (i.Operands[0] is not RegOperand address
                            || Stable(address.Reg, prior.Block, prior.Index, block, index)))
                    {
                        block.Instrs[index] = IrInfo.CopyOf(i, RegOperand.Of(prior.Load.Dest!));
                        continue;
                    }
                    if (_memory.Count >= 64) ForgetAll();
                    Set(key, new(i, block, index, Managed(i)));
                }
                else if (i.Op == Opcode.Store && !i.Device)
                    Written(i);
                else if (!IrInfo.IsPure(i) && i.Op is not (Opcode.Branch or Opcode.Jump))
                    ForgetAll();
            }
        }
    }

    /// <summary>Whether any instruction is a load Visit could remember.</summary>
    private static bool AnyCandidate(Function function)
    {
        foreach (Block block in function.Blocks)
        foreach (Instr i in block.Instrs)
        {
            if (i.Op == Opcode.Load && !i.Device && i.Dest is { } result && result.Type.IsInt() && Address(i, out _)) return true;
            if (i.Op == Opcode.ArrayLength) return true;
        }
        return false;
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
