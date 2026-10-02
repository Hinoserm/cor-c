#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// WHAT EACH ALLOCATION CAN BE REACHED FROM, over a whole module: the
/// points-to facts region inference stands on. A boundary -- a call whose
/// every object made beneath it and reachable afterwards only through what it
/// was handed, what it hands back, or a static -- frees the rest when it
/// returns; this pass answers which allocation sites are that rest.
///
/// Andersen's inclusion analysis over locations -- an object and a byte
/// offset into it, or any offset (an element, an address computed from an
/// index) -- with one object of context: an instance method is analysed once
/// per object it is called on, and what it makes is made once per such
/// object, so a List's array and the elements in it are that List's and not
/// every List's -- an object kept in a frame as much as one on the heap.
/// Contexts nest two deep; deeper ones are merged into the method's
/// context-free copy. A static function that hands back what it makes is
/// analysed once per call that reaches it, and what it makes is made once
/// per call (CallContext). A virtual call runs, for each object it is made
/// on, the method that object's own descriptor names.
///
/// Boundaries are the compiler's choice (Nearest): for each object the
/// lifetime passes left to the collector, the nearest call whose return it is
/// proved not to outlive. corc --region-report says what was proved.
/// </summary>
public sealed class RegionPointsTo : IModulePass
{
    public string Name => "region-points-to";

    /// <summary>
    /// The boundaries to report on (corc --region-report NAME,...: by part of
    /// a function's name): which allocations beneath each are proved dead by
    /// its return and which outlive it. Null for no report.
    /// </summary>
    public string? Report { get; init; }

    public const string Enter = Corsac.Lang.Lto.RuntimeAbi.RegionEnter;
    public const string Leave = Corsac.Lang.Lto.RuntimeAbi.RegionLeave;
    public const string InRegion = Corsac.Lang.Lto.RuntimeAbi.AllocRegion;
    public const string Near = "m_Runtime_AllocNear_4_V$NInt_V$NInt_V$NInt_V$NInt";
    private const long LeafKind = 0x4C454146, ObjectKind = 0x4F424A54;

    private const int Global = 0;
    private const int MaxDepth = 2;
    private const long Any = 0xFFFFFF;
    private const int NodeBudget = 6_000_000;
    // Past this many cells an object is one cell: an object read or written
    // at that many offsets is an array, or a pointer walked through memory.
    private const int MostCells = 64;
    // Past this many pairs of source and destination a block copy is one
    // node, everything from every source to anywhere in every destination.
    private const int MostPairs = 1024;
    // Past this many objects' copies of one method, the rest share its copy
    // in no object's context.
    private const int MostContexts = 16;
    private readonly Dictionary<Function, int> _contexts = new();

    // GIVING UP IS SOUND: nothing is rewritten. Past this many locations held
    // in all, the program is more than this analysis answers in the memory a
    // compile has, and it stops rather than taking the compiler down. A
    // location held in a set of more than a few is one bit (LocSet).
    private const long HeldBudget = 40_000_000;
    private long _held;
    // And past this much more heap than the compile had when the pass began,
    // whatever holds it: edges, watchers, pairs of copies.
    private const long HeapBudget = 200L * 1024 * 1024;
    private long _heapAtStart, _heapLimit = HeapBudget;

    private Module _m = null!;
    private Dictionary<string, Function> _byName = null!;
    private Dictionary<Instr, string[]>? _indirect;
    private Dictionary<string, DataItem> _data = null!;

    // Objects: an allocation site in a context, a frame slot of a function
    // copy, or the one Global (everything statics, unknown code and throws
    // can reach). Each has a cell node per offset stored to or read from,
    // and one for any offset.
    private readonly List<(Function? F, Instr? Site, int Context, FrameSlot? Slot, int Depth)> _objects = new();
    private readonly Dictionary<(Instr, int), int> _siteObjects = new();
    private readonly Dictionary<(int Copy, FrameSlot), int> _slotObjects = new();
    private readonly List<Dictionary<long, int>> _cells = new();
    private readonly List<List<Action<long, int>>?> _cellWatchers = new();
    // Per object: a node holding what every cell of it holds (-1 until
    // asked for), and whether its cells were folded into the any-offset one.
    private readonly List<int> _allCells = new();
    private readonly List<bool> _collapsed = new();

    // Function copies: a function in a context (-1: none).
    private readonly List<(Function F, int Context)> _copies = new();
    private readonly Dictionary<(Function, int), int> _copyIds = new();
    private readonly List<int> _copyBase = new();
    // Which copies call each copy, and which each calls.
    private readonly List<HashSet<int>> _callers = new();
    private readonly List<HashSet<int>> _callees = new();
    private readonly Dictionary<(int, Operand), int> _constants = new();

    // Nodes: a register of a copy, a copy's return, a cell. What each holds
    // is a set of locations; edges carry them on, moved by a constant or
    // made any-offset on the way.
    private readonly List<LocSet> _pts = new();
    private readonly List<List<(int To, long Shift)>?> _edges = new();
    private readonly List<HashSet<(int, long)>?> _edgeSet = new();
    private readonly List<List<Action<long>>?> _watchers = new();
    private readonly List<List<int>?> _delta = new();
    // Every location named so far, by number: what sets hold is the number.
    private readonly Dictionary<long, int> _locIds = new();
    private readonly List<long> _locs = new();
    private int _globalId = -1;
    // Per node: a cell no reference is ever kept in (HoldsNoReference),
    // which holds the unknown at most.
    private readonly List<bool> _noReference = new();
    private readonly Queue<int> _work = new();
    private long _steps;

    // Past this an offset is any offset: no field lies that far into an
    // object, and a pointer walked further is walking an array.
    private const long FarthestField = 4096;

    private static long Loc(int o, long offset) => ((long)o << 24) | (offset is < 0 or > FarthestField ? Any : offset);
    private static int ObjectOf(long loc) => (int)(loc >> 24);
    private static long OffsetOf(long loc) => loc & Any;
    // A pointer already inside an object moved again is anywhere in it: a
    // field's address is the object's moved once, and a count that once held
    // a pointer, stepped through memory where Walked cannot see the loop,
    // made every object a location per step.
    private static long Shifted(long loc, long shift) =>
        shift == long.MinValue || OffsetOf(loc) is not 0 ? Loc(ObjectOf(loc), Any) : Loc(ObjectOf(loc), shift);

    public void Run(Module m)
    {
        // A UNIT THE LINK REGENERATES with the whole program's answer
        // (Lto.RegionSolver): nothing to solve here, only to apply.
        if (m.RegionFacts is { } facts)
        {
            ApplyFacts(m, facts);
            return;
        }
        if (m.Entry is null) return;
        if (new[] { Enter, Leave, InRegion, Near }.Where(h => !m.Functions.Any(f => f.Name == h)).ToList() is { Count: > 0 } missing)
        {
            if (Report is not null) Console.Error.WriteLine("regions: no " + string.Join(", ", missing) + " in this program; nothing made a region");
            return;
        }
        _m = m;
        _byName = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions) _byName[f.Name] = f;
        _indirect = Escape.IndirectTargetsOf(m, _byName);
        _data = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) _data[d.Name] = d;

        _heapAtStart = GC.GetTotalMemory(false);
        NewObject(null, null, -1, null, 0);          // Global

        try
        {
            if (_byName.TryGetValue(m.Entry, out Function? entry)) CopyOf(entry, -1);
        }
        catch (OverBudget)
        {
            if (Report is not null) Console.Error.WriteLine("regions: gave up while building the constraints");
            return;
        }
        bool rooted = false;
        while (true)
        {
            if (!Solve())
            {
                if (Report is not null) Console.Error.WriteLine($"regions: gave up at {_pts.Count} nodes, {_copies.Count} copies, {_objects.Count} objects");
                return;
            }
            // AN INSTANCE CALL NOTHING WAS SEEN TO BE MADE ON still runs: its
            // callee's context-free copy, so its stores and calls are counted.
            bool more = false;
            foreach (var (seen, bind) in _unbound)
                if (seen.Count == 0) { seen.Add(-3); bind(); more = true; }
            // CODE THIS NEVER SAW CALL may call anything whose address it was
            // given, with anything: once any call goes where this cannot
            // follow, every function whose address is taken is called from
            // there too.
            if (_unknownCalls && !rooted)
            {
                rooted = true;
                foreach (Function f in AddressTaken(m))
                {
                    int copy = CopyOf(f, -1);
                    for (int k = 0; k < f.Params.Count; k++) Add(Reg(copy, f.Params[k]), Loc(Global, Any));
                    Edge(ReturnNode(copy), Cell(Global, Any), 0);
                }
                more = true;
            }
            if (!more) break;
        }
        // Each merged node answers with its representative's set.
        for (int n = 0; n < _pts.Count; n++) if (_rep[n] != n) _pts[n] = _pts[Find(n)];
        if (Report is not null) Judge();
        Apply();
    }

    private readonly List<(HashSet<int> Seen, Action Bind)> _unbound = new();
    private bool _unknownCalls;

    // Every function named as a value -- in code, or in data (a vtable, a
    // delegate's table) -- rather than only called.
    private IEnumerable<Function> AddressTaken(Module m)
    {
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    foreach (Operand o in i.Operands)
                        if (o is SymOperand { Name: var n }) named.Add(n);
        foreach (DataItem d in m.Data) foreach (DataReloc r in d.Relocs) named.Add(r.Symbol);
        foreach (string n in named) if (_byName.TryGetValue(n, out Function? f)) yield return f;
    }

    // ---- sites, as a separately compiled unit names them --------------------

    /// <summary>An allocation site: a call of any allocator, collecting or manual.</summary>
    internal static bool IsSiteCall(Instr i) =>
        i.Op == Opcode.Call && (Opt.Escape.IsAllocator(i.Callee) || i.Callee == Opt.Escape.ManualAllocator || i.Callee == Opt.Escape.ManualObjectAllocator);

    /// <summary>Whether a region may take a site's objects: the collecting allocators' calls only.</summary>
    internal static bool IsRewritable(string? callee) =>
        callee is Opt.Escape.Allocator or Opt.Escape.LeafAllocator or Opt.Escape.ObjectAllocator;

    /// <summary>
    /// THE SITES THE LINK CHOSE, marked on the IR it regenerates the unit
    /// from, before the late passes: a site is its function and its ordinal
    /// among the function's allocator calls, numbered here as the unit's
    /// compile numbered them for its RegionHints (RegionSummary), over the
    /// same IR. The mark rides on every copy the inliner makes of the call.
    /// </summary>
    public static int MarkSites(Module m, Corsac.Lang.Lto.RegionFacts facts)
    {
        int marked = 0;
        if (facts.Sites.Count == 0) return 0;
        foreach (Function f in m.Functions)
        {
            int ordinal = 0;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (IsSiteCall(i))
                    {
                        if (facts.Sites.Contains((f.Name, ordinal))) { i.RegionSite = true; marked++; }
                        ordinal++;
                    }
        }
        return marked;
    }

    /// <summary>
    /// The link's answer applied: every marked site still a collecting
    /// allocator's call made in the innermost open region, and every
    /// boundary named made one. Whatever the late passes did to a site --
    /// placed it in a frame, took it apart -- it is no longer a call, and
    /// nothing is made of it.
    /// </summary>
    private static void ApplyFacts(Module m, Corsac.Lang.Lto.RegionFacts facts)
    {
        int sites = 0, opened = 0;
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    Instr i = b.Instrs[k];
                    if (!i.RegionSite || i.Op != Opcode.Call || !IsRewritable(i.Callee)) continue;
                    VReg frame = f.NewReg(IrTypes.Word, "allocframe");
                    b.Instrs.Insert(k, new Instr { Op = Opcode.FramePointer, Dest = frame, Line = i.Line });
                    k++;
                    b.Instrs[k] = Retarget(f, i, InRegion, frame);
                    sites++;
                }
        foreach (Function f in m.Functions)
            if (facts.Boundaries.Contains(f.Name)) { Open(f); opened++; }
        if (sites > 0 || opened > 0)
            Console.Error.WriteLine($"regions: {opened} boundaries, {sites} sites in the innermost region, from the link");
    }

    // ---- applying -------------------------------------------------------------

    /// <summary>
    /// Every allocation site whose objects each boundary above it is proved to
    /// outlive made in that boundary's region, and every boundary made one.
    /// A site in an instance method is made beside the object it was called
    /// on (AllocNear): what it makes for an object in a region must be dead
    /// by that region's end. Any other site is made in the innermost region
    /// open (AllocRegion): dead by the end of every boundary it runs beneath.
    ///
    /// A FUNCTION SOME CALLS NEED MADE DIFFERENTLY is made once more for them:
    /// where the copies a call reaches -- its own contexts, not every
    /// caller's -- prove more of its sites than the function's every copy
    /// does, that call runs a version of it making those in the region.
    /// </summary>
    private void Apply()
    {
        HashSet<Function> chosenBoundaries = Nearest();
        List<int> boundaries = new();
        for (int c = 0; c < _copies.Count; c++)
            if (chosenBoundaries.Contains(_copies[c].F)) boundaries.Add(c);
        if (boundaries.Count == 0) return;

        // For each boundary: what outlives it, and the copies beneath it.
        List<(HashSet<int> Outlives, HashSet<int> Beneath)> judged = new();
        foreach (int c in boundaries) judged.Add((OutlivingOf(c), Beneath(c)));

        // Each site's objects, and the copy each is made in.
        Dictionary<Instr, List<(int, int)>> bySite = new(ReferenceEqualityComparer.Instance);
        foreach (var ((copy, site), o) in _madeIn)
            (bySite.TryGetValue(site, out List<(int, int)>? l) ? l : bySite[site] = new()).Add((o, copy));

        // How a site's objects -- all of them, or those of the copies one
        // version runs -- are made: null for on the heap, as they were.
        string? Decide(Function f, List<(int Object, int Copy)> objects)
        {
            // Beside the object called on when that may be in a region; when
            // every one is in a frame or unknown, in the innermost region.
            bool beside = IsInstance(f) && objects.Any(m => _objects[m.Object].Context >= 0 && _objects[_objects[m.Object].Context].Site is not null);
            bool anywhere = false;
            foreach ((int o, int copy) in objects)
            {
                int context = _objects[o].Context;
                for (int k = 0; k < boundaries.Count; k++)
                {
                    if (!judged[k].Beneath.Contains(copy)) continue;
                    anywhere = true;
                    // Beside an object that is never in this boundary's
                    // region -- one that outlives it, or one in a frame --
                    // never in it either, so nothing to prove.
                    if (beside && context >= 0 && (judged[k].Outlives.Contains(context) || _objects[context].Site is null)) continue;
                    if (judged[k].Outlives.Contains(o)) return null;
                }
            }
            return anywhere ? beside ? Near : InRegion : null;
        }

        int near = 0, inRegion = 0;
        Dictionary<Instr, string> chosen = new(ReferenceEqualityComparer.Instance);
        foreach ((Instr site, List<(int Object, int Copy)> objects) in bySite)
        {
            if (site.Callee is not (Opt.Escape.Allocator or Opt.Escape.LeafAllocator or Opt.Escape.ObjectAllocator)) continue;
            Function f = _objects[objects[0].Object].F!;
            if (Decide(f, objects) is not { } helper) continue;
            chosen[site] = helper;
            if (Report is not null) Console.Error.WriteLine($"regions: {(helper == Near ? "beside" : "region")} {f.Name} line {site.Line} {TypeOf(objects[0].Object)}");
            if (helper == Near) near++; else inRegion++;
        }

        List<Version> versions = Versions(chosen, Decide);

        // The versions' bodies, from the functions as they are before any
        // is rewritten.
        Dictionary<Version, (Function Body, Dictionary<Instr, Instr> From)> made = new();
        List<Function> originals = _m.Functions.ToList();
        foreach (Version v in versions)
        {
            var body = CloneBody(v.F, v.F.Name + "$region$" + made.Count);
            made[v] = body;
            _m.Functions.Add(body.Body);
        }

        foreach (Function f in originals)
            Rewrite(f, chosen.GetValueOrDefault, i => _roots.TryGetValue(i, out Version? to) && to.Same is { } same && made.TryGetValue(same, out var b) ? b.Body.Name : null);
        HashSet<Function> opened = new();
        foreach (int c in boundaries)
            if (opened.Add(_copies[c].F)) Open(_copies[c].F);

        int versionSites = 0;
        foreach (Version v in versions)
        {
            (Function body, Dictionary<Instr, Instr> from) = made[v];
            Dictionary<Instr, string> helpers = new(ReferenceEqualityComparer.Instance);
            Dictionary<Instr, string> calls = new(ReferenceEqualityComparer.Instance);
            foreach (var (site, helper) in v.Sites)
                if (helper is not null)
                {
                    helpers[from[site]] = helper;
                    if (chosen.GetValueOrDefault(site) != helper) versionSites++;
                }
            foreach (var (call, to) in v.Calls) if (to.Same is { } same && made.TryGetValue(same, out var b)) calls[from[call]] = b.Body.Name;
            Rewrite(body, helpers.GetValueOrDefault, calls.GetValueOrDefault);
            if (opened.Contains(v.F)) Open(body);
            if (Report is not null) Console.Error.WriteLine($"regions: version {body.Name} for {v.Copies.Length} of its copies");
        }
        Console.Error.WriteLine($"regions: {opened.Count} boundaries, {inRegion} sites in the innermost region, {near} beside their object, {versions.Count} versions making {versionSites} more");
    }

    // ---- versions -------------------------------------------------------------

    // A function as some calls reach it: the copies those calls reach, how
    // each of its sites makes its objects there, and which version each of
    // its own calls reaches.
    private sealed class Version
    {
        public required Function F;
        public required int[] Copies;
        public readonly Dictionary<Instr, string?> Sites = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Instr, Version> Calls = new(ReferenceEqualityComparer.Instance);
        public bool Wanted;
        // The version whose body this one runs: itself, or one alike.
        public Version? Same;
    }

    // Which version each call in the functions themselves reaches.
    private readonly Dictionary<Instr, Version> _roots = new(ReferenceEqualityComparer.Instance);

    // So many versions in all, and of any one function: past either, a call
    // runs the function itself, whose every site is proved for every copy.
    private const int MaxVersions = 512;
    private const int VersionsPerFunction = 16;

    /// <summary>
    /// The versions worth making: each a function reached, by the calls that
    /// reach it, in fewer copies than all of its own, where those copies
    /// make some site differently or call a version that does.
    /// </summary>
    private List<Version> Versions(Dictionary<Instr, string> chosen, Func<Function, List<(int Object, int Copy)>, string?> decide)
    {
        Dictionary<Function, List<int>> copiesOf = new();
        for (int c = 0; c < _copies.Count; c++)
            (copiesOf.TryGetValue(_copies[c].F, out List<int>? l) ? l : copiesOf[_copies[c].F] = new()).Add(c);
        Dictionary<string, Version> known = new(StringComparer.Ordinal);
        Dictionary<Function, int> count = new();
        Queue<Version> next = new();

        Version? VersionOf(Function f, HashSet<int> reached)
        {
            if (f.Async is not null || reached.Count >= copiesOf[f].Count) return null;
            int[] copies = reached.Order().ToArray();
            string key = f.Name + ":" + string.Join(",", copies);
            if (known.TryGetValue(key, out Version? v)) return v;
            if (known.Count >= MaxVersions || count.GetValueOrDefault(f) >= VersionsPerFunction) return null;
            count[f] = count.GetValueOrDefault(f) + 1;
            known[key] = v = new Version { F = f, Copies = copies };
            next.Enqueue(v);
            return v;
        }

        // The version each direct call in `f`, run as `copies`, reaches.
        void CallsOf(Function f, IEnumerable<int> copies, Action<Instr, Version> found)
        {
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.Call || i.Callee is null || !_byName.TryGetValue(i.Callee, out Function? g)) continue;
                    HashSet<int> reached = new();
                    foreach (int c in copies)
                        if (_bindings.TryGetValue((c, i), out HashSet<int>? to)) reached.UnionWith(to);
                    if (reached.Count > 0 && VersionOf(g, reached) is { } v) found(i, v);
                }
        }

        // The program's own functions first: the budget is theirs before
        // the class library's.
        foreach (Function f in _m.Functions.Where(f => !f.FromLibrary).Concat(_m.Functions.Where(f => f.FromLibrary)))
            if (copiesOf.TryGetValue(f, out List<int>? all)) CallsOf(f, all, (i, v) => _roots[i] = v);

        while (next.TryDequeue(out Version? v))
        {
            foreach (Block b in v.F.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Callee is not (Opt.Escape.Allocator or Opt.Escape.LeafAllocator or Opt.Escape.ObjectAllocator)) continue;
                    List<(int, int)> objects = new();
                    foreach (int c in v.Copies) if (_madeIn.TryGetValue((c, i), out int o)) objects.Add((o, c));
                    string? was = chosen.GetValueOrDefault(i);
                    v.Sites[i] = objects.Count == 0 ? was : decide(v.F, objects) ?? was;
                    if (v.Sites[i] != was) v.Wanted = true;
                }
            CallsOf(v.F, v.Copies, (i, to) => v.Calls[i] = to);
        }

        // Wanted too: a version that calls one that is.
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Version v in known.Values)
                if (!v.Wanted && v.Calls.Values.Any(to => to.Wanted)) { v.Wanted = true; grew = true; }
        }
        // ONE BODY FOR VERSIONS ALIKE: the same function making the same sites
        // the same way and calling versions alike -- found as a machine's
        // states are merged, by splitting until no class splits further.
        List<Version> wanted = known.Values.Where(v => v.Wanted).ToList();
        Dictionary<Version, int> kind = new();
        for (int classes = -1; ;)
        {
            Dictionary<string, int> named = new(StringComparer.Ordinal);
            Dictionary<Version, int> next2 = new();
            foreach (Version v in wanted)
            {
                System.Text.StringBuilder key = new(v.F.Name);
                key.Append('|').Append(kind.GetValueOrDefault(v));
                foreach (Block b in v.F.Blocks)
                    foreach (Instr i in b.Instrs)
                    {
                        if (v.Sites.TryGetValue(i, out string? h)) key.Append(h == Near ? 'n' : h == InRegion ? 'r' : '-');
                        else if (i.Op == Opcode.Call) key.Append(v.Calls.TryGetValue(i, out Version? to) && to.Wanted ? kind.GetValueOrDefault(to) : -1).Append(',');
                    }
                string k = key.ToString();
                next2[v] = named.TryGetValue(k, out int id) ? id : named[k] = named.Count;
            }
            kind = next2;
            if (named.Count == classes) break;
            classes = named.Count;
        }
        Dictionary<int, Version> first = new();
        foreach (Version v in wanted)
            v.Same = first.TryGetValue(kind[v], out Version? one) ? one : first[kind[v]] = v;
        return first.Values.ToList();
    }

    /// <summary>A function's body copied whole under another name, and which copied instruction each of its own became.</summary>
    private (Function Body, Dictionary<Instr, Instr> From) CloneBody(Function f, string name)
    {
        Function made = new(name, f.Returns)
        {
            Exported = false, SourceFile = f.SourceFile, Line = f.Line, Display = f.Display, FromLibrary = f.FromLibrary,
            NoInlining = f.NoInlining,
        };
        Dictionary<VReg, VReg> regs = new();
        Dictionary<FrameSlot, FrameSlot> slots = new();
        Dictionary<Block, Block> blocks = new();
        Dictionary<Instr, Instr> from = new(ReferenceEqualityComparer.Instance);
        VReg Reg(VReg r) => regs.TryGetValue(r, out VReg? m) ? m : regs[r] = made.NewReg(r.Type, r.Name);
        foreach (VReg p in f.Params) made.Params.Add(Reg(p));
        foreach (FrameSlot s in f.Slots) slots[s] = made.NewSlot(s.Bytes, s.Align, s.Name);
        foreach (Block b in f.Blocks)
        {
            Block copy = made.NewBlock(b.Label + "$");
            copy.IsLandingPad = b.IsLandingPad;
            blocks[b] = copy;
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                Instr c = new()
                {
                    Op = i.Op, Dest = i.Dest is null ? null : Reg(i.Dest), Size = i.Size, Signed = i.Signed, Offset = i.Offset,
                    Callee = i.Callee, DispatchType = i.DispatchType, Field = i.Field, Line = i.Line,
                    Default = i.Default is null ? null : blocks[i.Default],
                };
                foreach (Operand o in i.Operands)
                    c.Operands.Add(o switch { RegOperand r => new RegOperand(Reg(r.Reg)), SlotOperand s => new SlotOperand(slots[s.Slot]), _ => o });
                foreach (Block t in i.Targets) c.Targets.Add(blocks[t]);
                if (_m.KeepCalls.Contains(i)) _m.KeepCalls.Add(c);
                blocks[b].Instrs.Add(c);
                from[i] = c;
            }
        return (made, from);
    }

    // Each allocation `helper` names made by it, and each call `callee`
    // names sent there.
    private void Rewrite(Function f, Func<Instr, string?> helper, Func<Instr, string?> callee)
    {
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (helper(i) is { } h)
                {
                    // The allocating function's own frame: a region a throw
                    // left below it is closed before this one is used.
                    VReg frame = f.NewReg(IrTypes.Word, "allocframe");
                    b.Instrs.Insert(k, new Instr { Op = Opcode.FramePointer, Dest = frame, Line = i.Line });
                    k++;
                    b.Instrs[k] = Retarget(f, i, h, frame);
                }
                else if (callee(i) is { } to)
                {
                    Instr call = new() { Op = Opcode.Call, Dest = i.Dest, Callee = to, DispatchType = i.DispatchType, Field = i.Field, Line = i.Line };
                    call.Operands.AddRange(i.Operands);
                    if (_m.KeepCalls.Contains(i)) _m.KeepCalls.Add(call);
                    b.Instrs[k] = call;
                }
            }
    }

    // Whether a function may be a boundary at all: not the entry, not a
    // type's initialiser (run once, wherever first asked), not an async or
    // iterator body (its frame outlives a return).
    private bool MayBeBoundary(Function f) =>
        f.Async is null && f.Name != _m.Entry && !f.Name.Contains("StaticInit", StringComparison.Ordinal);

    /// <summary>
    /// THE NEAREST CALL EACH OBJECT DIES IN: from the copy that makes it up
    /// through its callers, nearest first, the first whose return it is
    /// proved not to outlive.
    /// </summary>
    private HashSet<Function> Nearest()
    {
        const int Reach = 8;
        HashSet<Function> found = new();
        for (int o = 1; o < _objects.Count; o++)
        {
            var obj = _objects[o];
            if (obj.Site?.Callee is not (Opt.Escape.Allocator or Opt.Escape.LeafAllocator or Opt.Escape.ObjectAllocator)) continue;
            int made = CopyIdOfObject(o);
            if (made < 0) continue;
            Dictionary<int, int> depth = new() { [made] = 0 };
            Queue<int> next = new();
            next.Enqueue(made);
            while (next.TryDequeue(out int c))
            {
                Function f = _copies[c].F;
                if (MayBeBoundary(f) && !OutlivingOf(c).Contains(o)) { found.Add(f); break; }
                if (depth[c] >= Reach) continue;
                foreach (int caller in _callers[c])
                    if (depth.TryAdd(caller, depth[c] + 1)) next.Enqueue(caller);
            }
        }
        if (Report is not null) foreach (Function f in found) Console.Error.WriteLine($"regions: boundary chosen {f.Name}");
        return found;
    }

    private readonly Dictionary<int, HashSet<int>> _outliving = new();
    private HashSet<int> OutlivingOf(int c) => _outliving.TryGetValue(c, out HashSet<int>? known) ? known : _outliving[c] = Outliving(c);

    private static Instr Retarget(Function f, Instr alloc, string helper, VReg frame)
    {
        Operand bytes = alloc.Operands[0];
        long kind = alloc.Callee == Opt.Escape.LeafAllocator ? LeafKind : alloc.Callee == Opt.Escape.ObjectAllocator ? ObjectKind : 0;
        Instr made = new() { Op = Opcode.Call, Callee = helper, Dest = alloc.Dest, Line = alloc.Line };
        made.Operands.Add(bytes);
        made.Operands.Add(new ImmOperand(kind, IrTypes.Word));
        if (helper == Near) made.Operands.Add(new RegOperand(f.Params[0]));
        made.Operands.Add(new RegOperand(frame));
        return made;
    }

    // The region opened on entry, given back on every return; a throw is
    // the runtime's to notice (Gc.PopStale).
    private static void Open(Function f)
    {
        // Never inlined: the record names the boundary's own frame, and a
        // caller's would outlive a throw the caller catches.
        f.NoInlining = true;
        VReg frame = f.NewReg(IrTypes.Word, "regionframe");
        VReg handle = f.NewReg(IrTypes.Word, "region");
        Block entry = f.Blocks[0];
        entry.Instrs.InsertRange(0, new[]
        {
            new Instr { Op = Opcode.FramePointer, Dest = frame, Line = f.Line },
            new Instr { Op = Opcode.Call, Callee = Enter, Dest = handle, Operands = { new RegOperand(frame) }, Line = f.Line },
        });
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
                if (b.Instrs[k].Op == Opcode.Ret)
                {
                    b.Instrs.Insert(k, new Instr { Op = Opcode.Call, Callee = Leave, Operands = { new RegOperand(handle) }, Line = b.Instrs[k].Line });
                    k++;
                }
    }

    /// <summary>Everything reachable from Global, from what copy `c` is handed, and from what it hands back.</summary>
    private HashSet<int> Outliving(int c)
    {
        Function f = _copies[c].F;
        // What Global reaches is the same for every copy: found once, and a
        // copy's own walk stops at it.
        _fromGlobal ??= Reachable(new[] { Global }, new HashSet<int>());
        HashSet<int> reached = new(_fromGlobal);
        List<int> start = new();
        foreach (VReg p in f.Params) foreach (long l in _pts[Reg(c, p)]) start.Add(ObjectOf(l));
        foreach (long l in _pts[ReturnNode(c)]) start.Add(ObjectOf(l));
        return Reachable(start, reached);
    }

    private HashSet<int>? _fromGlobal;
    private int[][]? _pointsInto;

    // The objects reachable from `start` through cells, added to `reached`;
    // one already in it is not walked again.
    private HashSet<int> Reachable(IEnumerable<int> start, HashSet<int> reached)
    {
        // Which objects each object's cells point into, found once.
        if (_pointsInto is null)
        {
            _pointsInto = new int[_objects.Count][];
            HashSet<int> into = new();
            for (int o = 0; o < _objects.Count; o++)
            {
                into.Clear();
                foreach (int cell in _cells[o].Values) foreach (long l in _pts[cell]) into.Add(ObjectOf(l));
                // A STATE MACHINE KEEPS WHAT ITS BODY SAVES across a suspension:
                // the stores AsyncTransform writes later, followed now.
                if (KeptBy().TryGetValue(o, out List<int>? machines))
                    foreach (int k in machines)
                    {
                        foreach (int saved in _machines[k].Saved) foreach (long l in _pts[saved]) into.Add(ObjectOf(l));
                        foreach (int slot in _machines[k].Slots) into.Add(slot);
                    }
                _pointsInto[o] = into.ToArray();
            }
        }
        Stack<int> next = new();
        foreach (int o in start) if (reached.Add(o)) next.Push(o);
        while (next.TryPop(out int o))
            foreach (int to in _pointsInto[o])
                if (reached.Add(to)) next.Push(to);
        return reached;
    }

    private Dictionary<int, List<int>> KeptBy()
    {
        if (_keptBy is not null) return _keptBy;
        _keptBy = new();
        for (int k = 0; k < _machines.Count; k++)
        {
            // A machine nothing was seen to make is anything's: Global's.
            HashSet<int> machines = _pts[_machines[k].Machine].Select(ObjectOf).ToHashSet();
            if (machines.Count == 0) machines.Add(Global);
            foreach (int o in machines)
                (_keptBy.TryGetValue(o, out List<int>? l) ? l : _keptBy[o] = new()).Add(k);
        }
        return _keptBy;
    }

    // ---- building -----------------------------------------------------------

    private int NewNode()
    {
        _rep.Add(_pts.Count);
        _noReference.Add(false);
        _pts.Add(new LocSet(_locs));
        _delta.Add(null);
        _edges.Add(null);
        _edgeSet.Add(null);
        _watchers.Add(null);
        return _pts.Count - 1;
    }

    private int NewObject(Function? f, Instr? site, int context, FrameSlot? slot, int depth)
    {
        _objects.Add((f, site, context, slot, depth));
        _cells.Add(new Dictionary<long, int>());
        _cellWatchers.Add(null);
        _allCells.Add(-1);
        _collapsed.Add(false);
        return _objects.Count - 1;
    }

    /// <summary>The node of one of an object's cells, made on first use.</summary>
    private int Cell(int o, long offset)
    {
        if (offset is < 0 or > FarthestField || _collapsed[o]) offset = Any;
        Dictionary<long, int> cells = _cells[o];
        if (cells.TryGetValue(offset, out int node)) return node;
        if (offset != Any && cells.Count >= MostCells) return Collapse(o);
        node = NewNode();
        cells[offset] = node;
        if (HoldsNoReference(o, offset)) _noReference[node] = true;
        if (_allCells[o] >= 0) Edge(node, _allCells[o], 0);
        // A cell read at any offset reads this one too; one written at any
        // offset is read wherever this one is.
        if (offset != Any)
        {
            Edge(Cell(o, Any), node, 0);
            if (_cellWatchers[o] is { } watchers) for (int w = 0; w < watchers.Count; w++) watchers[w](offset, node);
        }
        return node;
    }

    /// <summary>
    /// A WORD THE COLLECTOR NEVER READS AS A REFERENCE holds none: nothing
    /// kept there is an object anyone reaches by it, and what is read from it
    /// is a number, or the unknown at most -- never the objects an integer was
    /// merely thought to carry. A leaf (a string, an array of bytes) is never
    /// scanned; an object made with a descriptor is scanned by it: an array
    /// of numbers not at all, an instance by its reference map. The
    /// collector's own rules (Gc.ScanBlockWithin), and its own checks first:
    /// the stamp names a descriptor that names itself.
    /// </summary>
    private bool HoldsNoReference(int o, long offset)
    {
        Instr? site = _objects[o].Site;
        if (site?.Callee == Opt.Escape.LeafAllocator) return true;
        if (site?.Callee != Opt.Escape.ObjectAllocator || Stamp(o) is not var (table, at)) return false;
        int w = Target.Current.WordSize;
        if (at != Target.Current.DescriptorBytes || !_data.TryGetValue(table, out DataItem? d) || d.Bytes.Length < at
            || !d.Relocs.Any(r => r.Offset == DescSelf * w && r.Symbol == table && r.Addend == 0))
            return false;
        long flags = Word(d, DescFlags * w);
        if ((flags & 1) != 0)
            return (flags & 2) != 0 || (Word(d, DescGcFlags * w) & 1) == 0;
        int mapIndex = d.Relocs.FindIndex(r => r.Offset == DescRefMap * w);
        if (mapIndex < 0) return true;
        DataReloc mapAt = d.Relocs[mapIndex];
        if (offset == Any || mapAt.Addend != 0 || !_data.TryGetValue(mapAt.Symbol, out DataItem? map)) return false;
        if (offset % w != 0) return true;
        long word = offset / w;
        if (word >= Word(map, 0) || (1 + word / 32 + 1) * w > map.Bytes.Length) return false;
        return ((Word(map, (int)(1 + word / 32) * w) >> (int)(word % 32)) & 1) == 0;
    }

    private const int DescSelf = 5, DescFlags = 6, DescRefMap = 8, DescGcFlags = 9;

    private static long Word(DataItem d, int at)
    {
        long value = 0;
        for (int k = Target.Current.WordSize - 1; k >= 0; k--) value = value << 8 | (at + k < d.Bytes.Length ? d.Bytes[at + k] : 0);
        return value;
    }

    /// <summary>
    /// TOO MANY OFFSETS: the object becomes one any-offset cell. Each cell it
    /// had flows into that one, which already flows into each, so whatever
    /// is read from it anywhere is everything ever written to it; a location
    /// in it is any offset from here on.
    /// </summary>
    private int Collapse(int o)
    {
        int any = Cell(o, Any);
        _collapsed[o] = true;
        foreach (var (offset, node) in _cells[o]) if (offset != Any) Edge(node, any, 0);
        return any;
    }

    /// <summary>The node holding what every cell of an object holds: what a read at any offset reads.</summary>
    private int AllCells(int o)
    {
        if (_collapsed[o]) return Cell(o, Any);
        if (_allCells[o] >= 0) return _allCells[o];
        Cell(o, Any);
        int all = NewNode();
        _allCells[o] = all;
        if (HoldsNoReference(o, Any)) _noReference[all] = true;
        foreach (int cell in _cells[o].Values.ToArray()) Edge(cell, all, 0);
        return all;
    }

    /// <summary>Run `act` for every cell of an object, now and later.</summary>
    private void EachCell(int o, Action<long, int> act)
    {
        (_cellWatchers[o] ??= new()).Add(act);
        foreach (var (offset, node) in _cells[o].ToArray()) if (offset != Any) act(offset, node);
    }

    private int SiteObject(Instr site, Function f, int context)
    {
        int depth = Level(context);
        if (depth > MaxDepth) { context = -1; depth = 0; }
        if (_siteObjects.TryGetValue((site, context), out int known)) return known;
        int made = NewObject(f, site, context, null, depth);
        _siteObjects[(site, context)] = made;
        return made;
    }

    private int SlotObject(int copy, FrameSlot slot)
    {
        if (_slotObjects.TryGetValue((copy, slot), out int known)) return known;
        int made = NewObject(_copies[copy].F, null, copy, slot, 0);
        _slotObjects[(copy, slot)] = made;
        return made;
    }

    private int Reg(int copy, VReg r) => _copyBase[copy] + r.Id;
    private int ReturnNode(int copy) => _copyBase[copy] + _copies[copy].F.RegCount;

    private static bool IsInstance(Function f) => f.Params.Count > 0 && f.Params[0].Name == "this";

    private int CopyOf(Function f, int context)
    {
        if (!IsInstance(f)) { if (context >= 0) context = -1; }
        else if (context < -1 || context >= 0 && (_objects[context].Site is null && _objects[context].Slot is null || _objects[context].Depth >= MaxDepth)) context = -1;
        if (_copyIds.TryGetValue((f, context), out int known)) return known;
        // A METHOD CALLED IN MANY CONTEXTS is called in the rest without one:
        // each object (or call) its own copy multiplied the objects made in
        // them, and those the copies, past what a compile holds.
        if (context != -1)
        {
            _contexts.TryGetValue(f, out int made);
            if (made >= MostContexts) return CopyOf(f, -1);
            _contexts[f] = made + 1;
        }
        int copy = _copies.Count;
        _copies.Add((f, context));
        _copyIds[(f, context)] = copy;
        _copyBase.Add(_pts.Count);
        _callers.Add(new HashSet<int>());
        _callees.Add(new HashSet<int>());
        for (int k = 0; k <= f.RegCount; k++) NewNode();
        if (context >= 0) Add(Reg(copy, f.Params[0]), Loc(context, 0));
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                Constrain(copy, f, context, i);
        if (f.Async is { Lowered: false } frame)
        {
            // A SUSPENSION SAVES INTO THE STATE MACHINE, and the async
            // transform that writes those saves runs after this pass: every
            // register live across a suspension, and every frame slot (each
            // becomes a field of the machine), is kept by the machine for as
            // long as the machine lives. Kept here as reachability only: the
            // values a resumption reloads are the registers' own, so nothing
            // flows that does not flow already.
            List<int> saved = new();
            foreach (VReg r in SavedAcrossSuspensions(f, frame)) saved.Add(Reg(copy, r));
            List<int> slots = new();
            foreach (FrameSlot slot in f.Slots) slots.Add(SlotObject(copy, slot));
            _machines.Add((Reg(copy, frame.StateMachine), saved, slots));
        }
        return copy;
    }

    // Each async or iterator body copy: its machine's node, and what the
    // machine keeps -- the registers saved across a suspension, the slots.
    private readonly List<(int Machine, List<int> Saved, List<int> Slots)> _machines = new();
    // Which of those each object is a machine of (Global among them), once solved.
    private Dictionary<int, List<int>>? _keptBy;

    private readonly Dictionary<Function, List<VReg>> _saved = new();

    /// <summary>
    /// What an async or iterator body keeps in its state machine across a
    /// suspension, as AsyncTransform will: every register live after a resume
    /// marker, and every register a landing pad reads (the transform homes
    /// those in the machine too, since no liveness reaches a pad).
    /// </summary>
    private List<VReg> SavedAcrossSuspensions(Function f, AsyncFrame frame)
    {
        if (_saved.TryGetValue(f, out List<VReg>? known)) return known;
        Dictionary<int, VReg> registers = new();
        foreach (VReg p in f.Params) registers[p.Id] = p;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null) registers[i.Dest.Id] = i.Dest;
                foreach (Operand o in i.Operands) if (o is RegOperand { Reg: var u }) registers[u.Id] = u;
            }
        HashSet<VReg> saved = new();
        Liveness liveness = new(f);
        foreach (Block b in f.Blocks)
        {
            foreach ((Instr i, ulong[] liveAfter) in liveness.WalkBackwards(b))
            {
                if (!b.IsLandingPad && !(i.Op == Opcode.Call && i.Callee == AsyncFrame.Resume)) continue;
                foreach (VReg r in registers.Values)
                    if (r.Id < f.RegCount && Liveness.Test(liveAfter, r.Id)) saved.Add(r);
                if (b.IsLandingPad)
                    foreach (Operand o in i.Operands) if (o is RegOperand { Reg: var u }) saved.Add(u);
            }
        }
        saved.Remove(frame.StateMachine);
        return _saved[f] = saved.ToList();
    }

    private void Edge(int from, int to, long shift)
    {
        from = Find(from);
        to = Find(to);
        if (from == to && shift == 0) return;
        // A node that holds no reference holds the unknown at most, and
        // gives no more: no edge, only that.
        if (_noReference[from] || _noReference[to]) { Add(to, Loc(Global, Any)); return; }
        if (!NewEdge(from, to, shift)) return;
        _edgeCount++;
        LocSet held = _pts[from];
        if (from == to) { foreach (int id in held.Ids()) Add(to, Shifted(_locs[id], shift)); return; }
        // Walked in place: what is added goes to another node.
        if (held.Bits is { } bits)
        {
            for (int w = 0; w < bits.Length; w++)
                for (ulong word = bits[w]; word != 0; word &= word - 1)
                {
                    int id = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(word);
                    if (shift == 0) Held(to, id); else Add(to, Shifted(_locs[id], shift));
                }
            return;
        }
        for (int k = 0; k < held.Count; k++)
            if (shift == 0) Held(to, held.Few![k]); else Add(to, Shifted(_locs[held.Few![k]], shift));
    }

    // An edge not had before, recorded: a short list searched while short,
    // a set beside it past that -- most nodes have one or two edges.
    private bool NewEdge(int from, int to, long shift)
    {
        List<(int To, long Shift)> list = _edges[from] ??= new();
        if (_edgeSet[from] is not { } set)
        {
            if (list.Count < 16)
            {
                if (list.Contains((to, shift))) return false;
                list.Add((to, shift));
                return true;
            }
            _edgeSet[from] = set = new(list);
        }
        if (!set.Add((to, shift))) return false;
        list.Add((to, shift));
        return true;
    }

    private void Add(int node, long loc)
    {
        int o = ObjectOf(loc);
        if (_collapsed[o] && OffsetOf(loc) != Any) loc = Loc(o, Any);
        if (!_locIds.TryGetValue(loc, out int id))
        {
            _locIds[loc] = id = _locs.Count;
            _locs.Add(loc);
            if (loc == Loc(Global, Any)) _globalId = id;
        }
        Held(node, id);
    }

    // Location number `id` is held by `node`, and owed to what it feeds.
    private void Held(int node, int id)
    {
        node = Find(node);
        if (_noReference[node])
        {
            if (_globalId < 0) { Add(node, Loc(Global, Any)); return; }
            id = _globalId;
        }
        if (!_pts[node].Add(id)) return;
        // Checked here, not only between steps: one step's watchers can add
        // without end (a copy between two growing sets), and the compile died
        // inside it before the loop looked again.
        if ((++_held & 4095) == 0 && (_held > HeldBudget || OverHeap()))
            throw new OverBudget();
        List<int>? delta = _delta[node];
        if (delta is null) { _delta[node] = delta = new(); _work.Enqueue(node); }
        delta.Add(id);
    }

    /// <summary>Run `act` for every location `node` holds, now and later.</summary>
    private void Watch(int node, Action<long> act)
    {
        node = Find(node);
        (_watchers[node] ??= new()).Add(act);
        foreach (int id in _pts[node].Ids()) act(_locs[id]);
    }

    /// <summary>The node holding what operand `o` points to, in a copy; -1 for none.</summary>
    private int Value(int copy, Operand o)
    {
        if (o is RegOperand { Reg: var r }) return Reg(copy, r);
        if (o is not (SlotOperand or SymOperand)) return -1;
        if (_constants.TryGetValue((copy, o), out int known)) return known;
        int n = NewNode();
        Add(n, o is SlotOperand { Slot: var slot } ? Loc(SlotObject(copy, slot), 0) : Loc(Global, Any));
        return _constants[(copy, o)] = n;
    }

    /// <summary>An address operand's node: a constant address is somewhere unknown.</summary>
    private int Base(int copy, Operand o)
    {
        if (o is not ImmOperand) return Value(copy, o);
        if (_constants.TryGetValue((copy, o), out int known)) return known;
        int n = NewNode();
        Add(n, Loc(Global, Any));
        return _constants[(copy, o)] = n;
    }

    // What is read at `offset` from where `baseNode` points.
    private void Load(int dest, int baseNode, long offset)
    {
        if (baseNode < 0 || dest < 0) return;
        Watch(baseNode, l =>
        {
            int o = ObjectOf(l);
            // Out of Global comes Global: something unknown, not every object
            // that ever escaped -- those are already judged, and flowing on
            // they would reach every load from a static and every call on one.
            if (o == Global) { Add(dest, Loc(Global, Any)); return; }
            long at = OffsetOf(l) == Any ? Any : OffsetOf(l) + offset;
            if (at is < 0 or > FarthestField) at = Any;
            Edge(at == Any ? AllCells(o) : Cell(o, at), dest, 0);
        });
    }

    private void Store(int baseNode, long offset, int value)
    {
        if (baseNode < 0 || value < 0) return;
        Watch(baseNode, l =>
        {
            int o = ObjectOf(l);
            long at = o == Global || OffsetOf(l) == Any ? Any : OffsetOf(l) + offset;
            if (at is < 0 or > FarthestField) at = Any;
            Edge(value, Cell(o, at), 0);
        });
    }

    private void Leak(int value)
    {
        if (value >= 0) Edge(value, Cell(Global, Any), 0);
    }

    private void Constrain(int copy, Function f, int context, Instr i)
    {
        int dest = i.Dest is null ? -1 : Reg(copy, i.Dest);
        switch (i.Op)
        {
            case Opcode.Copy:
            case Opcode.Trunc64:
            case Opcode.ZExt32:
            case Opcode.SExt32:
            case Opcode.Phi:
            case Opcode.And:
            case Opcode.Or:
            // A tag shifted in can be shifted out again (Escape's rule too):
            // what is shifted left may still be the pointer.
            case Opcode.Shl:
                if (dest < 0) return;
                foreach (Operand o in i.Operands)
                    if (Value(copy, o) is int v and >= 0) Edge(v, dest, 0);
                return;

            case Opcode.Add:
            case Opcode.Sub:
            {
                // An address: the pointer moved by a constant, or by an index
                // to somewhere in it.
                if (dest < 0) return;
                // A POINTER WALKED IN A LOOP -- moved by a constant and joined
                // back into what it was moved from -- is anywhere in its
                // object: followed offset by offset, every turn made another
                // location, and the analysis ran out of memory counting them.
                bool constant = i.Operands.Count == 2 && i.Operands[1] is ImmOperand && !Walked(f, i);
                long by = constant ? ((ImmOperand)i.Operands[1]).Value * (i.Op == Opcode.Sub ? -1 : 1) : long.MinValue;
                foreach (Operand o in i.Operands)
                    if (Value(copy, o) is int v and >= 0) Edge(v, dest, by);
                return;
            }

            case Opcode.Load:
                Load(dest, Base(copy, i.Operands[0]), i.Offset);
                return;

            case Opcode.Store:
            case Opcode.InitArrayLength:
                if (i.Operands.Count >= 2) Store(Base(copy, i.Operands[0]), i.Offset, Value(copy, i.Operands[1]));
                return;

            case Opcode.AtomicSwap:
            case Opcode.AtomicCas:
            case Opcode.AtomicAdd:
            case Opcode.AtomicAnd:
            {
                // A read of the word and a write to it.
                int at = Base(copy, i.Operands[0]);
                Load(dest, at, i.Offset);
                for (int k = 1; k < i.Operands.Count; k++) Store(at, i.Offset, Value(copy, i.Operands[k]));
                return;
            }

            case Opcode.MemCopy:
                MemCopy(Base(copy, i.Operands[0]), Base(copy, i.Operands[1]),
                    i.Operands.Count > 2 && i.Operands[2] is ImmOperand n ? n.Value : Any);
                return;

            case Opcode.Ret:
                if (i.Operands.Count > 0 && Value(copy, i.Operands[0]) is int r and >= 0) Edge(r, ReturnNode(copy), 0);
                return;

            case Opcode.Unwind:
                foreach (Operand o in i.Operands) Leak(Value(copy, o));
                return;

            case Opcode.Call:
                Call(copy, f, context, i);
                return;

            case Opcode.CallIndirect:
                // A virtual call: each object it is made on runs the method
                // its own descriptor names in the slot.
                if (VirtualSlot(f, i) is long slot && Value(copy, i.Operands[1]) is int self and >= 0)
                {
                    HashSet<int> seen = new();
                    Watch(self, l =>
                    {
                        int o = ObjectOf(l);
                        if (!seen.Add(o)) return;
                        if (o != Global && Stamp(o) is var (table, at) && _data.TryGetValue(table, out DataItem? d))
                            foreach (DataReloc rel in d.Relocs)
                                if (rel.Offset == at + slot && rel.Addend == 0) { Bind(copy, i, rel.Symbol, 1, o); return; }
                        // An object whose descriptor is not known here -- the
                        // unknown one, one kept in a frame slot, one whose
                        // stamp was not found -- runs any of the call's targets.
                        if (!seen.Contains(-4)) { seen.Add(-4); Indirect(copy, i); }
                    });
                    return;
                }
                Indirect(copy, i);
                return;

            // What this does not follow -- a system call's answer, a frame's
            // or the stack's address, a label's -- may be anything; and what
            // a system call is handed, the kernel may keep and hand back
            // later (an event's data, a thread's argument).
            case Opcode.Syscall:
                foreach (Operand o in i.Operands) Leak(Value(copy, o));
                if (dest >= 0) Add(dest, Loc(Global, Any));
                return;
            case Opcode.FramePointer:
            case Opcode.StackPointer:
            case Opcode.LabelAddr:
                if (dest >= 0) Add(dest, Loc(Global, Any));
                return;

            // Numbers made of anything: a pointer multiplied, mixed, divided
            // or shifted right is a hash, and nothing in managed code turns
            // one back into a reference (Escape's rule too).
            default:
                return;
        }
    }

    // Whether an address computation's source is a join its own result flows
    // back into: `p = phi(start, next); next = p + 4`.
    private bool Walked(Function f, Instr add)
    {
        if (add.Dest is null || add.Operands[0] is not RegOperand { Reg: var from }) return false;
        HashSet<VReg> seen = new();
        Stack<VReg> next = new();
        next.Push(from);
        while (next.TryPop(out VReg? r))
        {
            if (!seen.Add(r) || seen.Count > 16) continue;
            if (r == add.Dest) return true;
            if (Single(f, r) is { Op: Opcode.Phi or Opcode.Copy } w)
                foreach (Operand o in w.Operands)
                    if (o is RegOperand { Reg: var q }) next.Push(q);
        }
        return false;
    }

    // Bytes move: what the source's cells hold, the destination's matching
    // cells may now hold.
    private void MemCopy(int to, int from, long count)
    {
        if (to < 0 || from < 0) return;
        // Each source location met with each destination once: two watchers
        // and the two sets seen so far, not one watcher on the destination
        // per source location.
        HashSet<long> srcSeen = new(), dstSeen = new();
        List<long> srcs = new(), dsts = new();
        // A copy at any offset depends only on the two objects.
        HashSet<(int, int)> anyPairs = new();
        void Pair(long src, long dst)
        {
            int os = ObjectOf(src), od = ObjectOf(dst);
            long ds = _collapsed[os] ? Any : OffsetOf(src), dd = _collapsed[od] ? Any : OffsetOf(dst);
            if (os == Global)
            {
                Add(Cell(od, Any), Loc(Global, Any));
                return;
            }
            if (ds == Any || dd == Any || od == Global)
            {
                if (anyPairs.Add((os, od))) Edge(AllCells(os), Cell(od, Any), 0);
                return;
            }
            EachCell(os, (offset, cell) =>
            {
                if (offset >= ds && (count == Any || offset - ds < count)) Edge(cell, Cell(od, dd + offset - ds), 0);
            });
            Edge(Cell(os, Any), Cell(od, Any), 0);
        }
        // TOO MANY PAIRS -- a copy through pointers that may each be any of
        // hundreds of objects -- and every source is copied to every
        // destination through one node, at any offset: a node per copy, not
        // a pair of every source and destination.
        int through = -1;
        void Into(long src)
        {
            if (ObjectOf(src) == Global) Add(through, Loc(Global, Any));
            else Edge(AllCells(ObjectOf(src)), through, 0);
        }
        void OutOf(long dst) => Edge(through, Cell(ObjectOf(dst), Any), 0);
        bool Summarised()
        {
            if (through >= 0) return true;
            if ((long)srcs.Count * dsts.Count <= MostPairs) return false;
            through = NewNode();
            foreach (long src in srcs) Into(src);
            foreach (long dst in dsts) OutOf(dst);
            return true;
        }
        Watch(from, src =>
        {
            if (!srcSeen.Add(src)) return;
            srcs.Add(src);
            if (through >= 0) { Into(src); return; }
            if (Summarised()) return;
            for (int k = 0; k < dsts.Count; k++) Pair(src, dsts[k]);
        });
        Watch(to, dst =>
        {
            if (!dstSeen.Add(dst)) return;
            dsts.Add(dst);
            if (through >= 0) { OutOf(dst); return; }
            if (Summarised()) return;
            for (int k = 0; k < srcs.Count; k++) Pair(srcs[k], dst);
        });
    }

    private void Call(int copy, Function f, int context, Instr i)
    {
        string? callee = i.Callee;
        if (callee is null) { Unknown(copy, i, 0); return; }
        if (Opt.Escape.IsAllocator(callee) || callee == Opt.Escape.ManualAllocator || callee == Opt.Escape.ManualObjectAllocator)
        {
            int made = SiteObject(i, f, context);
            _madeIn[(copy, i)] = made;
            if (i.Dest is not null) Add(Reg(copy, i.Dest), Loc(made, 0));
            return;
        }
        if (Harmless(callee)) return;
        if (_byName.ContainsKey(callee)) { Bind(copy, i, callee, 0); return; }
        Unknown(copy, i, 0);
    }

    // The collector's notes and the runtime's frees keep no pointer.
    internal static bool Harmless(string callee) =>
        Opt.Escape.IsCollectorNote(callee) || callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive
        || callee.StartsWith("m_Runtime_Free", StringComparison.Ordinal)
        || callee.StartsWith("m_Runtime_Card", StringComparison.Ordinal)
        || callee.StartsWith("m_Runtime_WriteBarrier", StringComparison.Ordinal);

    // Every target the call can reach, in no object's context.
    private void Indirect(int copy, Instr i)
    {
        if (_indirect is not null && _indirect.TryGetValue(i, out string[]? targets) && targets.Length > 0)
        {
            // The callee address is the first operand; the rest are the arguments.
            foreach (string target in targets) Bind(copy, i, target, 1, -1);
            return;
        }
        Unknown(copy, i, 1);
    }

    private void Bind(int copy, Instr i, string callee, int first, int receiver = -2)
    {
        if (!_byName.TryGetValue(callee, out Function? target)) { Unknown(copy, i, first); return; }
        List<int> args = new();
        for (int k = first; k < i.Operands.Count; k++) args.Add(Value(copy, i.Operands[k]));
        int dest = i.Dest is null ? -1 : Reg(copy, i.Dest);
        void To(int callee)
        {
            _callers[callee].Add(copy);
            _callees[copy].Add(callee);
            if (i.Op == Opcode.Call)
                (_bindings.TryGetValue((copy, i), out HashSet<int>? reached) ? reached : _bindings[(copy, i)] = new()).Add(callee);
            Function g = _copies[callee].F;
            int self = _copies[callee].Context;
            for (int k = 0; k < args.Count && k < g.Params.Count; k++)
            {
                if (args[k] < 0) continue;
                // THE OBJECT A COPY IS FOR is the only `this` it is handed: the
                // call's other receivers run their own copies, and handed in
                // here they read this one's fields from every other type.
                if (k == 0 && self >= 0)
                {
                    int to = Reg(callee, g.Params[0]);
                    Watch(args[0], l => { if (ObjectOf(l) == self) Add(to, l); });
                }
                else Edge(args[k], Reg(callee, g.Params[k]), 0);
            }
            if (dest >= 0) Edge(ReturnNode(callee), dest, 0);
        }
        if (receiver != -2) To(CopyOf(target, receiver));
        else if (IsInstance(target) && args.Count > 0 && args[0] >= 0)
        {
            HashSet<int> seen = new();
            Watch(args[0], l =>
            {
                int o = OffsetOf(l) == 0 && ObjectOf(l) != Global ? ObjectOf(l) : -1;
                if (seen.Add(o)) To(CopyOf(target, o));
            });
            _unbound.Add((seen, () => To(CopyOf(target, -1))));
        }
        else if (receiver == -2 && !IsInstance(target))
        {
            To(CopyOf(target, CallContext(i, copy, target)));
        }
        else To(CopyOf(target, -1));
    }

    // ---- call contexts --------------------------------------------------------

    // A STATIC FUNCTION THAT HANDS BACK WHAT IT MAKES -- a concatenation, a
    // substring, a list built from a sequence -- is analysed once per call
    // that reaches it, from each copy that makes the call, and what it makes
    // is made once per such call: the string one caller keeps is not the
    // string every caller makes. Calls nest three deep; past that, or past
    // so many in all, a call runs the function's context-free copy, as does
    // a call the class library makes itself from no object's copy, or past
    // so many such contexts of one function: where a boundary's own work
    // calls, it has one.
    private const int MaxHops = 3;
    private const int ContextsPerFunction = 24;
    private const int ContextBudget = 4096;
    private readonly List<(Instr Site, int Caller, int Level, int Hops)> _callContexts = new();
    private readonly Dictionary<(Instr, int), int> _callContextOf = new();
    private readonly Dictionary<Function, int> _contextCount = new();
    private HashSet<string>? _fresh;
    // The copies each direct call reaches, from each copy that makes it; and
    // the object each allocation makes in each copy.
    private readonly Dictionary<(int Copy, Instr Call), HashSet<int>> _bindings = new();
    private readonly Dictionary<(int Copy, Instr Site), int> _madeIn = new();

    // A call context is numbered below -1: -2 the first.
    private static bool IsCallContext(int context) => context < -1;
    private (Instr Site, int Caller, int Level, int Hops) CallContextAt(int context) => _callContexts[-2 - context];

    // How deep an object made in a context is: one below the object an
    // instance method was called on; in a call's, as deep as its caller's.
    private int Level(int context) =>
        context == -1 ? 0 : context >= 0 ? _objects[context].Depth + 1 : CallContextAt(context).Level;

    private int CallContext(Instr call, int caller, Function target)
    {
        if (_callContextOf.TryGetValue((call, caller), out int known)) return known;
        _fresh ??= FreshReturning();
        int from = _copies[caller].Context;
        int hops = IsCallContext(from) ? CallContextAt(from).Hops + 1 : 1;
        // The program's own calls, and the calls beneath them, each have one;
        // the class library's own, only from an object's copy and only so
        // many per function: they are not what a boundary is chosen around.
        bool program = !_copies[caller].F.FromLibrary || IsCallContext(from);
        int made = -1;
        if (_fresh.Contains(target.Name) && hops <= MaxHops && _callContexts.Count < ContextBudget
            && (program || from >= 0 && _contextCount.GetValueOrDefault(target) < ContextsPerFunction))
        {
            if (!program) _contextCount[target] = _contextCount.GetValueOrDefault(target) + 1;
            _callContexts.Add((call, caller, Level(from), hops));
            made = -1 - _callContexts.Count;
        }
        return _callContextOf[(call, caller)] = made;
    }

    /// <summary>
    /// The functions whose return can be an object they made, or one a
    /// function they call made and handed back: what a call context is for.
    /// </summary>
    private HashSet<string> FreshReturning()
    {
        HashSet<string> fresh = new(StringComparer.Ordinal);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Function f in _m.Functions)
                if (!fresh.Contains(f.Name) && ReturnsMade(f, fresh)) { fresh.Add(f.Name); grew = true; }
        }
        return fresh;
    }

    private static bool ReturnsMade(Function f, HashSet<string> fresh)
    {
        HashSet<VReg> made = new();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { } d || made.Contains(d)) continue;
                    bool from = i.Op switch
                    {
                        Opcode.Call => i.Callee is { } c && (Opt.Escape.IsAllocator(c) || fresh.Contains(c)),
                        Opcode.Copy or Opcode.Phi or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32
                            or Opcode.And or Opcode.Or or Opcode.Add or Opcode.Sub =>
                            i.Operands.Any(o => o is RegOperand { Reg: var r } && made.Contains(r)),
                        _ => false,
                    };
                    if (from) { made.Add(d); grew = true; }
                }
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Ret && i.Operands.Count > 0 && i.Operands[0] is RegOperand { Reg: var r } && made.Contains(r)) return true;
        return false;
    }

    private void Unknown(int copy, Instr i, int first)
    {
        _unknownCalls = true;
        for (int k = first; k < i.Operands.Count; k++) Leak(Value(copy, i.Operands[k]));
        if (i.Dest is not null) Add(Reg(copy, i.Dest), Loc(Global, Any));
    }

    /// <summary>The method slot a virtual call reads, from its receiver's own descriptor; null for any other indirect call.</summary>
    private long? VirtualSlot(Function f, Instr i)
    {
        if (i.Operands.Count < 2 || i.Operands[0] is not RegOperand { Reg: var target } || i.Operands[1] is not RegOperand { Reg: var self })
            return null;
        Instr? method = Single(f, target);
        if (method is not { Op: Opcode.Load } || method.Operands[0] is not RegOperand { Reg: var table }) return null;
        Instr? header = Single(f, table);
        if (header is not { Op: Opcode.Load, Offset: 0 } || header.Operands[0] is not RegOperand { Reg: var from } || from != self) return null;
        return method.Offset;
    }

    private readonly Dictionary<Function, Dictionary<VReg, Instr?>> _defs = new();

    // The one instruction that writes `r`, or null.
    private Instr? Single(Function f, VReg r)
    {
        if (!_defs.TryGetValue(f, out Dictionary<VReg, Instr?>? defs))
        {
            _defs[f] = defs = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d) defs[d] = defs.ContainsKey(d) ? null : i;
        }
        return defs.TryGetValue(r, out Instr? one) ? one : null;
    }

    private readonly Dictionary<object, (string, long)?> _stamps = new();

    /// <summary>The descriptor an object is stamped with and where in it its table begins, or null.</summary>
    private (string Table, long At)? Stamp(int o)
    {
        var obj = _objects[o];
        if (obj.F is null) return null;
        object key = (object?)obj.Site ?? obj.Slot!;
        if (key is null) return null;
        if (_stamps.TryGetValue(key, out var known)) return known;
        // ONE STAMP, from one store: the site the only writer of its register
        // (two allocations that share one would name two types), or the
        // slot's own first word. Two different stamps say nothing.
        (string, long)? found = null;
        bool many = false;
        VReg? made = obj.Site?.Dest;
        if (obj.Site is not null && (made is null || Single(obj.F, made) != obj.Site)) return _stamps[key] = null;
        foreach (Block b in obj.F.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Offset == 0 && i.Operands.Count >= 2
                    && i.Operands[1] is SymOperand { Name: var t, Offset: var at }
                    && (obj.Slot is not null ? i.Operands[0] is SlotOperand { Slot: var s0 } && s0 == obj.Slot
                        : i.Operands[0] is RegOperand { Reg: var to } && Derives(obj.F, to, made!, 0)))
                {
                    if (found is { } f0 && (f0.Item1 != t || f0.Item2 != at)) many = true;
                    found = (t, at);
                }
        return _stamps[key] = many ? null : found;
    }

    private bool Derives(Function f, VReg r, VReg made, int depth)
    {
        if (r == made) return true;
        if (depth > 3) return false;
        return Single(f, r) is { Op: Opcode.Trunc64 or Opcode.Copy } w && w.Operands[0] is RegOperand { Reg: var from }
            && Derives(f, from, made, depth + 1);
    }

    // ---- solving ------------------------------------------------------------

    /// <summary>The analysis has outgrown its budget: it stops, and nothing is rewritten.</summary>
    private sealed class OverBudget : Exception { }

    private bool Solve()
    {
        try { return SolveSteps(); }
        catch (OverBudget) { return false; }
    }

    // Past the heap budget. Counted without a collection, the heap is garbage
    // too: what is still held after one decides, and the next look waits for
    // garbage to pile up again.
    private bool OverHeap()
    {
        if (GC.GetTotalMemory(false) - _heapAtStart <= _heapLimit) return false;
        long live = GC.GetTotalMemory(true) - _heapAtStart;
        if (live > HeapBudget) return true;
        _heapLimit = Math.Max(HeapBudget, live + HeapBudget / 4);
        return false;
    }

    private bool SolveSteps()
    {
        while (_work.TryDequeue(out int node))
        {
            if (_pts.Count > NodeBudget || _held > HeldBudget) return false;
            if ((_steps & 63) == 0 && OverHeap()) return false;
            if (++_steps % 500_000 == 0)
                Console.Error.WriteLine($"regions: step {_steps}: {_pts.Count} nodes, {_copies.Count} copies, {_objects.Count} objects, {_work.Count} waiting");
            if (_edgeCount >= _nextMerge) MergeCycles();
            // A node merged into another since it was queued: its delta went too.
            if (_delta[node] is not { } delta) continue;
            _delta[node] = null;
            // Merges owe a node what each side lacked, often the same
            // locations many times over: each is carried once.
            if (_owedTwice.Remove(node) || delta.Count > _pts[node].Count) delta = Distinct(delta);
            if (_edges[node] is { } edges)
                for (int e = 0; e < edges.Count; e++)
                {
                    (int to, long shift) = edges[e];
                    if (shift == 0) foreach (int id in delta) Held(to, id);
                    else foreach (int id in delta) Add(to, Shifted(_locs[id], shift));
                }
            if (_watchers[node] is { } watchers)
                foreach (int id in delta)
                    for (int w = 0; w < watchers.Count; w++) watchers[w](_locs[id]);
        }
        return true;
    }

    // ---- cycles -------------------------------------------------------------

    // Each node's representative: nodes on a cycle of plain edges hold the
    // same locations, and are merged into one (union-find, path halving).
    private readonly List<int> _rep = new();
    // A merged node's set while solving; its representative's once solved.
    private static readonly LocSet Merged = new(new List<long>());
    private long _edgeCount, _nextMerge = 20_000;
    private readonly HashSet<int> _owedTwice = new();
    private int[] _seenAt = Array.Empty<int>();
    private int _seenStamp;

    private List<int> Distinct(List<int> ids)
    {
        if (_seenAt.Length < _locs.Count) Array.Resize(ref _seenAt, _locs.Count * 2);
        if (++_seenStamp == int.MaxValue) { Array.Clear(_seenAt); _seenStamp = 1; }
        List<int> once = new(ids.Count);
        foreach (int id in ids)
            if (_seenAt[id] != _seenStamp) { _seenAt[id] = _seenStamp; once.Add(id); }
        return once;
    }

    private int Find(int n)
    {
        while (_rep[n] != n) { _rep[n] = _rep[_rep[n]]; n = _rep[n]; }
        return n;
    }

    /// <summary>
    /// CYCLES OF PLAIN EDGES ARE ONE NODE. Whatever reaches one node of such
    /// a cycle reaches every other, so each holds the same set: thousands of
    /// copies of one set of locations, each carried edge by edge, until they
    /// are one. Found (Tarjan's, over representatives) each time the edges
    /// have grown by a quarter.
    /// </summary>
    private void MergeCycles()
    {
        _nextMerge = _edgeCount + Math.Max(20_000, _edgeCount / 4);
        int n = _pts.Count;
        int[] index = new int[n], low = new int[n];
        bool[] onStack = new bool[n];
        Stack<int> stack = new();
        Stack<(int Node, int Edge)> calls = new();
        int counter = 0;
        for (int root = 0; root < n; root++)
        {
            if (index[root] != 0 || _rep[root] != root || _edges[root] is null) continue;
            calls.Push((root, 0));
            index[root] = low[root] = ++counter;
            stack.Push(root);
            onStack[root] = true;
            while (calls.Count > 0)
            {
                (int v, int e) = calls.Pop();
                List<(int To, long Shift)>? edges = _edges[v];
                bool descended = false;
                while (edges is not null && e < edges.Count)
                {
                    (int to, long shift) = edges[e++];
                    if (shift != 0) continue;
                    int w = Find(to);
                    // A cell that holds no reference is never merged: it holds
                    // less than what feeds it.
                    if (w == v || _noReference[w]) continue;
                    if (index[w] == 0)
                    {
                        calls.Push((v, e));
                        calls.Push((w, 0));
                        index[w] = low[w] = ++counter;
                        stack.Push(w);
                        onStack[w] = true;
                        descended = true;
                        break;
                    }
                    if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                }
                if (descended) continue;
                if (low[v] == index[v])
                {
                    int w;
                    do
                    {
                        w = stack.Pop();
                        onStack[w] = false;
                        if (w != v) Merge(w, v);
                    } while (w != v);
                }
                if (calls.Count > 0) { int parent = calls.Peek().Node; low[parent] = Math.Min(low[parent], low[v]); }
            }
        }
    }

    // Node `from` becomes part of `into`: its locations, edges and watchers
    // move, and each side's edges and watchers are owed what only the other
    // held, along with what either still had to carry.
    private void Merge(int from, int into)
    {
        _rep[from] = into;
        LocSet a = _pts[from], b = _pts[into];
        List<int> owed = new();
        foreach (int id in a.Ids()) if (!b.Contains(id)) owed.Add(id);
        foreach (int id in b.Ids()) if (!a.Contains(id)) owed.Add(id);
        _held -= a.Count;
        foreach (int id in a.Ids()) if (b.Add(id)) _held++;
        if (_delta[from] is { } pending) owed.AddRange(pending);
        _delta[from] = null;
        _pts[from] = Merged;
        if (owed.Count > 0)
        {
            if (_delta[into] is not { } delta) { _delta[into] = delta = new(); _work.Enqueue(into); }
            else _owedTwice.Add(into);
            delta.AddRange(owed);
        }
        if (_edges[from] is { } edges)
            foreach (var edge in edges)
            {
                int to = Find(edge.To);
                if (!(to == into && edge.Shift == 0)) NewEdge(into, to, edge.Shift);
            }
        _edges[from] = null;
        _edgeSet[from] = null;
        if (_watchers[from] is { } watchers) (_watchers[into] ??= new()).AddRange(watchers);
        _watchers[from] = null;
    }

    /// <summary>
    /// A SET OF LOCATIONS, by number: a few in a short array, past that a bit
    /// for every location the pass has named. Thousands of nodes hold the
    /// same few hundred objects; a bit apiece is what that costs to hold.
    /// </summary>
    private sealed class LocSet : IEnumerable<long>
    {
        private const int Short = 32;
        private readonly List<long> _named;
        private int[]? _few;
        private ulong[]? _bits;

        public LocSet(List<long> named) => _named = named;

        public int Count { get; private set; }
        public int[]? Few => _few;
        public ulong[]? Bits => _bits;

        public bool Contains(int id) => _bits is { } bits
            ? id >> 6 < bits.Length && (bits[id >> 6] & 1UL << id) != 0
            : _few is { } few && Array.IndexOf(few, id, 0, Count) >= 0;

        public bool Add(int id)
        {
            if (_bits is null)
            {
                if (_few is { } few && Array.IndexOf(few, id, 0, Count) >= 0) return false;
                if (Count < Short)
                {
                    if (_few is null || Count == _few.Length) Array.Resize(ref _few, _few is null ? 4 : _few.Length * 2);
                    _few[Count++] = id;
                    return true;
                }
                _bits = new ulong[Math.Max(id, _named.Count) / 64 + 1];
                for (int k = 0; k < Count; k++) _bits[_few![k] >> 6] |= 1UL << _few[k];
                _few = null;
            }
            if (id >> 6 >= _bits.Length) Array.Resize(ref _bits, Math.Max(id >> 6, _named.Count / 64) + 1);
            ref ulong word = ref _bits[id >> 6];
            if ((word & 1UL << id) != 0) return false;
            word |= 1UL << id;
            Count++;
            return true;
        }

        /// <summary>The numbers held, as they are now: adding while walking them is safe.</summary>
        public int[] Ids()
        {
            int[] ids = new int[Count];
            if (_bits is null) { if (Count > 0) Array.Copy(_few!, ids, Count); return ids; }
            int n = 0;
            for (int w = 0; w < _bits.Length; w++)
                for (ulong word = _bits[w]; word != 0; word &= word - 1)
                    ids[n++] = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(word);
            return ids;
        }

        public IEnumerator<long> GetEnumerator()
        {
            foreach (int id in Ids()) yield return _named[id];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---- judging ------------------------------------------------------------

    private void Judge()
    {
        string[] wanted = Report.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long locations = 0;
        foreach (LocSet p in _pts) locations += p.Count;
        Console.Error.WriteLine($"regions: {_copies.Count} function copies, {_objects.Count} objects, {_pts.Count} nodes, {locations} locations held, {_steps} steps");
        Console.Error.WriteLine($"regions: {_callContexts.Count} call contexts");
        for (int c = 0; c < _copies.Count; c++)
        {
            (Function f, int context) = _copies[c];
            if (!wanted.Any(w => f.Name.Contains(w, StringComparison.Ordinal))) continue;
            HashSet<int> reached = Outliving(c);

            // What is made beneath it: in the copies it calls, transitively.
            HashSet<int> beneath = Beneath(c);
            int local = 0, kept = 0;
            SortedSet<string> lines = new(StringComparer.Ordinal);
            for (int o = 1; o < _objects.Count; o++)
            {
                var obj = _objects[o];
                if (obj.Site is null || !beneath.Contains(CopyIdOfObject(o))) continue;
                bool outlives = reached.Contains(o);
                if (outlives) kept++; else local++;
                lines.Add($"  {(outlives ? "outlives" : "local   ")} {obj.F!.Name} line {obj.Site.Line} {TypeOf(o)}{(IsCallContext(obj.Context) ? " called from line " + CallContextAt(obj.Context).Site.Line : "")}");
            }
            Console.Error.WriteLine($"regions: boundary {f.Name} ctx {context}: {local} local, {kept} outlive it");
            foreach (string line in lines) Console.Error.WriteLine(line);
        }
    }

    // The copy an object was made in: its function in its context.
    private int CopyIdOfObject(int o)
    {
        var obj = _objects[o];
        return _copyIds.TryGetValue((obj.F!, IsInstance(obj.F!) || IsCallContext(obj.Context) ? obj.Context : -1), out int c) ? c
            : _copyIds.TryGetValue((obj.F!, -1), out int d) ? d : -1;
    }

    // The copies a copy calls, transitively, and itself.
    private HashSet<int> Beneath(int start)
    {
        HashSet<int> seen = new() { start };
        Queue<int> next = new();
        next.Enqueue(start);
        while (next.TryDequeue(out int c))
            foreach (int callee in _callees[c])
                if (seen.Add(callee)) next.Enqueue(callee);
        return seen;
    }

    // The type an object is stamped with, if its making stores one.
    private string TypeOf(int o) => Stamp(o) is var (t, _) ? t
        : _objects[o].Site?.Callee == Opt.Escape.LeafAllocator ? "leaf" : "block";
}
