using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// REGIONS OVER EVERY UNIT: RegionPointsTo for a separately compiled closed
/// image, answered from the units' RegionHints alone -- never their IR.
///
/// Andersen's inclusion analysis, field-sensitive -- a location is an object
/// and a byte offset into it, or any offset -- and context-insensitive: each
/// function is one set of nodes, whoever calls it. Sets are sparse bitmaps,
/// copy cycles are collapsed as they are found, and a virtual call reaches
/// every override the image holds for its slot (VirtualTargets).
///
/// Then the boundaries, chosen as RegionPointsTo.Nearest chooses them: for
/// each allocation, the nearest caller -- not the entry, not recursive, not
/// a type's initialiser or an async body -- whose return it is proved not to
/// outlive. A site is made in a region only if every boundary that can be
/// open above it, however far up, is proved to outlive none of its objects
/// (Runtime.AllocRegion takes whichever is open innermost).
///
/// SOUND WHERE IT CANNOT SEE: a call nobody can name, or of a function no
/// unit summarised, hands its arguments to the unknown object and gets it
/// back; once there is one, every function whose address is taken may be
/// called from it, with anything; a function code outside the IR names is
/// called with anything. Past a fixed budget, the same on every machine, it
/// gives up, and nothing is made in a region.
/// </summary>
public static class RegionSolver
{
    /// <summary>The most nodes, locations held, and locations made, before giving up.</summary>
    public const int NodeBudget = 4_000_000, HeldBudget = 12_000_000, LocationBudget = 2_000_000;
    /// <summary>The most objects walked judging what outlives the boundaries, and the boundaries' calls walked.</summary>
    public const long JudgeBudget = 200_000_000;

    /// <summary>Each unit's answer, by its place in <paramref name="units"/>; null when it gave up.</summary>
    public static RegionFacts?[]? Solve(IReadOnlyList<RegionHints> units, Dictionary<string, string[]> virtuals, string entry,
        IReadOnlySet<string> foreign, string? report, Func<int, string, bool>? live = null)
    {
        Solver solver = new(units, virtuals, entry, foreign, report, live);
        return solver.Run();
    }

    /// <summary>Every virtual symbol the units' calls name: the link resolves them.</summary>
    public static IEnumerable<string> VirtualNames(IEnumerable<RegionHints> units)
    {
        foreach (RegionHints unit in units)
            foreach (RegionFunction function in unit.Functions)
                foreach (RegionCall call in function.Calls)
                    if (call.Callee is { } name && name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)) yield return name;
    }

    /// <summary>A set of small non-negative integers as sorted 64-bit blocks.</summary>
    private sealed class SparseSet
    {
        private int[] _keys = new int[1];
        private ulong[] _bits = new ulong[1];
        private int _blocks;
        public int Count { get; private set; }

        private int Find(int key)
        {
            int lo = 0, hi = _blocks - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid] == key) return mid;
                if (_keys[mid] < key) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }

        public bool Contains(int x)
        {
            int at = Find(x >> 6);
            return at >= 0 && (_bits[at] & (1UL << (x & 63))) != 0;
        }

        public bool Add(int x)
        {
            int key = x >> 6;
            ulong bit = 1UL << (x & 63);
            int at = Find(key);
            if (at >= 0)
            {
                if ((_bits[at] & bit) != 0) return false;
                _bits[at] |= bit;
                Count++;
                return true;
            }
            at = ~at;
            if (_blocks == _keys.Length)
            {
                int size = _keys.Length * 2;
                Array.Resize(ref _keys, size);
                Array.Resize(ref _bits, size);
            }
            Array.Copy(_keys, at, _keys, at + 1, _blocks - at);
            Array.Copy(_bits, at, _bits, at + 1, _blocks - at);
            _keys[at] = key;
            _bits[at] = bit;
            _blocks++;
            Count++;
            return true;
        }

        public int[] ToArray()
        {
            int[] result = new int[Count];
            int n = 0;
            for (int b = 0; b < _blocks; b++)
            {
                ulong bits = _bits[b];
                for (int k = 0; k < 64 && bits != 0; k++, bits >>= 1)
                    if ((bits & 1) != 0) result[n++] = (_keys[b] << 6) + k;
            }
            return result;
        }
    }

    // What watches an object's cells, now and later: a load at any offset
    // reading every cell, or a block copy taking each cell to its place.
    private sealed class Watcher
    {
        public int Reader = -1;
        public int To;
        public long From, At, Count;
        public bool Exact;
    }

    private sealed class MemCopyRecord
    {
        public int To, From;
        public long Count;
        public readonly HashSet<long> Pairs = new();
    }

    private sealed class Solver
    {
        private const int Global = 0;
        private const int FarthestField = 4096;
        private const int Any = FarthestField + 1;
        private const int NearestReach = 8;

        private readonly IReadOnlyList<RegionHints> _units;
        private readonly Dictionary<string, string[]> _virtuals;
        private readonly string _entry;
        private readonly IReadOnlySet<string> _foreign;
        private readonly string[]? _report;

        // Instances: one per function each unit summarised.
        private readonly List<RegionFunction> _functions = new();
        private readonly List<int> _unitOf = new();
        private readonly List<int> _nodeBase = new();
        private readonly List<int> _objectBase = new();
        private readonly Dictionary<string, List<int>> _globals = new(StringComparer.Ordinal);
        private readonly List<Dictionary<string, int>> _locals = new();

        // Objects: Global, then each instance's sites and slots.
        private readonly List<int> _objectInstance = new();
        private readonly List<int> _objectSite = new();        // ordinal, -1 for a slot
        private readonly List<List<int>?> _objectCells = new(); // locations of its cells at fixed offsets
        private readonly List<List<Watcher>?> _objectWatchers = new();

        // Locations: an object and an offset (Any: anywhere in it).
        private readonly Dictionary<long, int> _locations = new();
        private readonly List<int> _locationObject = new();
        private readonly List<int> _locationOffset = new();
        private readonly List<int> _cellNode = new();

        // Nodes.
        private readonly List<int> _parent = new();
        private readonly List<SparseSet?> _pts = new();
        private readonly List<List<int>?> _delta = new();
        private readonly List<List<(int To, long Shift)>?> _edges = new();
        private readonly List<HashSet<(int, long)>?> _edgeSets = new();
        private readonly List<List<(int Dest, long Offset)>?> _loads = new();
        private readonly List<List<(int Value, long Offset)>?> _stores = new();
        private readonly List<List<int>?> _memcopies = new();
        private readonly List<MemCopyRecord> _memcopyRecords = new();
        private readonly HashSet<long> _readers = new();
        private readonly Queue<int> _work = new();
        private long _held, _steps, _edgesSinceCollapse;
        private bool _over;

        // The call graph, by instance.
        private List<HashSet<int>> _callees = new();
        private List<HashSet<int>> _callers = new();
        // Only the calls named, for finding recursion: the edges an unknown
        // call or an allocation adds would make nearly everything one cycle.
        private List<HashSet<int>> _named = new();
        private bool _unknownCalls;
        private readonly HashSet<int> _callsUnknown = new();

        private readonly Func<int, string, bool>? _live;

        public Solver(IReadOnlyList<RegionHints> units, Dictionary<string, string[]> virtuals, string entry, IReadOnlySet<string> foreign, string? report,
            Func<int, string, bool>? live)
        {
            _units = units; _virtuals = virtuals; _entry = entry; _foreign = foreign; _live = live;
            _report = report?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private void Log(string text) => Console.Error.WriteLine("regions: " + text);

        public RegionFacts?[]? Run()
        {
            if (!Build()) return GiveUp("too many nodes");
            if (!Constrain()) return GiveUp("too much to hold");
            Collapse();
            if (!Solve()) return GiveUp("too much to hold");
            Log($"{_functions.Count} functions, {_objectInstance.Count} objects, {_parent.Count} nodes, {_locationObject.Count} locations, {_held} held, {_steps} steps");
            return Judge();
        }

        private RegionFacts?[]? GiveUp(string why)
        {
            Log($"gave up ({why}) at {_parent.Count} nodes, {_locationObject.Count} locations, {_held} held; nothing made a region");
            if (_report is not null) Largest();
            return null;
        }

        // What holds the most, for a report: where the sets grew.
        private void Largest()
        {
            List<(int Count, int Node)> sizes = new();
            for (int n = 0; n < _pts.Count; n++) if (_pts[n] is { } set) sizes.Add((set.Count, n));
            sizes.Sort((x, y) => y.Count.CompareTo(x.Count));
            Dictionary<int, int> cellOf = new();
            for (int loc = 0; loc < _cellNode.Count; loc++) if (_cellNode[loc] >= 0) cellOf[_cellNode[loc]] = loc;
            foreach ((int count, int node) in sizes.Take(25))
                Log($"  {count} held by {DescribeNode(node, cellOf)}");
        }

        private string DescribeNode(int node, Dictionary<int, int> cellOf)
        {
            if (cellOf.TryGetValue(node, out int loc))
            {
                int o = _locationObject[loc];
                string offset = _locationOffset[loc] == Any ? "any" : _locationOffset[loc].ToString();
                return "cell +" + offset + " of " + DescribeObject(o);
            }
            for (int i = _nodeBase.Count - 1; i >= 0; i--)
                if (_nodeBase[i] <= node)
                    return node - _nodeBase[i] < _functions[i].Nodes ? _functions[i].Name + " node " + (node - _nodeBase[i]) : "unknown node";
            return "unknown node";
        }

        private string DescribeObject(int o)
        {
            if (o == Global) return "the unknown object";
            int i = _objectInstance[o], site = _objectSite[o];
            return site >= 0 ? "site " + site + " of " + _functions[i].Name + " line " + _functions[i].Sites[site].Line : "a slot of " + _functions[i].Name;
        }

        // ---- building ---------------------------------------------------------

        private bool Build()
        {
            NewObject(-1, -1);                                   // Global
            long nodes = 0;
            for (int u = 0; u < _units.Count; u++)
            {
                Dictionary<string, int> locals = new(StringComparer.Ordinal);
                _locals.Add(locals);
                foreach (RegionFunction f in _units[u].Functions)
                {
                    // What the image does not keep is never called: a call
                    // of it from what it keeps is a call of something unknown.
                    if (_live is not null && !_live(u, f.Name)) continue;
                    int instance = _functions.Count;
                    _functions.Add(f);
                    _unitOf.Add(u);
                    if (f.Global) (_globals.TryGetValue(f.Name, out List<int>? list) ? list : _globals[f.Name] = new()).Add(instance);
                    else locals[f.Name] = instance;
                    nodes += f.Nodes;
                    if (nodes > NodeBudget) return false;
                }
            }
            for (int i = 0; i < _functions.Count; i++)
            {
                RegionFunction f = _functions[i];
                _nodeBase.Add(_parent.Count);
                for (int n = 0; n < f.Nodes; n++) NewNode();
                _objectBase.Add(_objectInstance.Count);
                for (int s = 0; s < f.Sites.Length; s++) NewObject(i, s);
                for (int s = 0; s < f.Slots; s++) NewObject(i, -1);
                _callees.Add(new());
                _callers.Add(new());
                _named.Add(new());
            }
            return true;
        }

        private int NewNode()
        {
            _parent.Add(_parent.Count);
            _pts.Add(null);
            _delta.Add(null);
            _edges.Add(null);
            _edgeSets.Add(null);
            _loads.Add(null);
            _stores.Add(null);
            _memcopies.Add(null);
            return _parent.Count - 1;
        }

        private void NewObject(int instance, int site)
        {
            _objectInstance.Add(instance);
            _objectSite.Add(site);
            _objectCells.Add(null);
            _objectWatchers.Add(null);
        }

        private int Node(int instance, int local) => _nodeBase[instance] + local;
        private int SiteObject(int instance, int site) => _objectBase[instance] + site;
        private int SlotObject(int instance, int slot) => _objectBase[instance] + _functions[instance].Sites.Length + slot;

        private int Rep(int n)
        {
            int root = n;
            while (_parent[root] != root) root = _parent[root];
            while (_parent[n] != root) { int next = _parent[n]; _parent[n] = root; n = next; }
            return root;
        }

        private int Location(int o, long offset)
        {
            int at = o == Global || offset < 0 || offset > FarthestField ? Any : (int)offset;
            long key = ((long)o << 16) | (uint)at;
            if (_locations.TryGetValue(key, out int known)) return known;
            int made = _locationObject.Count;
            if (made >= LocationBudget) _over = true;
            _locations[key] = made;
            _locationObject.Add(o);
            _locationOffset.Add(at);
            _cellNode.Add(-1);
            return made;
        }

        private int GlobalLocation => Location(Global, Any);

        private int Shifted(int loc, long shift)
        {
            if (shift == 0) return loc;
            int o = _locationObject[loc], offset = _locationOffset[loc];
            if (o == Global) return loc;
            if (shift == RegionConstraint.Any || offset == Any || shift > FarthestField || shift < -FarthestField) return Location(o, Any);
            return Location(o, offset + shift);
        }

        /// <summary>The node of one of an object's cells, made on first use.</summary>
        private int Cell(int loc)
        {
            if (_cellNode[loc] >= 0) return Rep(_cellNode[loc]);
            int node = NewNode();
            _cellNode[loc] = node;
            int o = _locationObject[loc];
            if (_locationOffset[loc] != Any)
            {
                // A cell written at any offset is read wherever this one is.
                Edge(Cell(Location(o, Any)), node, 0);
                (_objectCells[o] ??= new()).Add(loc);
                if (_objectWatchers[o] is { } watchers)
                    for (int w = 0; w < watchers.Count; w++) Watch(watchers[w], loc, node);
            }
            return node;
        }

        private void Watch(Watcher w, int loc, int cell)
        {
            if (w.Reader >= 0) { Edge(cell, w.Reader, 0); return; }
            if (!w.Exact) { Edge(cell, Cell(Location(w.To, Any)), 0); return; }
            long offset = _locationOffset[loc];
            if (offset >= w.From && (w.Count == RegionConstraint.Any || offset - w.From < w.Count))
                Edge(cell, Cell(Location(w.To, w.At + offset - w.From)), 0);
        }

        /// <summary>Run a watcher over every cell of an object at a fixed offset, now and later.</summary>
        private void EachCell(int o, Watcher w)
        {
            (_objectWatchers[o] ??= new()).Add(w);
            if (_objectCells[o] is { } cells)
                foreach (int loc in cells.ToArray()) Watch(w, loc, Cell(loc));
        }

        private void Add(int node, int loc)
        {
            node = Rep(node);
            SparseSet set = _pts[node] ??= new();
            if (!set.Add(loc)) return;
            if (++_held > HeldBudget) _over = true;
            List<int>? delta = _delta[node];
            if (delta is null) { _delta[node] = delta = new(); _work.Enqueue(node); }
            delta.Add(loc);
        }

        private void Edge(int from, int to, long shift)
        {
            from = Rep(from); to = Rep(to);
            if (from == to && shift == 0) return;
            List<(int To, long Shift)> edges = _edges[from] ??= new();
            if (_edgeSets[from] is { } set) { if (!set.Add((to, shift))) return; }
            else
            {
                for (int e = 0; e < edges.Count; e++) if (edges[e].To == to && edges[e].Shift == shift) return;
                if (edges.Count >= 8)
                {
                    HashSet<(int, long)> made = new();
                    foreach (var e in edges) made.Add((e.To, e.Shift));
                    made.Add((to, shift));
                    _edgeSets[from] = made;
                }
            }
            edges.Add((to, shift));
            if (shift == 0) _edgesSinceCollapse++;
            if (_pts[from] is { } pts)
                foreach (int loc in pts.ToArray()) Add(to, Shifted(loc, shift));
        }

        private void Leak(int node) => Edge(node, Cell(GlobalLocation), 0);

        private void Load(int dest, int baseNode, long offset)
        {
            baseNode = Rep(baseNode);
            (_loads[baseNode] ??= new()).Add((dest, offset));
            if (_pts[baseNode] is { } pts) foreach (int loc in pts.ToArray()) Loaded(loc, dest, offset);
        }

        private void Loaded(int loc, int dest, long offset)
        {
            int o = _locationObject[loc];
            // Out of the unknown object comes the unknown object.
            if (o == Global) { Add(dest, GlobalLocation); return; }
            int base0 = _locationOffset[loc];
            long at = base0 == Any ? Any : base0 + offset;
            int cell = Location(o, at);
            Edge(Cell(cell), dest, 0);
            if (_locationOffset[cell] == Any && _readers.Add(((long)o << 32) | (uint)Rep(dest))) EachCell(o, new Watcher { Reader = Rep(dest) });
        }

        private void Store(int baseNode, long offset, int value)
        {
            baseNode = Rep(baseNode);
            (_stores[baseNode] ??= new()).Add((value, offset));
            if (_pts[baseNode] is { } pts) foreach (int loc in pts.ToArray()) Stored(loc, value, offset);
        }

        private void Stored(int loc, int value, long offset)
        {
            int o = _locationObject[loc], base0 = _locationOffset[loc];
            long at = o == Global || base0 == Any ? Any : base0 + offset;
            Edge(value, Cell(Location(o, at)), 0);
        }

        private void MemCopy(int to, int from, long count)
        {
            int id = _memcopyRecords.Count;
            _memcopyRecords.Add(new MemCopyRecord { To = to, From = from, Count = count });
            to = Rep(to); from = Rep(from);
            (_memcopies[to] ??= new()).Add(id);
            if (from != to) (_memcopies[from] ??= new()).Add(id);
            if (_pts[from] is { } pts) foreach (int loc in pts.ToArray()) Copied(id, loc, true);
        }

        // A new location at one end of a block copy: paired with every one at the other.
        private void Copied(int id, int loc, bool atSource)
        {
            MemCopyRecord r = _memcopyRecords[id];
            int other = Rep(atSource ? r.To : r.From);
            if (_pts[other] is not { } pts) return;
            foreach (int with in pts.ToArray())
            {
                int src = atSource ? loc : with, dst = atSource ? with : loc;
                if (!r.Pairs.Add(((long)src << 32) | (uint)dst)) continue;
                Pair(src, dst, r.Count);
            }
        }

        private void Pair(int src, int dst, long count)
        {
            int os = _locationObject[src], od = _locationObject[dst];
            int ds = _locationOffset[src], dd = _locationOffset[dst];
            if (os == Global)
            {
                Add(Cell(Location(od, Any)), GlobalLocation);
                return;
            }
            bool exact = ds != Any && dd != Any && od != Global;
            EachCell(os, new Watcher { To = od, From = ds, At = dd, Count = count, Exact = exact });
            Edge(Cell(Location(os, Any)), Cell(Location(od, Any)), 0);
        }

        private bool Constrain()
        {
            for (int i = 0; i < _functions.Count; i++)
            {
                RegionFunction f = _functions[i];
                foreach (RegionConstraint c in f.Constraints)
                {
                    int a = Node(i, c.A);
                    switch (c.Kind)
                    {
                        case RegionConstraintKind.Site: Add(a, Location(SiteObject(i, c.B), 0)); break;
                        case RegionConstraintKind.Slot: Add(a, Location(SlotObject(i, c.B), 0)); break;
                        case RegionConstraintKind.Unknown: Add(a, GlobalLocation); break;
                        case RegionConstraintKind.Copy: Edge(Node(i, c.B), a, c.C); break;
                        case RegionConstraintKind.Load: Load(a, Node(i, c.B), c.C); break;
                        case RegionConstraintKind.Store: Store(a, c.C, Node(i, c.B)); break;
                        case RegionConstraintKind.MemCopy: MemCopy(a, Node(i, c.B), c.C); break;
                        case RegionConstraintKind.Leak: Leak(a); break;
                    }
                }
                foreach (RegionCall call in f.Calls) Call(i, call);
                if (_over) return false;
            }
            // CODE NOBODY SUMMARISED -- a call nobody can name, and whatever
            // calls in from outside the IR -- may call anything whose address
            // it can have, with anything. The entry is called with anything.
            HashSet<int> rooted = new();
            foreach (string name in _foreign)
                if (_globals.TryGetValue(name, out List<int>? defined)) rooted.UnionWith(defined);
            if (_globals.TryGetValue(_entry, out List<int>? entries)) rooted.UnionWith(entries);
            if (_unknownCalls)
                for (int u = 0; u < _units.Count; u++)
                    foreach (string name in _units[u].AddressTaken)
                        if (Resolve(u, name) is { } targets) rooted.UnionWith(targets);
            foreach (int r in rooted.Order())
            {
                RegionFunction f = _functions[r];
                for (int k = 0; k < f.Parameters; k++) Add(Node(r, k), GlobalLocation);
                Leak(Node(r, f.Parameters));
            }
            // And what an unknown call can reach is beneath it.
            foreach (int caller in _callsUnknown)
                foreach (int r in rooted)
                {
                    _callees[caller].Add(r);
                    _callers[r].Add(caller);
                }
            return !_over;
        }

        /// <summary>The instances a call by name in unit `u` reaches; null when no unit summarised it.</summary>
        private List<int>? Resolve(int u, string name)
        {
            if (_locals[u].TryGetValue(name, out int local)) return new List<int> { local };
            return _globals.TryGetValue(name, out List<int>? defined) ? defined : null;
        }

        private void Call(int caller, RegionCall call)
        {
            int u = _unitOf[caller];
            List<int> targets = new();
            bool unknown = call.Callee is null;
            if (call.Callee is { } name)
            {
                if (name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal))
                {
                    if (_virtuals.TryGetValue(name, out string[]? overrides))
                        foreach (string target in overrides)
                        {
                            if (Resolve(u, target) is { } found) targets.AddRange(found);
                            else unknown = true;
                        }
                    else unknown = true;
                }
                else if (Resolve(u, name) is { } found) targets.AddRange(found);
                else unknown = !call.GraphOnly;
            }
            foreach (int target in targets)
            {
                _callees[caller].Add(target);
                _callers[target].Add(caller);
                if (call.GraphOnly) continue;
                _named[caller].Add(target);
                RegionFunction g = _functions[target];
                for (int k = 0; k < call.Arguments.Length && k < g.Parameters; k++)
                    if (call.Arguments[k] >= 0) Edge(Node(caller, call.Arguments[k]), Node(target, k), 0);
                if (call.Dest >= 0) Edge(Node(target, g.Parameters), Node(caller, call.Dest), 0);
            }
            if (!unknown || call.GraphOnly) return;
            _unknownCalls = true;
            if (_report is not null) Log("unknown call in " + _functions[caller].Name + " of " + (call.Callee ?? "an address"));
            _callsUnknown.Add(caller);
            foreach (int a in call.Arguments) if (a >= 0) Leak(Node(caller, a));
            if (call.Dest >= 0) Add(Node(caller, call.Dest), GlobalLocation);
        }

        // ---- solving ----------------------------------------------------------

        private bool Solve()
        {
            while (_work.TryDequeue(out int queued))
            {
                if (_over) return false;
                int node = Rep(queued);
                List<int>? delta = _delta[queued];
                _delta[queued] = null;
                if (delta is null) continue;
                if (node != queued)
                {
                    // Merged away while waiting: what it held is the merged node's.
                    foreach (int loc in delta) Add(node, loc);
                    continue;
                }
                _steps++;
                foreach (int loc in delta) Propagate(node, loc);
                if (_edgesSinceCollapse > 50_000 + _parent.Count / 4) Collapse();
            }
            return !_over;
        }

        private void Propagate(int node, int loc)
        {
            if (_edges[node] is { } edges)
                for (int e = 0; e < edges.Count; e++) Add(edges[e].To, Shifted(loc, edges[e].Shift));
            if (_loads[node] is { } loads)
                for (int k = 0; k < loads.Count; k++) Loaded(loc, loads[k].Dest, loads[k].Offset);
            if (_stores[node] is { } stores)
                for (int k = 0; k < stores.Count; k++) Stored(loc, stores[k].Value, stores[k].Offset);
            if (_memcopies[node] is { } copies)
                for (int k = 0; k < copies.Count; k++)
                {
                    MemCopyRecord r = _memcopyRecords[copies[k]];
                    if (Rep(r.From) == node) Copied(copies[k], loc, true);
                    if (Rep(r.To) == node) Copied(copies[k], loc, false);
                }
        }

        /// <summary>
        /// COPY CYCLES ARE ONE NODE: every node on a cycle of plain copies
        /// holds the same, so they are merged (Tarjan's components over the
        /// copies with no shift).
        /// </summary>
        private void Collapse()
        {
            _edgesSinceCollapse = 0;
            int count = _parent.Count;
            int[] index = new int[count], low = new int[count];
            bool[] onStack = new bool[count];
            for (int n = 0; n < count; n++) index[n] = -1;
            int next = 0;
            Stack<int> stack = new();
            Stack<(int Node, int Edge)> calls = new();
            List<List<int>> components = new();
            for (int start = 0; start < count; start++)
            {
                if (_parent[start] != start || index[start] >= 0) continue;
                calls.Push((start, 0));
                index[start] = low[start] = next++;
                stack.Push(start); onStack[start] = true;
                while (calls.Count > 0)
                {
                    (int v, int e) = calls.Pop();
                    List<(int To, long Shift)>? edges = _edges[v];
                    bool descended = false;
                    while (edges is not null && e < edges.Count)
                    {
                        (int to, long shift) = edges[e++];
                        if (shift != 0) continue;
                        int w = Rep(to);
                        if (w == v) continue;
                        if (index[w] < 0)
                        {
                            calls.Push((v, e));
                            index[w] = low[w] = next++;
                            stack.Push(w); onStack[w] = true;
                            calls.Push((w, 0));
                            descended = true;
                            break;
                        }
                        if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                    }
                    if (descended) continue;
                    if (low[v] == index[v])
                    {
                        List<int> component = new();
                        int w;
                        do { w = stack.Pop(); onStack[w] = false; component.Add(w); } while (w != v);
                        if (component.Count > 1) components.Add(component);
                    }
                    if (calls.Count > 0) { int parent = calls.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
                }
            }
            foreach (List<int> component in components)
            {
                int into = component[0];
                for (int k = 1; k < component.Count; k++) into = Merge(component[k], into);
            }
        }

        /// <summary>Node `a` made one with `b`: its sets, edges and uses become b's.</summary>
        private int Merge(int a, int b)
        {
            a = Rep(a); b = Rep(b);
            if (a == b) return b;
            _parent[a] = b;
            SparseSet? held = _pts[a];
            List<(int To, long Shift)>? edges = _edges[a];
            List<(int Dest, long Offset)>? loads = _loads[a];
            List<(int Value, long Offset)>? stores = _stores[a];
            List<int>? copies = _memcopies[a];
            _pts[a] = null; _edges[a] = null; _edgeSets[a] = null; _loads[a] = null; _stores[a] = null; _memcopies[a] = null;
            if (edges is not null) foreach (var e in edges) Edge(b, e.To, e.Shift);
            if (loads is not null) foreach (var l in loads) Load(l.Dest, b, l.Offset);
            if (stores is not null) foreach (var s in stores) Store(b, s.Offset, s.Value);
            if (copies is not null)
                foreach (int id in copies)
                {
                    (_memcopies[b] ??= new()).Add(id);
                    if (_pts[b] is { } pts)
                        foreach (int loc in pts.ToArray())
                        {
                            MemCopyRecord r = _memcopyRecords[id];
                            if (Rep(r.From) == b) Copied(id, loc, true);
                            if (Rep(r.To) == b) Copied(id, loc, false);
                        }
                }
            if (held is not null) foreach (int loc in held.ToArray()) Add(b, loc);
            return b;
        }

        // ---- judging ----------------------------------------------------------

        private long _walked;

        private IEnumerable<int> ObjectsHeld(int node)
        {
            if (_pts[Rep(node)] is not { } pts) yield break;
            foreach (int loc in pts.ToArray()) yield return _locationObject[loc];
        }

        // Objects reached from `start` through their cells, past those in `stop`.
        private void Reach(IEnumerable<int> start, HashSet<int> reached, HashSet<int>? stop)
        {
            Queue<int> next = new();
            foreach (int o in start)
                if ((stop is null || !stop.Contains(o)) && reached.Add(o)) next.Enqueue(o);
            while (next.TryDequeue(out int o))
            {
                _walked++;
                IEnumerable<int> cells = _objectCells[o] ?? (IEnumerable<int>)Array.Empty<int>();
                foreach (int loc in cells.Append(Location(o, Any)))
                {
                    if (_cellNode[loc] < 0) continue;
                    foreach (int held in ObjectsHeld(_cellNode[loc]))
                        if ((stop is null || !stop.Contains(held)) && reached.Add(held)) next.Enqueue(held);
                }
            }
        }

        private HashSet<int> _globalReach = null!;
        private readonly Dictionary<int, HashSet<int>> _outliving = new();

        /// <summary>What outlives a call of instance `c` besides what the unknown object reaches: what it is handed and what it hands back reach.</summary>
        private HashSet<int> Outliving(int c)
        {
            if (_outliving.TryGetValue(c, out HashSet<int>? known)) return known;
            RegionFunction f = _functions[c];
            HashSet<int> reached = new();
            List<int> start = new();
            for (int k = 0; k <= f.Parameters; k++) start.AddRange(ObjectsHeld(Node(c, k)));
            Reach(start, reached, _globalReach);
            return _outliving[c] = reached;
        }

        private bool Outlives(int o, int c) => _globalReach.Contains(o) || Outliving(c).Contains(o);

        private RegionFacts?[]? Judge()
        {
            _globalReach = new();
            Reach(new[] { Global }, _globalReach, null);
            bool[] recursive = Recursive();
            bool MayBeBoundary(int c) => _functions[c].MayBeBoundary && !recursive[c] && _functions[c].Name != _entry;

            // THE NEAREST CALL EACH OBJECT DIES IN (RegionPointsTo.Nearest).
            List<int> sites = new();
            for (int o = 1; o < _objectInstance.Count; o++)
            {
                int site = _objectSite[o];
                if (site < 0 || !_functions[_objectInstance[o]].Sites[site].Rewritable || _globalReach.Contains(o)) continue;
                sites.Add(o);
            }
            SortedSet<int> chosen = new();
            foreach (int o in sites)
            {
                int made = _objectInstance[o];
                Dictionary<int, int> depth = new() { [made] = 0 };
                Queue<int> next = new();
                next.Enqueue(made);
                while (next.TryDequeue(out int c))
                {
                    if (MayBeBoundary(c) && !Outlives(o, c)) { chosen.Add(c); break; }
                    if (depth[c] >= NearestReach) continue;
                    foreach (int caller in _callers[c].Order())
                        if (depth.TryAdd(caller, depth[c] + 1)) next.Enqueue(caller);
                }
                if (_walked > JudgeBudget) return GiveUp("too much to judge");
            }
            if (chosen.Count == 0)
            {
                Log("no boundary found");
                return new RegionFacts?[_units.Count];
            }

            // EVERY BOUNDARY THAT CAN BE OPEN ABOVE A SITE, however far up,
            // must outlive none of its objects.
            HashSet<int> refused = new(), beneathOne = new();
            Dictionary<int, List<int>> sitesOf = new();
            foreach (int o in sites)
                (sitesOf.TryGetValue(_objectInstance[o], out List<int>? list) ? list : sitesOf[_objectInstance[o]] = new()).Add(o);
            Dictionary<int, HashSet<int>> beneath = new();
            foreach (int b in chosen)
            {
                HashSet<int> under = Beneath(b);
                if (_walked > JudgeBudget) return GiveUp("too much to judge");
                foreach (int c in under)
                    if (sitesOf.TryGetValue(c, out List<int>? made))
                        foreach (int o in made)
                        {
                            beneathOne.Add(o);
                            if (!refused.Contains(o) && Outlives(o, b)) refused.Add(o);
                        }
                if (_walked > JudgeBudget) return GiveUp("too much to judge");
            }
            HashSet<int> taken = new();
            foreach (int o in sites) if (beneathOne.Contains(o) && !refused.Contains(o)) taken.Add(o);

            // A boundary with nothing taken beneath it opens nothing worth opening.
            List<int> opened = new();
            foreach (int b in chosen)
            {
                bool any = false;
                foreach (int c in Beneath(b))
                    if (sitesOf.TryGetValue(c, out List<int>? made) && made.Any(taken.Contains)) { any = true; break; }
                if (any) opened.Add(b);
            }

            RegionFacts?[] facts = new RegionFacts?[_units.Count];
            RegionFacts For(int c) => facts[_unitOf[c]] ??= new RegionFacts();
            foreach (int b in opened) For(b).Boundaries.Add(_functions[b].Name);
            foreach (int o in taken) For(_objectInstance[o]).Sites.Add((_functions[_objectInstance[o]].Name, _objectSite[o]));
            if (_report is not null) Report(chosen, opened, taken, sitesOf);
            Log($"{opened.Count} boundaries, {taken.Count} sites in the innermost region, of {sites.Count} not reached from anything unknown");
            return facts;
        }

        // The instances a call of `start` can reach, itself among them.
        private HashSet<int> Beneath(int start)
        {
            HashSet<int> seen = new() { start };
            Queue<int> next = new();
            next.Enqueue(start);
            while (next.TryDequeue(out int c))
            {
                _walked++;
                foreach (int callee in _callees[c])
                    if (seen.Add(callee)) next.Enqueue(callee);
            }
            return seen;
        }

        // Each instance on a cycle of calls, itself included (Tarjan, iterative).
        private bool[] Recursive()
        {
            int count = _functions.Count;
            bool[] recursive = new bool[count];
            int[] index = new int[count], low = new int[count];
            bool[] onStack = new bool[count];
            for (int n = 0; n < count; n++) index[n] = -1;
            int next = 0;
            Stack<int> stack = new();
            Stack<(int Node, int[] Callees, int At)> calls = new();
            for (int start = 0; start < count; start++)
            {
                if (index[start] >= 0) continue;
                index[start] = low[start] = next++;
                stack.Push(start); onStack[start] = true;
                calls.Push((start, _named[start].ToArray(), 0));
                while (calls.Count > 0)
                {
                    (int v, int[] callees, int at) = calls.Pop();
                    bool descended = false;
                    while (at < callees.Length)
                    {
                        int w = callees[at++];
                        if (w == v) { recursive[v] = true; continue; }
                        if (index[w] < 0)
                        {
                            calls.Push((v, callees, at));
                            index[w] = low[w] = next++;
                            stack.Push(w); onStack[w] = true;
                            calls.Push((w, _named[w].ToArray(), 0));
                            descended = true;
                            break;
                        }
                        if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                    }
                    if (descended) continue;
                    if (low[v] == index[v])
                    {
                        List<int> component = new();
                        int w;
                        do { w = stack.Pop(); onStack[w] = false; component.Add(w); } while (w != v);
                        if (component.Count > 1) foreach (int c in component) recursive[c] = true;
                    }
                    if (calls.Count > 0) { int parent = calls.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
                }
            }
            return recursive;
        }

        private void Report(SortedSet<int> chosen, List<int> opened, HashSet<int> taken, Dictionary<int, List<int>> sitesOf)
        {
            foreach (int b in chosen)
            {
                string name = _functions[b].Name;
                if (!_report!.Any(w => name.Contains(w, StringComparison.Ordinal))) continue;
                SortedSet<string> lines = new(StringComparer.Ordinal);
                int local = 0, kept = 0;
                foreach (int c in Beneath(b))
                    if (sitesOf.TryGetValue(c, out List<int>? made))
                        foreach (int o in made)
                        {
                            bool outlives = Outlives(o, b);
                            if (outlives) kept++; else local++;
                            RegionSite site = _functions[c].Sites[_objectSite[o]];
                            lines.Add($"  {(outlives ? "outlives" : taken.Contains(o) ? "region  " : "local   ")} {_functions[c].Name} line {site.Line}");
                        }
                Log($"boundary {name}{(opened.Contains(b) ? "" : " (not opened)")}: {local} local, {kept} outlive it");
                foreach (string line in lines) Console.Error.WriteLine(line);
            }
        }
    }
}
