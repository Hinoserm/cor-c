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
/// RegionPointsTo's costs and approximations, each sound: past 16 objects'
/// copies of one method the rest share its context-free copy; an object
/// with more than 64 cells is one any-offset cell; a pointer already inside
/// an object moved again is anywhere in it; a read at any offset reads one
/// node per object; a block copy meets each source with each destination
/// once, and past 1024 pairs goes through one node; a word the collector
/// never reads as a reference holds the unknown at most. And two of its own,
/// for a program the size of the compiler: each location a receiver may be
/// is handed to the copies that run on it alone, never by an edge to every
/// copy the call reaches; and a node that holds more than 256 locations holds
/// the unknown object instead, and what it held escapes.
///
/// Then the boundaries, chosen as RegionPointsTo.Nearest chooses them: for
/// each allocation, the nearest caller -- not the entry, not recursive, not
/// a type's initialiser or an async body -- whose return it is proved not to
/// outlive. A site is made in a region only if, in every copy that makes it,
/// every boundary that can be the innermost open above it -- the first on
/// each way up, however far up -- is proved to outlive none of its objects
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
    public const int NodeBudget = 6_000_000, HeldBudget = 40_000_000, LocationBudget = 4_000_000;
    /// <summary>
    /// And the most heap the solve may grow by, whatever holds it: edges,
    /// watchers, pairs of copies. A reading over it is believed only after a
    /// collection (RegionPointsTo.OverHeap).
    /// </summary>
    public const long HeapBudget = 1L << 30;
    /// <summary>The most objects and calls walked judging the boundaries, before giving up.</summary>
    public const long JudgeBudget = 200_000_000;

    /// <summary>
    /// Each unit's answer, by its place in <paramref name="units"/>; null when
    /// it gave up. <paramref name="methodAt"/> names the function a descriptor
    /// holds at a byte offset (null: none, or not one descriptor);
    /// <paramref name="live"/> says which of a unit's functions the image keeps;
    /// <paramref name="noReference"/> whether the collector never reads a word
    /// of an object stamped with a descriptor, at a byte offset (null: any
    /// word), as a reference (VirtualTargets.HoldsNoReference);
    /// <paramref name="loops"/> whether the image has Runtime.RegionLoop, and
    /// a loop may be given a region of its own.
    /// </summary>
    public static RegionFacts?[]? Solve(IReadOnlyList<RegionHints> units, Dictionary<string, string[]> virtuals,
        Func<string, long, string?> methodAt, string entry, IReadOnlySet<string> foreign, string? report, Func<int, string, bool>? live = null,
        Func<string, long, long?, bool>? noReference = null, bool loops = false)
    {
        // CONTEXTS AS FAR AS THE BUDGET GOES: two objects deep, then one, then none
        // at all -- every function one copy, coarser but far smaller.
        //
        // NONE FIRST, to know whether any can fit. A deeper context only
        // copies more, so a solve too big with none is too big with any: the
        // compiler's own link outgrew the budget at two, one and none in turn,
        // a minute each for nothing, and the third, in a heap the first two
        // had broken up, ran a 32-bit process out of room. The answer kept is
        // still the deepest that fits; the coarse one is kept, not made again.
        Solver coarse = new(units, virtuals, methodAt, entry, foreign, report, live, 0, noReference) { LoopRegions = loops };
        RegionFacts?[]? coarseFacts = coarse.Run();
        if (coarseFacts is not null || !coarse.TooBig)
        {
            foreach (int depth in new[] { 2, 1 })
            {
                Solver solver = new(units, virtuals, methodAt, entry, foreign, report, live, depth, noReference) { LoopRegions = loops };
                if (solver.Run() is { } facts) return facts;
                if (!solver.TooBig) { coarseFacts = null; break; }
            }
            if (coarseFacts is not null) return coarseFacts;
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

        // Walked in place, block by block, while nothing is added to it.
        public int Blocks => _blocks;
        public int KeyAt(int block) => _keys[block];
        public ulong BitsAt(int block) => _bits[block];

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

    // What watches an object's cells, now and later: a block copy taking each
    // cell at a fixed offset to its place.
    private sealed class Watcher
    {
        public int To;
        public long From, At, Count;
    }

    // A node's uses beyond the copies out of it: the loads and stores it is
    // the address of, the block copies at either end, the calls it is the
    // receiver of; and its copies as a set, once there are many.
    private sealed class Uses
    {
        public List<(int Dest, long Offset)>? Loads;
        public List<(int Value, long Offset)>? Stores;
        public List<int>? MemCopies;
        public List<int>? Receivers;
        public HashSet<(int, long)>? EdgeSet;
    }

    // A block copy: each location at either end met with each at the other
    // once, from the two lists seen so far; one at any offset decided by its
    // two objects; past MostPairs, every source to every destination through
    // one node.
    private sealed class MemCopyRecord
    {
        public int To, From;
        public long Count;
        public readonly HashSet<int> SourcesSeen = new(), DestinationsSeen = new();
        public readonly List<int> Sources = new(), Destinations = new();
        public readonly HashSet<long> AnyPairs = new();
        public int Through = -1;
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

        // AN ELEMENT'S WORD IN AN ARRAY OF STRUCTS (RegionPointsTo.Strided):
        // an offset with this bit is the word at a residue of 2^k, k in bits
        // 8-10. Words that may be the same one are read and written as one.
        private const int StrideBit = 0x4000;
        private static bool IsStrided(int offset) => offset != Any && (offset & StrideBit) != 0;
        // A plain offset worked out: past the farthest field, any offset --
        // never a value that happens to carry StrideBit.
        private static long Plain(long offset) => offset < 0 || offset > FarthestField ? Any : offset;
        private static int Strided(int k, long residue) => StrideBit | (k << 8) | (int)(residue & ((1L << k) - 1));
        private static int StrideShiftOf(int offset) => (offset >> 8) & 0x7;
        private static int ResidueOf(int offset) => offset & 0xFF;
        private static int StridedPlus(int offset, long by)
        {
            int k = StrideShiftOf(offset);
            long m = 1L << k;
            return Strided(k, ((ResidueOf(offset) + by) % m + m) % m);
        }
        private static bool MayOverlap(int a, int b, int firstData)
        {
            if (a == b || a == Any || b == Any) return a == b;
            bool sa = IsStrided(a), sb = IsStrided(b);
            if (!sa && !sb) return false;
            if (sa && sb)
            {
                long m = 1L << Math.Min(StrideShiftOf(a), StrideShiftOf(b));
                return ((ResidueOf(a) - ResidueOf(b)) % m + m) % m == 0;
            }
            int strided = sa ? a : b, plain = sa ? b : a;
            return plain >= firstData && (plain & ((1 << StrideShiftOf(strided)) - 1)) == ResidueOf(strided);
        }
        // An array's elements are never its stamp (offset 0): the one word an
        // element's word cannot be, told without the target's header size.
        private int FirstData(int o) =>
            o != Global && _objectSite[o] >= 0 && _functions[_objectFunction[o]].Sites[_objectSite[o]] is { Table: { } table }
            && table.StartsWith("q_array", StringComparison.Ordinal) ? 1 : 0;
        private const int NearestReach = 8;
        // Past this many cells an object is one cell: an object read or
        // written at that many offsets is an array, or a pointer walked
        // through memory.
        private const int MostCells = 64;
        // Past this many pairs of source and destination a block copy is one
        // node, everything from every source to anywhere in every destination.
        private const int MostPairs = 1024;
        // Past this many objects' copies of one method, the rest share its
        // copy in no object's context.
        private const int MostContexts = 16;
        // Past this many locations a node holds the unknown object, and they escape.
        private const int MostHeld = 256;
        private readonly int _maxDepth;
        /// <summary>It gave up for its budget: fewer contexts might fit.</summary>
        public bool TooBig { get; private set; }
        /// <summary>Loops may be given regions of their own (Runtime.RegionLoop is in the image).</summary>
        public bool LoopRegions { get; init; }

        private readonly IReadOnlyList<RegionHints> _units;
        private readonly Dictionary<string, string[]> _virtuals;
        private readonly Func<string, long, string?> _methodAt;
        private readonly string _entry;
        private readonly IReadOnlySet<string> _foreign;
        private readonly string[]? _report;
        private readonly Func<int, string, bool>? _live;
        private readonly Func<string, long, long?, bool>? _noReferenceAt;

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
        // A COPY RUN FOR WHAT NOBODY FOLLOWS (Unseen) IS ONE NODE: everything
        // it is handed is the unknown object, so every register in it holds
        // what the unknown object reaches and what it makes -- as one node
        // they hold the same, and the copies cost one node each rather than
        // a second body of every function a root reaches, which ran the
        // compiler's own link out of memory.
        private readonly List<bool> _copyCollapsed = new();
        private readonly Dictionary<long, int> _copyIds = new();
        private readonly List<int>?[] _copiesOf;
        // Per function: the copies made for an object (MostContexts).
        private readonly int[] _contexts;
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
        // Per object: a node holding what every cell of it holds (-1 until
        // asked for), and whether its cells were folded into the any-offset one.
        private readonly List<int> _allCells = new();
        private readonly List<bool> _collapsed = new();
        // And a node holding what a read at any offset reads (ReadAnywhere):
        // every cell but a stamped object's stamp (-1 until asked for).
        private readonly List<int> _readCells = new();

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
        // What else a node is used for, made only for the nodes that are:
        // most are copied from and into, and nothing more.
        private readonly List<Uses?> _uses = new();
        // Per node: a cell no reference is ever kept in (HoldsNoReference),
        // which holds the unknown at most.
        private readonly List<bool> _noReference = new();
        // Per node: it held more than MostHeld, and holds the unknown object instead (Saturate).
        private readonly List<bool> _saturated = new();
        private readonly List<MemCopyRecord> _memcopyRecords = new();
        private readonly List<Binding> _bindings = new();
        private readonly Queue<int> _work = new();
        private long _held, _steps, _edgesSinceCollapse;
        private bool _over;
        // For a report: the node being carried on (~node: one saturating),
        // and per object, what first put it where nobody follows.
        private int _from = -1;
        private readonly Dictionary<int, int> _escapedFrom = new();
        private readonly long _heapAtStart = GC.GetTotalMemory(false);
        private long _heapLimit = HeapBudget;

        public Solver(IReadOnlyList<RegionHints> units, Dictionary<string, string[]> virtuals, Func<string, long, string?> methodAt,
            string entry, IReadOnlySet<string> foreign, string? report, Func<int, string, bool>? live, int depth,
            Func<string, long, long?, bool>? noReference)
        {
            _maxDepth = depth;
            _units = units; _virtuals = virtuals; _methodAt = methodAt; _entry = entry; _foreign = foreign; _live = live;
            _noReferenceAt = noReference;
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
            _contexts = new int[_functions.Count];
        }

        private void Log(string text) => Console.Error.WriteLine("regions: " + text);

        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>The solve has outgrown its budget, inside a step: it stops, and nothing is made in a region.</summary>
        private sealed class OverBudget : Exception { }

        public RegionFacts?[]? Run()
        {
            // Met anywhere -- a step's watchers adding without end, a copy made
            // while binding or rooting -- the budget is giving up, never an
            // unhandled exception.
            try { return Steps(); }
            catch (OverBudget)
            {
                _over = true;
                return GiveUp("too much to hold");
            }
        }

        private RegionFacts?[]? Steps()
        {
            NewObject(-1, -1, -1, 0);                               // Global
            // WHERE THE PROGRAM STARTS, each called with anything: the entry
            // (a unit's own symbol, it may be), whatever code outside the IR
            // names, and every function whose address the program takes --
            // a call nobody can name may reach it, and so may the kernel,
            // handed it by a system call (a signal's handler), whether or not
            // the IR makes any such call.
            SortedSet<int> started = new(), addressed = new();
            for (int f = 0; f < _functions.Count; f++)
                if (_functions[f].Name == _entry || _foreign.Contains(_functions[f].Name)) started.Add(f);
            if (started.Count == 0) return GiveUp("no entry " + _entry);
            for (int u = 0; u < _units.Count; u++)
                foreach (string name in _units[u].AddressTaken)
                    if (Resolve(u, name) is { } targets) addressed.UnionWith(targets);
            // The entry and what code outside the IR names run as they are
            // written, in the copy every followed call runs: the program
            // itself, Main and all it calls, is beneath them. What only an
            // address reaches runs a copy of its own (Unseen), whose callees
            // run in theirs.
            foreach (int f in started) Root(f, unseen: false);
            foreach (int f in addressed) Root(f, unseen: true);
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
                        {
                            int copy = CopyOf(g, -1, binding.Copy);
                            if (binding.Seen.Add(copy)) To(binding.Copy, binding.Call, copy, receiver: false);
                        }
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
                + $"{_locationObject.Count} locations, {_held} held, {_steps} steps, {_clock.ElapsedMilliseconds} ms");
            return Judge();
        }

        private RegionFacts?[]? GiveUp(string why)
        {
            TooBig = _over || _walked > JudgeBudget;
            Log($"gave up ({why}) with contexts {_maxDepth} deep at {_parent.Count} nodes, {_copyFunction.Count} copies, {_locationObject.Count} locations, {_held} held");
            if (_report is not null) Largest();
            return null;
        }

        /// <summary>
        /// The context of a root's copy: CODE NOBODY FOLLOWS CALLS IT with
        /// the unknown object, in a copy of its own. Called so in the copy
        /// every call that is followed runs, every function whose address is
        /// taken -- each virtual method, through its descriptor -- had the
        /// unknown object for parameters wherever it was called: a foreach
        /// over a list, whose enumerator lives in a frame and runs MoveNext's
        /// context-free copy, put each element where nobody follows, and the
        /// compiler's syntax trees all outlived every boundary.
        /// </summary>
        private const int Unseen = -2;

        // A function called from where nobody can say, with anything, its return going anywhere.
        private void Root(int f, bool unseen)
        {
            int copy = CopyOf(f, unseen ? Unseen : -1);
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
            _uses.Add(null);
            _noReference.Add(false);
            _saturated.Add(false);
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
            _allCells.Add(-1);
            _collapsed.Add(false);
            _readCells.Add(-1);
            return _objectFunction.Count - 1;
        }

        // Past the heap budget. Counted without a collection, the heap is
        // garbage too: what is still held after one decides, and the next
        // look waits for garbage to pile up again.
        private bool OverHeap()
        {
            if (GC.GetTotalMemory(false) - _heapAtStart <= _heapLimit) return false;
            long live = GC.GetTotalMemory(true) - _heapAtStart;
            if (live > HeapBudget) return true;
            _heapLimit = Math.Max(HeapBudget, live + HeapBudget / 4);
            return false;
        }

        private static long Key(int function, int context) => ((long)function << 32) | (uint)(context + 1);

        private int Node(int copy, int local) => _copyCollapsed[copy] ? _copyBase[copy] : _copyBase[copy] + local;

        /// <summary>A function's copy for an object it is called on (-1: none; Unseen: code nobody follows calls it), made on first use with its constraints.</summary>
        private int CopyOf(int f, int context, int from = -1)
        {
            RegionFunction function = _functions[f];
            // WHAT AN UNSEEN COPY CALLS IS UNSEEN TOO: its one node holds the
            // unknown object, and handed to a function's shared copy it would
            // be every caller's argument there.
            if (from >= 0 && _copyCollapsed[from]) context = Unseen;
            if (context == Unseen) { }
            else if (!function.Instance) context = -1;
            else if (context >= 0 && (_objectSite[context] < 0 || _objectDepth[context] >= _maxDepth)) context = -1;
            if (_copyIds.TryGetValue(Key(f, context), out int known)) return known;
            // A METHOD CALLED ON MANY OBJECTS is called on the rest without a
            // context: each object its own copy multiplied the objects made
            // in them, and those the copies (RegionPointsTo.MostContexts).
            if (context >= 0)
            {
                if (_contexts[f] >= MostContexts) return CopyOf(f, -1);
                _contexts[f]++;
            }
            int copy = _copyFunction.Count;
            _copyIds[Key(f, context)] = copy;
            _copyFunction.Add(f);
            _copyContext.Add(context);
            _copyBase.Add(_parent.Count);
            _copyCollapsed.Add(context == Unseen);
            (_copiesOf[f] ??= new()).Add(copy);
            _callees.Add(new());
            _callers.Add(new());
            if (context == Unseen) NewNode();
            else for (int n = 0; n < function.Nodes; n++) NewNode();
            if (_over) return copy;
            if (context >= 0) Add(Node(copy, 0), Location(context, 0));
            if (context == Unseen) Add(Node(copy, 0), GlobalLocation);
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
            // Plain offsets arrive clamped (Plain); one with StrideBit is an element's word.
            int at = o == Global || _collapsed[o] || offset < 0 || offset > FarthestField && (offset > 0x7FFF || !IsStrided((int)offset)) ? Any : (int)offset;
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

        // A pointer already inside an object moved again is anywhere in it
        // (RegionPointsTo.Shifted): a field's address is the object's moved
        // once, and a count that once held a pointer, stepped through memory
        // where Walked cannot see the loop, made every object a location per
        // step.
        private int Shifted(int loc, long shift)
        {
            if (shift == 0) return loc;
            int o = _locationObject[loc], offset = _locationOffset[loc];
            if (o == Global) return loc;
            if (shift == RegionConstraint.Any || offset == Any) return Location(o, Any);
            if (RegionConstraint.IsIndex(shift) || RegionConstraint.IsMovedBy(shift))
            {
                int k = (int)(shift - (RegionConstraint.IsMovedBy(shift) ? RegionConstraint.MovedBy : RegionConstraint.Index));
                if (k < 1) return Location(o, Any);
                return Location(o, IsStrided(offset) ? Strided(Math.Min(k, StrideShiftOf(offset)), ResidueOf(offset)) : Strided(k, offset));
            }
            if (shift > FarthestField || shift < -FarthestField) return Location(o, Any);
            if (IsStrided(offset)) return Location(o, StridedPlus(offset, shift));
            if (offset != 0) return Location(o, Any);
            return Location(o, shift);
        }

        // A location carried along a copy: moved, or -- the unknown object
        // along an index scaled into an address (RegionConstraint.Index,
        // RegionPointsTo.IndexShift) -- dropped.
        private void AddShifted(int to, int loc, long shift)
        {
            if (RegionConstraint.IsIndex(shift) && _locationObject[loc] == Global) return;
            Add(to, Shifted(loc, shift));
        }

        /// <summary>The node of one of an object's cells, made on first use.</summary>
        private int Cell(int loc)
        {
            if (_cellNode[loc] >= 0) return Rep(_cellNode[loc]);
            int o = _locationObject[loc];
            bool fixedOffset = _locationOffset[loc] != Any;
            // TOO MANY OFFSETS: the object becomes its any-offset cell.
            if (fixedOffset && (_collapsed[o] || _objectCells[o] is { Count: >= MostCells }))
            {
                int any = Collapse(o);
                _cellNode[loc] = any;
                return any;
            }
            int node = NewNode();
            _cellNode[loc] = node;
            if (HoldsNoReference(o, _locationOffset[loc])) _noReference[node] = true;
            if (_allCells[o] >= 0) Edge(node, _allCells[o], 0);
            if (_readCells[o] >= 0 && _locationOffset[loc] != 0) Edge(node, _readCells[o], 0);
            if (fixedOffset)
            {
                // A cell written at any offset is read wherever this one is.
                Edge(Cell(Location(o, Any)), node, 0);
                // Words that may be this one are this one, both ways (MayOverlap).
                if (_objectCells[o] is { } others)
                {
                    int firstData = FirstData(o), mine = _locationOffset[loc];
                    foreach (int other in others.ToArray())
                        if (MayOverlap(mine, _locationOffset[other], firstData)) { int at = Cell(other); Edge(node, at, 0); Edge(at, node, 0); }
                }
                (_objectCells[o] ??= new()).Add(loc);
                if (_objectWatchers[o] is { } watchers)
                    for (int w = 0; w < watchers.Count; w++) Watch(watchers[w], loc, node);
            }
            return node;
        }

        /// <summary>
        /// TOO MANY OFFSETS: the object becomes one any-offset cell
        /// (RegionPointsTo.Collapse). Each cell it had flows into that one,
        /// which already flows into each, so whatever is read from it anywhere
        /// is everything ever written to it; a location in it is any offset
        /// from here on.
        /// </summary>
        private int Collapse(int o)
        {
            int any = Cell(Location(o, Any));
            if (_collapsed[o]) return any;
            _collapsed[o] = true;
            if (_objectCells[o] is { } cells)
                foreach (int loc in cells.ToArray()) Edge(Cell(loc), any, 0);
            return Rep(any);
        }

        /// <summary>The node holding what every cell of an object holds: what a read at any offset reads.</summary>
        private int AllCells(int o)
        {
            if (_collapsed[o]) return Cell(Location(o, Any));
            if (_allCells[o] >= 0) return Rep(_allCells[o]);
            int any = Cell(Location(o, Any));
            int all = NewNode();
            _allCells[o] = all;
            if (HoldsNoReference(o, Any)) _noReference[all] = true;
            Edge(any, all, 0);
            if (_objectCells[o] is { } cells)
                foreach (int loc in cells.ToArray()) Edge(Cell(loc), all, 0);
            return all;
        }

        /// <summary>
        /// WHAT A READ AT ANY OFFSET READS (RegionPointsTo.ReadAnywhere): every
        /// cell of the object but the stamp of one its site stamps with a
        /// descriptor -- read-only data, written there by the stamp alone and
        /// read at its own offset. Read with the elements, it was the unknown
        /// object in every element read.
        /// </summary>
        private int ReadAnywhere(int o)
        {
            if (_collapsed[o] || o == Global || _objectSite[o] < 0 || _functions[_objectFunction[o]].Sites[_objectSite[o]].Table is null) return AllCells(o);
            if (_readCells[o] >= 0) return Rep(_readCells[o]);
            int any = Cell(Location(o, Any));
            int read = NewNode();
            _readCells[o] = read;
            if (HoldsNoReference(o, Any)) _noReference[read] = true;
            Edge(any, read, 0);
            if (_objectCells[o] is { } cells)
                foreach (int loc in cells.ToArray()) if (_locationOffset[loc] != 0) Edge(Cell(loc), read, 0);
            return read;
        }

        /// <summary>
        /// A WORD THE COLLECTOR NEVER READS AS A REFERENCE holds none
        /// (RegionPointsTo.HoldsNoReference): nothing kept there is an object
        /// anyone reaches by it, and what is read from it is a number, or the
        /// unknown at most. A leaf is never scanned; an object made with a
        /// descriptor is scanned by it, which the link reads from the image.
        /// </summary>
        private bool HoldsNoReference(int o, int offset)
        {
            if (o == Global || _objectSite[o] < 0) return false;
            RegionSite site = _functions[_objectFunction[o]].Sites[_objectSite[o]];
            if (site.Words == RegionWords.Leaf) return true;
            if (site.Words != RegionWords.Described || site.Table is not { } table || _noReferenceAt is null) return false;
            long? at = offset == Any ? null : offset;
            if (_noReferenceWords.TryGetValue((table, site.At, at), out bool known)) return known;
            return _noReferenceWords[(table, site.At, at)] = _noReferenceAt(table, site.At, at);
        }

        private readonly Dictionary<(string, long, long?), bool> _noReferenceWords = new();

        private void Watch(Watcher w, int loc, int cell)
        {
            int offset = _locationOffset[loc];
            // An element's word copied as a block: to the word at the same residue, moved as the block is.
            if (IsStrided(offset)) Edge(cell, Cell(Location(w.To, StridedPlus(offset, w.At - w.From))), 0);
            else if (offset >= w.From && (w.Count == RegionConstraint.Any || offset - w.From < w.Count))
                Edge(cell, Cell(Location(w.To, Plain(w.At + offset - w.From))), 0);
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
            // A node that holds no reference holds the unknown at most.
            if (_noReference[node]) loc = GlobalLocation;
            else if (_saturated[node] && _locationObject[loc] != Global) { Escape(loc); loc = GlobalLocation; }
            SparseSet set = _pts[node] ??= new();
            if (!set.Add(loc)) return;
            // For a report: what first put each location where nobody follows.
            if (_report is not null && _locationObject[loc] != Global && node == Rep(Cell(GlobalLocation))) _escapedFrom.TryAdd(_locationObject[loc], _from);
            // Checked here, not only between steps: one step's watchers can
            // add without end, and the link died inside it before the loop
            // looked again.
            if ((++_held & 4095) == 0 && (_held > HeldBudget || OverHeap())) throw new OverBudget();
            List<int>? delta = _delta[node];
            if (delta is null) { _delta[node] = delta = new(); _work.Enqueue(node); }
            delta.Add(loc);
            if (set.Count > MostHeld && node != Rep(Cell(GlobalLocation))) Saturate(node);
        }

        /// <summary>
        /// A NODE THAT HOLDS TOO MUCH holds the unknown object instead, and
        /// what it held, and is handed from here on, escapes: each location
        /// is put where nobody follows (the unknown object's cell), so what
        /// reaches it is reached from there, and the unknown object stands
        /// for it wherever the node is read, stored through or called on --
        /// everything those could reach is already reached from the unknown
        /// object. A context-free copy of a shared generic method (a List's,
        /// a Dictionary's) holds every object any list holds; past this, one
        /// set of thousands carried to every node it feeds was the compiler's
        /// own build holding a hundred million.
        /// </summary>
        private void Saturate(int node)
        {
            int from = _from;
            _from = ~node;
            _saturated[node] = true;
            int[] held = _pts[node]!.ToArray();
            _pts[node] = new SparseSet();
            _held -= held.Length;
            List<int> delta = _delta[node] ??= new();
            if (delta.Count == 0) _work.Enqueue(node);
            delta.Clear();
            foreach (int loc in held) if (_locationObject[loc] != Global) Escape(loc);
            _pts[node]!.Add(GlobalLocation);
            _held++;
            delta.Add(GlobalLocation);
            _from = from;
        }

        private void Escape(int loc) => Add(Cell(GlobalLocation), loc);

        private void Edge(int from, int to, long shift)
        {
            from = Rep(from); to = Rep(to);
            if (from == to && shift == 0) return;
            // A node that holds no reference holds the unknown at most (Add),
            // and gives no more -- and only once something is put in it: a
            // word only ever written numbers holds nothing anyone follows.
            // RegionPointsTo gives the unknown along every such edge at once,
            // and an instance read at any offset, through the numbers beside
            // its references, was the unknown, and a store through it kept
            // everything stored (973's terms, in its Main).
            List<(int To, long Shift)> edges = _edges[from] ??= new();
            if (_uses[from]?.EdgeSet is { } set) { if (!set.Add((to, shift))) return; }
            else
            {
                // A short list searched while short, a set beside it past
                // that: most nodes have one or two edges.
                for (int e = 0; e < edges.Count; e++) if (edges[e].To == to && edges[e].Shift == shift) return;
                if (edges.Count >= 16)
                {
                    HashSet<(int, long)> made = new();
                    foreach (var e in edges) made.Add((e.To, e.Shift));
                    made.Add((to, shift));
                    Use(from).EdgeSet = made;
                }
            }
            edges.Add((to, shift));
            if (shift == 0) _edgesSinceCollapse++;
            if (_pts[from] is not { } pts) return;
            int carried = _from;
            _from = from;
            // The unknown object's cell takes what escapes while this walks (Saturate).
            if (from == to || from == Rep(Cell(GlobalLocation))) { foreach (int loc in pts.ToArray()) AddShifted(to, loc, shift); }
            else
            {
                // Walked in place: what is added goes to another node.
                for (int b = 0; b < pts.Blocks; b++)
                {
                    int key = pts.KeyAt(b) << 6;
                    for (ulong bits = pts.BitsAt(b); bits != 0; bits &= bits - 1)
                    {
                        int loc = key + System.Numerics.BitOperations.TrailingZeroCount(bits);
                        if (shift == 0) Add(to, loc); else AddShifted(to, loc, shift);
                    }
                }
            }
            _from = carried;
        }

        private void Leak(int node) => Edge(node, Cell(GlobalLocation), 0);

        private Uses Use(int node) => _uses[node] ??= new Uses();

        private void Load(int dest, int baseNode, long offset)
        {
            baseNode = Rep(baseNode);
            (Use(baseNode).Loads ??= new()).Add((dest, offset));
            if (_pts[baseNode] is { } pts) foreach (int loc in pts.ToArray()) Loaded(loc, dest, offset);
        }

        private void Loaded(int loc, int dest, long offset)
        {
            int o = _locationObject[loc];
            // Out of the unknown object comes the unknown object.
            if (o == Global) { Add(dest, GlobalLocation); return; }
            int from = _locationOffset[loc];
            long at = from == Any ? Any : IsStrided(from) ? StridedPlus(from, offset) : Plain(from + offset);
            int cell = Location(o, at);
            // A read at any offset reads one node per object, not an edge
            // from every cell to every such read.
            Edge(_locationOffset[cell] == Any ? ReadAnywhere(o) : Cell(cell), dest, 0);
        }

        private void Store(int baseNode, long offset, int value)
        {
            baseNode = Rep(baseNode);
            (Use(baseNode).Stores ??= new()).Add((value, offset));
            if (_pts[baseNode] is { } pts) foreach (int loc in pts.ToArray()) Stored(loc, value, offset);
        }

        private void Stored(int loc, int value, long offset)
        {
            int o = _locationObject[loc], from = _locationOffset[loc];
            long at = o == Global || from == Any ? Any : IsStrided(from) ? StridedPlus(from, offset) : Plain(from + offset);
            Edge(value, Cell(Location(o, at)), 0);
        }

        private void MemCopy(int to, int from, long count)
        {
            int id = _memcopyRecords.Count;
            _memcopyRecords.Add(new MemCopyRecord { To = to, From = from, Count = count });
            to = Rep(to); from = Rep(from);
            (Use(to).MemCopies ??= new()).Add(id);
            if (from != to) (Use(from).MemCopies ??= new()).Add(id);
            if (_pts[from] is { } sources) foreach (int loc in sources.ToArray()) Copied(id, loc, true);
            if (_pts[to] is { } destinations) foreach (int loc in destinations.ToArray()) Copied(id, loc, false);
        }

        // A new location at one end of a block copy: met with each one seen
        // at the other end once, or, past MostPairs, through one node.
        private void Copied(int id, int loc, bool atSource)
        {
            MemCopyRecord r = _memcopyRecords[id];
            if (!(atSource ? r.SourcesSeen : r.DestinationsSeen).Add(loc)) return;
            (atSource ? r.Sources : r.Destinations).Add(loc);
            if (r.Through < 0 && (long)r.Sources.Count * r.Destinations.Count > MostPairs)
            {
                // TOO MANY PAIRS -- a copy through pointers that may each be
                // any of hundreds of objects -- and every source is copied to
                // every destination through one node, at any offset.
                r.Through = NewNode();
                foreach (int src in r.Sources) Into(r, src);
                foreach (int dst in r.Destinations) OutOf(r, dst);
                return;
            }
            if (r.Through >= 0)
            {
                if (atSource) Into(r, loc); else OutOf(r, loc);
                return;
            }
            List<int> others = atSource ? r.Destinations : r.Sources;
            for (int k = 0; k < others.Count; k++)
                if (atSource) Pair(r, loc, others[k]); else Pair(r, others[k], loc);
        }

        private void Into(MemCopyRecord r, int src)
        {
            int o = _locationObject[src];
            if (o == Global) Add(r.Through, GlobalLocation);
            else Edge(_locationOffset[src] == Any ? ReadAnywhere(o) : AllCells(o), r.Through, 0);
        }

        private void OutOf(MemCopyRecord r, int dst) => Edge(r.Through, Cell(Location(_locationObject[dst], Any)), 0);

        private void Pair(MemCopyRecord r, int src, int dst)
        {
            int os = _locationObject[src], od = _locationObject[dst];
            int ds = _collapsed[os] ? Any : _locationOffset[src], dd = _collapsed[od] ? Any : _locationOffset[dst];
            if (os == Global)
            {
                Add(Cell(Location(od, Any)), GlobalLocation);
                return;
            }
            // An element of an array of structs copied in or out, word by word
            // at its residues (RegionPointsTo's Pair).
            if ((IsStrided(ds) || IsStrided(dd)) && ds != Any && dd != Any && od != Global && r.Count != RegionConstraint.Any && r.Count is > 0 and <= FarthestField)
            {
                for (long k = 0; k < r.Count; k += 4)
                {
                    long from = IsStrided(ds) ? StridedPlus(ds, k) : Plain(ds + k), into = IsStrided(dd) ? StridedPlus(dd, k) : Plain(dd + k);
                    Edge(Cell(Location(os, from)), Cell(Location(od, into)), 0);
                }
                Edge(Cell(Location(os, Any)), Cell(Location(od, Any)), 0);
                return;
            }
            if (IsStrided(ds)) ds = Any;
            if (IsStrided(dd)) dd = Any;
            // A copy at any offset depends only on the two objects.
            if (ds == Any || dd == Any || od == Global)
            {
                // From any offset, what a read there reads (ReadAnywhere).
                if (r.AnyPairs.Add(((long)(ds == Any ? ~os : os) << 32) | (uint)od))
                    Edge(ds == Any ? ReadAnywhere(os) : AllCells(os), Cell(Location(od, Any)), 0);
                return;
            }
            EachCell(os, new Watcher { To = od, From = ds, At = dd, Count = r.Count });
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
                else To(copy, call, CopyOf(g, -1, copy));
            }
            if (instance.Count > 0) Watch(new Binding { Copy = copy, Call = call, Direct = instance }, Node(copy, call.Arguments[0]));
        }

        private void Watch(Binding binding, int receiver)
        {
            int id = _bindings.Count;
            _bindings.Add(binding);
            receiver = Rep(receiver);
            (Use(receiver).Receivers ??= new()).Add(id);
            if (_pts[receiver] is { } pts) foreach (int loc in pts.ToArray()) Received(id, loc);
        }

        /// <summary>
        /// A LOCATION A RECEIVER MAY BE goes to the copies that run on it, and
        /// to no other: the callee's copy for its object, the method its
        /// descriptor holds -- or, on an object of no known type, every
        /// override -- each handed this location as `this` and nothing else
        /// the receiver holds (RegionPointsTo.Bind's rule for the object a
        /// copy is for). A copy shared past the contexts, or one in no
        /// object's context, was handed every receiver of the call by an
        /// edge: every ToString in the compiler's own build was handed the
        /// six thousand objects anything was printed from.
        /// </summary>
        private void Received(int id, int loc)
        {
            Binding binding = _bindings[id];
            int o = _locationObject[loc];
            int caller = _copyFunction[binding.Copy];
            if (binding.Direct is not null)
            {
                binding.Unbound = false;
                // On the unknown object, the copy code nobody follows runs (Unseen).
                int context = o == Global ? Unseen : _locationOffset[loc] == 0 ? o : -1;
                foreach (int g in binding.Direct) Handed(binding, CopyOf(g, context, binding.Copy), loc);
                return;
            }
            // A virtual call runs, on an object whose stamp is known, the
            // method its descriptor holds; on any other, every override.
            if (o != Global && _objectSite[o] >= 0 && _functions[_objectFunction[o]].Sites[_objectSite[o]] is { Table: { } table } site
                && SlotOf(binding.Call.Callee!) is long slot && _methodAt(table, site.At + slot) is { } method
                && binding.Overrides!.Contains(method, StringComparer.Ordinal))
            {
                foreach (int g in Resolve(_unitOf[caller], method)!)
                {
                    _named[caller].Add(g);
                    Handed(binding, CopyOf(g, o, binding.Copy), loc);
                }
                return;
            }
            foreach (string target in binding.Overrides!)
                foreach (int g in Resolve(_unitOf[caller], target)!)
                {
                    _named[caller].Add(g);
                    Handed(binding, CopyOf(g, o == Global ? Unseen : -1, binding.Copy), loc);
                }
        }

        // The call made to a copy once, its receiver aside; and this location its `this`.
        private void Handed(Binding binding, int callee, int loc)
        {
            if (binding.Seen.Add(callee)) To(binding.Copy, binding.Call, callee, receiver: false);
            if (_functions[_copyFunction[callee]].Parameters > 0) Add(Node(callee, 0), loc);
        }

        // A virtual call made on nothing that can hold an address: every override, as it is.
        private void AllOverrides(Binding binding)
        {
            int u = _unitOf[_copyFunction[binding.Copy]];
            foreach (string target in binding.Overrides!)
                foreach (int g in Resolve(u, target)!)
                {
                    _named[_copyFunction[binding.Copy]].Add(g);
                    To(binding.Copy, binding.Call, CopyOf(g, -1, binding.Copy));
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

        // The call made: its arguments the callee copy's parameters -- its
        // receiver too, unless each location of it is handed over alone
        // (Received) -- and its return the call's result.
        private void To(int caller, RegionCall call, int callee, bool receiver = true)
        {
            Beneath(caller, callee);
            RegionFunction g = _functions[_copyFunction[callee]];
            for (int k = receiver ? 0 : 1; k < call.Arguments.Length && k < g.Parameters; k++)
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
                if ((++_steps & 63) == 0 && OverHeap()) { _over = true; return false; }
                foreach (int loc in delta) Propagate(node, loc);
                if (_edgesSinceCollapse > 50_000 + _parent.Count / 4) Collapse();
            }
            return !_over;
        }

        private void Propagate(int node, int loc)
        {
            _from = node;
            if (_edges[node] is { } edges)
                for (int e = 0; e < edges.Count; e++) AddShifted(edges[e].To, loc, edges[e].Shift);
            Uses? uses = _uses[node];
            if (uses is null) return;
            if (uses.Loads is { } loads)
                for (int k = 0; k < loads.Count; k++) Loaded(loc, loads[k].Dest, loads[k].Offset);
            if (uses.Stores is { } stores)
                for (int k = 0; k < stores.Count; k++) Stored(loc, stores[k].Value, stores[k].Offset);
            if (uses.MemCopies is { } copies)
                for (int k = 0; k < copies.Count; k++)
                {
                    MemCopyRecord r = _memcopyRecords[copies[k]];
                    if (Rep(r.From) == node) Copied(copies[k], loc, true);
                    if (Rep(r.To) == node) Copied(copies[k], loc, false);
                }
            if (uses.Receivers is { } receivers)
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
                        // A cell that holds no reference is never merged: it
                        // holds less than what feeds it.
                        if (w == v || _noReference[w]) continue;
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
            Uses? uses = _uses[a];
            List<(int Dest, long Offset)>? loads = uses?.Loads;
            List<(int Value, long Offset)>? stores = uses?.Stores;
            List<int>? copies = uses?.MemCopies;
            List<int>? receivers = uses?.Receivers;
            _pts[a] = null; _edges[a] = null; _uses[a] = null;
            if (edges is not null) foreach (var e in edges) Edge(b, e.To, e.Shift);
            if (loads is not null) foreach (var l in loads) Load(l.Dest, b, l.Offset);
            if (stores is not null) foreach (var s in stores) Store(b, s.Offset, s.Value);
            if (copies is not null)
                foreach (int id in copies)
                {
                    (Use(b).MemCopies ??= new()).Add(id);
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
                    (Use(b).Receivers ??= new()).Add(id);
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

        // Which objects each object's cells point into, found once.
        private int[][]? _pointsInto;

        // Objects reached from `start` through their cells, past those in `stop`.
        private void Reach(IEnumerable<int> start, HashSet<int> reached, HashSet<int>? stop)
        {
            if (_pointsInto is null)
            {
                _pointsInto = new int[_objectFunction.Count][];
                HashSet<int> into = new();
                for (int o = 0; o < _objectFunction.Count; o++)
                {
                    into.Clear();
                    List<int> cells = new(_objectCells[o] ?? new List<int>());
                    if (_locations.TryGetValue(((long)o << 16) | (uint)Any, out int any)) cells.Add(any);
                    foreach (int loc in cells)
                        if (_cellNode[loc] >= 0) into.UnionWith(ObjectsHeld(_cellNode[loc]));
                    _pointsInto[o] = into.ToArray();
                }
            }
            Stack<int> next = new();
            foreach (int o in start)
                if ((stop is null || !stop.Contains(o)) && reached.Add(o)) next.Push(o);
            while (next.TryPop(out int o))
            {
                _walked++;
                foreach (int held in _pointsInto[o])
                    if ((stop is null || !stop.Contains(held)) && reached.Add(held)) next.Push(held);
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
            // What is kept of these answers has a bound of its own: past it
            // they are forgotten, and asked again when wanted.
            _outlivingHeld += reached.Count;
            if (_outlivingHeld > OutlivingBudget) { _outliving.Clear(); _outlivingHeld = reached.Count; }
            return _outliving[c] = reached;
        }

        private const long OutlivingBudget = 8_000_000;
        private long _outlivingHeld;

        private bool Outlives(int o, int c) => _globalReach.Contains(o) || Outliving(c).Contains(o);

        private bool IsSite(int o) => o > Global && _objectSite[o] >= 0;

        // Sites one of whose objects code nobody follows may make and keep.
        private readonly HashSet<(int, int)> _unseenKept = new();

        private RegionFacts?[]? Judge()
        {
            _globalReach = new();
            Reach(new[] { Global }, _globalReach, null);
            // WHAT CODE NOBODY FOLLOWS MAKES AND KEEPS is never taken. A copy
            // a root reaches may run beneath a call this cannot see, inside a
            // region opened above that call and above no copy of it here: an
            // object such a copy makes that the unknown object reaches would
            // be made in that region and outlive it. Its site goes to the heap
            // wherever it runs.
            HashSet<int> unseen = new(_roots);
            Stack<int> walk = new(_roots);
            while (walk.TryPop(out int c))
                foreach (int callee in _callees[c])
                    if (unseen.Add(callee)) walk.Push(callee);
            for (int o = 1; o < _objectFunction.Count; o++)
                if (IsSite(o) && _globalReach.Contains(o) && _objectMakers[o].Any(unseen.Contains))
                    _unseenKept.Add((_objectFunction[o], _objectSite[o]));
            bool[] recursive = Recursive(), beforeBlock = BeforeThreadBlock();
            // Nor what the entry calls itself -- Main, the runtime's start --
            // as RegionPointsTo.EntryCalls: Main never (its summary says it
            // may not be one), the rest only while the entry calls Main, not
            // when it took Main into itself and calls what Main calls.
            HashSet<int> started = new();
            for (int f = 0; f < _functions.Count; f++)
                if (_functions[f].Name == _entry && _named[f].Any(g => _functions[g].Main)) started.UnionWith(_named[f]);
            bool MayBeBoundary(int f) => _functions[f].MayBeBoundary && !recursive[f] && !beforeBlock[f] && !started.Contains(f) && _functions[f].Name != _entry;

            // Each site's objects, by function and ordinal.
            Dictionary<(int, int), List<int>> bySite = new();
            for (int o = 1; o < _objectFunction.Count; o++)
                if (IsSite(o) && _functions[_objectFunction[o]].Sites[_objectSite[o]].Rewritable)
                {
                    (int, int) key = (_objectFunction[o], _objectSite[o]);
                    (bySite.TryGetValue(key, out List<int>? list) ? list : bySite[key] = new()).Add(o);
                }

            Dictionary<int, List<int>> madeBy = new();
            foreach (List<int> objects in bySite.Values)
                foreach (int o in objects)
                    foreach (int maker in _objectMakers[o])
                        (madeBy.TryGetValue(maker, out List<int>? list) ? list : madeBy[maker] = new()).Add(o);

            // THE NEAREST CALL EACH OBJECT DIES IN (RegionPointsTo.Nearest):
            // from each copy that makes it up through its callers, nearest
            // first, the first whose return it is proved not to outlive. One
            // walk a making copy, for all it makes.
            SortedSet<int> chosen = new();
            foreach ((int made, List<int> objects) in madeBy.OrderBy(pair => pair.Key))
            {
                List<int> open = objects.Where(o => !_globalReach.Contains(o)).ToList();
                if (open.Count == 0) continue;
                Dictionary<int, int> depth = new() { [made] = 0 };
                Queue<int> next = new();
                next.Enqueue(made);
                while (open.Count > 0 && next.TryDequeue(out int c))
                {
                    _walked++;
                    if (MayBeBoundary(_copyFunction[c]))
                        for (int k = open.Count - 1; k >= 0; k--)
                            if (!Outlives(open[k], c)) { chosen.Add(_copyFunction[c]); open.RemoveAt(k); }
                    if (depth[c] >= NearestReach) continue;
                    foreach (int caller in _callers[c].Order())
                        if (depth.TryAdd(caller, depth[c] + 1)) next.Enqueue(caller);
                }
                if (_walked > JudgeBudget) return GiveUp("too much to judge");
            }
            if (_report is not null) foreach (int f in chosen) Log("boundary chosen " + _functions[f].Name);
            // With no boundary at all a loop may still be given a region: Main's.
            if (chosen.Count == 0 && !LoopRegions)
            {
                Log("no boundary found");
                return new RegionFacts?[_units.Count];
            }

            // A BOUNDARY THAT COSTS MORE THAN IT GIVES IS DROPPED: one whose
            // own allocations it frees are fewer than the sites it alone keeps
            // out of an outer boundary's region -- a parser's list returned
            // through a split it chose for its own scratch array.
            HashSet<(int, int)> taken = null!;
            Verdict final = null!;
            for (int round = 0; ; round++)
            {
                if (Evaluate(chosen, bySite, madeBy) is not { } verdict) return GiveUp("too much to judge");
                taken = verdict.Taken;
                final = verdict;
                if (round == 3) break;
                List<int> dropped = chosen.Where(f => verdict.Loss.GetValueOrDefault(f) > verdict.Gain.GetValueOrDefault(f)).ToList();
                if (dropped.Count == 0) break;
                foreach (int f in dropped)
                {
                    chosen.Remove(f);
                    if (_report is not null) Log("boundary dropped " + _functions[f].Name + ": keeps " + verdict.Loss[f] + " sites out, takes " + verdict.Gain.GetValueOrDefault(f));
                }
            }

            // THE LOOPS GIVEN A REGION OF THEIR OWN (RegionPointsTo's "loops"),
            // over the boundaries left, and the sites taken with them.
            List<LoopRegion> loops = LoopRegions ? SelectLoops(final, madeBy, beforeBlock) : new();
            if (loops.Count > 0) taken = TakenWithLoops(loops, final, bySite, madeBy);
            if (chosen.Count == 0 && loops.Count == 0)
            {
                Log("no boundary found");
                return new RegionFacts?[_units.Count];
            }

            // A boundary with nothing taken innermost beneath it opens nothing
            // worth opening: what is taken beneath it is made in another's.
            HashSet<int> opening = new();
            foreach ((int c, List<int> made) in madeBy)
                if (made.Any(o => taken.Contains((_objectFunction[o], _objectSite[o]))))
                    foreach (int b in Above(c)) opening.Add(_copyFunction[b]);
            List<int> opened = chosen.Where(opening.Contains).ToList();

            RegionFacts?[] facts = new RegionFacts?[_units.Count];
            RegionFacts For(int f) => facts[_unitOf[f]] ??= new RegionFacts();
            foreach (int f in opened) For(f).Boundaries.Add(_functions[f].Name);
            foreach ((int f, int site) in taken) For(f).Sites.Add((_functions[f].Name, site));
            foreach (LoopRegion loop in loops) For(loop.Function).Loops.Add((_functions[loop.Function].Name, loop.Shape.Header));
            if (_report is not null) Report(chosen, opened, taken, madeBy);
            Log($"{opened.Count} boundaries, {loops.Count} loops, {taken.Count} sites in the innermost region, of {bySite.Count}; judged by {_clock.ElapsedMilliseconds} ms, {_walked} walked");
            return facts;
        }

        private sealed class Verdict
        {
            public readonly HashSet<(int, int)> Taken = new();
            /// <summary>Per object: the boundary above it, and the one refusing it (-2: more than one).</summary>
            public Dictionary<int, int> Above = null!, Refuser = null!;
            /// <summary>Per boundary: the sites it alone refuses that another boundary is above.</summary>
            public readonly Dictionary<int, int> Loss = new();
            /// <summary>Per boundary: the sites taken that only it is above.</summary>
            public readonly Dictionary<int, int> Gain = new();
        }

        /// <summary>
        /// EVERY BOUNDARY THAT CAN BE INNERMOST ABOVE A SITE, in any copy that
        /// makes it, must outlive none of its objects: the sites taken, and
        /// what each boundary costs and gives. Runtime.AllocRegion takes the
        /// region opened innermost, so a boundary farther up than another on
        /// every way to the site is never the one it is made in: the walk up
        /// from each making copy stops at the first boundary on each way
        /// (NearestAbove), however far up that is. Walking down from every
        /// boundary, every boundary above every object was judged: the
        /// compiler's own build judged for seven minutes. Null past the budget.
        /// </summary>
        private Verdict? Evaluate(SortedSet<int> chosen, Dictionary<(int, int), List<int>> bySite, Dictionary<int, List<int>> madeBy)
        {
            NearestAbove(chosen);
            // Per object: the boundary above it and the one refusing it, -1
            // for none and -2 for more than one.
            Dictionary<int, int> above = new(), refuser = new();
            static void Note(Dictionary<int, int> into, int o, int f)
            {
                if (!into.TryGetValue(o, out int was)) into[o] = f;
                else if (was != f) into[o] = -2;
            }
            _refusedBy.Clear();
            HashSet<int> outer = new();
            foreach ((int c, List<int> made) in madeBy)
            {
                foreach (int b in Above(c))
                {
                    int f = _copyFunction[b];
                    foreach (int o in made)
                    {
                        _walked++;
                        Note(above, o, f);
                        // Another boundary above this one: open around it
                        // too, for what it leaves -- as every boundary above
                        // an object was noted when every one was judged.
                        if (Above(b, beyond: true).Length > 0) outer.Add(o);
                        if (Outlives(o, b))
                        {
                            Note(refuser, o, f);
                            _refusedBy.TryAdd(o, b);
                        }
                    }
                }
                if (_walked > JudgeBudget) return null;
            }
            // A site is taken when none of its objects is refused and one is
            // beneath some boundary.
            Verdict verdict = new() { Above = above, Refuser = refuser };
            foreach (((int, int) key, List<int> objects) in bySite)
            {
                bool anywhere = objects.Any(above.ContainsKey);
                if (!anywhere || _unseenKept.Contains(key)) continue;
                if (!objects.Any(refuser.ContainsKey))
                {
                    verdict.Taken.Add(key);
                    // Taken only for the one boundary above all of it.
                    int only = -1;
                    foreach (int o in objects)
                        if (above.TryGetValue(o, out int f)) only = (only == -1 || only == f) && !outer.Contains(o) ? f : -2;
                    if (only >= 0) verdict.Gain[only] = verdict.Gain.GetValueOrDefault(only) + 1;
                    continue;
                }
                // Refused by one boundary alone, with another above it too.
                int sole = -1;
                bool other = false;
                foreach (int o in objects)
                {
                    if (refuser.TryGetValue(o, out int r)) sole = sole == -1 || sole == r ? r : -2;
                    if (outer.Contains(o) || above.TryGetValue(o, out int a) && (a == -2 || refuser.GetValueOrDefault(o, -1) != a)) other = true;
                }
                if (sole >= 0 && other) verdict.Loss[sole] = verdict.Loss.GetValueOrDefault(sole) + 1;
            }
            return verdict;
        }

        // Per copy: whether it is a chosen boundary's; per component of the
        // calls between copies, the boundary copies innermost above it.
        private bool[] _isBoundary = Array.Empty<bool>();
        private int[] _component = Array.Empty<int>();
        private int[][] _nearest = Array.Empty<int[]>();

        /// <summary>The boundary copies a region can be opened in innermost when copy `c` runs: itself, if it is one (unless `beyond`), else the first on each way up.</summary>
        private int[] Above(int c, bool beyond = false) => _isBoundary[c] && !beyond ? new[] { c } : _nearest[_component[c]];

        /// <summary>
        /// THE FIRST BOUNDARY ON EACH WAY UP, for every copy at once: over the
        /// components of the calls between copies (Tarjan's, which finds a
        /// component after every one it calls), callers first, each holding
        /// what its callers outside it hold -- a caller that is a boundary
        /// copy itself, else that caller's own. A boundary is never on a
        /// cycle: no recursive function is one.
        /// </summary>
        private void NearestAbove(SortedSet<int> chosen)
        {
            int count = _copyFunction.Count;
            _isBoundary = new bool[count];
            for (int c = 0; c < count; c++) _isBoundary[c] = chosen.Contains(_copyFunction[c]);
            _component = new int[count];
            List<List<int>> components = new();
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
                calls.Push((start, _callees[start].ToArray(), 0));
                while (calls.Count > 0)
                {
                    (int v, int[] callees, int at) = calls.Pop();
                    bool descended = false;
                    while (at < callees.Length)
                    {
                        int w = callees[at++];
                        if (index[w] < 0)
                        {
                            calls.Push((v, callees, at));
                            index[w] = low[w] = next++;
                            stack.Push(w); onStack[w] = true;
                            calls.Push((w, _callees[w].ToArray(), 0));
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
                        do { w = stack.Pop(); onStack[w] = false; component.Add(w); _component[w] = components.Count; } while (w != v);
                        components.Add(component);
                    }
                    if (calls.Count > 0) { int parent = calls.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
                }
            }
            // Found callees first: the last found is called by none found before it.
            _nearest = new int[components.Count][];
            HashSet<int> union = new();
            for (int k = components.Count - 1; k >= 0; k--)
            {
                union.Clear();
                int[]? only = null;
                bool many = false;
                foreach (int m in components[k])
                    foreach (int p in _callers[m])
                    {
                        if (_component[p] == k) continue;
                        _walked++;
                        int[] from = _isBoundary[p] ? new[] { p } : _nearest[_component[p]];
                        if (from.Length == 0) continue;
                        if (only is null && !many) { only = from; continue; }
                        if (ReferenceEquals(only, from)) continue;
                        if (!many) { union.UnionWith(only!); many = true; }
                        union.UnionWith(from);
                    }
                _nearest[k] = many ? union.Order().ToArray() : only ?? Array.Empty<int>();
            }
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

        /// <summary>
        /// WHAT RUNS BEFORE A THREAD'S BLOCK IS ITS OWN: the function that
        /// makes it so (RuntimeAbi.SetThreadBlock) and every function that
        /// calls it, however far up -- the entry stub's first call, a new
        /// thread's first method, which run with no block or the parent's.
        /// A region is opened in the block, so none of these is a boundary:
        /// 214_linq's link opened one in SetThreadBlock and died there.
        /// </summary>
        private bool[] BeforeThreadBlock()
        {
            bool[] before = new bool[_functions.Count];
            List<int>[] callers = new List<int>[_functions.Count];
            for (int f = 0; f < _functions.Count; f++)
                foreach (int g in _named[f]) (callers[g] ??= new()).Add(f);
            Stack<int> next = new();
            for (int f = 0; f < _functions.Count; f++)
                if (_functions[f].Name == RuntimeAbi.SetThreadBlock) { before[f] = true; next.Push(f); }
            while (next.TryPop(out int g))
                if (callers[g] is { } list)
                    foreach (int f in list)
                        if (!before[f]) { before[f] = true; next.Push(f); }
            return before;
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

        // ---- loops ------------------------------------------------------------
        //
        // A LOOP'S LAPS GET A REGION OF THEIR OWN where RegionPointsTo's "loops"
        // would give it one, judged over every unit: a loop whose laps each
        // leave dead what they make in it -- not reachable from what is live
        // where a lap ends (going round again, or out), from the function's
        // frame slots, from what it was handed or hands back, or from a static
        // -- opens a region at its top and gives back what the last lap made
        // there at the top of every lap after (Runtime.RegionLoop). Its laps
        // die only once the whole program is seen when the loop calls into
        // another unit, where every unit's own pass had to let them go.
        //
        // Judged as RegionPointsTo judges it. Not the entry's loops, nor
        // those of what runs before its thread's block is its own (the
        // region would be made in another thread's arena), nor those the unit
        // did not state: an async or iterator body's, a type's initialiser's,
        // or those of a function a throw may be caught in or that takes a
        // label's address (RegionSummary). A loop is given a region where, in
        // every copy of its function, no site taken by the boundaries is
        // refused for it -- but what a lap makes and carries into the next
        // only through what the loop itself writes, which goes to the heap
        // -- and where every lap that goes round makes, in some copy, with
        // no boundary between, something the region takes; a loop that makes
        // something only on a path seldom taken pays for no call at the top
        // of every lap. Then a site is taken when, besides what the
        // boundaries ask, no loop above any of its objects -- one in whose
        // body it is, or whose calls reach the copy that makes it -- finds it
        // live where a lap ends: whichever region is innermost when it is
        // made, it is dead by that region's end. And a loop with nothing taken
        // beneath it is given no region after all.

        /// <summary>The most copies and objects walked choosing loops: past it, the loops left get no region.</summary>
        private const long LoopBudget = 50_000_000;
        private long _loopWalked;
        // How deep AlwaysMakes follows calls made on every way to a return.
        private const int AlwaysDepth = 6;

        /// <summary>
        /// A loop given a region, in each copy of its function: the copies its
        /// calls reach (All), and what is reached from what is live where a
        /// lap ends and from the copy's frame slots, past the unknown object
        /// (LapLive). What outlives the copy (Outlives) outlives a lap too.
        /// </summary>
        private sealed record LoopRegion(int Function, RegionLoopShape Shape, List<(int Copy, HashSet<int> All, HashSet<int> LapLive)> Copies);

        // The functions a call by name, or a virtual call's symbol, in unit `u` may run.
        private readonly Dictionary<(int, string), HashSet<int>> _callFunctions = new();

        private HashSet<int> CallFunctions(int u, string name)
        {
            if (_callFunctions.TryGetValue((u, name), out HashSet<int>? known)) return known;
            HashSet<int> functions = new();
            IEnumerable<string> names = name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)
                ? _virtuals.GetValueOrDefault(name) ?? Array.Empty<string>() : new[] { name };
            foreach (string target in names)
                if (Resolve(u, target) is { } targets) functions.UnionWith(targets);
            return _callFunctions[(u, name)] = functions;
        }

        /// <summary>
        /// The copies call `k` of copy `c` may reach: those `c` calls that are
        /// copies of a function the call names. Never fewer than it reaches;
        /// a call nobody can name reaches what code nobody follows calls,
        /// whose objects a region may take are dead by its return.
        /// </summary>
        private IEnumerable<int> Targets(int c, int k)
        {
            int f = _copyFunction[c];
            if (_functions[f].Calls[k].Callee is not { } name) yield break;
            HashSet<int> functions = CallFunctions(_unitOf[f], name);
            if (functions.Count == 0) yield break;
            foreach (int t in _callees[c])
                if (functions.Contains(_copyFunction[t])) yield return t;
        }

        /// <summary>The copies a loop's calls in copy `c` reach (All), and those reached through no boundary copy (Near): where what is made is made in the loop's region.</summary>
        private (HashSet<int> All, HashSet<int> Near) Reached(int c, int[] calls)
        {
            HashSet<int> all = new(), near = new();
            Stack<int> next = new(), nearNext = new();
            foreach (int k in calls)
                foreach (int t in Targets(c, k))
                {
                    if (all.Add(t)) next.Push(t);
                    if (!_isBoundary[t] && near.Add(t)) nearNext.Push(t);
                }
            while (next.TryPop(out int k))
            {
                _loopWalked++;
                foreach (int callee in _callees[k]) if (all.Add(callee)) next.Push(callee);
            }
            while (nearNext.TryPop(out int k))
                foreach (int callee in _callees[k])
                    if (!_isBoundary[callee] && near.Add(callee)) nearNext.Push(callee);
            return (all, near);
        }

        /// <summary>What the nodes and frame slots given hold reaches in copy `c`, past what the unknown object reaches.</summary>
        private HashSet<int> HeldBy(int c, int[] nodes, IEnumerable<int> slots)
        {
            List<int> start = new();
            foreach (int n in nodes) start.AddRange(ObjectsHeld(Node(c, n)));
            foreach (int slot in slots)
                if (_slotObjects.TryGetValue(((long)c << 32) | (uint)slot, out int o)) start.Add(o);
            HashSet<int> reached = new();
            Reach(start, reached, _globalReach);
            _loopWalked += reached.Count;
            return reached;
        }

        private (int, int) SiteOf(int o) => (_objectFunction[o], _objectSite[o]);

        /// <summary>
        /// Whether one of `calls` of copy `c` always makes something `takes`
        /// (RegionPointsTo.AlwaysMakes): a site its callee makes on every way
        /// to a return, or a call it makes on every way there, through no
        /// boundary copy.
        /// </summary>
        private bool AlwaysMakes(int c, int[] calls, Dictionary<int, List<int>> madeBy, Func<int, bool> takes, HashSet<int> seen, int depth)
        {
            if (depth > AlwaysDepth) return false;
            foreach (int k in calls)
                foreach (int t in Targets(c, k))
                {
                    if (_isBoundary[t] || !seen.Add(t)) continue;
                    RegionFunction g = _functions[_copyFunction[t]];
                    if (madeBy.TryGetValue(t, out List<int>? made) && made.Any(o => Array.BinarySearch(g.MustSites, _objectSite[o]) >= 0 && takes(o))) return true;
                    if (AlwaysMakes(t, g.MustCalls, madeBy, takes, seen, depth + 1)) return true;
                }
            return false;
        }

        /// <summary>
        /// THE LOOPS GIVEN A REGION, judged against the sites the boundaries
        /// take (`verdict`, whose NearestAbove is the boundaries' state): in
        /// every copy, sound -- nothing taken that is made beneath the loop,
        /// in its body or in a copy its calls reach, live where a lap ends,
        /// but what a lap carries only through what the loop writes -- and in
        /// some copy, worth a call at the top of every lap.
        /// </summary>
        private List<LoopRegion> SelectLoops(Verdict verdict, Dictionary<int, List<int>> madeBy, bool[] beforeBlock)
        {
            List<LoopRegion> chosen = new();
            for (int f = 0; f < _functions.Count && _loopWalked < LoopBudget; f++)
            {
                RegionFunction function = _functions[f];
                if (function.Loops.Count == 0 || _copiesOf[f] is not { } copies || function.Name == _entry || beforeBlock[f]) continue;
                foreach (RegionLoopShape loop in function.Loops)
                {
                    if (_loopWalked >= LoopBudget) break;
                    List<(int, HashSet<int>, HashSet<int>)> instances = new();
                    bool sound = true, worth = false;
                    string? why = null;
                    foreach (int c in copies)
                    {
                        (HashSet<int> all, HashSet<int> near) = Reached(c, loop.Calls);
                        HashSet<int> lapLive = HeldBy(c, loop.Live, Enumerable.Range(0, function.Slots));
                        HashSet<int> kept = HeldBy(c, loop.Invariant, loop.KeptSlots);
                        bool Lap(int o) => lapLive.Contains(o) || Outlives(o, c);
                        bool InBody(int o) => Array.BinarySearch(loop.Sites, _objectSite[o]) >= 0;
                        madeBy.TryGetValue(c, out List<int>? own);
                        // Made in a lap, in the region the loop runs in: by the
                        // sites in its body, and in the copies its calls reach
                        // through no boundary.
                        HashSet<int> lapMade = new();
                        if (own is not null) foreach (int o in own) if (near.Contains(c) || InBody(o)) lapMade.Add(o);
                        foreach (int k in near) if (k != c && madeBy.TryGetValue(k, out List<int>? made)) lapMade.UnionWith(made);
                        // CARRIED AND DROPPED: live where a lap ends only through
                        // what the loop itself writes. Refused by the region, it
                        // goes to the heap, as RegionPointsTo sends it.
                        bool Carried(int o) => lapMade.Contains(o) && lapLive.Contains(o) && !kept.Contains(o) && !Outlives(o, c);
                        // Beneath it: the body's own sites, and every copy its calls reach.
                        IEnumerable<int> under = (own ?? new List<int>()).Where(o => all.Contains(c) || InBody(o))
                            .Concat(all.Where(a => a != c).SelectMany(a => madeBy.GetValueOrDefault(a) ?? new List<int>()));
                        foreach (int o in under)
                        {
                            _loopWalked++;
                            if (verdict.Taken.Contains(SiteOf(o)) && Lap(o) && !Carried(o))
                            {
                                sound = false;
                                why = DescribeObject(o) + " is live where a lap ends";
                                break;
                            }
                        }
                        if (!sound) break;
                        instances.Add((c, all, lapLive));
                        if (worth) continue;
                        bool Takes(int o) => !verdict.Refuser.ContainsKey(o) && !Lap(o);
                        worth = own is not null && own.Any(o => Array.BinarySearch(loop.AlwaysSites, _objectSite[o]) >= 0 && Takes(o))
                            || AlwaysMakes(c, loop.AlwaysCalls, madeBy, Takes, new HashSet<int>(), 0);
                    }
                    bool reported = _report is not null && _report.Any(w => function.Name.Contains(w, StringComparison.Ordinal));
                    if (sound && worth)
                    {
                        chosen.Add(new LoopRegion(f, loop, instances));
                        if (reported) Log($"loop region {function.Name} at block {loop.Header}");
                    }
                    else if (reported) Log($"no loop region {function.Name} at block {loop.Header}: " + (sound ? "no lap always makes what it would take" : why));
                }
            }
            if (_loopWalked >= LoopBudget) Log("loops: past the budget, the loops left get no region");
            return chosen;
        }

        /// <summary>
        /// The sites taken with the loops given regions: those the boundaries
        /// take (`verdict`), and those beneath a loop and no boundary -- but
        /// no site with an object a loop above it finds live where a lap
        /// ends. A loop with nothing taken beneath it is dropped from `loops`,
        /// and the sites found again without it.
        /// </summary>
        private HashSet<(int, int)> TakenWithLoops(List<LoopRegion> loops, Verdict verdict, Dictionary<(int, int), List<int>> bySite, Dictionary<int, List<int>> madeBy)
        {
            while (true)
            {
                // Per copy: the loops whose calls reach it, and those in its own body.
                Dictionary<int, List<(int Loop, int Instance)>> over = new(), hosted = new();
                for (int l = 0; l < loops.Count; l++)
                    for (int i = 0; i < loops[l].Copies.Count; i++)
                    {
                        (int c, HashSet<int> all, _) = loops[l].Copies[i];
                        foreach (int a in all) (over.TryGetValue(a, out var list) ? list : over[a] = new()).Add((l, i));
                        (hosted.TryGetValue(c, out var mine) ? mine : hosted[c] = new()).Add((l, i));
                    }
                HashSet<int> under = new(), refused = new();
                Dictionary<int, List<int>> beneath = new();
                foreach ((int m, List<int> made) in madeBy)
                {
                    over.TryGetValue(m, out var reaching);
                    hosted.TryGetValue(m, out var own);
                    if (reaching is null && own is null) continue;
                    foreach (int o in made)
                    {
                        IEnumerable<(int Loop, int Instance)> above = (reaching ?? new()).Concat((own ?? new())
                            .Where(x => Array.BinarySearch(loops[x.Loop].Shape.Sites, _objectSite[o]) >= 0));
                        foreach ((int l, int i) in above)
                        {
                            _walked++;
                            (int c, _, HashSet<int> lapLive) = loops[l].Copies[i];
                            under.Add(o);
                            (beneath.TryGetValue(l, out List<int>? list) ? list : beneath[l] = new()).Add(o);
                            if (lapLive.Contains(o) || Outlives(o, c)) refused.Add(o);
                        }
                    }
                }
                HashSet<(int, int)> taken = new();
                foreach (((int, int) key, List<int> objects) in bySite)
                    if (objects.Any(o => verdict.Above.ContainsKey(o) || under.Contains(o)) && !_unseenKept.Contains(key)
                        && !objects.Any(o => verdict.Refuser.ContainsKey(o) || refused.Contains(o)))
                        taken.Add(key);
                int before = loops.Count;
                for (int l = loops.Count - 1; l >= 0; l--)
                    if (!beneath.TryGetValue(l, out List<int>? objects) || !objects.Any(o => taken.Contains(SiteOf(o))))
                    {
                        if (_report is not null && _report.Any(w => _functions[loops[l].Function].Name.Contains(w, StringComparison.Ordinal)))
                            Log($"loop region dropped {_functions[loops[l].Function].Name} at block {loops[l].Shape.Header}: nothing taken beneath it");
                        loops.RemoveAt(l);
                    }
                if (loops.Count == before) return taken;
            }
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
                                && why.TryGetValue((_objectFunction[o], _objectSite[o]), out string? because) ? " (refused: " + because + ")"
                                : outlives ? " (" + WhyOutlives(o) + ")" : "";
                            lines.Add("  " + verdict + " " + DescribeObject(o) + reason);
                        }
                string state = opened.Contains(f) ? "opened" : chosen.Contains(f) ? "chosen, nothing taken" : "not chosen";
                Log($"boundary {name} context {_copyContext[b]} ({state}): {local} local, {kept} outlive it");
                foreach (string line in lines) Console.Error.WriteLine(line);
            }
        }

        // For a report: each object reached from the unknown object, by the
        // object it was first reached through (Global for one put there).
        private Dictionary<int, int>? _escapeParent;
        private Dictionary<int, int>? _cellOf;

        /// <summary>Why an object outlives a boundary, for a report: the way the unknown object reaches it, and what put the first of that way there.</summary>
        private string WhyOutlives(int o)
        {
            if (!_globalReach.Contains(o)) return "kept by what the boundary is handed or hands back";
            if (_escapeParent is null)
            {
                _escapeParent = new() { [Global] = -1 };
                Queue<int> next = new();
                next.Enqueue(Global);
                while (next.TryDequeue(out int at))
                    foreach (int held in _pointsInto![at])
                        if (_escapeParent.TryAdd(held, at)) next.Enqueue(held);
                _cellOf = new();
                for (int loc = 0; loc < _cellNode.Count; loc++) if (_cellNode[loc] >= 0) _cellOf[Rep(_cellNode[loc])] = loc;
            }
            List<int> way = new();
            for (int at = o; at != Global && at >= 0 && way.Count < 12; at = _escapeParent.GetValueOrDefault(at, -1)) way.Add(at);
            int first = way[^1];
            string put = _escapedFrom.TryGetValue(first, out int from)
                ? from < 0 ? (from == -1 ? "nothing seen" : "a node past " + MostHeld + ": " + DescribeNode(~from, _cellOf!)) : DescribeNode(from, _cellOf!)
                : "unknown";
            return "escapes " + (way.Count > 1 ? "through " + DescribeObject(first) + (way.Count > 2 ? " and " + (way.Count - 2) + " more" : "") + ", " : "")
                + "put where nobody follows by " + put;
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
            // And where it is held in all: by the function whose copies' nodes
            // hold it, or the site whose objects' cells do.
            Dictionary<string, long> by = new(StringComparer.Ordinal);
            int copy = 0;
            for (int n = 0; n < _pts.Count; n++)
            {
                if (_pts[n] is not { } set) continue;
                while (copy + 1 < _copyBase.Count && _copyBase[copy + 1] <= n) copy++;
                string where = cellOf.TryGetValue(n, out int loc) ? "cells of " + DescribeObject(_locationObject[loc])
                    : copy < _copyBase.Count && n - _copyBase[copy] < _functions[_copyFunction[copy]].Nodes ? _functions[_copyFunction[copy]].Name : "other nodes";
                by[where] = by.GetValueOrDefault(where) + set.Count;
            }
            foreach (var (where, count) in by.OrderByDescending(pair => pair.Value).Take(25))
                Log($"  {count} held in all by {where}");
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
                    return node - _copyBase[c] < f.Nodes ? f.Name + " context " + _copyContext[c] + " node " + (node - _copyBase[c])
                        + (node - _copyBase[c] == f.Parameters ? " (its return)" : node - _copyBase[c] < f.Parameters ? " (a parameter)" : "") : "a node";
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
