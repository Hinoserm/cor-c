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
        if (_hinting && m.LifetimeHints is not null) m.LifetimeHints.Owned = OwnedFieldHintsOf(m, summaries);
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

        Dictionary<string, List<long>> offsets = new(StringComparer.Ordinal);
        foreach (string field in decided.Mapped.Order(StringComparer.Ordinal))
        {
            int split = field.IndexOf("::", StringComparison.Ordinal);
            if (split <= 0 || !decided.Fields.TryGetValue(field, out long offset)) continue;
            string owner = "t_" + field[..split];
            if (!offsets.TryGetValue(owner, out List<long>? list)) offsets[owner] = list = new();
            if (!list.Contains(offset)) list.Add(offset);
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
            byte[] block = new byte[(1 + bits.Length) * w];
            for (int k = 0; k < w; k++) block[k] = (byte)(words >> (8 * k));
            for (int i = 0; i < bits.Length; i++) for (int k = 0; k < 4; k++) block[(1 + i) * w + k] = (byte)(bits[i] >> (8 * k));
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
        bool reporting = Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 0 };
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
        Dictionary<Function, Dictionary<VReg, List<Instr>>> writesOf = new();

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
        foreach (Function f in m.Functions)
        {
            var record = FunctionOf(f.Name);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is not null) record.Calls.Add(i.Callee);
                    else if (i.Op == Opcode.CallIndirect && _indirect is not null && _indirect.TryGetValue(i, out string[]? targets)) record.Calls.UnionWith(targets);
                    if (i.Field is null || i.Operands.Count < 1 || i.Operands[0] is SymOperand) continue;
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
            if (!writesOf.TryGetValue(f, out Dictionary<VReg, List<Instr>>? writes)) writesOf[f] = writes = Writes(f);
            while (work.Count > 0 && found.Count < 16)
            {
                VReg at = work.Pop();
                if (!seen.Add(at)) continue;
                int param = f.Params.IndexOf(at);
                if (param >= 0) { found.Add(new Source(SourceKind.Parameter, param, null)); continue; }
                if (!writes.TryGetValue(at, out List<Instr>? ws)) { found.Add(new Source(SourceKind.Unknown, 0, null)); continue; }
                foreach (Instr w in ws)
                {
                    if (w.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                    {
                        foreach (Operand o in w.Operands)
                        {
                            if (o is ImmOperand { Value: 0 }) { if (!found.Any(x => x.Kind == SourceKind.Null)) found.Add(new Source(SourceKind.Null, 0, null)); }
                            else if (o is SymOperand) { if (!found.Any(x => x.Kind == SourceKind.Static)) found.Add(new Source(SourceKind.Static, 0, null)); }
                            else if (o is RegOperand from) work.Push(from.Reg);
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
        {
            Liveness live = LivenessOf(f);
            if (derived.Any(r => !live.Tracks(r) || live.IsLiveOut(b, r))) return false;
            for (int after = b.Instrs.IndexOf(st) + 1; after < b.Instrs.Count; after++)
            {
                Instr i = b.Instrs[after];
                if (_bookkeeping.Contains(i)) continue;
                if (i.Operands.Any(o => o is RegOperand r && derived.Contains(r.Reg))) return false;
            }
            return true;
        }

        // THE STORES, each as OwnedFields judges it.
        foreach ((Function f, Block b, Instr st) in stores)
        {
            string field = st.Field!;
            OwnedFieldRecord record = Record(field, st.Offset);
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
                    if (!HandedOver(f, b, st, alone.Derived)) { Refuse(record, field, $"stores {v}, which is used after it is stored", f, st); continue; }
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
            Dictionary<VReg, List<Instr>> writes = writesOf.TryGetValue(f, out var w) ? w : writesOf[f] = Writes(f);
            Stack<VReg> work = new();
            foreach (Block rb in f.Blocks)
                if (rb.Terminator is { Op: Opcode.Ret, Operands: [RegOperand back] }) work.Push(back.Reg);
            while (work.TryPop(out VReg? r))
            {
                if (!chain.Add(r) || !writes.TryGetValue(r, out List<Instr>? ws)) continue;
                foreach (Instr i in ws)
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                        foreach (Operand o in i.Operands) if (o is RegOperand from) work.Push(from.Reg);
            }
            return returnedOf[f] = chain;
        }
        ReadJudgement? Judge(Function f, Block b, Instr ld, out string why)
        {
            why = "";
            // An async body's read is refused only when held across a
            // suspension (the danger walk below), as Escape.OwnedFields does.
            if (ld.Dest is null) { why = "odd read"; return null; }
            if (UsedUpInBlock(f, b, ld)) return new ReadJudgement();
            bool FreesOwnMaking(Block x, int k)
            {
                Instr free = x.Instrs[k];
                if (free.Operands.Count == 0 || free.Operands[0] is not RegOperand freed) return false;
                Instr? made = OriginOf(Defs(f), freed.Reg);
                // AN OWNED SLOT'S FREE -- what the slot held, loaded where the
                // function leaves -- is its record's: the object it made there.
                if (made is { Op: Opcode.Load } && _records.TryGetValue(f, out List<OwnedRecord>? recorded))
                    foreach (OwnedRecord r in recorded)
                        foreach (var own in r.Frees)
                            if (ReferenceEquals(own.Free, free)) { made = r.Origin; goto judged; }
                judged:
                if (made is not { Op: Opcode.Call, Callee: { } callee }) return false;
                if (callee == LeafAllocator) return true;
                return IsFreshCall(made) && _freshOrigins.TryGetValue(callee, out HashSet<Instr>? origins)
                    && origins.Count > 0 && origins.All(o => o.Callee == LeafAllocator);
            }
            HashSet<VReg>? back = f.Name == m.Entry ? null : Returned(f);
            Needs needs = new(this);
            Flow flow = Analyse(f, new[] { ld.Dest }, summaries, ld, returnable: back is { Count: > 0 } ? back : null, needs: needs);
            if (flow.Escapes) { why = $"read escapes via {flow.Why?.Op} {flow.Why?.Callee}"; return null; }
            ReadJudgement judged = new() { HandedBack = back is not null && flow.Derived.Overlaps(back) };
            judged.Needs.Add(needs.Condition);
            Liveness liveness = LivenessOf(f);
            HashSet<VReg> pads = PadsOf(f);
            if (flow.Derived.Any(r => !liveness.Tracks(r) || pads.Contains(r))) { why = "read lives into a handler"; return null; }
            bool Uses(Instr i) => i.Operands.Any(o => o is RegOperand r && flow.Derived.Contains(r.Reg));
            foreach (Block x in f.Blocks)
            {
                bool liveIn = flow.Derived.Any(r => liveness.IsLiveIn(x, r));
                bool liveOut = flow.Derived.Any(r => liveness.IsLiveOut(x, r));
                int from = liveIn ? 0 : ReferenceEquals(x, b) ? b.Instrs.IndexOf(ld) + 1 : -1;
                if (from < 0)
                {
                    int copied = x.Instrs.FindIndex(i => i.Dest is not null && flow.Derived.Contains(i.Dest));
                    if (copied < 0) continue;
                    from = copied + 1;
                }
                int to = x.Instrs.Count;
                if (!liveOut)
                {
                    to = from;
                    for (int k = from; k < x.Instrs.Count; k++) if (Uses(x.Instrs[k])) to = k;
                }
                for (int k = from; k < to; k++)
                {
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
                    else if (i.Op == Opcode.Store && i.Field is not null) judged.DangerFields.Add(i.Field);
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
        HashSet<(string Function, string Field)> borrowing = new();
        for (int n = 0; n < reads.Count; n++)
        {
            (Function f, Block b, Instr ld, string field) = reads[n];
            OwnedFieldRecord record = hints.Fields[field];
            if (record.Refused) continue;
            if (Judge(f, b, ld, out string why) is not ReadJudgement judged) { Refuse(record, field, why, f, ld); continue; }
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

        foreach ((string name, var record) in functions)
            if (record.Calls.Count + record.Writes.Count + record.InitWrites.Count + record.Borrows.Count > 0)
                hints.Functions[name] = new OwnedFunctionRecord(record.Calls.ToArray(), record.Writes.ToArray(), record.InitWrites.ToArray(), record.Borrows.ToArray());
        foreach (OwnedFieldRecord record in hints.Fields.Values)
            if (record.Refused) { record.Danger.Clear(); record.Sinks.Clear(); record.Needs.Stays.Clear(); record.Needs.Fresh.Clear(); record.Needs.Fields.Clear(); }
        return hints;
    }
}
