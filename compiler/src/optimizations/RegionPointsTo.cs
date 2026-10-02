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
/// every List's. Contexts nest two deep; deeper ones are merged into the
/// method's context-free copy. A virtual call runs, for each object it is
/// made on, the method that object's own descriptor names.
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

    // GIVING UP IS SOUND: nothing is rewritten. Past this many locations held
    // in all, the program is more than this analysis answers in the memory a
    // compile has, and it stops rather than taking the compiler down.
    private const long HeldBudget = 2_000_000;
    private long _held;
    // And past this much more heap than the compile had when the pass began,
    // whatever holds it: edges, watchers, pairs of copies.
    private const long HeapBudget = 200L * 1024 * 1024;
    private long _heapAtStart;

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
    private readonly List<HashSet<long>> _pts = new();
    private readonly List<List<(int To, long Shift)>?> _edges = new();
    private readonly List<HashSet<(int, long)>?> _edgeSet = new();
    private readonly List<List<Action<long>>?> _watchers = new();
    private readonly List<List<long>?> _delta = new();
    private readonly Queue<int> _work = new();
    private long _steps;

    // Past this an offset is any offset: no field lies that far into an
    // object, and a pointer walked further is walking an array.
    private const long FarthestField = 4096;

    private static long Loc(int o, long offset) => ((long)o << 24) | (offset is < 0 or > FarthestField ? Any : offset);
    private static int ObjectOf(long loc) => (int)(loc >> 24);
    private static long OffsetOf(long loc) => loc & Any;
    private static long Shifted(long loc, long shift) =>
        shift == long.MinValue || OffsetOf(loc) == Any ? Loc(ObjectOf(loc), Any) : Loc(ObjectOf(loc), OffsetOf(loc) + shift);

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

        // Each site's objects, and the boundaries each is made beneath.
        Dictionary<Instr, List<int>> bySite = new(ReferenceEqualityComparer.Instance);
        for (int o = 1; o < _objects.Count; o++)
            if (_objects[o].Site is { } site) (bySite.TryGetValue(site, out List<int>? l) ? l : bySite[site] = new()).Add(o);

        int near = 0, inRegion = 0;
        Dictionary<Instr, string> chosen = new(ReferenceEqualityComparer.Instance);
        foreach ((Instr site, List<int> objects) in bySite)
        {
            if (site.Callee is not (Opt.Escape.Allocator or Opt.Escape.LeafAllocator or Opt.Escape.ObjectAllocator)) continue;
            Function f = _objects[objects[0]].F!;
            bool beside = IsInstance(f) && objects.Any(o => _objects[o].Context >= 0);
            bool anywhere = false, ok = true;
            foreach (int o in objects)
            {
                int copy = CopyIdOfObject(o);
                int context = _objects[o].Context;
                for (int k = 0; k < boundaries.Count && ok; k++)
                {
                    if (!judged[k].Beneath.Contains(copy)) continue;
                    anywhere = true;
                    // Beside an object that is never in this boundary's
                    // region: never in it either, so nothing to prove.
                    if (beside && context >= 0 && judged[k].Outlives.Contains(context)) continue;
                    if (judged[k].Outlives.Contains(o)) ok = false;
                }
                if (!ok) break;
            }
            if (!ok || !anywhere) continue;
            chosen[site] = beside ? Near : InRegion;
            if (Report is not null) Console.Error.WriteLine($"regions: {(beside ? "beside" : "region")} {f.Name} line {site.Line} {TypeOf(objects[0])}");
            if (beside) near++; else inRegion++;
        }

        foreach (Function f in _m.Functions)
            foreach (Block b in f.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                    if (chosen.TryGetValue(b.Instrs[k], out string? helper))
                    {
                        // The allocating function's own frame: a region a throw
                        // left below it is closed before this one is used.
                        VReg frame = f.NewReg(IrTypes.Word, "allocframe");
                        b.Instrs.Insert(k, new Instr { Op = Opcode.FramePointer, Dest = frame, Line = b.Instrs[k].Line });
                        k++;
                        b.Instrs[k] = Retarget(f, b.Instrs[k], helper, frame);
                    }

        HashSet<Function> opened = new();
        foreach (int c in boundaries)
            if (opened.Add(_copies[c].F)) Open(_copies[c].F);
        Console.Error.WriteLine($"regions: {opened.Count} boundaries, {inRegion} sites in the innermost region, {near} beside their object");
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
        HashSet<int> reached = new();
        Queue<int> next = new();
        void Reach(long l) { if (reached.Add(ObjectOf(l))) next.Enqueue(ObjectOf(l)); }
        Reach(Loc(Global, Any));
        foreach (VReg p in f.Params) foreach (long l in _pts[Reg(c, p)]) Reach(l);
        foreach (long l in _pts[ReturnNode(c)]) Reach(l);
        while (next.TryDequeue(out int o))
        {
            foreach (int cell in _cells[o].Values)
                foreach (long l in _pts[cell]) Reach(l);
            // A STATE MACHINE KEEPS WHAT ITS BODY SAVES across a suspension:
            // the stores AsyncTransform writes later, followed now.
            if (KeptBy().TryGetValue(o, out List<int>? machines))
                foreach (int k in machines)
                {
                    foreach (int saved in _machines[k].Saved) foreach (long l in _pts[saved]) Reach(l);
                    foreach (int slot in _machines[k].Slots) Reach(Loc(slot, 0));
                }
        }
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
        _pts.Add(new HashSet<long>());
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
        return _objects.Count - 1;
    }

    /// <summary>The node of one of an object's cells, made on first use.</summary>
    private int Cell(int o, long offset)
    {
        if (offset is < 0 or > FarthestField) offset = Any;
        Dictionary<long, int> cells = _cells[o];
        if (cells.TryGetValue(offset, out int node)) return node;
        node = NewNode();
        cells[offset] = node;
        // A cell read at any offset reads this one too; one written at any
        // offset is read wherever this one is.
        if (offset != Any)
        {
            Edge(Cell(o, Any), node, 0);
            if (_cellWatchers[o] is { } watchers) for (int w = 0; w < watchers.Count; w++) watchers[w](offset, node);
        }
        return node;
    }

    /// <summary>Run `act` for every cell of an object, now and later.</summary>
    private void EachCell(int o, Action<long, int> act)
    {
        (_cellWatchers[o] ??= new()).Add(act);
        foreach (var (offset, node) in _cells[o].ToArray()) if (offset != Any) act(offset, node);
    }

    private int SiteObject(Instr site, Function f, int context)
    {
        int depth = context < 0 ? 0 : _objects[context].Depth + 1;
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
        if (!IsInstance(f)) context = -1;
        else if (context >= 0 && (_objects[context].Site is null || _objects[context].Depth >= MaxDepth)) context = -1;
        if (_copyIds.TryGetValue((f, context), out int known)) return known;
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
        if (from == to && shift == 0) return;
        if (!(_edgeSet[from] ??= new()).Add((to, shift))) return;
        (_edges[from] ??= new()).Add((to, shift));
        foreach (long l in _pts[from]) Add(to, shift == 0 ? l : Shifted(l, shift));
    }

    private void Add(int node, long loc)
    {
        if (!_pts[node].Add(loc)) return;
        // Checked here, not only between steps: one step's watchers can add
        // without end (a copy between two growing sets), and the compile died
        // inside it before the loop looked again.
        if ((++_held & 4095) == 0 && (_held > HeldBudget || GC.GetTotalMemory(false) - _heapAtStart > HeapBudget))
            throw new OverBudget();
        List<long>? delta = _delta[node];
        if (delta is null) { _delta[node] = delta = new(); _work.Enqueue(node); }
        delta.Add(loc);
    }

    /// <summary>Run `act` for every location `node` holds, now and later.</summary>
    private void Watch(int node, Action<long> act)
    {
        (_watchers[node] ??= new()).Add(act);
        foreach (long l in _pts[node].ToArray()) act(l);
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
            Edge(Cell(o, at), dest, 0);
            if (at == Any) EachCell(o, (_, cell) => Edge(cell, dest, 0));
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
        HashSet<(long, long)> pairs = new();
        Watch(from, src => Watch(to, dst =>
        {
            if (!pairs.Add((src, dst))) return;
            int os = ObjectOf(src), od = ObjectOf(dst);
            long ds = OffsetOf(src), dd = OffsetOf(dst);
            if (os == Global)
            {
                Add(Cell(od, Any), Loc(Global, Any));
                return;
            }
            bool exact = ds != Any && dd != Any && od != Global;
            EachCell(os, (offset, cell) =>
            {
                if (!exact) { Edge(cell, Cell(od, Any), 0); return; }
                if (offset >= ds && (count == Any || offset - ds < count)) Edge(cell, Cell(od, dd + offset - ds), 0);
            });
            Edge(Cell(os, Any), Cell(od, Any), 0);
        }));
    }

    private void Call(int copy, Function f, int context, Instr i)
    {
        string? callee = i.Callee;
        if (callee is null) { Unknown(copy, i, 0); return; }
        if (Opt.Escape.IsAllocator(callee) || callee == Opt.Escape.ManualAllocator || callee == Opt.Escape.ManualObjectAllocator)
        {
            if (i.Dest is not null) Add(Reg(copy, i.Dest), Loc(SiteObject(i, f, context), 0));
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
            Function g = _copies[callee].F;
            for (int k = 0; k < args.Count && k < g.Params.Count; k++)
                if (args[k] >= 0) Edge(args[k], Reg(callee, g.Params[k]), 0);
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
        else To(CopyOf(target, -1));
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

    private bool SolveSteps()
    {
        while (_work.TryDequeue(out int node))
        {
            if (_pts.Count > NodeBudget || _held > HeldBudget) return false;
            if ((_steps & 63) == 0 && GC.GetTotalMemory(false) - _heapAtStart > HeapBudget) return false;
            if (++_steps % 500_000 == 0)
                Console.Error.WriteLine($"regions: step {_steps}: {_pts.Count} nodes, {_copies.Count} copies, {_objects.Count} objects, {_work.Count} waiting");
            List<long> delta = _delta[node]!;
            _delta[node] = null;
            foreach (long l in delta)
            {
                if (_edges[node] is { } edges)
                    for (int e = 0; e < edges.Count; e++)
                        Add(edges[e].To, edges[e].Shift == 0 ? l : Shifted(l, edges[e].Shift));
                if (_watchers[node] is { } watchers) for (int w = 0; w < watchers.Count; w++) watchers[w](l);
            }
        }
        return true;
    }

    // ---- judging ------------------------------------------------------------

    private void Judge()
    {
        string[] wanted = Report.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long locations = 0;
        foreach (HashSet<long> p in _pts) locations += p.Count;
        Console.Error.WriteLine($"regions: {_copies.Count} function copies, {_objects.Count} objects, {_pts.Count} nodes, {locations} locations held, {_steps} steps");
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
                lines.Add($"  {(outlives ? "outlives" : "local   ")} {obj.F!.Name} line {obj.Site.Line} {TypeOf(o)}");
            }
            Console.Error.WriteLine($"regions: boundary {f.Name} ctx {context}: {local} local, {kept} outlive it");
            foreach (string line in lines) Console.Error.WriteLine(line);
        }
    }

    // The copy an object was made in: its function in its context.
    private int CopyIdOfObject(int o)
    {
        var obj = _objects[o];
        return _copyIds.TryGetValue((obj.F!, IsInstance(obj.F!) ? obj.Context : -1), out int c) ? c
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
