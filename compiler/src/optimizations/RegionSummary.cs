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
        RegionHints hints = new() { WordSize = Target.Current.WordSize };
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
        // Each symbol whose address the code takes: its node, and its place
        // among the function's Symbols. A string literal, a descriptor, a
        // function is a constant no region need follow; a static's storage is
        // the unknown object. Which is which the link says (RegionConstants),
        // holding every unit's data: the unit does not know another's.
        private readonly Dictionary<string, int> _symbolNodes = new(StringComparer.Ordinal);
        private readonly List<string> _symbols = new();
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
            if (o is SymOperand { Name: var symbol }) return Symbol(symbol);
            if (o is not SlotOperand { Slot: var slot }) return -1;
            if (_slotNodes.TryGetValue(slot, out int known)) return known;
            if (!_slots.TryGetValue(slot, out int index)) _slots[slot] = index = _slots.Count;
            int node = _next++;
            _constraints.Add(new(RegionConstraintKind.Slot, node, index, 0));
            return _slotNodes[slot] = node;
        }

        private int Symbol(string name)
        {
            if (_symbolNodes.TryGetValue(name, out int known)) return known;
            int node = _next++;
            _constraints.Add(new(RegionConstraintKind.Symbol, node, _symbols.Count, 0));
            _symbols.Add(name);
            return _symbolNodes[name] = node;
        }

        /// <summary>An address operand's node: a constant address is somewhere unknown.</summary>
        private int Base(Operand o) => o is ImmOperand ? Unknown() : Value(o);

        private void Copy(int to, int from, long shift)
        {
            if (to >= 0 && from >= 0) _constraints.Add(new(RegionConstraintKind.Copy, to, from, shift));
        }

        // The fields its typed loads and stores name (Instr.Family), each once.
        private readonly List<string> _families = new();
        private readonly Dictionary<string, int> _familyIndex = new(StringComparer.Ordinal);

        /// <summary>The field a load or store names, as an index into its Families; -1 for none.</summary>
        private int Family(Instr i)
        {
            if (i.Family is not string name) return -1;
            if (!_familyIndex.TryGetValue(name, out int at)) { at = _families.Count; _families.Add(name); _familyIndex[name] = at; }
            return at;
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
            // AN ASYNC OR ITERATOR BODY KEEPS IN ITS STATE MACHINE what it holds
            // across a suspension: AsyncTransform, after this IR, stores every
            // register live after a resumption (and every one a landing pad
            // reads) into a field of the machine at each suspension, and
            // every frame slot becomes a field of it. So each is a store into
            // the machine -- the body's first parameter, `this` of a MoveNext
            // -- at a field nobody here names, kept for as long as the machine
            // is: an enumerator a boundary walks and drops takes what its
            // laps made with it. A resumption reloads the registers' own
            // values, so nothing flows that does not flow already, as
            // RegionPointsTo has it. Where the machine goes nobody follows --
            // an awaiter's OnCompleted handed it, a scheduler keeping it --
            // that is the call's, as any argument's. (Every register leaked,
            // every object any iterator touched was everyone's.)
            //
            // A slot is WHAT IT HOLDS copied into the machine, not its address
            // stored there: the transform makes the slot a field of the
            // machine, and nothing holds a pointer to it. As an address
            // stored, the slot was an object a place reaches, so every load of
            // it took all that was written into places (RegionEscape.Aliased)
            // -- the machine's own saved registers among them -- and an
            // iterator that yields inside a try, unlinking its handler record
            // into the thread's block at the yield, put its arguments and
            // every object its laps made into the unknown object: 1298's
            // items, global in the caller that walked them.
            if (_f.Async is { Lowered: false } frame)
            {
                int machine = Reg(frame.StateMachine);
                foreach (VReg r in RegionPointsTo.Saved(_f, frame))
                    _constraints.Add(new(RegionConstraintKind.Store, machine, Reg(r), RegionConstraint.Any));
                foreach (FrameSlot slot in _f.Slots)
                    if (Value(new SlotOperand(slot)) is int held and >= 0)
                        _constraints.Add(new(RegionConstraintKind.MemCopy, machine, held, RegionConstraint.Any));
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
                    // An index scaled into an address never brings the
                    // unknown object (RegionPointsTo.IndexShift).
                    // The address it is added to moves to a residue of the
                    // index's scale (RegionPointsTo.Strided).
                    int scale = i.Op == Opcode.Add ? i.Operands.Max(o => IndexScale(i, o)) : 0;
                    foreach (Operand o in i.Operands)
                        Copy(dest, Value(o), IndexScale(i, o) is int k and > 0 ? RegionConstraint.IndexScaled(k)
                            : scale > 0 ? RegionConstraint.MovedByScaled(scale) : by);
                    return;
                }

                case Opcode.Load:
                {
                    // A number read (Instr.Number) is no address.
                    if (i.Number) return;
                    int at = Base(i.Operands[0]);
                    if (dest >= 0 && at >= 0) _constraints.Add(new(RegionConstraintKind.Load, dest, at, i.Offset, Family(i)));
                    return;
                }

                case Opcode.Store:
                case Opcode.InitArrayLength:
                    if (i.Operands.Count >= 2 && Base(i.Operands[0]) is int into and >= 0 && Value(i.Operands[1]) is int value and >= 0)
                        _constraints.Add(new(RegionConstraintKind.Store, into, value, i.Offset, i.Op == Opcode.Store ? Family(i) : -1));
                    return;

                case Opcode.AtomicSwap:
                case Opcode.AtomicCas:
                case Opcode.AtomicAdd:
                case Opcode.AtomicAnd:
                {
                    // A read of the word and a write to it.
                    int at = Base(i.Operands[0]);
                    if (at < 0) return;
                    // What it answers is the word that was there: a number
                    // where the word is one (Instr.Number), as a load's.
                    if (dest >= 0 && !i.Number) _constraints.Add(new(RegionConstraintKind.Load, dest, at, i.Offset));
                    for (int k = 1; k < i.Operands.Count; k++)
                        if (Value(i.Operands[k]) is int v and >= 0) _constraints.Add(new(RegionConstraintKind.Store, at, v, i.Offset));
                    return;
                }

                case Opcode.MemCopy:
                {
                    // Characters or bytes (Instr.Number) move no address.
                    if (i.Number) return;
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

        // Whether operand `o` of addition `i` is an index scaled into an
        // address (RegionPointsTo.ScaledIndex).
        private bool ScaledIndex(Instr i, Operand o) => IndexScale(i, o) > 0;

        // The k of an index scaled by 2^k, or 0 (past MostScale, one more: no
        // element's word told).
        private int IndexScale(Instr i, Operand o) =>
            i.Operands.Count == 2 && i.Operands.All(x => x is RegOperand) && o is RegOperand { Reg: var r }
            && Defs().TryGetValue(r, out Instr? w) && w is { Op: Opcode.Shl, Operands: [RegOperand, ImmOperand { Value: > 0 and var k }] }
                ? (int)Math.Min(k, RegionConstraint.MostScale + 1) : 0;

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
            // A parameter of a number type is handed no address (VReg.Number).
            for (int k = 0; k < _params; k++) { source[k] = !_f.Params[k].Number; sink[k] = true; }
            sink[Return] = true;
            foreach (RegionConstraint c in _constraints)
                switch (c.Kind)
                {
                    case RegionConstraintKind.Site:
                    case RegionConstraintKind.Slot:
                    case RegionConstraintKind.Unknown:
                    case RegionConstraintKind.Symbol:
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
                RegionConstraint r = new(c.Kind, a, b, c.C, c.Family);
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
                _params > 0 && _f.Params[0].Name == "this", _params, next, _slots.Count, _sites.ToArray())
            { Main = _main, NumberParams = Sorted(Enumerable.Range(0, _params).Where(k => _f.Params[k].Number)), Symbols = KeptSymbols(kept), Families = _families.ToArray() };
            result.Constraints.AddRange(kept);
            result.Calls.AddRange(calls);
            result.MustCalls = CallsAmong(must);
            result.MustSites = SitesAmong(must);
            result.Loops.AddRange(shapes);
            Repeat(result);
            return result;
        }

        // ---- how often each site and call runs ------------------------------
        //
        // WHAT ONE CALL OF THE FUNCTION CAN MAKE, for the link to size the
        // regions it opens (RegionSolver.Sizes): each site's block in the
        // arena, where the allocator is asked for a constant number of bytes
        // -- an object of a described type, an array of a constant length --
        // and how often each site and call runs, by the natural loops they
        // are in and the most laps each makes. A loop's laps are bounded where
        // a test run on every lap compares a counter with a constant: the
        // counter set only to constants before the loop and stepped by a
        // constant once a lap, in a block every lap runs. Nothing is bounded
        // in a function whose cycles are not all natural loops, nor in what a
        // handler or an address taken of a block reaches -- an unwind's way
        // back is an edge the graph does not hold -- nor in an async or
        // iterator body. What runs only on the way to a throw (Throwing) is
        // left out of every size: a region past its size grows as one nobody
        // sized does, and the throw ends it.

        // Past these, a counter or a step is no bound: no wrap on the way.
        private const long MostCounted = 1L << 30, MostStep = 1L << 20;
        // The most laps a loop is said to make; more is not known.
        private const long MostLaps = 1L << 24;

        private void Repeat(RegionFunction result)
        {
            int sites = _sites.Count;
            long[] bytes = new long[sites];
            int[] siteLoops = new int[sites], callLoops = new int[_calls.Count];
            Array.Fill(siteLoops, RegionFunction.Unbounded);
            Array.Fill(callLoops, RegionFunction.Unbounded);
            foreach ((Instr i, int site) in _siteOf)
                if (i.Operands.Count > 0 && i.Operands[0] is ImmOperand { Value: >= 0 and < MostCounted and var n })
                    bytes[site] = RegionLayout.Block(n, Target.Current.WordSize);
            result.SiteBytes = bytes; result.SiteLoops = siteLoops; result.CallLoops = callLoops;
            if (_f.Async is not null || _f.Blocks.Count > RegionPointsTo.LoopBlocks) return;
            Cfg cfg = new(_f);
            bool[] live = cfg.Live;
            // Every cycle a natural loop: none left once the back edges -- to
            // a block that dominates the one they leave -- are taken away.
            int[] state = new int[_f.Blocks.Count];
            bool Cyclic(Block b)
            {
                state[b.Order] = 1;
                foreach (Block s in cfg.Succs(b))
                {
                    if (cfg.Dominates(s, b)) continue;
                    if (state[s.Order] == 1 || state[s.Order] == 0 && Cyclic(s)) return true;
                }
                state[b.Order] = 2;
                return false;
            }
            foreach (Block b in _f.Blocks)
                if (live[b.Order] && state[b.Order] == 0 && Cyclic(b)) return;

            var natural = RegionPointsTo.NaturalLoops(_f, cfg);
            // Outer loops first: a loop's body holds every loop's in it, or none of it.
            natural.Sort((a, b) => b.Body.Count.CompareTo(a.Body.Count));
            int[] parent = new int[natural.Count];
            for (int k = 0; k < natural.Count; k++)
            {
                parent[k] = -1;
                for (int j = 0; j < k; j++)
                {
                    if (!natural[j].Body.Overlaps(natural[k].Body)) continue;
                    if (!natural[j].Body.IsSupersetOf(natural[k].Body)) return;
                    parent[k] = j;
                }
            }
            int[] inner = new int[_f.Blocks.Count];
            Array.Fill(inner, -1);
            for (int k = 0; k < natural.Count; k++)
                foreach (Block b in natural[k].Body) inner[b.Order] = k;
            // What a handler, or a block whose address is taken, reaches.
            bool[] unbounded = new bool[_f.Blocks.Count];
            Stack<Block> next = new();
            foreach (Block root in cfg.Roots)
                if (root != _f.Entry && !unbounded[root.Order]) { unbounded[root.Order] = true; next.Push(root); }
            while (next.TryPop(out Block? b))
                foreach (Block s in cfg.Succs(b))
                    if (!unbounded[s.Order]) { unbounded[s.Order] = true; next.Push(s); }

            Dictionary<VReg, List<(Instr Def, Block At)>> defs = new();
            foreach (Block b in _f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d) (defs.TryGetValue(d, out var list) ? list : defs[d] = new()).Add((i, b));
            RegionRepeat[] repeats = new RegionRepeat[natural.Count];
            for (int k = 0; k < natural.Count; k++)
                repeats[k] = new RegionRepeat(natural[k].Header.Order, parent[k], Laps(cfg, natural[k].Body, natural[k].Latches, defs));
            result.Repeats = repeats;
            // The blocks a return is reached from; the rest only throw.
            bool[] returns = new bool[_f.Blocks.Count];
            foreach (Block b in _f.Blocks)
                if (b.Terminator is { Op: Opcode.Ret } && !returns[b.Order]) { returns[b.Order] = true; next.Push(b); }
            while (next.TryPop(out Block? b))
                foreach (Block p in cfg.Preds(b))
                    if (!returns[p.Order]) { returns[p.Order] = true; next.Push(p); }
            foreach (Block b in _f.Blocks)
            {
                int at = unbounded[b.Order] ? RegionFunction.Unbounded : !returns[b.Order] ? RegionFunction.Throwing : inner[b.Order];
                foreach (Instr i in b.Instrs)
                {
                    if (_siteOf.TryGetValue(i, out int site)) siteLoops[site] = at;
                    if (_callOf.TryGetValue(i, out int call)) callLoops[call] = at;
                }
            }
        }

        /// <summary>The most laps a loop makes each time it is entered, by the fewest any test run every lap allows; 0 when none bounds them.</summary>
        private long Laps(Cfg cfg, HashSet<Block> body, List<Block> latches, Dictionary<VReg, List<(Instr Def, Block At)>> defs)
        {
            long fewest = 0;
            foreach (Block e in body)
            {
                if (e.Terminator is not { Op: Opcode.Branch } branch || branch.Targets.Count != 2
                    || branch.Operands[0] is not RegOperand { Reg: var c }) continue;
                bool stays = body.Contains(branch.Targets[0]);
                if (stays == body.Contains(branch.Targets[1])) continue;
                if (!latches.All(l => cfg.Dominates(e, l))) continue;
                // The test made afresh every lap, before the branch reads it.
                if (!defs.TryGetValue(c, out var tests) || tests.Count != 1 || tests[0].Def.Operands.Count != 2
                    || !body.Contains(tests[0].At) || !cfg.Dominates(tests[0].At, e)) continue;
                if (Laps(cfg, body, latches, defs, tests[0].Def, stays, tests[0].At) is long laps and > 0 && (fewest == 0 || laps < fewest)) fewest = laps;
            }
            return fewest;
        }

        // A test `v op n` that keeps the loop going while it is `stays`: v a
        // counter, or a counter plus a constant written in the lap before the
        // test is (`at`, the test's block).
        private long Laps(Cfg cfg, HashSet<Block> body, List<Block> latches, Dictionary<VReg, List<(Instr Def, Block At)>> defs, Instr test, bool stays, Block at)
        {
            Opcode op = test.Op;
            Operand a = test.Operands[0], b = test.Operands[1];
            if (a is ImmOperand && b is RegOperand) { (a, b) = (b, a); op = Mirror(op); }
            if (!stays) op = Negate(op);
            if (op == Opcode.Copy || a is not RegOperand { Reg: var v } || b is not ImmOperand { Value: var n } || Math.Abs(n) > MostCounted) return 0;
            bool unsigned = op is Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU;
            if (unsigned && n < 0) return 0;
            long offset = 0;
            (long Start, long Step)? counter = Counter(cfg, body, latches, defs, v);
            if (counter is null && defs.TryGetValue(v, out var vd) && vd.Count == 1 && body.Contains(vd[0].At) && cfg.Dominates(vd[0].At, at)
                && (vd[0].At != at || at.Instrs.IndexOf(vd[0].Def) < at.Instrs.IndexOf(test))
                && Stepped(vd[0].Def) is (VReg from, long by))
            {
                counter = Counter(cfg, body, latches, defs, from);
                offset = by;
            }
            if (counter is not (long start, long step)) return 0;
            start += offset;
            if (unsigned && start < 0) return 0;
            long passing = op switch
            {
                Opcode.LtS or Opcode.LtU when step > 0 => Ceiling(n - start, step),
                Opcode.LeS or Opcode.LeU when step > 0 => Floor(n - start, step) + 1,
                Opcode.GtS when step < 0 => Ceiling(start - n, -step),
                Opcode.GeS when step < 0 => Floor(start - n, -step) + 1,
                Opcode.Ne when step == 1 && n >= start => n - start,
                Opcode.Ne when step == -1 && n <= start => start - n,
                _ => -1,
            };
            if (passing < 0) return 0;
            long laps = Math.Max(0, passing) + 1;   // and the lap whose test fails
            return laps > MostLaps ? 0 : laps;
        }

        private static long Ceiling(long a, long b) => a <= 0 ? 0 : (a + b - 1) / b;
        private static long Floor(long a, long b) => a < 0 ? -1 : a / b;

        // A counter: set before the loop only to constants (the least of
        // them going up, the most going down, as Start), and stepped by a
        // constant once a lap, in a block every lap runs.
        private (long Start, long Step)? Counter(Cfg cfg, HashSet<Block> body, List<Block> latches, Dictionary<VReg, List<(Instr Def, Block At)>> defs, VReg i)
        {
            if (_f.Params.Contains(i) || !defs.TryGetValue(i, out var all)) return null;
            List<long> starts = new();
            long? step = null;
            foreach ((Instr def, Block at) in all)
            {
                if (!body.Contains(at))
                {
                    if (def.Op != Opcode.Copy || def.Operands[0] is not ImmOperand { Value: var s } || Math.Abs(s) > MostCounted) return null;
                    starts.Add(s);
                    continue;
                }
                if (step is not null || !latches.All(l => cfg.Dominates(at, l))) return null;
                (VReg From, long By)? stepped = Stepped(def);
                if (stepped is null && def.Op == Opcode.Copy && def.Operands[0] is RegOperand { Reg: var t }
                    && defs.TryGetValue(t, out var td) && td.Count == 1 && body.Contains(td[0].At) && cfg.Dominates(td[0].At, at)
                    && (td[0].At != at || at.Instrs.IndexOf(td[0].Def) < at.Instrs.IndexOf(def)))
                    stepped = Stepped(td[0].Def);
                if (stepped is not (VReg from, long by) || from != i || by == 0 || Math.Abs(by) > MostStep) return null;
                step = by;
            }
            if (starts.Count == 0 || step is not long d) return null;
            return (d > 0 ? starts.Min() : starts.Max(), d);
        }

        // `r + k` or `r - k` for a constant k: r and the signed step.
        private static (VReg From, long By)? Stepped(Instr i)
        {
            if (i.Operands.Count != 2) return null;
            if (i.Op == Opcode.Add && i.Operands[0] is RegOperand { Reg: var r } && i.Operands[1] is ImmOperand { Value: var k }) return (r, k);
            if (i.Op == Opcode.Add && i.Operands[1] is RegOperand { Reg: var r2 } && i.Operands[0] is ImmOperand { Value: var k2 }) return (r2, k2);
            if (i.Op == Opcode.Sub && i.Operands[0] is RegOperand { Reg: var r3 } && i.Operands[1] is ImmOperand { Value: var k3 }) return (r3, -k3);
            return null;
        }

        // `a op b` as `b op' a`; Copy for what is no comparison.
        private static Opcode Mirror(Opcode op) => op switch
        {
            Opcode.LtS => Opcode.GtS, Opcode.LeS => Opcode.GeS, Opcode.GtS => Opcode.LtS, Opcode.GeS => Opcode.LeS,
            Opcode.LtU => Opcode.GtU, Opcode.LeU => Opcode.GeU, Opcode.GtU => Opcode.LtU, Opcode.GeU => Opcode.LeU,
            Opcode.Eq or Opcode.Ne => op,
            _ => Opcode.Copy,
        };

        // `!(a op b)` as `a op' b`.
        private static Opcode Negate(Opcode op) => op switch
        {
            Opcode.LtS => Opcode.GeS, Opcode.LeS => Opcode.GtS, Opcode.GtS => Opcode.LeS, Opcode.GeS => Opcode.LtS,
            Opcode.LtU => Opcode.GeU, Opcode.LeU => Opcode.GtU, Opcode.GtU => Opcode.LeU, Opcode.GeU => Opcode.LtU,
            Opcode.Eq => Opcode.Ne, Opcode.Ne => Opcode.Eq,
            _ => Opcode.Copy,
        };

        // The symbols the kept constraints name, renumbered in their order.
        private string[] KeptSymbols(List<RegionConstraint> kept)
        {
            List<string> names = new();
            Dictionary<int, int> renumber = new();
            for (int k = 0; k < kept.Count; k++)
            {
                RegionConstraint c = kept[k];
                if (c.Kind != RegionConstraintKind.Symbol) continue;
                if (!renumber.TryGetValue(c.B, out int at)) { at = names.Count; names.Add(_symbols[c.B]); renumber[c.B] = at; }
                kept[k] = new RegionConstraint(c.Kind, c.A, at, c.C, c.Family);
            }
            return names.ToArray();
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
