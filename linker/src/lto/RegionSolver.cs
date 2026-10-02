using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// REGIONS OVER EVERY UNIT: RegionPointsTo for a separately compiled closed
/// image, answered from the units' RegionHints alone -- never their IR.
///
/// Andersen's inclusion analysis, field-sensitive -- a location is an object
/// and a byte offset into it, or any offset -- with RegionPointsTo's one
/// object of context: an instance method is a copy of its nodes for each
/// object it is called on, and what it makes is made once per such object,
/// so a List's array is that List's and not every List's. Contexts nest two
/// deep; deeper ones, and calls on what nobody can name, are the method's
/// context-free copy. A virtual call made on an object whose descriptor its
/// site stamps runs the method that descriptor holds; on any other, every
/// override the image holds for the slot (VirtualTargets). Only what is
/// called from the entry, or from code outside the IR, is ever copied. Sets
/// are sparse bitmaps, and copy cycles are collapsed as they are found.
///
/// Then the boundaries, chosen as RegionPointsTo.Nearest chooses them: for
/// each allocation, the nearest caller -- not the entry, not recursive, not
/// a type's initialiser or an async body -- whose return it is proved not to
/// outlive. A site is made in a region only if, in every copy that makes it,
/// every boundary that can be open above it, however far up, is proved to
/// outlive none of its objects (Runtime.AllocRegion takes whichever is open
/// innermost).
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
    /// <summary>The most objects and calls walked judging the boundaries, before giving up.</summary>
    public const long JudgeBudget = 200_000_000;

    /// <summary>
    /// Each unit's answer, by its place in <paramref name="units"/>; null when
    /// it gave up. <paramref name="methodAt"/> names the function a descriptor
    /// holds at a byte offset (null: none, or not one descriptor);
    /// <paramref name="live"/> says which of a unit's functions the image keeps.
    /// </summary>
    public static RegionFacts?[]? Solve(IReadOnlyList<RegionHints> units, Dictionary<string, string[]> virtuals,
        Func<string, long, string?> methodAt, string entry, IReadOnlySet<string> foreign, string? report, Func<int, string, bool>? live = null)
    {
        // CONTEXTS AS FAR AS THE BUDGET GOES: an object's two deep, then none
        // at all -- every function one copy, coarser but far smaller.
        foreach (int depth in new[] { 2, 0 })
        {
            Solver solver = new(units, virtuals, methodAt, entry, foreign, report, live, depth);
            if (solver.Run() is { } facts) return facts;
            if (!solver.TooBig) break;
        }
        Console.Error.WriteLine("regions: nothing made a region");
        return null;
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

    // A call whose callee depends on what its receiver is: an instance
    // method's, bound per object, or a virtual call's, per descriptor.
    private sealed class Binding
    {
        public int Copy;
        public RegionCall Call = null!;
        public List<int>? Direct;
        public string[]? Overrides;
        public readonly HashSet<long> Seen = new();
        public bool Unbound = true;
    }

    private sealed class Solver
    {
        private const int Global = 0;
        private const int FarthestField = 4096;
        private const int Any = FarthestField + 1;
        private const int NearestReach = 8;
        private readonly int _maxDepth;
        /// <summary>It gave up for its budget: fewer contexts might fit.</summary>
        public bool TooBig { get; private set; }

        private readonly IReadOnlyList<RegionHints> _units;
        private readonly Dictionary<string, string[]> _virtuals;
        private readonly Func<string, long, string?> _methodAt;
        private readonly string _entry;
        private readonly IReadOnlySet<string> _foreign;
        private readonly string[]? _report;
        private readonly Func<int, string, bool>? _live;

        // Functions: one per function each unit summarised that the image keeps.
        private readonly List<RegionFunction> _functions = new();
        private readonly List<int> _unitOf = new();
        private readonly Dictionary<string, List<int>> _globals = new(StringComparer.Ordinal);
        private readonly List<Dictionary<string, int>> _locals = new();
        // Every global function any unit summarised, kept by the image or not.
        private readonly HashSet<string> _summarised = new(StringComparer.Ordinal);

        // Copies: a function in a context (-1: none).
        private readonly List<int> _copyFunction = new();
        private readonly List<int> _copyContext = new();
        private readonly List<int> _copyBase = new();
        private readonly Dictionary<long, int> _copyIds = new();
        private readonly List<int>?[] _copiesOf;
        private readonly List<HashSet<int>> _callees = new();
        private readonly List<HashSet<int>> _callers = new();
        // Only the calls named, between functions, for finding recursion:
        // the edges an unknown call or an allocation adds would make nearly
        // everything one cycle.
        private readonly List<HashSet<int>> _named = new();
        private readonly HashSet<int> _roots = new();

        // Objects: Global, then sites in a context and frame slots of a copy.
        private readonly List<int> _objectFunction = new();
        private readonly List<int> _objectSite = new();        // ordinal, -1 for a slot
        private readonly List<int> _objectContext = new();
        private readonly List<int> _objectDepth = new();
        private readonly List<List<int>> _objectMakers = new(); // the copies that make it
        private readonly Dictionary<(int, int, int), int> _siteObjects = new();
        private readonly Dictionary<long, int> _slotObjects = new();
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
        private readonly List<List<int>?> _receivers = new();
        private readonly List<MemCopyRecord> _memcopyRecords = new();
        private readonly List<Binding> _bindings = new();
        private readonly HashSet<long> _readers = new();
        private readonly Queue<int> _work = new();
        private long _held, _steps, _edgesSinceCollapse;
        private bool _over;

        public Solver(IReadOnlyList<RegionHints> units, Dictionary<string, string[]> virtuals, Func<string, long, string?> methodAt,
            string entry, IReadOnlySet<string> foreign, string? report, Func<int, string, bool>? live, int depth)
        {
            _maxDepth = depth;
            _units = units; _virtuals = virtuals; _methodAt = methodAt; _entry = entry; _foreign = foreign; _live = live;
            _report = report?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int u = 0; u < _units.Count; u++)
            {
                Dictionary<string, int> locals = new(StringComparer.Ordinal);
                _locals.Add(locals);
                foreach (RegionFunction f in _units[u].Functions)
                {
                    if (f.Global) _summarised.Add(f.Name);
                    // What the image does not keep is never called: a call
                    // of it from what it keeps is a call of something unknown.
                    if (_live is not null && !_live(u, f.Name)) continue;
                    int function = _functions.Count;
                    _functions.Add(f);
                    _unitOf.Add(u);
                    _named.Add(new());
                    if (f.Global) (_globals.TryGetValue(f.Name, out List<int>? list) ? list : _globals[f.Name] = new()).Add(function);
                    else locals[f.Name] = function;
                }
            }
            _copiesOf = new List<int>?[_functions.Count];
        }

        private void Log(string text) => Console.Error.WriteLine("regions: " + text);

        public RegionFacts?[]? Run()
        {
            NewObject(-1, -1, -1, 0);                               // Global
            // WHERE THE PROGRAM STARTS, each called with anything: the entry
            // (a unit's own symbol, it may be), whatever code outside the IR
            // names, and every function whose address the program takes --
            // a call nobody can name may reach it, and so may the kernel,
            // handed it by a system call (a signal's handler), whether or not
            // the IR makes any such call.
            SortedSet<int> started = new();
            for (int f = 0; f < _functions.Count; f++)
                if (_functions[f].Name == _entry || _foreign.Contains(_functions[f].Name)) started.Add(f);
            if (started.Count == 0) return GiveUp("no entry " + _entry);
            for (int u = 0; u < _units.Count; u++)
                foreach (string name in _units[u].AddressTaken)
                    if (Resolve(u, name) is { } targets) started.UnionWith(targets);
            foreach (int f in started) Root(f);
            while (true)
            {
                if (!Solve()) return GiveUp("too much to hold");
                // AN INSTANCE CALL NOTHING WAS SEEN TO BE MADE ON still runs:
                // its callee's context-free copy, so its stores and calls count.
                bool more = false;
                for (int b = 0; b < _bindings.Count; b++)
                    if (_bindings[b].Direct is not null && _bindings[b].Unbound)
                    {
                        Binding binding = _bindings[b];
                        binding.Unbound = false;
                        foreach (int g in binding.Direct!)
                            if (binding.Seen.Add(Key(g, -1))) To(binding.Copy, binding.Call, CopyOf(g, -1));
                        more = true;
                    }
                if (!more) break;
                if (_over) return GiveUp("too much to hold");
            }
            // WHAT CODE NOBODY FOLLOWS CALLS is not beneath it here: a root's
            // parameters and return are the unknown object, so whatever one
            // makes that outlives its call is reached from the unknown object
            // and never taken, and the rest is dead by its return -- before
            // any region open above it, wherever, is given back.
            Log($"contexts {_maxDepth} deep: {_functions.Count} functions, {_copyFunction.Count} copies, {_objectFunction.Count} objects, {_parent.Count} nodes, "
                + $"{_locationObject.Count} locations, {_held} held, {_steps} steps");
            return Judge();
        }

        private RegionFacts?[]? GiveUp(string why)
        {
            TooBig = _over || _walked > JudgeBudget;
            Log($"gave up ({why}) with contexts {_maxDepth} deep at {_parent.Count} nodes, {_copyFunction.Count} copies, {_locationObject.Count} locations, {_held} held");
            if (_report is not null) Largest();
            return null;
        }

        // A function called from where nobody can say, with anything, its return going anywhere.
        private void Root(int f)
        {
            int copy = CopyOf(f, -1);
            if (!_roots.Add(copy)) return;
            RegionFunction function = _functions[f];
            for (int k = 0; k < function.Parameters; k++) Add(Node(copy, k), GlobalLocation);
            Leak(Node(copy, function.Parameters));
        }

        // ---- building ---------------------------------------------------------

        private int NewNode()
        {
            if (_parent.Count >= NodeBudget) _over = true;
            _parent.Add(_parent.Count);
            _pts.Add(null);
            _delta.Add(null);
            _edges.Add(null);
            _edgeSets.Add(null);
            _loads.Add(null);
            _stores.Add(null);
            _memcopies.Add(null);
            _receivers.Add(null);
            return _parent.Count - 1;
        }

        private int NewObject(int function, int site, int context, int depth)
        {
            _objectFunction.Add(function);
            _objectSite.Add(site);
            _objectContext.Add(context);
            _objectDepth.Add(depth);
            _objectMakers.Add(new List<int>());
            _objectCells.Add(null);
            _objectWatchers.Add(null);
            return _objectFunction.Count - 1;
        }

        private static long Key(int function, int context) => ((long)function << 32) | (uint)(context + 1);

        private int Node(int copy, int local) => _copyBase[copy] + local;

        /// <summary>A function's copy for an object it is called on (-1: none), made on first use with its constraints.</summary>
        private int CopyOf(int f, int context)
        {
            RegionFunction function = _functions[f];
            if (!function.Instance) context = -1;
            else if (context >= 0 && (_objectSite[context] < 0 || _objectDepth[context] >= _maxDepth)) context = -1;
            if (_copyIds.TryGetValue(Key(f, context), out int known)) return known;
            int copy = _copyFunction.Count;
            _copyIds[Key(f, context)] = copy;
            _copyFunction.Add(f);
            _copyContext.Add(context);
            _copyBase.Add(_parent.Count);
            (_copiesOf[f] ??= new()).Add(copy);
            _callees.Add(new());
            _callers.Add(new());
            for (int n = 0; n < function.Nodes; n++) NewNode();
            if (_over) return copy;
            if (context >= 0) Add(Node(copy, 0), Location(context, 0));
            foreach (RegionConstraint c in function.Constraints)
            {
                int a = Node(copy, c.A);
                switch (c.Kind)
                {
                    case RegionConstraintKind.Site: Add(a, Location(SiteObject(f, c.B, context, copy), 0)); break;
                    case RegionConstraintKind.Slot: Add(a, Location(SlotObject(copy, c.B), 0)); break;
                    case RegionConstraintKind.Unknown: Add(a, GlobalLocation); break;
                    case RegionConstraintKind.Copy: Edge(Node(copy, c.B), a, c.C); break;
                    case RegionConstraintKind.Load: Load(a, Node(copy, c.B), c.C); break;
                    case RegionConstraintKind.Store: Store(a, c.C, Node(copy, c.B)); break;
                    case RegionConstraintKind.MemCopy: MemCopy(a, Node(copy, c.B), c.C); break;
                    case RegionConstraintKind.Leak: Leak(a); break;
                }
            }
            foreach (RegionCall call in function.Calls) Call(copy, call);
            return copy;
        }

        private int SiteObject(int f, int site, int context, int maker)
        {
            int depth = context < 0 ? 0 : _objectDepth[context] + 1;
            if (depth > _maxDepth) { context = -1; depth = 0; }
            if (!_siteObjects.TryGetValue((f, site, context), out int made))
                _siteObjects[(f, site, context)] = made = NewObject(f, site, context, depth);
            if (!_objectMakers[made].Contains(maker)) _objectMakers[made].Add(maker);
            return made;
        }

        private int SlotObject(int copy, int slot)
        {
            long key = ((long)copy << 32) | (uint)slot;
            if (_slotObjects.TryGetValue(key, out int known)) return known;
            int made = NewObject(_copyFunction[copy], -1, -1, 0);
            _objectMakers[made].Add(copy);
            return _slotObjects[key] = made;
        }

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
            int from = _locationOffset[loc];
            long at = from == Any ? Any : from + offset;
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
            int o = _locationObject[loc], from = _locationOffset[loc];
            long at = o == Global || from == Any ? Any : from + offset;
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

        // ---- calls ------------------------------------------------------------

        /// <summary>The functions a call by name in unit `u` reaches; null when the image keeps none that a unit summarised.</summary>
        private List<int>? Resolve(int u, string name)
        {
            if (_locals[u].TryGetValue(name, out int local)) return new List<int> { local };
            return _globals.TryGetValue(name, out List<int>? defined) ? defined : null;
        }

        private void Call(int copy, RegionCall call)
        {
            int u = _unitOf[_copyFunction[copy]];
            if (call.Callee is not { } name) { Unknown(copy, call); return; }
            // An allocator's call, the collector's notes: what the runtime
            // runs there calls the program back only through what its
            // address is taken for (a hook), a root.
            if (call.GraphOnly) return;
            if (name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal))
            {
                List<string> overrides = new();
                bool known = _virtuals.TryGetValue(name, out string[]? found);
                // An override the image does not keep is a type nothing makes:
                // it is never run. One no unit summarised is code nobody follows.
                foreach (string target in found ?? Array.Empty<string>())
                    if (Resolve(u, target) is not null) overrides.Add(target);
                    else if (!_summarised.Contains(target)) known = false;
                if (!known) { Unknown(copy, call); return; }
                if (overrides.Count == 0) return;            // no object of the type exists
                int self = call.Arguments.Length > 0 ? call.Arguments[0] : -1;
                Binding binding = new() { Copy = copy, Call = call, Overrides = overrides.ToArray() };
                if (self < 0) { AllOverrides(binding); return; }
                Watch(binding, Node(copy, self));
                return;
            }
            if (Resolve(u, name) is not { } targets) { Unknown(copy, call); return; }
            List<int> instance = new();
            foreach (int g in targets)
            {
                _named[_copyFunction[copy]].Add(g);
                if (_functions[g].Instance && call.Arguments.Length > 0 && call.Arguments[0] >= 0) instance.Add(g);
                else To(copy, call, CopyOf(g, -1));
            }
            if (instance.Count > 0) Watch(new Binding { Copy = copy, Call = call, Direct = instance }, Node(copy, call.Arguments[0]));
        }

        private void Watch(Binding binding, int receiver)
        {
            int id = _bindings.Count;
            _bindings.Add(binding);
            receiver = Rep(receiver);
            (_receivers[receiver] ??= new()).Add(id);
            if (_pts[receiver] is { } pts) foreach (int loc in pts.ToArray()) Received(id, loc);
        }

        // An object a receiver may be: the callee's copy for it.
        private void Received(int id, int loc)
        {
            Binding binding = _bindings[id];
            int o = _locationObject[loc];
            if (binding.Direct is not null)
            {
                binding.Unbound = false;
                int context = _locationOffset[loc] == 0 && o != Global ? o : -1;
                foreach (int g in binding.Direct)
                    if (binding.Seen.Add(Key(g, context))) To(binding.Copy, binding.Call, CopyOf(g, context));
                return;
            }
            // A virtual call runs, on an object whose stamp is known, the
            // method its descriptor holds; on any other, every override.
            if (o != Global && _objectSite[o] >= 0 && _functions[_objectFunction[o]].Sites[_objectSite[o]] is { Table: { } table } site
                && SlotOf(binding.Call.Callee!) is long slot && _methodAt(table, site.At + slot) is { } method
                && binding.Overrides!.Contains(method, StringComparer.Ordinal))
            {
                if (!binding.Seen.Add(((long)o << 1) | 1)) return;
                foreach (int g in Resolve(_unitOf[_copyFunction[binding.Copy]], method)!)
                {
                    _named[_copyFunction[binding.Copy]].Add(g);
                    To(binding.Copy, binding.Call, CopyOf(g, o));
                }
                return;
            }
            AllOverrides(binding);
        }

        private void AllOverrides(Binding binding)
        {
            if (!binding.Seen.Add(-2)) return;
            int u = _unitOf[_copyFunction[binding.Copy]];
            foreach (string target in binding.Overrides!)
                foreach (int g in Resolve(u, target)!)
                {
                    _named[_copyFunction[binding.Copy]].Add(g);
                    To(binding.Copy, binding.Call, CopyOf(g, -1));
                }
        }

        private static long? SlotOf(string name)
        {
            int plus = name.LastIndexOf('+');
            return plus > 0 && long.TryParse(name.AsSpan(plus + 1), out long slot) ? slot : null;
        }

        private void Beneath(int caller, int callee)
        {
            _callees[caller].Add(callee);
            _callers[callee].Add(caller);
        }

        // The call made: its arguments the callee copy's parameters, its return the call's result.
        private void To(int caller, RegionCall call, int callee)
        {
            Beneath(caller, callee);
            RegionFunction g = _functions[_copyFunction[callee]];
            for (int k = 0; k < call.Arguments.Length && k < g.Parameters; k++)
                if (call.Arguments[k] >= 0) Edge(Node(caller, call.Arguments[k]), Node(callee, k), 0);
            if (call.Dest >= 0) Edge(Node(callee, g.Parameters), Node(caller, call.Dest), 0);
        }

        private void Unknown(int copy, RegionCall call)
        {
            if (_report is not null && _reportedUnknown.Add(call.Callee ?? "an address"))
                Log("unknown call in " + _functions[_copyFunction[copy]].Name + " of " + (call.Callee ?? "an address"));
            foreach (int a in call.Arguments) if (a >= 0) Leak(Node(copy, a));
            if (call.Dest >= 0) Add(Node(copy, call.Dest), GlobalLocation);
        }

        private readonly HashSet<string> _reportedUnknown = new(StringComparer.Ordinal);

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
            if (_receivers[node] is { } receivers)
                for (int k = 0; k < receivers.Count; k++) Received(receivers[k], loc);
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
            List<int>? receivers = _receivers[a];
            _pts[a] = null; _edges[a] = null; _edgeSets[a] = null; _loads[a] = null; _stores[a] = null; _memcopies[a] = null; _receivers[a] = null;
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
            if (receivers is not null)
                foreach (int id in receivers)
                {
                    (_receivers[b] ??= new()).Add(id);
                    if (_pts[b] is { } pts) foreach (int loc in pts.ToArray()) Received(id, loc);
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
                List<int> cells = new(_objectCells[o] ?? new List<int>());
                if (_locations.TryGetValue(((long)o << 16) | (uint)Any, out int any)) cells.Add(any);
                foreach (int loc in cells)
                {
                    if (_cellNode[loc] < 0) continue;
                    foreach (int held in ObjectsHeld(_cellNode[loc]))
                        if ((stop is null || !stop.Contains(held)) && reached.Add(held)) next.Enqueue(held);
                }
            }
        }

        private HashSet<int> _globalReach = null!;
        private readonly Dictionary<int, HashSet<int>> _outliving = new();

        /// <summary>What outlives a call of copy `c` besides what the unknown object reaches: what it is handed and what it hands back reach.</summary>
        private HashSet<int> Outliving(int c)
        {
            if (_outliving.TryGetValue(c, out HashSet<int>? known)) return known;
            RegionFunction f = _functions[_copyFunction[c]];
            HashSet<int> reached = new();
            List<int> start = new();
            for (int k = 0; k <= f.Parameters; k++) start.AddRange(ObjectsHeld(Node(c, k)));
            Reach(start, reached, _globalReach);
            return _outliving[c] = reached;
        }

        private bool Outlives(int o, int c) => _globalReach.Contains(o) || Outliving(c).Contains(o);

        private bool IsSite(int o) => o > Global && _objectSite[o] >= 0;

        private RegionFacts?[]? Judge()
        {
            _globalReach = new();
            Reach(new[] { Global }, _globalReach, null);
            bool[] recursive = Recursive();
            bool MayBeBoundary(int f) => _functions[f].MayBeBoundary && !recursive[f] && _functions[f].Name != _entry;

            // Each site's objects, by function and ordinal.
            Dictionary<(int, int), List<int>> bySite = new();
            for (int o = 1; o < _objectFunction.Count; o++)
                if (IsSite(o) && _functions[_objectFunction[o]].Sites[_objectSite[o]].Rewritable)
                {
                    (int, int) key = (_objectFunction[o], _objectSite[o]);
                    (bySite.TryGetValue(key, out List<int>? list) ? list : bySite[key] = new()).Add(o);
                }

            // THE NEAREST CALL EACH OBJECT DIES IN (RegionPointsTo.Nearest):
            // from each copy that makes it up through its callers, nearest
            // first, the first whose return it is proved not to outlive.
            SortedSet<int> chosen = new();
            foreach (List<int> objects in bySite.Values)
                foreach (int o in objects)
                {
                    if (_globalReach.Contains(o)) continue;
                    foreach (int made in _objectMakers[o])
                    {
                        Dictionary<int, int> depth = new() { [made] = 0 };
                        Queue<int> next = new();
                        next.Enqueue(made);
                        while (next.TryDequeue(out int c))
                        {
                            if (MayBeBoundary(_copyFunction[c]) && !Outlives(o, c)) { chosen.Add(_copyFunction[c]); break; }
                            if (depth[c] >= NearestReach) continue;
                            foreach (int caller in _callers[c].Order())
                                if (depth.TryAdd(caller, depth[c] + 1)) next.Enqueue(caller);
                        }
                    }
                    if (_walked > JudgeBudget) return GiveUp("too much to judge");
                }
            if (_report is not null) foreach (int f in chosen) Log("boundary chosen " + _functions[f].Name);
            if (chosen.Count == 0)
            {
                Log("no boundary found");
                return new RegionFacts?[_units.Count];
            }

            // EVERY BOUNDARY THAT CAN BE OPEN ABOVE A SITE, however far up,
            // in any copy that makes it, must outlive none of its objects.
            Dictionary<int, List<int>> madeBy = new();
            foreach (List<int> objects in bySite.Values)
                foreach (int o in objects)
                    foreach (int maker in _objectMakers[o])
                        (madeBy.TryGetValue(maker, out List<int>? list) ? list : madeBy[maker] = new()).Add(o);
            HashSet<int> refused = new(), beneathOne = new();
            List<int> boundaryCopies = new();
            foreach (int f in chosen) boundaryCopies.AddRange(_copiesOf[f]!);
            foreach (int b in boundaryCopies)
            {
                foreach (int c in Beneath(b))
                    if (madeBy.TryGetValue(c, out List<int>? made))
                        foreach (int o in made)
                        {
                            beneathOne.Add(o);
                            if (!refused.Contains(o) && Outlives(o, b)) { refused.Add(o); _refusedBy[o] = b; }
                        }
                if (_walked > JudgeBudget) return GiveUp("too much to judge");
            }
            // A site is taken when none of its objects is refused and one is
            // beneath some boundary.
            HashSet<(int, int)> taken = new();
            foreach (((int, int) key, List<int> objects) in bySite)
                if (objects.Any(beneathOne.Contains) && !objects.Any(refused.Contains)) taken.Add(key);

            // A boundary with nothing taken beneath it opens nothing worth opening.
            List<int> opened = new();
            foreach (int f in chosen)
            {
                bool any = false;
                foreach (int b in _copiesOf[f]!)
                {
                    foreach (int c in Beneath(b))
                        if (madeBy.TryGetValue(c, out List<int>? made) && made.Any(o => taken.Contains((_objectFunction[o], _objectSite[o])))) { any = true; break; }
                    if (any) break;
                }
                if (any) opened.Add(f);
            }

            RegionFacts?[] facts = new RegionFacts?[_units.Count];
            RegionFacts For(int f) => facts[_unitOf[f]] ??= new RegionFacts();
            foreach (int f in opened) For(f).Boundaries.Add(_functions[f].Name);
            foreach ((int f, int site) in taken) For(f).Sites.Add((_functions[f].Name, site));
            if (_report is not null) Report(chosen, opened, taken, madeBy);
            Log($"{opened.Count} boundaries, {taken.Count} sites in the innermost region, of {bySite.Count}");
            return facts;
        }

        // The copies a call of `start` can reach, itself among them.
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

        // Each function on a cycle of named calls, itself included (Tarjan, iterative).
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

        // ---- reporting --------------------------------------------------------

        // Which boundary copy found each refused object outliving it, for a report.
        private readonly Dictionary<int, int> _refusedBy = new();

        private void Report(SortedSet<int> chosen, List<int> opened, HashSet<(int, int)> taken, Dictionary<int, List<int>> madeBy)
        {
            // A key not taken though this object is local: which of its
            // objects some boundary refused, and which boundary.
            Dictionary<(int, int), string> why = new();
            foreach ((int o, int b) in _refusedBy)
                why.TryAdd((_objectFunction[o], _objectSite[o]), (_objectContext[o] >= 0 ? "context " + _objectContext[o] : "no context")
                    + " outlives " + _functions[_copyFunction[b]].Name + " context " + _copyContext[b]);
            for (int b = 0; b < _copyFunction.Count; b++)
            {
                int f = _copyFunction[b];
                string name = _functions[f].Name;
                if (!_report!.Any(w => name.Contains(w, StringComparison.Ordinal))) continue;
                SortedSet<string> lines = new(StringComparer.Ordinal);
                int local = 0, kept = 0;
                foreach (int c in Beneath(b))
                    if (madeBy.TryGetValue(c, out List<int>? made))
                        foreach (int o in made)
                        {
                            bool outlives = Outlives(o, b);
                            if (outlives) kept++; else local++;
                            string verdict = outlives ? "outlives" : taken.Contains((_objectFunction[o], _objectSite[o])) ? "region  " : "local   ";
                            string reason = !outlives && !taken.Contains((_objectFunction[o], _objectSite[o]))
                                && why.TryGetValue((_objectFunction[o], _objectSite[o]), out string? because) ? " (refused: " + because + ")" : "";
                            lines.Add("  " + verdict + " " + DescribeObject(o) + reason);
                        }
                string state = opened.Contains(f) ? "opened" : chosen.Contains(f) ? "chosen, nothing taken" : "not chosen";
                Log($"boundary {name} context {_copyContext[b]} ({state}): {local} local, {kept} outlive it");
                foreach (string line in lines) Console.Error.WriteLine(line);
            }
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
                string offset = _locationOffset[loc] == Any ? "any" : _locationOffset[loc].ToString();
                return "cell +" + offset + " of " + DescribeObject(_locationObject[loc]);
            }
            for (int c = _copyBase.Count - 1; c >= 0; c--)
                if (_copyBase[c] <= node)
                {
                    RegionFunction f = _functions[_copyFunction[c]];
                    return node - _copyBase[c] < f.Nodes ? f.Name + " context " + _copyContext[c] + " node " + (node - _copyBase[c]) : "a node";
                }
            return "a node";
        }

        private string DescribeObject(int o)
        {
            if (o == Global) return "the unknown object";
            RegionFunction f = _functions[_objectFunction[o]];
            int site = _objectSite[o];
            if (site < 0) return "a slot of " + f.Name;
            RegionSite s = f.Sites[site];
            return f.Name + " line " + s.Line + " " + (s.Table ?? "block") + (_objectContext[o] >= 0 ? " in context " + _objectContext[o] : "");
        }
    }
}
