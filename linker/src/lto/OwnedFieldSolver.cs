namespace Corsac.Lang.Lto;

/// <summary>The fields a closed image's link found own what they hold, as every regenerated unit applies them.</summary>
public sealed class OwnedFieldFacts
{
    /// <summary>Each owned field, by name, and its byte offset: a store into it frees what it replaces.</summary>
    public Dictionary<string, long> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The owned fields some store fills: the owner types' owned-field maps
    /// (descriptor word 11), made by whichever unit defines the type.
    /// </summary>
    public HashSet<string> Mapped { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Functions, and virtual call symbols reaching one, that hand back what
    /// an owned field holds: a call to one is a read of the field.
    /// </summary>
    public HashSet<string> Borrowers { get; } = new(StringComparer.Ordinal);
    /// <summary>Functions that store into an owned field: their bodies are not imported into other units.</summary>
    public HashSet<string> Writers { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// OWNED FIELDS OVER EVERY UNIT: Escape's OwnedFields for a whole program,
/// answered from the units' OwnedFieldHints alone -- never their IR -- as a
/// flat compile answers it from the module. What a unit judged by itself
/// stands; what it left to the link is settled here: which constructors
/// store only into new objects, which parameters every caller hands over,
/// which functions hand back what an owned field holds (and so make every
/// call to them a read of it), and which may replace a field while a read of
/// it is live, over the whole call graph. Every order is fixed, so the answer
/// is the same wherever it is computed.
/// </summary>
public static class OwnedFieldSolver
{
    /// <summary>How deep a sink may be handed on, as a flat compile bounds it.</summary>
    private const int SinkDepth = 7;

    /// <summary>
    /// The owned fields, or null when a unit did not judge them. `entry` is
    /// the image's entry, which no call reaches.
    /// </summary>
    public static OwnedFieldFacts? Solve(IReadOnlyList<LifetimeHints> units, LifetimeSolver solver,
        IReadOnlyDictionary<string, string[]> virtuals, string entry, Action<string>? report = null)
    {
        if (units.Count == 0 || units.Any(unit => unit.Owned is null)) return null;
        List<OwnedFieldHints> all = units.Select(unit => unit.Owned!).ToList();

        HashSet<string> addressed = new(StringComparer.Ordinal), codeNamed = new(StringComparer.Ordinal), slotted = new(StringComparer.Ordinal);
        bool unresolvedVirtual = false;
        foreach (OwnedFieldHints unit in all)
        {
            addressed.UnionWith(unit.Addressed); codeNamed.UnionWith(unit.CodeNamed); slotted.UnionWith(unit.Slotted);
            unresolvedVirtual |= unit.UnresolvedVirtual || unit.VirtualNames().Any(name => !virtuals.ContainsKey(name));
        }
        bool IsVirtualTarget(string name) => slotted.Contains(name) && !codeNamed.Contains(name) && !unresolvedVirtual;
        static bool IsVirtual(string name) => name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal);

        // Which units call each function directly: every one of them must
        // agree for a constructor or a sink.
        Dictionary<string, List<int>> directCallers = new(StringComparer.Ordinal);
        for (int u = 0; u < all.Count; u++)
        {
            HashSet<string> called = new(StringComparer.Ordinal);
            foreach (OwnedFunctionRecord function in all[u].Functions.Values)
                foreach (string call in function.Calls) if (!IsVirtual(call)) called.Add(call);
            foreach (string call in called.Order(StringComparer.Ordinal))
            {
                if (!directCallers.TryGetValue(call, out List<int>? list)) directCallers[call] = list = new();
                list.Add(u);
            }
        }

        // CONSTRUCTORS' OWN STORES: least fixed point, outward from `new`.
        HashSet<string> freshThis = new(StringComparer.Ordinal);
        List<string> constructors = directCallers.Keys.Where(name => !addressed.Contains(name)
            && directCallers[name].All(u => all[u].FreshFirst.ContainsKey(name))).Order(StringComparer.Ordinal).ToList();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (string name in constructors)
                if (!freshThis.Contains(name) && directCallers[name].All(u => all[u].FreshFirst[name].All(freshThis.Contains)))
                { freshThis.Add(name); grew = true; }
        }

        // SINKS: least fixed point, as deep as a flat compile follows them.
        HashSet<(string, int)> sinks = new();
        HashSet<(string, int)> wanted = new();
        foreach (OwnedFieldHints unit in all)
        {
            foreach (OwnedFieldRecord field in unit.Fields.Values) wanted.UnionWith(field.Sinks);
            foreach (OwnedSink sink in unit.Sinks.Values) wanted.UnionWith(sink.Sinks);
        }
        List<(string Callee, int Argument)> sinkOrder = wanted.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2).ToList();
        for (int round = 0; round < SinkDepth; round++)
        {
            bool grew = false;
            foreach ((string callee, int argument) in sinkOrder)
            {
                if (sinks.Contains((callee, argument)) || addressed.Contains(callee) || callee == entry
                    || !directCallers.TryGetValue(callee, out List<int>? callers)) continue;
                if (callers.All(u => !all[u].Kept.Contains((callee, argument))
                        && (!all[u].Sinks.TryGetValue((callee, argument), out OwnedSink? sink)
                            || solver.Holds(sink.Needs) && sink.Sinks.All(sinks.Contains))))
                { sinks.Add((callee, argument)); grew = true; }
            }
            if (!grew) break;
        }

        // THE CALL GRAPH, reversed, for who may replace a field: numbered
        // once, a virtual call's symbol standing for its overrides.
        Dictionary<string, int> number = new(StringComparer.Ordinal);
        List<List<int>> callersOf = new();
        int Node(string name)
        {
            if (number.TryGetValue(name, out int at)) return at;
            number[name] = callersOf.Count; callersOf.Add(new List<int>());
            return callersOf.Count - 1;
        }
        foreach (OwnedFieldHints unit in all)
            foreach ((string name, OwnedFunctionRecord function) in unit.Functions)
            {
                int caller = Node(name);
                foreach (string call in function.Calls) callersOf[Node(call)].Add(caller);
            }
        foreach ((string symbol, string[] targets) in virtuals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (number.ContainsKey(symbol)) foreach (string target in targets) callersOf[Node(target)].Add(Node(symbol));
        Dictionary<string, List<string>> writersOf = new(StringComparer.Ordinal);
        void Writer(string field, string function)
        {
            if (!writersOf.TryGetValue(field, out List<string>? list)) writersOf[field] = list = new();
            list.Add(function);
        }
        foreach (OwnedFieldHints unit in all)
            foreach ((string name, OwnedFunctionRecord function) in unit.Functions)
            {
                foreach (string field in function.Writes) Writer(field, name);
                if (!freshThis.Contains(name)) foreach (string field in function.InitWrites) Writer(field, name);
            }
        bool[] seen = new bool[callersOf.Count];
        List<int> touched = new();
        // Whether any of `danger` may store into `field`: the functions that
        // do, and every function that may call one of them.
        bool MayWrite(string field, HashSet<string> danger)
        {
            if (danger.Count == 0 || !writersOf.TryGetValue(field, out List<string>? writers)) return false;
            HashSet<int> targets = new();
            foreach (string d in danger) if (number.TryGetValue(d, out int n)) targets.Add(n);
            if (targets.Count == 0) return false;
            foreach (int k in touched) seen[k] = false;
            touched.Clear();
            Stack<int> work = new();
            foreach (string w in writers) work.Push(Node(w));
            if (seen.Length < callersOf.Count) Array.Resize(ref seen, callersOf.Count);
            while (work.TryPop(out int at))
            {
                if (seen[at]) continue;
                if (targets.Contains(at)) return true;
                seen[at] = true; touched.Add(at);
                foreach (int up in callersOf[at]) if (!seen[up]) work.Push(up);
            }
            return false;
        }

        // THE FIELDS, each as every unit found it.
        SortedDictionary<string, long> offsets = new(StringComparer.Ordinal);
        HashSet<string> refused = new(StringComparer.Ordinal), stored = new(StringComparer.Ordinal);
        void Refuse(string field, string why) { if (refused.Add(field)) report?.Invoke(field + " refused: " + why); }
        Dictionary<string, HashSet<string>> danger = new(StringComparer.Ordinal);
        foreach (OwnedFieldHints unit in all)
            foreach ((string field, OwnedFieldRecord record) in unit.Fields)
            {
                if (offsets.TryGetValue(field, out long known) && known != record.Offset) Refuse(field, "units disagree on its offset");
                offsets[field] = record.Offset;
                if (record.Stored) stored.Add(field);
                if (record.Refused) Refuse(field, "a unit refused it");
                else if (!solver.Holds(record.Needs)) Refuse(field, "needs " + Describe(record.Needs));
                else if (record.Sinks.FirstOrDefault(sink => !sinks.Contains(sink)) is { Callee: not null } lost)
                    Refuse(field, "stored from " + lost.Callee + " argument " + lost.Argument + ", which a caller does not hand over");
                if (!danger.TryGetValue(field, out HashSet<string>? set)) danger[field] = set = new(StringComparer.Ordinal);
                set.UnionWith(record.Danger);
            }

        // WHO HANDS BACK WHAT A FIELD HOLDS: the functions that said so, and
        // every function handing back what one of those returns.
        Dictionary<string, SortedSet<string>> borrows = new(StringComparer.Ordinal);
        bool Borrow(string function, string field)
        {
            if (!borrows.TryGetValue(function, out SortedSet<string>? set)) borrows[function] = set = new(StringComparer.Ordinal);
            return set.Add(field);
        }
        foreach (OwnedFieldHints unit in all)
            foreach ((string name, OwnedFunctionRecord function) in unit.Functions)
                foreach (string field in function.Borrows) Borrow(name, field);
        IEnumerable<string> Reached(string callee) => IsVirtual(callee)
            ? virtuals.TryGetValue(callee, out string[]? targets) ? targets : Array.Empty<string>()
            : new[] { callee };
        List<(string Callee, OwnedCallRead Read)> reads = all.SelectMany(unit => unit.CallReads.Select(pair => (pair.Key, pair.Value))).ToList();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach ((string callee, OwnedCallRead read) in reads)
            {
                if (read.ReturnedBy.Count == 0) continue;
                List<string> fields = Reached(callee).SelectMany(t => borrows.GetValueOrDefault(t) ?? new SortedSet<string>(StringComparer.Ordinal)).Distinct().ToList();
                foreach (string back in read.ReturnedBy) foreach (string field in fields) grew |= Borrow(back, field);
            }
        }
        foreach ((string function, SortedSet<string> fields) in borrows)
            if (addressed.Contains(function) && !IsVirtualTarget(function))
                foreach (string field in fields) Refuse(field, "handed back by " + function + ", whose address is taken");
        // Each call to a borrower is a read of what it hands back.
        foreach ((string callee, OwnedCallRead read) in reads)
            foreach (string target in Reached(callee))
                foreach (string field in borrows.GetValueOrDefault(target) ?? new SortedSet<string>(StringComparer.Ordinal))
                {
                    if (read.Refused) Refuse(field, "a read of " + callee + " lets it go");
                    else if (!solver.Holds(read.Needs)) Refuse(field, "a read of " + callee + " needs " + Describe(read.Needs));
                    else if (read.DangerFields.Contains(field)) Refuse(field, "a read of " + callee + " is live across a store into it");
                    if (!danger.TryGetValue(field, out HashSet<string>? set)) danger[field] = set = new(StringComparer.Ordinal);
                    set.UnionWith(read.Danger);
                }

        OwnedFieldFacts facts = new();
        foreach ((string field, long offset) in offsets)
        {
            if (refused.Contains(field)) continue;
            if (MayWrite(field, danger[field])) { report?.Invoke(field + " refused: a read is live across a call that may store into it"); continue; }
            facts.Fields[field] = offset;
            if (stored.Contains(field)) facts.Mapped.Add(field);
        }
        foreach ((string function, SortedSet<string> fields) in borrows)
            if (fields.Any(facts.Fields.ContainsKey)) facts.Borrowers.Add(function);
        foreach ((string symbol, string[] targets) in virtuals)
            if (targets.Any(facts.Borrowers.Contains)) facts.Borrowers.Add(symbol);
        foreach (OwnedFieldHints unit in all)
            foreach ((string name, OwnedFunctionRecord function) in unit.Functions)
                if (function.Writes.Concat(function.InitWrites).Any(facts.Fields.ContainsKey)) facts.Writers.Add(name);
        return facts;

        string Describe(LifetimeCondition c)
        {
            IEnumerable<string> stays = c.Stays.Where(s => solver.Escapes(s.Callee, s.Argument)).Select(s => s.Callee + " keeping nothing of argument " + s.Argument);
            IEnumerable<string> fresh = c.Fresh.Where(name => !solver.IsFresh(name)).Select(name => name + " fresh");
            return string.Join(", ", stays.Concat(fresh).Take(3));
        }
    }
}
