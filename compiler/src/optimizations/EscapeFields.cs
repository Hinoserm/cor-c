#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// OWNED FIELDS: what an object owned by the compiler holds in its reference
/// fields dies with it, when that is provable.
///
/// Escape analysis frees an object whose whole life it can see (a frame slot,
/// an owned allocation, a fresh function's result). What such an object holds
/// in its fields used to stay behind for the collector however short-lived
/// the owner: a List made, filled and dropped in one call left its array --
/// most of its memory -- as garbage, because the array was made inside
/// List.Grow and stored in a field there, which is an escape as far as Grow
/// is concerned.
///
/// A reference field of an owned object is freed together with the object
/// when every piece of code that touches that field, in the owner's function
/// and in every callee the object is handed to, keeps to three rules:
///
///   1. What it stores there is a FRESH object -- an allocation, or a fresh
///      function's result -- made for the field and let go no other way;
///      or null; or what was just loaded from the same field (put back).
///      Anything else (a shared array, a parameter, a number) makes the
///      field DIRTY, and a dirty field is never freed: freeing it could
///      free something somebody else holds.
///   2. What it loads from there does not escape: it is read, written into,
///      handed to callees that keep nothing, compared -- never stored
///      elsewhere, returned, or handed to a callee that keeps it.
///   3. The object itself is used only through constant field offsets:
///      handed to a callee at its base, read, written. An interior pointer
///      handed away, a copy of the object's bytes, an element index -- the
///      object becomes OPAQUE and none of its fields is freed.
///
/// Each function is summarised, bottom-up, for every parameter that does not
/// escape (what it does to that object's fields: ParameterFields) and, when
/// it returns a fresh object, for what it has left in that object's fields
/// (FreshFields). The owner's function merges the summaries of the callees
/// its object reaches with its own uses, and a field that some code filled
/// with fresh objects (rule 1) and none made dirty is CLEAN: at each point
/// where the owner dies, the field's current object is freed first
/// (Runtime.FreeField), provided no reference loaded from the field is still
/// in use there.
///
/// What a field held before it was overwritten -- the smaller array a List
/// grew out of -- is not freed: nothing here proves it is not still read.
/// The collector takes it, as before.
/// </summary>
public sealed partial class Escape
{
    /// <summary>Runtime.FreeField(long at, long offset): the field's object given back, before the owner's.</summary>
    public const string FieldFreer = LifetimeHints.FieldFreer;

    /// <summary>Runtime.KeepField(long at, long offset): nothing. What a field site becomes when the link finds the field is not clean.</summary>
    public const string FieldKeeper = LifetimeHints.FieldKeeper;

    /// <summary>
    /// The collector told of a reference: the write barrier, whole or inlined
    /// (Gc.MarkAt, Gc.Report). It keeps no pointer the program can use, and a
    /// block given back is refused when its turn comes to be marked.
    /// </summary>
    internal static bool IsFreeCall(string? callee) =>
        callee is Freer or ManualFreer or CollectorFreer or ReplacedFreer or FieldFreer or FieldKeeper or OwnedReplacedFreer or OwnedElements.Freer or OwnedElements.ArrayFreer or StorageFreer
        || (callee is not null && callee.StartsWith(FieldSitePrefix, StringComparison.Ordinal));

    /// <summary>
    /// The frees this thread's run inserted itself, which own nothing yet;
    /// every other free call an analysis meets was there before the run.
    /// </summary>
    [ThreadStatic] private static HashSet<Instr>? _inserted;

    internal static bool IsCollectorNote(string? callee) =>
        callee is "m_Gc_MarkAt_1_V$I64" or "m_Gc_Report_1_V$I64"
            or Corsac.Lang.Lto.RuntimeAbi.WriteBarrier or "m_Gc_Barrier_2_V$I64_V$I64"
            or Corsac.Lang.Lto.RuntimeAbi.WriteBarrierValues or "m_Gc_BarrierValues_2_V$I64_V$I64"
            or CardMarks.CardMark
            // The same notes as CardMarks writes them out, last of the late
            // passes: the link runs its lifetime pass after them (RunAtLink),
            // and took every object a field store was barriered for as gone.
            or "__x86.i.barrier" or "__x86.i.cardmark"
            // An array grown where it is (Runtime.GrowInPlace): its length and
            // its new zeroed elements written, nothing kept.
            or Corsac.Lang.Lto.RuntimeAbi.GrowInPlace
            // The mark on a collection whose elements go with its storage
            // (OwnedElements.Marker), a flag set and nothing kept: placed by
            // the unit's late passes, it is a call the link's lifetime pass
            // reads after them, and took the collection for gone -- a
            // parser's tokens, stored into the parser, then no field of the
            // parser freed with it.
            or OwnedElements.Marker;

    /// <summary>
    /// The notes the barrier is built from, which are never inlined. Whether
    /// Gc.Report fit the inliner's budget used to depend on the word size.
    /// </summary>
    internal static bool IsCollectorLeaf(string? callee) =>
        callee is "m_Gc_MarkAt_1_V$I64" or "m_Gc_Report_1_V$I64" or CardMarks.CardMark;

    /// <summary>How many fields this pass arranged to free with their owner.</summary>
    public int FieldsOwned { get; private set; }

    /// <summary>What a piece of code does to the fields of one object.</summary>
    internal sealed class FieldSummary
    {
        /// <summary>Used some way the field rules cannot follow: no field is freed.</summary>
        public bool Opaque;
        /// <summary>The first use that made it opaque or a field dirty (--trace-fields).</summary>
        public string? Why;
        /// <summary>The first reason each offset went dirty (--trace-fields).</summary>
        public Dictionary<long, string>? DirtyWhy;
        public void Note(long at, string why)
        {
            if (FieldTrace is null) return;
            DirtyWhy ??= new();
            DirtyWhy.TryAdd(at, why);
        }
        /// <summary>Word offsets something other than a fresh object was stored at, or whose loaded object escaped.</summary>
        public readonly HashSet<long> Dirty = new();
        /// <summary>Word offsets a fresh object was stored at.</summary>
        public readonly HashSet<long> FreshStored = new();
        /// <summary>The owner's own loads of each field, for the liveness check where it dies.</summary>
        public readonly List<(long Offset, VReg Value)> Loads = new();
        /// <summary>
        /// The fresh objects the owner's own function stored in each field,
        /// for the same check: one made once and stored in every owner a loop
        /// makes -- the list a foreach's enumerator holds -- outlives each of
        /// them, and freeing it with the first freed it under the rest.
        /// </summary>
        public readonly List<(long Offset, VReg Value)> Stored = new();

        public void Merge(FieldSummary? other)
        {
            if (other is null) { Opaque = true; Why ??= "a callee with no field summary"; return; }
            if (other.Opaque || other.Dirty.Count > 0) Why ??= other.Why;
            if (other.DirtyWhy is not null) foreach (var kv in other.DirtyWhy) Note(kv.Key, kv.Value);
            Opaque |= other.Opaque;
            Dirty.UnionWith(other.Dirty);
            FreshStored.UnionWith(other.FreshStored);
        }

        public IEnumerable<long> Clean() => Opaque ? Enumerable.Empty<long>() : FreshStored.Where(o => !Dirty.Contains(o));

        /// <summary>
        /// THE OVERRIDES A VIRTUAL CALL CAN REACH ARE ALTERNATIVES, each of its
        /// own class: what the receiver, or the object the call hands back, is
        /// one of them. An offset one fills with a fresh object is a field of
        /// that class; in another the same offset is another field, a number,
        /// or past the object's end. Merged, every offset some alternative
        /// filled is fresh -- freed then on an object of a class without that
        /// field. Fresh in all of them, it is a reference field of every class
        /// the object can be; fresh in some, it is made dirty here.
        /// </summary>
        public void DirtyPartial(List<HashSet<long>> alternatives, string where)
        {
            if (alternatives.Count < 2) return;
            HashSet<long> every = new(alternatives[0]);
            foreach (HashSet<long> one in alternatives) every.IntersectWith(one);
            foreach (HashSet<long> one in alternatives)
                foreach (long at in one)
                    if (!every.Contains(at) && Dirty.Add(at)) Note(at, $"fresh in only some overrides of {where}");
        }
    }

    /// <summary>The offsets a field hint fills with a fresh object, unconditionally or if its condition holds.</summary>
    private static IEnumerable<long> PartialOffsets(List<HashSet<long>> alternatives)
    {
        if (alternatives.Count < 2) return Enumerable.Empty<long>();
        HashSet<long> every = new(alternatives[0]);
        foreach (HashSet<long> one in alternatives) every.IntersectWith(one);
        return alternatives.SelectMany(one => one).Where(at => !every.Contains(at)).Distinct().ToList();
    }

    private static HashSet<long> HintFresh(LifetimeFields hint)
        => hint.Fresh.Concat(hint.Conditional.Where(c => c.Stores).Select(c => c.Offset)).ToHashSet();

    /// <summary>Per function, per parameter: its field summary; null for a parameter that escapes or is not a reference.</summary>
    private Dictionary<string, FieldSummary?[]> _paramFields = new(StringComparer.Ordinal);

    /// <summary>Per fresh function: what it left in the fields of the object it hands over.</summary>
    private Dictionary<string, FieldSummary> _freshFields = new(StringComparer.Ordinal);

    /// <summary>Instructions this pass inserted to track owned objects: not uses of the objects.</summary>
    private readonly HashSet<Instr> _bookkeeping = new(ReferenceEqualityComparer.Instance);

    /// <summary>An object this pass took ownership of, and where it dies.</summary>
    private sealed class OwnedRecord
    {
        /// <summary>What made it: the allocation, the fresh call, or (for a frame slot) the zeroing that replaced the allocation.</summary>
        public required Instr Origin { get; init; }
        public required VReg Root { get; init; }
        /// <summary>A frame-slot object: the register holding the slot's address, and the slot.</summary>
        public VReg? SlotAddress { get; init; }
        public FrameSlot? Slot { get; init; }
        /// <summary>Where the previous object from the same site dies: before this instruction, or after it (RenewAfter).</summary>
        public Instr? Renew { get; init; }
        public bool RenewAfter { get; init; }
        /// <summary>The frees of an owned allocation or fresh result: the call, and the register with the pointer it frees.</summary>
        public List<(Block Block, Instr Free, VReg Pointer)> Frees { get; } = new();
        public long Bytes { get; init; } = -1;
        public string? FreshCallee { get; set; }
    }

    private readonly Dictionary<Function, List<OwnedRecord>> _records = new();

    /// <summary>What made the object an owned slot's free gives back, when `free` is one.</summary>
    private Instr? RecordedOrigin(Function f, Instr free)
    {
        if (!_records.TryGetValue(f, out List<OwnedRecord>? recorded)) return null;
        foreach (OwnedRecord r in recorded)
            foreach (var own in r.Frees)
                if (ReferenceEquals(own.Free, free)) return r.Origin;
        return null;
    }

    private void Record(Function f, OwnedRecord r)
    {
        if (!_records.TryGetValue(f, out List<OwnedRecord>? list)) _records[f] = list = new();
        list.Add(r);
    }

    // ---- the summaries -------------------------------------------------------------------

    private FieldSummary?[] ParameterFields(Function f, Dictionary<string, bool[]> summaries)
    {
        FieldSummary?[] result = new FieldSummary?[f.Params.Count];
        LifetimeFields?[] hints = new LifetimeFields?[f.Params.Count];
        bool[] escapes = summaries[f.Name];
        // AN ITERATOR'S BODY IS SUMMARISED: its one parameter is the machine,
        // which owns no field (StampedBox), and what it keeps across a yield
        // goes into that machine and no further -- the iterator a LINQ
        // operator hands back holds its source, read here and walked, and a
        // caller holding the source in the machine asks what the body does to
        // that word. An async method's body still is not.
        if (f.Async is not null && !IteratorBody(f)) return result;
        _paramHints.TryGetValue(f.Name, out LifetimeCondition?[]? stays);
        for (int p = 0; p < f.Params.Count; p++)
        {
            VReg param = f.Params[p];
            if (param.Type is not (IrType.I32 or IrType.I64)) continue;
            // Handed back, or copied word for word (Copies): what it does to
            // the argument's fields was found then; its callers follow what
            // comes back as the argument's own words.
            if (p == 0 && _copyFields.TryGetValue(f.Name, out FieldSummary? copied)) { result[p] = copied; continue; }
            // The unit's own summary where the parameter stays here; the
            // link's wherever it may stay once other units are known.
            bool here = !(p < escapes.Length && escapes[p]);
            bool link = _hinting && stays is not null && p < stays.Length && stays[p] is not null;
            if (!here && !link) continue;
            LifetimeFields? hint = link ? new() : null;
            FieldSummary fields = FieldUses(f, new[] { param }, summaries, null, null, hint);
            if (here) result[p] = fields;
            hints[p] = hint;
        }
        if (_hinting) _fieldHints[f.Name] = hints;
        return result;
    }

    private FieldSummary FreshFields(Function f, Dictionary<string, bool[]> summaries, List<Instr> origins, HashSet<VReg> chain,
        LifetimeFields? hint = null)
    {
        FieldSummary merged = new();
        // A returned register joined from several paths is not followed by
        // the address walk, which wants a single definition: uses of it would
        // go unseen. Only a straight chain is summarised.
        Defs defs = new(f, buildCfg: false);
        foreach (VReg r in chain)
        {
            if (!defs.IsSingle(r) && !f.Params.Contains(r) || defs.Definition(r) is { Op: Opcode.Phi })
            {
                merged.Opaque = true;
                if (hint is not null) hint.Opaque = true;
                return merged;
            }
        }
        foreach (Instr origin in origins)
        {
            LifetimeFields? oneHint = hint is null ? null : new();
            FieldSummary one = FieldUses(f, new[] { origin.Dest! }, summaries, origin, chain, oneHint);
            if (origin.Callee is not null && !IsAllocator(origin.Callee))
            {
                one.Merge(_freshFields.GetValueOrDefault(origin.Callee));
                // What the maker left in it: another unit's, by its fresh
                // return (argument -1); this unit's, by its own hint.
                if (oneHint is not null)
                {
                    if (!_defined.Contains(origin.Callee)) oneHint.Merges.Add((origin.Callee, -1));
                    else if (_freshFieldHints.GetValueOrDefault(origin.Callee) is LifetimeFields made) oneHint.Absorb(made);
                    else oneHint.Opaque = true;
                }
            }
            // A virtual call's: what each override it reaches left in it,
            // together -- whichever ran. A unit's one symbol for them all
            // is another unit's function here, merged at the link.
            else if (origin.Op == Opcode.CallIndirect)
            {
                string[] targets = _indirect is not null && _indirect.TryGetValue(origin, out string[]? t) ? t : Array.Empty<string>();
                if (targets.Length == 0) { one.Opaque = true; if (oneHint is not null) oneHint.Opaque = true; }
                List<HashSet<long>> alternatives = new(), hinted = new();
                bool linked = false;
                foreach (string target in targets)
                {
                    FieldSummary? made = _freshFields.GetValueOrDefault(target);
                    one.Merge(made);
                    if (made is not null) alternatives.Add(made.FreshStored);
                    if (oneHint is null) continue;
                    if (!_defined.Contains(target)) { oneHint.Merges.Add((target, -1)); linked = true; }
                    else if (_freshFieldHints.GetValueOrDefault(target) is LifetimeFields madeHint) { oneHint.Absorb(madeHint); hinted.Add(HintFresh(madeHint)); }
                    else oneHint.Opaque = true;
                }
                if (targets.Length > 1)
                {
                    one.DirtyPartial(alternatives, origin.ToString());
                    if (oneHint is not null)
                    {
                        if (linked) oneHint.Opaque = true;
                        else foreach (long at in PartialOffsets(hinted)) oneHint.Dirty.Add(at);
                    }
                }
            }
            merged.Merge(one);
            if (oneHint is not null) hint!.Absorb(oneHint);
        }
        if (hint is not null && !hint.Bounded)
        {
            hint.Dirty.Clear(); hint.Fresh.Clear(); hint.Conditional.Clear(); hint.Merges.Clear();
            hint.Opaque = true;
        }
        return merged;
    }

    // ---- one object's fields ---------------------------------------------------------------

    /// <summary>
    /// What `f` does to the fields of the object whose base the roots hold.
    /// `source` (the instruction that made it) and this pass's own bookkeeping
    /// are not uses; a return of a register in `returnable` hands the object
    /// over and is allowed.
    ///
    /// With `hint`, the same question is also asked for the link (EscapeHints):
    /// the object handed to another unit's function merges that function's
    /// summary in, a child stored there from another unit's function is fresh
    /// if that function is, and a loaded child handed to one stays clean if it
    /// keeps nothing. The summary returned is the unit's own, pessimistic
    /// answer either way.
    /// </summary>
    internal FieldSummary FieldUses(Function f, IEnumerable<VReg> roots, Dictionary<string, bool[]> summaries,
        Instr? source, HashSet<VReg>? returnable, LifetimeFields? hint = null, bool framed = false, Stamp[]? kinds = null)
    {
        // ONE SCAN OF THE FUNCTION FOR THE WHOLE QUESTION (OwnedFieldEscape.
        // Scan): the question changes nothing, and outside PromoteIn's own
        // scan each of its address questions -- up to four -- scanned the
        // function again, and its definitions were found once more beside.
        // Each record OwnFields asks about, and each parameter ParameterFields
        // does, was that many walks of the function and their tables.
        var scope = OwnedFieldEscape.Scan(f);
        try { return FieldUsesCore(f, roots, summaries, source, returnable, hint, framed, kinds); }
        finally { OwnedFieldEscape.EndScan(scope); }
    }

    private FieldSummary FieldUsesCore(Function f, IEnumerable<VReg> roots, Dictionary<string, bool[]> summaries,
        Instr? source, HashSet<VReg>? returnable, LifetimeFields? hint, bool framed, Stamp[]? kinds)
    {
        FieldSummary fs = new();
        int word = IrTypes.Word.Bytes();
        Dictionary<VReg, long> addresses = OwnedFieldEscape.Addresses(f, roots);
        Defs defs = OwnedFieldEscape.DefsOf(f);
        List<(long At, VReg Value, Instr Load)> loads = new();
        List<(long At, Instr Store)> stores = new();

        Instr? current = null;
        void Opaque()
        {
            if (!fs.Opaque && FieldTrace is { } opaqueTraced && f.Name.Contains(opaqueTraced, StringComparison.Ordinal))
                Console.Error.WriteLine($"field trace: {f.Name} opaque at {current}{(kinds is null ? "" : " as " + string.Join(",", kinds.Select(k => k.Descriptor)))}");
            fs.Opaque = true;
            fs.Why ??= $"opaque: {current} in {f.Name}";
            if (hint is not null) hint.Opaque = true;
        }
        void DirtyRange(long at, long bytes)
        {
            fs.Why ??= $"dirty +{at}: {current} in {f.Name}";
            for (long o = at - ((at % word) + word) % word; o < at + bytes; o += word) fs.Note(o, $"{current} in {f.Name}");
            long first = at - ((at % word) + word) % word;
            for (long o = first; o < at + bytes; o += word)
            {
                fs.Dirty.Add(o);
                hint?.Dirty.Add(o);
            }
        }
        // Past the first use the rules cannot follow, nothing more is learned;
        // with a hint, only a use that is opaque whatever other units do.
        bool Done() => hint is null ? fs.Opaque : hint.Opaque;

        // AN OBJECT OF THE FRAME HELD BY ANOTHER: what is read back out of the
        // holder is the object again, and its uses are the object's. Known
        // only while every use of the holder is here.
        if (framed)
        {
            (Dictionary<VReg, long>? aliases, Instr? why) = FrameHeldAliases(f, roots, addresses, defs);
            if (aliases is null) { current = why; Opaque(); return fs; }
            addresses = aliases;
        }

        // WHAT A CALL HANDS BACK AS THE OBJECT OR A COPY OF ITS WORDS (Copies):
        // an iterator's GetEnumerator. Its words are the object's, and what
        // is read out of it is read as the object's own is; what is stored
        // into it may not be stored into the object, and is taken as dirty.
        // With the object's types known (kinds), a copy's types join them.
        HashSet<VReg>? throughCopies = null;
        if (!framed)
        {
            List<VReg>? copies = null;
            for (int round = 0; round < 4; round++)
            {
                List<VReg>? more = null;
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                    {
                        if (i.Dest is not { } d || addresses.ContainsKey(d) || i.Op is not (Opcode.Call or Opcode.CallIndirect)) continue;
                        int receiver = i.Op == Opcode.Call ? 0 : 1;
                        if (i.Operands.Count <= receiver || i.Operands[receiver] is not RegOperand { Reg: var r }
                            || !addresses.TryGetValue(r, out long at) || at != 0) continue;
                        if (CallTargets(defs, i, addresses, kinds) is not { } targets || !targets.Any(t => Copies(t) is not null)) continue;
                        (more ??= new()).Add(d);
                        if (kinds is not null)
                            kinds = kinds.Concat(targets.SelectMany(t => Copies(t) ?? Array.Empty<Stamp>())).Distinct().ToArray();
                    }
                if (more is null) break;
                (copies ??= new()).AddRange(more);
                addresses = OwnedFieldEscape.Addresses(f, roots.Concat(copies));
            }
            if (copies is not null) throughCopies = OwnedFieldEscape.Addresses(f, copies).Keys.ToHashSet();
        }
        // Of the object's types known: what none of them reaches.
        HashSet<Block>? dead = kinds is not null ? DeadUnder(f, defs, addresses, kinds) : null;

        // What `callee` does to the fields of its parameter `p`: its summary,
        // or, with the object's types known, its summary for those types
        // (TypedFields) -- the virtual calls on it each type's own method.
        (FieldSummary? Known, LifetimeFields? Hinted) MergeCallee(string callee, int p, Stamp[]? typed)
        {
            FieldSummary? known = null;
            bool found = false;
            if (typed is not null && Copies(callee) is null && TypedFields(callee, p, typed) is { } specific) { known = specific; found = true; }
            else if (_paramFields.TryGetValue(callee, out FieldSummary?[]? all) && p < all.Length) { known = all[p]; found = true; }
            if (found)
            {
                if (!fs.Opaque && known is not { Opaque: false } && FieldTrace is { } mergeTraced && f.Name.Contains(mergeTraced, StringComparison.Ordinal))
                    Console.Error.WriteLine($"field trace: {f.Name} opaque from {callee}:{p} at {current}{(typed is null ? "" : " typed")}: {known?.Why ?? "no summary"}");
                fs.Merge(known);
                if (known is null) fs.Why = fs.Why == "a callee with no field summary" ? $"{callee} param {p} has no field summary (it escapes)" : fs.Why;
            }
            else { fs.Opaque = true; fs.Why ??= $"{callee} unknown here (param {p})"; }
            if (hint is null) return (known, null);
            // For the link: another unit's function is merged in
            // there; one of this unit's brings its own hint.
            if (!_defined.Contains(callee)) { hint.Merges.Add((callee, p)); return (known, null); }
            if (_fieldHints.TryGetValue(callee, out LifetimeFields?[]? calleeHints) && p < calleeHints.Length
                && calleeHints[p] is LifetimeFields hinted)
            {
                hint.Absorb(hinted);
                return (known, hinted);
            }
            hint.Opaque = true;
            return (known, null);
        }

        foreach (Block b in f.Blocks)
        {
            if (dead is not null && dead.Contains(b)) continue;
            foreach (Instr i in b.Instrs)
            {
                if (ReferenceEquals(i, source) || _bookkeeping.Contains(i)) continue;
                current = i;
                bool uses = false;
                foreach (Operand uOperand in (i).Operands) if (uOperand is RegOperand { Reg: var u }) if (addresses.ContainsKey(u)) { uses = true; break; }
                if (!uses) continue;
                // An address of the object derived by a constant: followed already.
                if (i.Dest is not null && addresses.ContainsKey(i.Dest)
                    && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Add or Opcode.Sub) continue;

                switch (i.Op)
                {
                    case Opcode.Load:
                    {
                        if (i.Operands[0] is not RegOperand baseReg || !addresses.TryGetValue(baseReg.Reg, out long off))
                        { Opaque(); break; }
                        long at = off + i.Offset;
                        if (i.Size == word && ((at % word) + word) % word == 0 && i.Dest is not null && defs.IsSingle(i.Dest))
                            loads.Add((at, i.Dest, i));
                        else if (i.Size >= word)
                            // A pointer read into a register that is written
                            // elsewhere too, or at another width: where it
                            // goes cannot be followed.
                            DirtyRange(at, i.Size);
                        break;
                    }
                    case Opcode.Store:
                    {
                        if (i.Operands.Count < 2) { Opaque(); break; }
                        // The object's own address stored anywhere -- in itself included.
                        // EXCEPT, for an object the frame holds, into another
                        // object of the frame: a Defs kept in the frame holding
                        // the Cfg it was made over. Neither outlives the frame
                        // (they were put there for that), and every use of the
                        // holder is here (FrameHeldAliases): what is read back
                        // out of it is this object, its uses followed as this
                        // object's own.
                        if (i.Operands[1] is RegOperand v0 && addresses.ContainsKey(v0.Reg))
                        {
                            if (framed && !(i.Operands[0] is RegOperand into && addresses.ContainsKey(into.Reg)) && FrameMemory(i.Operands[0], defs)) break;
                            Opaque(); break;
                        }
                        if (i.Operands[0] is not RegOperand baseReg || !addresses.TryGetValue(baseReg.Reg, out long off))
                        { Opaque(); break; }
                        long at = off + i.Offset;
                        if (throughCopies is not null && throughCopies.Contains(baseReg.Reg)) DirtyRange(at, i.Size);
                        else if (i.Size == word && ((at % word) + word) % word == 0) stores.Add((at, i));
                        else DirtyRange(at, i.Size);
                        break;
                    }
                    case Opcode.MemSet:
                    {
                        // Zeroing: nulls, which any field may hold.
                        bool zero = i.Operands.Count >= 2 && i.Operands[1] is ImmOperand { Value: 0 };
                        bool baseOnly = i.Operands.Count >= 1 && i.Operands[0] is RegOperand t && addresses.ContainsKey(t.Reg);
                        for (int k = 1; k < i.Operands.Count; k++)
                            if (i.Operands[k] is RegOperand r && addresses.ContainsKey(r.Reg)) baseOnly = false;
                        if (!zero || !baseOnly) Opaque();
                        break;
                    }
                    case Opcode.Call:
                    {
                        if (IsCollectorNote(i.Callee)) break;
                        // A free already there (the link reruns this pass over
                        // a unit's frees): the object is someone's to give
                        // back, and freeing its fields again frees twice.
                        if (IsFreeCall(i.Callee)) { Opaque(); break; }
                        for (int a = 0; a < i.Operands.Count; a++)
                        {
                            if (i.Operands[a] is not RegOperand arg || !addresses.TryGetValue(arg.Reg, out long off)) continue;
                            if (off != 0 || i.Callee is null) { Opaque(); break; }
                            // A copy of its words handed back and not followed
                            // (a frame-held object's): read where nothing sees.
                            if (a == 0 && Copies(i.Callee) is not null && i.Dest is { } handedBack && !addresses.ContainsKey(handedBack)) { Opaque(); break; }
                            MergeCallee(i.Callee, a, kinds);
                        }
                        break;
                    }
                    case Opcode.CallIndirect when i.DispatchType == Devirtualize.NoTarget:
                        // No object made here has the method: never runs.
                        break;
                    case Opcode.CallIndirect:
                    {
                        // A VIRTUAL CALL is every override it reaches, each
                        // summary merged -- or, on the object itself as the
                        // receiver with its types known, each type's method.
                        if (i.Operands.Count == 0 || i.Operands[0] is RegOperand { Reg: var through } && addresses.ContainsKey(through)
                            || CallTargets(defs, i, addresses, kinds) is not { } targets) { Opaque(); break; }
                        for (int a = 1; a < i.Operands.Count && !Done(); a++)
                        {
                            if (i.Operands[a] is not RegOperand arg || !addresses.TryGetValue(arg.Reg, out long off)) continue;
                            if (off != 0) { Opaque(); break; }
                            if (a == 1 && targets.Any(t => Copies(t) is not null) && i.Dest is { } handedBack && !addresses.ContainsKey(handedBack)) { Opaque(); break; }
                            List<HashSet<long>> alternatives = new(), hinted = new();
                            bool linked = false;
                            foreach (string t in targets)
                            {
                                (FieldSummary? known, LifetimeFields? one) = MergeCallee(t, a - 1, a == 1 ? kinds : null);
                                if (known is not null) alternatives.Add(known.FreshStored);
                                if (one is not null) hinted.Add(HintFresh(one));
                                else linked |= !_defined.Contains(t);
                            }
                            if (a != 1 || targets.Length < 2) continue;
                            fs.DirtyPartial(alternatives, i.ToString());
                            // Another unit's override is merged at the link, by
                            // its own name, among the rest: no one there sees them
                            // as alternatives.
                            if (hint is null) continue;
                            if (linked) hint.Opaque = true;
                            else foreach (long at in PartialOffsets(hinted)) hint.Dirty.Add(at);
                        }
                        break;
                    }
                    case Opcode.Ret:
                        if (returnable is null || i.Operands.Count != 1 || i.Operands[0] is not RegOperand back
                            || !returnable.Contains(back.Reg) || addresses[back.Reg] != 0)
                            Opaque();
                        break;
                    case Opcode.Branch:
                    case Opcode.Switch:
                    // An array's count read: a number, no field.
                    case Opcode.ArrayLength:
                        break;
                    default:
                        if (!IrInfo.IsIntCompare(i.Op)) Opaque();
                        break;
                }
                if (Done()) return fs;
            }
        }

        // What went into each field (rule 1).
        // Counted apart: what the unit decides must not move with what it
        // only tells the link.
        Dictionary<Instr, int> storedOrigins = new(ReferenceEqualityComparer.Instance);
        Dictionary<Instr, int> hintOrigins = new(ReferenceEqualityComparer.Instance);
        List<(long At, Instr Origin, LifetimeCondition? Condition)> fresh = new();
        foreach ((long at, Instr st) in stores)
        {
            Operand value = st.Operands[1];
            if (value is ImmOperand { Value: 0 }) continue;
            if (value is not RegOperand vr) { fs.Dirty.Add(at); hint?.Dirty.Add(at); continue; }
            // Put back what was just taken from the same field.
            if (loads.Any(l => l.At == at && ReferenceEquals(l.Value, vr.Reg))) continue;
            if (FrameObject(defs, vr.Reg)) continue;
            HashSet<Instr> putHere = new(ReferenceEqualityComparer.Instance) { st };
            Instr? origin = FreshOrigin(f, defs, vr.Reg);
            if (origin is not null && !ReferenceEquals(origin, source) && !_owned.Contains(origin) && !_ownedCalls.Contains(origin))
            {
                Flow flow = Analyse(f, new[] { origin.Dest! }, summaries, origin, putHere);
                if (!flow.Escapes)
                {
                    storedOrigins[origin] = storedOrigins.GetValueOrDefault(origin) + 1;
                    hintOrigins[origin] = hintOrigins.GetValueOrDefault(origin) + 1;
                    fresh.Add((at, origin, null));
                    continue;
                }
            }
            fs.Dirty.Add(at);
            fs.Why ??= $"dirty +{at}: stored {vr.Reg} is not a fresh object only this field keeps ({st}) in {f.Name}";
            fs.Note(at, $"stored {vr.Reg} not fresh-and-only-here: {st} in {f.Name}");
            if (hint is null) continue;
            // For the link: a child made by another unit's function, or let go
            // only through calls to one, is fresh if the condition holds.
            Instr? made = origin ?? CallOrigin(defs, vr.Reg);
            if (made is null || ReferenceEquals(made, source) || _owned.Contains(made) || _ownedCalls.Contains(made))
            { hint.Dirty.Add(at); continue; }
            Needs needs = new(this);
            if (made.Callee is not null && !IsAllocator(made.Callee) && !IsFreshCall(made) && !needs.AllowFresh(made.Callee))
            { hint.Dirty.Add(at); continue; }
            if (Analyse(f, new[] { made.Dest! }, summaries, made, putHere, needs: needs).Escapes) { hint.Dirty.Add(at); continue; }
            hintOrigins[made] = hintOrigins.GetValueOrDefault(made) + 1;
            fresh.Add((at, made, needs.Condition));
        }
        foreach ((long at, Instr origin, LifetimeCondition? condition) in fresh)
        {
            fs.Stored.Add((at, origin.Dest!));
            // One object in two fields would be freed twice.
            if (condition is null)
            {
                if (storedOrigins[origin] > 1) { fs.Dirty.Add(at); fs.Note(at, $"one object stored twice: {origin} in {f.Name}"); }
                else fs.FreshStored.Add(at);
            }
            if (hint is null) continue;
            if (hintOrigins[origin] > 1) hint.Dirty.Add(at);
            else if (condition is null || condition.IsTrue) hint.Fresh.Add(at);
            else hint.Conditional.Add((at, condition, true));
        }

        // WHAT A FIELD HELD, GIVEN BACK ONCE THE FIELD HOLDS SOMETHING ELSE: a
        // free in the block of a store to the same field, after it. That is
        // the owned field's own discipline -- the collections free the array
        // their Grow replaced (OutgrownStorage) -- and not an escape: counted
        // as one, it made every growing table's fields dirty, and the arrays
        // of a table that died were never freed with it.
        // The store comes first: before the free in its block, or in a block
        // that dominates the free's (the free is behind a null test).
        Dictionary<long, HashSet<Instr>>? replacedFrees = null;
        if (stores.Count > 0)
        {
            Dictionary<Instr, Block> storeBlock = new(ReferenceEqualityComparer.Instance);
            Dictionary<Instr, int> storeIndex = new(ReferenceEqualityComparer.Instance);
            foreach (Block b in f.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                    if (b.Instrs[k].Op == Opcode.Store) { storeBlock[b.Instrs[k]] = b; storeIndex[b.Instrs[k]] = k; }
            Cfg? cfg = null;
            foreach (Block b in f.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    Instr i = b.Instrs[k];
                    if (i.Op != Opcode.Call || !IsFreeCall(i.Callee) || _bookkeeping.Contains(i)) continue;
                    foreach ((long sat, Instr st) in stores)
                    {
                        if (!storeBlock.TryGetValue(st, out Block? sb)) continue;
                        bool first = ReferenceEquals(sb, b) ? storeIndex[st] < k : (cfg ??= new Cfg(f)).Dominates(sb, b);
                        if (!first) continue;
                        replacedFrees ??= new();
                        if (!replacedFrees.TryGetValue(sat, out HashSet<Instr>? set)) replacedFrees[sat] = set = new(ReferenceEqualityComparer.Instance);
                        set.Add(i);
                    }
                }
        }

        // What came out of each field (rule 2).
        foreach ((long at, VReg v, Instr load) in loads)
        {
            fs.Loads.Add((at, v));
            bool unitDirty = fs.Dirty.Contains(at), hintDirty = hint is null || hint.Dirty.Contains(at);
            if (unitDirty && hintDirty) continue;
            HashSetOfStores putBack = new();
            foreach ((long sat, Instr st) in stores)
                if (sat == at && st.Operands[1] is RegOperand sv && ReferenceEquals(sv.Reg, v)) putBack.Set.Add(st);
            // Only where the store before the free put something else there.
            HashSet<Instr>? released = replacedFrees?.GetValueOrDefault(at) is { } frees && putBack.Set.Count == 0 ? frees : null;
            Needs? needs = hintDirty ? null : new(this);
            Flow flow = Analyse(f, new[] { v }, summaries, load, putBack.Set, needs: needs, consumers: released);
            bool escapes = flow.Escapes || needs is { Condition.IsTrue: false };
            if (escapes) { fs.Dirty.Add(at); fs.Note(at, $"loaded {v} escapes via {flow.Why} ({load}) in {f.Name}"); }
            if (needs is null) continue;
            if (flow.Escapes) hint!.Dirty.Add(at);
            else if (!needs.Condition.IsTrue) hint!.Conditional.Add((at, needs.Condition, false));
        }
        if (hint is not null && !hint.Bounded)
        {
            // Past the fixed bound: opaque, and nothing more to say.
            hint.Dirty.Clear(); hint.Fresh.Clear(); hint.Conditional.Clear(); hint.Merges.Clear();
            hint.Opaque = true;
        }
        return fs;
    }

    /// <summary>The call a register holds the result of, through copies and width changes; or null.</summary>
    private static Instr? CallOrigin(Defs defs, VReg r)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (!defs.IsSingle(r) || defs.Definition(r) is not Instr d) return null;
            if (d.Op == Opcode.Call && d.Callee is not null && d.Dest is not null) return d;
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32)
                || d.Operands.Count != 1 || d.Operands[0] is not RegOperand from) return null;
            r = from.Reg;
        }
        return null;
    }

    private sealed class HashSetOfStores
    {
        public HashSet<Instr> Set { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>The allocation or fresh call a register holds the result of, through copies and width changes; or null.</summary>
    /// <summary>
    /// Whether `r` is an object this pass put in the frame (the Copy of its
    /// slot that promotion made): no heap block, so nothing a field holding
    /// it must answer for -- a null, as far as rule 1 goes. A Dictionary made
    /// in the frame stores its first arrays, frame objects too, from its
    /// constructor; counted dirty, the fields were never freed, and nor was
    /// any array Grow put there after.
    /// </summary>
    private bool FrameObject(Defs defs, VReg r)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (!defs.IsSingle(r) || defs.Definition(r) is not Instr d) return false;
            if (d.Op == Opcode.Copy && d.Operands.Count == 1 && d.Operands[0] is SlotOperand) return _promotedMade.Contains(d);
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32)
                || d.Operands.Count != 1 || d.Operands[0] is not RegOperand from) return false;
            r = from.Reg;
        }
        return false;
    }

    private Instr? FreshOrigin(Function f, Defs defs, VReg r)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (!defs.IsSingle(r) || defs.Definition(r) is not Instr d) return null;
            if (d.Op == Opcode.Call && (IsAllocator(d.Callee) || IsFreshCall(d))) return d;
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32)
                || d.Operands.Count != 1 || d.Operands[0] is not RegOperand from) return null;
            r = from.Reg;
        }
        return null;
    }

    // ---- freeing them with their owner ----------------------------------------------------

    /// <summary>
    /// Every object owned in `f`: its clean fields found, and freed at each
    /// point where it dies -- before its own free, or, for a frame slot,
    /// before the slot is zeroed for the next one and on every return (with
    /// the fields cleared on entry, since a frame starts as garbage).
    /// </summary>
    /// <summary>
    /// Memory of the frame: a slot, or a register made once from one's address
    /// through copies and width changes (an object put in the frame keeps its
    /// own register, a copy of the slot's address, and its truncation).
    /// </summary>
    private static bool FrameMemory(Operand o, Defs defs) => FrameSlotOf(o, defs) is not null;

    /// <summary>The frame slot whose address `o` is, through copies and width changes; or null.</summary>
    private static FrameSlot? FrameSlotOf(Operand o, Defs defs)
    {
        for (int depth = 0; depth < 8; depth++)
        {
            if (o is SlotOperand { Slot: var slot }) return slot;
            if (o is not RegOperand { Reg: var r } || !defs.IsSingle(r) || defs.Site(r) is not { } site) return null;
            Instr made = site.Block.Instrs[site.Index];
            if (made.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || made.Operands.Count != 1) return null;
            o = made.Operands[0];
        }
        return null;
    }

    /// <summary>
    /// THE HOLDERS OF AN OBJECT OF THE FRAME: frame memory its address is
    /// stored into -- another object of the frame, or a slot. Its fields are
    /// still known there only while every use of the holder is in this
    /// function and can be followed: a load of it, a store into it, its
    /// zeroing, a copy of its address. What is loaded back from where the
    /// object was put is the object again, and joins its addresses. A holder
    /// handed to a callee is the object handed on unseen: the callee reads
    /// it back out and does what it likes with its fields -- a Deflater in
    /// the frame, its DeflateStream holding it and called to Write, stored
    /// the caller's buffer in one field and another field's array in it, and
    /// both fields were freed with it as if each held a fresh object of its
    /// own. Answers the object's addresses with every alias, or null with
    /// the use that cannot be followed.
    /// </summary>
    private Dictionary<VReg, long>? FrameHeldAliasesCore(Function f, List<VReg> roots, Dictionary<VReg, long> addresses, Defs defs, out Instr? why)
    {
        why = null;
        // The object's own slot is no holder of it: its address stored in
        // itself.
        HashSet<FrameSlot> own = new(ReferenceEqualityComparer.Instance);
        foreach (VReg r in roots)
            if (defs.IsSingle(r) && defs.Definition(r) is { Op: Opcode.Copy, Operands: [SlotOperand { Slot: var slot }] }) own.Add(slot);
        while (true)
        {
            // Where the object is put: each holder, and the offsets in it.
            Dictionary<FrameSlot, HashSet<long>> held = new(ReferenceEqualityComparer.Instance);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.Store || i.Operands.Count < 2 || _bookkeeping.Contains(i)) continue;
                    if (i.Operands[1] is not RegOperand v || !addresses.TryGetValue(v.Reg, out long delta)) continue;
                    if (i.Operands[0] is RegOperand into && addresses.ContainsKey(into.Reg)) continue;
                    if (FrameSlotOf(i.Operands[0], defs) is not FrameSlot holder) continue;
                    if (delta != 0 || own.Contains(holder)) { why = i; return null; }
                    if (!held.TryGetValue(holder, out HashSet<long>? offsets)) held[holder] = offsets = new();
                    offsets.Add(i.Offset);
                }
            if (held.Count == 0) return addresses;

            // Every use of every holder, judged; the loads of the object back
            // out of one become its addresses.
            List<VReg> grown = new(roots);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (_bookkeeping.Contains(i)) continue;
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (FrameSlotOf(i.Operands[k], defs) is not FrameSlot holder || !held.TryGetValue(holder, out HashSet<long>? offsets)) continue;
                        bool followed = (i.Op, k) switch
                        {
                            (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32, 0) => i.Dest is not null && defs.IsSingle(i.Dest),
                            (Opcode.Load, 0) => true,
                            (Opcode.Store, 0) => true,
                            (Opcode.MemSet, 0) => i.Operands.Count >= 2 && i.Operands[1] is ImmOperand { Value: 0 },
                            _ => false,
                        };
                        if (!followed) { why = i; return null; }
                        if (i.Op == Opcode.Load && offsets.Contains(i.Offset))
                        {
                            if (i.Dest is null || !defs.IsSingle(i.Dest)) { why = i; return null; }
                            if (!addresses.ContainsKey(i.Dest)) grown.Add(i.Dest);
                        }
                    }
                }
            if (grown.Count == roots.Count) return addresses;
            roots = grown;
            addresses = OwnedFieldEscape.Addresses(f, roots);
        }
    }

    private (Dictionary<VReg, long>? Aliases, Instr? Why) FrameHeldAliases(Function f, IEnumerable<VReg> roots, Dictionary<VReg, long> addresses, Defs defs)
    {
        Dictionary<VReg, long>? aliases = FrameHeldAliasesCore(f, roots.ToList(), addresses, defs, out Instr? why);
        return (aliases, why);
    }

    internal static readonly bool FieldTraceAll = Switches.FieldTraceAll;
    internal static readonly string? FieldTrace = Switches.FieldTrace;

    private void OwnFields(Function f, Dictionary<string, bool[]> summaries)
    {
        if (!_records.TryGetValue(f, out List<OwnedRecord>? records)) return;
        int word = IrTypes.Word.Bytes();
        // One analysis for all the records, as in PromoteIn: freeing a
        // record's fields adds only registers and calls of its own.
        Liveness? liveness = null;
        HashSet<VReg>? pads = null;
        foreach (OwnedRecord r in records)
        {
            VReg[] roots = r.SlotAddress is null ? new[] { r.Root } : new[] { r.Root, r.SlotAddress };
            // For the link as well, when the unit can leave it field sites.
            LifetimeFields? hint = _hinting && _fieldSites ? new() : null;
            FieldSummary fs = FieldUses(f, roots, summaries, r.Origin, null, hint, framed: r.SlotAddress is not null);
            if (r.FreshCallee == OpaqueCallee)
            {
                fs.Opaque = true;
                if (hint is not null) hint.Opaque = true;
            }
            else if (r.FreshCallee is not null)
            {
                fs.Merge(_freshFields.GetValueOrDefault(r.FreshCallee));
                if (hint is not null)
                {
                    if (!_defined.Contains(r.FreshCallee)) hint.Merges.Add((r.FreshCallee, -1));
                    else if (_freshFieldHints.GetValueOrDefault(r.FreshCallee) is LifetimeFields made) hint.Absorb(made);
                    else hint.Opaque = true;
                }
            }
            bool Fits(long o) => o >= 0 && (r.Bytes < 0 || o + word <= r.Bytes);
            List<long> clean = fs.Clean().Where(Fits).OrderBy(o => o).ToList();
            if (FieldTrace is { } traced && f.Name.Contains(traced, StringComparison.Ordinal))
                Console.Error.WriteLine($"field trace: {f.Name} {r.Origin} slot={r.Slot?.Name} opaque={fs.Opaque} fresh=[{string.Join(",", fs.FreshStored.Order())}] dirty=[{string.Join(",", fs.Dirty.Order())}] clean=[{string.Join(",", clean)}] why={fs.Why}"
                    + string.Concat((fs.DirtyWhy ?? new()).Where(kv => fs.FreshStored.Contains(kv.Key) || FieldTraceAll).OrderBy(kv => kv.Key).Select(kv => $"\n    +{kv.Key}: {kv.Value}")));
            // The fields only the link can call clean: those some code filled
            // with fresh objects -- or will have, if another unit's function
            // is what the link finds it to be -- and none made dirty here.
            List<long> later = hint is null || hint.Opaque || !hint.Bounded || hint.Merges.Count + hint.Conditional.Count == 0
                ? new()
                : hint.Fresh.Concat(hint.Conditional.Where(c => c.Stores).Select(c => c.Offset))
                    .Where(o => Fits(o) && !hint.Dirty.Contains(o) && !clean.Contains(o)).Distinct().OrderBy(o => o).ToList();
            if (clean.Count == 0 && later.Count == 0) continue;

            // Where the previous object dies inside the function, nothing
            // loaded from its fields may still be in use -- nor anything this
            // function stored there, which it may hold on to as well.
            if (r.Renew is not null)
            {
                liveness ??= new Liveness(f);
                pads ??= PadLive(liveness);
                // Field by field: a live one keeps its own object, not the others'.
                bool HeldLive(long offset) => LiveAt(f, liveness, pads, r.Renew,
                    Derivations(f, fs.Loads.Concat(fs.Stored).Where(l => l.Offset == offset).Select(l => l.Value)));
                clean.RemoveAll(HeldLive);
                later.RemoveAll(HeldLive);
            }
            if (clean.Count == 0 && later.Count == 0) continue;

            // A field the link decides is freed through a symbol of its own,
            // which the link makes Runtime.FreeField or Runtime.KeepField.
            List<(long Offset, string Callee)> frees = clean.Select(o => (o, FieldFreer)).ToList();
            if (later.Count > 0)
            {
                List<(string Symbol, long Offset)> sites = later.Select(o => (FieldSiteSymbol(f), o)).ToList();
                _fieldSiteRecords.Add((hint!, sites));
                frees.AddRange(sites.Select(site => (site.Offset, site.Symbol)));
            }
            if (r.Slot is not null && r.SlotAddress is not null)
            {
                SlotFieldFrees(f, r, frees);
            }
            else
            {
                foreach ((Block b, Instr free, VReg pointer) in r.Frees)
                {
                    int at = b.Instrs.IndexOf(free);
                    if (at < 0) continue;
                    List<Instr> before = new();
                    foreach ((long o, string callee) in frees) AppendFieldFree(f, before, pointer, o, free.Line, callee);
                    b.Instrs.InsertRange(at, before);
                    _bookkeeping.UnionWith(before);
                }
            }
            FieldsOwned += clean.Count;
        }
    }

    /// <summary>
    /// A field free the link decides: a symbol unique to this unit, function
    /// and site. Unit and serial were not enough: the backend compiles a unit
    /// in batches of functions, each a module of the unit's name whose serial
    /// began again at 0, and two batches of one object defined the same
    /// site. The function's name is unique in the unit and gives the same
    /// symbol however the batches fall.
    /// </summary>
    ///
    /// A SERIAL FOR EACH FUNCTION, kept while the unit is: one that restarted
    /// whenever the function changed named a function's sites from 0 again
    /// when its frees were placed in two turns with another function's
    /// between, and the object defined one site twice.
    private string FieldSiteSymbol(Function f)
    {
        _siteSerials.TryGetValue(f.Name, out int serial);
        _siteSerials[f.Name] = serial + 1;
        string function = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(f.Name)))[..12];
        return FieldSitePrefix + _unitKey + "$" + function + "$" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private readonly Dictionary<string, int> _siteSerials = new(StringComparer.Ordinal);

    /// <summary>The prefix of the symbols field sites call (LifetimeHints.FieldSites).</summary>
    public const string FieldSitePrefix = "__corsac_field$";

    private void SlotFieldFrees(Function f, OwnedRecord r, List<(long Offset, string Callee)> frees)
    {
        int word = IrTypes.Word.Bytes();
        // Inserted code takes its neighbour's line (the line table's runs).
        int line = EntryLine(f, r.Origin.Line);
        // On entry: the fields empty, so the first free finds nothing.
        List<Instr> entry = new();
        VReg entryAddr = f.NewReg(IrTypes.Word, "fieldsp");
        entry.Add(new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(r.Slot!) }, Line = line });
        foreach ((long o, string _) in frees)
            entry.Add(new Instr { Op = Opcode.Store, Size = word, Offset = o, Operands = { RegOperand.Of(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = line });
        f.Entry.Instrs.InsertRange(0, entry);
        _bookkeeping.UnionWith(entry);

        // Before the slot is zeroed for the next object: the last one's
        // fields. None off a loop, where no last one can be there.
        foreach (Block b in f.Blocks)
        {
            if (r.Renew is null) break;
            int at = b.Instrs.IndexOf(r.Renew!);
            if (at < 0) continue;
            List<Instr> before = new();
            int renewLine = b.Instrs[at].Line;
            VReg addr = f.NewReg(IrTypes.Word, "fieldsp");
            before.Add(new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(r.Slot!) }, Line = renewLine });
            foreach ((long o, string callee) in frees) AppendFieldFree(f, before, addr, o, renewLine, callee);
            b.Instrs.InsertRange(at, before);
            _bookkeeping.UnionWith(before);
            break;
        }

        // On every return.
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is not { Op: Opcode.Ret }) continue;
            List<Instr> before = new();
            int exitLine = b.Instrs[^1].Line;
            VReg addr = f.NewReg(IrTypes.Word, "fieldsp");
            before.Add(new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(r.Slot!) }, Line = exitLine });
            foreach ((long o, string callee) in frees) AppendFieldFree(f, before, addr, o, exitLine, callee);
            b.Instrs.InsertRange(b.Instrs.Count - 1, before);
            _bookkeeping.UnionWith(before);
        }
    }

    private static void AppendFieldFree(Function f, List<Instr> output, VReg owner, long offset, int line, string callee = FieldFreer)
    {
        VReg argument = Word(f, output, owner, line, "fieldOwner");
        output.Add(new Instr { Op = Opcode.Call, Callee = callee,
            Operands = { RegOperand.Of(argument), new ImmOperand(offset, IrTypes.Word) }, Line = line });
    }

    /// <summary>The registers holding what the given ones hold, or an address inside it.</summary>
    private static HashSet<VReg> Derivations(Function f, IEnumerable<VReg> roots)
    {
        HashSet<VReg> set = new(roots);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is null || set.Contains(i.Dest)) continue;
                    if (i.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Add or Opcode.Sub or Opcode.Phi)) continue;
                    foreach (Operand uOperand in (i).Operands) if (uOperand is RegOperand { Reg: var u })
                        if (set.Contains(u)) { set.Add(i.Dest); changed = true; break; }
                }
        }
        return set;
    }

    /// <summary>Whether any of `regs` is live just after `at` (and so just before it, since `at` defines none of them).</summary>
    private static bool LiveAt(Function f, Liveness liveness, HashSet<VReg> pads, Instr at, HashSet<VReg> regs)
    {
        if (regs.Count == 0) return false;
        // A handler's reads are live everywhere (Escape.PadLive); a register
        // newer than the analysis has no answer and is taken to be live.
        foreach (VReg r in regs) if (pads.Contains(r) || !liveness.Tracks(r)) return true;
        foreach (Block b in f.Blocks)
        {
            if (!b.Instrs.Contains(at)) continue;
            foreach ((Instr i, ulong[] liveAfter) in liveness.WalkBackwards(b, skipNewer: true))
            {
                if (!ReferenceEquals(i, at)) continue;
                foreach (VReg r in regs)
                    if (!ReferenceEquals(r, at.Dest) && Liveness.Test(liveAfter, r.Id)) return true;
                return false;
            }
        }
        return true;
    }


    /// <summary>
    /// THE BLOCKS NO OBJECT OF THESE TYPES REACHES, when the object at
    /// `addresses` is known to be one of `kinds`: a test of its type the
    /// descriptors answer -- `source is T[]`, `source is List&lt;T&gt;` -- is
    /// folded for each type, and a block reached only past tests every type
    /// fails is dead for it. List's constructor from IEnumerable, handed an
    /// iterator's machine, walks it as an enumerator and never as the array
    /// or the collection it tests for. Constants are propagated over the
    /// blocks that run (a register written in several places is what every
    /// write that runs gives it), the values read as Devirtualize reads them:
    /// symbols, read-only words and their relocations; the object itself is
    /// a place, never null.
    /// </summary>
    private static HashSet<Block>? DeadUnder(Function f, Defs defs, Dictionary<VReg, long> addresses, Stamp[] kinds)
    {
        if (_typeItems is null || kinds.Length == 0) return null;
        HashSet<Block>? dead = null;
        foreach (Stamp k in kinds)
        {
            HashSet<Block> live = LiveFor(k);
            HashSet<Block> notLive = new(f.Blocks.Where(b => !live.Contains(b)));
            if (dead is null) dead = notLive;
            else dead.IntersectWith(notLive);
            if (dead.Count == 0) return null;
        }
        return dead;

        HashSet<Block> LiveFor(Stamp k)
        {
            int word = IrTypes.Word.Bytes();
            // Absent: nothing known yet. Bottom: anything.
            Dictionary<VReg, Known> values = new();
            HashSet<VReg> bottom = new();
            // Every landing pad, whatever installs it: entered by an unwind.
            HashSet<Block> live = new(f.Blocks.Where(b => b.IsLandingPad)) { f.Entry };
            bool grew = true;
            for (int round = 0; grew && round < 64; round++)
            {
                grew = false;
                foreach (Block b in f.Blocks)
                {
                    if (!live.Contains(b)) continue;
                    foreach (Instr i in b.Instrs)
                    {
                        if (i.Op == Opcode.LabelAddr) foreach (Block pad in i.Targets) grew |= live.Add(pad);
                        if (i.Dest is { } d && !bottom.Contains(d))
                        {
                            Known? v = Evaluate(i);
                            if (v is null) { if (Settled(i)) { bottom.Add(d); values.Remove(d); grew = true; } }
                            else if (!values.TryGetValue(d, out Known was)) { values[d] = v.Value; grew = true; }
                            else if (was != v.Value) { bottom.Add(d); values.Remove(d); grew = true; }
                        }
                    }
                    Instr? t = b.Terminator;
                    if (t is null) continue;
                    if (t.Op == Opcode.Branch && t.Targets.Count == 2 && t.Operands.Count == 1)
                    {
                        Known? c = Of(t.Operands[0]);
                        if (c is { Sym: null } n) { grew |= live.Add(t.Targets[n.Value != 0 ? 0 : 1]); continue; }
                        if (c is not null) { grew |= live.Add(t.Targets[0]); continue; }      // a place: never zero
                        if (!IsBottom(t.Operands[0])) continue;                            // not known yet
                    }
                    foreach (Block next in t.Targets) grew |= live.Add(next);
                }
            }
            if (grew) return new HashSet<Block>(f.Blocks);      // did not settle: all of it
            return live;

            bool IsBottom(Operand o) => o is RegOperand { Reg: var r } && (bottom.Contains(r) || f.Params.Contains(r) && !addresses.ContainsKey(r));

            Known? Of(Operand o)
            {
                if (o is ImmOperand imm) return new Known(null, imm.Value);
                if (o is SymOperand sym) return new Known(sym.Name, sym.Offset);
                if (o is not RegOperand { Reg: var r }) return null;
                // Written once from the object: the object, never null. A
                // register also written null is followed through its writes.
                if (addresses.TryGetValue(r, out long at) && defs.IsSingle(r)) return new Known(Object, at);
                return values.TryGetValue(r, out Known v) ? v : null;
            }

            // Whether an instruction that gave no value never will: it is not
            // one this follows, one of its inputs is anything, or every input
            // is known already (and so its answer is not).
            bool Settled(Instr i)
            {
                if (i.Op is not (Opcode.Copy or Opcode.ZExt32 or Opcode.Trunc64 or Opcode.Add or Opcode.Sub or Opcode.Load)
                    && !IrInfo.IsIntCompare(i.Op)) return true;
                bool allKnown = true;
                foreach (Operand o in i.Operands)
                {
                    if (IsBottom(o) || o is not (RegOperand or ImmOperand or SymOperand)) return true;
                    if (Of(o) is null) allKnown = false;
                }
                return allKnown;
            }

            Known? Evaluate(Instr i)
            {
                switch (i.Op)
                {
                    case Opcode.Copy or Opcode.ZExt32 or Opcode.Trunc64 when i.Operands.Count == 1:
                        return Of(i.Operands[0]);
                    case Opcode.Add or Opcode.Sub when i.Operands.Count == 2 && Of(i.Operands[0]) is { } a && Of(i.Operands[1]) is { Sym: null } n:
                        return a with { Value = i.Op == Opcode.Add ? a.Value + n.Value : a.Value - n.Value };
                    case Opcode.Load when i.Operands.Count == 1 && Of(i.Operands[0]) is { Sym: { } table } at:
                    {
                        // The object's first word: its type's descriptor.
                        if (table == Object) return at.Value == 0 && i.Offset == 0 && i.Size == word ? new Known(k.Descriptor, k.Base) : null;
                        if (!_typeItems!.TryGetValue(table, out DataItem? item) || !item.ReadOnly || item.Zero) return null;
                        long w = at.Value + i.Offset;
                        foreach (DataReloc rel in item.Relocs)
                            if (rel.Offset == w) return i.Size == word ? new Known(rel.Symbol, rel.Addend) : null;
                        if (item.Relocs.Any(rel => rel.Offset < w + i.Size && rel.Offset + word > w)) return null;
                        if (w < 0 || w + i.Size > item.Bytes.Length || i.Size is not (4 or 8)) return null;
                        return new Known(null, i.Size == 8 ? BitConverter.ToInt64(item.Bytes, (int)w) : (i.Signed ? BitConverter.ToInt32(item.Bytes, (int)w) : BitConverter.ToUInt32(item.Bytes, (int)w)));
                    }
                    case var _ when IrInfo.IsIntCompare(i.Op) && i.Operands.Count == 2 && Of(i.Operands[0]) is { } x && Of(i.Operands[1]) is { } y:
                    {
                        if (x.Sym is not null || y.Sym is not null)
                        {
                            // Two places, or a place and a number: equal only
                            // as the same symbol at the same offset; a place
                            // is never a number.
                            if (i.Op is not (Opcode.Eq or Opcode.Ne)) return null;
                            bool same = x.Sym is not null && x == y;
                            return new Known(null, (i.Op == Opcode.Eq) == same ? 1 : 0);
                        }
                        bool? r = i.Op switch
                        {
                            Opcode.Eq => x.Value == y.Value, Opcode.Ne => x.Value != y.Value,
                            Opcode.LtS => x.Value < y.Value, Opcode.LeS => x.Value <= y.Value,
                            Opcode.GtS => x.Value > y.Value, Opcode.GeS => x.Value >= y.Value,
                            Opcode.LtU => (ulong)x.Value < (ulong)y.Value, Opcode.LeU => (ulong)x.Value <= (ulong)y.Value,
                            Opcode.GtU => (ulong)x.Value > (ulong)y.Value, Opcode.GeU => (ulong)x.Value >= (ulong)y.Value,
                            _ => null,
                        };
                        return r is bool known ? new Known(null, known ? 1 : 0) : null;
                    }
                    default:
                        return null;
                }
            }
        }
    }

    /// <summary>A value DeadUnder knows: a number (no symbol), or a place -- a symbol and an offset into it.</summary>
    private readonly record struct Known(string? Sym, long Value);

    /// <summary>DeadUnder's name for the object itself, which no symbol is.</summary>
    private const string Object = "\u0001object";
}
