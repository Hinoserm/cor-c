#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

// ---- owned fields across units ------------------------------------------------------
//
// A FIELD OWNS WHAT IT HOLDS only if the whole program says so (OwnedFields):
// every store into it, anywhere, and every read of it, anywhere. A unit
// compiled on its own sees some of the stores and some of the reads. So it
// judges those as OwnedFields would, and where the judgement needs another
// unit it writes the need down instead (Lto.OwnedFieldHints): a stored
// object made by another unit's function, which must hand over what it
// returns; a stored parameter, which every caller must hand over; a read
// handed to another unit's function, which must keep nothing; the calls a
// read is live across, none of which may store into the field, over the
// whole call graph; the values of calls it reads, which are reads of a field
// if the callee hands back what one holds. The link decides every field
// from every unit's hints (Lto.OwnedFieldSolver) and regenerates each unit
// with the answer, which the unit then applies exactly as OwnedFields does:
// a store that may replace a value frees it, and each type it defines gets
// its owned-field map. The unit decides nothing by itself: one unit's
// "owned" is every unit's.

public sealed partial class Escape
{
    /// <summary>At most this many objects a unit judges handed over per function, a fixed bound.</summary>
    private const int SinkJudgements = 32;

    /// <summary>The unit's side of OwnedFields: hints at a unit compile, the link's answer when regenerated.</summary>
    private void OwnedFieldsInUnit(Module m, Dictionary<string, Function> byName, Dictionary<string, bool[]> summaries)
    {
        if (!m.PreserveExports || !byName.ContainsKey(OwnedReplacedFreer) && !m.RuntimeHelpers.Contains(OwnedReplacedFreer)) return;
        if (m.OwnedFields is OwnedFieldFacts decided)
        {
            ApplyOwnedFields(m, decided, summaries);
            return;
        }
        if (_hinting && m.LifetimeHints is not null)
        {
            m.LifetimeHints.Owned = OwnedFieldHintsOf(m, summaries);
            // And of elements owned through a field, the link's to decide too.
            if (_elementMode == ElementMode.Hints) ElementHints(m, m.LifetimeHints.Owned);
        }
    }

    /// <summary>
    /// WHAT A CALL REPLACED IN AN OBJECT THIS FUNCTION OWNS, FREED AFTER IT.
    /// A store frees what it replaces only where its object was made (the
    /// thread rule); a List's Grow stores into its `this`, so a List whose Grow
    /// was not inlined left every array it grew out of to the collector. But
    /// an object private to this function is changed only by the calls it is
    /// handed to: around each, the object's owned fields are read before and
    /// after, and what changed is freed (Runtime.FreeOwnedReplaced frees the
    /// old value when it differs). The callee cannot have kept the old value --
    /// an owned field's read never escapes -- and a callee that hands a
    /// field's value back (a borrower) is left alone when its result is used.
    /// </summary>
    private void FreeReplacedAcrossCalls(Function f, Dictionary<string, bool[]> summaries, Dictionary<Instr, bool> privateOwner,
        Func<string, List<long>> ownedOffsets, Func<string, bool> handsBack)
    {
        Dictionary<VReg, Instr>? defs = null;
        Dictionary<Instr, string?> descriptorOf = new(ReferenceEqualityComparer.Instance);
        int w = IrTypes.Word.Bytes();
        foreach (Block b in f.Blocks)
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr call = b.Instrs[at];
                if (call.Op is not (Opcode.Call or Opcode.CallIndirect) || _bookkeeping.Contains(call)) continue;
                if (call.Op == Opcode.Call && (call.Callee is null || IsAllocator(call.Callee) || IsFreeCall(call.Callee)
                    || IsCollectorNote(call.Callee) || call.Callee == AsyncFrame.Suspend)) continue;
                if (call.Dest is not null && (call.Op == Opcode.CallIndirect || handsBack(call.Callee!))) continue;
                defs ??= SingleDefs(f);
                List<(RegOperand Owner, long Offset)> watched = new();
                HashSet<Instr> ownersSeen = new(ReferenceEqualityComparer.Instance);
                foreach (Operand o in call.Operands)
                {
                    if (o is not RegOperand r || OriginOf(defs, r.Reg) is not Instr made || !ownersSeen.Add(made)
                        || !PrivateOwner(f, made, summaries, privateOwner)) continue;
                    if (!descriptorOf.TryGetValue(made, out string? descriptor)) descriptorOf[made] = descriptor = DescriptorStored(f, defs, made);
                    if (descriptor is null) continue;
                    foreach (long offset in ownedOffsets(descriptor)) watched.Add((r, offset));
                }
                if (watched.Count == 0) continue;
                List<Instr> before = new();
                List<VReg> olds = new();
                foreach ((RegOperand owner, long offset) in watched)
                {
                    VReg old = f.NewReg(IrTypes.Word);
                    Instr load = new() { Op = Opcode.Load, Dest = old, Offset = offset, Size = w, Line = call.Line };
                    load.Operands.Add(owner);
                    before.Add(load);
                    olds.Add(old);
                }
                List<Instr> after = new();
                for (int k = 0; k < watched.Count; k++)
                {
                    VReg now = f.NewReg(IrTypes.Word);
                    Instr load = new() { Op = Opcode.Load, Dest = now, Offset = watched[k].Offset, Size = w, Line = call.Line };
                    load.Operands.Add(watched[k].Owner);
                    after.Add(load);
                    Instr free = new() { Op = Opcode.Call, Callee = OwnedReplacedFreer, Line = call.Line };
                    free.Operands.Add(Word(f, after, new RegOperand(olds[k]), call.Line));
                    free.Operands.Add(Word(f, after, new RegOperand(now), call.Line));
                    after.Add(free);
                }
                b.Instrs.InsertRange(at + 1, after);
                b.Instrs.InsertRange(at, before);
                _bookkeeping.UnionWith(before);
                _bookkeeping.UnionWith(after);
                at += before.Count + after.Count;
            }
    }

    /// <summary>The descriptor stored into an object where it is made (`t_...`), or null.</summary>
    private static string? DescriptorStored(Function f, Dictionary<VReg, Instr> defs, Instr made)
    {
        SlotOperand? slot = made is { Op: Opcode.Copy, Operands: [SlotOperand s] } ? s : null;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.Store || i.Offset != 0 || i.Operands.Count < 2 || i.Operands[1] is not SymOperand { Name: var name }
                    || !name.StartsWith("t_", StringComparison.Ordinal)) continue;
                bool mine = i.Operands[0] is RegOperand r && ReferenceEquals(OriginOf(defs, r.Reg), made)
                    || slot is not null && i.Operands[0] is SlotOperand other && ReferenceEquals(other.Slot, slot.Slot);
                if (mine) return name;
            }
        return null;
    }

    /// <summary>Through copies to what made a register, where one instruction did.</summary>
    private static Instr? OriginOf(Dictionary<VReg, Instr> defs, VReg r)
    {
        for (int hop = 0; hop < 8 && defs.TryGetValue(r, out Instr? d); hop++)
        {
            if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands.Count == 1 && d.Operands[0] is RegOperand from) { r = from.Reg; continue; }
            return d;
        }
        return null;
    }

    /// <summary>Each register written exactly once, to the instruction writing it.</summary>
    private static Dictionary<VReg, Instr> SingleDefs(Function f)
    {
        Dictionary<VReg, Instr> d = new();
        HashSet<VReg> many = new();
        foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (i.Dest is not null && !d.TryAdd(i.Dest, i)) many.Add(i.Dest);
        foreach (VReg r in many) d.Remove(r);
        return d;
    }

    /// <summary>
    /// THE REGISTERS THAT HOLD WHAT A READ GAVE AND NOTHING ELSE, or an address
    /// inside it a constant away: the read's own register, written by the
    /// read alone, and what copies and constant offsets make of it, each
    /// written once. Empty when the read's register is written elsewhere too.
    /// </summary>
    private static HashSet<VReg> ReadInterior(Function f, Dictionary<VReg, Instr> defs, VReg read)
    {
        HashSet<VReg> inside = new();
        if (!defs.ContainsKey(read) || f.Params.Contains(read)) return inside;
        inside.Add(read);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { } d || inside.Contains(d) || !defs.ContainsKey(d) || f.Params.Contains(d)
                        || i.Operands.Count == 0 || i.Operands[0] is not RegOperand { Reg: var from } || !inside.Contains(from)) continue;
                    bool same = i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Operands.Count == 1
                        || i.Op is Opcode.Add or Opcode.Sub && i.Operands.Count == 2 && i.Operands[1] is ImmOperand { Value: var by }
                           && by > -1048576 && by < 1048576;
                    if (same) { inside.Add(d); grew = true; }
                }
        }
        return inside;
    }

    /// <summary>
    /// WHAT A STORE PUTS IN A FIELD, BY TYPE: the descriptor an object made
    /// right here -- by the allocator, or in the frame where this pass put
    /// it -- is stamped with; null for anything else (another function's
    /// result, a parameter, static data, a join). A field whose every store
    /// anywhere puts null or an object of one stamp holds only that type,
    /// and a virtual call on what is read from it reaches that type's method
    /// alone (Analyse's `exact`).
    /// </summary>
    private (string Name, long Offset)? StoredStamp(Function f, Dictionary<VReg, Instr> defs, Instr store)
    {
        if (store.Operands.Count < 2 || store.Operands[1] is not RegOperand { Reg: var value } || OriginOf(defs, value) is not { Dest: { } made } origin)
            return null;
        if (origin.Op == Opcode.Call && IsAllocator(origin.Callee)) return StampOf(f, made) is Stamp stamp ? (stamp.Descriptor, stamp.Base) : null;
        if (origin is not { Op: Opcode.Copy, Operands: [SlotOperand { Slot: var slot }] } || !_promotedMade.Contains(origin)) return null;
        (string Name, long Offset)? found = null;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.Store || i.Offset != 0 || i.Operands.Count < 2 || i.Operands[1] is not SymOperand { Name: var t, Offset: var at }
                    || !IsDescriptor(t)) continue;
                bool into = i.Operands[0] is SlotOperand { Slot: var named } && named == slot
                    || i.Operands[0] is RegOperand { Reg: var r } && ReferenceEquals(OriginOf(defs, r), origin);
                if (!into) continue;
                if (found is not null && found.Value != (t, at)) return null;
                found = (t, at);
            }
        return found;
    }

    /// <summary>
    /// Per field, the one stamp every store of <paramref name="stores"/> puts
    /// in it, null or a store of an object made here aside; null for a field
    /// some store puts anything else in.
    /// </summary>
    private Dictionary<string, (string Name, long Offset)?> FieldStamps(IEnumerable<(Function F, Block B, Instr I)> stores, Func<Function, Dictionary<VReg, Instr>> defs)
    {
        Dictionary<string, (string Name, long Offset)?> stamps = new(StringComparer.Ordinal);
        foreach ((Function f, _, Instr st) in stores)
        {
            if (st.Field is not string field || stamps.TryGetValue(field, out var known) && known is null) continue;
            if (st.Operands.Count >= 2 && st.Operands[1] is ImmOperand { Value: 0 }) continue;
            (string Name, long Offset)? stamp = StoredStamp(f, defs(f), st);
            stamps[field] = stamp is null || known is { } other && other != stamp.Value ? null : stamp;
        }
        return stamps;
    }

    /// <summary>
    /// Where in block <paramref name="x"/> something of <paramref name="derived"/>
    /// -- what a read gave, read at <paramref name="readAt"/> of it, or -1 for
    /// a read elsewhere -- is live, as [From, To): from the block's start or
    /// just after the read or the copy that made it here, to the block's end
    /// or the last use. Empty, (0, 0), where nothing of it is.
    /// </summary>
    // In loops, not queries: asked for every read in every block, the
    // lambdas, closures and boxed enumerators were 5.5% of what a unit
    // allocated.
    private static (int From, int To) LiveStretch(Liveness liveness, Block x, int readAt, HashSet<VReg> derived)
    {
        bool liveIn = false, liveOut = false;
        foreach (VReg r in derived)
        {
            if (!liveIn && liveness.IsLiveIn(x, r)) liveIn = true;
            if (!liveOut && liveness.IsLiveOut(x, r)) liveOut = true;
            if (liveIn && liveOut) break;
        }
        int from = liveIn ? 0 : readAt >= 0 ? readAt + 1 : -1;
        List<Instr> instrs = x.Instrs;
        if (from < 0)
        {
            int made = -1;
            for (int k = 0; k < instrs.Count; k++)
                if (instrs[k].Dest is VReg d && derived.Contains(d)) { made = k; break; }
            if (made < 0) return (0, 0);
            from = made + 1;
        }
        int to = instrs.Count;
        if (!liveOut)
        {
            to = from;
            for (int k = from; k < instrs.Count; k++)
            {
                List<Operand> operands = instrs[k].Operands;
                for (int o = 0; o < operands.Count; o++)
                    if (operands[o] is RegOperand r && derived.Contains(r.Reg)) { to = k; break; }
            }
        }
        return (from, to);
    }

    /// <summary>
    /// A STORE INTO THE VERY OBJECT A READ OF AN OWNED FIELD GAVE -- an inlined
    /// List.Add writing the list's own count, version and array while the
    /// list read from a field is in hand -- is no danger to that read. What
    /// a store into an owned field replaces is freed only where the store's
    /// object was made, here, and kept here (PrivateOwner): never in an
    /// object read out of a field. And nothing it could free is the value
    /// read or holds it: the old value is the read object's own child, and
    /// an owned field holds only what was handed to it alone.
    /// </summary>
    private static bool StoreIntoRead(Instr store, HashSet<VReg> inside)
        => store.Op == Opcode.Store && store.Operands.Count > 0 && store.Operands[0] is RegOperand { Reg: var into } && inside.Contains(into);

    /// <summary>
    /// A STORE THAT FILLS A FIELD OF AN OBJECT JUST MADE for the first time --
    /// an inlined constructor's, `_occ[v] = new List()` while the array read
    /// from `_occ` is in hand -- replaces nothing: the allocator hands its
    /// block over zeroed, and the free a replacement gets is handed null.
    /// So on every way back from the store, the object is made before the
    /// field is written any other way: nothing between writes it there, and
    /// nothing between takes the object anywhere it could be written from --
    /// no call but the collector's notes, no copy of its bytes, no store of
    /// it, no register holding it that is not only its address. One per
    /// function, each store judged once.
    /// </summary>
    private sealed class FirstStores
    {
        private readonly Function _f;
        private readonly Dictionary<VReg, Instr> _defs;
        private readonly HashSet<Instr> _promotedMade, _zeroing;
        private Cfg? _cfg;
        private readonly Dictionary<Instr, Dictionary<VReg, long>> _addresses = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Instr, bool> _known = new(ReferenceEqualityComparer.Instance);

        /// <param name="promotedMade">The copies of a frame slot's address standing where a promoted object was made.</param>
        /// <param name="zeroing">The zeroing that makes each such object, as the allocator's call made it.</param>
        public FirstStores(Function f, Dictionary<VReg, Instr> defs, HashSet<Instr> promotedMade, HashSet<Instr> zeroing)
        { _f = f; _defs = defs; _promotedMade = promotedMade; _zeroing = zeroing; }

        /// <summary>Whether the store at <paramref name="at"/> of <paramref name="x"/> is the first into its field of an object just made.</summary>
        public bool Fills(Block x, int at)
        {
            Instr store = x.Instrs[at];
            if (_known.TryGetValue(store, out bool known)) return known;
            return _known[store] = Judge(x, at, store);
        }

        private bool Judge(Block x, int at, Instr store)
        {
            if (store.Op != Opcode.Store || store.Field is null || store.Operands.Count < 2) return false;
            // Made by the allocator, or in the frame where this pass put it
            // (zeroed there, as the allocator's memory is): through a register
            // holding its address, or its slot named directly.
            Instr? made = null;
            FrameSlot? slot = null;
            if (store.Operands[0] is RegOperand { Reg: var into }) made = OriginOf(_defs, into);
            else if (store.Operands[0] is SlotOperand { Slot: var named })
                foreach (Instr p in _promotedMade)
                    if (p is { Operands: [SlotOperand { Slot: var s }] } && s == named) { made = p; break; }
            if (made is not { Dest: { } root }) return false;
            bool framed = made is { Op: Opcode.Copy, Operands: [SlotOperand { Slot: var held }] } && _promotedMade.Contains(made);
            if (framed) slot = ((SlotOperand)made.Operands[0]).Slot;
            else if (made.Op != Opcode.Call || !IsAllocator(made.Callee)) return false;
            if (!_addresses.TryGetValue(made, out Dictionary<VReg, long>? addresses))
                _addresses[made] = addresses = OwnedFieldEscape.Addresses(_f, root);
            long start = 0;
            if (store.Operands[0] is RegOperand { Reg: var through } && !addresses.TryGetValue(through, out start)) return false;
            long field = start + store.Offset;
            _cfg ??= new Cfg(_f);
            HashSet<Block> seen = new();
            Stack<(Block Block, int End)> work = new();
            work.Push((x, at));
            while (work.TryPop(out (Block Block, int End) item))
            {
                bool reached = false;
                for (int k = item.End - 1; k >= 0; k--)
                {
                    Instr i = item.Block.Instrs[k];
                    if (!framed && ReferenceEquals(i, made)
                        || framed && _zeroing.Contains(i) && Mine(i.Operands[0], addresses, slot)) { reached = true; break; }
                    if (!Harmless(i, addresses, slot, field, store.Size)) return false;
                }
                if (reached) continue;
                // Entered from somewhere the object was not made: a root, or
                // round a loop back to the store itself (judged above).
                if (_cfg.IsRoot(item.Block)) return false;
                foreach (Block p in _cfg.Preds(item.Block))
                    if (seen.Add(p)) work.Push((p, p.Instrs.Count));
            }
            return true;
        }

        /// <summary>The object's address: a register holding it, or its frame slot.</summary>
        private static bool Mine(Operand o, Dictionary<VReg, long> addresses, FrameSlot? slot)
            => o is RegOperand { Reg: var r } && addresses.ContainsKey(r) || slot is not null && o is SlotOperand { Slot: var s } && s == slot;

        /// <summary>What may run between the object's making and the store without writing the field or letting the object go.</summary>
        private static bool Harmless(Instr i, Dictionary<VReg, long> addresses, FrameSlot? slot, long field, int width)
        {
            bool Mine(Operand o) => FirstStores.Mine(o, addresses, slot);
            switch (i.Op)
            {
                case Opcode.Store:
                    if (i.Operands.Count < 2 || Mine(i.Operands[1])) return false;
                    long from;
                    if (i.Operands[0] is RegOperand { Reg: var b } && addresses.TryGetValue(b, out from)
                        || slot is not null && i.Operands[0] is SlotOperand { Slot: var s } && s == slot && (from = 0) == 0)
                        return from + i.Offset + i.Size <= field || field + width <= from + i.Offset;
                    return !i.Operands.Skip(2).Any(Mine);
                case Opcode.Call:
                case Opcode.CallIndirect:
                    return IsCollectorNote(i.Callee) || !i.Operands.Any(Mine);
                case Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Add or Opcode.Sub:
                    return !i.Operands.Any(Mine) || i.Dest is not null && addresses.ContainsKey(i.Dest);
                case Opcode.Load or Opcode.ArrayLength:
                    return !i.Operands.Skip(1).Any(Mine);
                default:
                    return !i.Operands.Any(Mine) || IrInfo.IsIntCompare(i.Op) || i.Op == Opcode.Branch;
            }
        }
    }

    /// <summary>
    /// THE LINK'S ANSWER APPLIED, as OwnedFields applies its own: every store
    /// into an owned field frees what it replaces, unless the object was
    /// made right there; each type defined here that has owned fields, or
    /// whose ancestors do, carries the map of them in its descriptor.
    /// </summary>
    private void ApplyOwnedFields(Module m, OwnedFieldFacts decided, Dictionary<string, bool[]> summaries)
    {
        if (decided.Fields.Count == 0) return;
        Dictionary<Instr, bool> privateOwner = new(ReferenceEqualityComparer.Instance);
        foreach (Function f in m.Functions)
        {
            Dictionary<VReg, Instr>? defs = null;
            foreach (Block b in f.Blocks)
                for (int at = 0; at < b.Instrs.Count; at++)
                {
                    Instr st = b.Instrs[at];
                    if (st.Op != Opcode.Store || st.Field is null || st.Operands.Count < 2 || st.Operands[0] is SymOperand
                        || !decided.Fields.ContainsKey(st.Field)) continue;
                    defs ??= SingleDefs(f);
                    // ONLY IN AN OBJECT NO OTHER THREAD CAN SEE, as OwnedFields
                    // decides it: one this function made and that never escapes
                    // it, promoted to its frame or not. A field of a shared
                    // object replaced under a lock on one processor would free
                    // what another had just read; its old value is the
                    // collector's.
                    if (st.Operands[0] is not RegOperand baseReg || OriginOf(defs, baseReg.Reg) is not Instr madeOwner
                        || !PrivateOwner(f, madeOwner, summaries, privateOwner)) continue;
                    List<Instr> made = new();
                    VReg old = f.NewReg(IrTypes.Word);
                    Instr load = new() { Op = Opcode.Load, Dest = old, Offset = st.Offset, Size = st.Size, Line = st.Line };
                    load.Operands.Add(st.Operands[0]);
                    made.Add(load);
                    Instr free = new() { Op = Opcode.Call, Callee = OwnedReplacedFreer, Line = st.Line };
                    free.Operands.Add(Word(f, made, new RegOperand(old), st.Line));
                    free.Operands.Add(Word(f, made, st.Operands[1], st.Line));
                    made.Add(free);
                    b.Instrs.InsertRange(at, made);
                    _bookkeeping.UnionWith(made);
                    at += made.Count;
                }
        }

        // What calls replace in this function's own objects (FreeReplacedAcrossCalls).
        {
            Dictionary<string, List<long>> byOwner = new(StringComparer.Ordinal);
            foreach ((string field, long offset) in decided.Fields)
            {
                int split = field.IndexOf("::", StringComparison.Ordinal);
                if (split <= 0) continue;
                string owner = "t_" + field[..split];
                if (!byOwner.TryGetValue(owner, out List<long>? list)) byOwner[owner] = list = new();
                if (!list.Contains(offset)) list.Add(offset);
            }
            Dictionary<string, DataItem> described = new(StringComparer.Ordinal);
            foreach (DataItem d in m.Data) described[d.Name] = d;
            Dictionary<string, List<long>> ofType = new(StringComparer.Ordinal);
            List<long> OwnedOf(string descriptor)
            {
                if (ofType.TryGetValue(descriptor, out List<long>? known)) return known;
                HashSet<string> up = described.ContainsKey(descriptor) ? Ancestry(described, descriptor) : new HashSet<string>(StringComparer.Ordinal) { descriptor };
                return ofType[descriptor] = up.Where(byOwner.ContainsKey).SelectMany(a => byOwner[a]).Distinct().Order().ToList();
            }
            foreach (Function f in m.Functions)
                if (f.Async is null) FreeReplacedAcrossCalls(f, summaries, privateOwner, OwnedOf, decided.Borrowers.Contains);
        }

        // And which of them own their arrays' elements (the map's element bits).
        Dictionary<string, List<long>> offsets = new(StringComparer.Ordinal);
        Dictionary<string, List<long>> elementOffsets = new(StringComparer.Ordinal);
        foreach (string field in decided.Mapped.Order(StringComparer.Ordinal))
        {
            int split = field.IndexOf("::", StringComparison.Ordinal);
            if (split <= 0 || !decided.Fields.TryGetValue(field, out long offset)) continue;
            string owner = "t_" + field[..split];
            if (!offsets.TryGetValue(owner, out List<long>? list)) offsets[owner] = list = new();
            if (!list.Contains(offset)) list.Add(offset);
            if (!decided.ArrayElements.Contains(field)) continue;
            if (!elementOffsets.TryGetValue(owner, out List<long>? elements)) elementOffsets[owner] = elements = new();
            if (!elements.Contains(offset)) elements.Add(offset);
        }
        int w = Target.Current.WordSize;
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) items[d.Name] = d;
        foreach (DataItem d in m.Data.Where(d => d.Name.StartsWith("t_", StringComparison.Ordinal)).ToList())
        {
            List<long> mine = Ancestry(items, d.Name).Where(offsets.ContainsKey).SelectMany(a => offsets[a]).Distinct().Order().ToList();
            if (mine.Count == 0 || d.Bytes.Length < 12 * w || mine.Any(o => o % w != 0) || d.Relocs.Any(r => r.Offset == 11 * w)) continue;
            int words = (int)(mine.Max() / w) + 1;
            uint[] bits = new uint[(words + 31) / 32];
            foreach (long o in mine) bits[(int)(o / w) / 32] |= 1u << (int)(o / w % 32);
            // The element bits after the field bits, as many words, the
            // count word saying they follow (Escape.ElementMapFlag).
            List<long> elementsMine = Ancestry(items, d.Name).Where(elementOffsets.ContainsKey).SelectMany(a => elementOffsets[a]).Distinct().Where(mine.Contains).ToList();
            uint[] elementBits = new uint[elementsMine.Count > 0 ? bits.Length : 0];
            foreach (long o in elementsMine) elementBits[(int)(o / w) / 32] |= 1u << (int)(o / w % 32);
            byte[] block = new byte[(1 + bits.Length + elementBits.Length) * w];
            long countWord = words | (elementBits.Length > 0 ? ElementMapFlag : 0);
            for (int k = 0; k < w; k++) block[k] = (byte)(countWord >> (8 * k));
            for (int i = 0; i < bits.Length; i++) for (int k = 0; k < 4; k++) block[(1 + i) * w + k] = (byte)(bits[i] >> (8 * k));
            for (int i = 0; i < elementBits.Length; i++) for (int k = 0; k < 4; k++) block[(1 + bits.Length + i) * w + k] = (byte)(elementBits[i] >> (8 * k));
            string sym = "om_" + d.Name[2..];
            m.Data.Add(new DataItem(sym, block) { ReadOnly = true, Exported = false, Align = w });
            d.Relocs.Add(new DataReloc(11 * w, sym, 0));
        }
    }

    /// <summary>What a read of a value does while it is live, judged without knowing which field it is.</summary>
    private sealed class ReadJudgement
    {
        public LifetimeCondition Needs = new();
        public HashSet<string> Danger = new(StringComparer.Ordinal);
        public HashSet<string> DangerFields = new(StringComparer.Ordinal);
        public bool HandedBack;
    }

    /// <summary>
    /// THE UNIT'S HINTS FOR OWNED FIELDS: OwnedFields' judgement of every
    /// store and read this unit has, each question about another unit written
    /// down for the link rather than answered.
    /// </summary>
    private OwnedFieldHints OwnedFieldHintsOf(Module m, Dictionary<string, bool[]> summaries)
    {
        OwnedFieldHints hints = new();

        // What the unit names: every symbol code or data takes the address of.
        HashSet<string> own = new(m.Data.Select(d => d.Name), StringComparer.Ordinal);
        bool Named(string name) => !own.Contains(name) && !IsDescriptor(name) && !name.StartsWith("i_", StringComparison.Ordinal);
        foreach (DataItem d in m.Data)
            foreach (DataReloc r in d.Relocs)
            {
                if (!Named(r.Symbol)) continue;
                hints.Addressed.Add(r.Symbol);
                if (IsDescriptor(d.Name)) hints.Slotted.Add(r.Symbol);
            }
        foreach (Function f in m.Functions) foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs)
            foreach (Operand o in i.Operands)
                if (o is SymOperand sym && Named(sym.Name)) { hints.Addressed.Add(sym.Name); hints.CodeNamed.Add(sym.Name); }
        hints.UnresolvedVirtual = m.Functions.Any(g => g.Blocks.Any(bb => bb.Instrs.Any(i => i.Op == Opcode.CallIndirect && i.DispatchType is not null
            && (_indirect is null || !_indirect.ContainsKey(i)))));

        OwnedFieldRecord Record(string field, long offset)
        {
            if (!hints.Fields.TryGetValue(field, out OwnedFieldRecord? record)) hints.Fields[field] = record = new() { Offset = offset };
            else if (record.Offset != offset) record.Refused = true;
            return record;
        }
        bool reporting = Switches.AllocReport;
        void Refuse(OwnedFieldRecord record, string field, string why, Function f, Instr at)
        {
            if (!record.Refused && reporting) _fieldReport.Add($"{field} refused in this unit: {why} in {f.Name}:{at.Line}");
            record.Refused = true;
        }
        void Need(OwnedFieldRecord record, LifetimeCondition condition)
        {
            if (!record.Needs.Add(condition)) { record.Refused = true; record.Needs.Stays.Clear(); record.Needs.Fresh.Clear(); record.Needs.Fields.Clear(); }
        }

        // WHAT EACH FUNCTION CALLS AND STORES: the call graph the link finds
        // every function that may replace a field on.
        Dictionary<string, (SortedSet<string> Calls, SortedSet<string> Writes, SortedSet<string> InitWrites, SortedSet<string> Borrows)> functions = new(StringComparer.Ordinal);
        (SortedSet<string> Calls, SortedSet<string> Writes, SortedSet<string> InitWrites, SortedSet<string> Borrows) FunctionOf(string name)
        {
            if (!functions.TryGetValue(name, out var record))
                functions[name] = record = (new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            return record;
        }

        Dictionary<Function, Dictionary<VReg, Instr>> defsOf = new();
        Dictionary<VReg, Instr> Defs(Function f) => defsOf.TryGetValue(f, out var d) ? d : defsOf[f] = SingleDefs(f);
        Dictionary<Function, RegisterWrites> writesOf = new();

        // A call's result made for its caller: an allocation, a fresh function
        // of this unit, or -- if the link finds it fresh -- another unit's.
        LifetimeCondition? FreshCondition(Instr? w)
        {
            if (w is not { Op: Opcode.Call, Callee: string callee } || w.Dest is null) return null;
            if (IsAllocator(callee) || _fresh.Contains(callee)) return new LifetimeCondition();
            Needs needs = new(this);
            return needs.AllowFresh(callee) ? needs.Condition : null;
        }

        List<(Function F, Block B, Instr I)> stores = new(), loads = new();
        // Stores whose object is used after them, with the object made: each a
        // read of its field from the store on, as Escape.OwnedFields has it.
        Dictionary<Instr, VReg> forwarded = new(ReferenceEqualityComparer.Instance);
        foreach (Function f in m.Functions)
        {
            var record = FunctionOf(f.Name);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is not null) record.Calls.Add(i.Callee);
                    else if (i.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(i, out string[]? targets)) record.Calls.UnionWith(targets);
                    if (i.Field is null || i.Operands.Count < 1 || i.Operands[0] is SymOperand || i.ReturnsFreshStruct) continue;
                    if (i.Op == Opcode.Store)
                    {
                        stores.Add((f, b, i));
                        Record(i.Field, i.Offset).Stored = true;
                        if (i.Operands[0] is not RegOperand baseReg) record.Writes.Add(i.Field);
                        else if (OriginOf(Defs(f), baseReg.Reg) is { Op: Opcode.Call } made && IsAllocator(made.Callee)) { }
                        else if (f.Params.Count > 0 && baseReg.Reg == f.Params[0]) record.InitWrites.Add(i.Field);
                        else record.Writes.Add(i.Field);
                    }
                    else if (i.Op == Opcode.Load)
                    {
                        loads.Add((f, b, i));
                        Record(i.Field, i.Offset);
                    }
                    // A field's address taken (ref, out, Interlocked on it):
                    // what is read and written through it is no load or store
                    // above, and proves nothing -- refused, as OwnedFields does.
                    else Refuse(Record(i.Field, i.Offset), i.Field, "its address is taken (ref, out)", f, i);
                }
        }

        // CONSTRUCTORS' OWN STORES (OwnedFields' freshThis): for each callee,
        // whether every call here passes first an object just made, or the
        // caller's own first argument (then the caller must be one too).
        Dictionary<string, SortedSet<string>?> freshFirst = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions)
            foreach (Block b in g.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    if (b.Instrs[k] is not { Op: Opcode.Call, Callee: string callee } call) continue;
                    if (freshFirst.TryGetValue(callee, out SortedSet<string>? callers) && callers is null) continue;
                    string? needs = null;
                    bool ok = call.Operands.Count > 0 && call.Operands[0] is RegOperand r && (
                        g.Params.Count > 0 && r.Reg == g.Params[0] ? (needs = g.Name) is not null : JustMade(g, b, k, r.Reg));
                    if (!ok) { freshFirst[callee] = null; continue; }
                    callers ??= freshFirst[callee] = new SortedSet<string>(StringComparer.Ordinal);
                    if (needs is not null) callers!.Add(needs);
                }
        bool JustMade(Function g, Block at, int index, VReg arg)
        {
            if (OriginOf(Defs(g), arg) is not { Op: Opcode.Call } made || !IsAllocator(made.Callee)) return false;
            int from = at.Instrs.IndexOf(made);
            if (from < 0 || from > index) return false;
            HashSet<VReg> names = new() { made.Dest! };
            for (int k = from + 1; k < index; k++)
            {
                Instr i = at.Instrs[k];
                if (!i.Operands.Any(o => o is RegOperand u && names.Contains(u.Reg))) continue;
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null) { names.Add(i.Dest); continue; }
                if (i.Op == Opcode.Store && i.Operands[1] is SymOperand) continue;
                return false;
            }
            return true;
        }
        foreach ((string callee, SortedSet<string>? callers) in freshFirst)
            if (callers is not null && callers.Count <= OwnedFieldHints.Limit) hints.FreshFirst[callee] = callers;

        // Where a value comes from, through copies and joins (OwnedFields' Sources).
        Dictionary<(Function, VReg), List<Source>?> sourcesOf = new();
        List<Source>? Sources(Function f, VReg r)
        {
            if (sourcesOf.TryGetValue((f, r), out List<Source>? known)) return known;
            List<Source> found = new();
            HashSet<VReg> seen = new();
            Stack<VReg> work = new();
            work.Push(r);
            if (!writesOf.TryGetValue(f, out RegisterWrites? writes)) writesOf[f] = writes = new(f);
            while (work.Count > 0 && found.Count < 16)
            {
                VReg at = work.Pop();
                if (!seen.Add(at)) continue;
                int param = f.Params.IndexOf(at);
                if (param >= 0) { found.Add(new Source(SourceKind.Parameter, param, null)); continue; }
                if (!writes.TryGetValue(at, out WriteList ws)) { found.Add(new Source(SourceKind.Unknown, 0, null)); continue; }
                foreach (Instr w in ws)
                {
                    if (w.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                    {
                        foreach (Operand o in w.Operands)
                        {
                            if (o is ImmOperand { Value: 0 }) { if (!found.Any(x => x.Kind == SourceKind.Null)) found.Add(new Source(SourceKind.Null, 0, null)); }
                            else if (o is SymOperand) { if (!found.Any(x => x.Kind == SourceKind.Static)) found.Add(new Source(SourceKind.Static, 0, null)); }
                            else if (o is RegOperand from) work.Push(from.Reg);
                            // AN OBJECT THIS PASS PUT IN THE FRAME: taken for an
                            // unknown value, every field a unit's inlined
                            // constructor filled with an array it made in the
                            // frame was refused for the whole program. It is
                            // judged as the object made fresh that it was --
                            // handed over at the store, going nowhere else --
                            // and not as static data, as OwnedFields' Sources
                            // takes it: the link runs these passes again over
                            // the same IR, and what this run put in the frame
                            // that one may leave a heap block, the budget gone
                            // to another. Either way the field may own it: a
                            // free handed a frame block finds none to free.
                            else if (o is SlotOperand && _promotedMade.Contains(w) && w.Dest is not null) found.Add(new Source(SourceKind.Fresh, 0, w));
                            else found.Add(new Source(SourceKind.Unknown, 0, null));
                        }
                        continue;
                    }
                    found.Add(FreshCondition(w) is not null ? new Source(SourceKind.Fresh, 0, w) : new Source(SourceKind.Unknown, 0, w));
                }
            }
            if (work.Count > 0) found.Add(new Source(SourceKind.Unknown, 0, null));
            return sourcesOf[(f, r)] = found;
        }

        Dictionary<Function, FirstStores> firstOf = new();
        FirstStores FirstOf(Function f) => firstOf.TryGetValue(f, out FirstStores? known) ? known : firstOf[f] = new FirstStores(f, Defs(f), _promotedMade, _promotedZeroing);
        Dictionary<Function, Liveness> livenessOf = new();
        Liveness LivenessOf(Function f) => livenessOf.TryGetValue(f, out Liveness? l) ? l : livenessOf[f] = new Liveness(f);
        Dictionary<Function, HashSet<VReg>> padsOf = new();
        HashSet<VReg> PadsOf(Function f) => padsOf.TryGetValue(f, out HashSet<VReg>? p) ? p : padsOf[f] = PadLive(LivenessOf(f));

        // SINKS: each direct call's arguments, handed over or not.
        HashSet<(string, int)> kept = new();
        Dictionary<(string, int), OwnedSink> sinks = new();
        foreach (Function g in m.Functions)
        {
            int judged = 0;
            foreach (Block b in g.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    if (b.Instrs[k] is not { Op: Opcode.Call, Callee: string callee } c || IsIntrinsic(callee) || IsAllocator(callee) || IsFreeCall(callee)) continue;
                    for (int index = 0; index < c.Operands.Count; index++)
                    {
                        if (kept.Contains((callee, index))) continue;
                        Operand arg = c.Operands[index];
                        if (arg is ImmOperand { Value: 0 } or SymOperand) continue;
                        LifetimeCondition needs = new();
                        SortedSet<(string, int)> inner = new(OwnedFieldHints.PairOrder.Instance);
                        bool ok = arg is RegOperand a && Sources(g, a.Reg) is { } sources && sources.All(s => s.Kind != SourceKind.Unknown);
                        if (ok && ((RegOperand)arg).Reg is var reg && Sources(g, reg)!.Any(s => s.Kind is SourceKind.Fresh or SourceKind.Parameter) && ++judged > SinkJudgements) ok = false;
                        if (ok)
                            foreach (Source src in Sources(g, ((RegOperand)arg).Reg)!)
                            {
                                if (src.Kind is SourceKind.Null or SourceKind.Static) continue;
                                if (src.Kind == SourceKind.Parameter)
                                {
                                    Needs asked = new(this);
                                    if (Analyse(g, new[] { g.Params[src.Index] }, summaries, null, needs: asked, consumers: new(ReferenceEqualityComparer.Instance) { c }).Escapes
                                        || !needs.Add(asked.Condition)) { ok = false; break; }
                                    inner.Add((g.Name, src.Index));
                                    continue;
                                }
                                if (src.Made!.Dest is null || _owned.Contains(src.Made) || _ownedCalls.Contains(src.Made)
                                    || FreshCondition(src.Made) is not LifetimeCondition fresh || !needs.Add(fresh)) { ok = false; break; }
                                Needs flowNeeds = new(this);
                                Flow flow = Analyse(g, new[] { src.Made.Dest }, summaries, src.Made, needs: flowNeeds, consumers: new(ReferenceEqualityComparer.Instance) { c });
                                if (flow.Escapes || !needs.Add(flowNeeds.Condition)) { ok = false; break; }
                                Liveness live = LivenessOf(g);
                                if (flow.Derived.Any(r => !live.Tracks(r) || live.IsLiveOut(b, r))) { ok = false; break; }
                                for (int after = k + 1; after < b.Instrs.Count && ok; after++)
                                    if (b.Instrs[after].Operands.Any(o => o is RegOperand r && flow.Derived.Contains(r.Reg))) ok = false;
                                if (!ok) break;
                            }
                        if (!ok) { kept.Add((callee, index)); sinks.Remove((callee, index)); continue; }
                        if (needs.IsTrue && inner.Count == 0) continue;
                        if (!sinks.TryGetValue((callee, index), out OwnedSink? sink)) sinks[(callee, index)] = sink = new();
                        if (!sink.Needs.Add(needs) || sink.Sinks.Count + inner.Count > LifetimeCondition.Limit) { kept.Add((callee, index)); sinks.Remove((callee, index)); continue; }
                        sink.Sinks.UnionWith(inner);
                    }
                }
        }
        foreach (((string, int) key, OwnedSink sink) in sinks) hints.Sinks[key] = sink;
        foreach ((string, int) key in kept) hints.Kept.Add(key);

        // HANDED OVER AT THE STORE (OwnedFields' HandedOver).
        bool HandedOver(Function f, Block b, Instr st, HashSet<VReg> derived)
            => HandedOverWithField(f, b, st, derived, LivenessOf(f));

        // THE STORES, each as OwnedFields judges it.
        foreach ((Function f, Block b, Instr st) in stores)
        {
            string field = st.Field!;
            OwnedFieldRecord record = Record(field, st.Offset);
            // What it puts there, by type, for a read judged as of one type.
            if (!(st.Operands.Count >= 2 && st.Operands[1] is ImmOperand { Value: 0 }))
                record.Kinds.Add(StoredStamp(f, Defs(f), st) is { } kind ? Kind(kind) : OwnedFieldRecord.UnknownKind);
            if (record.Refused) continue;
            // Judged as any other's (Escape.OwnedFields says why).
            if (st.Operands.Count < 2) { Refuse(record, field, "odd store", f, st); continue; }
            if (st.Operands[1] is ImmOperand { Value: 0 } or SymOperand) continue;
            if (st.Operands[1] is not RegOperand v) { Refuse(record, field, "stores an odd operand", f, st); continue; }
            if (OriginOf(Defs(f), v.Reg) is Instr made && made.Dest is not null && FreshCondition(made) is LifetimeCondition madeFresh)
            {
                Needs needs = new(this);
                Flow alone = Analyse(f, new[] { made.Dest }, summaries, made, new HashSet<Instr>(ReferenceEqualityComparer.Instance) { st }, needs: needs);
                if (!alone.Escapes)
                {
                    if (!HandedOver(f, b, st, alone.Derived)) forwarded[st] = made.Dest;
                    Need(record, madeFresh); Need(record, needs.Condition);
                    continue;
                }
            }
            if (Sources(f, v.Reg) is not { } sources || sources.Any(src => src.Kind == SourceKind.Unknown))
            {
                Refuse(record, field, $"stores {v}, not a fresh object", f, st);
                continue;
            }
            HashSet<Instr> only = new(ReferenceEqualityComparer.Instance) { st };
            foreach ((Function g, Block other, Instr sibling) in stores)
                if (ReferenceEquals(g, f) && !ReferenceEquals(sibling, st) && sibling.Field == st.Field
                    && !Reaches(f, b, other) && !Reaches(f, other, b))
                    only.Add(sibling);
            List<Instr> fresh = sources.Where(x => x.Kind == SourceKind.Fresh).Select(x => x.Made!).Distinct().ToList();
            bool ok = !fresh.Any(x => x.Dest is null);
            if (ok && fresh.Count > 0)
            {
                Needs needs = new(this);
                Flow together = Analyse(f, fresh.Select(x => x.Dest!).ToList(), summaries, fresh.Count == 1 ? fresh[0] : null, only, needs: needs);
                ok = !together.Escapes && HandedOver(f, b, st, together.Derived);
                if (ok) Need(record, needs.Condition);
                foreach (Instr x in fresh) if (ok && FreshCondition(x) is LifetimeCondition c) Need(record, c);
            }
            foreach (Source src in ok ? sources : new List<Source>())
            {
                if (src.Kind != SourceKind.Parameter) continue;
                Needs needs = new(this);
                if (Analyse(f, new[] { f.Params[src.Index] }, summaries, null, only, needs: needs).Escapes) { ok = false; break; }
                Need(record, needs.Condition);
                record.Sinks.Add((f.Name, src.Index));
                if (record.Sinks.Count > LifetimeCondition.Limit) { ok = false; break; }
            }
            if (!ok) Refuse(record, field, $"stores {v}, which a caller may not hand over", f, st);
        }

        // THE READS, each judged without knowing the field, then as a read of it.
        Dictionary<Function, HashSet<VReg>> returnedOf = new();
        HashSet<VReg> Returned(Function f)
        {
            if (returnedOf.TryGetValue(f, out HashSet<VReg>? known)) return known;
            HashSet<VReg> chain = new();
            RegisterWrites writes = writesOf.TryGetValue(f, out var w) ? w : writesOf[f] = new(f);
            Stack<VReg> work = new();
            foreach (Block rb in f.Blocks)
                if (rb.Terminator is { Op: Opcode.Ret, Operands: [RegOperand back] }) work.Push(back.Reg);
            while (work.TryPop(out VReg? r))
            {
                if (!chain.Add(r) || !writes.TryGetValue(r, out WriteList ws)) continue;
                foreach (Instr i in ws)
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                        foreach (Operand o in i.Operands) if (o is RegOperand from) work.Push(from.Reg);
            }
            return returnedOf[f] = chain;
        }
        ReadJudgement? Judge(Function f, Block b, Instr ld, out string why, (string Name, long Offset)? exact = null)
        {
            why = "";
            // An async body's read is refused only when held across a
            // suspension (the danger walk below), as Escape.OwnedFields does.
            VReg? value = ld.Op == Opcode.Store ? forwarded.GetValueOrDefault(ld) : ld.Dest;
            if (value is null) { why = "odd read"; return null; }
            if (ld.Op != Opcode.Store && UsedUpInBlock(f, b, ld)) return new ReadJudgement();
            bool FreesOwnMaking(Block x, int k)
            {
                Instr free = x.Instrs[k];
                if (free.Operands.Count == 0 || free.Operands[0] is not RegOperand freed) return false;
                Instr? made = OriginOf(Defs(f), freed.Reg);
                // AN OWNED SLOT'S FREE -- what the slot held, loaded where the
                // function leaves -- is its record's: the object it made there.
                if (made is { Op: Opcode.Load } && RecordedOrigin(f, free) is Instr recordedOrigin) made = recordedOrigin;
                if (made is not { Op: Opcode.Call, Callee: { } callee }) return false;
                if (callee == LeafAllocator) return true;
                // Or a box made here or handed back fresh (HandsBackLeafOrBox).
                if (IsAllocator(callee) && made.Dest is not null && StampOf(f, made.Dest) is { Descriptor: var stamp }
                    && stamp.StartsWith("b_", StringComparison.Ordinal)) return true;
                return IsFreshCall(made) && HandsBackLeafOrBox(callee);
            }
            HashSet<VReg>? back = f.Name == m.Entry ? null : Returned(f);
            Needs needs = new(this);
            Flow flow = Analyse(f, new[] { value }, summaries, ld, ld.Op == Opcode.Store ? new HashSet<Instr>(ReferenceEqualityComparer.Instance) { ld } : null,
                returnable: back is { Count: > 0 } ? back : null, needs: needs, exact: exact);
            if (flow.Escapes) { why = $"read escapes via {flow.Why?.Op} {flow.Why?.Callee}"; return null; }
            ReadJudgement judged = new() { HandedBack = back is not null && flow.Derived.Overlaps(back) };
            judged.Needs.Add(needs.Condition);
            Liveness liveness = LivenessOf(f);
            HashSet<VReg> pads = PadsOf(f);
            // Into a handler, wherever it can be entered from (PadLiveAt), as
            // Escape.OwnedFields has it.
            if (flow.Derived.Any(r => !liveness.Tracks(r))) { why = "read lives where liveness does not follow it"; return null; }
            bool intoPad = flow.Derived.Overlaps(pads);
            HashSet<VReg> inside = ReadInterior(f, Defs(f), value);
            foreach (Block x in f.Blocks)
            {
                (int from, int to) = LiveStretch(liveness, x, ReferenceEquals(x, b) ? b.Instrs.IndexOf(ld) : -1, flow.Derived);
                for (int k = 0; k < x.Instrs.Count; k++)
                {
                    if ((k < from || k >= to) && !(intoPad && PadLiveAt(liveness, x, k).Overlaps(flow.Derived))) continue;
                    Instr i = x.Instrs[k];
                    if (i.Op == Opcode.CallIndirect)
                    {
                        if (_indirect is null || !_indirect.TryGetValue(i, out string[]? t)) { why = "read live across an unresolved indirect call"; return null; }
                        judged.Danger.UnionWith(t);
                    }
                    else if (i.Op == Opcode.Call)
                    {
                        if (IsFreeCall(i.Callee) && !FreesOwnMaking(x, k) || IsCatchEnd(i.Callee) || i.Callee == OwnedReplacedFreer
                            || i.Callee == AsyncFrame.Suspend)
                        { why = $"read live across {i.Op} {i.Callee}"; return null; }
                        if (i.Callee is not null && !NeverWritesFields(i.Callee)) judged.Danger.Add(i.Callee);
                    }
                    // Not a field this unit refused: no store into it frees anything.
                    else if (i.Op == Opcode.Store && i.Field is not null && !(hints.Fields.TryGetValue(i.Field, out OwnedFieldRecord? into) && into.Refused)
                        && !StoreIntoRead(i, inside) && !FirstOf(f).Fills(x, k)) judged.DangerFields.Add(i.Field);
                }
            }
            if (judged.Danger.Count > OwnedFieldHints.Limit || judged.DangerFields.Count > OwnedFieldHints.Limit) { why = "read live across too much"; return null; }
            return judged;
        }

        // A READ USED UP WHERE IT IS MADE: every use of it, and of copies and
        // addresses made from it, later in its block, as a load's base, an
        // array length, a comparison or a branch; nothing of it live out of
        // the block or into a handler; and no call, free or store into a
        // field before its last use. What the full judgement finds of such a
        // read is always the same -- it keeps within, needs nothing, and is
        // live across nothing -- and this finds it without the flow analysis.
        bool UsedUpInBlock(Function f, Block b, Instr ld)
        {
            HashSet<VReg> derived = new() { ld.Dest! };
            int at = b.Instrs.IndexOf(ld);
            int last = at;
            bool quiet = true;
            for (int k = at + 1; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                bool uses = false;
                for (int o = 0; o < i.Operands.Count; o++)
                    if (i.Operands[o] is RegOperand r && derived.Contains(r.Reg))
                    {
                        uses = true;
                        bool fine = i.Op switch
                        {
                            Opcode.Load or Opcode.ArrayLength => o == 0,
                            Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Add or Opcode.Sub => i.Dest is not null,
                            Opcode.Eq or Opcode.Ne or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU
                                or Opcode.GtU or Opcode.GeU or Opcode.Branch => true,
                            _ => false,
                        };
                        if (!fine) return false;
                    }
                if (uses)
                {
                    if (!quiet) return false;
                    last = k;
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Add or Opcode.Sub) derived.Add(i.Dest!);
                }
                if (i.Op is Opcode.Call or Opcode.CallIndirect or Opcode.Unwind || i.Op == Opcode.Store && i.Field is not null) quiet = false;
            }
            Liveness liveness = LivenessOf(f);
            HashSet<VReg> pads = PadsOf(f);
            // Written once each: a register written elsewhere as well could
            // carry the value out another way.
            Dictionary<VReg, Instr> defs = Defs(f);
            return derived.All(r => liveness.Tracks(r) && !liveness.IsLiveOut(b, r) && !pads.Contains(r) && defs.ContainsKey(r));
        }

        // Reads of fields: the unit's loads, and calls of its own functions
        // that hand back what a field holds (borrowing, as in OwnedFields).
        // Each direct call with a result, by callee, found once.
        Dictionary<string, List<(Function G, Block B, Instr Call)>> callsTo = new(StringComparer.Ordinal);
        List<(Function G, Block B, Instr Call, string Key)> virtualCalls = new();
        foreach (Function g in m.Functions)
            foreach (Block cb in g.Blocks)
                foreach (Instr call in cb.Instrs)
                {
                    if (call.Dest is null) continue;
                    if (call.Op == Opcode.Call && call.Callee is string direct)
                    {
                        if (!callsTo.TryGetValue(direct, out var list)) callsTo[direct] = list = new();
                        list.Add((g, cb, call));
                    }
                    else if (call.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(call, out string[]? t) && t.Length == 1)
                        virtualCalls.Add((g, cb, call, t[0]));
                }
        List<(Function F, Block B, Instr I, string Field)> reads = loads.Select(l => (l.F, l.B, l.I, l.I.Field!)).ToList();
        foreach ((Function f, Block b, Instr st) in stores) if (forwarded.ContainsKey(st)) reads.Add((f, b, st, st.Field!));
        HashSet<(string Function, string Field)> borrowing = new();
        Dictionary<string, (string Name, long Offset)?>? unitStamps = null;
        static string Kind((string Name, long Offset) stamp) => stamp.Name + "+" + stamp.Offset;
        for (int n = 0; n < reads.Count; n++)
        {
            (Function f, Block b, Instr ld, string field) = reads[n];
            OwnedFieldRecord record = hints.Fields[field];
            if (record.Refused) continue;
            // AS OF ONE TYPE, if not otherwise: what every store here puts in
            // the field (FieldStamps), the link holding the other units to it.
            // Judged so first: a virtual call the type answers here is
            // otherwise a condition on every override in the program.
            ReadJudgement? judged = null;
            string why = "";
            if (ld.Op == Opcode.Load && (unitStamps ??= FieldStamps(stores, Defs)).GetValueOrDefault(field) is { } stamp
                && Judge(f, b, ld, out why, stamp) is ReadJudgement typed)
            {
                judged = typed;
                record.Assumes.Add(Kind(stamp));
            }
            judged ??= Judge(f, b, ld, out why);
            if (judged is null) { Refuse(record, field, why, f, ld); continue; }
            // ANY owned field's store, not only this one's: replacing o.A frees
            // o.A's own owned fields (Runtime.FreeOwnedFields), so a read of
            // o.A.B is endangered by a store into o.A -- as OwnedFields has it.
            if (judged.DangerFields.Count > 0) { Refuse(record, field, "read live across a store into a field", f, ld); continue; }
            Need(record, judged.Needs);
            record.Danger.UnionWith(judged.Danger);
            if (record.Danger.Count > OwnedFieldHints.Limit) { Refuse(record, field, "read live across too many calls", f, ld); record.Danger.Clear(); continue; }
            if (!judged.HandedBack) continue;
            // Handed back: every call of this function reads the field -- the
            // ones here now, the other units' at the link.
            FunctionOf(f.Name).Borrows.Add(field);
            if (borrowing.Add((f.Name, field)) && callsTo.TryGetValue(f.Name, out var calls))
                foreach ((Function g, Block cb, Instr call) in calls) reads.Add((g, cb, call, field));
        }

        // Reads of what calls return, where the callee may hand back what a
        // field holds for all this unit can tell: another unit's function, a
        // virtual call, or a function of this unit that hands back one of
        // those in turn.
        HashSet<string> relaying = new(StringComparer.Ordinal);
        Queue<string> pending = new();
        bool Relayed(string callee) => relaying.Contains(callee)
            || !_defined.Contains(callee) && !IsIntrinsic(callee) && !IsAllocator(callee) && !IsFreeCall(callee);
        HashSet<Instr> judgedCalls = new(ReferenceEqualityComparer.Instance);
        IEnumerable<(Function G, Block B, Instr Call, string Key)> First()
        {
            foreach ((string callee, var list) in callsTo)
                if (Relayed(callee)) foreach (var c in list) yield return (c.G, c.B, c.Call, callee);
            foreach (var c in virtualCalls) yield return c;
        }
        for (bool first = true; first || pending.Count > 0; first = false)
        {
            string? only = first ? null : pending.Dequeue();
            IEnumerable<(Function G, Block B, Instr Call, string Key)> batch = only is null ? First().ToList()
                : (callsTo.GetValueOrDefault(only) ?? new()).Select(c => (c.G, c.B, c.Call, only));
            foreach ((Function g, Block cb, Instr call, string key) in batch)
                    {
                        if (call.Dest!.Type != IrTypes.Word || !judgedCalls.Add(call)) continue;
                        if (!hints.CallReads.TryGetValue(key, out OwnedCallRead? read)) hints.CallReads[key] = read = new();
                        if (read.Refused) continue;
                        if (Judge(g, cb, call, out _) is not ReadJudgement judged
                            || !read.Needs.Add(judged.Needs))
                        {
                            read.Refused = true; read.Danger.Clear(); read.DangerFields.Clear(); read.ReturnedBy.Clear();
                            read.Needs.Stays.Clear(); read.Needs.Fresh.Clear(); read.Needs.Fields.Clear();
                            continue;
                        }
                        read.Danger.UnionWith(judged.Danger); read.DangerFields.UnionWith(judged.DangerFields);
                        if (judged.HandedBack)
                        {
                            read.ReturnedBy.Add(g.Name);
                            if (relaying.Add(g.Name)) pending.Enqueue(g.Name);
                        }
                        if (read.Danger.Count > OwnedFieldHints.Limit || read.DangerFields.Count > OwnedFieldHints.Limit)
                        { read.Refused = true; read.Danger.Clear(); read.DangerFields.Clear(); read.ReturnedBy.Clear(); }
                    }
        }
        // A refused read made its caller relay nothing it knows of; the
        // callers already queued stay (more reads judged, never fewer).

        // ITS ARRAYS' ELEMENTS (ArrayFieldElements), each field this unit
        // has not refused judged over its stores and reads here.
        ArrayFieldElementHints(hints, stores, loads, new HashSet<Instr>(forwarded.Keys, ReferenceEqualityComparer.Instance), LivenessOf, PadsOf, summaries,
            m.RuntimeHelpers.Contains(OwnedElements.ArrayFreer) || m.Functions.Any(g => g.Name == OwnedElements.ArrayFreer));

        foreach ((string name, var record) in functions)
            if (record.Calls.Count + record.Writes.Count + record.InitWrites.Count + record.Borrows.Count > 0)
                hints.Functions[name] = new OwnedFunctionRecord(record.Calls.ToArray(), record.Writes.ToArray(), record.InitWrites.ToArray(), record.Borrows.ToArray());
        foreach (OwnedFieldRecord record in hints.Fields.Values)
        {
            if (record.Refused) { record.Danger.Clear(); record.Sinks.Clear(); record.Needs.Stays.Clear(); record.Needs.Fresh.Clear(); record.Needs.Fields.Clear(); }
            if (record.Refused || record.ElementsRefused)
            {
                record.ElementsRefused = true; record.ElementArrays = false; record.ElementDanger.Clear(); record.ElementDangerFields.Clear();
                record.ElementNeeds.Stays.Clear(); record.ElementNeeds.Fresh.Clear(); record.ElementNeeds.Fields.Clear();
            }
        }
        return hints;
    }
}
