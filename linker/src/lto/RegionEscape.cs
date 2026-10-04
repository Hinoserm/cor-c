using System.Runtime.InteropServices;
namespace Corsac.Lang.Lto;

/// <summary>
/// WHICH OBJECTS OUTLIVE EACH CALL, found from the bottom of the calls up:
/// the escape answers region inference stands on (RegionSolver's judge), for
/// a program of any size.
///
/// A function is solved by itself, Andersen's inclusion over its own nodes,
/// with locations an object and a byte offset into it (or any offset), so a
/// field's address and an element's stay what they are. Its objects are its
/// allocation sites and frame slots, the unknown object, and PLACES: what a
/// parameter points to, and what is reached from that by a field and then
/// another (deeper than that, one object for all of it). A load of a place
/// finds the place one field further on, besides whatever was written there.
///
/// What the function leaves the world -- what it writes into places and the
/// unknown object, and what it hands back, with the objects it made that
/// they reach -- is its SUMMARY: those objects and the cells between them.
/// A caller applies it at each call: a place is what the caller reaches by
/// the same fields from the argument, an object the callee made is one made
/// at that call (so two lists the caller fills are two lists still), and the
/// writes are writes.
///
/// A CYCLE OF CALLS IS SOLVED AS ONE: its members' nodes in one graph, a
/// call between them its arguments into the callee's parameters and the
/// callee's return into its result, the way a function's own nodes are --
/// no round after round of summaries. What each member's parameters come to
/// hold there, and its result, and the unknown object, reach the objects
/// that outlive it; its summary for calls from outside the cycle is what it
/// does to its own places.
///
/// A function's objects that its summary reaches outlive its return; every
/// other object made beneath it is dead by then. Nothing here gives up for
/// the program: past a bound, objects are merged, a node holds the unknown
/// object, or a function is not followed and everything made beneath it is
/// everyone's -- each only ever answering more outliving.
/// </summary>
internal sealed class RegionEscape
{
    // A field further than this is any offset: no field lies that far into
    // an object, and an address walked further is walking an array.
    private const int FarthestField = 4096;
    public const int Any = -1;
    // How many fields a place follows from its parameter before every deeper
    // object is one (Deep): a list, its array, and the elements in it. An
    // element read after them (any offset) is one field more: a list a
    // parameter holds -- an enumerator's, a struct's `this` -- is a field
    // further from it, and its array's elements are still kept apart from
    // all they reach. (Below the deep place instead, a foreach over a list
    // of nodes was handed every object of the tree for its loop variable.)
    private const int PlaceDepth = 2;
    // The most objects a summary keeps that the function made: past it the
    // rest are one object with all their origins.
    private const int MostFresh = 32;
    // The most places a summary keeps: past it the deepest are the deep place.
    private const int MostPlaces = 32;
    // The most cells a summary keeps: past it, every place below a
    // parameter is its deep place and every object made is one.
    private const int MostCells = 128;
    // A member of a cycle's parameters are numbered past every member before
    // it: its place in the cycle times this, plus the parameter.
    private const int ParamStride = 1 << 12;

    private readonly IReadOnlyList<RegionFunction> _functions;
    private readonly int[]?[][] _targets;
    private readonly string?[][] _keys;
    private readonly int[] _siteBase;
    private readonly bool[] _wanted;
    private readonly bool[] _rooted;
    private readonly Summary?[] _summaries;

    /// <summary>Counts for a report: locations carried, summaries applied, functions not followed, the largest cycle.</summary>
    public long Work, Applied, Unfollowed, LargestCycle, Fallbacks;

    /// <param name="functions">The functions the image keeps.</param>
    /// <param name="targets">Per function, per call: the functions it may run, or null for a call nobody follows.</param>
    /// <param name="keys">Per function, per call: a virtual call's symbol (its targets are its overrides), or null.</param>
    /// <param name="siteBase">Per function: the number of its first site among every function's.</param>
    /// <param name="wanted">Per function: whether what outlives it is asked for.</param>
    /// <param name="rooted">Per function: called from where nobody follows, with anything (the entry, code outside the IR, an address taken).</param>
    public RegionEscape(IReadOnlyList<RegionFunction> functions, int[]?[][] targets, string?[][] keys, int[] siteBase, int sites, bool[] wanted, bool[] rooted)
    {
        _functions = functions; _targets = targets; _keys = keys; _siteBase = siteBase; _wanted = wanted; _rooted = rooted;
        Escaping = new int[]?[functions.Count];
        Global = new bool[sites];
        LoopHeld = new (int[], int[])[]?[functions.Count];
        _summaries = new Summary?[functions.Count];
    }

    // A NUMBER HANDED TO A CALL holds no address: an argument every target
    // takes as a parameter of a number type (RegionFunction.NumberParams)
    // is nothing the call is handed. Where nobody can say who is called, it
    // is still handed on.
    private bool NumberArgument(int[]? targets, int a)
        => targets is { Length: > 0 } && targets.All(t => _functions[t].IsNumber(a));

    // ---- where an object came from ----------------------------------------
    //
    // AN OBJECT'S ORIGINS, not its sites: a site of its own (Leaf), or an
    // object of a callee's summary it was made from at a call (Ref: the
    // summary's holder and the object's place in it). A summary's object
    // keeps its own origins once, wherever it is applied, so what a call
    // made is one reference however many sites are beneath it; the sites an
    // origin may be are found by following origins down (SitesOf). Carried
    // as sets of sites, the summaries of the compiler's own link held
    // nineteen million of them, most the same ones again.

    private static int Leaf(int site) => -site - 1;
    private static int Ref(int holder, int index)
    {
        if (index >= 1 << IndexBits || holder >= 1 << (30 - IndexBits)) throw new InvalidOperationException("region escape: an object past what an origin can name");
        return holder << IndexBits | index;
    }
    private const int IndexBits = 10;

    // Per holder: each of its summary's objects' origins (null for a place).
    // A HOLDER IS NEVER RENAMED: once registered, its objects' origins stay
    // what they were, so a Ref anything still holds -- a summary, an answer,
    // what is global -- means what it meant when it was made. A summary solved
    // again comes out under a new holder unless it is the same as before.
    private readonly List<int[]?[]?> _holders = new();
    // What is global, and what roots hand back, while one component is
    // answered: each component's own (_globalOf, _rootedOf), so one solved
    // again takes back what it said before. Gathered at the end (Close).
    private HashSet<int> _globalRefs = new();
    // For a report: what only the rule for functions called from where nobody follows made global.
    private HashSet<int> _rootedRefs = new();
    public int GlobalByUnknown, GlobalByRoots;

    /// <summary>A summary's objects named for what is made from them: under its holder.</summary>
    private void Register(Summary s, int holder)
    {
        s.Holder = holder;
        while (_holders.Count <= holder) _holders.Add(null);
        int[]?[] origins = new int[]?[s.Objects.Count];
        for (int k = 0; k < s.Objects.Count; k++) if (s.Objects[k].Kind == Kind.Made) origins[k] = s.Objects[k].Origins;
        _holders[holder] = origins;
    }

    private int NewHolder() => Math.Max(_holders.Count, _functions.Count);

    private int[]? OriginsOf(int r)
    {
        if (r < 0 || (r >> IndexBits) >= _holders.Count || _holders[r >> IndexBits] is not { } h) return null;
        int k = r & ((1 << IndexBits) - 1);
        return k < h.Length ? h[k] : null;
    }

    /// <summary>Per function asked about: the origins of the objects made beneath it that outlive its return (sorted).</summary>
    public readonly int[]?[] Escaping;
    /// <summary>Per site: an object of it is reached from the unknown object, or outlives a call nobody follows.</summary>
    public readonly bool[] Global;
    /// <summary>Per function, per loop: the origins what is live where a lap ends reaches, and what the loop keeps reaches.</summary>
    public readonly (int[] LapLive, int[] Kept)[]?[] LoopHeld;

    // Per function asked about: its escaping sites as one bit a site, shared
    // by every function whose escaping origins are the same -- a cycle's
    // members' often are. A question is then one load.
    private ulong[]?[]? _escapingBits;
    private readonly Dictionary<int[], ulong[]> _bitsByOrigins = new(TargetsComparer.Instance);

    /// <summary>Whether an object of `site` made beneath function `f` may outlive its return.</summary>
    public bool Escapes(int f, int site)
    {
        if (EscapingBits(f) is not { } bits) return true;
        return (bits[site >> 6] >> (site & 63) & 1) != 0;
    }

    /// <summary>The sites whose objects made beneath function `f` may outlive its return, one bit a site; null when every one may.</summary>
    public ulong[]? EscapingBits(int f)
    {
        int[]? escaping = Escaping[f];
        if (escaping is null) return null;
        return (_escapingBits ??= new ulong[]?[Escaping.Length])[f] ??= BitsOf(escaping);
    }

    /// <summary>The sites some origins may be, one bit a site.</summary>
    public ulong[] BitsOf(int[] origins)
    {
        if (_bitsByOrigins.TryGetValue(origins, out ulong[]? known)) return known;
        ulong[] bits = new ulong[(Global.Length + 63) >> 6];
        // Asked only once every function is solved, when the holders are
        // all there: each holder's objects numbered from its first
        // (_holderFirst), an object seen when its mark is the walk's.
        if (_holderFirst is null || _holderFirst.Length != _holders.Count + 1)
        {
            _holderFirst = new int[_holders.Count + 1];
            for (int h = 0; h < _holders.Count; h++) _holderFirst[h + 1] = _holderFirst[h] + (_holders[h]?.Length ?? 0);
            _seenMark = new int[_holderFirst[^1]];
        }
        int stamp = ++_seenStamp;
        _next.Clear();
        void Visit(int r)
        {
            if (r < 0) { int site = -r - 1; bits[site >> 6] |= 1UL << (site & 63); return; }
            int h = r >> IndexBits, k = r & ((1 << IndexBits) - 1);
            if (h >= _holders.Count || _holders[h] is not { } objects || k >= objects.Length || objects[k] is null) return;
            int at = _holderFirst[h] + k;
            if (_seenMark![at] == stamp) return;
            _seenMark[at] = stamp;
            _next.Push(r);
        }
        foreach (int r in origins) Visit(r);
        while (_next.TryPop(out int r))
            foreach (int c in OriginsOf(r)!) Visit(c);
        return _bitsByOrigins[origins] = bits;
    }

    private int[]? _holderFirst, _seenMark;
    private int _seenStamp;
    private readonly Stack<int> _next = new();

    /// <summary>
    /// For the tests: a function's summary as it stands -- each object as
    /// its kind, parameter and path ("Place 0 [8]", "Deep -1 []", a guard
    /// "g", the deep step "deep"), its cells and its result, and whether it
    /// was made coarse -- or null for none, or the unknown call's.
    /// </summary>
    internal (string[] Objects, (int From, int Offset, int To, int ToOffset)[] Cells, (int To, int ToOffset)[] Result, bool Coarse)? SummaryOf(int f)
    {
        if (_summaries[f] is not { IsUnknown: false } s) return null;
        static string Step(int step) => step == DeepStep ? "deep" : IsGuard(step) ? "g" : step.ToString();
        return (s.Objects.Select(o => o.Kind + " " + o.Param + " [" + string.Join(",", o.Path.Select(Step)) + "]").ToArray(),
            s.Cells.ToArray(), s.Result.ToArray(), s.MadeCoarse);
    }

    /// <summary>
    /// For a report: where an origin comes from -- a site, or an object of
    /// whose summary (a function's, or a merged or stand-in one), what kind
    /// of object it is there, and so on down, `depth` steps.
    /// </summary>
    private string Lineage(int r, int depth)
    {
        if (r < 0) return "site " + (-r - 1);
        int h = r >> IndexBits, k = r & ((1 << IndexBits) - 1);
        string holder = h < _functions.Count ? _functions[h].Name : "merged or stand-in summary " + h;
        string kind = h < _functions.Count && _summaries[h] is { IsUnknown: false } s && k < s.Objects.Count ? s.Objects[k].Kind.ToString() : "object";
        int[]? below = OriginsOf(r);
        if (below is null) return $"{kind} {k} of {holder} (unregistered)";
        if (below.Length == 0) return $"{kind} {k} of {holder}, of no origin (a constant or a frame slot)";
        return $"{kind} {k} of {holder}" + (depth <= 0 ? "" : " < " + string.Join(" | ", below.Take(3).Select(x => Lineage(x, depth - 1))));
    }

    /// <summary>The sites the given origins may be.</summary>
    public int[] SitesOf(int[] origins)
    {
        HashSet<int> seen = new(), sites = new();
        Stack<int> next = new();
        foreach (int r in origins) if (seen.Add(r)) next.Push(r);
        while (next.TryPop(out int r))
        {
            if (r < 0) { sites.Add(-r - 1); continue; }
            if (OriginsOf(r) is { } below) foreach (int c in below) if (seen.Add(c)) next.Push(c);
        }
        return sites.Order().ToArray();
    }

    // After every function: what is global, down to sites -- what the unknown
    // object reaches, and what functions called from where nobody follows hand back.
    /// <summary>Whether a word of a site's objects, at a byte offset (Any: some word), is never read as a reference (RegionSolver.HoldsNoReference); null for no such knowledge.</summary>
    public Func<int, int, bool>? NoReference;

    /// <summary>A diagnostic only, never for an image: leave out what roots hand back.</summary>
    public bool NoRoots;
    /// <summary>A diagnostic: the sites to explain the global reach of.</summary>
    public Func<int, bool>? Why;
    public Func<int, bool>? WhyFunction;

    private void Close()
    {
        _globalRefs = new();
        _rootedRefs = new();
        foreach (HashSet<int>? each in _globalOf) if (each is not null) _globalRefs.UnionWith(each);
        foreach (HashSet<int>? each in _rootedOf) if (each is not null) _rootedRefs.UnionWith(each);
        Mark(_globalRefs);
        GlobalByUnknown = Global.Count(g => g);
        if (!NoRoots) Mark(_rootedRefs);
        GlobalByRoots = Global.Count(g => g) - GlobalByUnknown;
    }

    private void Mark(IEnumerable<int> refs)
    {
        Stack<int> next = new();
        HashSet<int> seen = new();
        foreach (int r in refs) if (seen.Add(r)) next.Push(r);
        while (next.TryPop(out int r))
        {
            if (r < 0) { Global[-r - 1] = true; continue; }
            if (OriginsOf(r) is { } below) foreach (int c in below) if (seen.Add(c)) next.Push(c);
        }
    }

    // ---- what a virtual call runs on an object, and guarded places ---------
    //
    // A VIRTUAL CALL RUNS ONE OVERRIDE ON EACH OBJECT: the one its
    // descriptor holds at the call's slot. On an object whose site is known,
    // that one's summary is applied, and no other's (Graph.Received). On a
    // place, whose object nobody here knows, each override's summary is
    // applied to the place GUARDED by it: the objects there that run that
    // override. A guard is a step of a place's path, as a field is; a caller
    // reaching the place by the same path keeps, at that step, only the
    // objects a guard lets through -- those of a site whose descriptor runs
    // the override, and any of no known descriptor. (Merged, every
    // override's effects fell on every object: the field a Name keeps its
    // string in, leaked by Name.Emit, is where an Add keeps its left
    // operand, and an Assign's whole tree leaked with its target's name.)

    /// <summary>Per function, per call, per site: the targets a virtual call runs on an object of the site; null for any of them (RegionSolver).</summary>
    public Func<int, int, int, int[]?>? TargetsOn;

    // A path's steps: a field (an offset, or Any), a guard (-2 - its id),
    // or the deep step -- every object below, by one field or more.
    private const int DeepStep = int.MinValue;
    private static bool IsField(int step) => step >= Any;
    private static bool IsGuard(int step) => step < Any && step != DeepStep;
    private static int GuardStep(int guard) => -2 - guard;
    private static int GuardOf(int step) => -2 - step;
    private static readonly int[] DeepBelow = { DeepStep };

    private static int Fields(int[] path)
    {
        int n = 0;
        foreach (int step in path) if (IsField(step)) n++;
        return n;
    }

    /// <summary>A deep place's path with its deep step: one made without is everything below the path it names.</summary>
    private static int[] DeepPath(int[] path) => KindOf(path) == Kind.Deep ? path : [.. path, DeepStep];

    /// <summary>A place's kind by its path: deep when it ends in the deep step and guards after it.</summary>
    private static Kind KindOf(int[] path)
    {
        int d = Array.LastIndexOf(path, DeepStep);
        if (d < 0) return Kind.Place;
        for (int i = d + 1; i < path.Length; i++) if (IsField(path[i])) return Kind.Place;
        return Kind.Deep;
    }

    /// <summary>
    /// A path one field further, kept to so many: past PlaceDepth fields,
    /// everything below the first PlaceDepth is the deep place; a field of
    /// the deep place is the deep place. A GUARD BEGINS THE COUNT AGAIN:
    /// what the objects of a class there hold is kept apart, PlaceDepth
    /// fields of it, before a deep place of its own -- a node's type, and
    /// the type that holds, not all the tree below the node. (A path has
    /// one guard, so it is at most twice as long.)
    /// </summary>
    private static int[] WithField(int[] path, int offset)
    {
        int g = Array.FindLastIndex(path, IsGuard);
        int fields = 0;
        for (int i = g + 1; i < path.Length; i++)
        {
            if (path[i] == DeepStep) return path;
            fields++;
        }
        return fields > PlaceDepth || fields == PlaceDepth && offset != Any ? [.. path, DeepStep] : [.. path, offset];
    }

    /// <summary>
    /// A place made coarse, KEEPING ITS GUARD: the guard's objects anywhere
    /// below the parameter where the path took fields to them, and all below
    /// them where it went on. Each covers what it was; a guard left out made
    /// a name's string, leaked deep in a tree, the whole tree below the
    /// parameter, once a summary went past its bounds.
    /// </summary>
    private static int[] Coarsened(int[] path)
    {
        bool below = Fields(path) > 0 || Array.IndexOf(path, DeepStep) >= 0;
        int g = Array.FindIndex(path, IsGuard);
        if (g < 0) return below ? DeepBelow : Array.Empty<int>();
        List<int> kept = new();
        if (g > 0) kept.Add(DeepStep);
        kept.Add(path[g]);
        for (int i = g + 1; i < path.Length; i++) if (!IsGuard(path[i])) { kept.Add(DeepStep); break; }
        return kept.ToArray();
    }

    /// <summary>
    /// A path guarded at its end: two guards running are both at once. ONE
    /// GUARD A PATH, the last: a guard further up is left out (and what it
    /// kept apart below a deep step is the deep place again). A caller's
    /// filter at the last guard already drops every object of another
    /// class, whatever the path took to it; kept, each guard of a recursion
    /// was another place at each level, and a cycle's receivers ran past
    /// what a node holds.
    /// </summary>
    private int[] WithGuard(int[] path, int guard)
    {
        if (path.Length > 0 && IsGuard(path[^1]))
        {
            int had = GuardOf(path[^1]);
            return had == guard ? path : [.. path[..^1], GuardStep(Both(had, guard))];
        }
        return path.Any(IsGuard) ? [.. Stripped(path), GuardStep(guard)] : [.. path, GuardStep(guard)];
    }

    /// <summary>A path's guards left out: its fields again from the parameter, what is below the first deep step that deep place.</summary>
    private static int[] Stripped(int[] path)
    {
        if (!path.Any(IsGuard)) return path;
        int[] kept = Array.Empty<int>();
        foreach (int step in path)
        {
            if (step == DeepStep) return Array.IndexOf(kept, DeepStep) >= 0 ? kept : [.. kept, DeepStep];
            if (IsField(step)) kept = WithField(kept, step);
        }
        return kept;
    }

    // A guard: what must hold of an object's site, each a virtual call
    // (named by its symbol and unit, through one call of it) that runs the
    // target on it.
    private readonly List<(int F, int K, int Target)[]> _guards = new();
    private readonly Dictionary<string, int> _guardIds = new(StringComparer.Ordinal);
    private readonly Dictionary<(int, int), bool> _sitePasses = new();

    private int Guard(int f, int k, int target) => Intern(new[] { (f, k, target) });

    private int Both(int a, int b) => Intern(_guards[a].Concat(_guards[b]));

    private int Intern(IEnumerable<(int F, int K, int Target)> conditions)
    {
        SortedDictionary<string, (int, int, int)> each = new(StringComparer.Ordinal);
        foreach (var c in conditions) each.TryAdd(_keys[c.F][c.K] + "#" + c.Target, c);
        string key = string.Join("|", each.Keys);
        if (_guardIds.TryGetValue(key, out int id)) return id;
        _guards.Add(each.Values.ToArray());
        return _guardIds[key] = _guards.Count - 1;
    }

    /// <summary>Whether an object of a site may be among what a guard lets through: every call of it runs its target there, or nobody knows what it runs.</summary>
    private bool SitePasses(int site, int guard)
    {
        if (_sitePasses.TryGetValue((site, guard), out bool known)) return known;
        bool passes = true;
        foreach (var (f, k, target) in _guards[guard])
            if (TargetsOn!(f, k, site) is { } runs && Array.BinarySearch(runs, target) < 0) { passes = false; break; }
        return _sitePasses[(site, guard)] = passes;
    }

    // ---- the order: callees first, a cycle together -----------------------

    /// <summary>Progress for a report (null: none).</summary>
    public Action<string>? Progress;

    /// <summary>For a report: the largest cycle with virtual calls of more than `most` targets left out, and the calls left out by targets.</summary>
    public void ReportCycles(int most)
    {
        int count = _functions.Count;
        int[] index = new int[count], low = new int[count];
        bool[] onStack = new bool[count];
        Array.Fill(index, -1);
        int next = 0, largest = 0;
        Stack<int> stack = new();
        Dictionary<string, int> wide = new(StringComparer.Ordinal);
        for (int f = 0; f < count; f++)
            for (int k = 0; k < _targets[f].Length; k++)
                if (_targets[f][k] is { } t && t.Length > most && _keys[f][k] is { } key) wide[key.Split('@')[0] + " (" + t.Length + ")"] = wide.GetValueOrDefault(key.Split('@')[0] + " (" + t.Length + ")") + 1;
        IEnumerable<int> Edges(int v)
        {
            for (int k = 0; k < _targets[v].Length; k++)
                if (_targets[v][k] is { } t && t.Length <= most) foreach (int w in t) yield return w;
        }
        Stack<(int Node, IEnumerator<int> Edges)> walk = new();
        for (int start = 0; start < count; start++)
        {
            if (index[start] >= 0) continue;
            index[start] = low[start] = next++;
            stack.Push(start); onStack[start] = true;
            walk.Push((start, Edges(start).GetEnumerator()));
            while (walk.Count > 0)
            {
                var (v, e) = walk.Peek();
                if (e.MoveNext())
                {
                    int w = e.Current;
                    if (index[w] < 0) { index[w] = low[w] = next++; stack.Push(w); onStack[w] = true; walk.Push((w, Edges(w).GetEnumerator())); }
                    else if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                    continue;
                }
                walk.Pop();
                if (low[v] == index[v])
                {
                    int size = 0, w;
                    do { w = stack.Pop(); onStack[w] = false; size++; } while (w != v);
                    largest = Math.Max(largest, size);
                }
                if (walk.Count > 0) { int parent = walk.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
            }
        }
        Progress?.Invoke($"escape graphs: without virtual calls of more than {most} targets the largest cycle is {largest}; {wide.Count} such slots");
        foreach (var (key, calls) in wide.OrderByDescending(x => x.Value).Take(15)) Progress?.Invoke($"escape graphs:   {calls} calls of {key}");
    }

    /// <summary>
    /// Virtual calls of more targets than this are assumed, not followed in
    /// order (WIDE CALLS, below); 0 follows every call (--region-report
    /// +wide=N). SIXTEEN BY DEFAULT: past that, a call's overrides are
    /// Equals, GetHashCode, ToString, an iterator's MoveNext, a visitor's
    /// Accept -- each joining all of them and all that calls them into one
    /// cycle, thirteen thousand of the compiler's functions, which only
    /// unification (Unified) can take and coarsely. A program's own calls
    /// of a handful of overrides stay edges of the order, solved exactly;
    /// rounds after the first solve again only what a grown stand-in
    /// changed (Again), so the rounds cost a fraction of the first.
    /// </summary>
    public int WideTargets = 16;

    /// <summary>
    /// A diagnostic and a trade: every stand-in made from the first round
    /// with every site its targets reach (as WidenAfter makes one that still
    /// grows), so only its bits are left to grow.
    /// </summary>
    public bool WidenFirst;

    public void Run()
    {
        if (WideTargets > 0)
        {
            _assumed = new(TargetsComparer.Instance);
            for (int round = 1; ; round++)
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                int solved = round == 1 ? Order() : Again();
                int grew = Check(round >= WidenAfter);
                Progress?.Invoke($"escape graphs: round {round}: {_assumed.Count} wide calls assumed, {grew} grew, {solved} of {_components.Count} components solved, largest cycle {LargestCycle}, "
                    + $"{Work} carried, {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:F0} ms");
                if (ReportStandIns) DescribeStandIns();
                if (grew == 0) break;
                if (round == MostRounds)
                {
                    // Still growing: every call followed in order after all.
                    Progress?.Invoke($"escape graphs: wide calls still growing after {round} rounds: every call followed");
                    Reset();
                    _assumed = null;
                    Order();
                    break;
                }
            }
        }
        else Order();
        Close();
    }

    // ---- wide calls -------------------------------------------------------
    //
    // A VIRTUAL CALL OF MANY TARGETS -- Equals, GetHashCode, ToString, an
    // iterator's MoveNext, each with hundreds of overrides -- joins every
    // override and all that calls it into one cycle: on the compiler's own
    // link, thirteen thousand functions, solved together and coarsely. So a
    // wide call is not an edge of the order: each set of targets is assumed
    // a summary (a stand-in), empty at first, and every function solved with
    // it. Then the targets' own summaries are asked whether the stand-in
    // covers them all; where it does not, it grows to cover them and every
    // function is solved again. What every stand-in covers is a fixed point
    // above the least one, so the answers are sound: by induction on how
    // deep calls go, each call does no more than its stand-in says.
    //
    // A stand-in is a shape of a few objects -- the unknown object, each
    // argument's own object and all below it, and every object its targets
    // may make (the sites, as leaves) -- with which of them may hold which
    // and be returned: a bit each, and the unknown call's bit. Shapes only
    // grow and are finite, so the rounds end; past WidenAfter, a stand-in
    // that grew takes every site its targets reach at once, and only its
    // bits are left to grow.

    private const int MostRounds = 8;
    private const int WidenAfter = 3;

    // A stand-in's objects, by class: the unknown object, what its targets
    // make, every argument past the first ParamClasses and all they reach
    // (Deep -1), and for each of the first ParamClasses parameters its own
    // object (Place) and all below it (Deep). Which class may hold which is
    // a bit a pair, which may be returned a bit a class, and the unknown
    // call one more.
    private const int ParamClasses = 6;
    private const int Classes = 3 + 2 * ParamClasses;
    private const int ResultBits = Classes * Classes;
    private const int UnknownBit = ResultBits + Classes;
    private const int ShapeWords = (UnknownBit + 64) / 64;

    private static bool Has(ulong[] bits, int bit) => (bits[bit >> 6] >> (bit & 63) & 1) != 0;
    private static void Set(ulong[] bits, int bit) => bits[bit >> 6] |= 1UL << (bit & 63);

    private sealed class Assumed
    {
        public readonly ulong[] Bits = new ulong[ShapeWords];
        public int[] Sites = Array.Empty<int>();
        public bool Widened;

        public Summary Build()
        {
            if (Has(Bits, UnknownBit)) return Summary.Unknown;
            Summary s = new();
            int[] index = new int[Classes];
            Array.Fill(index, -1);
            index[0] = 0;
            bool Uses(int cls)
            {
                for (int x = 0; x < Classes; x++)
                    if (Has(Bits, x * Classes + cls) || Has(Bits, cls * Classes + x)) return true;
                return Has(Bits, ResultBits + cls);
            }
            for (int cls = 1; cls < Classes; cls++)
            {
                if (!Uses(cls) && !(cls == 1 && Sites.Length > 0)) continue;
                index[cls] = s.Objects.Count;
                s.Objects.Add(cls switch
                {
                    1 => (Kind.Made, -1, Array.Empty<int>(), Sites.Select(Leaf).Order().ToArray()),
                    2 => (Kind.Deep, -1, Array.Empty<int>(), Array.Empty<int>()),
                    _ => (cls - 3) % 2 == 0 ? (Kind.Place, (cls - 3) / 2, Array.Empty<int>(), Array.Empty<int>()) : (Kind.Deep, (cls - 3) / 2, DeepBelow, Array.Empty<int>()),
                });
            }
            for (int x = 0; x < Classes; x++)
            {
                if (index[x] < 0) continue;
                for (int y = 0; y < Classes; y++)
                    if (index[y] >= 0 && Has(Bits, x * Classes + y)) s.Cells.Add((index[x], Any, index[y], Any));
                if (Has(Bits, ResultBits + x)) s.Result.Add((index[x], Any));
            }
            s.Cells.Sort(); s.Result.Sort();
            return s;
        }
    }

    /// <summary>A diagnostic: each round's stand-ins, by how much they hold.</summary>
    public bool ReportStandIns;

    private void DescribeStandIns()
    {
        string Name(int cls) => cls switch { 0 => "U", 1 => "M", 2 => "A*", _ => ((cls - 3) % 2 == 0 ? "P" : "D") + (cls - 3) / 2 };
        int unknown = _assumed!.Values.Count(a => Has(a.Bits, UnknownBit));
        int holding = _assumed.Values.Count(a => !Has(a.Bits, UnknownBit) && Enumerable.Range(2, Classes - 2).Any(y => Has(a.Bits, y)));
        int past = 0, unified = 0;
        for (int f = 0; f < _how.Length; f++) { if (_how[f] == How.PastBound) past++; else if (_how[f] == How.Unified) unified++; }
        Progress?.Invoke($"escape graphs: stand-ins: {unknown} of {_assumed.Count} the unknown call, {holding} more with the unknown object holding an argument; "
            + $"{past} functions unified past their bound, {unified} in large cycles");
        foreach (var (targets, a) in _assumed.OrderByDescending(x => x.Value.Bits.Sum(w => System.Numerics.BitOperations.PopCount(w))).Take(40))
        {
            List<string> held = new();
            for (int x = 0; x < Classes; x++)
                for (int y = 0; y < Classes; y++)
                    if (Has(a.Bits, x * Classes + y)) held.Add(Name(x) + ">" + Name(y));
            for (int x = 0; x < Classes; x++) if (Has(a.Bits, ResultBits + x)) held.Add("ret " + Name(x));
            if (Has(a.Bits, UnknownBit))
            {
                string[] why = targets.Where(t => _summaries[t] is null or { IsUnknown: true }).Take(4).Select(t => _functions[t].Name).ToArray();
                held.Add("UNKNOWN by " + string.Join(", ", why));
            }
            // WHO MAKES THE UNKNOWN OBJECT HOLD AN ARGUMENT: the targets
            // whose own summaries say so, how many, and how each was found
            // -- by inclusion, unified as a large cycle or past its bound,
            // made coarse past a summary's bounds.
            bool LeaksArgument(ulong[] bits)
            {
                for (int y = 2; y < Classes; y++) if (Has(bits, y)) return true;
                return false;
            }
            if (LeaksArgument(a.Bits))
            {
                List<int> leaking = targets.Where(t => _summaries[t] is { IsUnknown: false } s && LeaksArgument(Shape(s).Bits)).ToList();
                string Tag(int t) => (t < _how.Length ? _how[t] : How.None) switch { How.Unified => "unified", How.PastBound => "past bound", How.Inclusion => "inclusion", _ => "?" }
                    + (_summaries[t]!.MadeCoarse ? ", coarse" : "");
                held.Add($"U>arg by {leaking.Count}: " + string.Join(", ", leaking.Take(4).Select(t => _functions[t].Name + " (" + Tag(t) + ")")));
            }
            Progress?.Invoke($"escape graphs:   {targets.Length} targets ({_functions[targets[0]].Name}): {a.Sites.Length} sites; " + string.Join(" ", held));
        }
    }

    private Dictionary<int[], Assumed>? _assumed;

    // For a report: how each function's summary was found.
    private enum How : byte { None, Inclusion, Unified, PastBound }
    private How[] _how = Array.Empty<How>();
    private readonly Dictionary<int[], Summary> _standIns = new(TargetsComparer.Instance);

    private bool IsWide(int[] targets) => _assumed is not null && targets.Length > WideTargets;

    /// <summary>The summary a wide call is assumed to have this round.</summary>
    private Summary StandIn(int[] targets)
    {
        if (_standIns.TryGetValue(targets, out Summary? known)) return known;
        if (!_assumed!.TryGetValue(targets, out Assumed? a))
        {
            _assumed[targets] = a = new Assumed();
            if (WidenFirst) { a.Widened = true; a.Sites = Beneath(targets).Order().ToArray(); }
        }
        Summary s = a.Build();
        if (!s.IsUnknown) Register(s, NewHolder());
        return _standIns[targets] = s;
    }

    // A summary as a stand-in's shape: which classes hold which, and the
    // sites its made objects may be.
    private (ulong[] Bits, int[] Sites) Shape(Summary s)
    {
        ulong[] bits = new ulong[ShapeWords];
        if (s.IsUnknown) { Set(bits, UnknownBit); return (bits, Array.Empty<int>()); }
        int Class(int k)
        {
            var o = s.Objects[k];
            if (o.Kind == Kind.Unknown) return 0;
            if (o.Kind == Kind.Made) return 1;
            if (o.Param < 0 || o.Param >= ParamClasses) return 2;
            // (A place guarded at its parameter is still that object.)
            return 3 + 2 * o.Param + (o.Kind == Kind.Place && Fields(o.Path) == 0 && Array.IndexOf(o.Path, DeepStep) < 0 ? 0 : 1);
        }
        foreach (var c in s.Cells) Set(bits, Class(c.From) * Classes + Class(c.To));
        foreach (var r in s.Result) Set(bits, ResultBits + Class(r.To));
        List<int> origins = new();
        foreach (var o in s.Objects) if (o.Kind == Kind.Made) origins.AddRange(o.Origins);
        return (bits, origins.Count == 0 ? Array.Empty<int>() : SitesOf(origins.ToArray()));
    }

    /// <summary>Grows every stand-in its targets' summaries are not covered by: how many grew.</summary>
    private int Check(bool widen)
    {
        int grew = 0;
        foreach (var (targets, a) in _assumed!)
        {
            ulong[] bits = new ulong[ShapeWords];
            HashSet<int> sites = new();
            foreach (int t in targets)
            {
                // By the summary itself: one a round kept (Publish) keeps its shape.
                Summary summary = _summaries[t] ?? _noSummary;
                if (!_shapes.TryGetValue(summary, out var shape)) _shapes[summary] = shape = Shape(summary);
                for (int w = 0; w < ShapeWords; w++) bits[w] |= shape.Bits[w];
                sites.UnionWith(shape.Sites);
            }
            HashSet<int> had = new(a.Sites);
            bool covered = sites.IsSubsetOf(had);
            for (int w = 0; w < ShapeWords; w++) if ((bits[w] & ~a.Bits[w]) != 0) covered = false;
            if (covered) continue;
            grew++;
            // Built again, under a new holder, where next applied.
            _grown.Add(targets);
            _standIns.Remove(targets);
            for (int w = 0; w < ShapeWords; w++) a.Bits[w] |= bits[w];
            if (widen && !a.Widened)
            {
                a.Widened = true;
                sites.UnionWith(Beneath(targets));
            }
            sites.UnionWith(had);
            a.Sites = sites.Order().ToArray();
        }
        return grew;
    }

    private readonly Dictionary<Summary, (ulong[] Bits, int[] Sites)> _shapes = new(ReferenceEqualityComparer.Instance);
    private static readonly Summary _noSummary = Summary.Unknown;

    // Every site in the targets and every function they may call.
    private IEnumerable<int> Beneath(int[] targets)
    {
        HashSet<int> seen = new(targets);
        Stack<int> next = new(targets);
        while (next.TryPop(out int f))
        {
            for (int site = 0; site < _functions[f].Sites.Length; site++) yield return _siteBase[f] + site;
            foreach (int[]? those in _targets[f])
                if (those is not null) foreach (int g in those) if (seen.Add(g)) next.Push(g);
        }
    }

    /// <summary>Everything solved, forgotten: every call to be followed in order from nothing.</summary>
    private void Reset()
    {
        Array.Clear(_summaries);
        Array.Clear(_how);
        Array.Clear(Escaping);
        Array.Clear(LoopHeld);
        _holders.Clear();
        _globalRefs.Clear();
        _rootedRefs.Clear();
        _merged.Clear();
        _standIns.Clear();
        _escapingBits = null;
        _holderFirst = null;
        _bitsByOrigins.Clear();
        _components.Clear(); _globalOf.Clear(); _rootedOf.Clear(); _shapes.Clear();
        _grown.Clear();
        Work = Applied = Unfollowed = LargestCycle = Fallbacks = 0;
    }

    // ---- solving again: only what a grown stand-in changed -----------------
    //
    // THE COMPONENTS, IN THE ORDER THE FIRST ROUND SOLVED THEM: a wide call is
    // no edge of the order, so the order is the same every round. A later
    // round solves a component again only if it applies a stand-in that grew,
    // or calls (not as a wide call) a function outside it whose summary came
    // out different this round; callers come after their callees, so one pass
    // reaches every change. A summary that comes out the same as before keeps
    // its holder and object (Publish), and nothing above it is solved again
    // for its sake. What a component not solved again answered -- what
    // outlives its members, its loops, what it made global -- was answered
    // from summaries, stand-ins and merged summaries that are all still the
    // ones it read, under holders whose origins never change; so every answer
    // is the final summaries' answer, as a round solving everything gives.
    //
    // WHAT ELSE A SOLVE READS does not change between rounds: what a virtual
    // call runs on an object of a site (TargetsOn) is the descriptors' and
    // the targets', and a guard is interned once for good (Intern), so a path
    // that names one names it in every round and a summary kept (SameAs)
    // still means what it says. A call watched at its receiver (Received)
    // applies merged summaries of some of its targets outside the component
    // (Group): each of them is one of the call's targets, so among the
    // callees whose change solves the component again.
    private readonly List<int[]> _components = new();
    private readonly List<HashSet<int>?> _globalOf = new(), _rootedOf = new();
    private readonly HashSet<int[]> _grown = new(TargetsComparer.Instance);
    private bool[]? _changed;
    private int[]? _componentOf;
    private List<(int[] Functions, int[][] Wide)>? _inputs;

    private int Again()
    {
        Work = Applied = Unfollowed = Fallbacks = 0;
        _escapingBits = null;
        int count = _functions.Count;
        if (_componentOf is null || _inputs is null)
        {
            _componentOf = new int[count];
            for (int c = 0; c < _components.Count; c++) foreach (int f in _components[c]) _componentOf[f] = c;
            _inputs = new();
            for (int c = 0; c < _components.Count; c++)
            {
                HashSet<int> callees = new();
                HashSet<int[]> wide = new(TargetsComparer.Instance);
                foreach (int f in _components[c])
                    foreach (int[]? targets in _targets[f])
                    {
                        if (targets is null) continue;
                        if (IsWide(targets)) { wide.Add(targets); continue; }
                        foreach (int t in targets) if (_componentOf[t] != c) callees.Add(t);
                    }
                _inputs.Add((callees.ToArray(), wide.ToArray()));
            }
        }
        _changed = new bool[count];
        int solved = 0;
        for (int c = 0; c < _components.Count; c++)
        {
            (int[] callees, int[][] wide) = _inputs[c];
            bool again = false;
            foreach (int[] targets in wide) if (_grown.Contains(targets)) { again = true; break; }
            if (!again) foreach (int t in callees) if (_changed[t]) { again = true; break; }
            if (!again) continue;
            solved++;
            SolveComponent(c);
        }
        _grown.Clear();
        _changed = null;
        return solved;
    }

    /// <summary>Component c solved (again): its answers replace what it answered before.</summary>
    private void SolveComponent(int c)
    {
        int[] members = _components[c];
        foreach (int f in members) { Escaping[f] = null; LoopHeld[f] = null; }
        HashSet<int> global = _globalRefs, rooted = _rootedRefs;
        _globalRefs = new(); _rootedRefs = new();
        Solve(new List<int>(members));
        _globalOf[c] = _globalRefs; _rootedOf[c] = _rootedRefs;
        _globalRefs = global; _rootedRefs = rooted;
    }

    /// <summary>
    /// Member f's summary, solved: the one it had if this is the same (its
    /// holder, and every Ref to it, still good), else this under a holder of
    /// its own -- f itself the first time, a new one after.
    /// </summary>
    private void Publish(int f, Summary s)
    {
        // For a report (+why=Name): the summary each function asked about
        // gives its callers, and what of it the unknown object holds.
        if (Why is not null && WhyFunction?.Invoke(f) == true && Progress is { } say)
        {
            static string Step(int step) => step == DeepStep ? "deep" : IsGuard(step) ? "g" + GuardOf(step) : step.ToString();
            string Of(int k) => s.Objects[k].Kind + " " + s.Objects[k].Param + " [" + string.Join(",", s.Objects[k].Path.Select(Step)) + "]";
            say(s.IsUnknown ? $"escape graphs why: summary of {_functions[f].Name}: the unknown call's"
                : $"escape graphs why: summary of {_functions[f].Name}: {s.Objects.Count} objects, {s.Cells.Count} cells, {s.Result.Count} results{(s.MadeCoarse ? ", made coarse" : "")}; "
                  + "the unknown object holds " + string.Join("; ", s.Cells.Where(c => c.From == 0 && c.To != 0).Select(c => Of(c.To)).Distinct()));
        }
        Summary? before = _summaries[f];
        if (before is not null && before.SameAs(s)) return;
        int holder = before is null && (_holders.Count <= f || _holders[f] is null) ? f : NewHolder();
        Register(_summaries[f] = s, holder);
        if (_changed is not null) _changed[f] = true;
    }

    // Callees first, a cycle together; a wide call is no edge. Each component
    // is kept, in the order solved, for the rounds after (Again).
    private int Order()
    {
        int count = _functions.Count;
        int[] index = new int[count], low = new int[count];
        bool[] onStack = new bool[count];
        Array.Fill(index, -1);
        int next = 0;
        Stack<int> stack = new();
        Stack<(int Node, int Call, int Target)> walk = new();
        for (int start = 0; start < count; start++)
        {
            if (index[start] >= 0) continue;
            index[start] = low[start] = next++;
            stack.Push(start); onStack[start] = true;
            walk.Push((start, 0, 0));
            while (walk.Count > 0)
            {
                (int v, int call, int target) = walk.Pop();
                bool descended = false;
                int[]?[] calls = _targets[v];
                while (call < calls.Length)
                {
                    int[]? those = calls[call];
                    if (those is null || target >= those.Length || IsWide(those)) { call++; target = 0; continue; }
                    int w = those[target++];
                    if (index[w] < 0)
                    {
                        walk.Push((v, call, target));
                        index[w] = low[w] = next++;
                        stack.Push(w); onStack[w] = true;
                        walk.Push((w, 0, 0));
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
                    _components.Add(component.ToArray()); _globalOf.Add(null); _rootedOf.Add(null);
                    SolveComponent(_components.Count - 1);
                }
                if (walk.Count > 0) { int parent = walk.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
            }
        }
        _componentOf = null; _inputs = null;
        return _components.Count;
    }

    // A cycle this large, or with this many nodes, is solved by unification (Unified).
    private const int LargeCycle = 300;
    private const long LargeNodes = 100_000;

    private void Solve(List<int> component)
    {
        if (_how.Length != _functions.Count) _how = new How[_functions.Count];
        LargestCycle = Math.Max(LargestCycle, component.Count);
        long began = Progress is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        long nodes = 0;
        foreach (int f in component) nodes += _functions[f].Nodes;
        if (component.Count > LargeCycle || nodes > LargeNodes)
        {
            Unified u = new Unified(this, component.ToArray()).Solved();
            for (int m = 0; m < component.Count; m++) Publish(component[m], u.Summarise(m));
            for (int m = 0; m < component.Count; m++) { u.Answer(m); _how[component[m]] = How.Unified; }
            Progress?.Invoke($"escape graphs: cycle of {component.Count} ({_functions[component[0]].Name}) unified in {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:F0} ms: "
                + u.Describe() + $", heap {GC.GetTotalMemory(false) >> 20} MB");
            return;
        }
        Graph g = new Graph(this, component.ToArray()).Solved();
        if (g.Overflowed)
        {
            // PAST ITS BOUND BY INCLUSION, solved by unification: coarser,
            // and still each object's own, where giving up made every site
            // in it global and its summary the unknown call's.
            Fallbacks++;
            Unified u = new Unified(this, component.ToArray()).Solved();
            for (int m = 0; m < component.Count; m++) Publish(component[m], u.Summarise(m));
            for (int m = 0; m < component.Count; m++) { u.Answer(m); _how[component[m]] = How.PastBound; }
            if (Progress is not null && System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds > 300)
                Progress($"escape graphs: {_functions[component[0]].Name} ({component.Count}) past its bound by inclusion, unified in {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:F0} ms: " + g.Describe());
            return;
        }
        for (int m = 0; m < component.Count; m++) Publish(component[m], g.Summarise(m));
        for (int m = 0; m < component.Count; m++) { g.Answer(m); _how[component[m]] = How.Inclusion; }
        if (Progress is not null && (component.Count > 50 || System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds > 300))
            Progress($"escape graphs: cycle of {component.Count} ({_functions[component[0]].Name}) in {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:F0} ms: "
                + g.Describe() + $", heap {GC.GetTotalMemory(false) >> 20} MB");
    }

    // One merged summary for each set of targets, whatever call asks.
    private sealed class TargetsComparer : IEqualityComparer<int[]>
    {
        public static readonly TargetsComparer Instance = new();
        public bool Equals(int[]? a, int[]? b) => a!.AsSpan().SequenceEqual(b);
        public int GetHashCode(int[] a)
        {
            HashCode h = new();
            foreach (int x in a) h.Add(x);
            return h.ToHashCode();
        }
    }
    private readonly Dictionary<int[], (Summary Merged, int[] From)> _merged = new(TargetsComparer.Instance);

    /// <summary>
    /// The one summary for a set of targets solved before: any of them may
    /// run. Kept while every target's summary is the one it was made from
    /// (by holder): a round that changed one makes it again, under a new holder.
    /// </summary>
    private Summary MergedFor(int[] targets)
    {
        if (targets.Length == 1) return _summaries[targets[0]] ?? Summary.Unknown;
        int[] from = new int[targets.Length];
        for (int k = 0; k < targets.Length; k++) from[k] = _summaries[targets[k]] is { } s ? s.Holder : -2;
        if (_merged.TryGetValue(targets, out var done) && done.From.AsSpan().SequenceEqual(from)) return done.Merged;
        Summary merged = Summary.Merge(targets.Select(t => _summaries[t] ?? Summary.Unknown));
        if (!merged.IsUnknown) Register(merged, NewHolder());
        _merged[targets] = (merged, from);
        return merged;
    }

    // ---- a summary --------------------------------------------------------

    private enum Kind : byte { Unknown, Place, Deep, Made }

    /// <summary>
    /// What a call leaves the world. Its objects: the unknown object (0),
    /// places -- a parameter and the fields followed from it, or the deep
    /// place past those (parameter -1: past every parameter) -- and objects
    /// it made, each with its origins. Its cells: an object, an offset, and
    /// the locations written there. Its result: locations.
    /// </summary>
    private sealed class Summary
    {
        public readonly List<(Kind Kind, int Param, int[] Path, int[] Origins)> Objects = new() { (Kind.Unknown, -1, Array.Empty<int>(), Array.Empty<int>()) };
        public readonly List<(int From, int Offset, int To, int ToOffset)> Cells = new();
        public readonly List<(int To, int ToOffset)> Result = new();
        /// <summary>The unknown call's summary: every argument escapes, the result is the unknown object.</summary>
        public bool IsUnknown;
        /// <summary>Whose objects these are, for an object made from one (Ref): a function, or a merged summary registered as one; -1 for none.</summary>
        public int Holder = -1;
        /// <summary>For a report: past its bounds, made coarse (Coarse, Everything).</summary>
        public bool MadeCoarse;

        public static Summary Unknown => new() { IsUnknown = true };

        /// <summary>Whether two summaries say the same, object by object, origins and all.</summary>
        public bool SameAs(Summary other)
        {
            if (IsUnknown || other.IsUnknown) return IsUnknown == other.IsUnknown;
            if (Objects.Count != other.Objects.Count || Cells.Count != other.Cells.Count || Result.Count != other.Result.Count) return false;
            for (int k = 0; k < Objects.Count; k++)
            {
                var a = Objects[k];
                var b = other.Objects[k];
                if (a.Kind != b.Kind || a.Param != b.Param || !a.Path.AsSpan().SequenceEqual(b.Path) || !a.Origins.AsSpan().SequenceEqual(b.Origins)) return false;
            }
            for (int k = 0; k < Cells.Count; k++) if (Cells[k] != other.Cells[k]) return false;
            for (int k = 0; k < Result.Count; k++) if (Result[k] != other.Result[k]) return false;
            return true;
        }

        /// <summary>Every summary's effects at once: what any of a virtual call's overrides may do.</summary>
        public static Summary Merge(IEnumerable<Summary> each)
        {
            Summary merged = new();
            Dictionary<(Kind, int, string), int> places = new();
            foreach (Summary s in each)
            {
                if (s.IsUnknown) return Unknown;
                int[] map = new int[s.Objects.Count];
                for (int k = 0; k < s.Objects.Count; k++)
                {
                    var o = s.Objects[k];
                    if (o.Kind == Kind.Unknown) { map[k] = 0; continue; }
                    if (o.Kind is Kind.Place or Kind.Deep)
                    {
                        var key = (o.Kind, o.Param, string.Join(",", o.Path));
                        if (!places.TryGetValue(key, out int at)) { at = merged.Objects.Count; merged.Objects.Add(o); places[key] = at; }
                        map[k] = at;
                        continue;
                    }
                    map[k] = merged.Objects.Count;
                    merged.Objects.Add(s.Holder >= 0 ? (o.Kind, o.Param, o.Path, new[] { Ref(s.Holder, k) }) : o);
                }
                foreach (var c in s.Cells) merged.Cells.Add((map[c.From], c.Offset, map[c.To], c.ToOffset));
                foreach (var r in s.Result) merged.Result.Add((map[r.To], r.ToOffset));
            }
            merged.Cells.Sort(); merged.Result.Sort();
            Dedupe(merged.Cells); Dedupe(merged.Result);
            return merged.Bounded();
        }

        public static void Dedupe<T>(List<T> sorted) where T : IEquatable<T>
        {
            int w = 0;
            for (int r = 0; r < sorted.Count; r++)
                if (w == 0 || !sorted[r].Equals(sorted[w - 1])) sorted[w++] = sorted[r];
            sorted.RemoveRange(w, sorted.Count - w);
            // A summary is kept for good: not at the size it was gathered at.
            sorted.TrimExcess();
        }

        /// <summary>
        /// THE COARSEST SUMMARY BUT THE UNKNOWN CALL'S: each parameter's own
        /// object, everything below it one deep place, everything made one
        /// object, every cell at any offset.
        /// </summary>
        public Summary Coarse()
        {
            Summary b = new();
            int[] map = new int[Objects.Count];
            Dictionary<(Kind, int, string), int> kept = new();
            int Keep(Kind kind, int param, int[] path, int[] origins)
            {
                var key = (kind, param, string.Join(",", path));
                if (kept.TryGetValue(key, out int at)) return at;
                at = b.Objects.Count;
                b.Objects.Add((kind, param, path, origins));
                return kept[key] = at;
            }
            int KeepPlace(int param, int[] path)
            {
                if (param < 0) return Keep(Kind.Deep, param, DeepBelow, Array.Empty<int>());
                int[] coarse = Coarsened(path);
                return Keep(KindOf(coarse), param, coarse, Array.Empty<int>());
            }
            List<int> all = new();
            foreach (var o in Objects) if (o.Kind == Kind.Made) all.AddRange(o.Origins);
            all.Sort();
            int[] origins = all.Distinct().ToArray();
            for (int k = 0; k < Objects.Count; k++)
            {
                var o = Objects[k];
                // A parameter's own object stays a place; what is below it is
                // its deep place, or the objects of a guard's class below it
                // (Coarsened).
                map[k] = o.Kind switch
                {
                    Kind.Unknown => 0,
                    Kind.Place or Kind.Deep => KeepPlace(o.Param, o.Path),
                    _ => Keep(Kind.Made, -1, Array.Empty<int>(), origins),
                };
            }
            // Merged objects are pointed into anywhere: their parts' offsets differ.
            foreach (var c in Cells) b.Cells.Add((map[c.From], Any, map[c.To], b.Objects[map[c.To]].Kind == Kind.Place ? c.ToOffset : Any));
            foreach (var r in Result) b.Result.Add((map[r.To], b.Objects[map[r.To]].Kind == Kind.Place ? r.ToOffset : Any));
            b.Cells.Sort(); b.Result.Sort();
            Dedupe(b.Cells); Dedupe(b.Result);
            b.MadeCoarse = true;
            return b.Cells.Count > MostCells ? b.Everything() : b;
        }

        /// <summary>
        /// THE SHAPE PAST EVEN THE COARSE ONE: every argument and all it
        /// reaches one object, everything made another, and the unknown
        /// object -- what a coarse summary dense with cells, each object to
        /// every other, came to anyway, at one closure a call. Each holds what
        /// one of the objects merged into it held of one merged into the
        /// other: a merge, so no less than the summary said. It held every
        /// pair, and the unknown object every argument wherever a cell
        /// touched it at all: a ToString that read a static, or an argument
        /// handed a literal, leaked every argument of every call of it --
        /// and, through a wide call's stand-in, of every override's call.
        /// </summary>
        public Summary Everything()
        {
            Summary e = new();
            bool places = false;
            List<int> origins = new();
            foreach (var o in Objects) { if (o.Kind is Kind.Place or Kind.Deep) places = true; if (o.Kind == Kind.Made) origins.AddRange(o.Origins); }
            origins.Sort();
            int all = -1, blob = -1;
            if (places) { all = e.Objects.Count; e.Objects.Add((Kind.Deep, -1, Array.Empty<int>(), Array.Empty<int>())); }
            if (origins.Count > 0) { blob = e.Objects.Count; e.Objects.Add((Kind.Made, -1, Array.Empty<int>(), origins.Distinct().ToArray())); }
            int Merged(int k) => Objects[k].Kind switch { Kind.Unknown => 0, Kind.Made => blob, _ => all };
            foreach (var c in Cells)
            {
                int from = Merged(c.From), to = Merged(c.To);
                if (from >= 0 && to >= 0 && !(from == 0 && to == 0)) e.Cells.Add((from, Any, to, Any));
            }
            foreach (var r in Result) if (Merged(r.To) is int to and >= 0) e.Result.Add((to, Any));
            e.Cells.Sort(); e.Result.Sort();
            Dedupe(e.Cells); Dedupe(e.Result);
            e.MadeCoarse = true;
            return e;
        }

        /// <summary>
        /// PAST THE BOUNDS, FEWER OBJECTS: the made objects past MostFresh are
        /// one, and a place past MostPlaces is its parameter's deep place;
        /// past MostCells, the coarse summary. Merging two objects is
        /// everything either was, and every cell of both: only ever more
        /// outlives.
        /// </summary>
        public Summary Bounded()
        {
            int made = 0, placed = 0;
            foreach (var o in Objects) { if (o.Kind == Kind.Made) made++; else if (o.Kind == Kind.Place) placed++; }
            if (made <= MostFresh && placed <= MostPlaces && Cells.Count <= MostCells) return this;
            if (Objects.Any(o => o.Kind is Kind.Place or Kind.Deep && o.Path.Any(IsGuard)))
            {
                Summary loose = Loosened();
                if (loose.Objects.Count < Objects.Count) return loose.Bounded();
            }
            if (Cells.Count <= MostCells && Objects.Count <= 512) return Fewer(MostFresh, placed);
            // PAST THE CELLS, FIRST EVERY OBJECT MADE ONE: what a list's
            // grown arrays and a node's instructions held of each place, each
            // of them apart, was most of an emitter's cells; its places, and
            // the guards on them, are kept.
            Summary one = Fewer(0, placed);
            if (one.Cells.Count <= MostCells && one.Objects.Count <= 512) return one;
            // Then every parameter of no guard all one deep place below it:
            // what is kept apart is only what tells classes apart.
            one = Fewer(0, placed, unguarded: true);
            return one.Cells.Count <= MostCells && one.Objects.Count <= 512 ? one : Coarse();
        }

        /// <summary>
        /// PAST THE BOUNDS, GUARDS ONLY WHERE WHAT LEAKS IS: a guarded place
        /// the unknown object does not reach is the place unguarded -- what
        /// is written into it written into an object of any class there,
        /// what is read from it read from one. A place leaked keeps its
        /// guard, which keeps a tree from leaking with a name in it, but not
        /// where the class was: below a field of its parameter, any object
        /// of the class anywhere below it, and what the path takes from
        /// there. (Each node's Bind writing its type, guarded once for each
        /// class, was four places at each place a node may be; and each
        /// place a node may be leaked the name of each class of node.)
        /// </summary>
        private Summary Loosened()
        {
            HashSet<int> leaked = new() { 0 };
            Stack<int> next = new();
            next.Push(0);
            while (next.TryPop(out int o))
                foreach (var c in Cells) if (c.From == o && leaked.Add(c.To)) next.Push(c.To);
            Summary b = new();
            int[] map = new int[Objects.Count];
            Dictionary<(Kind, int, string), int> places = new();
            for (int k = 0; k < Objects.Count; k++)
            {
                var o = Objects[k];
                if (k == 0) { map[k] = 0; continue; }
                if (o.Kind is Kind.Place or Kind.Deep && o.Param >= 0)
                {
                    int[] path = leaked.Contains(k) ? Anywhere(o.Path) : Stripped(o.Path);
                    Kind kind = KindOf(path);
                    var key = (kind, o.Param, string.Join(",", path));
                    if (!places.TryGetValue(key, out int at)) { at = b.Objects.Count; b.Objects.Add((kind, o.Param, path, o.Origins)); places[key] = at; }
                    map[k] = at;
                    continue;
                }
                map[k] = b.Objects.Count;
                b.Objects.Add(o);
            }
            foreach (var c in Cells) b.Cells.Add((map[c.From], c.Offset, map[c.To], c.ToOffset));
            foreach (var r in Result) b.Result.Add((map[r.To], r.ToOffset));
            b.Cells.Sort(); b.Result.Sort();
            Dedupe(b.Cells); Dedupe(b.Result);
            return b;
        }

        private static int[] Anywhere(int[] path)
        {
            int g = Array.FindIndex(path, IsGuard);
            if (g <= 0 || Fields(path[..g]) == 0 && Array.IndexOf(path, DeepStep, 0, g) < 0) return path;
            int[] rest = path[g..];
            return Array.IndexOf(rest, DeepStep) >= 0 && Array.IndexOf(rest, DeepStep) != Array.LastIndexOf(rest, DeepStep) ? path : [DeepStep, .. rest];
        }

        // The made objects past `keep` one; a place past MostPlaces its parameter's deep place.
        private Summary Fewer(int keep, int placed, bool unguarded = false)
        {
            Summary b = new();
            int[] map = new int[Objects.Count];
            int blob = -1;
            // A place below its parameter made coarse (Coarsened): the deep
            // place, or the objects of its guard's class below.
            Dictionary<(int, string), int> deep = new();
            int Deep(int param, int[] path)
            {
                int[] coarse = Coarsened(path);
                var key = (param, string.Join(",", coarse));
                if (deep.TryGetValue(key, out int d)) return d;
                d = b.Objects.Count;
                b.Objects.Add((KindOf(coarse), param, coarse, Array.Empty<int>()));
                return deep[key] = d;
            }
            // PAST MostPlaces, A PARAMETER AT A TIME: its places below a
            // field of a field are its deep place, until the rest are few
            // enough -- those of no guard first, which lose no class kept
            // apart, the most places first; then, before a guarded one's,
            // every place below a field of theirs; then the guarded ones'.
            // (All at once, a dictionary's insides made a tree's guarded
            // places one deep place, the whole tree.)
            static bool Further(int[] path) => Fields(path) >= PlaceDepth || Array.IndexOf(path, DeepStep) >= 0;
            static bool Below(int[] path) => Fields(path) > 0 || Array.IndexOf(path, DeepStep) >= 0;
            HashSet<int> collapsed = new(), whole = new(), guarded = new();
            Dictionary<int, int> further = new(), below = new();
            foreach (var o in Objects)
            {
                if (o.Kind is not (Kind.Place or Kind.Deep) || o.Param < 0) continue;
                if (o.Path.Any(IsGuard)) guarded.Add(o.Param);
                if (o.Kind == Kind.Place && Further(o.Path)) further[o.Param] = further.GetValueOrDefault(o.Param) + 1;
                if (o.Kind == Kind.Place && Below(o.Path)) below[o.Param] = below.GetValueOrDefault(o.Param) + 1;
            }
            int left = placed;
            foreach (var (param, count) in further.Where(x => !guarded.Contains(x.Key)).OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            {
                if (left <= MostPlaces) break;
                collapsed.Add(param);
                left -= count;
            }
            foreach (var (param, count) in below.Where(x => !guarded.Contains(x.Key)).OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            {
                if (!unguarded && (left <= MostPlaces || guarded.Count == 0)) break;
                whole.Add(param);
                left -= count - further.GetValueOrDefault(param);
            }
            foreach (var (param, count) in further.Where(x => guarded.Contains(x.Key)).OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            {
                if (left <= MostPlaces) break;
                collapsed.Add(param);
                left -= count;
            }
            List<int> blobOrigins = new();
            int keptMade = 0;
            for (int k = 0; k < Objects.Count; k++)
            {
                var o = Objects[k];
                if (o.Kind == Kind.Unknown) { map[k] = 0; continue; }
                if (o.Kind == Kind.Made && ++keptMade > keep - 1)
                {
                    if (blob < 0) { blob = b.Objects.Count; b.Objects.Add((Kind.Made, -1, Array.Empty<int>(), Array.Empty<int>())); }
                    blobOrigins.AddRange(o.Origins);
                    map[k] = blob;
                    continue;
                }
                if (o.Kind is Kind.Place or Kind.Deep && whole.Contains(o.Param) && (Fields(o.Path) > 0 || Array.IndexOf(o.Path, DeepStep) >= 0)) { map[k] = Deep(o.Param, o.Path); continue; }
                if (o.Kind == Kind.Place && collapsed.Contains(o.Param) && Further(o.Path)) { map[k] = Deep(o.Param, o.Path); continue; }
                if (o.Kind == Kind.Deep && collapsed.Contains(o.Param) && Fields(o.Path) > 1) { map[k] = Deep(o.Param, o.Path); continue; }
                map[k] = b.Objects.Count;
                b.Objects.Add(o);
            }
            if (blob >= 0)
            {
                blobOrigins.Sort();
                b.Objects[blob] = (Kind.Made, -1, Array.Empty<int>(), blobOrigins.Distinct().ToArray());
            }
            // A merged object's cells are at any offset: its parts' fields differ.
            foreach (var c in Cells)
            {
                int from = map[c.From], to = map[c.To];
                bool mergedFrom = from == blob || b.Objects[from].Kind == Kind.Deep;
                bool mergedTo = to == blob || b.Objects[to].Kind == Kind.Deep;
                b.Cells.Add((from, mergedFrom ? Any : c.Offset, to, mergedTo ? Any : c.ToOffset));
            }
            foreach (var r in Result) b.Result.Add((map[r.To], map[r.To] == blob || b.Objects[map[r.To]].Kind == Kind.Deep ? Any : r.ToOffset));
            b.Cells.Sort(); b.Result.Sort();
            Dedupe(b.Cells); Dedupe(b.Result);
            return b;
        }
    }

    // ---- one function's solve, or one cycle's -----------------------------

    // A copy's shift that is an index scaled into an address: anywhere in
    // each object, and never the unknown object (RegionSolver.AddShifted).
    private const int IndexShift = int.MinValue + 1;
    private const int AnyShift = int.MinValue;

    /// <summary>
    /// The nodes, objects and cells of one function, or of every member of a
    /// cycle, solved: Andersen's inclusion with a worklist, over locations
    /// numbered as they are named. A cell holds what was written into it;
    /// what a place holds of the place one field on, and the unknown object
    /// of itself, a load adds as it reads.
    /// </summary>
    /// <summary>
    /// A NODE'S LOCATIONS, in the order they came -- the order HashSet&lt;int&gt;
    /// walked them in, nothing ever being removed, which every answer and
    /// every edge made later follows -- without its entry per member, chain,
    /// or copy to walk it. Up to Small members a scan of the list; past it,
    /// an open-addressed table of each location plus one beside the list.
    /// </summary>
    private sealed class LocSet
    {
        private const int Small = 8;
        public int[] Items = new int[4];
        public int Count;
        private int[]? _table;
        private int _shift;

        public bool Add(int loc)
        {
            if (_table is null)
            {
                int[] items = Items;
                for (int i = 0; i < Count; i++) if (items[i] == loc) return false;
                Append(loc);
                if (Count > Small) Rehash(5);
                return true;
            }
            int[] table = _table;
            int mask = table.Length - 1;
            int at = Slot(loc, _shift);
            while (true)
            {
                int held = table[at];
                if (held == 0) break;
                if (held == loc + 1) return false;
                at = (at + 1) & mask;
            }
            table[at] = loc + 1;
            Append(loc);
            if (Count * 2 > table.Length) Rehash(32 - _shift + 1);
            return true;
        }

        private static int Slot(int loc, int shift) => (int)(((uint)loc * 0x9E3779B1u) >> shift);

        private void Append(int loc)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
            Items[Count++] = loc;
        }

        private void Rehash(int bits)
        {
            int[] table = new int[1 << bits];
            int mask = table.Length - 1;
            _shift = 32 - bits;
            for (int i = 0; i < Count; i++)
            {
                int at = Slot(Items[i], _shift);
                while (table[at] != 0) at = (at + 1) & mask;
                table[at] = Items[i] + 1;
            }
            _table = table;
        }

        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator
        {
            private readonly LocSet _set;
            private int _at;
            public Enumerator(LocSet set) { _set = set; _at = -1; }
            public bool MoveNext() => ++_at < _set.Count;
            public int Current => _set.Items[_at];
        }
    }

    private sealed class Graph
    {
        private readonly RegionEscape _owner;
        private readonly int[] _members;
        private readonly HashSet<int> _memberSet;
        private readonly Dictionary<int, int> _memberOf = new();
        private readonly int[] _base;

        // Objects: what each is, its origins, its cells by offset.
        private readonly List<Kind> _kind = new();
        private readonly List<int> _param = new();
        private readonly List<int[]> _path = new();
        private readonly List<int[]> _origins = new();
        private readonly List<Dictionary<int, int>> _cells = new();
        private readonly Dictionary<(int, string), int> _places = new();
        private readonly Dictionary<(int, int), int> _siteObjects = new(), _slotObjects = new();
        // Per object: the nodes a load at any offset of it reads into.
        private readonly List<HashSet<int>?> _allReaders = new();
        // Per object: what may be in it at any offset without being written
        // into it here (Aliased) -- what is written into places, the unknown
        // object -- read by every load of it, and never followed for what it
        // reaches or summarised: all of it is outside already.

        // Locations: an object and an offset (Any).
        private readonly Dictionary<long, int> _locations = new();
        private readonly List<int> _locObject = new(), _locOffset = new();
        // Per location, what its object is (LocClass), so adding one to a
        // node reads one byte rather than the object and then its kind.
        private readonly List<byte> _locClass = new();
        private const byte MadeClass = 1, PlaceClass = 2, UnknownClass = 4;

        // Nodes: the members' own, then those calls and cells add.
        private readonly List<LocSet?> _pts = new();
        private readonly List<List<(int To, int Shift)>?> _copies = new();
        private readonly List<List<(int Dest, int Offset)>?> _loads = new();
        private readonly List<List<(int Value, int Offset)>?> _stores = new();
        private readonly List<List<int>?> _readsAll = new();
        // What each node has gained since it was last carried on, in the
        // order it came: a buffer taken from _spareDeltas and given back once
        // carried, its length beside it. A new list a node a wave, grown as it
        // filled, was a tenth of the solve in copying and the collector.
        private readonly List<int[]?> _deltaBuf = new();
        private readonly List<int> _deltaLen = new();
        private readonly Stack<int[]> _spareDeltas = new();
        // THE NODES WITH SOMETHING TO CARRY, in wave order: by each node's
        // place in the copy graph as it last stood (Collapse), sources first,
        // so a node is carried once after what feeds it rather than once for
        // each of its feeders. A node made since is carried after those.
        private readonly PriorityQueue<int, int> _work = new();
        private readonly List<int> _wave = new();
        // NODES ON ONE CYCLE OF COPIES ARE ONE NODE (Collapse): each node's
        // representative, by union-find. What a node holds, and its edges,
        // are its representative's; every edge and every addition is made to
        // the representative.
        private readonly List<int> _parent = new();
        private long _copyEdges, _edgesAtCollapse;
        // Each copy edge once (CopyEdge): a second of the same carries
        // nothing the first has not, wherever it falls in the list.
        private readonly HashSet<long> _plainCopies = new();
        private readonly HashSet<(int, int, int)> _shiftedCopies = new();
        // A guard's filter: what of a node's locations a guard lets through.
        private readonly List<List<(int To, int Guard)>?> _filters = new();
        // The virtual calls a node is the receiver of (Received).
        private readonly List<List<VCall>?> _vcalls = new();

        // PAST THIS MUCH WORK A FUNCTION, OR A CYCLE, IS NOT FOLLOWED: its
        // summary is the unknown call's, and everything made beneath it is
        // everyone's. A cycle's bound grows with it.
        private readonly long _mostCarried;
        private readonly int _mostNodes;
        private long _carried;
        private readonly bool _big;
        public bool Overflowed;

        public Graph(RegionEscape owner, int[] members)
        {
            _owner = owner; _members = members; _memberSet = new(members);
            _base = new int[members.Length];
            long nodes = 0;
            foreach (int f in members) nodes += owner._functions[f].Nodes;
            _mostCarried = 150_000 + 20 * nodes;
            _mostNodes = (int)Math.Min(int.MaxValue, 400_000 + 30 * nodes);
            _big = members.Length > 1000;
            NewObject(Kind.Unknown, -1, Array.Empty<int>(), Array.Empty<int>());     // 0: the unknown object
            for (int m = 0; m < members.Length; m++)
            {
                _memberOf[members[m]] = m;
                _base[m] = _pts.Count;
                RegionFunction f = owner._functions[members[m]];
                for (int n = 0; n < f.Nodes; n++) NewNode();
            }
        }

        private RegionFunction Function(int m) => _owner._functions[_members[m]];
        private int Node(int m, int n) => _base[m] + n;
        private int Ret(int m) => _base[m] + Function(m).Parameters;

        private int NewObject(Kind kind, int param, int[] path, int[] origins)
        {
            _kind.Add(kind); _param.Add(param); _path.Add(path); _origins.Add(origins); _cells.Add(new()); _allReaders.Add(null);
            return _kind.Count - 1;
        }

        private int NewNode()
        {
            _pts.Add(null); _copies.Add(null); _loads.Add(null); _stores.Add(null); _readsAll.Add(null); _deltaBuf.Add(null); _deltaLen.Add(0);
            _parent.Add(_pts.Count - 1); _wave.Add(int.MaxValue);
            _filters.Add(null); _vcalls.Add(null);
            _madeHeld.Add(0); _nodeFlags.Add(0);
            if (_pts.Count > _mostNodes) Overflowed = true;
            return _pts.Count - 1;
        }

        private int Location(int o, int offset)
        {
            if (o == 0 || offset < 0 || offset > FarthestField) offset = Any;
            long key = ((long)o << 16) | (uint)(offset & 0xFFFF);
            if (_locations.TryGetValue(key, out int known)) return known;
            int made = _locObject.Count;
            _locations[key] = made;
            _locObject.Add(o); _locOffset.Add(offset);
            _locClass.Add(o == 0 ? UnknownClass : _kind[o] switch { Kind.Made => MadeClass, Kind.Place or Kind.Deep => PlaceClass, _ => (byte)0 });
            return made;
        }

        // A CONSTANT'S ADDRESS (RegionConstants), one object for every
        // constant: made of no site, never written, and what is read from it
        // is a constant again. It holds nothing, as the region engines need
        // it to; but it is SOMETHING, so a virtual call whose receiver may be
        // a literal or a descriptor still runs, any of its targets on it
        // (Received: an object of no known site) -- an empty receiver is a
        // call that never runs, and the call's other arguments were never
        // handed over. A summary keeps it as a made object of no origins.
        private int _constantLoc = -1;
        private int Constant => _constantLoc >= 0 ? _constantLoc
            : _constantLoc = Location(NewObject(Kind.Made, -1, Array.Empty<int>(), Array.Empty<int>()), 0);
        private bool IsConstant(int o) => _constantLoc >= 0 && _locObject[_constantLoc] == o;

        // The unknown object's location, named once and then remembered.
        private int _unknownLoc = -1;
        private int Unknown => _unknownLoc >= 0 ? _unknownLoc : _unknownLoc = Location(0, Any);

        /// <summary>A location moved along a copy, or -1 when the copy drops it.</summary>
        private int Shift(int loc, int shift)
        {
            if (shift == 0) return loc;
            int o = _locObject[loc], at = _locOffset[loc];
            if (o == 0) return shift == IndexShift ? -1 : loc;
            if (shift is AnyShift or IndexShift || at == Any || at != 0 || shift > FarthestField || shift < -FarthestField) return Location(o, Any);
            return Location(o, shift);
        }

        /// <summary>The place a parameter (member m's k: m times ParamStride plus k) reaches by `path`, or its deep place past PlaceDepth fields.</summary>
        // Past PlaceDepth fields, the deep place of the first PlaceDepth: what
        // is below a field of a field, not everything below the parameter --
        // a string kept in a node's field and leaked, all of the node's
        // subtree with it.
        // (The path comes kept so already: WithField, WithGuard.)
        private int Place(int param, int[] path)
        {
            string key = string.Join(",", path);
            if (_places.TryGetValue((param, key), out int known)) return known;
            int o = NewObject(KindOf(path), param, path, Array.Empty<int>());
            _places[(param, key)] = o;
            return o;
        }

        // What a load of an object at `offset` finds there besides what was
        // written: the place one field on, the deep place itself, the unknown
        // object itself.
        private int Implicit(int o, int offset)
        {
            switch (_kind[o])
            {
                case Kind.Unknown: return Unknown;
                case Kind.Place or Kind.Deep: return Location(Place(_param[o], WithField(_path[o], offset)), 0);
                default: return -1;
            }
        }

        /// <summary>The node of an object's cell at an offset, made on first use.</summary>
        private int Cell(int o, int offset)
        {
            if (o == 0) offset = Any;
            if (_cells[o].TryGetValue(offset, out int node)) return node;
            node = NewNode();
            _cells[o][offset] = node;
            if (o == 0) _unknownCell = node;
            // Read by every load of the whole object. (A write at any offset
            // is read by a load at a fixed one as it loads: Loaded.)
            if (_allReaders[o] is { } readers) foreach (int r in readers) CopyEdge(node, r, 0);
            return node;
        }

        // PAST THIS MANY LOCATIONS A NODE HOLDS THE UNKNOWN OBJECT, and what it
        // held goes there: they escape (RegionSolver's MostHeld). The node
        // the unknown object's own writes gather in is never cut short:
        // nothing is read out of it.
        private const int MostHeld = 256;
        private int _saturatedCount;
        private int _unknownCell = -1;
        // What Aliased feeds loads (what is written into places): all of it is
        // outside already, and cut short it would put every one of those
        // writes where nobody follows.


        // PLACES ARE NOT WHAT FILLS A NODE: there are only so many, and
        // sent where nobody follows they are arguments escaping at every
        // call. A node counts the objects made it holds against MostHeld;
        // past it, those go to the unknown object and the places stay, up to
        // a bound of their own.
        private const int MostPlacesHeld = 512;
        // Per node: the made objects it holds, and its flags (Saturated,
        // PlacesDumped, NeverSaturated), where a hash lookup a location was
        // most of what adding one cost.
        private readonly List<int> _madeHeld = new();
        private readonly List<byte> _nodeFlags = new();
        private const byte Saturated = 1, PlacesDumped = 2, NeverSaturated = 4;

        private void Add(int node, int loc)
        {
            if (loc < 0) return;
            node = Find(node);
            byte kind = _locClass[loc];
            byte flags = _nodeFlags[node];
            if ((flags & Saturated) != 0 && !((kind & PlaceClass) != 0 && (flags & PlacesDumped) == 0))
            {
                // Into the unknown object's cell, which is never cut short.
                if ((kind & UnknownClass) == 0) Add(UnknownCell(), loc);
                return;
            }
            LocSet pts = _pts[node] ??= new();
            if (!pts.Add(loc)) return;
            Delta(node, loc);
            if (node == _unknownCell || (flags & NeverSaturated) != 0) return;
            int made = (kind & MadeClass) != 0 ? ++CollectionsMarshal.AsSpan(_madeHeld)[node] : _madeHeld[node];
            if ((flags & Saturated) == 0 && made > MostHeld)
            {
                _nodeFlags[node] |= Saturated; _saturatedCount++;
                int sink = UnknownCell();
                // What it held when it filled: nothing below adds to it.
                for (int i = 0, n = pts.Count; i < n; i++) { int held = pts.Items[i]; if ((_locClass[held] & MadeClass) != 0) Add(sink, held); }
                if (pts.Add(Unknown)) Delta(node, Unknown);
            }
            if (pts.Count - made > MostPlacesHeld && (_nodeFlags[node] & PlacesDumped) == 0)
            {
                if ((_nodeFlags[node] & Saturated) == 0) _saturatedCount++;
                _nodeFlags[node] |= Saturated | PlacesDumped;
                int sink = UnknownCell();
                for (int i = 0, n = pts.Count; i < n; i++) { int held = pts.Items[i]; if ((_locClass[held] & UnknownClass) == 0) Add(sink, held); }
                if (pts.Add(Unknown)) Delta(node, Unknown);
            }
        }

        private void Delta(int node, int loc)
        {
            int[]? buffer = _deltaBuf[node];
            int length = _deltaLen[node];
            if (buffer is null)
            {
                buffer = _spareDeltas.TryPop(out int[]? spare) ? spare : new int[16];
                _deltaBuf[node] = buffer;
            }
            else if (length == buffer.Length)
            {
                int[] bigger = new int[length * 2];
                Array.Copy(buffer, bigger, length);
                _spareDeltas.Push(buffer);
                _deltaBuf[node] = buffer = bigger;
            }
            buffer[length] = loc;
            _deltaLen[node] = length + 1;
            if (length == 0) _work.Enqueue(node, _wave[node]);
        }

        private int UnknownCell()
        {
            if (_unknownCell < 0) _unknownCell = Cell(0, Any);
            return _unknownCell;
        }

        // What a node holds now is carried along a new edge at once (each
        // walk over what it held when the edge was made: a set only grows,
        // at its end). An edge already there is not listed again -- carrying
        // a node's gains along it twice adds nothing the first did not -- but
        // what the node holds is still carried now, as it was when every
        // edge was listed: what it has gained and not yet carried arrives
        // where it goes in the same order.
        private void CopyEdge(int from, int to, int shift)
        {
            from = Find(from); to = Find(to);
            if (from == to && shift == 0) return;
            bool fresh = shift == 0 ? _plainCopies.Add(((long)from << 32) | (uint)to) : _shiftedCopies.Add((from, to, shift));
            if (fresh) { (_copies[from] ??= new()).Add((to, shift)); _copyEdges++; }
            if (_pts[from] is not { } pts) return;
            if (shift == 0) for (int i = 0, n = pts.Count; i < n; i++) Add(to, pts.Items[i]);
            else for (int i = 0, n = pts.Count; i < n; i++) Add(to, Shift(pts.Items[i], shift));
        }

        private void LoadEdge(int dest, int address, int offset)
        {
            address = Find(address);
            (_loads[address] ??= new()).Add((dest, offset));
            if (_pts[address] is { } pts) for (int i = 0, n = pts.Count; i < n; i++) Loaded(pts.Items[i], dest, offset);
        }

        private void StoreEdge(int address, int offset, int value)
        {
            address = Find(address);
            (_stores[address] ??= new()).Add((value, offset));
            if (_pts[address] is { } pts) for (int i = 0, n = pts.Count; i < n; i++) Stored(pts.Items[i], value, offset);
        }

        // Every cell of what `address` points to, into `dest`.
        private void LoadAllEdge(int dest, int address)
        {
            address = Find(address);
            (_readsAll[address] ??= new()).Add(dest);
            if (_pts[address] is { } pts) for (int i = 0, n = pts.Count; i < n; i++) LoadedAll(pts.Items[i], dest);
        }

        /// <summary>A node's representative (union-find, halving the path as it goes).</summary>
        private int Find(int node)
        {
            Span<int> parent = CollectionsMarshal.AsSpan(_parent);
            while (parent[node] != node)
            {
                parent[node] = parent[parent[node]];
                node = parent[node];
            }
            return node;
        }

        /// <summary>What a node holds: its representative's set.</summary>
        private LocSet? Pts(int node) => _pts[Find(node)];

        // A pair of nodes (or an object and a word) as one key. Kept by the
        // nodes as they were named, never their representatives: a load's
        // first reading of a cell does more than add the edge (Loaded), and
        // two cells one node are still two objects' cells.
        private static long Pair(int a, int b) => ((long)a << 32) | (uint)b;

        private void FilterEdge(int from, int to, int guard)
        {
            from = Find(from);
            (_filters[from] ??= new()).Add((to, guard));
            if (_pts[from] is { } pts) for (int i = 0, n = pts.Count; i < n; i++) Add(to, Filtered(pts.Items[i], guard));
        }
        /// <summary>
        /// A location through a guard: an object made of no site the guard
        /// lets through is dropped; a place is the place guarded, the caller's
        /// to filter in turn; anything else -- the unknown object, an address
        /// into an object, an object of no known site -- goes through as it is.
        /// </summary>
        private int Filtered(int loc, int guard)
        {
            int o = _locObject[loc];
            if (_kind[o] == Kind.Made && _locOffset[loc] is 0 or Any) return Passes(o, guard) ? loc : -1;
            if (_locOffset[loc] != 0) return loc;
            switch (_kind[o])
            {
                case Kind.Place or Kind.Deep: return Location(Place(_param[o], _owner.WithGuard(_path[o], guard)), 0);
                default: return loc;
            }
        }

        private readonly Dictionary<(int, int), bool> _passes = new();

        private bool Passes(int o, int guard)
        {
            if (_passes.TryGetValue((o, guard), out bool known)) return known;
            int[] sites = SitesOfObject(o);
            bool passes = sites.Length == 0;
            foreach (int site in sites) if (_owner.SitePasses(site, guard)) { passes = true; break; }
            return _passes[(o, guard)] = passes;
        }

        private readonly Dictionary<int, int[]> _objectSites = new();

        private int[] SitesOfObject(int o)
        {
            if (_objectSites.TryGetValue(o, out int[]? known)) return known;
            return _objectSites[o] = _origins[o].Length == 0 ? Array.Empty<int>() : _owner.SitesOf(_origins[o]);
        }

        private static int Offset(int at, int offset) => at == Any || offset == Any ? Any : at + offset <= FarthestField ? at + offset : Any;

        private readonly HashSet<long> _loadedFrom = new();

        private void Loaded(int loc, int dest, int offset)
        {
            int o = _locObject[loc];
            if (IsConstant(o)) { Add(dest, Constant); return; }
            int at = Offset(_locOffset[loc], offset);
            if (at == Any) { LoadedAll(loc, dest); return; }
            // A word never read as a reference holds a number: the unknown object at most.
            if (NoReference(o, at)) { Add(dest, Unknown); return; }
            int cell = Cell(o, at);
            if (!_loadedFrom.Add(Pair(cell, dest))) return;
            CopyEdge(cell, dest, 0);
            // What was written at an offset nobody knew may be here too.
            int any = Cell(o, Any);
            if (_loadedFrom.Add(Pair(any, dest))) CopyEdge(any, dest, 0);
            Aliasing(o, dest);
            Add(dest, Implicit(o, at));
        }

        private void LoadedAll(int loc, int dest)
        {
            int o = _locObject[loc];
            if (o == 0) { Add(dest, Unknown); return; }
            if (IsConstant(o)) { Add(dest, Constant); return; }
            if (NoReference(o, Any)) { Add(dest, Unknown); return; }
            HashSet<int> readers = _allReaders[o] ??= new();
            if (!readers.Add(dest)) return;
            Cell(o, Any);
            foreach (int node in _cells[o].Values.ToArray()) CopyEdge(node, dest, 0);
            Aliasing(o, dest);
            Add(dest, Implicit(o, Any));
        }

        // WHAT MAY BE IN AN OBJECT WITHOUT A WRITE HERE (Aliased): for one a
        // place reaches, what is written into places -- one node for all of
        // them, read by each load of such an object -- and for one the
        // unknown object reaches, the unknown object. Each object's loads
        // are kept, for an object that joins later. (A node of its own for
        // each object, each fed all that is written into places, carried a
        // hundred million locations on the compiler's own link.)
        private readonly HashSet<int> _placedMade = new(), _unknownMade = new();
        private readonly Dictionary<int, List<int>> _loadsOf = new();
        private readonly HashSet<long> _fedFrom = new();
        private int _written = -1;

        private void Aliasing(int o, int dest)
        {
            if (_kind[o] != Kind.Made) return;
            (_loadsOf.TryGetValue(o, out List<int>? loads) ? loads : _loadsOf[o] = new()).Add(dest);
            if (_placedMade.Contains(o) && _fedFrom.Add(Pair(_written, dest))) CopyEdge(_written, dest, 0);
            if (_unknownMade.Contains(o)) Add(dest, Unknown);
        }

        private void Placed(int o)
        {
            if (!_placedMade.Add(o) || !_loadsOf.TryGetValue(o, out List<int>? loads)) return;
            foreach (int dest in loads.ToArray()) if (_fedFrom.Add(Pair(_written, dest))) CopyEdge(_written, dest, 0);
        }

        private void ReachedByUnknown(int o)
        {
            if (!_unknownMade.Add(o) || !_loadsOf.TryGetValue(o, out List<int>? loads)) return;
            foreach (int dest in loads.ToArray()) Add(dest, Unknown);
        }

        private readonly HashSet<long> _storedInto = new();

        private void Stored(int loc, int value, int offset)
        {
            int o = _locObject[loc];
            int at = Offset(_locOffset[loc], offset);
            // A number kept where no reference is, or a constant, which is
            // never written: nothing anyone reaches.
            if (NoReference(o, at) || IsConstant(o)) return;
            int cell = Cell(o, at);
            if (_storedInto.Add(Pair(value, cell))) CopyEdge(value, cell, 0);
        }

        // Per object and word: never read as a reference (every site it may be says so).
        private readonly Dictionary<long, bool> _noReference = new();

        private bool NoReference(int o, int at)
        {
            if (_kind[o] != Kind.Made || _origins[o].Length == 0 || _owner.NoReference is not { } rule) return false;
            if (_noReference.TryGetValue(Pair(o, at), out bool known)) return known;
            bool none = true;
            foreach (int r in _origins[o])
            {
                int[] sites = r < 0 ? new[] { -r - 1 } : _owner.SitesOf(new[] { r });
                if (sites.Length == 0 || sites.Any(site => !rule(site, at))) { none = false; break; }
            }
            return _noReference[Pair(o, at)] = none;
        }

        // ---- building --------------------------------------------------------

        public Graph Solved()
        {
            for (int m = 0; m < _members.Length; m++)
            {
                RegionFunction f = Function(m);
                // A parameter of a number type points to no place.
                for (int k = 0; k < f.Parameters; k++)
                    if (!f.IsNumber(k)) Add(Node(m, k), Location(Place(m * ParamStride + k, Array.Empty<int>()), 0));
                foreach (RegionConstraint c in f.Constraints)
                {
                    int a = Node(m, c.A);
                    switch (c.Kind)
                    {
                        case RegionConstraintKind.Site: Add(a, Location(SiteObject(m, c.B), 0)); break;
                        case RegionConstraintKind.Slot: Add(a, Location(SlotObject(m, c.B), 0)); break;
                        case RegionConstraintKind.Unknown: Add(a, Unknown); break;
                        // A constant's address holds nothing to follow (RegionConstants);
                        // a symbol the link did not judge is the unknown object.
                        case RegionConstraintKind.Symbol: Add(a, f.ConstantsKnown ? Constant : Unknown); break;
                        case RegionConstraintKind.Copy: CopyEdge(Node(m, c.B), a, CopyShift(c.C)); break;
                        case RegionConstraintKind.Load: LoadEdge(a, Node(m, c.B), Plain(c.C)); break;
                        case RegionConstraintKind.Store: StoreEdge(a, Plain(c.C), Node(m, c.B)); break;
                        case RegionConstraintKind.MemCopy:
                        {
                            int through = NewNode();
                            LoadAllEdge(through, Node(m, c.B));
                            StoreEdge(a, Any, through);
                            break;
                        }
                        case RegionConstraintKind.Leak: Leak(a); break;
                    }
                }
                for (int k = 0; k < f.Calls.Count && !Overflowed; k++) Call(m, k, f.Calls[k]);
                if (Overflowed) return this;
            }
            Propagate();
            if (!Overflowed) Aliased();
            return this;
        }

        private static int Plain(long offset) => offset < 0 || offset > FarthestField ? Any : (int)offset;

        private static int CopyShift(long c) =>
            RegionConstraint.IsIndex(c) ? IndexShift
            : c == RegionConstraint.Any || RegionConstraint.IsMovedBy(c) || c > FarthestField || c < -FarthestField ? AnyShift
            : (int)c;

        private int SiteObject(int m, int ordinal)
        {
            if (_siteObjects.TryGetValue((m, ordinal), out int o)) return o;
            return _siteObjects[(m, ordinal)] = NewObject(Kind.Made, -1, Array.Empty<int>(), new[] { Leaf(_owner._siteBase[_members[m]] + ordinal) });
        }

        private int SlotObject(int m, int slot)
        {
            if (_slotObjects.TryGetValue((m, slot), out int o)) return o;
            return _slotObjects[(m, slot)] = NewObject(Kind.Made, -1, Array.Empty<int>(), Array.Empty<int>());
        }

        // What a node holds goes where nobody follows: into the unknown object.
        private void Leak(int node) => CopyEdge(node, UnknownCell(), 0);

        // Per set of targets in this cycle: the nodes a call of them hands its
        // arguments to and takes its result from.
        private readonly Dictionary<int[], (int[] Args, int Ret)> _dispatch = new(TargetsComparer.Instance);

        private void Call(int m, int k, RegionCall call)
        {
            int f = _members[m];
            int[]? targets = _owner._targets[f][k];
            int[] args = new int[call.Arguments.Length];
            for (int a = 0; a < args.Length; a++) args[a] = call.Arguments[a] < 0 || _owner.NumberArgument(targets, a) ? -1 : Node(m, call.Arguments[a]);
            int dest = call.Dest < 0 ? -1 : Node(m, call.Dest);
            if (targets is null) { UnknownCall(args, dest); return; }
            if (targets.Length == 0) return;                       // no object of the type exists
            if (_owner.IsWide(targets))
            {
                Summary standIn = _owner.StandIn(targets);
                Explain(f, "(wide, a stand-in) " + call.Callee, targets, standIn);
                Apply(args, dest, standIn);
                return;
            }
            int[] inside = targets.Where(_memberSet.Contains).ToArray();
            int[] outside = inside.Length == 0 ? targets : targets.Where(t => !_memberSet.Contains(t)).ToArray();
            // A VIRTUAL CALL OF MORE THAN ONE TARGET ON A RECEIVER: each
            // location of it to the overrides that run on it (Received).
            bool watched = _owner._keys[f][k] is not null && _owner.TargetsOn is not null && targets.Length > 1 && args.Length > 0 && args[0] >= 0;
            if (inside.Length > 0)
            {
                // WITHIN THE CYCLE: into the callees' own nodes, through one
                // pair a set of targets however many calls make it.
                if (!_dispatch.TryGetValue(inside, out var through))
                {
                    int most = inside.Max(t => _owner._functions[t].Parameters);
                    int[] p = new int[most];
                    for (int j = 0; j < most; j++) p[j] = NewNode();
                    int r = NewNode();
                    foreach (int t in inside)
                    {
                        int tm = _memberOf[t];
                        for (int j = 0; j < _owner._functions[t].Parameters; j++) CopyEdge(p[j], Node(tm, j), 0);
                        CopyEdge(Ret(tm), r, 0);
                    }
                    through = (p, r);
                    _dispatch[inside] = through;
                }
                for (int j = watched ? 1 : 0; j < args.Length && j < through.Args.Length; j++) if (args[j] >= 0) CopyEdge(args[j], through.Args[j], 0);
                // A parameter this call passes nothing for: whatever its word holds.
                for (int j = args.Length; j < through.Args.Length; j++) Add(through.Args[j], Unknown);
                if (dest >= 0) CopyEdge(through.Ret, dest, 0);
            }
            if (watched)
            {
                VCall v = new() { F = f, K = k, Args = args, Dest = dest, Inside = inside, Outside = outside, Callee = call.Callee };
                int receiver = Find(args[0]);
                (_vcalls[receiver] ??= new()).Add(v);
                if (_pts[receiver] is { } pts) for (int i = 0, n = pts.Count; i < n; i++) Received(v, pts.Items[i]);
                return;
            }
            if (outside.Length > 0)
            {
                Summary applied = _owner.MergedFor(outside);
                Explain(f, call.Callee, outside, applied);
                Apply(args, dest, applied);
            }
        }

        private void UnknownCall(int[] args, int dest)
        {
            foreach (int a in args) if (a >= 0) Leak(a);
            if (dest >= 0) Add(dest, Unknown);
        }

        // A virtual call watched at its receiver: per set of targets outside
        // the cycle, the receiver node their summary was applied with.
        private sealed class VCall
        {
            public int F, K, Dest;
            public int[] Args = null!, Inside = null!, Outside = null!;
            public string? Callee;
            public readonly Dictionary<int[], int> Groups = new(TargetsComparer.Instance);
            public readonly Dictionary<int, int[]?> Classes = new();
            public bool? SplitOutside;
        }

        // The most sets of targets a call applies apart, past which a set is
        // all of them merged; and the most targets a place is guarded for,
        // one by one.
        private const int MostGroups = 8;
        private const int MostGuarded = 8;
        private const int MostGuardedCycle = 16;

        /// <summary>
        /// A LOCATION THE RECEIVER MAY BE, to the overrides that run on it.
        /// An object made at sites whose descriptors are known runs theirs,
        /// and their summaries alone are applied to it, it alone their
        /// `this`. A place runs, for each override, that override on the
        /// objects there of a class that runs it: the place guarded by it.
        /// Anything else -- the unknown object, an address into an object, a
        /// place of a call of too many targets -- may run any of them.
        /// Within the cycle, the same: a member's `this` is handed only what
        /// it may run on.
        /// </summary>
        private void Received(VCall v, int loc)
        {
            int o = _locObject[loc];
            // (A receiver is an object's start: anywhere in a made object,
            // it is that object.)
            bool whole = _locOffset[loc] == 0;
            if ((whole || _locOffset[loc] == Any) && _kind[o] == Kind.Made && ClassTargets(v, o) is { } runs)
            {
                List<int> outside = new();
                foreach (int g in runs)
                    if (_memberSet.Contains(g)) Receives(g, loc);
                    else outside.Add(g);
                if (outside.Count > 0) Add(Group(v, outside.ToArray()), loc);
                return;
            }
            if (whole && _kind[o] is Kind.Place or Kind.Deep && v.Inside.Length + v.Outside.Length <= MostGuarded)
            {
                // Apart only where it may tell: the targets outside when
                // their summaries do anything to what the receiver is or
                // reaches, the members of a small cycle. (Each place apart
                // in each of a large cycle's calls carried its solve past
                // its bound.)
                v.SplitOutside ??= v.Outside.Length > 1 && _owner.MergedFor(v.Outside).Objects.Any(x => x.Kind is Kind.Place or Kind.Deep && x.Param == 0);
                bool inside = _members.Length <= MostGuardedCycle;
                foreach (int g in v.Inside) Receives(g, inside ? Guarded(o, v, g) : loc);
                if (v.SplitOutside == true) foreach (int g in v.Outside) Add(Group(v, new[] { g }), Guarded(o, v, g));
                else if (v.Outside.Length > 0) Add(Group(v, v.Outside), loc);
                return;
            }
            if (_owner.Why is not null && _owner.Progress is { } say && _owner.WhyFunction?.Invoke(v.F) == true)
                say($"escape graphs why: in {_owner._functions[v.F].Name}: call {v.Callee} runs any of its {v.Inside.Length + v.Outside.Length} targets on {Describe(o)} +{_locOffset[loc]}"
                    + (_kind[o] == Kind.Made ? " (sites " + string.Join(",", SitesOfObject(o).Take(8)) + "; made from " + string.Join(", ", _origins[o].Take(3).Select(r => _owner.Lineage(r, 4))) + ")" : ""));
            foreach (int g in v.Inside) Receives(g, loc);
            if (v.Outside.Length > 0) Add(Group(v, v.Outside), loc);
        }

        private void Receives(int member, int loc)
        {
            if (_owner._functions[member].Parameters > 0) Add(Node(_memberOf[member], 0), loc);
        }

        private int Guarded(int place, VCall v, int target) =>
            Location(Place(_param[place], _owner.WithGuard(_path[place], _owner.Guard(v.F, v.K, target))), 0);

        /// <summary>The targets the call runs on an object, by its sites; null when any may run.</summary>
        private int[]? ClassTargets(VCall v, int o)
        {
            if (v.Classes.TryGetValue(o, out int[]? known)) return known;
            int[] sites = SitesOfObject(o);
            SortedSet<int>? runs = sites.Length == 0 ? null : new();
            foreach (int site in sites)
            {
                if (_owner.TargetsOn!(v.F, v.K, site) is not { } those) { runs = null; break; }
                runs!.UnionWith(those);
            }
            return v.Classes[o] = runs?.ToArray();
        }

        /// <summary>The receiver node of a set of targets outside the cycle, their summary applied to it on first use.</summary>
        private int Group(VCall v, int[] targets)
        {
            if (v.Groups.TryGetValue(targets, out int recv)) return recv;
            if (v.Groups.Count >= MostGroups && !TargetsComparer.Instance.Equals(targets, v.Outside)) return Group(v, v.Outside);
            recv = NewNode();
            v.Groups[targets] = recv;
            int[] args = (int[])v.Args.Clone();
            args[0] = recv;
            Summary applied = _owner.MergedFor(targets);
            Explain(v.F, v.Callee, targets, applied);
            Apply(args, v.Dest, applied);
            return recv;
        }

        // For a report: what a summary applied for a function asked about leaks.
        private void Explain(int f, string? callee, int[] targets, Summary applied)
        {
            if (_owner.Why is null || _owner.Progress is null || _owner.WhyFunction?.Invoke(f) != true) return;
            var leaks = applied.Cells.Where(c => c.From == 0).Select(c => applied.Objects[c.To]).Select(o => o.Kind + " " + o.Param + " [" + string.Join(",", o.Path.Select(Step)) + "]"
                + (o.Kind == Kind.Made && o.Origins.Length > 0 ? " (sites " + string.Join(",", _owner.SitesOf(o.Origins).Take(8)) + ")" : ""));
            if (applied.IsUnknown || leaks.Any())
                _owner.Progress($"escape graphs why: in {_owner._functions[f].Name}: call {callee} ({targets.Length} targets{(targets.Length == 1 ? " " + _owner._functions[targets[0]].Name : "")}) {(applied.IsUnknown ? "is the unknown call" : "leaks " + string.Join("; ", leaks))}");
        }

        private static string Step(int step) => step == DeepStep ? "deep" : IsGuard(step) ? "g" + GuardOf(step) : step.ToString();

        /// <summary>
        /// A CALLEE'S SUMMARY AT THIS CALL: its places the caller's own reach
        /// from the arguments by the same fields, the objects it made made
        /// here, its cells written, its result the call's.
        /// </summary>
        private void Apply(int[] args, int dest, Summary s)
        {
            _owner.Applied++;
            if (s.IsUnknown) { UnknownCall(args, dest); return; }
            int[] node = new int[s.Objects.Count];
            Dictionary<(int, int), int> chains = new();
            // A place's path from the argument: a field a load, a guard its
            // filter, the deep step everything below.
            int Chain(int at, int[] path)
            {
                foreach (int step in path)
                {
                    if (!chains.TryGetValue((at, step), out int next))
                    {
                        next = NewNode();
                        if (step == DeepStep) { LoadAllEdge(next, at); LoadAllEdge(next, next); }
                        else if (IsGuard(step)) FilterEdge(at, next, GuardOf(step));
                        else LoadEdge(next, at, step);
                        chains[(at, step)] = next;
                    }
                    at = next;
                }
                return at;
            }
            for (int k = 0; k < s.Objects.Count; k++)
            {
                var o = s.Objects[k];
                int arg = o.Param >= 0 && o.Param < args.Length ? args[o.Param] : -1;
                // A PARAMETER THE CALL PASSES NOTHING FOR is whatever its word
                // holds: the unknown object, and what is written there
                // escapes. (Nothing at all, a callee's writes through it
                // were dropped.)
                bool unpassed = o.Param >= args.Length;
                switch (o.Kind)
                {
                    case Kind.Unknown:
                        node[k] = NewNode(); Add(node[k], Unknown); break;
                    case Kind.Place when unpassed:
                    case Kind.Deep when unpassed:
                        node[k] = NewNode(); Add(node[k], Unknown); break;
                    case Kind.Place:
                        node[k] = arg < 0 ? NewNode() : Chain(arg, o.Path);     // nothing passed that could hold an address: nothing
                        break;
                    case Kind.Deep when o.Param >= 0:
                        // Everything below what the argument reaches by the
                        // deep place's fields (its path's deep step).
                        node[k] = arg < 0 ? NewNode() : Chain(arg, DeepPath(o.Path));
                        break;
                    case Kind.Deep:
                    {
                        // Past every parameter (-1): what every argument is and reaches.
                        node[k] = NewNode();
                        foreach (int a in args)
                        {
                            if (a < 0) continue;
                            CopyEdge(a, node[k], 0);
                            LoadAllEdge(node[k], a);
                        }
                        LoadAllEdge(node[k], node[k]);
                        break;
                    }
                    case Kind.Made:
                    {
                        int made = NewObject(Kind.Made, -1, Array.Empty<int>(), s.Holder >= 0 ? new[] { Ref(s.Holder, k) } : o.Origins);
                        node[k] = NewNode();
                        Add(node[k], Location(made, 0));
                        break;
                    }
                }
            }
            // One node a target moved by an offset, and one store a source
            // cell gathering every value written there: a dense summary made
            // a node and a store a cell, and a few calls of it ran a function
            // past its bound.
            Dictionary<(int, int), int> moved = new();
            int At(int obj, int offset)
            {
                if (offset == 0) return node[obj];
                if (moved.TryGetValue((obj, offset), out int known)) return known;
                int made = NewNode();
                CopyEdge(node[obj], made, offset == Any ? AnyShift : offset);
                return moved[(obj, offset)] = made;
            }
            Dictionary<(int, int), int> gathered = new();
            foreach (var c in s.Cells)
            {
                if (!gathered.TryGetValue((c.From, c.Offset), out int into))
                {
                    into = NewNode();
                    gathered[(c.From, c.Offset)] = into;
                    StoreEdge(node[c.From], c.Offset, into);
                }
                CopyEdge(At(c.To, c.ToOffset), into, 0);
            }
            if (dest >= 0) foreach (var r in s.Result) CopyEdge(At(r.To, r.ToOffset), dest, 0);
        }

        // ---- solving ---------------------------------------------------------

        private void Propagate()
        {
            if (_copyEdges > _edgesAtCollapse) Collapse();
            while (_work.TryDequeue(out int n, out _))
            {
                // Cycles closed by the edges loads and stores have added, once
                // there are enough new ones to be worth a walk of the graph.
                if (_copyEdges - _edgesAtCollapse > Math.Max(4096, _edgesAtCollapse >> 2))
                {
                    Collapse();
                    if (Overflowed) return;
                    if (_deltaBuf[n] is null) continue;
                }
                int[]? delta = _deltaBuf[n];
                int count = _deltaLen[n];
                if (delta is null || count == 0) continue;
                // Taken from the node: what it gains while this is carried is
                // a new delta, carried in its turn.
                _deltaBuf[n] = null; _deltaLen[n] = 0;
                _owner.Work += count;
                if ((_carried += count) > _mostCarried || Overflowed) { Overflowed = true; return; }
                if (_big && _owner.Progress is not null && (_carried & ~0xFFFFFL) != ((_carried - count) & ~0xFFFFFL))
                    _owner.Progress($"escape graphs:   cycle solve: {Describe()}, heap {GC.GetTotalMemory(false) >> 20} MB");
                // Edges are read afresh each time: one made while these are
                // carried is carried along too, as a list walked by index was.
                if (_copies[n] is { } copies)
                    for (int e = 0; e < copies.Count; e++)
                    {
                        (int to, int shift) = copies[e];
                        if (shift == 0) for (int k = 0; k < count; k++) Add(to, delta[k]);
                        else for (int k = 0; k < count; k++) Add(to, Shift(delta[k], shift));
                    }
                if (_loads[n] is { } loads)
                    for (int e = 0; e < loads.Count; e++)
                    {
                        (int dest, int offset) = loads[e];
                        for (int k = 0; k < count; k++) Loaded(delta[k], dest, offset);
                    }
                if (_stores[n] is { } stores)
                    for (int e = 0; e < stores.Count; e++)
                    {
                        (int value, int offset) = stores[e];
                        for (int k = 0; k < count; k++) Stored(delta[k], value, offset);
                    }
                if (_readsAll[n] is { } all)
                    for (int e = 0; e < all.Count; e++)
                    {
                        int dest = all[e];
                        for (int k = 0; k < count; k++) LoadedAll(delta[k], dest);
                    }
                // Through a guard, and to a virtual call's overrides by what
                // the receiver may be (Received).
                if (_filters[n] is { } filters)
                    for (int e = 0; e < filters.Count; e++)
                    {
                        (int to, int guard) = filters[e];
                        for (int k = 0; k < count; k++) Add(to, Filtered(delta[k], guard));
                    }
                if (_vcalls[n] is { } vcalls)
                    for (int e = 0; e < vcalls.Count; e++)
                    {
                        VCall v = vcalls[e];
                        for (int k = 0; k < count; k++) Received(v, delta[k]);
                    }
                _spareDeltas.Push(delta);
            }
        }

        /// <summary>
        /// CYCLES OF COPIES COLLAPSED, and the wave order found. Nodes that copy
        /// into each other round a cycle (shift 0) hold the same at the end,
        /// so they are made one: Tarjan's components over the copy edges as
        /// they stand, each merged into the member holding most. What would
        /// be carried round the cycle once a member is carried once. Not the
        /// unknown object's cell nor a node never cut short, whose own rules
        /// (Add) a merge would lend to others or take from them. A merged
        /// node holds the union of what its members would: at most it fills
        /// past MostHeld where one alone would not, and sends what it holds
        /// to the unknown object -- more outlives, never less.
        /// The components come out sinks first; numbered back from there,
        /// each node's wave is its place, sources first.
        /// </summary>
        private void Collapse()
        {
            _edgesAtCollapse = _copyEdges;
            int count = _pts.Count;
            int[] index = new int[count], low = new int[count], finished = new int[count];
            Array.Fill(index, -1);
            bool[] onStack = new bool[count];
            int[] stack = new int[count], frameNode = new int[count], frameEdge = new int[count];
            int sp = 0, next = 0, done = 0;
            List<int[]> cycles = new();
            List<int> members = new();
            bool Collapsible(int n) => _parent[n] == n && n != _unknownCell && (_nodeFlags[n] & NeverSaturated) == 0;
            for (int root = 0; root < count; root++)
            {
                if (index[root] >= 0 || !Collapsible(root) || _copies[root] is null) continue;
                int fp = 0;
                frameNode[0] = root; frameEdge[0] = 0;
                index[root] = low[root] = next++; stack[sp++] = root; onStack[root] = true;
                while (fp >= 0)
                {
                    int v = frameNode[fp];
                    List<(int To, int Shift)>? edges = _copies[v];
                    bool deeper = false;
                    while (edges is not null && frameEdge[fp] < edges.Count)
                    {
                        (int to, int shift) = edges[frameEdge[fp]++];
                        if (shift != 0) continue;
                        int w = Find(to);
                        if (w == v || !Collapsible(w)) continue;
                        if (index[w] < 0)
                        {
                            index[w] = low[w] = next++; stack[sp++] = w; onStack[w] = true;
                            fp++; frameNode[fp] = w; frameEdge[fp] = 0;
                            deeper = true;
                            break;
                        }
                        if (onStack[w] && index[w] < low[v]) low[v] = index[w];
                    }
                    if (deeper) continue;
                    if (low[v] == index[v])
                    {
                        members.Clear();
                        int w;
                        done++;
                        do { w = stack[--sp]; onStack[w] = false; finished[w] = done; members.Add(w); } while (w != v);
                        if (members.Count > 1) cycles.Add(members.ToArray());
                    }
                    fp--;
                    if (fp >= 0 && low[v] < low[frameNode[fp]]) low[frameNode[fp]] = low[v];
                }
            }
            for (int n = 0; n < count; n++) _wave[n] = finished[n] > 0 ? done - finished[n] : int.MaxValue;
            foreach (int[] cycle in cycles)
            {
                int keep = cycle[0];
                foreach (int n in cycle) if ((_pts[n]?.Count ?? 0) > (_pts[keep]?.Count ?? 0)) keep = n;
                Merge(keep, cycle);
                if (Overflowed) return;
            }
        }

        /// <summary>
        /// The members of a cycle made `keep`: each one's edges become keep's,
        /// carrying what keep holds along them as a new edge does, and what
        /// each held is added to keep, carried along every edge it now has.
        /// What a member had gained and not yet carried is in what it held.
        /// </summary>
        private void Merge(int keep, int[] cycle)
        {
            foreach (int n in cycle) if (n != keep) _parent[n] = keep;
            foreach (int n in cycle)
            {
                if (n == keep) continue;
                if (_deltaBuf[n] is { } pending) { _spareDeltas.Push(pending); _deltaBuf[n] = null; _deltaLen[n] = 0; }
                if (_copies[n] is { } copies) { _copies[n] = null; foreach ((int to, int shift) in copies) CopyEdge(keep, to, shift); }
                if (_loads[n] is { } loads) { _loads[n] = null; foreach ((int dest, int offset) in loads) LoadEdge(dest, keep, offset); }
                if (_stores[n] is { } stores) { _stores[n] = null; foreach ((int value, int offset) in stores) StoreEdge(keep, offset, value); }
                if (_readsAll[n] is { } all) { _readsAll[n] = null; foreach (int dest in all) LoadAllEdge(dest, keep); }
                // A guard's filter and a call's receiver are edges as a copy
                // is: what keep holds goes along them now, and what the
                // member held below.
                if (_filters[n] is { } filters) { _filters[n] = null; foreach ((int to, int guard) in filters) FilterEdge(keep, to, guard); }
                if (_vcalls[n] is { } vcalls)
                {
                    _vcalls[n] = null;
                    List<VCall> into = _vcalls[keep] ??= new();
                    foreach (VCall v in vcalls)
                    {
                        into.Add(v);
                        if (_pts[keep] is { } had) for (int i = 0, c = had.Count; i < c; i++) Received(v, had.Items[i]);
                    }
                }
                if (_pts[n] is { } held)
                {
                    _pts[n] = null;
                    for (int i = 0; i < held.Count; i++) Add(keep, held.Items[i]);
                }
            }
        }

        /// <summary>
        /// WHAT IS OUTSIDE MAY BE WRITTEN THROUGH ANY WAY OUTSIDE. An object
        /// made here that a place reaches is the caller's to reach by other
        /// ways too -- another parameter that is the same object, a parent
        /// pointer -- so it holds, at any offset, whatever was written into
        /// any place; and one the unknown object reaches holds the unknown
        /// object, which anybody may have written there. Until nothing more
        /// joins. Nothing else can be in it: it was made during this call,
        /// and every write of this call is a write here, into it or into a
        /// place. (Some object outside the call in each, mapped by a caller
        /// to every argument and all it reaches, put every argument in what
        /// the call made: an emitter's instructions held the syntax tree,
        /// and the tree outlived the module handed back.)
        /// </summary>
        private void Aliased()
        {
            HashSet<int> feeding = new();
            while (!Overflowed)
            {
                bool more = false;
                List<int> start = new();
                for (int o = 1; o < _kind.Count; o++) if (_kind[o] is Kind.Place or Kind.Deep && Written(o)) start.Add(o);
                foreach (int o in Reached(start))
                {
                    if (_kind[o] != Kind.Made || _placedMade.Contains(o)) continue;
                    if (_written < 0) { _written = NewNode(); _nodeFlags[_written] |= NeverSaturated; }
                    more = true;
                    Placed(o);
                }
                if (_written >= 0)
                    for (int o = 1; o < _kind.Count; o++)
                        if (_kind[o] is Kind.Place or Kind.Deep)
                            foreach (int node in _cells[o].Values.ToArray()) if (feeding.Add(node)) { CopyEdge(node, _written, 0); more = true; }
                foreach (int o in Reached(new[] { 0 }))
                {
                    if (o == 0 || _kind[o] != Kind.Made || _unknownMade.Contains(o)) continue;
                    more = true;
                    ReachedByUnknown(o);
                }
                if (!more) break;
                Propagate();
            }
        }

        // ---- answers ---------------------------------------------------------

        // The objects reached from `start` through what their cells hold; not
        // past an object `skipped` says to stop at.
        private HashSet<int> Reached(IEnumerable<int> start, Func<int, bool>? skipped = null)
        {
            HashSet<int> seen = new();
            Stack<int> next = new();
            foreach (int o in start) if (seen.Add(o)) next.Push(o);
            while (next.TryPop(out int o))
            {
                if (skipped is not null && skipped(o)) continue;
                foreach (int node in _cells[o].Values)
                    if (Pts(node) is { } pts)
                        foreach (int loc in pts)
                            if (seen.Add(_locObject[loc])) next.Push(_locObject[loc]);
            }
            return seen;
        }

        private bool Written(int o)
        {
            foreach (int node in _cells[o].Values) if (Pts(node) is { Count: > 0 }) return true;
            return false;
        }

        // Whether object o is a place of member m (or past every parameter), or another member's.
        private bool PlaceOf(int o, int m) => _kind[o] is Kind.Place or Kind.Deep && (_param[o] < 0 || _param[o] / ParamStride == m);
        private bool ForeignPlace(int o, int m) => _kind[o] is Kind.Place or Kind.Deep && _param[o] >= 0 && _param[o] / ParamStride != m;

        private int Rank(int o) => _kind[o] switch { Kind.Place => 1, Kind.Deep => 2, _ => 3 };

        private int Compare(int a, int b)
        {
            int ka = Rank(a), kb = Rank(b);
            if (ka != kb) return ka.CompareTo(kb);
            if (_kind[a] != Kind.Made)
            {
                if (_param[a] != _param[b]) return _param[a].CompareTo(_param[b]);
                int la = _path[a].Length, lb = _path[b].Length;
                if (la != lb) return la.CompareTo(lb);
                for (int k = 0; k < la; k++) if (_path[a][k] != _path[b][k]) return _path[a][k].CompareTo(_path[b][k]);
                return 0;
            }
            return a.CompareTo(b);
        }

        /// <summary>
        /// MEMBER m's SUMMARY, for calls from outside the cycle: what it
        /// leaves its own places and the unknown object, and its result, with
        /// the objects they reach -- not what other members' calls of it
        /// handed it, which no call from outside does.
        /// </summary>
        public Summary Summarise(int m)
        {
            if (Overflowed) return Summary.Unknown;
            List<int> start = new() { 0 };
            for (int o = 1; o < _kind.Count; o++) if (PlaceOf(o, m) && Written(o)) start.Add(o);
            if (Pts(Ret(m)) is { } result) foreach (int loc in result) start.Add(_locObject[loc]);
            HashSet<int> outside = Reached(start, o => ForeignPlace(o, m));
            outside.RemoveWhere(o => ForeignPlace(o, m));
            // WHAT THE UNKNOWN OBJECT REACHES IS EVERYONE'S, whatever holds
            // it: the objects made among it are one, and every one of it is
            // a cell of the unknown object's -- what it holds in turn is
            // reached anyway, and a load of it finds the unknown object.
            // (Kept apart, the exceptions a list's Add and a dictionary's
            // indexer may throw made a node's Emit a hundred cells past the
            // bound, and its coarse summary leaked the whole tree below it.)
            HashSet<int> leaked = Reached(new[] { 0 }, o => ForeignPlace(o, m));
            leaked.IntersectWith(outside);
            // A place leaked below another leaked place is reached from it:
            // only what else holds it, or hands it back, keeps it.
            HashSet<int> held = new();
            foreach (int o in outside)
                if (!leaked.Contains(o))
                    foreach (int node in _cells[o].Values)
                        if (Pts(node) is { } pts) foreach (int loc in pts) held.Add(_locObject[loc]);
            if (Pts(Ret(m)) is { } handed) foreach (int loc in handed) held.Add(_locObject[loc]);
            HashSet<(int, string)> leakedPlaces = new();
            foreach (int o in leaked) if (_kind[o] is Kind.Place or Kind.Deep) leakedPlaces.Add((_param[o], string.Join(",", _path[o])));
            bool Below(int o)
            {
                if (_kind[o] is not (Kind.Place or Kind.Deep) || held.Contains(o)) return false;
                int[] path = _path[o];
                for (int n = 0; n < path.Length; n++) if (leakedPlaces.Contains((_param[o], string.Join(",", path[..n])))) return true;
                return false;
            }
            List<int> order = outside.Where(o => o != 0 && !(leaked.Contains(o) && Below(o))).ToList();
            order.Sort(Compare);
            Summary s = new();
            Dictionary<int, int> index = new() { [0] = 0 };
            int blob = -1;
            List<int> blobOrigins = new();
            foreach (int o in order)
            {
                if (_kind[o] == Kind.Made && leaked.Contains(o))
                {
                    if (blob < 0) { blob = s.Objects.Count; s.Objects.Add((Kind.Made, -1, Array.Empty<int>(), Array.Empty<int>())); }
                    index[o] = blob;
                    blobOrigins.AddRange(_origins[o]);
                    continue;
                }
                index[o] = s.Objects.Count;
                int param = _kind[o] is Kind.Place or Kind.Deep && _param[o] >= 0 ? _param[o] - m * ParamStride : _param[o];
                s.Objects.Add((_kind[o], param, _path[o], _origins[o]));
            }
            if (blob >= 0) { blobOrigins.Sort(); s.Objects[blob] = (Kind.Made, -1, Array.Empty<int>(), blobOrigins.Distinct().ToArray()); }
            foreach (int o in outside)
                foreach (var (offset, node) in _cells[o])
                    if (Pts(node) is { } pts)
                        foreach (int loc in pts)
                        {
                            int to = _locObject[loc];
                            if (!index.TryGetValue(to, out int t)) continue;   // another member's place
                            bool fromLeaked = leaked.Contains(o);
                            if (to == 0 && fromLeaked) continue;
                            s.Cells.Add((fromLeaked ? 0 : index[o], fromLeaked ? Any : offset, t, t == blob ? Any : _locOffset[loc]));
                        }
            if (Pts(Ret(m)) is { } back)
                foreach (int loc in back)
                    if (index.TryGetValue(_locObject[loc], out int t)) s.Result.Add((t, t == blob ? Any : _locOffset[loc]));
            s.Cells.Sort(); s.Result.Sort();
            Summary.Dedupe(s.Cells); Summary.Dedupe(s.Result);
            return s.Bounded();
        }

        /// <summary>Member m's answers: what outlives it, what is global, what its loops hold.</summary>
        public void Answer(int m)
        {
            int f = _members[m];
            RegionFunction function = Function(m);
            if (Overflowed)
            {
                // NOT FOLLOWED: its own sites and every object its callees'
                // summaries make are everyone's; nothing is asked of it.
                _owner.Unfollowed++;
                for (int site = 0; site < function.Sites.Length; site++) _owner._globalRefs.Add(Leaf(_owner._siteBase[f] + site));
                for (int k = 0; k < function.Calls.Count; k++)
                {
                    if (_owner._targets[f][k] is not { } targets) continue;
                    if (_owner.IsWide(targets))
                    {
                        if (_owner.StandIn(targets) is { Holder: >= 0 } standIn)
                            for (int i = 0; i < standIn.Objects.Count; i++) if (standIn.Objects[i].Kind == Kind.Made) _owner._globalRefs.Add(Ref(standIn.Holder, i));
                        continue;
                    }
                    foreach (int g in targets)
                        if (!_memberSet.Contains(g) && _owner._summaries[g] is { Holder: >= 0 } s)
                            for (int i = 0; i < s.Objects.Count; i++) if (s.Objects[i].Kind == Kind.Made) _owner._globalRefs.Add(Ref(s.Holder, i));
                }
                return;
            }
            if (m == 0) foreach (int o in Reached(new[] { 0 })) _owner._globalRefs.UnionWith(_origins[o]);
            if (m == 0 && _owner.Why is { } why)
            {
                // For a report: which sites of this graph the unknown object reaches, and by what.
                HashSet<int> seen = new() { 0 };
                Queue<(int O, int From)> next = new();
                next.Enqueue((0, -1));
                while (next.TryDequeue(out var at))
                    foreach (var (offset, node) in _cells[at.O])
                        if (Pts(node) is { } pts)
                            foreach (int loc in pts)
                            {
                                int to = _locObject[loc];
                                if (!seen.Add(to)) continue;
                                next.Enqueue((to, at.O));
                                foreach (int site in _owner.SitesOf(_origins[to]))
                                    if (why(site)) _owner.Progress?.Invoke($"escape graphs why: in {_owner._functions[f].Name}{(_members.Length > 1 ? " (cycle of " + _members.Length + ")" : "")}: site {site} reached from {(at.O == 0 ? "the unknown object" : Describe(at.O))} +{offset}, saturated {_saturatedCount}");
                            }
            }
            bool rooted = _owner._rooted[f];
            if (_owner._wanted[f] || rooted)
            {
                // WHAT OUTLIVES IT: what its parameters come to hold, whoever
                // handed it, what it hands back, the unknown object, and every
                // place anything was written into -- a place one field below
                // a parameter is no cell of the parameter's, but a load's
                // finding: written, it is outside as the parameter is. (Left
                // out, a writer's grown buffer, put into the packet it holds,
                // was dead by the writer's return and freed with its region.)
                List<int> start = new() { 0 };
                for (int k = 0; k <= function.Parameters; k++)
                    if (Pts(Node(m, k)) is { } held) foreach (int loc in held) start.Add(_locObject[loc]);
                for (int o = 1; o < _kind.Count; o++) if (_kind[o] is Kind.Place or Kind.Deep && Written(o)) start.Add(o);
                SortedSet<int> escaping = new();
                foreach (int o in Reached(start)) escaping.UnionWith(_origins[o]);
                // CALLED WITH ANYTHING, from where nobody follows: what it
                // hands back and writes into what it was handed is everyone's.
                if (rooted) _owner._rootedRefs.UnionWith(escaping);
                if (_owner._wanted[f]) _owner.Escaping[f] = escaping.ToArray();
                if (_owner.Why is { } asked && _owner.WhyFunction?.Invoke(f) == true) WhyOutlives(m, asked);
            }
            if (function.Loops.Count > 0)
            {
                var held = new (int[], int[])[function.Loops.Count];
                for (int l = 0; l < held.Length; l++)
                {
                    RegionLoopShape loop = function.Loops[l];
                    held[l] = (OriginsHeld(m, loop.Live, Enumerable.Range(0, function.Slots)), OriginsHeld(m, loop.Invariant, loop.KeptSlots));
                }
                _owner.LoopHeld[f] = held;
            }
        }

        /// <summary>
        /// For a report (+why=Name): how each site asked about outlives member
        /// m -- from which of what outlives it (a parameter, its result, the
        /// unknown object, a place written), by which cells, to the object it
        /// is among. The first path found, breadth first: the shortest.
        /// </summary>
        private void WhyOutlives(int m, Func<int, bool> asked)
        {
            if (_owner.Progress is not { } say) return;
            int f = _members[m];
            Dictionary<int, (int From, int Offset)> parent = new();
            Dictionary<int, string> root = new();
            Queue<int> next = new();
            void Root(int o, string why)
            {
                if (root.ContainsKey(o) || parent.ContainsKey(o)) return;
                root[o] = why;
                next.Enqueue(o);
            }
            Root(0, "the unknown object");
            RegionFunction function = Function(m);
            for (int k = 0; k <= function.Parameters; k++)
                if (Pts(Node(m, k)) is { } held)
                    foreach (int loc in held) Root(_locObject[loc], k < function.Parameters ? "parameter " + k : "its result");
            for (int o = 1; o < _kind.Count; o++) if (_kind[o] is Kind.Place or Kind.Deep && Written(o)) Root(o, "a place written, " + Describe(o));
            int told = 0;
            while (next.TryDequeue(out int o) && told < 40)
            {
                foreach (int site in _owner.SitesOf(_origins[o]))
                {
                    if (!asked(site) || told >= 40) continue;
                    told++;
                    List<string> path = new();
                    for (int at = o; parent.TryGetValue(at, out var up); at = up.From) path.Add("+" + up.Offset + " " + Describe(at));
                    int start = o;
                    while (parent.TryGetValue(start, out var up)) start = up.From;
                    path.Reverse();
                    say($"escape graphs why: site {site} outlives {_owner._functions[f].Name}: from {root[start]}" + (path.Count == 0 ? "" : " by " + string.Join(" > ", path)));
                }
                foreach (var (offset, node) in _cells[o])
                    if (Pts(node) is { } pts)
                        foreach (int loc in pts)
                        {
                            int to = _locObject[loc];
                            if (root.ContainsKey(to) || parent.ContainsKey(to)) continue;
                            parent[to] = (o, offset);
                            next.Enqueue(to);
                        }
            }
        }

        private int[] OriginsHeld(int m, int[] nodes, IEnumerable<int> slots)
        {
            List<int> start = new();
            foreach (int n in nodes) if (n < Function(m).Nodes && Pts(Node(m, n)) is { } pts) foreach (int loc in pts) start.Add(_locObject[loc]);
            foreach (int slot in slots) if (_slotObjects.TryGetValue((m, slot), out int o)) start.Add(o);
            start.RemoveAll(o => o == 0);
            SortedSet<int> origins = new();
            foreach (int o in Reached(start)) origins.UnionWith(_origins[o]);
            return origins.ToArray();
        }

        public string Describe() => $"{_kind.Count} objects, {_locObject.Count} locations, {_pts.Count} nodes, {_carried} carried, {_saturatedCount} saturated{(Overflowed ? ", NOT FOLLOWED" : "")}";

        private string Describe(int o) => _kind[o] switch
        {
            Kind.Place => $"place {_param[o]} [{string.Join(",", _path[o].Select(Step))}]",
            Kind.Deep => $"deep place {_param[o]} below [{string.Join(",", _path[o].Select(Step))}]",
            Kind.Unknown => "the unknown object",
            _ => $"made object (origins {string.Join(",", _origins[o].Take(4))})",
        };
    }

    // ---- a large cycle, by unification ------------------------------------

    /// <summary>
    /// A LARGE CYCLE BY UNIFICATION (Steensgaard's, by field): every node
    /// points to one class of objects, and a class holds at each offset one
    /// class -- two things that may be in one place are one class from then
    /// on. Linear in what the cycle states, where inclusion over a cycle of
    /// thirteen thousand of the compiler's functions held a million nodes and
    /// gigabytes. Coarser: what the members hand one another is one class.
    /// Calls out of the cycle apply their summaries as the inclusion solve
    /// does; each member's summary for calls into the cycle is its classes
    /// as they are, field by field (ByField) -- what one field of an
    /// argument holds leaks or not apart from what another does -- and
    /// coarse past its bounds.
    /// </summary>
    private sealed class Unified
    {
        private readonly RegionEscape _owner;
        private readonly int[] _members;
        private readonly HashSet<int> _memberSet;
        private readonly Dictionary<int, int> _memberOf = new();
        private readonly int[] _base;

        // Classes: union-find, size, fields by offset (Any once collapsed),
        // origins, and the one class of the unknown object.
        private readonly List<int> _parent = new(), _size = new();
        private readonly List<Dictionary<int, int>?> _fields = new();
        private readonly List<bool> _collapsed = new();
        private readonly List<List<int>?> _originsOf = new();
        private readonly int _global;
        // Per node: the class it points to (-1: none yet).
        private readonly List<int> _pointee = new();
        private readonly Queue<(int, int)> _pending = new();
        private long _unions;

        public Unified(RegionEscape owner, int[] members)
        {
            _owner = owner; _members = members; _memberSet = new(members);
            _base = new int[members.Length];
            _global = NewClass();
            _collapsed[_global] = true;
            _fields[_global] = new() { [Any] = _global };
            for (int m = 0; m < members.Length; m++)
            {
                _memberOf[members[m]] = m;
                _base[m] = _pointee.Count;
                for (int n = 0; n < owner._functions[members[m]].Nodes; n++) _pointee.Add(-1);
            }
        }

        private RegionFunction Function(int m) => _owner._functions[_members[m]];
        private int Node(int m, int n) => _base[m] + n;

        private int NewClass()
        {
            _parent.Add(_parent.Count); _size.Add(1); _fields.Add(null); _collapsed.Add(false); _originsOf.Add(null);
            return _parent.Count - 1;
        }

        private int NewNode() { _pointee.Add(-1); return _pointee.Count - 1; }

        private int Find(int c)
        {
            int root = c;
            while (_parent[root] != root) root = _parent[root];
            while (_parent[c] != root) { int next = _parent[c]; _parent[c] = root; c = next; }
            return root;
        }

        private int Pointee(int n)
        {
            if (_pointee[n] < 0) _pointee[n] = NewClass();
            return Find(_pointee[n]);
        }

        private int Field(int c, int offset)
        {
            c = Find(c);
            if (offset == Any && !_collapsed[c]) Collapse(c);
            c = Find(c);
            int at = _collapsed[c] ? Any : offset;
            Dictionary<int, int> fields = _fields[c] ??= new();
            if (fields.TryGetValue(at, out int t)) return Find(t);
            t = NewClass();
            fields[at] = t;
            return t;
        }

        // Every field one: what was at each offset, unified.
        private void Collapse(int c)
        {
            c = Find(c);
            if (_collapsed[c]) return;
            _collapsed[c] = true;
            Dictionary<int, int>? fields = _fields[c];
            int any = -1;
            if (fields is not null)
                foreach (int t in fields.Values) { if (any < 0) any = t; else _pending.Enqueue((any, t)); }
            _fields[c] = new() { [Any] = any < 0 ? NewClass() : any };
            Drain();
        }

        private void Unify(int a, int b)
        {
            _pending.Enqueue((a, b));
            Drain();
        }

        private void Drain()
        {
            while (_pending.TryDequeue(out var pair))
            {
                int a = Find(pair.Item1), b = Find(pair.Item2);
                if (a == b) continue;
                _unions++;
                // The unknown object's class swallows: it holds itself everywhere.
                if (b == _global || a != _global && _size[a] < _size[b]) (a, b) = (b, a);
                _parent[b] = a;
                _size[a] += _size[b];
                if (_originsOf[b] is { } moved) (_originsOf[a] ??= new()).AddRange(moved);
                _originsOf[b] = null;
                Dictionary<int, int>? fa = _fields[a], fb = _fields[b];
                _fields[b] = null;
                if (_collapsed[a] || _collapsed[b])
                {
                    bool was = _collapsed[a];
                    _collapsed[a] = true;
                    List<int> all = new();
                    if (fa is not null) all.AddRange(fa.Values);
                    if (fb is not null) all.AddRange(fb.Values);
                    if (a == _global) { foreach (int t in all) _pending.Enqueue((_global, t)); _fields[a] = new() { [Any] = _global }; continue; }
                    int any = all.Count > 0 ? all[0] : NewClass();
                    foreach (int t in all) if (t != any) _pending.Enqueue((any, t));
                    _fields[a] = new() { [Any] = any };
                    continue;
                }
                if (fb is null) continue;
                if (fa is null) { _fields[a] = fb; continue; }
                foreach (var (offset, t) in fb)
                    if (fa.TryGetValue(offset, out int u)) _pending.Enqueue((u, t));
                    else fa[offset] = t;
            }
        }

        private int Fresh(int origin) => FreshAll(new[] { origin });

        private int FreshAll(int[] origins)
        {
            int c = NewClass();
            _originsOf[c] = new(origins);
            return c;
        }

        // A frame slot: one class, whoever holds its address.
        private readonly Dictionary<(int, int), int> _slots = new();
        private int SlotClass(int m, int slot)
        {
            if (!_slots.TryGetValue((m, slot), out int c)) _slots[(m, slot)] = c = NewClass();
            return Find(c);
        }

        // ---- building --------------------------------------------------------

        private readonly Dictionary<int[], (int[] Args, int Ret)> _dispatch = new(TargetsComparer.Instance);

        public Unified Solved()
        {
            for (int m = 0; m < _members.Length; m++)
            {
                RegionFunction f = Function(m);
                foreach (RegionConstraint c in f.Constraints)
                {
                    int a = Node(m, c.A);
                    switch (c.Kind)
                    {
                        case RegionConstraintKind.Site: Unify(Pointee(a), Fresh(Leaf(_owner._siteBase[_members[m]] + c.B))); break;
                        case RegionConstraintKind.Slot: Unify(Pointee(a), SlotClass(m, c.B)); break;
                        case RegionConstraintKind.Unknown: Unify(Pointee(a), _global); break;
                        // A constant joins nothing: storing it makes nothing escape.
                        case RegionConstraintKind.Symbol: if (!Function(m).ConstantsKnown) Unify(Pointee(a), _global); break;
                        case RegionConstraintKind.Copy:
                        {
                            int b = Node(m, c.B);
                            if (RegionConstraint.IsIndex(c.C)) { _indexed.Add((a, b)); break; }
                            Unify(Pointee(a), Pointee(b));
                            if (c.C != 0) Collapse(Pointee(a));
                            break;
                        }
                        case RegionConstraintKind.Load: Unify(Pointee(a), Field(Pointee(Node(m, c.B)), Plain(c.C))); break;
                        case RegionConstraintKind.Store: Unify(Field(Pointee(a), Plain(c.C)), Pointee(Node(m, c.B))); break;
                        case RegionConstraintKind.MemCopy:
                        {
                            int x = Pointee(a), y = Pointee(Node(m, c.B));
                            Collapse(x); Collapse(y);
                            Unify(Field(x, Any), Field(y, Any));
                            break;
                        }
                        case RegionConstraintKind.Leak: Unify(Pointee(a), _global); break;
                    }
                }
                for (int k = 0; k < f.Calls.Count; k++) Call(m, k, f.Calls[k]);
            }
            // An index scaled into an address: anywhere in the object, never the unknown object.
            foreach (var (a, b) in _indexed)
                if (Pointee(b) != _global) { Unify(Pointee(a), Pointee(b)); Collapse(Pointee(a)); }
            return this;
        }

        private readonly List<(int, int)> _indexed = new();

        private static int Plain(long offset) => offset < 0 || offset > FarthestField ? Any : (int)offset;

        private void Call(int m, int k, RegionCall call)
        {
            int f = _members[m];
            int[]? targets = _owner._targets[f][k];
            int[] args = new int[call.Arguments.Length];
            for (int a = 0; a < args.Length; a++) args[a] = call.Arguments[a] < 0 || _owner.NumberArgument(targets, a) ? -1 : Node(m, call.Arguments[a]);
            int dest = call.Dest < 0 ? -1 : Node(m, call.Dest);
            if (targets is null) { UnknownCall(args, dest); return; }
            if (targets.Length == 0) return;
            if (_owner.IsWide(targets)) { Apply(args, dest, _owner.StandIn(targets)); return; }
            int[] inside = targets.Where(_memberSet.Contains).ToArray();
            int[] outside = inside.Length == 0 ? targets : targets.Where(t => !_memberSet.Contains(t)).ToArray();
            if (inside.Length > 0)
            {
                if (!_dispatch.TryGetValue(inside, out var through))
                {
                    int most = inside.Max(t => _owner._functions[t].Parameters);
                    int[] p = new int[most];
                    for (int j = 0; j < most; j++) p[j] = NewNode();
                    int r = NewNode();
                    foreach (int t in inside)
                    {
                        int tm = _memberOf[t];
                        for (int j = 0; j < _owner._functions[t].Parameters; j++) Unify(Pointee(p[j]), Pointee(Node(tm, j)));
                        Unify(Pointee(r), Pointee(Node(tm, _owner._functions[t].Parameters)));
                    }
                    through = (p, r);
                    _dispatch[inside] = through;
                }
                for (int j = 0; j < args.Length && j < through.Args.Length; j++) if (args[j] >= 0) Unify(Pointee(args[j]), Pointee(through.Args[j]));
                for (int j = args.Length; j < through.Args.Length; j++) Unify(Pointee(through.Args[j]), _global);
                if (dest >= 0) Unify(Pointee(dest), Pointee(through.Ret));
            }
            if (outside.Length > 0) Apply(args, dest, _owner.MergedFor(outside));
        }

        // A place's path from an argument's class: a field its field, the
        // deep step one class below that holds itself, a guard nothing --
        // one class has every object of every class there.
        private int Walk(int c, int[] path)
        {
            foreach (int step in path)
            {
                if (IsGuard(step)) continue;
                if (step != DeepStep) { c = Field(c, step); continue; }
                int d = NewClass();
                Collapse(d);
                Unify(Field(d, Any), d);
                Unify(Field(c, Any), d);
                c = Find(d);
            }
            return c;
        }

        private void UnknownCall(int[] args, int dest)
        {
            foreach (int a in args) if (a >= 0) Unify(Pointee(a), _global);
            if (dest >= 0) Unify(Pointee(dest), _global);
        }

        private void Apply(int[] args, int dest, Summary s)
        {
            _owner.Applied++;
            if (s.IsUnknown) { UnknownCall(args, dest); return; }
            int[] cls = new int[s.Objects.Count];
            for (int k = 0; k < s.Objects.Count; k++)
            {
                var o = s.Objects[k];
                int arg = o.Param >= 0 && o.Param < args.Length ? args[o.Param] : -1;
                switch (o.Kind)
                {
                    case Kind.Unknown: cls[k] = _global; break;
                    // A parameter the call passes nothing for: whatever its word holds.
                    case Kind.Place or Kind.Deep when o.Param >= args.Length: cls[k] = _global; break;
                    case Kind.Place:
                        cls[k] = arg < 0 ? NewClass() : Walk(Pointee(arg), o.Path);
                        break;
                    case Kind.Deep when o.Param >= 0:
                        cls[k] = arg < 0 ? NewClass() : Walk(Pointee(arg), DeepPath(o.Path));
                        break;
                    case Kind.Deep:
                    {
                        // EVERYTHING, ONE CLASS that holds itself: past every
                        // parameter, every argument and all it reaches, for good.
                        int d = NewClass();
                        Collapse(d);
                        Unify(Field(d, Any), d);
                        foreach (int a in args) if (a >= 0) Unify(Pointee(a), d);
                        cls[k] = Find(d);
                        break;
                    }
                    case Kind.Made:
                        cls[k] = s.Holder >= 0 ? Fresh(Ref(s.Holder, k)) : FreshAll(o.Origins);
                        break;
                }
            }
            foreach (var c in s.Cells)
            {
                int to = cls[c.To];
                if (c.ToOffset != 0) Collapse(to);
                Unify(Field(cls[c.From], c.Offset), to);
            }
            if (dest >= 0)
                foreach (var r in s.Result)
                {
                    if (r.ToOffset != 0) Collapse(cls[r.To]);
                    Unify(Pointee(dest), cls[r.To]);
                }
        }

        // ---- answers ---------------------------------------------------------

        // ---- answers ---------------------------------------------------------

        // The classes every member's parameters and results point to: the only
        // places a reach is asked from, and what a reach is asked to find.
        private HashSet<int>? _roots;
        private HashSet<int>? _globalClasses;
        private readonly Dictionary<int, (int[] All, int[] Local, HashSet<int> Roots, bool Global)> _rootInfo = new();
        private readonly Dictionary<string, int[]> _shared = new(StringComparer.Ordinal);

        private void Prepare()
        {
            if (_roots is not null) return;
            _globalClasses = Reached(new[] { _global });
            _roots = new();
            for (int m = 0; m < _members.Length; m++)
                for (int k = 0; k <= Function(m).Parameters; k++)
                    if (_pointee[Node(m, k)] >= 0) _roots.Add(Pointee(Node(m, k)));
        }

        /// <summary>What a root class reaches, once: its origins (and those past the unknown object's), the roots it reaches, whether it reaches the unknown object.</summary>
        private (int[] All, int[] Local, HashSet<int> Roots, bool Global) Info(int c)
        {
            Prepare();
            c = Find(c);
            if (_rootInfo.TryGetValue(c, out var known)) return known;
            SortedSet<int> all = new(), local = new();
            HashSet<int> roots = new();
            bool global = false;
            foreach (int r in Reached(new[] { c }))
            {
                if (r == Find(_global)) global = true;
                if (_roots!.Contains(r)) roots.Add(r);
                if (_originsOf[r] is not { } origins) continue;
                all.UnionWith(origins);
                if (!_globalClasses!.Contains(r)) local.UnionWith(origins);
            }
            return _rootInfo[c] = (Share(all), Share(local), roots, global);
        }

        // One array for every member whose answer is the same.
        private int[] Share(IEnumerable<int> sorted)
        {
            int[] a = sorted as int[] ?? sorted.ToArray();
            if (a.Length == 0) return Array.Empty<int>();
            string key = a.Length + ":" + a[0] + ":" + a[^1] + ":" + HashOf(a);
            if (_shared.TryGetValue(key, out int[]? known) && known.AsSpan().SequenceEqual(a)) return known;
            return _shared[key] = a;
        }

        private static int HashOf(int[] a)
        {
            HashCode h = new();
            foreach (int x in a) h.Add(x);
            return h.ToHashCode();
        }

        private int[] MergeSorted(IEnumerable<int[]> arrays)
        {
            List<int[]> list = arrays.Where(x => x.Length > 0).Distinct().ToList();
            if (list.Count == 0) return Array.Empty<int>();
            if (list.Count == 1) return list[0];
            SortedSet<int> all = new();
            foreach (int[] a in list) all.UnionWith(a);
            return Share(all.ToArray());
        }

        private HashSet<int> Reached(IEnumerable<int> start)
        {
            HashSet<int> seen = new();
            Stack<int> next = new();
            foreach (int c in start) { int r = Find(c); if (seen.Add(r)) next.Push(r); }
            while (next.TryPop(out int c))
                if (_fields[c] is { } fields)
                    foreach (int t in fields.Values) { int r = Find(t); if (seen.Add(r)) next.Push(r); }
            return seen;
        }

        /// <summary>
        /// A MEMBER'S SUMMARY, field by field where unification kept them
        /// apart (ByField), else as coarse as unification once was
        /// (Coarsest).
        ///
        /// THE COARSE ONE: every argument and all it reaches one object, the
        /// objects made beneath it another, each may hold the other and
        /// itself, and the unknown object when any argument reaches it. One
        /// closure a call, where a cell from each parameter to each other's
        /// deep place made a call into the cycle cost the square of its
        /// arguments' reach.
        ///
        /// WHAT THE RESULT REACHES IS NOT THE ARGUMENTS': a member handing
        /// back a literal or a static's object -- a ToString -- reaches the
        /// unknown object by its result alone, and that once made the
        /// unknown object hold every argument, so that every argument of
        /// every call of it escaped. Only what the arguments reach decides
        /// whether the unknown object may hold them: a class of the caller's
        /// that the result reaches and the unknown object holds is one the
        /// arguments reach too, or the caller never had it.
        /// </summary>
        public Summary Summarise(int m) => ByField(m) ?? Coarsest(m);

        // How many classes one member's summary walks, and how many objects it
        // states, before it is the coarse one: a member of a cycle of
        // thousands each walking all it reaches was the square of the cycle.
        private const int MostWalked = 4096, MostStated = 512;

        /// <summary>
        /// A MEMBER'S SUMMARY BY FIELD: each argument's class, and what its
        /// fields hold, field by field, a place below that argument by the
        /// fields taken to it (WithField: past PlaceDepth, the deep place);
        /// a class holding objects made in the cycle a made object; and the
        /// unknown object's class the unknown object. A cell for every field
        /// of each such class to each class there -- at Any where the class
        /// was collapsed, pointing anywhere into one that was -- and the
        /// unknown object holding everything its class has: an argument
        /// below one field that leaks leaks, and what another field holds
        /// does not. What is below an object that leaks leaks with it, and
        /// is not followed. A class that holds nothing -- no object made, no
        /// place, read where nothing was written -- is nothing to the caller.
        /// Null past MostWalked or MostStated: the coarse summary (Coarsest).
        /// </summary>
        private Summary? ByField(int m)
        {
            Prepare();
            RegionFunction f = Function(m);
            int n = f.Parameters;
            int global = Find(_global);
            Summary s = new();
            Dictionary<int, List<int>> reps = new();
            Dictionary<(Kind, int, string), int> placed = new();
            Queue<(int Class, int Object)> next = new();
            int walked = 0;

            int Place(int k, int[] path)
            {
                Kind kind = KindOf(path);
                var key = (kind, k, string.Join(",", path));
                if (placed.TryGetValue(key, out int at)) return at;
                at = s.Objects.Count;
                s.Objects.Add((kind, k, path, Array.Empty<int>()));
                return placed[key] = at;
            }
            void Add(int c, int o)
            {
                List<int> list = reps.TryGetValue(c, out List<int>? known) ? known : reps[c] = new();
                if (list.Contains(o)) return;
                list.Add(o);
                next.Enqueue((c, o));
            }
            // A class first met: the unknown object, or the objects made in it.
            void Meet(int c)
            {
                if (reps.ContainsKey(c)) return;
                walked++;
                if (c == global) { Add(c, 0); return; }
                reps[c] = new();
                if (_originsOf[c] is { Count: > 0 } origins)
                {
                    int made = s.Objects.Count;
                    s.Objects.Add((Kind.Made, -1, Array.Empty<int>(), origins.Distinct().Order().ToArray()));
                    Add(c, made);
                }
            }

            for (int k = 0; k < n; k++)
            {
                // A parameter of a number type is handed no address.
                if (_pointee[Node(m, k)] < 0 || f.IsNumber(k)) continue;
                int c = Pointee(Node(m, k));
                Meet(c);
                Add(c, Place(k, Array.Empty<int>()));
            }
            int result = _pointee[Node(m, n)] >= 0 ? Pointee(Node(m, n)) : -1;
            if (result >= 0) Meet(result);
            while (next.TryDequeue(out var item))
            {
                if (walked > MostWalked || s.Objects.Count > MostStated) return null;
                (int c, int o) = item;
                if (c == global || _fields[c] is not { } fields) continue;
                var obj = s.Objects[o];
                foreach (var (offset, held) in fields)
                {
                    int t = Find(held);
                    Meet(t);
                    if (obj.Kind is Kind.Place or Kind.Deep && obj.Param >= 0) Add(t, Place(obj.Param, WithField(obj.Path, offset)));
                }
            }
            if (walked > MostWalked || s.Objects.Count > MostStated) return null;

            foreach (var (c, list) in reps)
            {
                if (list.Count == 0 || c == global || _fields[c] is not { } fields) continue;
                foreach (var (offset, held) in fields)
                {
                    int t = Find(held);
                    if (!reps.TryGetValue(t, out List<int>? to) || to.Count == 0) continue;
                    int at = _collapsed[t] ? Any : 0;
                    foreach (int r in list)
                        foreach (int u in to)
                            if (!(r == 0 && u == 0)) s.Cells.Add((r, offset, u, at));
                }
            }
            // What the unknown object's class holds, it holds: those escape.
            if (reps.TryGetValue(global, out List<int>? leaked))
                foreach (int u in leaked) if (u != 0) s.Cells.Add((0, Any, u, Any));
            if (result >= 0 && reps.TryGetValue(result, out List<int>? back))
                foreach (int u in back) s.Result.Add((u, _collapsed[result] ? Any : 0));
            s.Cells.Sort(); s.Result.Sort();
            Summary.Dedupe(s.Cells); Summary.Dedupe(s.Result);
            return s.Bounded();
        }

        /// <summary>The coarse summary (Summarise), past ByField's bounds.</summary>
        private Summary Coarsest(int m)
        {
            Prepare();
            RegionFunction f = Function(m);
            int n = f.Parameters;
            Summary s = new();
            var info = new (int[] All, int[] Local, HashSet<int> Roots, bool Global)[n + 1];
            bool any = false, argsGlobal = false, resultGlobal = false;
            for (int k = 0; k <= n; k++)
            {
                // A parameter of a number type is handed no address.
                int own = _pointee[Node(m, k)] < 0 || f.IsNumber(k) ? -1 : Pointee(Node(m, k));
                info[k] = own < 0 ? (Array.Empty<int>(), Array.Empty<int>(), new HashSet<int>(), false) : Info(own);
                if (own >= 0 && k < n) any = true;
                bool global = own >= 0 && (info[k].Global || _globalClasses!.Contains(own));
                if (k < n) argsGlobal |= global; else resultGlobal = global;
            }
            int all = -1;
            if (any) { all = s.Objects.Count; s.Objects.Add((Kind.Deep, -1, Array.Empty<int>(), Array.Empty<int>())); }
            int[] origins = MergeSorted(info.Select(x => x.Local));
            int blob = -1;
            if (origins.Length > 0) { blob = s.Objects.Count; s.Objects.Add((Kind.Made, -1, Array.Empty<int>(), origins)); }
            foreach (int from in new[] { all, blob })
            {
                if (from < 0) continue;
                if (all >= 0) s.Cells.Add((from, Any, all, Any));
                if (blob >= 0) s.Cells.Add((from, Any, blob, Any));
                // What was made may be reached from the result alone.
                if (argsGlobal || from == blob && resultGlobal) s.Cells.Add((from, Any, 0, Any));
            }
            if (argsGlobal && all >= 0) s.Cells.Add((0, Any, all, Any));
            if (_pointee[Node(m, n)] >= 0)
            {
                if (all >= 0) s.Result.Add((all, Any));
                if (blob >= 0) s.Result.Add((blob, Any));
                if (resultGlobal || argsGlobal) s.Result.Add((0, Any));
            }
            s.Cells.Sort(); s.Result.Sort();
            Summary.Dedupe(s.Cells); Summary.Dedupe(s.Result);
            s.MadeCoarse = true;
            return s;
        }

        public void Answer(int m)
        {
            Prepare();
            int f = _members[m];
            RegionFunction function = Function(m);
            if (m == 0)
            {
                _owner._globalRefs.UnionWith(Info(_global).All);
                _owner.Progress?.Invoke($"escape graphs: unified global class reaches {Info(_global).All.Length} origins ({_owner.SitesOf(Info(_global).All).Length} sites); {_globalClasses!.Count} of {_parent.Count} classes");
            }
            bool rooted = _owner._rooted[f];
            if (_owner._wanted[f] || rooted)
            {
                List<int[]> parts = new() { Info(_global).All };
                for (int k = 0; k <= function.Parameters; k++) if (_pointee[Node(m, k)] >= 0) parts.Add(Info(Pointee(Node(m, k))).All);
                int[] escaping = MergeSorted(parts);
                if (rooted) _owner._rootedRefs.UnionWith(escaping);
                if (_owner._wanted[f]) _owner.Escaping[f] = escaping;
            }
            if (function.Loops.Count > 0)
            {
                var held = new (int[], int[])[function.Loops.Count];
                for (int l = 0; l < held.Length; l++)
                {
                    RegionLoopShape loop = function.Loops[l];
                    held[l] = (Held(m, loop.Live, Enumerable.Range(0, function.Slots)), Held(m, loop.Invariant, loop.KeptSlots));
                }
                _owner.LoopHeld[f] = held;
            }
        }

        private int[] Held(int m, int[] nodes, IEnumerable<int> slots)
        {
            List<int> start = new();
            foreach (int n in nodes) if (n < Function(m).Nodes && _pointee[Node(m, n)] >= 0) start.Add(Pointee(Node(m, n)));
            foreach (int slot in slots) if (_slots.TryGetValue((m, slot), out int c)) start.Add(Find(c));
            SortedSet<int> origins = new();
            foreach (int r in Reached(start))
                if (!_globalClasses!.Contains(r) && _originsOf[r] is { } o) origins.UnionWith(o);
            return origins.ToArray();
        }

        public string Describe() => $"{_parent.Count} classes, {_pointee.Count} nodes, {_unions} unions";
    }
}
