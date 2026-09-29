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
    /// <summary>Per function, per parameter: what it does to that object's fields; null where the parameter escapes.</summary>
    public Dictionary<string, SolvedFields?[]> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>Per fresh function: what it left in the fields of the object it hands over.</summary>
    public Dictionary<string, SolvedFields> FreshFields { get; } = new(StringComparer.Ordinal);
    /// <summary>The runtime's frees the unit may call (its hints' helpers).</summary>
    public HashSet<string> Helpers { get; } = new(StringComparer.Ordinal);
}

/// <summary>What the whole program does to one object's fields: the owned field rules' summary, solved.</summary>
public sealed record SolvedFields(bool Opaque, long[] Dirty, long[] Fresh);

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
    private readonly Dictionary<(string, int), Accumulated> _fields = new();

    /// <summary>A field summary being solved: only ever grows.</summary>
    private sealed class Accumulated
    {
        public bool Opaque;
        public readonly SortedSet<long> Dirty = new();
        public readonly SortedSet<long> Fresh = new();
        public bool Absorb(Accumulated other)
        {
            int before = Dirty.Count + Fresh.Count;
            bool opaque = Opaque;
            Opaque |= other.Opaque; Dirty.UnionWith(other.Dirty); Fresh.UnionWith(other.Fresh);
            return Opaque != opaque || Dirty.Count + Fresh.Count != before;
        }
        public SolvedFields Solved() => new(Opaque, Dirty.ToArray(), Fresh.ToArray());
    }

    /// <summary>The fresh-return summary's key: parameter -1.</summary>
    private const int Returned = -1;

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
        SolveFields();
    }

    /// <summary>Whether the named global's argument escapes; a function with no summary keeps everything.</summary>
    public bool Escapes(string callee, int argument)
        => !_escapes.TryGetValue(callee, out bool[]? escapes) || argument >= escapes.Length || escapes[argument];

    public bool IsFresh(string callee) => _fresh.Contains(callee);

    public bool Holds(LifetimeCondition? condition)
        => condition is not null && condition.Stays.All(need => !Escapes(need.Callee, need.Argument)) && condition.Fresh.All(IsFresh)
           && condition.Fields.All(need => Merged(need.Callee, need.Argument) is { Opaque: false });

    /// <summary>A merge's summary: an argument's, or (argument -1) what a fresh function returns.</summary>
    private SolvedFields? Merged(string callee, int argument)
        => argument == Returned ? FreshFieldsOf(callee) : FieldsOf(callee, argument);

    /// <summary>What the named global does to its argument's fields; null when that argument escapes or nothing says.</summary>
    public SolvedFields? FieldsOf(string callee, int argument)
        => !Escapes(callee, argument) && _fields.TryGetValue((callee, argument), out Accumulated? found) ? found.Solved() : null;

    /// <summary>What a fresh global left in its returned object's fields; null when it is not fresh or nothing says.</summary>
    public SolvedFields? FreshFieldsOf(string callee)
        => IsFresh(callee) && _fields.TryGetValue((callee, Returned), out Accumulated? found) ? found.Solved() : null;

    /// <summary>
    /// A field hint's own part, with its conditions answered: a child stored
    /// from another unit's function is fresh if that function is and the
    /// child is let go no other way, dirty if not; a child loaded and handed
    /// to one stays clean only if it keeps nothing.
    /// </summary>
    private Accumulated Local(LifetimeFields hint)
    {
        Accumulated local = new() { Opaque = hint.Opaque };
        local.Dirty.UnionWith(hint.Dirty);
        local.Fresh.UnionWith(hint.Fresh);
        foreach ((long offset, LifetimeCondition condition, bool stores) in hint.Conditional)
        {
            bool holds = Holds(condition);
            if (stores && holds) local.Fresh.Add(offset);
            else if (!holds) local.Dirty.Add(offset);
        }
        return local;
    }

    /// <summary>
    /// Field summaries: every summary's own part, then each summary merged
    /// with those of the functions the object is handed to, until nothing
    /// grows. Handed to a function nobody summarised, or to a parameter that
    /// escapes, the object is opaque.
    /// </summary>
    private void SolveFields()
    {
        Dictionary<(string, int), List<(string, int)>> dependents = new();
        Dictionary<(string, int), LifetimeFields> hints = new();
        foreach (string name in _globals.Keys.Order(StringComparer.Ordinal))
        {
            LifetimeFunction function = _globals[name];
            LifetimeFields?[] parameters = function.ParameterFields ?? Array.Empty<LifetimeFields?>();
            for (int p = 0; p < parameters.Length; p++)
                if (parameters[p] is LifetimeFields hint && !Escapes(name, p)) hints[(name, p)] = hint;
            if (function.FreshFields is LifetimeFields returned && IsFresh(name)) hints[(name, Returned)] = returned;
        }
        Queue<(string, int)> grew = new();
        foreach (((string, int) key, LifetimeFields hint) in hints.OrderBy(pair => pair.Key.Item1, StringComparer.Ordinal).ThenBy(pair => pair.Key.Item2))
        {
            Accumulated local = Local(hint);
            foreach ((string callee, int argument) in hint.Merges)
            {
                if (!hints.ContainsKey((callee, argument))) { local.Opaque = true; continue; }
                if (!dependents.TryGetValue((callee, argument), out var list)) dependents[(callee, argument)] = list = new();
                list.Add(key);
            }
            _fields[key] = local;
        }
        foreach ((string, int) key in _fields.Keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2)) grew.Enqueue(key);
        while (grew.TryDequeue(out (string, int) done))
        {
            if (!dependents.TryGetValue(done, out var list)) continue;
            foreach ((string, int) waiter in list)
                if (_fields[waiter].Absorb(_fields[done])) grew.Enqueue(waiter);
        }
    }

    /// <summary>A field hint answered by the whole program: its own part and what it merges.</summary>
    public SolvedFields? Solve(LifetimeFields hint) => Answer(hint);

    /// <summary>A unit's own function's field hint answered: its own part and what it merges.</summary>
    private SolvedFields? Answer(LifetimeFields? hint)
    {
        if (hint is null) return null;
        Accumulated result = Local(hint);
        foreach ((string callee, int argument) in hint.Merges)
        {
            if (Merged(callee, argument) is SolvedFields merged)
            {
                result.Opaque |= merged.Opaque; result.Dirty.UnionWith(merged.Dirty); result.Fresh.UnionWith(merged.Fresh);
            }
            else result.Opaque = true;
        }
        return result.Solved();
    }

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
            if (_escapes.TryGetValue(call, out bool[]? escapes))
            {
                facts.Escapes[call] = (bool[])escapes.Clone();
                facts.Fields[call] = Enumerable.Range(0, escapes.Length).Select(p => FieldsOf(call, p)).ToArray();
            }
            if (_fresh.Contains(call))
            {
                facts.Fresh.Add(call);
                if (FreshFieldsOf(call) is SolvedFields returned) facts.FreshFields[call] = returned;
            }
        }
        foreach (LifetimeFunction function in unit.Functions)
        {
            bool[] escapes = function.Parameters.Select(condition => !Holds(condition)).ToArray();
            facts.Escapes[function.Name] = escapes;
            LifetimeFields?[] fields = function.ParameterFields ?? Array.Empty<LifetimeFields?>();
            facts.Fields[function.Name] = Enumerable.Range(0, escapes.Length)
                .Select(p => escapes[p] || p >= fields.Length ? null : Answer(fields[p])).ToArray();
            facts.FreshFields.Remove(function.Name);
            if (Holds(function.Fresh))
            {
                facts.Fresh.Add(function.Name);
                if (Answer(function.FreshFields) is SolvedFields returned) facts.FreshFields[function.Name] = returned;
            }
            else facts.Fresh.Remove(function.Name);
        }
        facts.Helpers.UnionWith(unit.Helpers);
        return facts;
    }
}
