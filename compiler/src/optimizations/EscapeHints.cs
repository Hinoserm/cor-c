#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

// ---- hints for the link ---------------------------------------------------------------
//
// A SOURCE COMPILED ON ITS OWN SEES ONLY ITSELF. An object handed to a
// function another unit defines, or made by one, is left to the collector,
// because nothing here says what that function does with it. Compiling the
// whole program as one module would say -- and would throw away the build's
// parallel units and need the whole program in memory at once.
//
// So a unit does everything it can by itself and leaves hints for the link
// (Corsac.Lang.Lto.LifetimeHints): each function's summary stated in terms
// of the other units' functions it calls ("parameter 0 keeps nothing if
// Foo's parameter 1 keeps nothing"), and, for every object it had to leave
// to the collector ONLY because of such a call, the condition under which
// it could have been freed or kept in the frame. The link solves every
// unit's summaries together and recompiles just the units whose condition
// then holds, with the answers (LifetimeSolver, RunAtLink below).
//
// The summaries the unit itself uses are the same analysis read the
// pessimistic way: a parameter whose hint has conditions escapes, as far as
// this unit can tell, exactly as a call to an unknown function always made
// it escape. Nothing this unit decides depends on the hints.

public sealed partial class Escape
{
    /// <summary>
    /// The escape question asked so that calls to other units' functions do
    /// not answer it: each is written down as a condition instead. A call to
    /// a function of this unit whose own summary has conditions adds those.
    /// Past <see cref="LifetimeCondition.Limit"/> the answer is "escapes",
    /// a fixed bound, so it is the same on every machine.
    /// </summary>
    internal sealed class Needs
    {
        private readonly Escape _pass;
        public LifetimeCondition Condition { get; } = new();

        public Needs(Escape pass) => _pass = pass;

        public bool Allow(string callee, int argument)
        {
            if (IsIntrinsic(callee)) return false;
            if (!_pass._defined.Contains(callee))
            {
                Condition.Stays.Add((callee, argument));
                return Condition.Count <= LifetimeCondition.Limit;
            }
            return _pass._paramHints.TryGetValue(callee, out LifetimeCondition?[]? hints) && argument < hints.Length
                && hints[argument] is LifetimeCondition condition && Condition.Add(condition);
        }

        public bool AllowFresh(string callee)
        {
            if (IsIntrinsic(callee)) return false;
            if (!_pass._defined.Contains(callee))
            {
                Condition.Fresh.Add(callee);
                return Condition.Count <= LifetimeCondition.Limit;
            }
            return _pass._freshHints.TryGetValue(callee, out LifetimeCondition? condition)
                && condition is not null && Condition.Add(condition);
        }
    }

    /// <summary>
    /// A name the backend answers itself -- `__x86.i.threadblock`,
    /// `__exception` -- or a field site's: no unit defines it, so no other
    /// unit's summary can speak for it.
    /// </summary>
    public static bool IsIntrinsic(string callee)
        => callee.StartsWith("__x86.", StringComparison.Ordinal) || callee == "__exception"
           || callee.StartsWith(FieldSitePrefix, StringComparison.Ordinal);

    private readonly HashSet<string> _defined = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifetimeCondition?[]> _paramHints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifetimeCondition?> _freshHints = new(StringComparer.Ordinal);
    /// <summary>Each parameter kept only in the box its function returns, as a condition on other units (SummariseHeld).</summary>
    private readonly Dictionary<string, LifetimeHeld?[]> _heldHints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifetimeFields?[]> _fieldHints = new(StringComparer.Ordinal);
    /// <summary>Field frees the link decides (OwnFields): each owned object's field summary and its sites.</summary>
    private readonly List<(LifetimeFields Fields, List<(string Symbol, long Offset)> Sites)> _fieldSiteRecords = new();
    /// <summary>Whether the runtime has both frees a field site can become; the unit then leaves sites.</summary>
    private bool _fieldSites;
    /// <summary>What makes this unit's field-site symbols its own: its name, hashed.</summary>
    private string _unitKey = "";
    private readonly Dictionary<string, LifetimeFields?> _freshFieldHints = new(StringComparer.Ordinal);
    private readonly List<LifetimeCondition> _pending = new();
    private readonly HashSet<LifetimeCondition> _pendingSeen = new();

    /// <summary>Whether this run leaves hints (a unit compile) or reads the link's answers (RunAtLink).</summary>
    private bool _hinting = true;

    /// <summary>
    /// Each parameter's summary as a condition on other units: null, it
    /// escapes whatever they do; empty, it keeps nothing; otherwise it keeps
    /// nothing if the condition holds.
    /// </summary>
    private LifetimeCondition?[] ParameterHints(Function f, Dictionary<string, bool[]> summaries)
    {
        LifetimeCondition?[] result = new LifetimeCondition?[f.Params.Count];
        for (int p = 0; p < f.Params.Count; p++)
        {
            VReg param = f.Params[p];
            if (param.Type is not (IrType.I32 or IrType.I64))
            {
                result[p] = new LifetimeCondition();
                continue;
            }
            Needs needs = new(this);
            Flow flow = Analyse(f, new[] { param }, summaries, null, needs: needs);
            result[p] = flow.Escapes ? null : needs.Condition;
            if (PromoteTrace is { } pt && f.Name.Contains(pt, StringComparison.Ordinal))
                Console.Error.WriteLine($"param {f.Name}:{p} escapes={flow.Escapes} via {flow.Why} needs={string.Join(",", needs.Condition.Stays.Select(s => s.Callee + ":" + s.Argument))}");
        }
        return result;
    }

    /// <summary>The summary this unit acts on: escaping unless it keeps nothing whatever the other units do.</summary>
    private static bool[] Pessimistic(LifetimeCondition?[] hints)
        => hints.Select(hint => hint is not { IsTrue: true }).ToArray();

    /// <summary>
    /// The parameters of one call-graph cycle (or one function), solved to
    /// the least fixed point as conditions: every parameter starts out
    /// keeping nothing, and each round asks again with what the others were
    /// found to need, until nothing changes. Conditions only grow, so it
    /// settles; if it does not within the bound, everything escapes, as it
    /// always did.
    /// </summary>
    private void SummariseCycle(List<Function> cycle, Dictionary<string, bool[]> summaries)
    {
        if (cycle.Count == 1 && !CallsItself(cycle[0]))
        {
            LifetimeCondition?[] hints = ParameterHints(cycle[0], summaries);
            _paramHints[cycle[0].Name] = hints;
            summaries[cycle[0].Name] = Pessimistic(hints);
            return;
        }
        foreach (Function f in cycle)
        {
            _paramHints[f.Name] = f.Params.Select(_ => (LifetimeCondition?)new LifetimeCondition()).ToArray();
            summaries[f.Name] = new bool[f.Params.Count];
        }
        for (int round = 0; round < CycleRounds; round++)
        {
            bool changed = false;
            foreach (Function f in cycle)
            {
                LifetimeCondition?[] again = ParameterHints(f, summaries);
                LifetimeCondition?[] before = _paramHints[f.Name];
                bool same = true;
                for (int p = 0; p < again.Length; p++) same &= LifetimeCondition.Same(again[p], before[p]);
                if (same) continue;
                _paramHints[f.Name] = again;
                summaries[f.Name] = Pessimistic(again);
                changed = true;
            }
            if (!changed) return;
        }
        foreach (Function f in cycle)
        {
            _paramHints[f.Name] = new LifetimeCondition?[f.Params.Count];
            summaries[f.Name] = Enumerable.Repeat(true, f.Params.Count).ToArray();
        }
    }

    /// <summary>
    /// Whether what `f` returns is fresh, as a condition on other units: the
    /// objects it returns may come from their functions as well as from its
    /// own allocations. Empty: fresh here and now (ReturnsFresh's answer).
    /// </summary>
    private LifetimeCondition? FreshHint(Function f, Dictionary<string, bool[]> summaries,
        out List<Instr>? origins, out HashSet<VReg>? chain)
    {
        Needs needs = new(this);
        return ReturnsFresh(f, summaries, _fresh, out origins, out chain, needs) ? needs.Condition : null;
    }

    /// <summary>
    /// An allocation or a call's result this unit had to leave to the
    /// collector: if the only reason was calls into other units, the
    /// condition under which it need not have been, for the link.
    /// `freshCallee`, for a call's result, is the function that must hand
    /// it over. Only what the unit would then act on is recorded: the
    /// liveness proof it would still need has to hold already.
    /// </summary>
    private void Pending(Function f, Block b, Instr made, Dictionary<string, bool[]> summaries,
        Liveness liveness, HashSet<VReg> pads, string? freshCallee)
    {
        if (!_hinting || _pending.Count >= LifetimeHints.PendingLimit || made.Dest is null) return;
        Needs needs = new(this);
        if (freshCallee is not null && !needs.AllowFresh(freshCallee)) return;
        Flow flow = Analyse(f, new[] { made.Dest }, summaries, made, needs: needs);
        bool tracing = PromoteTrace is { } pt && f.Name.Contains(pt, StringComparison.Ordinal);
        if (tracing) Console.Error.WriteLine($"pending {f.Name}: {made} escapes={flow.Escapes} via {flow.Why} needs={needs.Condition}");
        if (flow.Escapes || needs.Condition.IsTrue) return;
        if (!liveness.Tracks(made.Dest) || LiveAtSelf(liveness, pads, b, made, flow.Derived))
        {
            if (tracing) Console.Error.WriteLine($"pending {f.Name}: {made} live at its own making");
            return;
        }
        AddPending(needs.Condition);
        // An allocation stays a call to the allocator, not to what the
        // allocator calls: the link recognises allocations by that name.
        if (freshCallee is null) _keep.Add(made);
    }

    private readonly HashSet<Instr> _keep = new(ReferenceEqualityComparer.Instance);

    /// <summary>A condition the link is to check for this unit: once each, and no more than the fixed bound.</summary>
    private void AddPending(LifetimeCondition condition)
    {
        if (_pending.Count < LifetimeHints.PendingLimit && _pendingSeen.Add(condition)) _pending.Add(condition);
    }

    /// <summary>The unit's hints: every function's summary and every pending condition.</summary>
    private LifetimeHints Hints(Module m, Func<string, bool> provided)
    {
        LifetimeHints hints = new();
        foreach (Function f in m.Functions)
        {
            LifetimeCondition?[] parameters = _paramHints.TryGetValue(f.Name, out LifetimeCondition?[]? known)
                ? known : new LifetimeCondition?[f.Params.Count];
            hints.Functions.Add(new(f.Name, f.Exported, parameters, _freshHints.GetValueOrDefault(f.Name),
                _fieldHints.GetValueOrDefault(f.Name), _freshFieldHints.GetValueOrDefault(f.Name), _heldHints.GetValueOrDefault(f.Name)));
        }
        hints.Pending.AddRange(_pending);
        hints.FieldSites.AddRange(_fieldSiteRecords);
        ThrowHints(m, hints);
        m.KeepCalls.UnionWith(_keep);
        foreach (string helper in new[] { Freer, FieldFreer, ReplacedFreer, FieldKeeper, OwnedReplacedFreer, OwnedElements.Freer, StorageFreer })
            if (provided(helper)) hints.Helpers.Add(helper);
        return hints;
    }

    /// <summary>
    /// THE LINK'S SECOND LOOK at one function of a unit it recompiles: the
    /// same placing and freeing the unit did, now with every function's
    /// summary answered by the whole program (LifetimeSolver). Called on
    /// each function as the backend loads it, so a unit is never held whole.
    /// The IR is what the unit compile left, its own frees included; an
    /// object it already placed or owns is stored in its slot and so escapes
    /// here, and is not taken twice.
    /// </summary>
    /// <summary>
    /// The link's answers for one unit in the form the passes read them,
    /// made once per unit rather than once per function it loads.
    /// </summary>
    public sealed class LinkFacts
    {
        internal HashSet<string> Fresh { get; }
        internal Dictionary<string, bool[]> Escapes { get; }
        /// <summary>Per function, per parameter: the offsets of the box it returns that alone keep it (Held); null where not so.</summary>
        internal Dictionary<string, long[]?[]> Held { get; }
        internal HashSet<string> Helpers { get; }
        internal Dictionary<string, FieldSummary?[]> ParameterFields { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, FieldSummary> FreshFields { get; } = new(StringComparer.Ordinal);
        /// <summary>The fields the whole program found own what they hold, and who hands one back (OwnedFieldSolver).</summary>
        internal OwnedFieldFacts? OwnedFields { get; }

        public LinkFacts(LifetimeFacts facts)
        {
            OwnedFields = facts.OwnedFields;
            Fresh = facts.Fresh;
            Escapes = facts.Escapes;
            Held = facts.Held;
            Helpers = facts.Helpers;
            foreach ((string name, SolvedFields?[] fields) in facts.Fields) ParameterFields[name] = fields.Select(Summary).ToArray();
            foreach ((string name, SolvedFields fields) in facts.FreshFields) FreshFields[name] = Summary(fields)!;
        }

        private static FieldSummary? Summary(SolvedFields? solved)
        {
            if (solved is null) return null;
            FieldSummary summary = new() { Opaque = solved.Opaque };
            summary.Dirty.UnionWith(solved.Dirty);
            summary.FreshStored.UnionWith(solved.Fresh);
            return summary;
        }
    }

    public static int RunAtLink(Function f, LinkFacts facts, Dictionary<string, DataItem>? descriptors = null)
    {

        // The facts are shared by every function of the unit, on every
        // backend worker: read here, never written, never copied.
        Escape pass = new()
        {
            _hinting = false, _fresh = facts.Fresh, _paramFields = facts.ParameterFields, _freshFields = facts.FreshFields,
        };
        Dictionary<string, bool[]> summaries = facts.Escapes;
        bool canFree = facts.Helpers.Contains(Freer);
        pass._storageFreer = facts.Helpers.Contains(StorageFreer);
        pass._descriptors = descriptors;
        Dictionary<string, Function> byName = new(StringComparer.Ordinal) { [f.Name] = f };
        _inserted = pass._bookkeeping;
        _held = facts.Held;
        _fieldsOf = facts.ParameterFields;
        // Its virtual calls, each as every override the image has for it,
        // wherever the link could say (VirtualCallees; Lto.VirtualTargets).
        _indirect = VirtualCallees(new[] { f }, summaries.ContainsKey);
        try
        {
            pass.PromoteIn(f, summaries, canFree, new OwnedFieldEscape(byName, summaries));
            if (canFree && facts.Helpers.Contains(ReplacedFreer)) pass.OwnVariables(f, summaries);
            if (canFree && facts.Helpers.Contains(FieldFreer)) pass.OwnFields(f, summaries);
        }
        finally { _inserted = null; _indirect = null; _held = null; _fieldsOf = null; }
        // A READ OF AN OWNED FIELD was judged, by the unit and then the link,
        // among the frees the function had then: a free placed now, while
        // what was read is live, could free the field's owner under it. The
        // caller then takes the function back as it was (-1).
        if (facts.OwnedFields is { Fields.Count: > 0 } owned && pass._bookkeeping.Count > 0 && FreesUnderOwnedRead(f, owned, pass._bookkeeping)) return -1;
        return pass.Promoted + pass.Owned + pass.FieldsOwned;
    }

    /// <summary>Whether the link's lifetime pass could place a free a read of an owned field must be checked against.</summary>
    public static bool ReadsOwnedField(Function f, LinkFacts facts) => facts.OwnedFields is { Fields.Count: > 0 } owned && ReadsOwned(f, owned);

    /// <summary>
    /// Whether one of the frees just placed runs while a value read from an
    /// owned field (or handed back by a function that reads one) is live:
    /// the stretch OwnedFields judges, found the same way.
    /// </summary>
    private static bool FreesUnderOwnedRead(Function f, OwnedFieldFacts owned, HashSet<Instr> placed)
    {
        if (!placed.Any(i => i.Op == Opcode.Call && IsFreeCall(i.Callee))) return false;
        Liveness liveness = new(f);
        HashSet<VReg> pads = PadLive(liveness);
        Dictionary<Instr, string[]> virtuals = VirtualCallees(new[] { f });
        Dictionary<VReg, Instr> defs = SingleDefs(f);
        HashSet<VReg> written = new();
        foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (i.Dest is not null) written.Add(i.Dest);
        HashSet<VReg> parameters = f.Params.Where(p => !written.Contains(p)).ToHashSet();
        // Through copies, address arithmetic and loads, to a parameter never
        // written again or to a static: what the function was handed.
        bool Handed(VReg r, int depth = 0)
        {
            if (parameters.Contains(r)) return true;
            if (depth > 16 || !defs.TryGetValue(r, out Instr? d)) return false;
            return d.Op switch
            {
                Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Add or Opcode.Sub or Opcode.Load
                    => d.Operands.Count > 0 && d.Operands.All(o => o switch
                    {
                        RegOperand from => Handed(from.Reg, depth + 1),
                        SymOperand => true,
                        ImmOperand => d.Op is not Opcode.Load,
                        _ => false,
                    }),
                _ => false,
            };
        }
        foreach (Block b in f.Blocks)
            foreach (Instr read in b.Instrs)
            {
                if (read.Dest is null) continue;
                bool reads = read.Op == Opcode.Load && read.Field is not null && owned.Fields.ContainsKey(read.Field)
                    || read.Op == Opcode.Call && read.Callee is not null && owned.Borrowers.Contains(read.Callee)
                    || read.Op == Opcode.CallIndirect && owned.Borrowers.Count > 0
                       && (!virtuals.TryGetValue(read, out string[]? targets) || targets.Any(owned.Borrowers.Contains));
                if (!reads) continue;
                // AN OWNER THE FUNCTION WAS HANDED -- a parameter, a static, or
                // what is read from one -- no free placed here can reach:
                // each frees only an object the function made or was handed
                // fresh, and kept to itself, with what that object's owned
                // fields hold, and nothing kept to itself was ever stored
                // where a parameter or a static reaches. Its `this._paths`
                // read across a string's free took Monomorphiser.Named back
                // whole once HashSet.Contains was inlined into it.
                if (read.Operands.All(o => o is not RegOperand r || Handed(r.Reg))) continue;
                // What was read, and every copy and address made from it.
                HashSet<VReg> derived = new() { read.Dest };
                for (bool grew = true; grew;)
                {
                    grew = false;
                    foreach (Block x in f.Blocks)
                        foreach (Instr i in x.Instrs)
                            if (i.Dest is not null && !derived.Contains(i.Dest)
                                && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Phi or Opcode.Add or Opcode.Sub
                                && i.Operands.Any(o => o is RegOperand r && derived.Contains(r.Reg)))
                            { derived.Add(i.Dest); grew = true; }
                }
                if (derived.Any(r => !liveness.Tracks(r) || pads.Contains(r))) return true;
                bool Uses(Instr i) => i.Operands.Any(o => o is RegOperand r && derived.Contains(r.Reg));
                foreach (Block x in f.Blocks)
                {
                    bool liveIn = derived.Any(r => liveness.IsLiveIn(x, r));
                    bool liveOut = derived.Any(r => liveness.IsLiveOut(x, r));
                    int from = liveIn ? 0 : ReferenceEquals(x, b) ? b.Instrs.IndexOf(read) + 1 : -1;
                    if (from < 0)
                    {
                        int copied = x.Instrs.FindIndex(i => i.Dest is not null && derived.Contains(i.Dest));
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
                        if (placed.Contains(x.Instrs[k]) && x.Instrs[k].Op == Opcode.Call && IsFreeCall(x.Instrs[k].Callee)) return true;
                }
            }
        return false;
    }

    /// <summary>Whether a function reads an owned field: loads it, or calls what hands one back.</summary>
    private static bool ReadsOwned(Function f, OwnedFieldFacts owned)
    {
        Dictionary<Instr, string[]>? virtuals = null;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Load && i.Field is not null && owned.Fields.ContainsKey(i.Field)) return true;
                if (i.Dest is null) continue;
                if (i.Op == Opcode.Call && i.Callee is not null && owned.Borrowers.Contains(i.Callee)) return true;
                if (i.Op == Opcode.CallIndirect && owned.Borrowers.Count > 0)
                {
                    virtuals ??= VirtualCallees(new[] { f });
                    if (!virtuals.TryGetValue(i, out string[]? targets) || targets.Any(owned.Borrowers.Contains)) return true;
                }
            }
        return false;
    }
}
