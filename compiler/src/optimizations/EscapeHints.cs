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
            if (!_pass._defined.Contains(callee))
            {
                Condition.Fresh.Add(callee);
                return Condition.Count <= LifetimeCondition.Limit;
            }
            return _pass._freshHints.TryGetValue(callee, out LifetimeCondition? condition)
                && condition is not null && Condition.Add(condition);
        }
    }

    private readonly HashSet<string> _defined = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifetimeCondition?[]> _paramHints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifetimeCondition?> _freshHints = new(StringComparer.Ordinal);
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
        if (flow.Escapes || needs.Condition.IsTrue) return;
        if (!liveness.Tracks(made.Dest) || LiveAtSelf(liveness, pads, b, made, flow.Derived)) return;
        if (_pendingSeen.Add(needs.Condition)) _pending.Add(needs.Condition);
        // An allocation stays a call to the allocator, not to what the
        // allocator calls: the link recognises allocations by that name.
        if (freshCallee is null) _keep.Add(made);
    }

    private readonly HashSet<Instr> _keep = new(ReferenceEqualityComparer.Instance);

    /// <summary>The unit's hints: every function's summary and every pending condition.</summary>
    private LifetimeHints Hints(Module m, Func<string, bool> provided)
    {
        LifetimeHints hints = new();
        foreach (Function f in m.Functions)
        {
            LifetimeCondition?[] parameters = _paramHints.TryGetValue(f.Name, out LifetimeCondition?[]? known)
                ? known : new LifetimeCondition?[f.Params.Count];
            hints.Functions.Add(new(f.Name, f.Exported, parameters, _freshHints.GetValueOrDefault(f.Name)));
        }
        hints.Pending.AddRange(_pending);
        m.KeepCalls.UnionWith(_keep);
        foreach (string helper in new[] { Freer, FieldFreer, ReplacedFreer })
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
    public static int RunAtLink(Function f, LifetimeFacts facts)
    {
        // The facts are shared by every function of the unit, on every
        // backend worker: read here, never written, never copied.
        Escape pass = new() { _hinting = false, _fresh = facts.Fresh };
        Dictionary<string, bool[]> summaries = facts.Escapes;
        bool canFree = facts.Helpers.Contains(Freer);
        Dictionary<string, Function> byName = new(StringComparer.Ordinal) { [f.Name] = f };
        pass.PromoteIn(f, summaries, canFree, new OwnedFieldEscape(byName, summaries));
        if (canFree && facts.Helpers.Contains(ReplacedFreer)) pass.OwnVariables(f, summaries);
        if (canFree && facts.Helpers.Contains(FieldFreer)) pass.OwnFields(f, summaries);
        return pass.Promoted + pass.Owned;
    }
}
