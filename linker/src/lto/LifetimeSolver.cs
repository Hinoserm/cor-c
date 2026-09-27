namespace Corsac.Lang.Lto;

/// <summary>
/// What one unit's backend is told when the link recompiles it: for every
/// function it defines or calls, which parameters escape and whether what
/// it returns is fresh, as the whole program answers. A function missing
/// from here is one nobody summarised -- assembly, a shared library -- and
/// keeps everything it is handed.
/// </summary>
public sealed class LifetimeFacts
{
    public Dictionary<string, bool[]> Escapes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Fresh { get; } = new(StringComparer.Ordinal);
    /// <summary>The runtime's frees the unit may call (its hints' helpers).</summary>
    public HashSet<string> Helpers { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Every unit's <see cref="LifetimeHints"/> solved together. Each unit stated
/// its functions' summaries in terms of other units' functions; here those
/// statements are one system of monotone equations over the whole program,
/// solved to the least fixed point as the compiler solves a cycle inside one
/// unit: every parameter starts out keeping nothing and turns to escaping
/// only when something it depends on does; every function starts out not
/// fresh and turns fresh only once everything it depends on holds. Both
/// halves are worklists over a reverse-dependency index, so the solve is
/// linear in the size of the hints, and the order is fixed -- the answer is
/// the same wherever and however often it is computed.
/// </summary>
public sealed class LifetimeSolver
{
    private readonly Dictionary<string, LifetimeFunction> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool[]> _escapes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fresh = new(StringComparer.Ordinal);

    /// <summary>
    /// Units in link order; the first definition of a global name is the one
    /// every other unit's call resolves to, as in symbol resolution.
    /// </summary>
    public LifetimeSolver(IEnumerable<LifetimeHints> units)
    {
        foreach (LifetimeHints unit in units)
            foreach (LifetimeFunction function in unit.Functions)
                if (function.Global) _globals.TryAdd(function.Name, function);
        SolveEscapes();
        SolveFresh();
    }

    /// <summary>Whether the named global's argument escapes; a function with no summary keeps everything.</summary>
    public bool Escapes(string callee, int argument)
        => !_escapes.TryGetValue(callee, out bool[]? escapes) || argument >= escapes.Length || escapes[argument];

    public bool IsFresh(string callee) => _fresh.Contains(callee);

    public bool Holds(LifetimeCondition? condition)
        => condition is not null && condition.Stays.All(need => !Escapes(need.Callee, need.Argument)) && condition.Fresh.All(IsFresh);

    private void SolveEscapes()
    {
        Dictionary<(string, int), List<(bool[] Escapes, int Parameter, string Owner)>> dependents = new();
        Queue<(string, int)> escaped = new();
        foreach (string name in _globals.Keys.Order(StringComparer.Ordinal))
        {
            LifetimeFunction function = _globals[name];
            bool[] escapes = new bool[function.Parameters.Length];
            _escapes.Add(name, escapes);
        }
        foreach (string name in _globals.Keys.Order(StringComparer.Ordinal))
        {
            LifetimeFunction function = _globals[name];
            bool[] escapes = _escapes[name];
            for (int p = 0; p < escapes.Length; p++)
            {
                LifetimeCondition? condition = function.Parameters[p];
                if (condition is null) { escapes[p] = true; escaped.Enqueue((name, p)); continue; }
                foreach ((string callee, int argument) in condition.Stays)
                {
                    // Nothing summarised it: it escapes from the start.
                    if (!_escapes.TryGetValue(callee, out bool[]? known) || argument >= known.Length)
                    {
                        if (!escapes[p]) { escapes[p] = true; escaped.Enqueue((name, p)); }
                        continue;
                    }
                    if (!dependents.TryGetValue((callee, argument), out var list)) dependents[(callee, argument)] = list = new();
                    list.Add((escapes, p, name));
                }
            }
        }
        while (escaped.TryDequeue(out (string, int) done))
        {
            if (!dependents.TryGetValue(done, out var list)) continue;
            foreach ((bool[] escapes, int p, string owner) in list)
                if (!escapes[p]) { escapes[p] = true; escaped.Enqueue((owner, p)); }
        }
    }

    private void SolveFresh()
    {
        // How many of each candidate's fresh callees are not yet fresh.
        Dictionary<string, int> waiting = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = new(StringComparer.Ordinal);
        Queue<string> ready = new();
        foreach (string name in _globals.Keys.Order(StringComparer.Ordinal))
        {
            LifetimeCondition? condition = _globals[name].Fresh;
            if (condition is null || condition.Stays.Any(need => Escapes(need.Callee, need.Argument))) continue;
            waiting[name] = condition.Fresh.Count;
            foreach (string callee in condition.Fresh)
            {
                if (!dependents.TryGetValue(callee, out List<string>? list)) dependents[callee] = list = new();
                list.Add(name);
            }
            if (condition.Fresh.Count == 0) ready.Enqueue(name);
        }
        while (ready.TryDequeue(out string? name))
        {
            if (!_fresh.Add(name) || !dependents.TryGetValue(name, out List<string>? list)) continue;
            foreach (string waiter in list)
                if (--waiting[waiter] == 0) ready.Enqueue(waiter);
        }
    }

    /// <summary>
    /// The facts one unit is recompiled with: its own functions, local ones
    /// included, answered from their conditions; and every global it calls.
    /// A local name shadows a global of the same name, as in the link.
    /// </summary>
    public LifetimeFacts For(LifetimeHints unit, IEnumerable<string> calls)
    {
        LifetimeFacts facts = new();
        foreach (string call in calls)
        {
            if (_escapes.TryGetValue(call, out bool[]? escapes)) facts.Escapes[call] = (bool[])escapes.Clone();
            if (_fresh.Contains(call)) facts.Fresh.Add(call);
        }
        foreach (LifetimeFunction function in unit.Functions)
        {
            facts.Escapes[function.Name] = function.Parameters.Select(condition => !Holds(condition)).ToArray();
            if (Holds(function.Fresh)) facts.Fresh.Add(function.Name);
            else facts.Fresh.Remove(function.Name);
        }
        facts.Helpers.UnionWith(unit.Helpers);
        return facts;
    }
}
