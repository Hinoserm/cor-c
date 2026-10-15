using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHETHER A CLOSED IMAGE NEEDS A COLLECTOR, decided over every unit's
/// records (HeapRecords) as a whole program decides it over its one module
/// (Escape.CollectorSites, OnceRun, Reached): from the entry, over every call
/// and every address code and data name, the allocations left to the
/// collector that are not
///   - stored, in a static initialiser on no cycle, into a static nothing
///     else writes (a table for the life of the program, PermanentStatics);
///   - thrown, or made in a block that ends in a throw, where every catch
///     the program reaches runs at most once and it reaches no async body
///     (the throw budget: boundedly many unwinds, and what they leave);
///   - made at most once in a run: in the entry or a static initialiser, or
///     a function every direct call to which is made once from one that is,
///     on no cycle, and whose address nothing takes; or behind a latch that
///     lets its block through once;
///   - a fresh result every call that reaches its function frees.
/// None left: the program needs no collector, and every unit is made again
/// with the manual heap in the collector's place (Module.LinkHeap).
/// </summary>
public static class HeapDecision
{
    public sealed class Answer
    {
        public bool NeedsHeap { get; init; }
        /// <summary>The allocations a collector is still needed for, "function: allocator", in the order found.</summary>
        public List<string> Sites { get; } = new();
        /// <summary>Why the answer is a collector without looking at sites: a unit's own source calls into it.</summary>
        public string? Because { get; set; }
        /// <summary>For --alloc-report: each site reached and excused, and why.</summary>
        public List<string> Excused { get; } = new();
        /// <summary>For --alloc-report: whether unwinds are bounded, and the fields that leak.</summary>
        public string Catches { get; set; } = "";
    }

    private readonly record struct Node(int Unit, string Name);

    /// <summary>
    /// The decision. <paramref name="units"/> are the objects with IR, each
    /// with its records; <paramref name="virtuals"/>, a virtual call's
    /// targets the image holds, null where it cannot say (VirtualTargets.Resolve); <paramref name="ancestry"/>,
    /// every type each of some types is (VirtualTargets.Ancestry); <paramref name="ownedFields"/>,
    /// the fields the whole program found own what they hold (OwnedFieldSolver).
    /// </summary>
    public static Answer Decide(List<(string Name, ObjectFile Object)> inputs, List<(ObjectFile Object, HeapRecords Records)> units,
        string entry, Func<IEnumerable<string>, HashSet<string>> ancestry, IReadOnlySet<string>? ownedFields = null,
        IReadOnlyCollection<string>? foreign = null)
    {
        // What code without IR names: run from where nothing here follows,
        // with anything, as often as it likes -- roots, and taken addresses.
        IReadOnlyCollection<string> outside = foreign ?? Array.Empty<string>();
        // A fresh object stored into a field the whole program found owns
        // what it holds is the field's owner's to free (HeapEvent.Held) --
        // unless a store the program reaches may replace a value of it that
        // nobody frees, more than once (the fields that leak, below).
        HashSet<string> leaky = new(StringComparer.Ordinal);
        bool Held(HeapEvent e) => e.Held is string field && ownedFields?.Contains(field) == true && !leaky.Contains(field);
        foreach ((ObjectFile _, HeapRecords records) in units)
            if (records.CallsCollector) return new Answer { NeedsHeap = true, Because = "the program calls into the collector" };
        World world = new(inputs, units);

        // WHAT EVERY UNIT'S STATIC INITIALISERS ALONE WRITE (PermanentStatics).
        HashSet<string> writtenElsewhere = new(StringComparer.Ordinal);
        foreach ((ObjectFile _, HeapRecords records) in units) writtenElsewhere.UnionWith(records.StaticWrites);

        // REACHED, as Escape.Reached: from the entry, over calls, names,
        // resolved targets and data -- a descriptor's methods only through
        // calls when every virtual call reached is resolved. A static
        // initialiser is reached by the guard that calls it. The catches that
        // keep what they caught are those of what is reached.
        //
        // AND ONLY THE TYPES IT MAKES (rapid type analysis, VirtualTargets.Made):
        // a virtual call runs the overrides of the types something reached
        // names -- a `new` stamps its object with its descriptor, data that
        // holds a type names it -- grown with the walk until it holds still.
        // Every override of every type the units define, as their IR named
        // them before the late passes, reached an exception's Message from a
        // static initialiser's failure path and XML's ToString from there: a
        // whole program's module holds only what its entry reaches, and its
        // virtual calls run on that.
        List<Node> entryRoots = world.Roots(entry, initialisers: false).ToList();
        (HashSet<Node> reached, HashSet<string> addressed, Func<string, string[]?> virtuals, bool everyMethod) = ReachFrom(world, inputs, units, entryRoots, outside);
        addressed.UnionWith(outside);

        // Once, over the same reach: a caller the program cannot reach runs
        // never. A whole program's module holds only what its entry reaches;
        // a unit holds every method of its types, and every one was a caller.
        Once once = new(world, entry, reached, addressed);
        // A PROGRAM THAT SAID IT RUNS WITHOUT A COLLECTOR gets none, as a
        // whole program's does: what is left is never given back.
        bool none = units.Any(unit => unit.Records.NoCollector);

        // THE THROW BUDGET (Escape.RepeatingCatches): a reached catch that can
        // run more than once, or a reached async body, lets unwinds come round
        // without bound, and what they leave behind with them.
        List<string> repeating = new();
        foreach (Node node in reached)
        {
            HeapFunction f = world.Record(node);
            if (f.Async) repeating.Add(node.Name + ": an async body");
            foreach (HeapEvent e in f.Events)
                if (e.Kind == HeapEventKind.Catch && !once.RunsOnce(node.Name, e)) repeating.Add(node.Name + ": a catch of " + e.Name + " that can run more than once");
        }
        if (repeating.Count > 0)
        {
            Answer unbounded = new() { NeedsHeap = !none, Because = (none ? "--no-collector; " : "") + "unwinds unbounded: " + string.Join("; ", repeating.Take(4)) };
            unbounded.Sites.AddRange(repeating);
            return unbounded;
        }
        // THE FIELDS THAT LEAK (Escape.LeakyFields): a reached store that may
        // replace what nobody frees, more than once, not into what is thrown.
        foreach (Node node in reached)
            foreach (HeapEvent e in world.Record(node).Events)
                if (e.Kind == HeapEventKind.Store && (e.Flags & HeapFlags.AtThrow) == 0 && !once.RunsOnce(node.Name, e)) leaky.Add(e.Name);

        // THE COLLECTOR'S SITES (Escape.CollectorSites): from the entry, each
        // function reached as through a call that frees its fresh result or
        // not (unowned, the stronger), a trapping block deciding nothing.
        Dictionary<Node, bool> state = new();
        Dictionary<Node, Node> cameFrom = new();
        Node? current = null;
        Stack<(Node Node, bool Unowned)> work = new();
        HashSet<(Node, int)> counted = new();
        List<string> sites = new(), excused = new();
        void Reach(Node? target, bool unowned)
        {
            if (target is not Node node) return;
            if (state.TryGetValue(node, out bool was) && (was || !unowned)) return;
            state[node] = unowned;
            if (current is Node by) cameFrom[node] = by;
            work.Push((node, unowned));
        }
        // An entry without records -- code with no IR -- could call anything.
        List<Node> roots = world.Roots(entry, initialisers: false).ToList();
        if (roots.Count == 0) return new Answer { NeedsHeap = true, Because = "the entry " + entry + " has no records" };
        HashSet<(ObjectFile, string)> dataSeen = new();
        foreach (Node root in roots) Reach(root, true);
        foreach (string name in outside) Reach(world.Resolve(-1, name, f => Reach(f, true), everyMethod, dataSeen), true);
        while (work.Count > 0)
        {
            (Node node, bool unowned) = work.Pop();
            if (state[node] != unowned) continue;
            current = node;
            HeapFunction f = world.Record(node);
            for (int k = 0; k < f.Events.Count; k++)
            {
                HeapEvent e = f.Events[k];
                // A trapping block's own allocations are made on the way to one of
                // boundedly many unwinds; its call that ends the program is not
                // followed, and its throw is (Escape.CollectorSites).
                if ((e.Flags & HeapFlags.Dying) != 0) continue;
                bool trapping = (e.Flags & HeapFlags.Trapping) != 0;
                bool origin = (e.Flags & HeapFlags.Origin) != 0;
                switch (e.Kind)
                {
                    case HeapEventKind.Call:
                        if ((e.Flags & HeapFlags.Site) != 0 && !trapping)
                        {
                            string? why = Held(e) ? "held by " + e.Held
                                : e.Permanent is not null && !writtenElsewhere.Contains(e.Permanent) ? "permanent in " + e.Permanent
                                : e.Thrown is not null ? "thrown " + e.Thrown + ", one of boundedly many unwinds"
                                : once.RunsOnce(node.Name, e) ? "runs once"
                                : origin && !unowned ? "handed back to a caller that frees it" : null;
                            if (why is null && counted.Add((node, k))) sites.Add(node.Name + ": " + e.Name + " (reached: " + Path(node) + ")");
                            else if (why is not null) excused.Add(node.Name + ": " + e.Name + ": " + why);
                        }
                        if ((e.Flags & HeapFlags.OwnedAllocation) == 0)
                            Reach(world.Resolve(node.Unit, e.Name, f => Reach(f, true), everyMethod, dataSeen),
                                (e.Flags & HeapFlags.OwnedResult) != 0 || Held(e) ? false : origin ? unowned : true);
                        break;
                    case HeapEventKind.Named or HeapEventKind.Target when !e.Name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal):
                        Reach(world.Resolve(node.Unit, e.Name, f => Reach(f, true), everyMethod, dataSeen), true);
                        break;
                    case HeapEventKind.Virtual or HeapEventKind.Target:
                        if (virtuals(e.Name) is string[] targets)
                            foreach (string t in targets) Reach(world.Resolve(node.Unit, t, f => Reach(f, true), everyMethod, dataSeen), true);
                        break;
                }
            }
        }
        string Path(Node node)
        {
            List<string> chain = new() { node.Name };
            for (Node at = node; chain.Count < 10 && cameFrom.TryGetValue(at, out Node up) && !chain.Contains(up.Name); at = up) chain.Add(up.Name);
            return string.Join(" <- ", chain);
        }
        Answer answer = new()
        {
            NeedsHeap = sites.Count > 0 && !none, Because = none && sites.Count > 0 ? "--no-collector" : null,
            Catches = "unwinds bounded; fields that leak: " + (leaky.Count == 0 ? "none" : string.Join(", ", leaky.Order(StringComparer.Ordinal).Take(12)))
                + " (" + reached.Count + " functions reached)",
        };
        answer.Sites.AddRange(sites);
        answer.Excused.AddRange(excused.Distinct());
        return answer;
    }

    /// <summary>
    /// What the program reaches (Decide's REACHED): from `roots`, and the
    /// names `rootNames` -- functions or data -- over calls, names, data and
    /// virtual calls resolved by the types what is reached makes, grown
    /// until it holds still; every method of a reached type wherever a
    /// virtual call cannot be resolved.
    /// </summary>
    private static (HashSet<Node> Reached, HashSet<string> Addressed, Func<string, string[]?> Virtuals, bool EveryMethod) ReachFrom(
        World world, List<(string Name, ObjectFile Object)> inputs, List<(ObjectFile Object, HeapRecords Records)> units,
        List<Node> roots, IReadOnlyCollection<string> rootNames)
    {
        List<string> virtualNames = units.SelectMany(unit => unit.Records.Functions).SelectMany(function => function.Events)
            .Where(e => (e.Kind == HeapEventKind.Virtual || e.Name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)) && e.Name != "*")
            .SelectMany(e => e.Name.Contains('@') ? new[] { e.Name, VirtualTargets.Plain(e.Name) } : new[] { e.Name })
            .Distinct(StringComparer.Ordinal).ToList();
        VirtualTargets.Made made = new();
        Dictionary<string, string[]> resolved = new(StringComparer.Ordinal);
        string[]? virtuals(string name) => resolved.TryGetValue(name, out string[]? found) ? found
            : name.Contains('@') && resolved.TryGetValue(VirtualTargets.Plain(name), out found) ? found : null;
        bool everyMethod = false;
        HashSet<Node> reached;
        HashSet<string> addressed;
        while (true)
        {
            resolved = VirtualTargets.Resolve(inputs, virtualNames, made);
            bool unresolved = false;
            HashSet<string> named = new(StringComparer.Ordinal);
            addressed = new(StringComparer.Ordinal);
            reached = world.Walk(roots, everyMethod, addressed, (node, record) =>
            {
                List<string> next = new();
                foreach (HeapEvent e in record.Events)
                    switch (e.Kind)
                    {
                        case HeapEventKind.Call or HeapEventKind.Named or HeapEventKind.Target when !e.Name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal):
                            if (e.Kind == HeapEventKind.Named && World.IsDescriptor(e.Name)) named.Add(e.Name);
                            next.Add(e.Name); break;
                        case HeapEventKind.Virtual or HeapEventKind.Target:
                            if (virtuals(e.Name) is string[] targets) next.AddRange(targets); else unresolved = true;
                            break;
                    }
                return next;
            }, named, rootNames);
            if (unresolved && !everyMethod) { everyMethod = true; continue; }
            int before = made.Descriptors.Count;
            made.Descriptors.UnionWith(named);
            foreach (string name in rootNames) if (World.IsDescriptor(name)) made.Descriptors.Add(name);
            if (made.Descriptors.Count == before) break;
        }
        return (reached, addressed, virtuals, everyMethod);
    }

    /// <summary>
    /// The functions a closed image reaches, each by the object whose records
    /// hold it, from its entry and the names `roots` (what the link itself
    /// calls, what code without IR names): the reach the whole program's
    /// ForeignThrows judges throws over (IrLinkOptimizer.ForeignCatchable).
    /// </summary>
    public static HashSet<(ObjectFile Object, string Function)> Reach(List<(string Name, ObjectFile Object)> inputs,
        List<(ObjectFile Object, HeapRecords Records)> units, string entry, IReadOnlyCollection<string> roots)
    {
        World world = new(inputs, units);
        var (reached, _, _, _) = ReachFrom(world, inputs, units, world.Roots(entry, initialisers: false).ToList(), roots);
        return reached.Select(node => (world.UnitObject(node.Unit), node.Name)).ToHashSet();
    }

    /// <summary>
    /// The edges a program without a collector does not have, for the
    /// link's reachability of its second making (IrReachability): from a
    /// function whose compile's records say it no longer refers to a name (HeapRecords.Gone).
    /// </summary>
    public static Func<ObjectFile, string, string, bool> GoneEdges(List<(ObjectFile Object, HeapRecords Records)> units)
    {
        Dictionary<ObjectFile, Dictionary<string, HashSet<string>>> gone = new(ReferenceEqualityComparer.Instance);
        foreach ((ObjectFile obj, HeapRecords records) in units)
        {
            Dictionary<string, HashSet<string>> byFunction = new(StringComparer.Ordinal);
            foreach ((string function, string[] names) in records.Gone) byFunction[function] = new(names, StringComparer.Ordinal);
            gone[obj] = byFunction;
        }
        return (obj, from, to) => gone.TryGetValue(obj, out var byFunction) && byFunction.TryGetValue(from, out HashSet<string>? names) && names.Contains(to);
    }

    /// <summary>The image as the walks see it: functions by their records, data by its relocations.</summary>
    private sealed class World
    {
        private readonly List<(ObjectFile Object, HeapRecords Records)> _units;
        private readonly Dictionary<ObjectFile, int> _unitOf = new(ReferenceEqualityComparer.Instance);
        private readonly List<Dictionary<string, HeapFunction>> _functions = new();
        private readonly Dictionary<string, ObjectFile> _owners = new(StringComparer.Ordinal);
        private readonly Dictionary<ObjectFile, Dictionary<string, Symbol>> _locals = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Section, (long[] Offsets, string[] Targets)> _relocations = new(ReferenceEqualityComparer.Instance);

        public World(List<(string Name, ObjectFile Object)> inputs, List<(ObjectFile Object, HeapRecords Records)> units)
        {
            _units = units;
            for (int u = 0; u < units.Count; u++)
            {
                _unitOf[units[u].Object] = u;
                Dictionary<string, HeapFunction> byName = new(StringComparer.Ordinal);
                foreach (HeapFunction f in units[u].Records.Functions) byName.TryAdd(f.Name, f);
                _functions.Add(byName);
            }
            foreach ((string _, ObjectFile obj) in inputs)
            {
                Dictionary<string, Symbol> locals = new(StringComparer.Ordinal);
                foreach (Symbol symbol in obj.Symbols)
                {
                    if (!symbol.IsDefined) continue;
                    if (symbol.Global) _owners.TryAdd(symbol.Name, obj); else locals.TryAdd(symbol.Name, symbol);
                }
                _locals[obj] = locals;
            }
        }

        public HeapFunction Record(Node node) => _functions[node.Unit][node.Name];

        public ObjectFile UnitObject(int unit) => _units[unit].Object;

        /// <summary>The entry, and with `initialisers` every static initialiser of every unit (OnceRun, Reached).</summary>
        public IEnumerable<Node> Roots(string entry, bool initialisers)
        {
            if (Resolve(-1, entry) is Node main) yield return main;
            if (!initialisers) yield break;
            for (int u = 0; u < _functions.Count; u++)
                foreach (string name in _functions[u].Keys)
                    if (name.Contains("StaticInit$", StringComparison.Ordinal)) yield return new Node(u, name);
        }

        /// <summary>
        /// A name as `unit` refers to it: its own record, else the record of
        /// the unit that defines it; null for data or for code without records.
        /// </summary>
        public Node? Resolve(int unit, string name)
        {
            // A unit's own local first; a global, as the copy the link keeps --
            // the first definer's (DefinitionCoalescer) -- whose record may say
            // other than a coalesced copy's; a unit's own copy the link took out
            // of its object, as its archive still holds it.
            if (unit >= 0 && _locals.TryGetValue(_units[unit].Object, out var locals) && locals.ContainsKey(name) && _functions[unit].ContainsKey(name))
                return new Node(unit, name);
            if (_owners.TryGetValue(name, out ObjectFile? owner) && _unitOf.TryGetValue(owner, out int u) && _functions[u].ContainsKey(name)) return new Node(u, name);
            if (unit >= 0 && _functions[unit].ContainsKey(name)) return new Node(unit, name);
            return null;
        }

        /// <summary>
        /// A name as `unit` refers to it, a function's node; data is walked
        /// here, every function it names handed to `function` -- a
        /// descriptor's methods only with `everyMethod` (their calls reach them).
        /// </summary>
        public Node? Resolve(int unit, string name, Action<Node?> function, bool everyMethod, HashSet<(ObjectFile, string)> seen, HashSet<string>? addressed = null,
            HashSet<string>? types = null)
        {
            if (Resolve(unit, name) is Node node) return node;
            ObjectFile? from = unit >= 0 ? _units[unit].Object : null;
            Stack<(ObjectFile? From, string Name)> pending = new();
            pending.Push((from, name));
            while (pending.TryPop(out var item))
            {
                if (Data(item.From, item.Name) is not (ObjectFile obj, Symbol symbol) || !seen.Add((obj, symbol.Name))) continue;
                bool descriptor = IsDescriptor(symbol.Name);
                int at = _unitOf.TryGetValue(obj, out int u) ? u : -1;
                foreach (string target in Relocations(symbol))
                {
                    // Every address the data the program reaches holds (OnceRun's addressed).
                    addressed?.Add(target);
                    // A type data holds, but a descriptor's own words (its
                    // ancestors, its display): an object may be made of it.
                    if (!descriptor && IsDescriptor(target)) types?.Add(target);
                    if (Resolve(at, target) is Node method)
                    {
                        if (everyMethod || !descriptor) function(method);
                        continue;
                    }
                    pending.Push((obj, target));
                }
            }
            return null;
        }

        /// <summary>The program's data a name is, as `from` refers to it: none for code, the link's own tables, or nothing defined.</summary>
        private (ObjectFile, Symbol)? Data(ObjectFile? from, string name)
        {
            Symbol? symbol = null;
            ObjectFile? obj = null;
            if (from is not null && _locals.TryGetValue(from, out var locals) && locals.TryGetValue(name, out Symbol? local)) { symbol = local; obj = from; }
            else if (_owners.TryGetValue(name, out ObjectFile? owner))
            {
                obj = owner;
                symbol = owner.Symbols.FirstOrDefault(s => s.IsDefined && s.Global && s.Name == name);
            }
            if (symbol?.Section is not Section section || !IsProgramData(section)) return null;
            return (obj!, symbol);
        }

        /// <summary>A section of the program's own data: not code, nor a table or note of the compiler's (.corsac.*).</summary>
        public static bool IsProgramData(Section section)
            => section.Kind is not (SectionKind.Code or SectionKind.Note) && !section.Name.StartsWith(".corsac", StringComparison.Ordinal) && !section.Name.StartsWith(".note", StringComparison.Ordinal);

        public IEnumerable<string> Relocations(Symbol symbol)
        {
            Section section = symbol.Section!;
            if (!_relocations.TryGetValue(section, out var sorted))
            {
                Relocation[] all = section.Relocs.OrderBy(r => r.Offset).ToArray();
                sorted = (all.Select(r => (long)r.Offset).ToArray(), all.Select(r => r.Symbol).ToArray());
                _relocations[section] = sorted;
            }
            int at = Array.BinarySearch(sorted.Offsets, symbol.Offset);
            if (at < 0) at = ~at;
            else while (at > 0 && sorted.Offsets[at - 1] == symbol.Offset) at--;
            long end = symbol.Size > 0 ? symbol.Offset + symbol.Size : symbol.Offset + 1;
            for (; at < sorted.Offsets.Length && sorted.Offsets[at] < end; at++) yield return sorted.Targets[at];
        }

        /// <summary>
        /// A plain walk from `roots` over the names `next` gives of each
        /// function reached; data is walked into, its functions reached
        /// (a descriptor's only with `everyMethod`).
        /// </summary>
        public HashSet<Node> Walk(IEnumerable<Node> roots, bool everyMethod, HashSet<string>? addressed, Func<Node, HeapFunction, IEnumerable<string>> next,
            HashSet<string>? types = null, IEnumerable<string>? rootNames = null)
        {
            HashSet<Node> reached = new();
            Stack<Node> work = new();
            HashSet<(ObjectFile, string)> seen = new();
            void Add(Node? node) { if (node is Node n && reached.Add(n)) work.Push(n); }
            foreach (Node root in roots) Add(root);
            foreach (string name in rootNames ?? Enumerable.Empty<string>()) Add(Resolve(-1, name, Add, everyMethod, seen, addressed, types));
            while (work.TryPop(out Node node))
                foreach (string name in next(node, Record(node)))
                    Add(Resolve(node.Unit, name, Add, everyMethod, seen, addressed, types));
            return reached;
        }

        public IEnumerable<Node> AllFunctions()
        {
            for (int u = 0; u < _functions.Count; u++)
                foreach (string name in _functions[u].Keys) yield return new Node(u, name);
        }

        public static bool IsDescriptor(string name) => name.Length > 2 && name[1] == '_' && name[0] is 't' or 'q' or 'v' or 'b';
    }

    /// <summary>
    /// WHAT RUNS AT MOST ONCE IN A RUN (Escape.OnceRun): the entry and every
    /// static initialiser; a function every direct call to which, from code
    /// the program can run, is in a block on no cycle of a function that runs
    /// once, and whose address nothing takes; and every block a latch lets
    /// through once -- `if (done) return; done = true;` on a flag nothing
    /// sets but to true.
    /// </summary>
    private sealed class Once
    {
        private readonly HashSet<string> _once = new(StringComparer.Ordinal);
        private readonly HashSet<string> _latchOnly = new(StringComparer.Ordinal);

        public Once(World world, string entry, HashSet<Node> reached, HashSet<string> addressedByData)
        {
            HashSet<string> addressed = new(addressedByData, StringComparer.Ordinal);
            Dictionary<string, List<(string Caller, bool Once)>> calls = new(StringComparer.Ordinal);
            HashSet<string> sets = new(StringComparer.Ordinal), breaks = new(StringComparer.Ordinal);
            foreach (Node node in reached)
            {
                HeapFunction f = world.Record(node);
                sets.UnionWith(f.LatchSets); breaks.UnionWith(f.LatchBreaks);
                foreach (HeapEvent e in f.Events)
                {
                    if (e.Kind == HeapEventKind.Named) addressed.Add(e.Name);
                    if (e.Kind == HeapEventKind.Call && (e.Flags & HeapFlags.Direct) != 0)
                    {
                        if (!calls.TryGetValue(e.Name, out var list)) calls[e.Name] = list = new();
                        list.Add((node.Name, (e.Flags & HeapFlags.Once) != 0));
                    }
                }
            }
            foreach (string flag in sets) if (!breaks.Contains(flag)) _latchOnly.Add(flag);
            _once.Add(entry);
            List<HeapFunction> all = world.AllFunctions().Select(world.Record).ToList();
            foreach (HeapFunction f in all)
                if (IsStaticInitialiser(f.Name) && !f.Async) _once.Add(f.Name);
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (HeapFunction f in all)
                {
                    if (_once.Contains(f.Name) || f.Async || addressed.Contains(f.Name)
                        || !calls.TryGetValue(f.Name, out var sites) || sites.Count == 0) continue;
                    if (sites.All(site => _once.Contains(site.Caller) && site.Once))
                    {
                        _once.Add(f.Name);
                        changed = true;
                    }
                }
            }
        }

        public bool RunsOnce(string function, HeapEvent e)
            => _once.Contains(function) && (e.Flags & HeapFlags.Once) != 0 || e.Latches.Any(_latchOnly.Contains);

        private static bool IsStaticInitialiser(string name)
            => name.Contains("_StaticInit$", StringComparison.Ordinal) || name.Contains("_StaticInitBody$", StringComparison.Ordinal)
               || name.Contains("_StaticConstructorBody$", StringComparison.Ordinal);
    }
}
