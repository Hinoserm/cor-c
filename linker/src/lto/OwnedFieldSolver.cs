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
    /// The owned fields whose arrays own their elements (Escape's
    /// ArrayFieldElements): their bits in the owner's map say so.
    /// </summary>
    public HashSet<string> ArrayElements { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Functions, and virtual call symbols reaching one, that hand back what
    /// an owned field holds: a call to one is a read of the field.
    /// </summary>
    public HashSet<string> Borrowers { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The fields a collection handed to them gives its elements back
    /// through, by the collection's kind: every read of the field in the
    /// program proved (Opt.OwnedElements, through a field).
    /// </summary>
    public Dictionary<string, string> Elements { get; } = new(StringComparer.Ordinal);
    /// <summary>The parameters, by function, a collection handed to one of those fields goes through: the field each stores it into.</summary>
    public Dictionary<(string Callee, int Argument), string> ElementCallees { get; } = new();
    /// <summary>
    /// The owned fields a collection frees the value of itself as it replaces
    /// it (Opt.Escape, EscapeSelfFrees): no call is watched for replacing one.
    /// </summary>
    public HashSet<string> SelfFreed { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Whether the image opens regions: then what is stored into a self-freed
    /// field is made beside the object stored into (Runtime.AllocNear,
    /// Opt.RegionPointsTo.MakeStorageBeside) -- in its region when it is in
    /// the innermost one open, on the heap otherwise.
    /// </summary>
    public bool Beside { get; set; }
    /// <summary>The fields some unit kept the reads of from the inliner for that rule: where not proved, its calls are the inliner's again.</summary>
    public HashSet<string> ElementKept { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether there is nothing here for a unit to apply.</summary>
    public bool IsEmpty => Fields.Count == 0 && Elements.Count == 0 && ElementKept.Count == 0;
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
                // A function nothing calls directly, and whose address nothing
                // takes, is called by nobody: every caller hands over. One the
                // compile's inliner took into every caller is such a function,
                // its store judged where it was taken (`new Parser(tokens)`).
                if (sinks.Contains((callee, argument)) || addressed.Contains(callee) || callee == entry) continue;
                List<int> callers = directCallers.GetValueOrDefault(callee) ?? new List<int>();
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
                // WHAT AN UNHANDLED EXCEPTION'S REPORT CALLS IS NOBODY'S STORE:
                // Runtime.Unhandled prints and ends the process, so no read
                // live in any caller is ever used after it. Followed, its
                // virtual ToString reached the whole program -- every writer
                // of every field -- and the collector's grid test, a division
                // the barrier makes (Gc.Whole), put that under every store a
                // collection does: no Dictionary, List or Queue field owned.
                if (NeverReturns(name)) continue;
                foreach (string call in function.Calls) callersOf[Node(call)].Add(caller);
            }
        foreach ((string symbol, string[] targets) in virtuals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (number.ContainsKey(symbol)) foreach (string target in targets) callersOf[Node(target)].Add(Node(symbol));
        Dictionary<string, List<string>> writersOf = new(StringComparer.Ordinal);
        static bool NeverReturns(string function) => function.StartsWith("m_Runtime_Unhandled_", StringComparison.Ordinal);
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
        // Whether any of `danger` may store into `field`: the functions that
        // do, and every function that may call one of them.
        //
        // ONE PASS OVER THE CALL GRAPH, NOT ONE A FIELD. Each question walked
        // up from the field's writers through every caller until it met one
        // of `danger` or ran out: fields times the graph, and the compiler's
        // own link asks it of every field some unit owns. What the walks
        // found is what a function may store into, itself or through what it
        // calls -- the same set for every question -- so it is worked out
        // once, bottom-up over the graph's strongly connected parts (a cycle
        // stores into what any of its members does), for the fields that can
        // be asked about, and each question is then a lookup. The edges are
        // the walk's: a call, a virtual call's symbol to each override, and
        // nothing out of a function that never returns (NeverReturns).
        // The fields judged below (THE FIELDS), declared here for MayWrite.
        SortedDictionary<string, long> offsets = new(StringComparer.Ordinal);
        Dictionary<string, int>? asked = null;
        int[]? partOf = null;
        ulong[]?[]? storesBelow = null;
        bool MayWrite(string field, HashSet<string> danger)
        {
            if (danger.Count == 0 || !writersOf.ContainsKey(field)) return false;
            if (asked is null) (asked, partOf, storesBelow) = StoresBelow(offsets.Keys, writersOf, number, callersOf);
            if (!asked.TryGetValue(field, out int bit)) return false;
            foreach (string d in danger)
                if (number.TryGetValue(d, out int n) && storesBelow![partOf![n]] is { } bits && (bits[bit >> 6] & 1UL << (bit & 63)) != 0)
                    return true;
            return false;
        }

        // THE FIELDS, each as every unit found it.
        HashSet<string> refused = new(StringComparer.Ordinal), stored = new(StringComparer.Ordinal), selfFreed = new(StringComparer.Ordinal);
        void Refuse(string field, string why) { if (refused.Add(field)) report?.Invoke(field + " refused: " + why); }
        Dictionary<string, HashSet<string>> danger = new(StringComparer.Ordinal);
        Dictionary<string, SortedSet<string>> kinds = new(StringComparer.Ordinal), assumed = new(StringComparer.Ordinal);
        foreach (OwnedFieldHints unit in all)
            foreach ((string field, OwnedFieldRecord record) in unit.Fields)
            {
                if (offsets.TryGetValue(field, out long known) && known != record.Offset) Refuse(field, "units disagree on its offset");
                offsets[field] = record.Offset;
                if (record.Stored) stored.Add(field);
                if (record.SelfFreed) selfFreed.Add(field);
                if (record.Refused) Refuse(field, "a unit refused it");
                else if (!solver.Holds(record.Needs)) Refuse(field, "needs " + Describe(record.Needs));
                else if (record.Sinks.FirstOrDefault(sink => !sinks.Contains(sink)) is { Callee: not null } lost)
                    Refuse(field, "stored from " + lost.Callee + " argument " + lost.Argument + ", which a caller does not hand over");
                if (!danger.TryGetValue(field, out HashSet<string>? set)) danger[field] = set = new(StringComparer.Ordinal);
                set.UnionWith(record.Danger);
                if (!kinds.TryGetValue(field, out SortedSet<string>? stamps)) kinds[field] = stamps = new(StringComparer.Ordinal);
                stamps.UnionWith(record.Kinds);
                if (record.Assumes.Count > 0)
                {
                    if (!assumed.TryGetValue(field, out SortedSet<string>? taken)) assumed[field] = taken = new(StringComparer.Ordinal);
                    taken.UnionWith(record.Assumes);
                }
            }
        // A READ JUDGED AS OF ONE TYPE (a virtual call on it reaching that
        // type's method alone) holds only if every unit's stores put nothing
        // else in the field.
        foreach ((string field, SortedSet<string> taken) in assumed)
            if (taken.Count != 1 || kinds[field].Any(kind => !taken.Contains(kind)))
                Refuse(field, "a read was judged as " + string.Join(", ", taken) + ", and it holds " + string.Join(", ", kinds[field]));

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
                    // Any field's store, not only this one's: replacing a field
                    // frees the old value's own owned fields too.
                    else if (read.DangerFields.Count > 0) Refuse(field, "a read of " + callee + " is live across a store into a field");
                    if (!danger.TryGetValue(field, out HashSet<string>? set)) danger[field] = set = new(StringComparer.Ordinal);
                    set.UnionWith(read.Danger);
                }

        OwnedFieldFacts facts = new();
        foreach ((string field, long offset) in offsets)
        {
            if (refused.Contains(field)) continue;
            // Live across a virtual call no descriptor answers: it could be
            // anything, as an unresolved indirect call is to a flat compile.
            if (danger[field].FirstOrDefault(d => IsVirtual(d) && !virtuals.ContainsKey(d)) is string unknown)
            { report?.Invoke(field + " refused: a read is live across " + unknown + ", which the link could not resolve"); continue; }
            if (MayWrite(field, danger[field])) { report?.Invoke(field + " refused: a read is live across a call that may store into it"); continue; }
            facts.Fields[field] = offset;
            if (stored.Contains(field)) facts.Mapped.Add(field);
            if (selfFreed.Contains(field)) facts.SelfFreed.Add(field);
        }
        foreach ((string function, SortedSet<string> fields) in borrows)
            if (fields.Any(facts.Fields.ContainsKey)) facts.Borrowers.Add(function);
        foreach ((string symbol, string[] targets) in virtuals)
            if (targets.Any(facts.Borrowers.Contains)) facts.Borrowers.Add(symbol);

        // ITS ARRAYS OWNING THEIR ELEMENTS (Escape.ArrayFieldElements): an
        // owned field every unit that stores or reads it proved so, some
        // unit storing such an array, handed back by no function, what the
        // proofs need of the units holding, and nothing an element is live
        // across able to store into the field, nor any field stored into
        // meanwhile owned.
        foreach (string field in facts.Mapped.Order(StringComparer.Ordinal))
        {
            List<OwnedFieldRecord> records = all.Select(unit => unit.Fields.GetValueOrDefault(field)).OfType<OwnedFieldRecord>().ToList();
            if (records.Count == 0 || records.Any(r => r.ElementsRefused) || !records.Any(r => r.ElementArrays)) continue;
            if (borrows.Values.Any(fields => fields.Contains(field))) { report?.Invoke(field + " elements refused: a function hands it back"); continue; }
            if (records.FirstOrDefault(r => !solver.Holds(r.ElementNeeds)) is OwnedFieldRecord needing)
            { report?.Invoke(field + " elements refused: needs " + Describe(needing.ElementNeeds)); continue; }
            HashSet<string> elementDanger = new(records.SelectMany(r => r.ElementDanger), StringComparer.Ordinal);
            if (elementDanger.FirstOrDefault(d => IsVirtual(d) && !virtuals.ContainsKey(d)) is string unknown)
            { report?.Invoke(field + " elements refused: one is live across " + unknown + ", which the link could not resolve"); continue; }
            if (MayWrite(field, elementDanger)) { report?.Invoke(field + " elements refused: one is live across a call that may store into it"); continue; }
            if (records.SelectMany(r => r.ElementDangerFields).FirstOrDefault(facts.Fields.ContainsKey) is string into)
            { report?.Invoke(field + " elements refused: one is live across a store into " + into + ", which is owned"); continue; }
            facts.ArrayElements.Add(field);
        }
        Elements(all, solver, facts, report);
        return facts;

        string Describe(LifetimeCondition c)
        {
            IEnumerable<string> stays = c.Stays.Where(s => solver.Escapes(s.Callee, s.Argument)).Select(s => s.Callee + " keeping nothing of argument " + s.Argument);
            IEnumerable<string> fresh = c.Fresh.Where(name => !solver.IsFresh(name)).Select(name => name + " fresh");
            return string.Join(", ", stays.Concat(fresh).Take(3));
        }
    }

    /// <summary>
    /// For each part of the call graph (its strongly connected components,
    /// numbered by <c>partOf</c>), the fields among <paramref name="fields"/>
    /// it or anything it calls stores into, as bits by the number
    /// <c>asked</c> gives each field; null where it stores into none. The
    /// graph is <paramref name="callersOf"/> read the other way.
    /// </summary>
    private static (Dictionary<string, int> Asked, int[] PartOf, ulong[]?[] StoresBelow) StoresBelow(IEnumerable<string> fields,
        Dictionary<string, List<string>> writersOf, Dictionary<string, int> number, List<List<int>> callersOf)
    {
        Dictionary<string, int> asked = new(StringComparer.Ordinal);
        foreach (string field in fields) if (writersOf.ContainsKey(field)) asked[field] = asked.Count;
        int nodes = callersOf.Count, words = (asked.Count + 63) >> 6;
        // Callees of each function: the walk's edges, turned round.
        int[] calleeStart = new int[nodes + 1];
        foreach (List<int> callers in callersOf) foreach (int caller in callers) calleeStart[caller + 1]++;
        for (int n = 0; n < nodes; n++) calleeStart[n + 1] += calleeStart[n];
        int[] callees = new int[calleeStart[nodes]];
        int[] fill = (int[])calleeStart.Clone();
        for (int callee = 0; callee < nodes; callee++)
            foreach (int caller in callersOf[callee]) callees[fill[caller]++] = callee;

        // Tarjan's components, iteratively: a part is finished only after
        // every part it calls into, so its stores can take theirs.
        int[] partOf = new int[nodes], index = new int[nodes], low = new int[nodes];
        Array.Fill(partOf, -1); Array.Fill(index, -1);
        List<ulong[]?> parts = new();
        Stack<int> open = new();
        bool[] onOpen = new bool[nodes];
        Stack<(int Node, int Next)> walk = new();
        int counter = 0;
        // What each function stores into itself.
        ulong[]?[] own = new ulong[]?[nodes];
        foreach ((string field, int bit) in asked)
            foreach (string writer in writersOf[field])
                if (number.TryGetValue(writer, out int w)) (own[w] ??= new ulong[words])[bit >> 6] |= 1UL << (bit & 63);
        for (int root = 0; root < nodes; root++)
        {
            if (index[root] >= 0) continue;
            walk.Push((root, calleeStart[root]));
            index[root] = low[root] = counter++; open.Push(root); onOpen[root] = true;
            while (walk.Count > 0)
            {
                (int n, int next) = walk.Pop();
                if (next < calleeStart[n + 1])
                {
                    walk.Push((n, next + 1));
                    int c = callees[next];
                    if (index[c] < 0)
                    {
                        index[c] = low[c] = counter++; open.Push(c); onOpen[c] = true;
                        walk.Push((c, calleeStart[c]));
                    }
                    else if (onOpen[c]) low[n] = Math.Min(low[n], index[c]);
                    continue;
                }
                if (walk.Count > 0) { int up = walk.Peek().Node; low[up] = Math.Min(low[up], low[n]); }
                if (low[n] != index[n]) continue;
                // A whole part: its own stores and those of every part it
                // calls into, each finished already.
                int part = parts.Count;
                List<int> members = new();
                int m;
                do { m = open.Pop(); onOpen[m] = false; partOf[m] = part; members.Add(m); } while (m != n);
                ulong[]? bits = null;
                foreach (int member in members)
                {
                    if (own[member] is { } mine) Union(ref bits, mine, words);
                    for (int e = calleeStart[member]; e < calleeStart[member + 1]; e++)
                        if (partOf[callees[e]] is int below && below != part && parts[below] is { } theirs) Union(ref bits, theirs, words);
                }
                parts.Add(bits);
            }
        }
        return (asked, partOf, parts.ToArray());

        static void Union(ref ulong[]? into, ulong[] from, int words)
        {
            if (into is null) { into = (ulong[])from.Clone(); return; }
            for (int w = 0; w < words; w++) into[w] |= from[w];
        }
    }

    /// <summary>
    /// ELEMENTS OWNED THROUGH A FIELD OVER EVERY UNIT: Escape's
    /// FieldElementsProved for a whole program, from the units' hints. A
    /// field is proved when some unit hands it a collection whose elements
    /// are its own -- into the field itself, or to a parameter of another
    /// unit's function that stores it there and does nothing else with it --
    /// every such collection of one kind, and every unit that reads the
    /// field, or takes its address, proved every read it has of it as reads
    /// of that kind. A unit that reads it and judged nothing refuses it.
    /// </summary>
    private static void Elements(List<OwnedFieldHints> all, LifetimeSolver solver, OwnedFieldFacts facts, Action<string>? report)
    {
        // Each function's stored parameter, as every unit that defines it says.
        Dictionary<(string, int), string?> stored = new();
        foreach (OwnedFieldHints unit in all)
            foreach (((string callee, int argument), string field) in unit.StoredParameters)
                stored[(callee, argument)] = stored.TryGetValue((callee, argument), out string? known) && known != field ? null : field;
        // A hand-off whose condition fails is no hand-off: the collection
        // is not proved, and its unit marks nothing.
        SortedDictionary<string, string?> handed = new(StringComparer.Ordinal);
        void Hand(string field, OwnedElementRecord record)
        {
            if (!solver.Holds(record.Needs)) { report?.Invoke(field + " elements: a hand-off needs what does not hold"); return; }
            handed[field] = handed.TryGetValue(field, out string? known) && known != record.Kind ? null : record.Kind;
        }
        foreach (OwnedFieldHints unit in all)
        {
            foreach ((string field, OwnedElementRecord record) in unit.ElementHandOffs) Hand(field, record);
            foreach (((string callee, int argument), OwnedElementRecord record) in unit.ElementCalls)
                if (stored.GetValueOrDefault((callee, argument)) is string field) Hand(field, record);
            facts.ElementKept.UnionWith(unit.ElementKept);
        }
        foreach ((string field, string? kind) in handed)
        {
            if (kind is null) { report?.Invoke(field + " elements refused: collections of two kinds are handed to it"); continue; }
            if (all.FirstOrDefault(unit => unit.Loaded.Contains(field)
                    && !(unit.ElementReads.TryGetValue(field, out OwnedElementRecord? reads) && reads.Kind == kind && solver.Holds(reads.Needs))) is not null)
            { report?.Invoke(field + " elements refused: a unit reads it otherwise"); continue; }
            facts.Elements[field] = kind;
        }
        foreach (((string callee, int argument), string? field) in stored)
            if (field is not null && facts.Elements.ContainsKey(field)) facts.ElementCallees[(callee, argument)] = field;
    }
}
