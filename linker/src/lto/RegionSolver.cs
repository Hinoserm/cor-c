using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// REGIONS OVER EVERY UNIT: RegionPointsTo for a separately compiled closed
/// image, answered from the units' RegionHints alone -- never their IR
/// (docs/REGIONS.md).
///
/// TWO ENGINES ANSWER WHAT OUTLIVES A CALL (--region-engine). The default,
/// escape, solves each function, and each cycle of calls together, from the
/// bottom of the calls up (RegionEscape), and the judge below asks its
/// answers. The other, andersen, is what follows: one inclusion solve over
/// the whole image.
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
/// each allocation, the nearest caller -- not the entry, not
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
        Func<string, long, long?, bool>? noReference = null, bool loops = false, Func<string, string, bool?>? isA = null,
        Func<string, IReadOnlyCollection<long>?>? slotsOf = null,
        (Func<string, string, bool?> MayBeThis, Func<string, bool?> MadeOutside)? receivers = null)
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
        if (!Switches.AndersenRegions)
        {
            Solver graphs = new(units, virtuals, methodAt, entry, foreign, report, live, 0, noReference) { LoopRegions = loops, Graphs = true, IsA = isA, SlotsOf = slotsOf, Receivers = receivers };
            if (graphs.Run() is { } found) return found;
            Console.Error.WriteLine("regions: nothing made a region");
            return null;
        }
        Solver coarse = new(units, virtuals, methodAt, entry, foreign, report, live, 0, noReference) { LoopRegions = loops, SlotsOf = slotsOf };
        RegionFacts?[]? coarseFacts = coarse.Run();
        if (coarseFacts is not null || !coarse.TooBig)
        {
            foreach (int depth in new[] { 2, 1 })
            {
                Solver solver = new(units, virtuals, methodAt, entry, foreign, report, live, depth, noReference) { LoopRegions = loops, SlotsOf = slotsOf };
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
        /// <summary>Escape found function by function (RegionEscape), one copy a function, rather than by solving the whole program at once.</summary>
        public bool Graphs { get; init; }
        private RegionEscape? _escape;
        // Per object of a site: the site's number among every function's (Graphs).
        private readonly List<int> _siteNumber = new();

        private readonly IReadOnlyList<RegionHints> _units;
        private readonly Dictionary<string, string[]> _virtuals;
        private readonly Func<string, long, string?> _methodAt;
        /// <summary>Whether objects of a descriptor are of a type (VirtualTargets.IsA); null for no such knowledge.</summary>
        public Func<string, string, bool?>? IsA { get; init; }
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
        // Every function any unit summarised, global or local, kept by the image or not.
        private readonly HashSet<string> _summarised = new(StringComparer.Ordinal);
        // Every unit's local functions the image keeps, by name (ResolveOverride).
        private readonly Dictionary<string, List<int>> _localsByName = new(StringComparer.Ordinal);

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
                    _summarised.Add(f.Name);
                    // What the image does not keep is never called: a call
                    // of it from what it keeps is a call of something unknown.
                    if (_live is not null && !_live(u, f.Name)) continue;
                    int function = _functions.Count;
                    _functions.Add(f);
                    _unitOf.Add(u);
                    _named.Add(new());
                    if (f.Global) (_globals.TryGetValue(f.Name, out List<int>? list) ? list : _globals[f.Name] = new()).Add(function);
                    else
                    {
                        locals[f.Name] = function;
                        (_localsByName.TryGetValue(f.Name, out List<int>? same) ? same : _localsByName[f.Name] = new()).Add(function);
                    }
                }
            }
            _copiesOf = new List<int>?[_functions.Count];
            _contexts = new int[_functions.Count];
        }

        private void Log(string text) => Console.Error.WriteLine("regions: " + text);

        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>The solve has outgrown its budget, inside a step: it stops, and nothing is made in a region.</summary>
        private sealed class OverBudget : Exception { }

        public RegionFacts?[]? Run() => Graphs ? Budgeted(GraphSteps) : OnDeepStack(() => Budgeted(Steps));

        // Met anywhere -- a step's watchers adding without end, a copy made
        // while binding or rooting -- the budget is giving up, never an
        // unhandled exception.
        private RegionFacts?[]? Budgeted(Func<RegionFacts?[]?> steps)
        {
            try { return steps(); }
            catch (OverBudget)
            {
                _over = true;
                return GiveUp("too much to hold");
            }
        }

        /// <summary>
        /// THE INCLUSION SOLVE (Steps) ON A STACK OF ITS OWN. A copy made
        /// (CopyOf) is built at once -- its constraints, then its calls -- and
        /// a call it makes of a function with no copy yet makes that one, and
        /// so on down: a receiver already bound (Watch, Received) the same.
        /// The depth is the depth of the call graph's first walk, thousands
        /// of frames on the compiler's own link, and the self-hosted
        /// compiler's threads have a few megabytes. A worklist would build
        /// the copies in another order, and the order is the answer here: a
        /// node saturates (MostHeld) with what came to it first. So the same
        /// recursion runs where it has room, and answers alike.
        /// </summary>
        private static RegionFacts?[]? OnDeepStack(Func<RegionFacts?[]?> solve)
        {
            RegionFacts?[]? result = null;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failed = null;
            Thread worker = new(() =>
            {
                try { result = solve(); }
                catch (Exception error) { failed = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
            }, DeepStack) { IsBackground = true, Name = "regions-inclusion" };
            worker.Start();
            worker.Join();
            failed?.Throw();
            return result;
        }

        // Room for tens of thousands of frames, and no more address space than a
        // 32-bit process can spare.
        private const int DeepStack = 64 * 1024 * 1024;

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
                foreach (string name in Addressed(u))
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

        /// <summary>
        /// THE SAME QUESTIONS ANSWERED FUNCTION BY FUNCTION (RegionEscape):
        /// every function one copy, called by every call that may run it,
        /// each site one object; what outlives a call is what the function's
        /// summary reaches. The judge then asks as it asks of a solve.
        /// </summary>
        private RegionFacts?[]? GraphSteps()
        {
            NewObject(-1, -1, -1, 0);                               // Global
            _siteNumber.Add(-1);
            int count = _functions.Count;
            for (int f = 0; f < count; f++)
            {
                int copy = _copyFunction.Count;
                _copyIds[Key(f, -1)] = copy;
                _copyFunction.Add(f); _copyContext.Add(-1); _copyBase.Add(0); _copyCollapsed.Add(false);
                (_copiesOf[f] ??= new()).Add(copy);
                _callees.Add(new()); _callers.Add(new());
            }
            int[] siteBase = new int[count];
            int sites = 0;
            for (int f = 0; f < count; f++)
            {
                siteBase[f] = sites;
                for (int s = 0; s < _functions[f].Sites.Length; s++)
                {
                    SiteObject(f, s, -1, f);
                    _siteNumber.Add(sites + s);
                }
                sites += _functions[f].Sites.Length;
            }
            int[]?[][] targets = new int[]?[count][];
            string?[][] keys = new string?[count][];
            for (int f = 0; f < count; f++)
            {
                RegionFunction function = _functions[f];
                int u = _unitOf[f];
                targets[f] = new int[]?[function.Calls.Count];
                keys[f] = new string?[function.Calls.Count];
                for (int k = 0; k < function.Calls.Count; k++)
                {
                    RegionCall call = function.Calls[k];
                    int[]? those = GraphTargets(f, call, out bool isVirtual);
                    targets[f][k] = those;
                    if (isVirtual && those is { Length: > 0 }) keys[f][k] = call.Callee + "@" + u;
                }
            }
            // CALLED FROM WHERE NOBODY FOLLOWS, with anything: the entry,
            // what code outside the IR names, every function whose address
            // is taken.
            bool[] rooted = new bool[count];
            bool anyStart = false;
            for (int f = 0; f < count; f++)
                if (_functions[f].Name == _entry || _foreign.Contains(_functions[f].Name)) { rooted[f] = true; anyStart = true; }
            if (!anyStart) return GiveUp("no entry " + _entry);
            for (int u = 0; u < _units.Count; u++)
                foreach (string name in Addressed(u))
                    if (Resolve(u, name) is { } those) foreach (int f in those) rooted[f] = true;
            // A ROOT CALLED ONLY BLIND, AS A METHOD A DESCRIPTOR HOLDS: its
            // `this` an object of a type that holds it, for RegionTypes. Not
            // the entry, nor what code outside the IR names, nor a function
            // whose address the program takes as a value: those may be
            // handed anything.
            bool[] typedThis = new bool[count];
            if (Receivers is { } receivers)
            {
                for (int f = 0; f < count; f++)
                    typedThis[f] = rooted[f] && _functions[f].Instance && _functions[f].Parameters > 0
                        && receivers.MadeOutside(_functions[f].Name) is not null;
                for (int f = 0; f < count; f++)
                    if (_functions[f].Name == _entry || _foreign.Contains(_functions[f].Name)) typedThis[f] = false;
                for (int u = 0; u < _units.Count; u++)
                    foreach (string name in _units[u].AddressTaken)
                        if (Resolve(u, name) is { } those) foreach (int f in those) typedThis[f] = false;
            }
            // ONLY WHAT EACH RECEIVER'S TYPES RUN (RegionTypes): a virtual
            // call's targets narrowed before the graphs and the judge's
            // callers are built from them (+typesoff: every override).
            // Every call's targets before, for what a site's own stamp runs (TargetsOnSite).
            int[]?[][] unpruned = targets.Select(calls => (int[]?[])calls.Clone()).ToArray();
            if (_report?.Contains("+typesoff") != true)
            {
                RegionTypes types = new(_functions, targets, keys, rooted, (f, k, table, at) => RunsOn(f, k, targets[f][k]!, table, at))
                {
                    TypedThis = typedThis,
                    Receives = Receivers is { } held ? (f, table, at) => held.MayBeThis(_functions[f].Name, table) != false : null,
                    ThisMadeOutside = Receivers is { } stamped ? f => stamped.MadeOutside(_functions[f].Name) != false : null,
                };
                bool pruned = types.Prune();
                Log(pruned
                    ? $"receiver types: {types.Objects} objects; {types.Narrowed} of {types.Calls} virtual calls narrowed, {types.Unknown} left every target, targets {types.Before} -> {types.After}, {_clock.ElapsedMilliseconds} ms"
                    : $"receiver types: gave up past its budget, every target kept, {_clock.ElapsedMilliseconds} ms");
            }
            for (int f = 0; f < count; f++)
                for (int k = 0; k < targets[f].Length; k++)
                    if (targets[f][k] is { } those) foreach (int g in those) { Beneath(f, g); _named[f].Add(g); }
            bool[] wanted = new bool[count];
            for (int f = 0; f < count; f++) wanted[f] = _functions[f].MayBeBoundary || _functions[f].Loops.Count > 0;
            int[] siteFunction = new int[sites];
            for (int f = 0; f < count; f++) for (int k = 0; k < _functions[f].Sites.Length; k++) siteFunction[siteBase[f] + k] = f;
            _escape = new RegionEscape(_functions, targets, keys, siteBase, sites, wanted, rooted)
            {
                Progress = _report is null ? null : Log, NoRoots = _report?.Contains("+noroots") == true,
                NoReference = _report?.Contains("+norefoff") == true ? null : (site, at) => SiteHoldsNoReference(siteFunction[site], site - siteBase[siteFunction[site]], at),
                TargetsOn = _report?.Contains("+classoff") == true ? null : TargetsOnSite(targets, unpruned, keys, siteFunction, siteBase),
            };
            if (_report?.FirstOrDefault(w => w.StartsWith("+why=", StringComparison.Ordinal)) is { } whyOf)
            {
                string fn = whyOf[5..];
                HashSet<int> explain = new();
                for (int f = 0; f < count; f++)
                    if (_functions[f].Name.Contains(fn, StringComparison.Ordinal))
                        for (int k = 0; k < _functions[f].Sites.Length; k++) explain.Add(siteBase[f] + k);
                _escape.Why = explain.Contains;
                _escape.WhyFunction = f => _functions[f].Name.Contains(fn, StringComparison.Ordinal);
            }
            // A diagnostic: wide calls past another count of targets than the
            // default sixteen (+wide=0: every call followed in order).
            if (_report?.FirstOrDefault(w => w.StartsWith("+wide=", StringComparison.Ordinal)) is { } wideOf && int.TryParse(wideOf[6..], out int wide))
                _escape.WideTargets = wide;
            // The link's pool of inclusion work past each component's own bound, and each one's most of it (millions).
            if (_report?.FirstOrDefault(w => w.StartsWith("+pool=", StringComparison.Ordinal)) is { } poolOf && long.TryParse(poolOf[6..], out long pool))
                _escape.InclusionPerRound = pool * 1_000_000;
            if (_report?.FirstOrDefault(w => w.StartsWith("+cap=", StringComparison.Ordinal)) is { } capOf && long.TryParse(capOf[5..], out long cap))
                _escape.InclusionCap = cap * 1_000_000;
            _escape.ReportStandIns = _report?.Contains("+standins") == true;
            // Memory, for a report: the heap at each slow solve and round, and
            // with +memtop=N the N solves it grew most over each round.
            _escape.MemReport = _report is not null;
            if (_report?.FirstOrDefault(w => w.StartsWith("+memtop=", StringComparison.Ordinal)) is { } memTop && int.TryParse(memTop[8..], out int top))
                _escape.MemTop = top;
            // A trade: stand-ins with every site their targets reach from the first round.
            _escape.WidenFirst = _report?.Contains("+widefirst") == true;
            if (_report is not null && _report.Contains("+cycles")) foreach (int most in new[] { 256, 64, 16, 4 }) _escape.ReportCycles(most);
            _escape.Run();
            Log($"escape graphs: {count} functions, {sites} sites, {_escape.Applied} summaries applied, largest cycle {_escape.LargestCycle}, "
                + $"{_escape.Unfollowed} not followed, {_escape.Fallbacks} unified past their bound, {_escape.PoolComponents} past it given {_escape.PoolDrawn} more from the pool ({_escape.InclusionPool} left of the last round's), {_escape.Work} carried, global {_escape.GlobalByUnknown} by the unknown object + {_escape.GlobalByRoots} by roots, {_clock.ElapsedMilliseconds} ms");
            return Judge();
        }

        /// <summary>
        /// WHAT A VIRTUAL CALL RUNS ON AN OBJECT OF A SITE, for the escape
        /// graphs (RegionEscape.TargetsOn): the method the site's descriptor
        /// holds at the call's slot, as Received finds it, among the call's
        /// targets; null for a site of no descriptor, or a method that is
        /// none of them -- any of them may run. A stamp is written once, by
        /// the site, and only read after, so an object of the site runs that
        /// method and no other. NONE (empty) for a descriptor not of the
        /// call's type: its targets are what every object of the type runs
        /// there, and nothing else is ever its receiver -- a list or a token
        /// a coarse enumerator handed a foreach over nodes is no node.
        /// AMONG THE CALL'S OWN TARGETS, narrowed or not (RegionTypes): what
        /// the stamp runs is found once a symbol, among every target the
        /// symbol has (<paramref name="unpruned"/>, alike for every call of
        /// it in a unit), and kept to those this call has; a method this
        /// call no longer has is any of them.
        /// </summary>
        private Func<int, int, int, int[]?> TargetsOnSite(int[]?[][] targets, int[]?[][] unpruned, string?[][] keys, int[] siteFunction, int[] siteBase)
        {
            Dictionary<(string, string, long), int[]?> known = new();
            return (f, k, site) =>
            {
                if (keys[f][k] is not { } key || targets[f][k] is not { } those || unpruned[f][k] is not { } every) return null;
                int g = siteFunction[site];
                if (_functions[g].Sites[site - siteBase[g]] is not { Table: { } table } s) return null;
                if (!known.TryGetValue((key, table, s.At), out int[]? runs)) known[(key, table, s.At)] = runs = RunsOn(f, k, every, table, s.At);
                if (runs is null || runs.Length == 0 || ReferenceEquals(those, every)) return runs;
                int[] kept = runs.Where(x => Array.BinarySearch(those, x) >= 0).ToArray();
                return kept.Length > 0 ? kept : null;
            };
        }

        /// <summary>
        /// What virtual call k of function f runs on an object stamped with
        /// <paramref name="table"/>, its method table <paramref name="at"/>
        /// bytes in, among <paramref name="those"/> (sorted): nothing for a
        /// descriptor not of the call's type, the method the descriptor holds
        /// at the slot where it is one of them, null where it cannot say.
        /// </summary>
        private int[]? RunsOn(int f, int k, int[] those, string table, long at)
        {
            string callee = _functions[f].Calls[k].Callee!;
            int plus = callee.LastIndexOf('+');
            if (plus > VirtualTargets.Prefix.Length && IsA?.Invoke(table, callee[VirtualTargets.Prefix.Length..plus]) == false) return Array.Empty<int>();
            if (SlotOf(callee) is long slot && _methodAt(table, at + slot) is { } method
                && _virtuals.TryGetValue(callee, out string[]? overrides) && overrides.Contains(method, StringComparer.Ordinal)
                && ResolveOverride(_unitOf[f], method) is { } resolved)
            {
                int[] among = resolved.Where(x => Array.BinarySearch(those, x) >= 0).Order().ToArray();
                if (among.Length > 0) return among;
            }
            return null;
        }

        /// <summary>
        /// WHAT A CALL NOBODY CAN NAME MAY RUN, of unit u's addresses taken:
        /// every one it names as a value -- and those only its descriptors'
        /// method slots name, when anything may call one of them blind. A
        /// virtual call the link follows reaches its overrides with its own
        /// arguments; anything else reaches a method only by reading it out of
        /// a descriptor and calling it as no virtual call (a unit says so,
        /// RegionHints.CallsThroughMethods), or by a virtual call the link
        /// cannot resolve. (The runtime reads a descriptor's own words, at
        /// its start, never a method to keep as a value.) Taken for one that
        /// may be, a virtual method was called with anything: what each
        /// override made and wrote into its object -- an iterator's item, a
        /// node's emitted instruction -- was everyone's, and no region took it.
        /// Rooted so, a function is rooted as a body too: any copy of a body
        /// rooted roots it (RegionEscape's bodies).
        ///
        /// ONLY THE METHODS HELD AT A SLOT SUCH A CALL READS: a function that
        /// calls a method it read out of a descriptor says where in the
        /// method table it read it (RegionFunction.BlindSlots), and a method
        /// that only descriptors name is reached so only if some descriptor
        /// holds it at one of those offsets (SlotsOf). Every method, where a
        /// virtual call is unresolved, a read's offset is not known, a unit
        /// says it calls one blind but no function of it does (hints older
        /// than the mark), or the link cannot say where methods are held.
        /// Rooted for a blind call that reads one slot, every MoveNext,
        /// Current, Dispose and closure Invoke was called with anything, and
        /// all each made and stored into its object was everyone's.
        /// </summary>
        private IEnumerable<string> Addressed(int u)
        {
            Blindness();
            if (_report is not null && !_saidBlind)
            {
                _saidBlind = true;
                if (_methodsBlind!.Value)
                    Log("methods are called blind: " + (_units.Any(unit => unit.CallsThroughMethods) ? "a unit calls a method it read from a descriptor" : "a virtual call is unresolved")
                        + (_blindAll ? ", at any slot" + (_blindBy is null ? "" : " (" + _blindBy + ")") : ", at slots " + string.Join(",", _blindSlots!.Order())));
                ReportBlind();
            }
            return _methodsBlind!.Value ? _units[u].AddressTaken.Concat(_units[u].MethodsTaken.Where(CalledBlind)) : _units[u].AddressTaken;
        }

        /// <summary>What a method a descriptor holds may run on, and whether an object of its type is made outside the IR (VirtualTargets.Receivers); null: a root's `this` may be anything.</summary>
        public (Func<string, string, bool?> MayBeThis, Func<string, bool?> MadeOutside)? Receivers { get; init; }

        /// <summary>Where each method is held in a method table (VirtualTargets.SlotsOf); null: every method may be called blind.</summary>
        public Func<string, IReadOnlyCollection<long>?>? SlotsOf { get; init; }

        private bool? _methodsBlind;
        private bool _saidBlind;
        // The slots methods are read at to be called blind; every one (_blindAll).
        private HashSet<long>? _blindSlots;
        private bool _blindAll;
        // The function whose blind call reads no slot the link can say, for the report.
        private string? _blindBy;

        private void Blindness()
        {
            if (_methodsBlind is not null) return;
            bool through = _units.Any(unit => unit.CallsThroughMethods);
            bool unresolved = AnyUnresolvedVirtual();
            _methodsBlind = through || unresolved;
            _blindAll = unresolved || SlotsOf is null;
            if (_blindAll || !through) return;
            _blindSlots = new();
            foreach (RegionHints unit in _units)
            {
                if (!unit.CallsThroughMethods) continue;
                bool said = false;
                foreach (RegionFunction function in unit.Functions)
                {
                    if (!function.CallsThroughMethod) continue;
                    said = true;
                    if (function.BlindSlots.Length == 0 || function.BlindSlots.Contains(RegionConstraint.Any)) { _blindAll = true; _blindBy = function.Name; return; }
                    _blindSlots.UnionWith(function.BlindSlots);
                }
                if (!said) { _blindAll = true; _blindBy = "a unit whose functions say none"; return; }
            }
        }

        // Whether a method only descriptors name may be called blind.
        private bool CalledBlind(string method)
            => _blindAll || SlotsOf!(method) is not { } slots || slots.Any(_blindSlots!.Contains);

        // Whether some virtual call's targets the link cannot say (GraphTargets' rule).
        private bool AnyUnresolvedVirtual() => Unresolved(stopAtFirst: true).Count > 0;

        /// <summary>
        /// THE VIRTUAL CALLS THE LINK CANNOT SAY THE TARGETS OF, by name: one
        /// no descriptor answers for as code (VirtualTargets.Targets: a slot
        /// holding what is not a function here), or an override the image
        /// keeps no summary of. Each with why, how many calls, and the first
        /// function making one. Any one of them roots every method a
        /// descriptor names (Addressed).
        /// </summary>
        private SortedDictionary<string, (string Why, int Calls, string First)> Unresolved(bool stopAtFirst)
        {
            SortedDictionary<string, (string Why, int Calls, string First)> found = new(StringComparer.Ordinal);
            for (int f = 0; f < _functions.Count; f++)
            {
                int u = _unitOf[f];
                foreach (RegionCall call in _functions[f].Calls)
                {
                    if (call.Callee is not { } name || !name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)) continue;
                    string? why = null;
                    if (!_virtuals.TryGetValue(name, out string[]? targets)) why = "a slot holds what is not code here";
                    else
                        foreach (string target in targets)
                            if (ResolveOverride(u, target) is null && !_summarised.Contains(target)) { why = "no summary of " + target; break; }
                    if (why is null) continue;
                    found[name] = found.TryGetValue(name, out var had) ? (had.Why, had.Calls + 1, had.First) : (why, 1, _functions[f].Name);
                    if (stopAtFirst) return found;
                }
            }
            return found;
        }

        // For a report: every virtual call left unresolved, and every function
        // that calls a method it read from a descriptor (RegionFunction.CallsThroughMethod).
        private void ReportBlind()
        {
            var unresolved = Unresolved(stopAtFirst: false);
            Log($"unresolved virtual calls: {unresolved.Count}");
            foreach (var (name, (why, calls, first)) in unresolved)
                Log($"unresolved virtual call {name}: {why}; {calls} call(s), first in {first}");
            List<string> blind = new();
            for (int f = 0; f < _functions.Count; f++) if (_functions[f].CallsThroughMethod) blind.Add(_functions[f].Name);
            Log($"functions calling a method read from a descriptor: {blind.Count}"
                + (_units.Any(unit => unit.CallsThroughMethods) && blind.Count == 0 ? " (named by no function: hints before the per-function mark)" : ""));
            foreach (string name in blind.Order(StringComparer.Ordinal)) Log("calls a method read from a descriptor: " + name);
        }

        /// <summary>The functions a call may run, as Call finds them; null when one is nothing summarised.</summary>
        private int[]? GraphTargets(int f, RegionCall call, out bool isVirtual)
        {
            isVirtual = false;
            int u = _unitOf[f];
            if (call.Callee is not { } name) { ReportUnknown(f, call, null); return null; }
            if (name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal))
            {
                isVirtual = true;
                bool known = _virtuals.TryGetValue(name, out string[]? found);
                HashSet<int> reached = new();
                string? missing = null;
                foreach (string target in found ?? Array.Empty<string>())
                    if (ResolveOverride(u, target) is { } those) reached.UnionWith(those);
                    else if (!_summarised.Contains(target)) { known = false; missing ??= target; }
                if (!known) { ReportUnknown(f, call, found is null ? "no targets" : "no summary of " + missing); return null; }
                return reached.Order().ToArray();
            }
            if (Resolve(u, name) is not { } direct) { ReportUnknown(f, call, "not summarised"); return null; }
            return direct.ToArray();
        }

        // Escape graphs answer each question with a search of a sorted array:
        // the judge may ask ten times as many.
        private long Budget => _escape is null ? JudgeBudget : 10 * JudgeBudget;

        private RegionFacts?[]? GiveUp(string why)
        {
            TooBig = _over || _walked > Budget;
            Log($"gave up ({why}) with contexts {_maxDepth} deep at {_parent.Count} nodes, {_copyFunction.Count} copies, {_locationObject.Count} locations, {_held} held");
            if (_report is not null && _escape is null) Largest();
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
                    // A constant too: an instance call is bound by what its receiver holds.
                    case RegionConstraintKind.Unknown or RegionConstraintKind.Symbol: Add(a, GlobalLocation); break;
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

        // The same of a site, by function and ordinal (Any: some word) -- escape graphs' question.
        private bool SiteHoldsNoReference(int f, int ordinal, int offset)
        {
            RegionSite site = _functions[f].Sites[ordinal];
            if (site.Words == RegionWords.Leaf) return true;
            if (site.Words != RegionWords.Described || site.Table is not { } table || _noReferenceAt is null) return false;
            long? at = offset < 0 ? null : offset;
            if (_noReferenceWords.TryGetValue((table, site.At, at), out bool known)) return known;
            return _noReferenceWords[(table, site.At, at)] = _noReferenceAt(table, site.At, at);
        }

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

        /// <summary>
        /// THE FUNCTIONS AN OVERRIDE NAMES, wherever they are: a descriptor
        /// is any unit's, and the method it holds may be local to that unit
        /// -- an iterator's MoveNext, its type named by a hash of its body, so
        /// every unit that makes the same one has its own copy. Each such
        /// copy is reached, which is everything one could be. Resolved only
        /// in the calling unit, every interface call that might reach an
        /// iterator made in another unit -- a foreach over an IEnumerable,
        /// most of LINQ -- was a call of nothing summarised: the unknown
        /// object was handed what it was given, and the compiler's own link
        /// held too much to solve.
        /// </summary>
        private List<int>? ResolveOverride(int u, string name)
            => Resolve(u, name) ?? _localsByName.GetValueOrDefault(name);

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
                string? missing = null;
                foreach (string target in found ?? Array.Empty<string>())
                    if (ResolveOverride(u, target) is not null) overrides.Add(target);
                    else if (!_summarised.Contains(target)) { known = false; missing ??= target; }
                if (!known) { Unknown(copy, call, found is null ? "no targets" : "no summary of " + missing); return; }
                if (overrides.Count == 0) return;            // no object of the type exists
                int self = call.Arguments.Length > 0 ? call.Arguments[0] : -1;
                Binding binding = new() { Copy = copy, Call = call, Overrides = overrides.ToArray() };
                if (self < 0) { AllOverrides(binding); return; }
                Watch(binding, Node(copy, self));
                return;
            }
            if (Resolve(u, name) is not { } targets) { Unknown(copy, call, "not summarised"); return; }
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
                foreach (int g in ResolveOverride(_unitOf[caller], method)!)
                {
                    _named[caller].Add(g);
                    Handed(binding, CopyOf(g, o, binding.Copy), loc);
                }
                return;
            }
            foreach (string target in binding.Overrides!)
                foreach (int g in ResolveOverride(_unitOf[caller], target)!)
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
                foreach (int g in ResolveOverride(u, target)!)
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
        // (Received) -- and its return the call's result. A parameter of a
        // number type (RegionFunction.NumberParams) is handed no address.
        private void To(int caller, RegionCall call, int callee, bool receiver = true)
        {
            Beneath(caller, callee);
            RegionFunction g = _functions[_copyFunction[callee]];
            for (int k = receiver ? 0 : 1; k < call.Arguments.Length && k < g.Parameters; k++)
                if (call.Arguments[k] >= 0 && !g.IsNumber(k)) Edge(Node(caller, call.Arguments[k]), Node(callee, k), 0);
            if (call.Dest >= 0) Edge(Node(callee, g.Parameters), Node(caller, call.Dest), 0);
        }

        private void Unknown(int copy, RegionCall call, string? why = null)
        {
            ReportUnknown(_copyFunction[copy], call, why);
            foreach (int a in call.Arguments) if (a >= 0) Leak(Node(copy, a));
            if (call.Dest >= 0) Add(Node(copy, call.Dest), GlobalLocation);
        }

        private readonly HashSet<string> _reportedUnknown = new(StringComparer.Ordinal);

        private void ReportUnknown(int f, RegionCall call, string? why)
        {
            if (_report is not null && _reportedUnknown.Add(call.Callee ?? "an address"))
                Log("unknown call in " + _functions[f].Name + " of " + (call.Callee ?? "an address") + (why is null ? "" : " (" + why + ")"));
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

        private bool Outlives(int o, int c) => Outlives(o, c, EscapingBits(c));

        // THE JUDGE ASKS HUNDREDS OF MILLIONS OF TIMES whether an object
        // outlives a copy, most of them of one copy for object after object:
        // the copy's answers fetched once (EscapingBits), each question is a
        // load or two -- an object reached from the unknown object (_isGlobal),
        // else, from escape graphs, the site's bit among those the function's
        // summary reaches (null: every one), else Outliving's set.
        private bool[] _isGlobal = Array.Empty<bool>();
        // Per object: its site's number among every function's, -1 for one that is no site.
        private int[] _siteOf = Array.Empty<int>();

        private ulong[]? EscapingBits(int c) => _escape?.EscapingBits(_copyFunction[c]);

        private bool Outlives(int o, int c, ulong[]? bits)
        {
            if (_isGlobal[o]) return true;
            if (_escape is null) return Outliving(c).Contains(o);
            int site = _siteOf[o];
            return site < 0 || bits is null || (bits[site >> 6] >> (site & 63) & 1) != 0;
        }

        private bool IsSite(int o) => o > Global && _objectSite[o] >= 0;

        // Sites one of whose objects code nobody follows may make and keep.
        private readonly HashSet<(int, int)> _unseenKept = new();

        private RegionFacts?[]? Judge()
        {
            // +reachouter offers outer boundaries, and is judged as +judgebytes
            // judges: an outer boundary credited by the sites' bytes, and
            // charged only for what a boundary above would take.
            _reachOuter = _report?.Contains("+reachouter") == true;
            // BY BYTES, an inner boundary credited with what it takes beneath an
            // outer one too: counted by sites, and not at all beneath another,
            // the inner one lost to the outer, whose region holds the same
            // objects longer -- Work's rows went to Run's region and were
            // given back only when every round was done (1200, 1290, 1298,
            // 1316, 1319, 1323). +judgecount weighs the old way, for an A/B.
            _judgeBytes = _reachOuter || _report?.Contains("+judgecount") != true;
            _globalReach = new();
            if (_escape is not null)
            {
                _globalReach.Add(Global);
                for (int o = 1; o < _objectFunction.Count; o++)
                    if (IsSite(o) && _escape.Global[_siteNumber[o]]) _globalReach.Add(o);
            }
            else Reach(new[] { Global }, _globalReach, null);
            _isGlobal = new bool[_objectFunction.Count];
            foreach (int o in _globalReach) _isGlobal[o] = true;
            _siteOf = new int[_objectFunction.Count];
            for (int o = 0; o < _siteOf.Length; o++) _siteOf[o] = _escape is not null && IsSite(o) ? _siteNumber[o] : -1;
            // The calls between copies as arrays, each in its set's own order:
            // walked again and again, the order things are found in kept.
            _calleesOf = new int[_callees.Count][];
            _callersOf = new int[_callers.Count][];
            for (int c = 0; c < _callees.Count; c++) { _calleesOf[c] = _callees[c].ToArray(); _callersOf[c] = _callers[c].ToArray(); }
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
            bool[] beforeBlock = BeforeThreadBlock();
            // Nor what the entry calls itself -- Main, the runtime's start --
            // as RegionPointsTo.EntryCalls: Main never (its summary says it
            // may not be one), the rest only while the entry calls Main, not
            // when it took Main into itself and calls what Main calls.
            HashSet<int> started = new();
            for (int f = 0; f < _functions.Count; f++)
                if (_functions[f].Name == _entry && _named[f].Any(g => _functions[g].Main)) started.UnionWith(_named[f]);
            // A FUNCTION ON A CYCLE MAY BE ONE: each activation opens its own
            // region, and what outlives a call of it is what outlives any one
            // (the escape answers do not tell activations apart), so the
            // innermost region where an object is made -- the first boundary
            // on the way up, which NearestAbove finds stopping at boundaries
            // -- is proved as any. Refused, the compiler's own code, nearly
            // all of it on one cycle of virtual calls, had no boundary at all.
            bool MayBeBoundary(int f) => _functions[f].MayBeBoundary && !beforeBlock[f] && !started.Contains(f) && _functions[f].Name != _entry;

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
            _madeAt = new List<int>?[_copyFunction.Count];
            foreach ((int maker, List<int> objects) in madeBy) _madeAt[maker] = objects;

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
                if (_walked > Budget) return GiveUp("too much to judge");
            }
            if (_report is not null) Log($"judge: nearest walk {_walked} walked, {chosen.Count} chosen, {_clock.ElapsedMilliseconds} ms");
            // What the nearest walk chose, kept: +reachouter's more candidates
            // cost Evaluate more, and past its budget the judge goes back to
            // these rather than give every region up.
            SortedSet<int>? nearestOnly = _reachOuter ? new(chosen) : null;
            if (_reachOuter) OfferOuter(chosen, madeBy, MayBeBoundary);
            long walkedBefore = _walked;
            if (_report is not null && _escape is null) foreach (int f in chosen) Log("boundary chosen " + _functions[f].Name);
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
                Verdict? evaluated = Evaluate(chosen, bySite, madeBy);
                if (evaluated is null && nearestOnly is not null && chosen.Count > nearestOnly.Count)
                {
                    Log($"judge: reachouter's candidates past the judge's budget; the nearest walk's {nearestOnly.Count} judged instead");
                    chosen = nearestOnly;
                    nearestOnly = null;
                    _walked = walkedBefore;
                    round = -1;
                    continue;
                }
                if (evaluated is not { } verdict) return GiveUp("too much to judge");
                if (_report is not null) Log($"judge: round {round}, {_walked} walked, {chosen.Count} chosen, {verdict.Taken.Count} taken, {_clock.ElapsedMilliseconds} ms");
                taken = verdict.Taken;
                final = verdict;
                if (round == 3) break;
                List<int> dropped = _judgeBytes
                    ? chosen.Where(f => verdict.LossBytes.GetValueOrDefault(f) > verdict.GainBytes.GetValueOrDefault(f)).ToList()
                    : chosen.Where(f => verdict.Loss.GetValueOrDefault(f) > verdict.Gain.GetValueOrDefault(f)).ToList();
                if (dropped.Count == 0) break;
                foreach (int f in dropped)
                {
                    chosen.Remove(f);
                    if (_report is not null)
                        Log("boundary dropped " + _functions[f].Name + ": keeps " + verdict.Loss.GetValueOrDefault(f) + " sites out, takes " + verdict.Gain.GetValueOrDefault(f)
                            + (_judgeBytes ? $" ({verdict.LossBytes.GetValueOrDefault(f)} bytes out, {verdict.GainBytes.GetValueOrDefault(f)} taken)" : ""));
                }
            }

            // THE LOOPS GIVEN A REGION OF THEIR OWN (RegionPointsTo's "loops"),
            // over the boundaries left, and the sites taken with them.
            List<LoopRegion> loops = LoopRegions ? SelectLoops(final, beforeBlock) : new();
            if (_report is not null) Log($"judge: loops selected {loops.Count}, {_loopWalked} walked, {_clock.ElapsedMilliseconds} ms");
            if (loops.Count > 0) taken = TakenWithLoops(loops, final, bySite, madeBy);
            if (_report is not null) Log($"judge: taken with loops, {_walked} walked, {_clock.ElapsedMilliseconds} ms");
            taken = WithoutPiledUp(taken, loops);
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
            Sizes(opened, loops, taken, For);
            if (_report is not null) Report(chosen, opened, taken, madeBy);
            // --region-report +sites: every site's verdict, for weighing against where the bytes go.
            if (_report is not null && _report.Contains("+sites"))
                foreach (((int f, int site), List<int> objects) in bySite)
                {
                    string verdict = taken.Contains((f, site)) ? "taken"
                        : _unseenKept.Contains((f, site)) ? "unseen"
                        : objects.Any(_globalReach.Contains) ? "global"
                        : objects.FirstOrDefault(o => _refusedBy[o] >= 0) is int r && _refusedBy[r] >= 0 ? "refused-by " + _functions[_copyFunction[_refusedBy[r]]].Name
                        : objects.Any(o => final.Above[o] != -1) ? "loop-refused" : "no-boundary";
                    Console.Error.WriteLine("regions-site " + _functions[f].Name + " " + site + " " + verdict + " line " + _functions[f].Sites[site].Line + " " + (_functions[f].Sites[site].Table ?? "-"));
                }
            Log($"{opened.Count} boundaries, {loops.Count} loops, {taken.Count} sites in the innermost region, of {bySite.Count}; judged by {_clock.ElapsedMilliseconds} ms, {_walked} walked");
            return facts;
        }

        // The drop rule by bytes (SiteWeight, TakenAbove); --region-report +judgecount: by sites, as before.
        private bool _judgeBytes;
        // --region-report +reachouter: candidacy's A/B (OfferOuter).
        private bool _reachOuter;

        // How far up OfferOuter looks: a compiler's pass is some six to
        // twelve calls above what its helpers allocate (driver, the pass's
        // Run, a visitor, a rule, a builder, the collection), and the
        // nearest walk's eight stopped short of it. Sixteen covers that with
        // room, and every level is walked once a making copy, under its own
        // budget.
        private const int OuterReach = 16;
        // What a function must take to be offered: 1 KB a call, estimated
        // (SiteWeight). A region's opening and closing are two calls and a
        // 32-byte record; a kilobyte is some thirty small objects the
        // collector no longer sweeps one by one, well past what the calls
        // cost, while an outer function holding only a stray object or two
        // is left alone. The judge weighs every one offered again
        // (+judgebytes), so the threshold only bounds how many it weighs.
        private const long OuterShare = 1024;
        // The walking OfferOuter may do: one judge's budget, counted apart
        // from the judge's own (whose Budget gives every region up). Past
        // it, what is offered so far stands and the judge goes on.
        private const long OuterBudget = JudgeBudget;

        /// <summary>
        /// OUTER BOUNDARIES OFFERED (+reachouter): from each copy that makes
        /// objects, every function up to OuterReach calls above it that may be
        /// a boundary and whose return an object does not outlive is credited
        /// with the object's bytes (SiteWeight); one credited with OuterShare
        /// or more is a candidate too, beside the nearest the walk found.
        /// Evaluate decides between them, in bytes. The nearest walk settles an
        /// object at the first function that does not outlive it, so an outer
        /// function -- a pass's Run, over leaves that kill their own
        /// temporaries -- was a candidate only for objects nothing below it
        /// settled.
        /// </summary>
        private void OfferOuter(SortedSet<int> chosen, Dictionary<int, List<int>> madeBy, Func<int, bool> mayBeBoundary)
        {
            long walked = 0;
            Dictionary<int, long> credit = new();
            bool stopped = false;
            foreach ((int made, List<int> objects) in madeBy.OrderBy(pair => pair.Key))
            {
                List<int> open = objects.Where(o => !_globalReach.Contains(o)).ToList();
                if (open.Count == 0) continue;
                long[] weight = open.Select(o => SiteWeight(_objectFunction[o], _objectSite[o])).ToArray();
                Dictionary<int, int> depth = new() { [made] = 0 };
                Queue<int> next = new();
                next.Enqueue(made);
                HashSet<int> creditedHere = new();
                while (next.TryDequeue(out int c))
                {
                    walked++;
                    int f = _copyFunction[c];
                    if (mayBeBoundary(f) && creditedHere.Add(f))
                    {
                        long sum = 0;
                        for (int k = 0; k < open.Count; k++)
                        {
                            walked++;
                            if (!Outlives(open[k], c)) sum += weight[k];
                        }
                        if (sum > 0) credit[f] = Math.Min(long.MaxValue / 2, credit.GetValueOrDefault(f) + sum);
                    }
                    if (depth[c] >= OuterReach) continue;
                    foreach (int caller in _callers[c].Order())
                        if (depth.TryAdd(caller, depth[c] + 1)) next.Enqueue(caller);
                }
                if (walked > OuterBudget) { stopped = true; break; }
            }
            int offered = 0;
            foreach ((int f, long bytes) in credit)
                if (bytes >= OuterShare && chosen.Add(f)) offered++;
            if (_report is not null)
                Log($"judge: reachouter offered {offered} more, {credit.Count} credited, {walked} walked{(stopped ? ", past its budget" : "")}, {_clock.ElapsedMilliseconds} ms");
        }

        // What a site costs a call, estimated: its block's bytes (the sizes
        // hints; 32 where not a constant) times how often it runs in its
        // function, the product of its loops' trip counts (16 a loop of no
        // known count), capped. A pass's Run over thousands of instructions
        // weighs what it makes in its loops, where a leaf's one array weighs
        // one array.
        private long SiteWeight(int f, int site)
        {
            const long Cap = 1L << 30;
            RegionFunction function = _functions[f];
            long bytes = function.BytesOf(site) is long b and > 0 ? b : 32;
            long times = 1;
            int loop = function.LoopOfSite(site);
            if (loop == RegionFunction.Throwing) return bytes;
            if (loop == RegionFunction.Unbounded) times = 16;
            for (int l = loop, steps = 0; l >= 0 && l < function.Repeats.Length && steps < 16; l = function.Repeats[l].Parent, steps++)
            {
                long trip = function.Repeats[l].Trip;
                times = Math.Min(Cap, times * (trip > 0 ? Math.Min(trip, 4096) : 16));
            }
            return Math.Min(Cap, bytes * times);
        }

        // Whether a boundary above the copy that refused each object --
        // the first on each way up past it -- would take the object: what
        // Loss charges a boundary for is what it keeps out of a region that
        // would hold it. The rule before charged every refusal beneath any
        // other boundary, though that one refused the same objects: a pass's
        // Run beneath its driver was charged for every IR object it kept,
        // which outlives the driver too.
        private bool TakenAbove(List<int> objects)
        {
            foreach (int o in objects)
            {
                int b = _refusedBy[o];
                if (b < 0) continue;
                bool takes = false;
                foreach (int p in _callersOf[b])
                {
                    foreach (int g in _isBoundary[p] ? Alone(p) : _nearest[_component[p]])
                        if (_copyFunction[g] != _copyFunction[b] && !Outlives(o, g, EscapingBits(g))) { takes = true; break; }
                    if (takes) break;
                }
                if (!takes) return false;
            }
            return true;
        }

        private sealed class Verdict
        {
            public readonly HashSet<(int, int)> Taken = new();
            /// <summary>Per object: whether its site is taken.</summary>
            public bool[] TakenObject = null!;
            /// <summary>Per object: the boundary above it, and the one refusing it (-1: none, -2: more than one).</summary>
            public int[] Above = null!, Refuser = null!;
            /// <summary>Per boundary: the sites it alone refuses that another boundary is above.</summary>
            public readonly Dictionary<int, int> Loss = new();
            /// <summary>Per boundary: the sites taken that only it is above.</summary>
            public readonly Dictionary<int, int> Gain = new();
            /// <summary>+judgebytes: Loss and Gain as estimated bytes a call (SiteWeight), Loss only where a boundary above would take the site.</summary>
            public readonly Dictionary<int, long> LossBytes = new(), GainBytes = new();
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
            // for none and -2 for more than one; and whether another boundary
            // is above that one.
            int objectCount = _objectFunction.Count;
            int[] above = new int[objectCount], refuser = new int[objectCount];
            Array.Fill(above, -1);
            Array.Fill(refuser, -1);
            bool[] outer = new bool[objectCount];
            static void Note(int[] into, int o, int f)
            {
                int was = into[o];
                if (was == -1) into[o] = f;
                else if (was != f) into[o] = -2;
            }
            if (_refusedBy.Length != objectCount) _refusedBy = new int[objectCount];
            Array.Fill(_refusedBy, -1);
            _refusedOrder.Clear();
            // Per boundary copy, asked once a round: 1 when another
            // function's boundary is above it, 0 when none is, -1 unknown.
            sbyte[] hasOuter = new sbyte[_copyFunction.Count];
            Array.Fill(hasOuter, (sbyte)-1);
            foreach ((int c, List<int> made) in madeBy)
            {
                // What every boundary above the copy notes of each object it
                // makes, noted once for all of them: the boundary above it
                // (one function's, or -2 for more than one), and whether
                // another boundary is above that one -- open around it too,
                // for what it leaves, as every boundary above an object was
                // noted when every one was judged.
                int[] boundaries = Above(c);
                int noted = -1;
                bool beneathOuter = false;
                foreach (int b in boundaries)
                {
                    int f = _copyFunction[b];
                    noted = noted == -1 || noted == f ? f : -2;
                    if (hasOuter[b] < 0) hasOuter[b] = (sbyte)(BeneathAnother(b) ? 1 : 0);
                    beneathOuter |= hasOuter[b] > 0;
                }
                if (noted != -1)
                    foreach (int o in made)
                    {
                        int was = above[o];
                        above[o] = was == -1 ? noted : was == noted ? was : -2;
                        if (beneathOuter) outer[o] = true;
                    }
                foreach (int b in boundaries)
                {
                    int f = _copyFunction[b];
                    ulong[]? bits = EscapingBits(b);
                    _walked += made.Count;
                    foreach (int o in made)
                        if (Outlives(o, b, bits))
                        {
                            Note(refuser, o, f);
                            if (_refusedBy[o] == -1) { _refusedBy[o] = b; _refusedOrder.Add(o); }
                        }
                }
                if (_walked > Budget) return null;
            }
            // WHAT WOULD ONLY PILE UP, with the boundaries this round has: no
            // Gain for the boundary whose region it would pile in, and no
            // Loss either -- refused, it goes to the heap, where it goes anyway.
            HashSet<(int, int)> reachable = new();
            foreach (((int, int) key, List<int> objects) in bySite)
                if (objects.Any(o => above[o] != -1)) reachable.Add(key);
            HashSet<(int, int)> piled = PiledSites(reachable, new List<LoopRegion>(), out _, freshNear: true);
            // A site is taken when none of its objects is refused and one is
            // beneath some boundary.
            Verdict verdict = new() { Above = above, Refuser = refuser, TakenObject = new bool[objectCount] };
            foreach (((int, int) key, List<int> objects) in bySite)
            {
                bool anywhere = false, refused = false;
                foreach (int o in objects) { anywhere |= above[o] != -1; refused |= refuser[o] != -1; }
                if (!anywhere || _unseenKept.Contains(key)) continue;
                bool piles = piled.Contains(key);
                if (!refused)
                {
                    verdict.Taken.Add(key);
                    foreach (int o in objects) verdict.TakenObject[o] = true;
                    // Taken only for the one boundary above all of it.
                    int only = -1;
                    foreach (int o in objects)
                        if (above[o] is int f and not -1) only = (only == -1 || only == f) && (_judgeBytes || !outer[o]) ? f : -2;
                    if (only >= 0 && !piles)
                    {
                        verdict.Gain[only] = verdict.Gain.GetValueOrDefault(only) + 1;
                        verdict.GainBytes[only] = verdict.GainBytes.GetValueOrDefault(only) + SiteWeight(key.Item1, key.Item2);
                    }
                    continue;
                }
                // Refused by one boundary alone, with another above it too.
                int sole = -1;
                bool other = false;
                foreach (int o in objects)
                {
                    if (refuser[o] is int r and not -1) sole = sole == -1 || sole == r ? r : -2;
                    if (outer[o] || above[o] is int a and not -1 && (a == -2 || refuser[o] != a)) other = true;
                }
                if (piles) continue;
                if (sole >= 0 && other) verdict.Loss[sole] = verdict.Loss.GetValueOrDefault(sole) + 1;
                if (_judgeBytes && sole >= 0 && other && TakenAbove(objects))
                    verdict.LossBytes[sole] = verdict.LossBytes.GetValueOrDefault(sole) + SiteWeight(key.Item1, key.Item2);
            }
            return verdict;
        }

        // Per copy: whether it is a chosen boundary's; per component of the
        // calls between copies, the boundary copies innermost above it.
        private bool[] _isBoundary = Array.Empty<bool>();
        private int[] _component = Array.Empty<int>();
        private int[][] _nearest = Array.Empty<int[]>();
        // Per copy: its callees and callers, in their sets' order; a copy alone, asked of again and again.
        private int[][] _calleesOf = Array.Empty<int[]>(), _callersOf = Array.Empty<int[]>();
        private int[]?[] _alone = Array.Empty<int[]?>();
        // Per copy: the objects it makes that a region may take (Judge's madeBy).
        private List<int>?[] _madeAt = Array.Empty<List<int>?>();

        private int[] Alone(int c)
        {
            if (_alone.Length != _copyFunction.Count) _alone = new int[]?[_copyFunction.Count];
            return _alone[c] ??= new[] { c };
        }

        /// <summary>The boundary copies a region can be opened in innermost when copy `c` runs: itself, if it is one, else the first on each way up.</summary>
        private int[] Above(int c) => _isBoundary[c] ? Alone(c) : _nearest[_component[c]];

        // Per component: the one function whose boundary copies are innermost
        // above it, -1 for none, -2 for more than one.
        private int[] _nearestFunction = Array.Empty<int>();

        /// <summary>Whether another function's boundary is innermost above a call of boundary copy `b`: on one of its callers, that caller if it is one, else the first above it.</summary>
        private bool BeneathAnother(int b)
        {
            int f = _copyFunction[b];
            foreach (int p in _callersOf[b])
            {
                int g = _isBoundary[p] ? _copyFunction[p] : _nearestFunction[_component[p]];
                if (g != -1 && g != f) return true;
            }
            return false;
        }

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
                calls.Push((start, _calleesOf[start], 0));
                while (calls.Count > 0)
                {
                    (int v, int[] callees, int at) = calls.Pop();
                    bool descended = false;
                    while (at < callees.Length)
                    {
                        int w = callees[at++];
                        // Up stops at a boundary: each is a component of its own.
                        if (_isBoundary[w]) continue;
                        if (index[w] < 0)
                        {
                            calls.Push((v, callees, at));
                            index[w] = low[w] = next++;
                            stack.Push(w); onStack[w] = true;
                            calls.Push((w, _calleesOf[w], 0));
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
            _nearestFunction = new int[components.Count];
            // The union of several callers': a copy in it when its mark is the component's.
            int[] mark = new int[count];
            List<int> union = new();
            for (int k = components.Count - 1; k >= 0; k--)
            {
                // A boundary's own component: what is above it is
                // BeneathAnother's to say, over callers whose components may
                // come after it.
                if (components[k].Count == 1 && _isBoundary[components[k][0]]) { _nearest[k] = Array.Empty<int>(); _nearestFunction[k] = -1; continue; }
                union.Clear();
                int[]? only = null;
                bool many = false;
                foreach (int m in components[k])
                    foreach (int p in _callersOf[m])
                    {
                        if (_component[p] == k) continue;
                        _walked++;
                        int[] from = _isBoundary[p] ? Alone(p) : _nearest[_component[p]];
                        if (from.Length == 0) continue;
                        if (only is null && !many) { only = from; continue; }
                        if (ReferenceEquals(only, from)) continue;
                        if (!many)
                        {
                            foreach (int x in only!) if (mark[x] != k + 1) { mark[x] = k + 1; union.Add(x); }
                            many = true;
                        }
                        foreach (int x in from) if (mark[x] != k + 1) { mark[x] = k + 1; union.Add(x); }
                    }
                int[] nearest = _nearest[k] = many ? union.ToArray() : only ?? Array.Empty<int>();
                if (many) Array.Sort(nearest);
                int function = -1;
                foreach (int x in nearest)
                    if (function == -1 || function == _copyFunction[x]) function = _copyFunction[x];
                    else { function = -2; break; }
                _nearestFunction[k] = function;
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
        ///
        /// AND WHAT RUNS AS IT ENDS: the function that hands the thread's arena
        /// back (RuntimeAbi.ReleaseRegion) and every caller of it -- a
        /// thread's body, which calls Runtime.EndThread before it returns.
        /// Its region's leave would come after the arena it was opened in was
        /// gone: Thread.Body made a boundary faulted in RegionLeave in every
        /// thread test.
        /// </summary>
        private bool[] BeforeThreadBlock()
        {
            bool[] before = new bool[_functions.Count];
            List<int>[] callers = new List<int>[_functions.Count];
            for (int f = 0; f < _functions.Count; f++)
                foreach (int g in _named[f]) (callers[g] ??= new()).Add(f);
            Stack<int> next = new();
            for (int f = 0; f < _functions.Count; f++)
                if (_functions[f].Name is RuntimeAbi.SetThreadBlock or RuntimeAbi.ReleaseRegion) { before[f] = true; next.Push(f); }
            while (next.TryPop(out int g))
                if (callers[g] is { } list)
                    foreach (int f in list)
                        if (!before[f]) { before[f] = true; next.Push(f); }
            return before;
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
        // only through what the loop itself writes, which goes to the heap,
        // and what a lap makes, with no boundary between the loop and its
        // maker, that is live where a lap ends: the boundary taking it is the
        // copy's own or one above, so it too goes to the heap, where fewer
        // sites go there than every lap makes for the loop's region that no
        // boundary takes. What a
        // boundary inside the lap takes is never sent there; a loop that
        // would refuse it gets no region. And where every lap that goes round
        // makes, in some copy, with no boundary between, something the region
        // takes; a loop that makes something only on a path seldom taken
        // pays for no call at the top of every lap. Then a site is taken
        // when, besides what the boundaries ask, no loop above any of its
        // objects -- one in whose body it is, or whose calls reach the copy
        // that makes it -- finds it live where a lap ends: whichever region
        // is innermost when it is made, it is dead by that region's end. And
        // a loop with nothing taken beneath it is given no region after all.

        /// <summary>The most copies and objects walked choosing loops: past it, the loops left get no region.</summary>
        private const long LoopBudget = 50_000_000;
        private long _loopWalked;
        // How deep AlwaysMakes follows calls made on every way to a return.
        private const int AlwaysDepth = 6;

        /// <summary>
        /// A loop given a region, in each copy of its function: the copies its
        /// calls reach (All), and what is reached from what is live where a
        /// lap ends and from the copy's frame slots, past the unknown object
        /// (LapLive, one bit an object). What outlives the copy (Outlives)
        /// outlives a lap too.
        /// </summary>
        private sealed record LoopRegion(int Function, RegionLoopShape Shape, List<(int Copy, int[] All, ulong[] LapLive)> Copies);

        private static bool Has(ulong[] set, int x) => (set[x >> 6] >> (x & 63) & 1) != 0;
        private ulong[] NoObjects() => new ulong[(_objectFunction.Count + 63) >> 6];

        // The functions a call by name, or a virtual call's symbol, in unit `u` may run.
        private readonly Dictionary<(int, string), HashSet<int>> _callFunctions = new();

        private HashSet<int> CallFunctions(int u, string name)
        {
            if (_callFunctions.TryGetValue((u, name), out HashSet<int>? known)) return known;
            HashSet<int> functions = new();
            IEnumerable<string> names = name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)
                ? _virtuals.GetValueOrDefault(name) ?? Array.Empty<string>() : new[] { name };
            bool virtualCall = names is string[] && name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal);
            foreach (string target in names)
                if ((virtualCall ? ResolveOverride(u, target) : Resolve(u, target)) is { } targets) functions.UnionWith(targets);
            return _callFunctions[(u, name)] = functions;
        }

        // Per copy, per call: Targets, found once.
        private int[]?[]?[] _targetsOf = Array.Empty<int[]?[]?>();

        /// <summary>
        /// The copies call `k` of copy `c` may reach: those `c` calls that are
        /// copies of a function the call names, in the order `c` calls them.
        /// Never fewer than it reaches; a call nobody can name reaches what
        /// code nobody follows calls, whose objects a region may take are
        /// dead by its return.
        /// </summary>
        private int[] Targets(int c, int k)
        {
            if (_targetsOf.Length != _copyFunction.Count) _targetsOf = new int[]?[]?[_copyFunction.Count];
            int f = _copyFunction[c];
            int[]?[] known = _targetsOf[c] ??= new int[]?[_functions[f].Calls.Count];
            if (known[k] is { } found) return found;
            List<int> targets = new();
            if (_functions[f].Calls[k].Callee is { } name && CallFunctions(_unitOf[f], name) is { Count: > 0 } functions)
                foreach (int t in _calleesOf[c])
                    if (functions.Contains(_copyFunction[t])) targets.Add(t);
            return known[k] = targets.Count == 0 ? Array.Empty<int>() : targets.ToArray();
        }

        /// <summary>
        /// THE COPIES A CALL REACHES, a component at a time: the components
        /// of the calls between copies (Tarjan's), each one's closure -- its
        /// own copies and every one its callees' components reach -- found
        /// once, when first asked, and kept: a small one as its copies, a
        /// large one a bit a copy. Copies `excluded` are never entered (a
        /// walk through no boundary). A loop's calls into the compiler's own
        /// cycle of thirteen thousand functions walked a million calls each,
        /// for thousands of loops; the cycle is one component here, its
        /// closure found once.
        /// </summary>
        private sealed class CallClosures
        {
            private const int Small = 256;
            private readonly int[][] _callees;
            private readonly bool[]? _excluded;
            private readonly int[] _component;
            private readonly List<int[]> _members = new();
            private readonly int[]?[] _list;
            private readonly ulong[]?[] _bits;
            private readonly ulong[] _scratch;
            private readonly int[] _seen;
            private int _stamp;

            public int Words { get; }

            public CallClosures(int[][] callees, bool[]? excluded)
            {
                _callees = callees;
                _excluded = excluded;
                int count = callees.Length;
                Words = (count + 63) >> 6;
                _scratch = new ulong[Words];
                _component = new int[count];
                int[] index = new int[count], low = new int[count];
                bool[] onStack = new bool[count];
                Array.Fill(index, -1);
                int next = 0;
                Stack<int> stack = new();
                Stack<(int Node, int At)> calls = new();
                for (int start = 0; start < count; start++)
                {
                    if (index[start] >= 0 || Excluded(start)) continue;
                    index[start] = low[start] = next++;
                    stack.Push(start); onStack[start] = true;
                    calls.Push((start, 0));
                    while (calls.Count > 0)
                    {
                        (int v, int at) = calls.Pop();
                        bool descended = false;
                        int[] out_ = callees[v];
                        while (at < out_.Length)
                        {
                            int w = out_[at++];
                            if (Excluded(w)) continue;
                            if (index[w] < 0)
                            {
                                calls.Push((v, at));
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
                            do { w = stack.Pop(); onStack[w] = false; component.Add(w); _component[w] = _members.Count; } while (w != v);
                            _members.Add(component.ToArray());
                        }
                        if (calls.Count > 0) { int parent = calls.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
                    }
                }
                _list = new int[]?[_members.Count];
                _bits = new ulong[]?[_members.Count];
                _seen = new int[_members.Count];
            }

            private bool Excluded(int c) => _excluded is not null && _excluded[c];

            /// <summary>Adds the copies a call of copy `c` may reach, itself among them, to `into`, a bit a copy.</summary>
            public void AddReached(int c, ulong[] into)
            {
                int k = _component[c];
                Close(k);
                if (_bits[k] is { } bits) for (int w = 0; w < bits.Length; w++) into[w] |= bits[w];
                else foreach (int x in _list[k]!) into[x >> 6] |= 1UL << (x & 63);
            }

            // Finds the closure of component `top` and of every one beneath it
            // not found yet, callees first: Tarjan's numbering already puts a
            // component after every one it reaches.
            private void Close(int top)
            {
                if (_list[top] is not null || _bits[top] is not null) return;
                Stack<int> pending = new();
                pending.Push(top);
                List<int> order = new();
                int stamp = ++_stamp;
                _seen[top] = stamp;
                while (pending.TryPop(out int k))
                {
                    order.Add(k);
                    foreach (int m in _members[k])
                        foreach (int w in _callees[m])
                        {
                            if (Excluded(w)) continue;
                            int s = _component[w];
                            if (_seen[s] != stamp && _list[s] is null && _bits[s] is null) { _seen[s] = stamp; pending.Push(s); }
                        }
                }
                order.Sort();
                foreach (int k in order)
                {
                    Array.Clear(_scratch);
                    int stampK = ++_stamp;
                    foreach (int m in _members[k])
                    {
                        _scratch[m >> 6] |= 1UL << (m & 63);
                        foreach (int w in _callees[m])
                        {
                            if (Excluded(w)) continue;
                            int s = _component[w];
                            if (s == k || _seen[s] == stampK) continue;
                            _seen[s] = stampK;
                            if (_bits[s] is { } bits) for (int i = 0; i < bits.Length; i++) _scratch[i] |= bits[i];
                            else foreach (int x in _list[s]!) _scratch[x >> 6] |= 1UL << (x & 63);
                        }
                    }
                    int count = 0;
                    foreach (ulong word in _scratch) count += System.Numerics.BitOperations.PopCount(word);
                    if (count >= Small) { _bits[k] = (ulong[])_scratch.Clone(); continue; }
                    int[] list = new int[count];
                    int at = 0;
                    for (int w = 0; w < _scratch.Length; w++)
                        for (ulong word = _scratch[w]; word != 0; word &= word - 1)
                            list[at++] = w << 6 | System.Numerics.BitOperations.TrailingZeroCount(word);
                    _list[k] = list;
                }
            }
        }

        // The copies the calls reach (All), and those reached through no
        // boundary copy (Near), found once for every loop (SelectLoops).
        private CallClosures _allClosures = null!, _nearClosures = null!;
        private ulong[] _allBits = Array.Empty<ulong>(), _nearBits = Array.Empty<ulong>();

        /// <summary>The copies a loop's calls in copy `c` reach, into `into`, a bit a copy: All, or with `near`, those reached through no boundary copy, Near -- where what is made is made in the loop's region.</summary>
        private void Reached(int c, int[] calls, bool near, ulong[] into)
        {
            Array.Clear(into);
            foreach (int k in calls)
                foreach (int t in Targets(c, k))
                    if (!near) _allClosures.AddReached(t, into);
                    else if (!_isBoundary[t]) _nearClosures.AddReached(t, into);
        }

        // The walk of FirstUnsound: a copy is found when its mark is the stamp of the walk.
        private int[] _allMark = Array.Empty<int>(), _walkStack = Array.Empty<int>();
        private int _walkStamp;

        /// <summary>
        /// The objects made beneath a loop's calls in copy `c` looked at, in
        /// the order a walk down the calls finds their copies (the order
        /// SelectLoops always asked in, and counts by), up to the first
        /// `unsound` (`found`); -1 for none.
        /// </summary>
        private long FirstUnsound(int c, int[] calls, Func<int, bool> unsound, out int found)
        {
            int count = _copyFunction.Count;
            if (_allMark.Length != count) { _allMark = new int[count]; _walkStack = new int[count]; _walkStamp = 0; }
            int stamp = ++_walkStamp;
            int[] mark = _allMark, stack = _walkStack;
            long looked = 0;
            int top = 0;
            found = -1;
            // Found: its objects looked at, then it is walked from.
            bool Found(int a, ref long looked, ref int found)
            {
                if (a == c || _madeAt[a] is not { } made) return false;
                foreach (int o in made)
                {
                    looked++;
                    if (unsound(o)) { found = o; return true; }
                }
                return false;
            }
            foreach (int k in calls)
                foreach (int t in Targets(c, k))
                    if (mark[t] != stamp)
                    {
                        mark[t] = stamp;
                        if (Found(t, ref looked, ref found)) return looked;
                        stack[top++] = t;
                    }
            while (top > 0)
                foreach (int callee in _calleesOf[stack[--top]])
                    if (mark[callee] != stamp)
                    {
                        mark[callee] = stamp;
                        if (Found(callee, ref looked, ref found)) return looked;
                        stack[top++] = callee;
                    }
            return looked;
        }

        /// <summary>What the nodes and frame slots given hold reaches in copy `c`, past what the unknown object reaches, one bit an object.</summary>
        private ulong[] HeldBy(int c, int[] nodes, IEnumerable<int> slots)
        {
            List<int> start = new();
            foreach (int n in nodes) start.AddRange(ObjectsHeld(Node(c, n)));
            foreach (int slot in slots)
                if (_slotObjects.TryGetValue(((long)c << 32) | (uint)slot, out int o)) start.Add(o);
            HashSet<int> reached = new();
            Reach(start, reached, _globalReach);
            _loopWalked += reached.Count;
            ulong[] held = NoObjects();
            foreach (int o in reached) held[o >> 6] |= 1UL << (o & 63);
            return held;
        }

        private (int, int) SiteOf(int o) => (_objectFunction[o], _objectSite[o]);

        // The objects of the sites escape graphs found held, past the global ones, one bit an object.
        private ulong[] GraphHeld(int[]? sites)
        {
            ulong[] held = NoObjects();
            if (sites is null) return held;
            ulong[] bits = _escape!.BitsOf(sites);
            for (int w = 0; w < bits.Length; w++)
                for (ulong word = bits[w]; word != 0; word &= word - 1)
                {
                    int o = ObjectOfSite(w << 6 | System.Numerics.BitOperations.TrailingZeroCount(word));
                    if (o > Global && !_isGlobal[o]) held[o >> 6] |= 1UL << (o & 63);
                }
            return held;
        }

        private int[]? _siteObjectOf;
        private int ObjectOfSite(int site)
        {
            if (_siteObjectOf is null)
            {
                _siteObjectOf = new int[_escape!.Global.Length];
                for (int o = 1; o < _siteNumber.Count; o++) if (_siteNumber[o] >= 0) _siteObjectOf[_siteNumber[o]] = o;
            }
            return _siteObjectOf[site];
        }

        // AlwaysMakes' copies seen: a copy is seen when its mark is the stamp of the search.
        private int[] _alwaysSeen = Array.Empty<int>();
        private int _alwaysStamp;

        /// <summary>
        /// Whether one of `calls` of copy `c` always makes something `takes`
        /// (RegionPointsTo.AlwaysMakes): a site its callee makes on every way
        /// to a return, or a call it makes on every way there, through no
        /// boundary copy. `stamp` marks the copies the search has seen. Given
        /// `into`, every such site is gathered there rather than the first
        /// found answering.
        /// </summary>
        private bool AlwaysMakes(int c, int[] calls, Func<int, bool> takes, int stamp, int depth, HashSet<(int, int)>? into = null)
        {
            if (depth > AlwaysDepth) return false;
            bool found = false;
            foreach (int k in calls)
                foreach (int t in Targets(c, k))
                {
                    if (_isBoundary[t] || _alwaysSeen[t] == stamp) continue;
                    _alwaysSeen[t] = stamp;
                    RegionFunction g = _functions[_copyFunction[t]];
                    if (_madeAt[t] is { } made)
                        foreach (int o in made)
                        {
                            if (Array.BinarySearch(g.MustSites, _objectSite[o]) < 0 || !takes(o)) continue;
                            if (into is null) return true;
                            into.Add(SiteOf(o));
                            found = true;
                        }
                    if (AlwaysMakes(t, g.MustCalls, takes, stamp, depth + 1, into))
                    {
                        if (into is null) return true;
                        found = true;
                    }
                }
            return found;
        }

        /// <summary>
        /// THE LOOPS GIVEN A REGION, judged against the sites the boundaries
        /// take (`verdict`, whose NearestAbove is the boundaries' state): in
        /// every copy, sound -- nothing taken that is made beneath the loop,
        /// in its body or in a copy its calls reach, live where a lap ends,
        /// but what a lap carries only through what the loop writes, and what
        /// a lap makes through no boundary, which TakenWithLoops sends to the
        /// heap -- in some copy, worth a call at the top of every lap, and
        /// sending fewer sites to the heap than every lap makes for it that
        /// no boundary takes.
        ///
        /// Thousands of loops reach the compiler's cycle of thirteen thousand
        /// functions, each walking all of it: every set here is a mark or a
        /// bit, and what a lap makes (lapMade) is asked of an object's makers
        /// rather than gathered.
        /// </summary>
        private List<LoopRegion> SelectLoops(Verdict verdict, bool[] beforeBlock)
        {
            List<LoopRegion> chosen = new();
            // A DIAGNOSTIC: --region-report +loopold weighs a loop as before
            // 04cbd6b, what it sends to the heap against every site its laps
            // make that it takes, whether a boundary takes it already or not.
            bool loopOld = _report?.Contains("+loopold") == true;
            if (_alwaysSeen.Length != _copyFunction.Count) _alwaysSeen = new int[_copyFunction.Count];
            _allClosures = new CallClosures(_calleesOf, null);
            _nearClosures = new CallClosures(_calleesOf, _isBoundary);
            _allBits = new ulong[_allClosures.Words];
            _nearBits = new ulong[_allClosures.Words];
            for (int f = 0; f < _functions.Count && _loopWalked < LoopBudget; f++)
            {
                RegionFunction function = _functions[f];
                if (function.Loops.Count == 0 || _copiesOf[f] is not { } copies || function.Name == _entry || beforeBlock[f]) continue;
                // A function escape graphs did not follow has nothing said of its loops.
                if (_escape is not null && _escape.LoopHeld[f] is null) continue;
                for (int loopIndex = 0; loopIndex < function.Loops.Count; loopIndex++)
                {
                    RegionLoopShape loop = function.Loops[loopIndex];
                    if (_loopWalked >= LoopBudget) break;
                    List<(int, int[], ulong[])> instances = new();
                    bool sound = true, worth = false;
                    int unsoundBy = -1;
                    // TO THE HEAP INSTEAD: a site the boundaries take that is
                    // live where a lap ends, made with no boundary between the
                    // loop and its maker -- the boundary taking it is this
                    // copy's own or one above it. TakenWithLoops refuses it
                    // wherever the loop keeps its region, so it is made on the
                    // heap; weighed against what every lap makes that the
                    // loop's region takes. A lap that only sometimes makes
                    // something for it -- a list grown now and then -- gives
                    // no reason to lose a site made on every one. And the
                    // loop must take more than it sends away, counting only
                    // what no boundary takes: a site a boundary takes already
                    // is only given back sooner by the loop, where one sent to
                    // the heap costs an allocation every lap and is left to
                    // the collector. A loop whose every lap makes only what a
                    // boundary takes anyway sends nothing to the heap or gets
                    // no region: Make's lap in 1290 sent the row a boundary
                    // took to the heap for the number's text, which that
                    // boundary took too.
                    HashSet<(int, int)> forced = new(), gained = new();
                    List<(int Copy, List<int>? Own, Func<int, bool> Takes)> weigh = new();
                    foreach (int c in copies)
                    {
                        ulong[] all = _allBits;
                        Reached(c, loop.Calls, false, all);
                        int allCount = 0;
                        foreach (ulong word in all) allCount += System.Numerics.BitOperations.PopCount(word);
                        _loopWalked += allCount;
                        ulong[] lapLive = _escape is not null ? GraphHeld(_escape.LoopHeld[f]?[loopIndex].LapLive)
                            : HeldBy(c, loop.Live, Enumerable.Range(0, function.Slots));
                        ulong[] kept = _escape is not null ? GraphHeld(_escape.LoopHeld[f]?[loopIndex].Kept)
                            : HeldBy(c, loop.Invariant, loop.KeptSlots);
                        ulong[]? escaping = EscapingBits(c);
                        bool Lap(int o) => Has(lapLive, o) || Outlives(o, c, escaping);
                        bool InBody(int o) => Array.BinarySearch(loop.Sites, _objectSite[o]) >= 0;
                        List<int>? own = _madeAt[c];
                        // Near is wanted only for what may be carried: found when first asked.
                        bool nearFound = false;
                        bool InNear(int x)
                        {
                            if (!nearFound) { Reached(c, loop.Calls, true, _nearBits); nearFound = true; }
                            return Has(_nearBits, x);
                        }
                        // Made in a lap, in the region the loop runs in: by the
                        // sites in its body, and in the copies its calls reach
                        // through no boundary -- by one of its makers.
                        bool LapMade(int o)
                        {
                            foreach (int maker in _objectMakers[o])
                                if (maker == c ? InBody(o) || InNear(c) : InNear(maker)) return true;
                            return false;
                        }
                        // CARRIED AND DROPPED: live where a lap ends only through
                        // what the loop itself writes. Refused by the region, it
                        // goes to the heap, as RegionPointsTo sends it. What is
                        // left live where a lap ends of what the boundaries take
                        // goes there too when a lap made it (forced, above); made
                        // beneath a boundary inside the lap, sending it to the
                        // heap would take from that boundary, and the loop gets
                        // no region (Unsound).
                        bool Live(int o)
                        {
                            if (!verdict.TakenObject[o]) return false;
                            bool live = Has(lapLive, o), outlives = Outlives(o, c, escaping);
                            if (!live && !outlives) return false;
                            bool carried = live && !outlives && !Has(kept, o) && LapMade(o);
                            return !carried;
                        }
                        bool Unsound(int o) => Live(o) && !LapMade(o);
                        // Beneath it: the body's own sites, and every copy its
                        // calls reach. Every one is looked at, and counted, in
                        // the order a walk down the calls finds it, up to the
                        // first that refuses the loop: a loop found sound is
                        // looked at in any order, one found not is looked at
                        // again in that order (FirstUnsound) to count the same.
                        // What a sound copy sends to the heap is the whole of
                        // what it looked at, in whatever order.
                        List<(int, int)> sent = new();
                        void Look(int o)
                        {
                            if (Live(o))
                            {
                                if (LapMade(o)) sent.Add(SiteOf(o));
                                else unsoundBy = o;
                            }
                        }
                        if (own is not null)
                            foreach (int o in own)
                                if (Has(all, c) || InBody(o))
                                {
                                    _loopWalked++;
                                    Look(o);
                                    if (unsoundBy >= 0) break;
                                }
                        if (unsoundBy < 0)
                        {
                            long looked = 0;
                            for (int w = 0; w < all.Length && unsoundBy < 0; w++)
                                for (ulong word = all[w]; word != 0 && unsoundBy < 0; word &= word - 1)
                                {
                                    int a = w << 6 | System.Numerics.BitOperations.TrailingZeroCount(word);
                                    if (a == c || _madeAt[a] is not { } made) continue;
                                    foreach (int o in made)
                                    {
                                        Look(o);
                                        if (unsoundBy >= 0) break;
                                    }
                                    looked += made.Count;
                                }
                            if (unsoundBy >= 0) looked = FirstUnsound(c, loop.Calls, Unsound, out unsoundBy);
                            _loopWalked += looked;
                        }
                        if (unsoundBy >= 0) { sound = false; break; }
                        forced.UnionWith(sent);
                        int[] reached = new int[allCount];
                        int at = 0;
                        for (int w = 0; w < all.Length; w++)
                            for (ulong word = all[w]; word != 0; word &= word - 1)
                                reached[at++] = w << 6 | System.Numerics.BitOperations.TrailingZeroCount(word);
                        instances.Add((c, reached, lapLive));
                        bool Takes(int o) => verdict.Refuser[o] == -1 && !Lap(o);
                        weigh.Add((c, own, Takes));
                        if (worth) continue;
                        if (own is not null)
                            foreach (int o in own)
                                if (Array.BinarySearch(loop.AlwaysSites, _objectSite[o]) >= 0 && Takes(o)) { worth = true; break; }
                        worth = worth || AlwaysMakes(c, loop.AlwaysCalls, Takes, ++_alwaysStamp, 0);
                    }
                    // Only where something goes to the heap: what every lap
                    // makes that no boundary takes, gathered in full.
                    if (sound && worth && forced.Count > 0)
                        foreach ((int c, List<int>? own, Func<int, bool> takes) in weigh)
                        {
                            // (+loopold: as before 04cbd6b, every site a lap
                            // makes that the loop takes, a boundary's too --
                            // an A/B of what the rule costs in loops and in
                            // what regions give back.)
                            bool TakesAlone(int o) => takes(o) && (loopOld || !verdict.TakenObject[o]);
                            if (own is not null)
                                foreach (int o in own)
                                    if (Array.BinarySearch(loop.AlwaysSites, _objectSite[o]) >= 0 && TakesAlone(o)) gained.Add(SiteOf(o));
                            AlwaysMakes(c, loop.AlwaysCalls, TakesAlone, ++_alwaysStamp, 0, gained);
                        }
                    gained.ExceptWith(forced);
                    bool weighed = forced.Count == 0 || forced.Count < gained.Count;
                    bool reported = _report is not null && _report.Any(w => function.Name.Contains(w, StringComparison.Ordinal));
                    if (sound && worth && weighed)
                    {
                        chosen.Add(new LoopRegion(f, loop, instances));
                        if (reported) Log($"loop region {function.Name} at block {loop.Header}" + (forced.Count > 0
                            ? $", {forced.Count} sites to the heap ({string.Join(", ", forced.Select(DescribeSite))}) for {gained.Count} every lap makes ({string.Join(", ", gained.Select(DescribeSite))})" : ""));
                    }
                    else if (reported)
                        Log($"no loop region {function.Name} at block {loop.Header}: " + (!sound ? DescribeObject(unsoundBy) + " is live where a lap ends"
                            : !worth ? "no lap always makes what it would take"
                            : $"sends {forced.Count} sites to the heap, every lap makes {gained.Count} it takes that no boundary does"));
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
                List<(int Loop, int Instance)>?[] over = new List<(int, int)>?[_copyFunction.Count], hosted = new List<(int, int)>?[_copyFunction.Count];
                for (int l = 0; l < loops.Count; l++)
                    for (int i = 0; i < loops[l].Copies.Count; i++)
                    {
                        (int c, int[] all, _) = loops[l].Copies[i];
                        foreach (int a in all) (over[a] ??= new()).Add((l, i));
                        (hosted[c] ??= new()).Add((l, i));
                    }
                bool[] under = new bool[_objectFunction.Count], refused = new bool[_objectFunction.Count];
                List<int>?[] beneath = new List<int>?[loops.Count];
                foreach ((int m, List<int> made) in madeBy)
                {
                    List<(int Loop, int Instance)>? reaching = over[m], own = hosted[m];
                    if (reaching is null && own is null) continue;
                    foreach (int o in made)
                    {
                        if (reaching is not null) foreach ((int l, int i) in reaching) Beneath(o, l, i);
                        if (own is not null)
                            foreach ((int l, int i) in own)
                                if (Array.BinarySearch(loops[l].Shape.Sites, _objectSite[o]) >= 0) Beneath(o, l, i);
                    }
                }
                void Beneath(int o, int l, int i)
                {
                    _walked++;
                    (int c, _, ulong[] lapLive) = loops[l].Copies[i];
                    under[o] = true;
                    (beneath[l] ??= new()).Add(o);
                    if (Has(lapLive, o) || Outlives(o, c)) refused[o] = true;
                }
                HashSet<(int, int)> taken = new();
                foreach (((int, int) key, List<int> objects) in bySite)
                {
                    bool anywhere = false, refusedAny = false;
                    foreach (int o in objects)
                    {
                        anywhere |= verdict.Above[o] != -1 || under[o];
                        refusedAny |= verdict.Refuser[o] != -1 || refused[o];
                    }
                    if (anywhere && !_unseenKept.Contains(key) && !refusedAny) taken.Add(key);
                }
                int before = loops.Count;
                for (int l = loops.Count - 1; l >= 0; l--)
                    if (beneath[l] is not { } objects || !objects.Any(o => taken.Contains(SiteOf(o))))
                    {
                        if (_report is not null && _report.Any(w => _functions[loops[l].Function].Name.Contains(w, StringComparison.Ordinal)))
                            Log($"loop region dropped {_functions[loops[l].Function].Name} at block {loops[l].Shape.Header}: nothing taken beneath it");
                        loops.RemoveAt(l);
                    }
                if (loops.Count == before) return taken;
            }
        }

        // ---- what a region would pile up -----------------------------------------
        //
        // A REGION GIVES BACK WHAT IT HOLDS ONLY WHEN IT ENDS: an object dead
        // long before is held to the end, where the collector would have taken
        // it at the next collection. One object or a few is nothing; the
        // objects of a loop's laps are as many as the laps. A site whose
        // objects die in the lap that made them, beneath a loop that has no
        // region of its own (or none beneath it that takes them), and whose
        // laps are not bounded, piles one more into the region every lap: a
        // pass over ten thousand instructions held ten thousand laps of
        // garbage until the pass returned. Such a site goes to the heap. What a
        // lap carries on, or what outlives the call, is held as long by the
        // collector, and stays in the region.
        //
        // Measured on the compiler's own build (two units): peak live heap 434
        // MB with regions empty, 2410 MB with 27099 sites taken, 762 MB of
        // arena in use at the peak.

        private HashSet<(int, int)> WithoutPiledUp(HashSet<(int, int)> taken, List<LoopRegion> loops)
        {
            HashSet<(int, int)> piled = PiledSites(taken, loops, out long looked, freshNear: false);
            HashSet<(int, int)> kept = new(taken);
            kept.ExceptWith(piled);
            if (_report is not null)
            {
                Log($"judge: {taken.Count - kept.Count} sites would pile up in a region lap after lap, to the heap; {looked} looked at, {_clock.ElapsedMilliseconds} ms");
                if (_report.Contains("+piled"))
                    foreach ((int f, int site) in taken.Except(kept)) Log("piled " + DescribeSite((f, site)));
            }
            return kept;
        }

        /// <summary>
        /// The sites of `among` that would pile up lap after lap in the
        /// region a loop runs in, with these loop regions and the boundaries
        /// as they stand (_isBoundary): asked by every round of the judge,
        /// so a boundary is weighed without what it would only pile up --
        /// Shelf.Make, credited with its loop's temporaries, refused what
        /// Work's region would have taken and gave back nothing (1316).
        /// `freshNear`: the calls' closures made again for the boundaries
        /// now chosen.
        /// </summary>
        private HashSet<(int, int)> PiledSites(HashSet<(int, int)> taken, List<LoopRegion> loops, out long looked, bool freshNear)
        {
            // The loop regions, by function and header, and the objects beneath each instance.
            Dictionary<int, List<LoopRegion>> regionsIn = new();
            foreach (LoopRegion loop in loops) (regionsIn.TryGetValue(loop.Function, out var l) ? l : regionsIn[loop.Function] = new()).Add(loop);
            HashSet<int> piled = new();
            if (_nearClosures is null || freshNear) _nearClosures = new CallClosures(_calleesOf, _isBoundary);
            ulong[] near = new ulong[_nearClosures.Words];
            looked = 0;
            for (int f = 0; f < _functions.Count; f++)
            {
                RegionFunction function = _functions[f];
                if (function.Loops.Count == 0 || _copiesOf[f] is not { } copies) continue;
                for (int loopIndex = 0; loopIndex < function.Loops.Count; loopIndex++)
                {
                    RegionLoopShape loop = function.Loops[loopIndex];
                    // Its own region gives each lap's dead back.
                    if (regionsIn.TryGetValue(f, out var own) && own.Any(r => r.Shape.Header == loop.Header)) continue;
                    // Laps bounded: a lap's worth times a known count, no more.
                    int repeat = Array.FindIndex(function.Repeats, r => r.Header == loop.Header);
                    if (repeat >= 0 && function.Repeats[repeat].Trip > 0) continue;
                    // The loop regions nested in this loop, in this function.
                    List<LoopRegion> inner = own?.Where(r => Nested(function, r.Shape.Header, loop.Header)).ToList() ?? new();
                    foreach (int c in copies)
                    {
                        ulong[] lapLive = _escape is not null
                            ? GraphHeld(_escape.LoopHeld[f]?[loopIndex].LapLive)
                            : HeldBy(c, loop.Live, Enumerable.Range(0, function.Slots));
                        ulong[]? escaping = EscapingBits(c);
                        // Dead in the lap that made it: not live where a lap
                        // ends, and not outliving the call. An object the
                        // escape graphs never followed is held as live.
                        bool DiesInLap(int o) => !Has(lapLive, o) && !Outlives(o, c, escaping);
                        bool InnerRegion(int o) => inner.Any(r => Array.BinarySearch(r.Shape.Sites, _objectSite[o]) >= 0);
                        if (_madeAt[c] is { } made)
                            foreach (int o in made)
                            {
                                looked++;
                                if (Array.BinarySearch(loop.Sites, _objectSite[o]) < 0 || !taken.Contains(SiteOf(o))) continue;
                                if (InnerRegion(o) || !DiesInLap(o)) continue;
                                piled.Add(o);
                            }
                        // Made beneath the lap's calls through no boundary: in
                        // the region the loop runs in -- unless a loop region of
                        // a copy beneath takes it (TakenWithLoops), cut each of
                        // that loop's laps.
                        Reached(c, loop.Calls, true, near);
                        for (int w = 0; w < near.Length; w++)
                            for (ulong word = near[w]; word != 0; word &= word - 1)
                            {
                                int a = w << 6 | System.Numerics.BitOperations.TrailingZeroCount(word);
                                if (a == c || _madeAt[a] is not { } beneath) continue;
                                bool cutBeneath = regionsIn.ContainsKey(_copyFunction[a]);
                                foreach (int o in beneath)
                                {
                                    looked++;
                                    if (!taken.Contains(SiteOf(o)) || !DiesInLap(o)) continue;
                                    if (cutBeneath && regionsIn[_copyFunction[a]].Any(r => Array.BinarySearch(r.Shape.Sites, _objectSite[o]) >= 0)) continue;
                                    piled.Add(o);
                                }
                            }
                    }
                }
            }
            HashSet<(int, int)> sites = new();
            foreach (int o in piled) sites.Add(SiteOf(o));
            return sites;
        }

        // Whether loop `inner` (by header) is inside loop `outer` in `function`, by the loops' parents.
        private static bool Nested(RegionFunction function, int inner, int outer)
        {
            int at = Array.FindIndex(function.Repeats, r => r.Header == inner);
            if (at < 0) return false;
            for (int l = function.Repeats[at].Parent; l >= 0; l = function.Repeats[l].Parent)
                if (function.Repeats[l].Header == outer) return true;
            return false;
        }

        // ---- what a region holds -----------------------------------------------
        //
        // THE BYTES A REGION NEEDS, where they can be proved: for a boundary
        // opened, the most its region holds in one call; for a loop given a
        // region, the most one lap makes in it -- handed to RegionEnter and
        // RegionLoop, which lay the region down where all of it fits rather
        // than growing the arena by guesses (Gc, "regions").
        //
        // What a scope holds -- a function's call, or a lap of one of its
        // loop regions -- is what stays in the region until it ends, OWN,
        // and above it at most one region opened inside at a time, PEAK: a
        // boundary called, or an inner loop's region -- its record and a lap
        // -- each given back before the next opens. Own is every site taken
        // there -- its block's bytes times how often it runs in the scope
        // (RegionFunction.Repeats) -- and the own of every function called
        // there that is no boundary, times how often the call runs (the most
        // of its targets'). A call is weighed only where what it reaches can
        // make something in a region (Contributes), and nothing that runs
        // only on the way to a throw (RegionFunction.Throwing): past its size
        // a region grows as an unsized one does. Nothing is proved where a
        // taken site's size is not a constant, where a site or a call that
        // makes something runs in a loop of laps not bounded, in a call
        // nobody can name, or in recursion.

        // Past this, a region is not sized.
        private const long MostRegionBytes = 1L << 40;

        private void Sizes(List<int> opened, List<LoopRegion> loops, HashSet<(int, int)> taken, Func<int, RegionFacts> facts)
        {
            if (opened.Count == 0 && loops.Count == 0) return;
            int count = _functions.Count;
            int word = _units.Count > 0 ? _units[0].WordSize : 4;
            long record = RegionLayout.Record(word);
            // WHAT ONE WORD CAN SAY: the size is handed to RegionEnter and
            // RegionLoop as an immediate of the target's word, and on i386 a
            // proof past 2^31 bytes came out a different number -- a region
            // laid down for a few bytes, or one asking the system for gigabytes.
            // Past a gigabyte of a 32-bit address space, a region is unsized.
            long mostSized = word >= 8 ? MostRegionBytes : 1L << 30;
            bool[] boundary = new bool[count];
            foreach (int f in opened) boundary[f] = true;
            Dictionary<int, List<int>> loopHeaders = new();
            foreach (LoopRegion loop in loops) (loopHeaders.TryGetValue(loop.Function, out var l) ? l : loopHeaders[loop.Function] = new()).Add(loop.Shape.Header);
            HashSet<int>? Targets(int f, int k) => _functions[f].Calls[k].Callee is { } name ? CallFunctions(_unitOf[f], name) : null;

            // WHAT CAN MAKE SOMETHING IN A REGION: a taken site, a loop region
            // or a boundary of its own, a call nobody can name, or a call of
            // what can.
            bool[] contributes = new bool[count];
            List<int>[] callers = new List<int>[count];
            Stack<int> next = new();
            for (int f = 0; f < count; f++)
            {
                RegionFunction function = _functions[f];
                bool own = boundary[f] || loopHeaders.ContainsKey(f);
                for (int site = 0; site < function.Sites.Length && !own; site++) own = taken.Contains((f, site));
                for (int k = 0; k < function.Calls.Count; k++)
                {
                    if (Targets(f, k) is not { } called) { own = true; continue; }
                    foreach (int t in called) (callers[t] ??= new()).Add(f);
                }
                if (own) { contributes[f] = true; next.Push(f); }
            }
            while (next.TryPop(out int t))
                if (callers[t] is { } list)
                    foreach (int f in list)
                        if (!contributes[f]) { contributes[f] = true; next.Push(f); }

            Dictionary<int, (long Own, long Peak)?> calls = new();
            Dictionary<int, long?> bounds = new();
            HashSet<int> running = new();

            // How often what is in loop `at` runs each time scope `scope` (a
            // loop, or -1 for the call) runs: null when not known.
            static long? Times(RegionFunction function, int at, int scope)
            {
                long times = 1;
                for (int l = at; l != scope; l = function.Repeats[l].Parent)
                {
                    if (l < 0) return null;
                    long trip = function.Repeats[l].Trip;
                    if (trip <= 0 || times > MostRegionBytes / trip) return null;
                    times *= trip;
                }
                return times;
            }

            // The loop region an item in loop `at` is made in: the innermost
            // loop holding it that has one, -1 for the call's own; Unbounded
            // where that is not known.
            int RegionOf(RegionFunction function, HashSet<int> regions, int at)
            {
                if (at == RegionFunction.Unbounded) return RegionFunction.Unbounded;
                for (int l = at; l >= 0; l = function.Repeats[l].Parent)
                    if (regions.Contains(l)) return l;
                return -1;
            }

            static long? Sum(long? a, long? b) => a is long x && b is long y && x + y <= MostRegionBytes ? x + y : null;

            (long Own, long Peak)? Scope(int f, int scope)
            {
                RegionFunction function = _functions[f];
                // Its loop regions, as loops of Repeats: each must be found.
                HashSet<int> regions = new();
                if (loopHeaders.TryGetValue(f, out List<int>? headers))
                    foreach (int header in headers)
                    {
                        int at = Array.FindIndex(function.Repeats, r => r.Header == header);
                        if (at < 0) return null;
                        regions.Add(at);
                    }
                long? own = 0;
                long peak = 0;
                for (int site = 0; site < function.Sites.Length; site++)
                {
                    if (!taken.Contains((f, site))) continue;
                    int at = function.LoopOfSite(site);
                    if (at == RegionFunction.Throwing) continue;
                    int region = RegionOf(function, regions, at);
                    if (region != scope && region != RegionFunction.Unbounded) continue;
                    if (region == RegionFunction.Unbounded || function.BytesOf(site) <= 0 || Times(function, at, scope) is not long times
                        || function.BytesOf(site) > MostRegionBytes / times) return null;
                    own = Sum(own, function.BytesOf(site) * times);
                }
                foreach (int inner in regions)
                {
                    if (RegionOf(function, regions, function.Repeats[inner].Parent) != scope) continue;
                    if (Scope(f, inner) is not (long lapOwn, long lapPeak)) return null;
                    peak = Math.Max(peak, record + lapOwn + lapPeak);
                }
                for (int k = 0; k < function.Calls.Count; k++)
                {
                    int at = function.LoopOfCall(k);
                    if (at == RegionFunction.Throwing) continue;
                    int region = RegionOf(function, regions, at);
                    if (region != scope && region != RegionFunction.Unbounded) continue;
                    if (Targets(f, k) is not { } called) return null;
                    long most = 0;
                    foreach (int t in called)
                    {
                        if (!contributes[t]) continue;
                        if (region == RegionFunction.Unbounded) return null;
                        if (boundary[t])
                        {
                            if (Bound(t) is not long b) return null;
                            peak = Math.Max(peak, record + b);
                            continue;
                        }
                        if (Call(t) is not (long calledOwn, long calledPeak)) return null;
                        most = Math.Max(most, calledOwn);
                        peak = Math.Max(peak, calledPeak);
                    }
                    if (most == 0) continue;
                    if (Times(function, at, scope) is not long times || most > MostRegionBytes / times) return null;
                    own = Sum(own, most * times);
                }
                return own is long o && o + peak <= MostRegionBytes ? (o, peak) : null;
            }

            // A call of a function that is no boundary: what it leaves in the
            // region it runs in. Recursion is not sized.
            (long Own, long Peak)? Call(int f)
            {
                if (calls.TryGetValue(f, out var known)) return known;
                if (!running.Add(f)) return null;
                var held = Scope(f, -1);
                running.Remove(f);
                return calls[f] = held;
            }

            // A boundary's region: the most it holds in one call, its record apart.
            long? Bound(int f)
            {
                if (bounds.TryGetValue(f, out long? known)) return known;
                if (!running.Add(f)) return null;
                long? bound = Scope(f, -1) is (long o, long p) ? o + p : null;
                running.Remove(f);
                return bounds[f] = bound;
            }

            bool Reported(int f) => _report is not null && _report.Any(w => _functions[f].Name.Contains(w, StringComparison.Ordinal));
            int sized = 0;
            foreach (int f in opened)
            {
                long? bound = Bound(f);
                if (bound is long b && b > 0 && b <= mostSized) { facts(f).BoundaryBytes[_functions[f].Name] = b; sized++; }
                if (Reported(f)) Log($"region of {_functions[f].Name}: " + (bound is long n ? n + " bytes" : "not sized"));
            }
            foreach (LoopRegion loop in loops)
            {
                RegionFunction function = _functions[loop.Function];
                int at = Array.FindIndex(function.Repeats, r => r.Header == loop.Shape.Header);
                long? lap = null;
                if (at >= 0 && !running.Contains(loop.Function)) { running.Add(loop.Function); lap = Scope(loop.Function, at) is (long o, long p) ? o + p : null; running.Remove(loop.Function); }
                if (lap is long b && b > 0 && b <= mostSized) { facts(loop.Function).LoopBytes[(function.Name, loop.Shape.Header)] = b; sized++; }
                if (Reported(loop.Function)) Log($"loop region of {function.Name} at block {loop.Shape.Header}: " + (lap is long n ? n + " bytes a lap" : "not sized"));
            }
            if (_report is not null) Log($"sizes: {sized} of {opened.Count + loops.Count} regions sized");
        }

        // ---- reporting --------------------------------------------------------

        // Which boundary copy found each refused object outliving it (-1:
        // none), for a report; and the objects so found, in the order found.
        private int[] _refusedBy = Array.Empty<int>();
        private readonly List<int> _refusedOrder = new();

        private void Report(SortedSet<int> chosen, List<int> opened, HashSet<(int, int)> taken, Dictionary<int, List<int>> madeBy)
        {
            // A key not taken though this object is local: which of its
            // objects some boundary refused, and which boundary.
            Dictionary<(int, int), string> why = new();
            foreach (int o in _refusedOrder)
                why.TryAdd((_objectFunction[o], _objectSite[o]), (_objectContext[o] >= 0 ? "context " + _objectContext[o] : "no context")
                    + " outlives " + _functions[_copyFunction[_refusedBy[o]]].Name + " context " + _copyContext[_refusedBy[o]]);
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
            if (_escape is not null) return "reached from the unknown object, or kept by a call nobody follows";
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

        private string DescribeSite((int Function, int Site) key)
        {
            RegionFunction f = _functions[key.Function];
            RegionSite s = f.Sites[key.Site];
            return f.Name + " line " + s.Line + " " + (s.Table ?? "block");
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
