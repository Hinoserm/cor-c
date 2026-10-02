#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A UNIT'S POINTER CONSTRAINTS FOR THE LINK (Lto.RegionHints): what
/// RegionPointsTo would solve over a whole module, stated function by
/// function for RegionSolver to solve over every unit. Taken from the IR the
/// link regenerates the unit from -- the module as the late passes find it
/// -- so an allocation site named here by its function and ordinal is the
/// same call when the link numbers it again (RegionPointsTo.MarkSites).
///
/// The rules are RegionPointsTo's: what is not followed -- a call nobody can
/// name, a system call's answer, a frame's, the stack's or a label's address,
/// a constant address -- is the unknown object, never nothing. Registers that
/// can hold no address, or whose address goes nowhere, are left out, and a
/// register that only copies another is that other.
/// </summary>
public static class RegionSummary
{
    public static RegionHints Of(Module m)
    {
        RegionHints hints = new();
        HashSet<string> data = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            data.Add(d.Name);
            foreach (DataReloc r in d.Relocs) hints.AddressTaken.Add(r.Symbol);
        }
        foreach (Function f in m.Functions)
        {
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    foreach (Operand o in i.Operands)
                        if (o is SymOperand { Name: var n }) hints.AddressTaken.Add(n);
            hints.Functions.Add(new Builder(f, f.Name == m.Main).Build());
        }
        // A data item's name is never a function's.
        hints.AddressTaken.RemoveWhere(data.Contains);
        return hints;
    }

    private sealed class Builder
    {
        private readonly Function _f;
        private readonly int _params;
        private int _next;
        private readonly Dictionary<VReg, int> _regs = new();
        private readonly Dictionary<FrameSlot, int> _slots = new();
        private readonly Dictionary<FrameSlot, int> _slotNodes = new();
        private int _unknownNode = -1;
        private readonly List<RegionSite> _sites = new();
        private readonly List<RegionConstraint> _constraints = new();
        private readonly List<RegionCall> _calls = new();
        private Dictionary<Instr, string[]>? _virtuals;
        private Dictionary<VReg, Instr?>? _defs;
        // Each call named in _calls by its place there, and each site by its ordinal.
        private readonly Dictionary<Instr, int> _callOf = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Instr, int> _siteOf = new(ReferenceEqualityComparer.Instance);
        // Nodes kept whatever they are used for: a loop judges by what they hold.
        private readonly HashSet<int> _forced = new();

        private readonly bool _main;

        public Builder(Function f, bool main)
        {
            _f = f;
            _main = main;
            _params = f.Params.Count;
            for (int k = 0; k < _params; k++) _regs.TryAdd(f.Params[k], k);
            _next = _params + 1;
        }

        private int Return => _params;

        private int Reg(VReg r)
        {
            if (_regs.TryGetValue(r, out int n)) return n;
            return _regs[r] = _next++;
        }

        private int Unknown()
        {
            if (_unknownNode >= 0) return _unknownNode;
            _unknownNode = _next++;
            _constraints.Add(new(RegionConstraintKind.Unknown, _unknownNode, 0, 0));
            return _unknownNode;
        }

        /// <summary>The node holding what operand `o` points to; -1 for none.</summary>
        private int Value(Operand o)
        {
            if (o is RegOperand { Reg: var r }) return Reg(r);
            if (o is SymOperand) return Unknown();
            if (o is not SlotOperand { Slot: var slot }) return -1;
            if (_slotNodes.TryGetValue(slot, out int known)) return known;
            if (!_slots.TryGetValue(slot, out int index)) _slots[slot] = index = _slots.Count;
            int node = _next++;
            _constraints.Add(new(RegionConstraintKind.Slot, node, index, 0));
            return _slotNodes[slot] = node;
        }

        /// <summary>An address operand's node: a constant address is somewhere unknown.</summary>
        private int Base(Operand o) => o is ImmOperand ? Unknown() : Value(o);

        private void Copy(int to, int from, long shift)
        {
            if (to >= 0 && from >= 0) _constraints.Add(new(RegionConstraintKind.Copy, to, from, shift));
        }

        private void Leak(int value)
        {
            if (value >= 0) _constraints.Add(new(RegionConstraintKind.Leak, value, 0, 0));
        }

        public RegionFunction Build()
        {
            _virtuals = Escape.VirtualCallees(new[] { _f });
            foreach (Block b in _f.Blocks)
                foreach (Instr i in b.Instrs)
                    Constrain(i);
            // AN ASYNC OR ITERATOR BODY'S FRAME OUTLIVES ITS RETURN: what its
            // registers and slots hold across a suspension is moved into a
            // frame on the heap after this IR (AsyncTransform), there for as
            // long as the task or the enumerator is. Nothing it holds is
            // dead by any return.
            if (_f.Async is not null)
            {
                foreach (int node in _regs.Values.ToArray()) Leak(node);
                foreach (int node in _slotNodes.Values.ToArray()) Leak(node);
                Leak(Return);
            }
            // ITS LOOPS, for the link to give a region of their own where
            // RegionPointsTo would (its "loops"): stated over this IR, by the
            // header's place, which the link names back to the regenerated
            // unit (RegionPointsTo.MarkLoops). Only where one may be given:
            // not in a type's initialiser, nor where a throw is caught in the
            // function's own frame -- a region a throw left open there would
            // be the one the catch went on making in -- nor where a label's
            // address is taken. A register live where a lap ends is judged by
            // what it holds, however it is used, so it is never left out.
            List<RegionPointsTo.LoopShape> loops = _f.Name.Contains("StaticInit", StringComparison.Ordinal)
                || _f.Blocks.Any(b => b.IsLandingPad || b.Instrs.Any(i => i.Op == Opcode.LabelAddr))
                ? new() : RegionPointsTo.Shapes(_f);
            foreach (RegionPointsTo.LoopShape loop in loops)
                foreach (VReg r in loop.Live.Concat(loop.Invariant)) _forced.Add(Reg(r));
            return Reduce(loops);
        }

        private int[] Sorted(IEnumerable<int> values) => values.Distinct().Order().ToArray();

        private int[] CallsAmong(IEnumerable<Instr> instrs) => Sorted(instrs.Where(_callOf.ContainsKey).Select(i => _callOf[i]));

        private int[] SitesAmong(IEnumerable<Instr> instrs) => Sorted(instrs.Where(_siteOf.ContainsKey).Select(i => _siteOf[i]));

        private void Constrain(Instr i)
        {
            int dest = i.Dest is null ? -1 : Reg(i.Dest);
            switch (i.Op)
            {
                case Opcode.Copy:
                case Opcode.Trunc64:
                case Opcode.ZExt32:
                case Opcode.SExt32:
                case Opcode.Phi:
                case Opcode.And:
                case Opcode.Or:
                // A tag shifted in can be shifted out again: what is shifted
                // left may still be the pointer.
                case Opcode.Shl:
                    if (dest < 0) return;
                    foreach (Operand o in i.Operands) Copy(dest, Value(o), 0);
                    return;

                case Opcode.Add:
                case Opcode.Sub:
                {
                    if (dest < 0) return;
                    // A pointer walked in a loop is anywhere in its object
                    // (RegionPointsTo.Walked).
                    bool constant = i.Operands.Count == 2 && i.Operands[1] is ImmOperand && !Walked(i);
                    long by = constant ? ((ImmOperand)i.Operands[1]).Value * (i.Op == Opcode.Sub ? -1 : 1) : RegionConstraint.Any;
                    foreach (Operand o in i.Operands) Copy(dest, Value(o), by);
                    return;
                }

                case Opcode.Load:
                {
                    int at = Base(i.Operands[0]);
                    if (dest >= 0 && at >= 0) _constraints.Add(new(RegionConstraintKind.Load, dest, at, i.Offset));
                    return;
                }

                case Opcode.Store:
                case Opcode.InitArrayLength:
                    if (i.Operands.Count >= 2 && Base(i.Operands[0]) is int into and >= 0 && Value(i.Operands[1]) is int value and >= 0)
                        _constraints.Add(new(RegionConstraintKind.Store, into, value, i.Offset));
                    return;

                case Opcode.AtomicSwap:
                case Opcode.AtomicCas:
                case Opcode.AtomicAdd:
                case Opcode.AtomicAnd:
                {
                    // A read of the word and a write to it.
                    int at = Base(i.Operands[0]);
                    if (at < 0) return;
                    if (dest >= 0) _constraints.Add(new(RegionConstraintKind.Load, dest, at, i.Offset));
                    for (int k = 1; k < i.Operands.Count; k++)
                        if (Value(i.Operands[k]) is int v and >= 0) _constraints.Add(new(RegionConstraintKind.Store, at, v, i.Offset));
                    return;
                }

                case Opcode.MemCopy:
                {
                    int to = Base(i.Operands[0]), from = Base(i.Operands[1]);
                    long count = i.Operands.Count > 2 && i.Operands[2] is ImmOperand n ? n.Value : RegionConstraint.Any;
                    if (to >= 0 && from >= 0) _constraints.Add(new(RegionConstraintKind.MemCopy, to, from, count));
                    return;
                }

                case Opcode.Ret:
                    if (i.Operands.Count > 0) Copy(Return, Value(i.Operands[0]), 0);
                    return;

                case Opcode.Unwind:
                    foreach (Operand o in i.Operands) Leak(Value(o));
                    return;

                case Opcode.Call:
                    Call(i, dest);
                    return;

                case Opcode.CallIndirect:
                {
                    // A virtual call: every override the link finds for its
                    // declaring type and slot. Any other: nobody can say.
                    string? callee = _virtuals!.TryGetValue(i, out string[]? named) && named.Length == 1 ? named[0] : null;
                    _callOf[i] = _calls.Count;
                    _calls.Add(new(callee, dest, Arguments(i, 1)));
                    return;
                }

                // What a system call is handed, the kernel may keep and hand
                // back later (an event's data, a thread's argument).
                case Opcode.Syscall:
                    foreach (Operand o in i.Operands) Leak(Value(o));
                    Copy(dest, Unknown(), 0);
                    return;

                // What this does not follow may be anything.
                case Opcode.FramePointer:
                case Opcode.StackPointer:
                case Opcode.LabelAddr:
                    Copy(dest, Unknown(), 0);
                    return;

                // Numbers made of anything (RegionPointsTo's rule).
                default:
                    return;
            }
        }

        private int[] Arguments(Instr i, int first)
        {
            int[] arguments = new int[Math.Max(0, i.Operands.Count - first)];
            for (int k = first; k < i.Operands.Count; k++) arguments[k - first] = Value(i.Operands[k]);
            return arguments;
        }

        private void Call(Instr i, int dest)
        {
            string? callee = i.Callee;
            if (callee is null) { _callOf[i] = _calls.Count; _calls.Add(new(null, dest, Arguments(i, 0))); return; }
            if (RegionPointsTo.IsSiteCall(i))
            {
                int site = _sites.Count;
                (string? table, long at) = Stamp(i) ?? (null, 0);
                // How the collector reads its words (RegionPointsTo.HoldsNoReference).
                RegionWords words = callee == Escape.LeafAllocator ? RegionWords.Leaf : callee == Escape.ObjectAllocator ? RegionWords.Described : RegionWords.Any;
                _siteOf[i] = site;
                _sites.Add(new(RegionPointsTo.IsRewritable(callee), i.Line, table, at, words));
                if (dest >= 0) _constraints.Add(new(RegionConstraintKind.Site, dest, site, 0));
                return;
            }
            // The collector's notes and the runtime's frees keep no pointer.
            if (RegionPointsTo.Harmless(callee)) return;
            // An instruction the backend makes of a call -- the thread's block,
            // the exception being caught -- calls no code: what it answers is
            // unknown, what it is handed goes where nobody follows.
            if (Escape.IsIntrinsic(callee))
            {
                foreach (int argument in Arguments(i, 0)) Leak(argument);
                Copy(dest, Unknown(), 0);
                return;
            }
            _callOf[i] = _calls.Count;
            _calls.Add(new(callee, dest, Arguments(i, 0)));
        }

        // Each register written once, to its instruction; null where written more.
        private Dictionary<VReg, Instr?> Defs()
        {
            if (_defs is not null) return _defs;
            _defs = new();
            foreach (Block b in _f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d) _defs[d] = _defs.ContainsKey(d) ? null : i;
            return _defs;
        }

        /// <summary>
        /// The descriptor a site's object is stamped with and where in it its
        /// method table begins (RegionPointsTo.Stamp): one store of a symbol
        /// into its first word, from the one writer of the site's register.
        /// Two different stamps say nothing.
        /// </summary>
        private (string Table, long At)? Stamp(Instr site)
        {
            VReg? made = site.Dest;
            if (made is null || !Defs().TryGetValue(made, out Instr? only) || only != site) return null;
            (string, long)? found = null;
            foreach (Block b in _f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Offset == 0 && i.Operands.Count >= 2
                        && i.Operands[1] is SymOperand { Name: var t, Offset: var at }
                        && i.Operands[0] is RegOperand { Reg: var to } && Derives(to, made, 0))
                    {
                        if (found is { } f0 && (f0.Item1 != t || f0.Item2 != at)) return null;
                        found = (t, at);
                    }
            return found;
        }

        private bool Derives(VReg r, VReg made, int depth)
        {
            if (r == made) return true;
            if (depth > 3) return false;
            return Defs().TryGetValue(r, out Instr? w) && w is { Op: Opcode.Trunc64 or Opcode.Copy } && w.Operands[0] is RegOperand { Reg: var from }
                && Derives(from, made, depth + 1);
        }

        // Whether an address computation's source is a join its own result
        // flows back into: `p = phi(start, next); next = p + 4`.
        private bool Walked(Instr add)
        {
            if (add.Dest is null || add.Operands[0] is not RegOperand { Reg: var from }) return false;
            Dictionary<VReg, Instr?> defs = Defs();
            HashSet<VReg> seen = new();
            Stack<VReg> next = new();
            next.Push(from);
            while (next.TryPop(out VReg? r))
            {
                if (!seen.Add(r) || seen.Count > 16) continue;
                if (r == add.Dest) return true;
                if (defs.TryGetValue(r, out Instr? w) && w is { Op: Opcode.Phi or Opcode.Copy })
                    foreach (Operand o in w.Operands)
                        if (o is RegOperand { Reg: var q }) next.Push(q);
            }
            return false;
        }

        /// <summary>
        /// ONLY THE NODES THAT MATTER: one that can hold an address (from an
        /// allocation, a slot, the unknown object, a load, a call, a
        /// parameter) and hands it somewhere it is used (a store, a load's
        /// base, a call, the return, a throw). A node whose one source is a
        /// plain copy of another is that other. What is left is numbered
        /// again: parameters, the return, then the rest.
        /// </summary>
        private RegionFunction Reduce(List<RegionPointsTo.LoopShape> loops)
        {
            int count = _next;
            List<int>?[] copiesFrom = new List<int>?[count];   // per node: the nodes it copies from
            List<int>?[] copiesTo = new List<int>?[count];
            bool[] source = new bool[count], sink = new bool[count];
            int[] incoming = new int[count];
            for (int k = 0; k < _params; k++) { source[k] = true; sink[k] = true; }
            sink[Return] = true;
            foreach (RegionConstraint c in _constraints)
                switch (c.Kind)
                {
                    case RegionConstraintKind.Site:
                    case RegionConstraintKind.Slot:
                    case RegionConstraintKind.Unknown:
                        source[c.A] = true; incoming[c.A] += 2;
                        break;
                    case RegionConstraintKind.Copy:
                        (copiesFrom[c.A] ??= new()).Add(c.B);
                        (copiesTo[c.B] ??= new()).Add(c.A);
                        incoming[c.A] += c.C == 0 ? 1 : 2;
                        break;
                    case RegionConstraintKind.Load:
                        source[c.A] = true; incoming[c.A] += 2; sink[c.B] = true;
                        break;
                    case RegionConstraintKind.Store:
                    case RegionConstraintKind.MemCopy:
                        sink[c.A] = true; sink[c.B] = true;
                        break;
                    case RegionConstraintKind.Leak:
                        sink[c.A] = true;
                        break;
                }
            foreach (RegionCall call in _calls)
            {
                if (call.Dest >= 0) { source[call.Dest] = true; incoming[call.Dest] += 2; }
                foreach (int a in call.Arguments) if (a >= 0) sink[a] = true;
            }
            for (int k = 0; k < _params; k++) incoming[k] += 2;
            foreach (int n in _forced) sink[n] = true;
            bool[] holds = Spread(source, copiesTo), used = Spread(sink, copiesFrom);
            bool Kept(int n) => n >= 0 && (n <= _params || holds[n] && used[n]);

            // A node with one plain copy into it and nothing else is that copy's source.
            int[] same = new int[count];
            for (int n = 0; n < count; n++) same[n] = n;
            foreach (RegionConstraint c in _constraints)
                if (c.Kind == RegionConstraintKind.Copy && c.C == 0 && c.A > _params && incoming[c.A] == 1 && Kept(c.A) && Kept(c.B))
                    same[c.A] = c.B;
            int Root(int n)
            {
                for (int hops = 0; hops < count && same[n] != n; hops++) n = same[n];
                return n;
            }

            int[] renumber = new int[count];
            for (int n = 0; n < count; n++) renumber[n] = -1;
            int next = 0;
            for (int n = 0; n <= _params; n++) renumber[n] = next++;
            int Node(int n)
            {
                if (!Kept(n)) return -1;
                n = Root(n);
                if (renumber[n] < 0) renumber[n] = next++;
                return renumber[n];
            }

            HashSet<RegionConstraint> seen = new();
            List<RegionConstraint> kept = new();
            foreach (RegionConstraint c in _constraints)
            {
                int a = Node(c.A);
                int b = c.Kind is RegionConstraintKind.Copy or RegionConstraintKind.Load or RegionConstraintKind.Store or RegionConstraintKind.MemCopy ? Node(c.B) : c.B;
                if (a < 0 || b < 0) continue;
                if (c.Kind == RegionConstraintKind.Copy && c.C == 0 && a == b) continue;
                RegionConstraint r = new(c.Kind, a, b, c.C);
                if (seen.Add(r)) kept.Add(r);
            }
            List<RegionCall> calls = new();
            foreach (RegionCall call in _calls)
            {
                int[] arguments = new int[call.Arguments.Length];
                for (int k = 0; k < arguments.Length; k++) arguments[k] = Node(call.Arguments[k]);
                calls.Add(new(call.Callee, Node(call.Dest), arguments));
            }
            // Numbered before the count is taken: a node only a loop names is one.
            int[] Nodes(IEnumerable<VReg> registers) => Sorted(registers.Select(r => Node(Reg(r))).Where(n => n >= 0));
            List<RegionLoopShape> shapes = new();
            foreach (RegionPointsTo.LoopShape loop in loops)
                shapes.Add(new RegionLoopShape(loop.Header, SitesAmong(loop.Instrs), CallsAmong(loop.Calls), SitesAmong(loop.Always), CallsAmong(loop.Always),
                    Nodes(loop.Live), Nodes(loop.Invariant), Sorted(loop.KeptSlots.Where(_slots.ContainsKey).Select(slot => _slots[slot]))));
            List<Instr> must = RegionPointsTo.MustRunCalls(_f);
            RegionFunction result = new(_f.Name, _f.Exported, _f.Async is null && !_f.Name.Contains("StaticInit", StringComparison.Ordinal) && !_main,
                _params > 0 && _f.Params[0].Name == "this", _params, next, _slots.Count, _sites.ToArray()) { Main = _main };
            result.Constraints.AddRange(kept);
            result.Calls.AddRange(calls);
            result.MustCalls = CallsAmong(must);
            result.MustSites = SitesAmong(must);
            result.Loops.AddRange(shapes);
            return result;
        }

        // Every node reached from the marked ones along the edges given.
        private static bool[] Spread(bool[] marked, List<int>?[] edges)
        {
            bool[] reached = (bool[])marked.Clone();
            Stack<int> next = new();
            for (int n = 0; n < marked.Length; n++) if (marked[n]) next.Push(n);
            while (next.TryPop(out int n))
                if (edges[n] is { } to)
                    foreach (int m in to)
                        if (!reached[m]) { reached[m] = true; next.Push(m); }
            return reached;
        }
    }
}
