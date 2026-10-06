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
    public const string Catch = "m_Runtime_RegionCatch_1_V$NInt";
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
    // And a node holding what a read at any offset reads (ReadAnywhere):
    // every cell but a stamped object's stamp (-1 until asked for).
    private readonly List<int> _readCells = new();

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

    private static long Loc(int o, long offset) => ((long)o << 24) | (offset is < 0 || offset > FarthestField && !IsStrided(offset) ? Any : offset);

    /// <summary>
    /// AN ELEMENT'S WORD IN AN ARRAY OF STRUCTS: an address made by adding an
    /// index scaled by 2^k (`shl i k`) to an array's is the array's word at
    /// some offset congruent to a residue mod 2^k -- not anywhere in it. An
    /// array of `(int At, Tok Was)` read at any offset mixed the number with
    /// the reference: the number read back held every Tok, and a sum made of
    /// it kept them all; a Tok written back through one the unknown object.
    /// Offsets past FarthestField with this bit name such a word: the stride's
    /// shift and the residue. A plain offset into the array's data, and
    /// another such word, that may be the same word are read and written as
    /// one (Cell).
    /// </summary>
    private const long StrideBit = 0x100000;
    private const int MostStrideShift = Corsac.Lang.Lto.RegionConstraint.MostScale;
    private static bool IsStrided(long offset) => offset != Any && (offset & StrideBit) != 0;
    // A plain offset worked out: past the farthest field, any offset -- never
    // a value that happens to carry StrideBit.
    private static long Plain(long offset) => offset < 0 || offset > FarthestField ? Any : offset;
    private static long Strided(int shift, long residue) => StrideBit | ((long)shift << 12) | (residue & ((1L << shift) - 1));
    private static int StrideShiftOf(long offset) => (int)((offset >> 12) & 0xFF);
    private static long ResidueOf(long offset) => offset & 0xFFF;
    // A strided word moved by a constant: the same stride, the residue moved.
    private static long StridedPlus(long offset, long by)
    {
        int k = StrideShiftOf(offset);
        long s = 1L << k;
        return Strided(k, ((ResidueOf(offset) + by) % s + s) % s);
    }
    // Whether two offsets of one object may name the same word.
    private static bool MayOverlap(long a, long b, long firstData)
    {
        if (a == b || a == Any || b == Any) return a == b;
        bool sa = IsStrided(a), sb = IsStrided(b);
        if (!sa && !sb) return false;
        if (sa && sb)
        {
            long m = 1L << Math.Min(StrideShiftOf(a), StrideShiftOf(b));
            return ((ResidueOf(a) - ResidueOf(b)) % m + m) % m == 0;
        }
        long strided = sa ? a : b, plain = sa ? b : a;
        // In an array, only a word of its data: an element is never the
        // stamp or the length. Anywhere in anything else.
        return plain >= firstData && (plain & ((1L << StrideShiftOf(strided)) - 1)) == ResidueOf(strided);
    }
    private static int ObjectOf(long loc) => (int)(loc >> 24);
    private static long OffsetOf(long loc) => loc & Any;
    // A pointer already inside an object moved again is anywhere in it: a
    // field's address is the object's moved once, and a count that once held
    // a pointer, stepped through memory where Walked cannot see the loop,
    // made every object a location per step.
    private static long Shifted(long loc, long shift)
    {
        int o = ObjectOf(loc);
        long off = OffsetOf(loc);
        if (shift == long.MinValue || off == Any) return Loc(o, Any);
        if (IsIndexShift(shift) || IsMovedBy(shift))
        {
            int k = (int)(shift - (IsMovedBy(shift) ? MovedBy : IndexShift));
            if (k < 1 || k > MostStrideShift) return Loc(o, Any);
            if (!IsStrided(off)) return Loc(o, Strided(k, off));
            int kk = Math.Min(k, StrideShiftOf(off));
            return Loc(o, Strided(kk, ResidueOf(off)));
        }
        if (IsStrided(off)) return Loc(o, StridedPlus(off, shift));
        return off is not 0 ? Loc(o, Any) : Loc(o, Plain(shift));
    }

    // An index scaled by 2^k is IndexShift + k on an edge; IndexShift alone
    // an index of no known scale. What it is added to moves by MovedBy + k:
    // to a residue of 2^k, the unknown object kept (an index is never one).
    private static bool IsIndexShift(long shift) => shift >= IndexShift && shift <= IndexShift + MostStrideShift;
    private const long MovedBy = Corsac.Lang.Lto.RegionConstraint.MovedBy;
    private static bool IsMovedBy(long shift) => shift > MovedBy && shift <= MovedBy + MostStrideShift;

    /// <summary>
    /// AN INDEX ADDED TO AN ADDRESS: an operand of an addition of two
    /// registers made by shifting left by a constant -- an element's index
    /// scaled by its size -- moves the address anywhere in its object, and is
    /// never the address: a pointer shifted left is no pointer until it is
    /// shifted right again, and what is shifted right (or multiplied) holds
    /// nothing already (Constrain's default). What the index holds goes on
    /// (a value tagged and kept), but not the unknown object: a count read
    /// from where nobody follows, or a number an array of tuples holds beside
    /// a reference, made every element written at it written where nobody
    /// follows (1180's split positions). RegionConstraint.Index at the link.
    /// </summary>
    private const long IndexShift = Corsac.Lang.Lto.RegionConstraint.Index;

    // A location carried along an edge: moved, or dropped (IndexShift).
    private void AddShifted(int to, long loc, long shift)
    {
        if (IsIndexShift(shift) && ObjectOf(loc) == Global) return;
        Add(to, Shifted(loc, shift));
    }

    /// <summary>Whether operand `o` of addition `i` is an index scaled into an address (IndexShift).</summary>
    private bool ScaledIndex(Function f, Instr i, Operand o) => IndexScale(f, i, o) > 0;

    // The k of an index scaled by 2^k, or 0.
    private int IndexScale(Function f, Instr i, Operand o) =>
        i.Operands.Count == 2 && i.Operands.All(x => x is RegOperand) && o is RegOperand { Reg: var r }
        && Single(f, r) is { Op: Opcode.Shl, Operands: [RegOperand, ImmOperand { Value: > 0 and var k }] } ? (int)Math.Min(k, MostStrideShift + 1) : 0;

    public void Run(Module m)
    {
        // A UNIT THE LINK REGENERATES with the whole program's answer
        // (Lto.RegionSolver): nothing to solve here, only to apply.
        if (m.RegionFacts is { } facts)
        {
            ApplyFacts(m, facts, Report is not null);
            return;
        }
        if (m.Entry is null) return;
        if (new[] { Enter, Leave, InRegion, Near, Catch }.Where(h => !m.Functions.Any(f => f.Name == h)).ToList() is { Count: > 0 } missing)
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
            // By index: a bind makes copies, and their instance calls are
            // unbound too.
            try
            {
                for (int k = 0; k < _unbound.Count; k++)
                    if (_unbound[k].Seen.Count == 0) { _unbound[k].Seen.Add(-3); _unbound[k].Bind(); more = true; }
            }
            catch (OverBudget)
            {
                if (Report is not null) Console.Error.WriteLine("regions: gave up while building the constraints");
                return;
            }
            // CODE THIS NEVER SAW CALL may call anything whose address it was
            // given, with anything: once any call goes where this cannot
            // follow, every function whose address is taken is called from
            // there too.
            if (_unknownCalls && !rooted)
            {
                rooted = true;
                try
                {
                    foreach (Function f in AddressTaken(m))
                    {
                        int copy = CopyOf(f, -1);
                        for (int k = 0; k < f.Params.Count; k++) Add(Reg(copy, f.Params[k]), Loc(Global, Any));
                        Edge(ReturnNode(copy), Cell(Global, Any), 0);
                    }
                }
                catch (OverBudget)
                {
                    if (Report is not null) Console.Error.WriteLine("regions: gave up while building the constraints");
                    return;
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
    /// THE LOOPS THE LINK GAVE A REGION, marked on the same IR: each header
    /// by its place among its function's blocks, which the unit's compile
    /// named it by in its RegionHints (RegionSummary), over the same IR. The
    /// function is never inlined from here on, as OpenLoops leaves it: the
    /// loop the link judged is its own. A header the late passes take away,
    /// or that heads no loop by the time the region pass opens them, opens
    /// nothing, and that is sound: what a lap leaves dead is dead by the end
    /// of whatever region is open outside it.
    /// </summary>
    public static int MarkLoops(Module m, Corsac.Lang.Lto.RegionFacts facts)
    {
        int marked = 0;
        if (facts.Loops.Count == 0) return 0;
        foreach (Function f in m.Functions)
        {
            var named = facts.Loops.GetViewBetween((f.Name, int.MinValue), (f.Name, int.MaxValue));
            if (named.Count == 0) continue;
            // Each a loop's header here too, or the IR is not the one judged.
            HashSet<int> headers = Shapes(f).Select(loop => loop.Header).ToHashSet();
            foreach ((string _, int header) in named)
                if (headers.Contains(header))
                {
                    f.Blocks[header].RegionLoop = true;
                    f.Blocks[header].RegionLoopBytes = facts.LoopBytes.GetValueOrDefault((f.Name, header));
                    f.NoInlining = true;
                    marked++;
                }
        }
        return marked;
    }

    /// <summary>
    /// The sites the link chose of another unit's body it brought in to
    /// inline (IrImport.RegionSites), marked as its own unit's are: by
    /// ordinal, over the same IR its unit numbered them on.
    /// </summary>
    public static int MarkSites(Function f, IReadOnlyCollection<int> ordinals)
    {
        int marked = 0, ordinal = 0;
        if (ordinals.Count == 0) return 0;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (IsSiteCall(i))
                {
                    if (ordinals.Contains(ordinal)) { i.RegionSite = true; marked++; }
                    ordinal++;
                }
        return marked;
    }

    /// <summary>
    /// The link's answer applied: every boundary named made one. Its marked
    /// sites stay calls of the collecting allocators until the regenerated
    /// unit's last lifetime run is done (MakeSitesInRegion): the link brings
    /// other units' bodies in after these passes, and its lifetime rules
    /// then see whole objects nothing here could follow -- a stream whose
    /// constructor and Dispose are the library's -- and place them in the
    /// frame or free them where they die, which they can do only to an
    /// allocator's call. A region is the last resort before the collector,
    /// not the first. Whatever happens to a site before then -- placed in a
    /// frame, taken apart -- it is no longer a call, and nothing is made of it.
    /// </summary>
    private static void ApplyFacts(Module m, Corsac.Lang.Lto.RegionFacts facts, bool report)
    {
        int sites = 0, opened = 0, loops = 0;
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.RegionSite && i.Op == Opcode.Call && IsRewritable(i.Callee)) sites++;
        foreach (Function f in m.Functions)
        {
            List<int> headers = MarkedLoops(f, out Dictionary<int, long> lapBytes);
            if (facts.Boundaries.Contains(f.Name)) { Open(f, headers.Count > 0, facts.BoundaryBytes.GetValueOrDefault(f.Name)); opened++; }
            if (headers.Count > 0) { OpenLoops(f, headers, lapBytes); loops += headers.Count; }
        }
        if (sites > 0 || opened > 0 || loops > 0) CatchUp(m);
        if ((sites > 0 || opened > 0 || loops > 0) && report)
            Console.Error.WriteLine($"regions: {opened} boundaries, {loops} loops, {sites} sites in the innermost region, from the link");
    }

    /// <summary>
    /// The loops of a function the link marked (MarkLoops) that still head a
    /// loop, by their place in its blocks now -- none where the late passes
    /// brought a landing pad or a label's address in, as the link's own
    /// judgement would have given none (RegionSummary). With each, the most
    /// one lap makes in it (Block.RegionLoopBytes).
    /// </summary>
    private static List<int> MarkedLoops(Function f, out Dictionary<int, long> lapBytes)
    {
        List<int> headers = new();
        lapBytes = new();
        if (!f.Blocks.Any(b => b.RegionLoop)) return headers;
        if (f.Async is null && f.Blocks.Count <= LoopBlocks && !f.Blocks.Any(b => b.IsLandingPad || b.Instrs.Any(i => i.Op == Opcode.LabelAddr)))
        {
            Cfg cfg = new(f);
            foreach ((Block header, _, _) in NaturalLoops(f, cfg))
                if (header.RegionLoop && !cfg.IsRoot(header))
                {
                    headers.Add(header.Order);
                    lapBytes[header.Order] = header.RegionLoopBytes;
                }
        }
        foreach (Block b in f.Blocks) { b.RegionLoop = false; b.RegionLoopBytes = 0; }
        return headers;
    }

    /// <summary>
    /// Every marked site of <paramref name="f"/> still a collecting
    /// allocator's call made in the innermost open region (ApplyFacts):
    /// what the link's lifetime rules left of the sites the link chose.
    /// </summary>
    public static int MakeSitesInRegion(Function f)
    {
        int sites = 0;
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (!i.RegionSite || i.Op != Opcode.Call || !IsRewritable(i.Callee)) continue;
                VReg frame = f.NewReg(IrTypes.Word, "allocframe");
                b.Instrs.Insert(k, new Instr { Op = Opcode.FramePointer, Dest = frame, Line = i.Line });
                k++;
                List<Instr> retargeted = Retarget(f, i, InRegion, frame);
                b.Instrs.RemoveAt(k);
                b.Instrs.InsertRange(k, retargeted);
                k += retargeted.Count - 1;
                sites++;
            }
        // And the pads of what the link brought in and inlined since the
        // unit's own catch-up: a catch from another unit's body closes the
        // regions it caught out of too.
        CatchUp(f);
        return sites;
    }

    /// <summary>
    /// STORAGE MADE BESIDE ITS OWNER. A collection made in a region grows
    /// inside its own methods, whose allocations no boundary proves anything
    /// of: every array a List or a Dictionary grew into was the heap's, and
    /// the collector's when the region ended. But an array stored into a
    /// field the link found owns what it holds (OwnedFieldFacts) -- every
    /// object stored there made for it and kept nowhere else, every read of
    /// it going nowhere and dead before anything could replace it -- is
    /// reachable through that field alone, so it is dead whenever the object
    /// holding the field is. Made beside that object (AllocNear), it is in
    /// the object's region when that is the innermost one open, and on the
    /// heap otherwise: given back with the region, at the latest, with the
    /// object it belongs to.
    ///
    /// Only fields the collection frees itself as it replaces them (SelfFreed,
    /// Escape's self-replacing frees): their old values are given back by
    /// the collection's own free, as an owned field's old value
    /// (Runtime.FreeOwnedReplaced) -- in a region at once where it is the
    /// top (Gc.RegionFree), else with its region, and dead either way, every
    /// read of an owned field being dead before the store that replaces it;
    /// no other free is ever handed one. The owner is found here, on the IR as it is
    /// now, not as the link saw it -- the body may have been inlined anywhere
    /// since: the allocation's value, through copies, is stored only into such
    /// fields, all of one object, held by a register written once (or a
    /// parameter never written) whose value is there before the allocation is
    /// made. Anything else stays where it was, on the heap.
    ///
    /// A CONSTRUCTOR'S FIRST ARRAYS are the same: a Dictionary's keys and
    /// values, a List's items for a capacity, made as the collection is,
    /// stored into the same fields its growth replaces them in. The owner is
    /// `this`, a parameter never written, there before anything the
    /// constructor makes; or, inlined, the object just made. One under
    /// construction in a frame, or a struct's storage, is in no region, and
    /// AllocNear makes its arrays the heap's. The allocation is followed
    /// through a join with constants too (`capacity == 0 ? empty : new
    /// T[capacity]`), whose every store is held to the same rule.
    /// </summary>
    public static int MakeStorageBeside(Function f, Corsac.Lang.Lto.OwnedFieldFacts owned)
    {
        if (owned.SelfFreed.Count == 0) return 0;
        List<(Block, Instr)> made = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && IsRewritable(i.Callee) && i.Dest is not null) made.Add((b, i));
        if (made.Count == 0) return 0;
        Dictionary<VReg, Instr> defs = new();
        HashSet<VReg> many = new(), written = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d)
                {
                    written.Add(d);
                    if (!defs.TryAdd(d, i)) many.Add(d);
                }
        // Every write of a register written more than once: a join.
        Dictionary<VReg, List<Instr>> joins = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d && many.Contains(d)) (joins.TryGetValue(d, out List<Instr>? list) ? list : joins[d] = new()).Add(i);
        foreach (VReg r in many) defs.Remove(r);
        // What a register holds the object of, through copies: one written
        // once, or a parameter never written.
        VReg? Root(VReg r)
        {
            for (int hop = 0; hop < 8; hop++)
            {
                if (f.Params.Contains(r)) return written.Contains(r) ? null : r;
                if (!defs.TryGetValue(r, out Instr? d)) return null;
                if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands is [RegOperand from]) { r = from.Reg; continue; }
                return r;
            }
            return null;
        }
        Cfg? cfg = null;
        int beside = 0;
        foreach ((Block home, Instr alloc) in made)
        {
            if (!defs.ContainsKey(alloc.Dest!)) continue;
            // The registers that hold what it made: its own, and copies of it --
            // and A JOIN OF IT WITH CONSTANTS, `capacity == 0 ? empty : new
            // T[capacity]`, written more than once, each time a copy of one of
            // these or a constant (an immediate, a symbol's address): what it
            // holds besides the allocation is nothing anyone's storage, and
            // where it is stored is held to the same rule below.
            HashSet<VReg> names = new() { alloc.Dest! };
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                        if (i.Dest is { } d && !names.Contains(d) && defs.ContainsKey(d) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                            && i.Operands is [RegOperand { Reg: var from }] && names.Contains(from))
                        { names.Add(d); grew = true; }
                foreach (var (joined, writes) in joins)
                {
                    if (names.Contains(joined)) continue;
                    bool fromIt = false, only = true;
                    foreach (Instr w in writes)
                    {
                        if (w.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)) { only = false; break; }
                        foreach (Operand o in w.Operands)
                        {
                            if (o is RegOperand { Reg: var from } && names.Contains(from)) fromIt = true;
                            else if (o is not (ImmOperand or SymOperand)) { only = false; break; }
                        }
                        if (!only) break;
                    }
                    if (only && fromIt) { names.Add(joined); grew = true; }
                }
            }
            VReg? owner = null;
            bool ok = true;
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    bool storesIt = i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[1] is RegOperand { Reg: var value } && names.Contains(value)
                        || i.Op is Opcode.MemCopy or Opcode.AtomicSwap or Opcode.AtomicCas && i.Operands.Skip(1).Any(o => o is RegOperand { Reg: var r } && names.Contains(r));
                    if (!storesIt) continue;
                    if (i.Op != Opcode.Store || i.Field is not string field || !owned.SelfFreed.Contains(field)
                        || !owned.Fields.TryGetValue(field, out long offset) || offset != i.Offset
                        || i.Operands[0] is not RegOperand { Reg: var into } || Root(into) is not VReg root
                        || owner is not null && owner != root) { ok = false; break; }
                    owner = root;
                }
                if (!ok) break;
            }
            if (!ok || owner is null) continue;
            // The owner there before the allocation, on every way to it.
            if (!f.Params.Contains(owner))
            {
                Instr def = defs[owner];
                Block? at = f.Blocks.FirstOrDefault(b => b.Instrs.Contains(def));
                if (at is null) continue;
                if (ReferenceEquals(at, home))
                {
                    if (home.Instrs.IndexOf(def) >= home.Instrs.IndexOf(alloc)) continue;
                }
                else if (!(cfg ??= new Cfg(f)).Dominates(at, home)) continue;
            }
            int k = home.Instrs.IndexOf(alloc);
            home.Instrs.RemoveAt(k);
            home.Instrs.InsertRange(k, Beside(f, alloc, owner));
            beside++;
        }
        // As MakeSitesInRegion: a catch closes the regions it caught out of.
        if (beside > 0) CatchUp(f);
        return beside;
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
        // WHAT A LOOP MAKES AND DROPS EVERY TIME ROUND is no boundary's to
        // keep: those objects go to the heap, and a boundary chosen only for
        // them is not one (FindChurn). Found again over the boundaries left,
        // as a boundary dropped lets a loop reach further down.
        HashSet<int> churn = new();
        HashSet<Function> chosenBoundaries = Nearest(churn);
        for (int round = 0; round < 4 && FindChurn(chosenBoundaries, churn); round++)
            chosenBoundaries = Nearest(churn);
        if (Report is not null) foreach (Function f in chosenBoundaries) Console.Error.WriteLine($"regions: boundary chosen {f.Name}");
        List<int> boundaries = new();
        for (int c = 0; c < _copies.Count; c++)
            if (chosenBoundaries.Contains(_copies[c].F)) boundaries.Add(c);

        // For each boundary: what outlives it, and the copies beneath it --
        // and for a loop's, its own copy and the sites in its body.
        List<Judged> judged = new();
        foreach (int c in boundaries) judged.Add(new Judged(OutlivingOf(c), Beneath(c), -1, null));
        int functionBoundaries = judged.Count;

        // Each site's objects, and the copy each is made in.
        Dictionary<Instr, List<(int, int)>> bySite = new(ReferenceEqualityComparer.Instance);
        foreach (var ((copy, site), o) in _madeIn)
            (bySite.TryGetValue(site, out List<(int, int)>? l) ? l : bySite[site] = new()).Add((o, copy));

        // Whether object `o`, made in `copy`, can be kept past the end of
        // boundary `k` while made in its region.
        bool Fails(Judged k, int o, int copy, bool beside)
        {
            if (!k.Under(copy, _objects[o].Site!)) return false;
            int context = _objects[o].Context;
            // Beside an object that is never in this boundary's
            // region -- one that outlives it, or one in a frame --
            // never in it either, so nothing to prove.
            if (beside && context >= 0 && (k.Outlives.Contains(context) || _objects[context].Site is null)) return false;
            return k.Outlives.Contains(o);
        }

        // Whether the functions' boundaries, or a loop that drops it, send
        // object `o` to the heap whatever a loop's region would prove.
        bool FailsAnyway(int o, int copy, bool beside)
        {
            if (churn.Contains(o)) return true;
            for (int k = 0; k < functionBoundaries; k++) if (Fails(judged[k], o, copy, beside)) return true;
            return false;
        }

        Dictionary<Function, List<int>> loopRegions = SelectLoops(chosenBoundaries, judged, churn, FailsAnyway, Fails);
        if (judged.Count == 0) return;

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
                if (churn.Contains(o)) return null;
                foreach (Judged k in judged)
                {
                    if (!k.Under(copy, _objects[o].Site!)) continue;
                    anywhere = true;
                    if (Fails(k, o, copy, beside)) return null;
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
            var body = CloneBody(v.F, v.F.Name + "$region$" + made.Count, _m.KeepCalls);
            made[v] = body;
            _m.Functions.Add(body.Body);
        }

        foreach (Function f in originals)
            Rewrite(f, chosen.GetValueOrDefault, i => _roots.TryGetValue(i, out Version? to) && to.Same is { } same && made.TryGetValue(same, out var b) ? b.Body.Name : null);
        HashSet<Function> opened = new();
        foreach (int c in boundaries)
            if (opened.Add(_copies[c].F)) Open(_copies[c].F, loopRegions.ContainsKey(_copies[c].F));
        foreach ((Function f, List<int> headers) in loopRegions) OpenLoops(f, headers);

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
            if (opened.Contains(v.F)) Open(body, loopRegions.ContainsKey(v.F));
            if (loopRegions.TryGetValue(v.F, out List<int>? loopHeaders)) OpenLoops(body, loopHeaders);
            if (Report is not null) Console.Error.WriteLine($"regions: version {body.Name} for {v.Copies.Length} of its copies");
        }
        if (opened.Count > 0 || loopRegions.Count > 0 || inRegion + near > 0) CatchUp(_m);
        if (Report is not null)
            Console.Error.WriteLine($"regions: {opened.Count} boundaries, {loopRegions.Values.Sum(l => l.Count)} loops, {inRegion} sites in the innermost region, {near} beside their object, {versions.Count} versions making {versionSites} more, {churn.Count} objects loops drop");
    }

    /// <summary>
    /// A CATCH CLOSES WHAT IT CAUGHT OUT OF. A throw leaves a boundary with no
    /// RegionLeave, and its region stays open; a call made from the catching
    /// function then ran at the very depth the thrown-out-of boundary had
    /// run, no frame below the region's, and was given that region -- what
    /// it made was kept by the catcher and zeroed by the next region opened
    /// there. Every boundary a handler catches out of ran below the
    /// handler's own frame, so on entry to it each region opened by a frame
    /// below is closed (Runtime.RegionCatch).
    /// </summary>
    private static void CatchUp(Module m)
    {
        foreach (Function f in m.Functions) CatchUp(f);
    }

    // Once a pad: a function caught up already (the unit's, then again after
    // the link brought in and inlined other units' bodies) keeps one call.
    private static void CatchUp(Function f)
    {
            foreach (Block b in f.Blocks)
            {
                if (!b.IsLandingPad) continue;
                // After the pad's fetch of what was thrown, which must come first.
                int at = b.Instrs.Count > 0 && b.Instrs[0] is { Op: Opcode.Call, Callee: "__exception" } ? 1 : 0;
                if (b.Instrs.Skip(at).Take(2).Any(i => i is { Op: Opcode.Call, Callee: Catch })) continue;
                VReg frame = f.NewReg(IrTypes.Word, "catchframe");
                b.Instrs.InsertRange(at, new[]
                {
                    new Instr { Op = Opcode.FramePointer, Dest = frame, Line = b.Instrs.Count > 0 ? b.Instrs[0].Line : f.Line },
                    new Instr { Op = Opcode.Call, Callee = Catch, Operands = { RegOperand.Of(frame) }, Line = b.Instrs.Count > 0 ? b.Instrs[0].Line : f.Line },
                });
            }
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

    /// <summary>
    /// A function's body copied whole under another name, and which copied
    /// instruction each of its own became: every mark with it -- a site the
    /// link chose, a loop's region -- and a call kept (`keep`) kept in the
    /// copy too.
    /// </summary>
    public static (Function Body, Dictionary<Instr, Instr> From) CloneBody(Function f, string name, HashSet<Instr> keep)
    {
        Function made = new(name, f.Returns)
        {
            Exported = false, SourceFile = f.SourceFile, Line = f.Line, Display = f.Display, FromLibrary = f.FromLibrary,
            NoInlining = f.NoInlining, CalleePops = f.CalleePops,
        };
        Dictionary<VReg, VReg> regs = new();
        Dictionary<FrameSlot, FrameSlot> slots = new();
        Dictionary<Block, Block> blocks = new();
        Dictionary<Instr, Instr> from = new(ReferenceEqualityComparer.Instance);
        VReg Reg(VReg r) => regs.TryGetValue(r, out VReg? m) ? m : regs[r] = made.NewReg(r.Type, r.Name);
        foreach (VReg p in f.Params) made.Params.Add(Reg(p));
        foreach (VReg p in f.Params) regs[p].Number = p.Number;
        foreach (FrameSlot s in f.Slots) slots[s] = made.NewSlot(s.Bytes, s.Align, s.Name);
        foreach (Block b in f.Blocks)
        {
            Block copy = made.NewBlock(b.Label + "$");
            copy.IsLandingPad = b.IsLandingPad;
            copy.RegionLoop = b.RegionLoop;
            copy.RegionLoopBytes = b.RegionLoopBytes;
            blocks[b] = copy;
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                Instr c = new()
                {
                    Op = i.Op, Dest = i.Dest is null ? null : Reg(i.Dest), Size = i.Size, Signed = i.Signed, Offset = i.Offset,
                    Callee = i.Callee, DispatchType = i.DispatchType, Field = i.Field, Number = i.Number, Family = i.Family, Line = i.Line,
                    RegionSite = i.RegionSite,
                    Default = i.Default is null ? null : blocks[i.Default],
                };
                foreach (Operand o in i.Operands)
                    c.Operands.Add(o switch { RegOperand r => RegOperand.Of(Reg(r.Reg)), SlotOperand s => new SlotOperand(slots[s.Slot]), _ => o });
                foreach (Block t in i.Targets) c.WritableTargets.Add(blocks[t]);
                if (keep.Contains(i)) keep.Add(c);
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
                    List<Instr> retargeted = Retarget(f, i, h, frame);
                    b.Instrs.RemoveAt(k);
                    b.Instrs.InsertRange(k, retargeted);
                    k += retargeted.Count - 1;
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
    // iterator body (its frame outlives a return), not what runs before its
    // thread's block is its own (BeforeThreadBlock).
    private bool MayBeBoundary(Function f) =>
        f.Async is null && f.Name != _m.Entry && !EntryCalls().Contains(f.Name)
        && !f.Name.Contains("StaticInit", StringComparison.Ordinal)
        && !(_beforeBlock ??= BeforeThreadBlock()).Contains(f.Name);

    private HashSet<string>? _entryCalls;

    // WHAT THE ENTRY CALLS IS THE PROGRAM -- Main, and the stub's own setup:
    // a region opened there lasts the whole run, so what it holds is never
    // given back before the end, and only fills the arena. Main always
    // (Module.Main); the stub's other calls only while it calls Main: a stub
    // that took Main into itself calls what Main calls, and 1110's Checksum,
    // called twenty thousand times from Main, was never a boundary.
    private HashSet<string> EntryCalls()
    {
        if (_entryCalls is not null) return _entryCalls;
        _entryCalls = new(StringComparer.Ordinal);
        if (_m.Entry is string entry && _byName.TryGetValue(entry, out Function? start))
            foreach (Block b in start.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Callee is string callee) _entryCalls.Add(callee);
        if (_m.Main is string main && !_entryCalls.Contains(main)) _entryCalls.Clear();
        if (_m.Main is string program) _entryCalls.Add(program);
        return _entryCalls;
    }

    private HashSet<string>? _beforeBlock;

    /// <summary>
    /// WHAT RUNS BEFORE A THREAD'S BLOCK IS ITS OWN: the function that makes
    /// it so (RuntimeAbi.SetThreadBlock) and every function that calls it,
    /// however far up -- the entry stub's first call, a new thread's first
    /// method, which run with no block or the parent's. A region is opened
    /// in the block, so none of these is a boundary.
    /// </summary>
    private HashSet<string> BeforeThreadBlock()
    {
        Dictionary<string, List<string>> callers = new(StringComparer.Ordinal);
        foreach (Function f in _m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Callee is { } callee)
                        (callers.TryGetValue(callee, out List<string>? l) ? l : callers[callee] = new()).Add(f.Name);
        HashSet<string> before = new(StringComparer.Ordinal) { Corsac.Lang.Lto.RuntimeAbi.SetThreadBlock };
        Stack<string> next = new();
        next.Push(Corsac.Lang.Lto.RuntimeAbi.SetThreadBlock);
        while (next.TryPop(out string? g))
            if (callers.TryGetValue(g, out List<string>? list))
                foreach (string f in list)
                    if (before.Add(f)) next.Push(f);
        return before;
    }

    /// <summary>
    /// THE NEAREST CALL EACH OBJECT DIES IN: from the copy that makes it up
    /// through its callers, nearest first, the first whose return it is
    /// proved not to outlive. Not for an object a loop drops (FindChurn):
    /// that goes to the heap.
    /// </summary>
    private HashSet<Function> Nearest(HashSet<int> churn)
    {
        const int Reach = 8;
        HashSet<Function> found = new();
        for (int o = 1; o < _objects.Count; o++)
        {
            var obj = _objects[o];
            if (obj.Site?.Callee is not (Opt.Escape.Allocator or Opt.Escape.LeafAllocator or Opt.Escape.ObjectAllocator) || churn.Contains(o)) continue;
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
        return found;
    }

    private readonly Dictionary<int, HashSet<int>> _outliving = new();
    private HashSet<int> OutlivingOf(int c) => _outliving.TryGetValue(c, out HashSet<int>? known) ? known : _outliving[c] = Outliving(c);

    /// <summary>
    /// An allocation made beside another object instead (AllocNear): in that
    /// object's region if it is in the innermost one open, or on the heap.
    /// The instructions to put in its place.
    /// </summary>
    public static Instr[] Beside(Function f, Instr alloc, VReg owner)
    {
        VReg frame = f.NewReg(IrTypes.Word, "allocframe");
        long kind = alloc.Callee == Opt.Escape.LeafAllocator ? LeafKind : alloc.Callee == Opt.Escape.ObjectAllocator ? ObjectKind : 0;
        List<Instr> made = new() { new Instr { Op = Opcode.FramePointer, Dest = frame, Line = alloc.Line } };
        Instr call = new() { Op = Opcode.Call, Callee = Near, Dest = alloc.Dest, Line = alloc.Line };
        call.Operands.Add(AsWord(f, made, alloc.Operands[0], alloc.Line));
        call.Operands.Add(new ImmOperand(kind, IrTypes.Word));
        call.Operands.Add(AsWord(f, made, RegOperand.Of(owner), alloc.Line));
        call.Operands.Add(RegOperand.Of(frame));
        made.Add(Checked(call));
        return made.ToArray();
    }

    /// <summary>
    /// An allocation made a region helper's call: the conversions its
    /// operands need, then the call (Checked).
    /// </summary>
    private static List<Instr> Retarget(Function f, Instr alloc, string helper, VReg frame)
    {
        long kind = alloc.Callee == Opt.Escape.LeafAllocator ? LeafKind : alloc.Callee == Opt.Escape.ObjectAllocator ? ObjectKind : 0;
        List<Instr> made = new();
        Instr call = new() { Op = Opcode.Call, Callee = helper, Dest = alloc.Dest, Line = alloc.Line };
        call.Operands.Add(AsWord(f, made, alloc.Operands[0], alloc.Line));
        call.Operands.Add(new ImmOperand(kind, IrTypes.Word));
        if (helper == Near) call.Operands.Add(AsWord(f, made, RegOperand.Of(f.Params[0]), alloc.Line));
        call.Operands.Add(RegOperand.Of(frame));
        made.Add(Checked(call));
        return made;
    }

    /// <summary>
    /// AN OPERAND AS THE MACHINE WORD the region helpers take, every one of
    /// them a word (nint): a register of another width converted first, the
    /// conversion added to `before`. The owner MakeStorageBeside finds is
    /// the register its value came from, through copies and narrowings -- on
    /// a 32-bit target a long the word was cut from, which the backend pushed
    /// as two words: AllocNear took five for its four, the frame where the
    /// owner's high half was.
    /// </summary>
    private static Operand AsWord(Function f, List<Instr> before, Operand o, int line)
    {
        if (o is ImmOperand imm) return imm.Type == IrTypes.Word ? imm : new ImmOperand(imm.Value, IrTypes.Word);
        if (o is not RegOperand { Reg: var r } || r.Type == IrTypes.Word) return o;
        VReg word = f.NewReg(IrTypes.Word);
        before.Add(new Instr { Op = r.Type == IrType.I64 ? Opcode.Trunc64 : Opcode.ZExt32, Dest = word, Operands = { RegOperand.Of(r) }, Line = line });
        return RegOperand.Of(word);
    }

    /// <summary>
    /// A region helper's call as the runtime declares it: exactly its
    /// parameters (AllocRegion three, AllocNear four), each a word. Anything
    /// else is this pass's mistake, and the call would read its arguments
    /// out of the wrong stack slots.
    /// </summary>
    private static Instr Checked(Instr call)
    {
        int arity = call.Callee == Near ? 4 : call.Callee == InRegion ? 3 : -1;
        if (arity >= 0 && call.Operands.Count != arity)
            throw new InvalidOperationException($"region pass: {call.Callee} made with {call.Operands.Count} operands, not {arity}");
        foreach (Operand o in call.Operands)
            if (o is RegOperand { Reg: var r } && r.Type != IrTypes.Word || o is ImmOperand imm && imm.Type != IrTypes.Word)
                throw new InvalidOperationException($"region pass: {call.Callee} handed an operand of {o.Type}, not a word");
        return call;
    }

    // The region opened where the call first needs it, given back on every
    // return; a throw is the runtime's to notice (Gc.PopStale). On entry
    // where the function has loop regions too (OpenLoops): opened later, at
    // the same frame, it would close the loop's. `bytes` is the most the
    // region holds in one call, as the link proved it (0: not known).
    private static void Open(Function f, bool onEntry, long bytes = 0)
    {
        // Never inlined: the record names the boundary's own frame, and a
        // caller's would outlive a throw the caller catches.
        f.NoInlining = true;
        VReg frame = f.NewReg(IrTypes.Word, "regionframe");
        VReg handle = f.NewReg(IrTypes.Word, "region");
        Block entry = f.Blocks[0];
        (Block at, int k0) = onEntry ? (entry, 0) : OpenAt(f);
        Instr[] open =
        {
            new Instr { Op = Opcode.FramePointer, Dest = frame, Line = f.Line },
            new Instr { Op = Opcode.Call, Callee = Enter, Dest = handle, Operands = { RegOperand.Of(frame), new ImmOperand(bytes, IrTypes.Word) }, Line = f.Line },
        };
        at.Instrs.InsertRange(k0, open);
        // Opened further in: a return that never passed there hands RegionLeave
        // the -1 a region that could not be opened hands it, and it does nothing.
        if (at != entry || k0 != 0)
            entry.Instrs.Insert(0, new Instr { Op = Opcode.Copy, Dest = handle, Operands = { new ImmOperand(-1, IrTypes.Word) }, Line = f.Line });
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
                if (b.Instrs[k].Op == Opcode.Ret)
                {
                    b.Instrs.Insert(k, new Instr { Op = Opcode.Call, Callee = Leave, Operands = { RegOperand.Of(handle) }, Line = b.Instrs[k].Line });
                    k++;
                }
    }

    /// <summary>
    /// A BOUNDARY'S LEAVE AFTER WHAT ITS RETURN GIVES BACK. Open puts
    /// RegionLeave just before each return; the link's lifetime pass runs
    /// again after it (Escape.RunAtLink) and puts its own frees just before
    /// each return too -- after the leave: a collection it placed in the
    /// frame gives back its storage and elements, an owned variable or field
    /// what it holds. Storage made beside a frame owner is the region's
    /// (Gc.OnStackWithin), so those frees read, and gave back, memory the
    /// leave had cut -- zeroed, or a chunk handed back to the system, where
    /// a collection that owns its elements read them from an unmapped page.
    /// So each leave goes back to just before its return, past what was put
    /// after it, unless something there allocates: what that makes would
    /// then be cut with the region, and the leave stays where it was.
    /// </summary>
    public static int LeaveLast(Function f)
    {
        int moved = 0;
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is not { Op: Opcode.Ret }) continue;
            int ret = b.Instrs.Count - 1;
            // EVERY LEAVE, in the order they were: a function's own and each
            // loop's a return inside it passes (OpenLoops), the frees behind
            // all of them. Only the ones past the block's last allocation
            // move; one before it stays, and what was made after it is cut
            // with its region as before.
            int last = -1;
            for (int k = 0; k < ret; k++)
                if (IsSiteCall(b.Instrs[k]) || b.Instrs[k].Op == Opcode.Call && b.Instrs[k].Callee is InRegion or Near or Enter) last = k;
            List<Instr> leaves = new();
            for (int k = last + 1; k < ret; k++)
                if (b.Instrs[k] is { Op: Opcode.Call, Callee: Leave } leave) leaves.Add(leave);
            if (leaves.Count == 0) continue;
            List<Instr> rest = b.Instrs.GetRange(last + 1, ret - last - 1).Where(i => !leaves.Contains(i)).ToList();
            if (rest.Count == 0) continue;
            b.Instrs.RemoveRange(last + 1, ret - last - 1);
            b.Instrs.InsertRange(last + 1, rest.Concat(leaves));
            moved += leaves.Count;
        }
        return moved;
    }

    // Past this many blocks the region is opened on entry: the dominators
    // below are bit sets, a pair of blocks at a time.
    private const int OpenAtBlocks = 256;

    /// <summary>
    /// WHERE A BOUNDARY OPENS ITS REGION: on entry, or -- when every call that
    /// can make anything there lies beneath one block that runs at most once
    /// a call -- in that block, just before the first such call. A hot
    /// function whose only allocation is on a path seldom taken (a message
    /// built for a bad argument) then pays for no region on the path it
    /// usually takes. Where it opens is a matter of cost and never of
    /// soundness: every site made in the innermost region is proved dead by
    /// the end of every boundary it can run beneath, so whatever is made
    /// before the region opens goes to an outer one, or to the heap, and is
    /// as dead by its end. It must only never open twice in one call (a
    /// second record at the same frame closes the first, PopStale), so not
    /// in a block on a cycle, nor in a function a handler is entered by an
    /// unwind into -- an edge the graph does not hold.
    ///
    /// The calls that count are those on a path to a return: a path that
    /// only throws closes the region it leaves anyway. Its objects go to an
    /// outer region, or the heap.
    /// </summary>
    private static (Block At, int Index) OpenAt(Function f)
    {
        Block entry = f.Blocks[0];
        if (f.Blocks.Count > OpenAtBlocks || f.Blocks.Skip(1).Any(b => b.IsLandingPad)
            || f.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.LabelAddr)))
            return (entry, 0);
        Cfg cfg = new(f);
        if (cfg.Roots.Count != 1 || cfg.InCycle(entry)) return (entry, 0);

        // The blocks from which a return is reached.
        HashSet<Block> returning = new(ReferenceEqualityComparer.Instance);
        Queue<Block> next = new();
        foreach (Block b in f.Blocks)
            if (b.Terminator is { Op: Opcode.Ret } && returning.Add(b)) next.Enqueue(b);
        while (next.TryDequeue(out Block? b))
            foreach (Block p in cfg.Preds(b))
                if (returning.Add(p)) next.Enqueue(p);

        // A type's initialiser is not counted: run once, what it keeps is
        // kept by its statics, and the rest it makes once is no cost.
        bool Needs(Instr i) => i.Op == Opcode.CallIndirect || i.Op == Opcode.Call && i.Callee is { } c && !Harmless(c) && c != Leave && c != Enter
            && !c.Contains("StaticInit", StringComparison.Ordinal);
        List<Block> needing = f.Blocks.Where(b => returning.Contains(b) && b.Instrs.Any(Needs)).ToList();
        if (needing.Count == 0) return (entry, 0);

        // The blocks that dominate every one that needs it: a chain, from the
        // entry down. The lowest that is on no cycle.
        Block? best = null;
        foreach (Block d in f.Blocks)
        {
            if (!needing.All(b => cfg.Dominates(d, b)) || cfg.InCycle(d)) continue;
            if (best is null || cfg.Dominates(best, d)) best = d;
        }
        if (best is null || best == entry) return (entry, 0);
        int first = best.Instrs.FindIndex(Needs);
        if (first < 0)
        {
            first = 0;
            while (first < best.Instrs.Count && best.Instrs[first].Op == Opcode.Phi) first++;
        }
        return (best, first);
    }

    // ---- loops ----------------------------------------------------------------
    //
    // A BOUNDARY THAT LOOPS keeps everything every time round makes, until it
    // returns: a thread's body making a node a lap until it is told to stop
    // filled its arena, and the collector read and copied the whole of it at
    // every cycle. Two answers, each a loop's:
    //
    // A LOOP REGION, where what a lap makes is proved dead by the end of that
    // lap: not reachable from anything live where the lap ends -- going round
    // again, or out of the loop -- nor from the function's frame slots, what
    // it was handed, what it hands back or a static. The function opens the
    // region at the top of the loop and gives back what is in it at the top
    // of every lap after (Gc.RegionLoop), and closes it on every way out. To
    // Decide it is one more boundary, beneath which run the sites in the
    // body and the copies the body's calls reach.
    //
    // THE HEAP, for what a lap makes, drops, and no loop region can take:
    // made in one lap and carried into the next only by the registers the
    // loop writes (a list's newest node, a string grown by concatenation) --
    // nothing kept before the loop holds it -- or proved dead by the lap's
    // end in a loop no region could be given. A boundary above would hold
    // every lap's.

    /// <summary>
    /// A boundary as Decide judges it: what outlives it and the copies that
    /// run beneath it -- and for a loop's region, the copy it is in and the
    /// sites in its body, its copy's others being made outside it.
    /// </summary>
    private sealed record Judged(HashSet<int> Outlives, HashSet<int> Beneath, int Host, HashSet<Instr>? Body)
    {
        public bool Under(int copy, Instr site) => Beneath.Contains(copy) || copy == Host && Body!.Contains(site);
    }

    /// <summary>
    /// A natural loop of a function, by its header's place in the blocks:
    /// the instructions in its body and the calls among them -- and those
    /// made in every lap that goes round, in blocks that dominate each back
    /// edge -- the registers live where a lap ends, and those live into the
    /// header that the body never writes: what was there before the loop
    /// and stays.
    /// </summary>
    internal sealed class LoopShape
    {
        public required int Header;
        public required HashSet<Instr> Instrs;
        public required List<Instr> Calls;
        public required List<Instr> Always;
        public required List<VReg> Live;
        public required List<VReg> Invariant;
        public required List<FrameSlot> KeptSlots;
    }

    private readonly Dictionary<Function, List<LoopShape>> _loops = new();
    private Dictionary<int, List<(int Object, Instr Site)>>? _madeBy;
    // Past this many blocks a function's loops are not looked for: the
    // dominators are bit sets, a pair of blocks at a time.
    internal const int LoopBlocks = 1024;
    // Past this many copies walked, no further loop is judged: those left
    // keep what their boundaries keep, as they always did.
    private const long LoopBudget = 20_000_000;
    private long _loopWork;

    public const string LoopTop = Corsac.Lang.Lto.RuntimeAbi.RegionLoop;

    /// <summary>Each loop's header and body: the blocks round a back edge to a block that dominates it, merged by header.</summary>
    internal static List<(Block Header, HashSet<Block> Body, List<Block> Latches)> NaturalLoops(Function f, Cfg cfg)
    {
        List<(Block, HashSet<Block>, List<Block>)> loops = new();
        Dictionary<Block, List<Block>> latches = new(ReferenceEqualityComparer.Instance);
        List<Block> headers = new();
        bool[] live = cfg.Live;
        foreach (Block b in f.Blocks)
        {
            if (!live[b.Order]) continue;
            foreach (Block h in cfg.Succs(b))
            {
                if (!cfg.Dominates(h, b)) continue;
                if (!latches.TryGetValue(h, out List<Block>? l)) { latches[h] = l = new(); headers.Add(h); }
                l.Add(b);
            }
        }
        foreach (Block h in headers)
        {
            HashSet<Block> body = new(ReferenceEqualityComparer.Instance) { h };
            Stack<Block> next = new();
            foreach (Block l in latches[h]) if (body.Add(l)) next.Push(l);
            while (next.TryPop(out Block? b))
                foreach (Block p in cfg.Preds(b))
                    if (body.Add(p)) next.Push(p);
            loops.Add((h, body, latches[h]));
        }
        return loops;
    }

    private List<LoopShape> LoopsOf(Function f)
    {
        if (_loops.TryGetValue(f, out List<LoopShape>? known)) return known;
        return _loops[f] = Shapes(f);
    }

    /// <summary>
    /// Every natural loop of a function but one headed by a root, as
    /// LoopShape states it: none in an async or iterator body, nor past
    /// LoopBlocks. The unit's summary for the link states the same loops
    /// over the same IR (RegionSummary).
    /// </summary>
    internal static List<LoopShape> Shapes(Function f)
    {
        List<LoopShape> found = new();
        if (f.Async is not null || f.Blocks.Count < 2 || f.Blocks.Count > LoopBlocks) return found;
        Cfg cfg = new(f);
        var natural = NaturalLoops(f, cfg);
        if (natural.Count == 0) return found;
        Liveness liveness = new(cfg);
        (HashSet<FrameSlot> aliased, Func<Instr, IEnumerable<FrameSlot>> writes) = SlotUses(f);
        // THE BLOCKS A RETURN IS REACHED FROM (RegionSummary's `returns`); the
        // rest only throw. A lap that leaves for one of those goes on to no
        // code after the loop: what it keeps, it keeps through the calls it
        // makes there, which the escape answers follow. Counted as a lap's
        // end, a failed cast's path -- the object handed to InvalidCastTo --
        // made every array a lap read live where the lap ended, and the loop's
        // region refused it (1323). Only where nothing is caught here: a
        // handler is joined to no call that unwinds to it.
        bool[] returns = new bool[f.Blocks.Count];
        bool caughtHere = cfg.Roots.Any(root => root != f.Entry);
        Stack<Block> towards = new();
        foreach (Block b in f.Blocks)
            if (caughtHere || b.Terminator is { Op: Opcode.Ret }) { returns[b.Order] = true; towards.Push(b); }
        while (towards.TryPop(out Block? b))
            foreach (Block p in cfg.Preds(b))
                if (!returns[p.Order]) { returns[p.Order] = true; towards.Push(p); }
        foreach ((Block header, HashSet<Block> body, List<Block> latches) in natural)
        {
            if (cfg.IsRoot(header)) continue;
            HashSet<Instr> instrs = new(ReferenceEqualityComparer.Instance);
            HashSet<VReg> written = new(), live = new();
            List<Instr> calls = new(), always = new();
            foreach (Block b in body)
            {
                bool everyLap = latches.All(l => cfg.Dominates(b, l));
                foreach (Instr i in b.Instrs)
                {
                    instrs.Add(i);
                    if (i.Dest is { } d) written.Add(d);
                    if (i.Op is Opcode.Call or Opcode.CallIndirect) { calls.Add(i); if (everyLap) always.Add(i); }
                }
                // A lap ends going round again, or out: what is live into
                // where it goes, and what that block's joins take from here.
                foreach (Block s in cfg.Succs(b))
                    if (s == header || !body.Contains(s) && returns[s.Order])
                    {
                        foreach (VReg r in liveness.LiveIn(s)) live.Add(r);
                        foreach (Instr phi in s.Instrs)
                        {
                            if (phi.Op != Opcode.Phi) break;
                            for (int k = 0; k < phi.Operands.Count && k < phi.Targets.Count; k++)
                                if (phi.Targets[k] == b && phi.Operands[k] is RegOperand { Reg: var r }) live.Add(r);
                        }
                    }
            }
            List<VReg> invariant = new();
            foreach (VReg r in liveness.LiveIn(header))
            {
                live.Add(r);
                if (!written.Contains(r)) invariant.Add(r);
            }
            // A frame slot the body writes, whose address goes nowhere else
            // (SlotUses), is the loop's own as a register it writes is; any
            // other is kept from before the loop.
            HashSet<FrameSlot> stored = new();
            foreach (Instr i in instrs) stored.UnionWith(writes(i));
            List<FrameSlot> keptSlots = f.Slots.Where(s => !stored.Contains(s) || aliased.Contains(s)).ToList();
            found.Add(new LoopShape
            {
                Header = header.Order, Instrs = instrs, Calls = calls, Always = always, Live = live.ToList(), Invariant = invariant, KeptSlots = keptSlots,
            });
        }
        return found;
    }

    /// <summary>
    /// How a function uses its frame slots' addresses: named, or copied into
    /// a register only ever written so. A slot is written by a store there
    /// or by a call of the runtime's frees handed its address -- they replace
    /// what an owned slot holds and keep no address (Harmless); one whose
    /// address goes anywhere else is aliased, and may be written by anything.
    /// </summary>
    internal static (HashSet<FrameSlot> Aliased, Func<Instr, IEnumerable<FrameSlot>> Writes) SlotUses(Function f)
    {
        Dictionary<VReg, FrameSlot?> held = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d)
                    held[d] = i.Op == Opcode.Copy && i.Operands.Count == 1 && i.Operands[0] is SlotOperand { Slot: var s }
                        && (!held.TryGetValue(d, out FrameSlot? was) || was == s) ? s : null;
        FrameSlot? SlotOf(Operand o) => o switch
        {
            SlotOperand { Slot: var s } => s,
            RegOperand { Reg: var r } => held.GetValueOrDefault(r),
            _ => null,
        };
        bool Freer(Instr i) => i.Op == Opcode.Call && i.Callee is { } c && Harmless(c);
        HashSet<FrameSlot> aliased = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (SlotOf(i.Operands[k]) is { } s && !(k == 0 && i.Op is Opcode.Load or Opcode.Store) && !Freer(i)
                        && !(i.Op == Opcode.Copy && i.Dest is { } d && held.GetValueOrDefault(d) == s))
                        aliased.Add(s);
        IEnumerable<FrameSlot> Writes(Instr i)
        {
            if (i.Op == Opcode.Store && i.Operands.Count > 0 && SlotOf(i.Operands[0]) is { } s) yield return s;
            else if (Freer(i)) foreach (Operand o in i.Operands) if (SlotOf(o) is { } t) yield return t;
        }
        return (aliased, Writes);
    }

    // The objects each copy makes, and where.
    private List<(int Object, Instr Site)> MadeBy(int copy)
    {
        if (_madeBy is null)
        {
            _madeBy = new();
            foreach (var ((c, site), o) in _madeIn)
                if (site.Callee is Opt.Escape.Allocator or Opt.Escape.LeafAllocator or Opt.Escape.ObjectAllocator)
                    (_madeBy.TryGetValue(c, out var l) ? l : _madeBy[c] = new()).Add((o, site));
        }
        return _madeBy.TryGetValue(copy, out var made) ? made : new();
    }

    /// <summary>
    /// The copies a loop's body runs, in copy `c`: every one its calls reach
    /// (all), and those reached through no boundary of `stop` (near) -- where
    /// what is made is made in the region the loop runs in. A call this
    /// cannot follow reaches only the copies of functions whose address is
    /// taken, handed everything: what they make that a region may take is
    /// dead by their own return, as it is beneath a function's boundary.
    /// </summary>
    private (HashSet<int> All, HashSet<int> Near) Reached(int c, List<Instr> calls, HashSet<int> stop)
    {
        HashSet<int> all = new(), near = new();
        Stack<int> next = new(), nearNext = new();
        foreach (Instr i in calls)
        {
            if (!_callTargets.TryGetValue((c, i), out HashSet<int>? targets)) continue;
            foreach (int t in targets)
            {
                if (all.Add(t)) next.Push(t);
                if (!stop.Contains(t) && near.Add(t)) nearNext.Push(t);
            }
        }
        while (next.TryPop(out int k))
        {
            _loopWork++;
            foreach (int callee in _callees[k]) if (all.Add(callee)) next.Push(callee);
        }
        while (nearNext.TryPop(out int k))
            foreach (int callee in _callees[k])
                if (!stop.Contains(callee) && near.Add(callee)) nearNext.Push(callee);
        return (all, near);
    }

    // What outlives a lap of a loop in copy `c`: what outlives the copy, and
    // what the registers and frame slots given reach.
    private HashSet<int> LapOutlives(int c, List<VReg> registers, IEnumerable<FrameSlot> slots)
    {
        HashSet<int> reached = new(OutlivingOf(c));
        List<int> start = new();
        Function f = _copies[c].F;
        foreach (VReg r in registers)
            if (r.Id < f.RegCount)
                foreach (long l in _pts[Reg(c, r)]) start.Add(ObjectOf(l));
        foreach (FrameSlot slot in slots)
            if (_slotObjects.TryGetValue((c, slot), out int o)) start.Add(o);
        _loopWork += reached.Count;
        return Reachable(start, reached);
    }

    // The objects made in one lap, in the region the loop runs in: by the
    // sites in its body, and in the copies its calls reach through no boundary.
    private IEnumerable<(int Object, int Copy)> MadeInLap(int c, LoopShape loop, HashSet<int> near)
    {
        foreach ((int o, Instr site) in MadeBy(c)) if (loop.Instrs.Contains(site) || near.Contains(c)) yield return (o, c);
        foreach (int k in near) if (k != c) foreach ((int o, _) in MadeBy(k)) yield return (o, k);
    }

    // Whether one of `calls`, made from copy `c`, always makes something
    // `takes` -- in a block of its callee that every return passes through,
    // or in a call made from such a block, through no boundary of `stop`.
    private bool AlwaysMakes(int c, List<Instr> calls, HashSet<int> stop, Func<int, int, bool> takes, HashSet<int> seen, int depth)
    {
        if (depth > 6) return false;
        foreach (Instr i in calls)
        {
            if (!_callTargets.TryGetValue((c, i), out HashSet<int>? targets)) continue;
            foreach (int t in targets)
            {
                if (stop.Contains(t) || !seen.Add(t)) continue;
                List<Instr> must = MustRun(_copies[t].F);
                if (MadeBy(t).Any(m => must.Contains(m.Site) && takes(m.Object, t))) return true;
                if (AlwaysMakes(t, must, stop, takes, seen, depth + 1)) return true;
            }
        }
        return false;
    }

    private readonly Dictionary<Function, List<Instr>> _mustRun = new();

    // The calls a function makes on every way to a return: in the blocks
    // that dominate each block that returns.
    private List<Instr> MustRun(Function f)
    {
        if (_mustRun.TryGetValue(f, out List<Instr>? known)) return known;
        return _mustRun[f] = MustRunCalls(f);
    }

    /// <summary>MustRun's answer, uncached: also what a unit's summary states for the link (RegionSummary).</summary>
    internal static List<Instr> MustRunCalls(Function f)
    {
        List<Instr> must = new();
        if (f.Blocks.Count > LoopBlocks) return must;
        Cfg cfg = new(f);
        List<Block> returns = f.Blocks.Where(b => b.Terminator is { Op: Opcode.Ret } && cfg.Live[b.Order]).ToList();
        if (returns.Count == 0) return must;
        foreach (Block b in f.Blocks)
            if (cfg.Live[b.Order] && returns.All(r => cfg.Dominates(b, r)))
                foreach (Instr i in b.Instrs)
                    if (i.Op is Opcode.Call or Opcode.CallIndirect) must.Add(i);
        return must;
    }

    private HashSet<int> BoundaryCopies(HashSet<Function> boundaries)
    {
        HashSet<int> copies = new();
        for (int c = 0; c < _copies.Count; c++) if (boundaries.Contains(_copies[c].F)) copies.Add(c);
        return copies;
    }

    /// <summary>
    /// THE OBJECTS LOOPS CARRY AND DROP: made in a lap, reachable where a lap
    /// ends, but only through what the loop itself writes -- nothing live
    /// into the loop that the loop leaves alone keeps them. Added to `churn`;
    /// whether any were.
    /// </summary>
    private bool FindChurn(HashSet<Function> boundaries, HashSet<int> churn)
    {
        HashSet<int> stop = BoundaryCopies(boundaries);
        bool grew = false;
        for (int c = 0; c < _copies.Count && _loopWork < LoopBudget; c++)
        {
            Function f = _copies[c].F;
            if (f.Name == _m.Entry) continue;
            foreach (LoopShape loop in LoopsOf(f))
            {
                (_, HashSet<int> near) = Reached(c, loop.Calls, stop);
                List<int> made = MadeInLap(c, loop, near).Select(m => m.Object).Where(o => !churn.Contains(o)).Distinct().ToList();
                if (made.Count == 0) continue;
                HashSet<int> lap = LapOutlives(c, loop.Live, f.Slots), kept = LapOutlives(c, loop.Invariant, loop.KeptSlots);
                foreach (int o in made)
                    if (lap.Contains(o) && !kept.Contains(o) && churn.Add(o))
                    {
                        grew = true;
                        if (Report is not null) Console.Error.WriteLine($"regions: dropped by a loop in {f.Name}: {_objects[o].F!.Name} line {_objects[o].Site!.Line} {TypeOf(o)}");
                    }
            }
        }
        return grew;
    }

    /// <summary>
    /// THE LOOPS GIVEN A REGION: by function, their headers' places. A loop
    /// is given one where, in every copy of its function, every object made
    /// beneath it that the functions' boundaries would put in a region is
    /// proved dead by the end of a lap -- so that no site made in a region
    /// before goes to the heap for it -- and where every lap that goes round
    /// makes one, with no boundary between: a loop that makes something only
    /// on a path seldom taken (a message for a bad record) pays for no call
    /// at the top of every lap. Each copy's loop is added to `judged`, and Decide
    /// proves every site beneath it against it. What a loop not given one
    /// makes and drops in a lap goes to the heap (`churn`).
    ///
    /// Only in a function with no landing pad: a throw caught in the loop's
    /// own frame, and the region it left open there, would be the region the
    /// catch went on making in. Never the program's entry, nor a type's
    /// initialiser, nor an async or iterator body.
    /// </summary>
    private Dictionary<Function, List<int>> SelectLoops(HashSet<Function> boundaries, List<Judged> judged, HashSet<int> churn,
        Func<int, int, bool, bool> failsAnyway, Func<Judged, int, int, bool, bool> fails)
    {
        Dictionary<Function, List<int>> chosen = new();
        HashSet<int> stop = BoundaryCopies(boundaries);
        Dictionary<Function, List<int>> copiesOf = new();
        for (int c = 0; c < _copies.Count; c++)
            (copiesOf.TryGetValue(_copies[c].F, out List<int>? l) ? l : copiesOf[_copies[c].F] = new()).Add(c);
        bool helper = _byName.ContainsKey(LoopTop);
        List<(int Copy, LoopShape Loop, HashSet<int> Near, HashSet<int> Lap)> refused = new();

        foreach ((Function f, List<int> copies) in copiesOf)
        {
            List<LoopShape> loops = LoopsOf(f);
            if (loops.Count == 0) continue;
            bool may = helper && f.Name != _m.Entry && !f.Name.Contains("StaticInit", StringComparison.Ordinal)
                && !f.Blocks.Any(b => b.IsLandingPad) && !f.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.LabelAddr));
            foreach (LoopShape loop in loops)
            {
                if (_loopWork >= LoopBudget) break;
                List<Judged> mine = new();
                List<(int, LoopShape, HashSet<int>, HashSet<int>)> lapsHere = new();
                bool sound = may, worth = false;
                foreach (int c in copies)
                {
                    (HashSet<int> all, HashSet<int> near) = Reached(c, loop.Calls, stop);
                    HashSet<int> lap = LapOutlives(c, loop.Live, f.Slots);
                    lapsHere.Add((c, loop, near, lap));
                    if (!sound) continue;
                    Judged k = new(lap, all, c, loop.Instrs);
                    mine.Add(k);
                    // Under it: the body's own sites, and every copy its calls reach.
                    IEnumerable<(int Object, int Copy)> under = MadeBy(c).Where(m => k.Under(c, m.Site)).Select(m => (m.Object, c))
                        .Concat(all.Where(a => a != c).SelectMany(a => MadeBy(a).Select(m => (m.Object, a))));
                    foreach ((int o, int copy) in under)
                    {
                        foreach (bool beside in new[] { false, true })
                            if (!failsAnyway(o, copy, beside) && fails(k, o, copy, beside)) { sound = false; break; }
                        if (!sound) break;
                    }
                    if (!sound || worth) continue;
                    // Worth a call at the top of every lap: a lap that goes
                    // round always makes something the region takes.
                    bool Takes(int o, int copy) => !failsAnyway(o, copy, false) && !lap.Contains(o);
                    worth = MadeBy(c).Any(m => loop.Always.Contains(m.Site) && Takes(m.Object, c))
                        || AlwaysMakes(c, loop.Always, stop, Takes, new HashSet<int>(), 0);
                }
                if (sound && worth)
                {
                    judged.AddRange(mine);
                    (chosen.TryGetValue(f, out List<int>? headers) ? headers : chosen[f] = new()).Add(loop.Header);
                    if (Report is not null) Console.Error.WriteLine($"regions: loop region {f.Name} at block {loop.Header}");
                }
                else refused.AddRange(lapsHere);
            }
        }

        // WHAT A LAP OF A LOOP WITH NO REGION MAKES AND DROPS goes to the heap,
        // unless a loop region inside it takes it.
        foreach ((int c, LoopShape loop, HashSet<int> near, HashSet<int> lap) in refused)
            foreach ((int o, int copy) in MadeInLap(c, loop, near))
            {
                if (lap.Contains(o) || churn.Contains(o)) continue;
                bool taken = false;
                foreach (Judged k in judged)
                    if (k.Body is not null && k.Under(copy, _objects[o].Site!) && !k.Outlives.Contains(o)) { taken = true; break; }
                if (taken) continue;
                churn.Add(o);
                if (Report is not null) Console.Error.WriteLine($"regions: dropped by a loop in {_copies[c].F.Name}: {_objects[o].F!.Name} line {_objects[o].Site!.Line} {TypeOf(o)}");
            }
        return chosen;
    }

    /// <summary>
    /// A REGION FOR EACH LOOP NAMED, by its header's place in the blocks:
    /// RegionLoop at the top of every lap -- opening it the first time, giving
    /// back what the lap before made in it after -- and RegionLeave on every
    /// edge out of the loop and before every return, each handle -1 wherever
    /// its loop is not running, which RegionLeave does nothing with. The
    /// function is never inlined: the record names its own frame. Each lap
    /// is handed the most it makes, by header (`lapBytes`; 0: not known).
    /// </summary>
    private static void OpenLoops(Function f, List<int> headers, IReadOnlyDictionary<int, long>? lapBytes = null)
    {
        Cfg cfg = new(f);
        var loops = NaturalLoops(f, cfg).Where(l => headers.Contains(l.Header.Order) && !cfg.IsRoot(l.Header)).ToList();
        if (loops.Count == 0) return;
        f.NoInlining = true;
        VReg frame = f.NewReg(IrTypes.Word, "loopframe");
        List<Instr> onEntry = new() { new Instr { Op = Opcode.FramePointer, Dest = frame, Line = f.Line } };
        Dictionary<Block, List<Instr>> leaves = new(ReferenceEqualityComparer.Instance), tops = new(ReferenceEqualityComparer.Instance);
        List<VReg> handles = new();
        foreach ((Block header, HashSet<Block> body, _) in loops)
        {
            VReg handle = f.NewReg(IrTypes.Word, "loopregion");
            handles.Add(handle);
            long bytes = lapBytes?.GetValueOrDefault(header.Order) ?? 0;
            int line = header.Instrs.Count > 0 ? header.Instrs[0].Line : f.Line;
            onEntry.Add(new Instr { Op = Opcode.Copy, Dest = handle, Operands = { new ImmOperand(-1, IrTypes.Word) }, Line = f.Line });
            (tops.TryGetValue(header, out List<Instr>? t) ? t : tops[header] = new()).Add(
                new Instr { Op = Opcode.Call, Callee = LoopTop, Dest = handle, Operands = { RegOperand.Of(handle), RegOperand.Of(frame), new ImmOperand(bytes, IrTypes.Word) }, Line = line });
            HashSet<Block> outs = new(ReferenceEqualityComparer.Instance);
            foreach (Block b in body)
                foreach (Block s in cfg.Succs(b))
                    if (!body.Contains(s) && outs.Add(s))
                    {
                        List<Instr> l = leaves.TryGetValue(s, out List<Instr>? have) ? have : leaves[s] = new();
                        int at = s.Instrs.Count > 0 ? s.Instrs[0].Line : line;
                        l.Add(new Instr { Op = Opcode.Call, Callee = Leave, Operands = { RegOperand.Of(handle) }, Line = at });
                        l.Add(new Instr { Op = Opcode.Copy, Dest = handle, Operands = { new ImmOperand(-1, IrTypes.Word) }, Line = at });
                    }
        }
        foreach (Block b in f.Blocks)
        {
            List<Instr> put = new();
            if (leaves.TryGetValue(b, out List<Instr>? l)) put.AddRange(l);
            if (tops.TryGetValue(b, out List<Instr>? t)) put.AddRange(t);
            if (put.Count > 0)
            {
                int k0 = 0;
                while (k0 < b.Instrs.Count && b.Instrs[k0].Op == Opcode.Phi) k0++;
                b.Instrs.InsertRange(k0, put);
            }
            for (int k = 0; k < b.Instrs.Count; k++)
                if (b.Instrs[k].Op == Opcode.Ret)
                {
                    foreach (VReg h in handles)
                    {
                        b.Instrs.Insert(k, new Instr { Op = Opcode.Call, Callee = Leave, Operands = { RegOperand.Of(h) }, Line = b.Instrs[k].Line });
                        k++;
                    }
                }
        }
        f.Blocks[0].Instrs.InsertRange(0, onEntry);
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
        _readCells.Add(-1);
        return _objects.Count - 1;
    }

    /// <summary>The node of one of an object's cells, made on first use.</summary>
    private int Cell(int o, long offset)
    {
        if (offset is < 0 || offset > FarthestField && !IsStrided(offset) || _collapsed[o]) offset = Any;
        Dictionary<long, int> cells = _cells[o];
        if (cells.TryGetValue(offset, out int node)) return node;
        if (offset != Any && cells.Count >= MostCells) return Collapse(o);
        node = NewNode();
        cells[offset] = node;
        // Words that may be this one are this one, both ways (MayOverlap).
        long firstData = IsArrayObject(o) ? Target.Current.ArrayHeaderBytes : 0;
        foreach (var (other, at) in cells.ToArray())
            if (other != offset && MayOverlap(offset, other, firstData)) { Edge(node, at, 0); Edge(at, node, 0); }
        if (HoldsNoReference(o, offset)) _noReference[node] = true;
        if (_allCells[o] >= 0) Edge(node, _allCells[o], 0);
        if (_readCells[o] >= 0 && offset != 0) Edge(node, _readCells[o], 0);
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
    // Whether the object is an array: stamped with a descriptor that says so.
    private bool IsArrayObject(int o)
    {
        if (Stamp(o) is not var (table, at)) return false;
        int w = Target.Current.WordSize;
        if (at != Target.Current.DescriptorBytes || !_data.TryGetValue(table, out DataItem? d) || d.Bytes.Length < at
            || !d.Relocs.Any(r => r.Offset == DescSelf * w && r.Symbol == table && r.Addend == 0))
            return false;
        return (Word(d, DescFlags * w) & 1) != 0;
    }

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

    /// <summary>
    /// WHAT A READ AT ANY OFFSET READS: every cell of the object -- but the
    /// stamp of one a descriptor stamps, which an element read never reads.
    /// The word at offset 0 of such an object is written only by its stamp
    /// (Stamp: one store of the descriptor's address there, from the one
    /// writer of the site's register); a store at any offset still reaches
    /// every read through the any-offset cell. The descriptor is read-only
    /// data, read at its own offset (a virtual call's slot, a cast's check),
    /// and nothing is ever stored through it. Read with the elements, the
    /// address was the unknown object in every element read: every string a
    /// string[] held (Split's words) was the unknown, and so was every number
    /// an array of tuples held beside a reference -- 1180's split positions,
    /// with which the list of tokens was written: each element the address
    /// of a list's element was made from, as an index, was stored where
    /// nobody follows. The link's solver reads the same (RegionSolver.ReadAnywhere).
    /// </summary>
    private int ReadAnywhere(int o)
    {
        if (_collapsed[o] || Stamp(o) is null) return AllCells(o);
        if (_readCells[o] >= 0) return _readCells[o];
        Cell(o, Any);
        int read = NewNode();
        _readCells[o] = read;
        if (HoldsNoReference(o, Any)) _noReference[read] = true;
        foreach (var (offset, cell) in _cells[o].ToArray()) if (offset != 0) Edge(cell, read, 0);
        return read;
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
        => _saved.TryGetValue(f, out List<VReg>? known) ? known : _saved[f] = Saved(f, frame);

    /// <summary>SavedAcrossSuspensions, found afresh: the unit's summary for the link asks it too (RegionSummary).</summary>
    internal static List<VReg> Saved(Function f, AsyncFrame frame)
    {
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
        return saved.ToList();
    }

    private void Edge(int from, int to, long shift)
    {
        from = Find(from);
        to = Find(to);
        if (from == to && shift == 0) return;
        // A node that holds no reference holds the unknown at most (Held),
        // and gives no more -- and only once something is put in it, as the
        // link's solver has it (RegionSolver.Edge): a word only ever written
        // numbers holds nothing anyone follows. Given the unknown along every
        // such edge at once, a list's count was the unknown, so was the
        // address of its element at that count, and every element List.Add
        // stored was stored where nobody follows: 1180's tokens, 1100's.
        if (!NewEdge(from, to, shift)) return;
        _edgeCount++;
        _from = from;
        LocSet held = _pts[from];
        if (from == to) { foreach (int id in held.Ids()) AddShifted(to, _locs[id], shift); return; }
        // Walked in place: what is added goes to another node.
        if (held.Bits is { } bits)
        {
            for (int w = 0; w < bits.Length; w++)
                for (ulong word = bits[w]; word != 0; word &= word - 1)
                {
                    int id = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(word);
                    if (shift == 0) Held(to, id); else AddShifted(to, _locs[id], shift);
                }
            return;
        }
        for (int k = 0; k < held.Count; k++)
            if (shift == 0) Held(to, held.Few![k]); else AddShifted(to, _locs[held.Few![k]], shift);
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

    // For a report: the node being carried on, and per object, the node that
    // first put it where nobody follows.
    private int _from = -1;
    private readonly Dictionary<int, int> _escapedFrom = new();

    /// <summary>A node, for a report: the function copy whose register (or return) it is, or a cell.</summary>
    private string DescribeNode(int node)
    {
        if (node < 0) return "nothing seen";
        for (int c = _copyBase.Count - 1; c >= 0; c--)
            if (_copyBase[c] <= node)
            {
                (Function f, int context) = _copies[c];
                int r = node - _copyBase[c];
                if (r > f.RegCount) return "a cell";
                string name = r == f.RegCount ? "its return" : f.Params.FirstOrDefault(p => p.Id == r) is { } p ? "parameter " + p.Name : "register " + r;
                return f.Name + " context " + context + " " + name;
            }
        return "a node";
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
        // For a report: what first put each object where nobody follows.
        if (Report is not null && ObjectOf(_locs[id]) != Global && node == Find(Cell(Global, Any)))
            _escapedFrom.TryAdd(ObjectOf(_locs[id]), _from);
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
            long at = OffsetOf(l) == Any ? Any : IsStrided(OffsetOf(l)) ? StridedPlus(OffsetOf(l), offset) : Plain(OffsetOf(l) + offset);
            Edge(at == Any ? ReadAnywhere(o) : Cell(o, at), dest, 0);
        });
    }

    private void Store(int baseNode, long offset, int value)
    {
        if (baseNode < 0 || value < 0) return;
        Watch(baseNode, l =>
        {
            int o = ObjectOf(l);
            long at = o == Global || OffsetOf(l) == Any ? Any : IsStrided(OffsetOf(l)) ? StridedPlus(OffsetOf(l), offset) : Plain(OffsetOf(l) + offset);
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
                // The address an index scaled by 2^k is added to moves by a
                // multiple of 2^k: a word at a residue (Strided), not anywhere.
                int scale = i.Op == Opcode.Add ? i.Operands.Max(o => IndexScale(f, i, o)) : 0;
                foreach (Operand o in i.Operands)
                    if (Value(copy, o) is int v and >= 0)
                        Edge(v, dest, IndexScale(f, i, o) is int k and > 0 ? IndexShift + k : scale > 0 ? MovedBy + scale : by);
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
                // Characters or bytes (Instr.Number) move no address.
                if (i.Number) return;
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
                // THROUGH A TABLE NAMED OUTRIGHT -- the method read from a
                // descriptor the devirtualiser put in place of the receiver's
                // first word (`%t = copy @t_T+48; %m = load %t +108`): the one
                // method there, or, where the descriptor has none in the slot,
                // no call at all. Run as every target, a view's Count handed
                // the view, and the list's new array with it, to code nobody
                // follows.
                if (ConstantTable(f, i) is var (table, slotAt) && _data.TryGetValue(table, out DataItem? named))
                {
                    foreach (DataReloc rel in named.Relocs)
                        if (rel.Offset == slotAt && rel.Addend == 0) { Bind(copy, i, rel.Symbol, 1); return; }
                    if (!named.Relocs.Any(rel => rel.Offset == slotAt)) return;
                    Indirect(copy, i);
                    return;
                }
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
                        {
                            foreach (DataReloc rel in d.Relocs)
                                if (rel.Offset == at + slot && rel.Addend == 0) { Bind(copy, i, rel.Symbol, 1, o); return; }
                            // NO METHOD IN THE SLOT: its type does not have
                            // the method -- an interface it does not implement,
                            // tested for first -- and the call is never made
                            // on it. Run as every target, it was a call nobody
                            // follows: 1180's view of an array, which a List
                            // is made from when it is an ICollection<T>, and
                            // its CopyTo was handed the list's new array.
                            if (!d.Relocs.Any(rel => rel.Offset == at + slot)) return;
                        }
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
            // AN ELEMENT OF AN ARRAY OF STRUCTS copied in or out, word by word
            // at its residues (IsStrided): the struct a list's indexer hands
            // back, the one its Add stores.
            if ((IsStrided(ds) || IsStrided(dd)) && ds != Any && dd != Any && od != Global && count != Any && count <= FarthestField)
            {
                int w = Target.Current.WordSize;
                for (long k = 0; k < count; k += w)
                {
                    long from = IsStrided(ds) ? StridedPlus(ds, k) : Plain(ds + k), into = IsStrided(dd) ? StridedPlus(dd, k) : Plain(dd + k);
                    Edge(Cell(os, from), Cell(od, into), 0);
                }
                Edge(Cell(os, Any), Cell(od, Any), 0);
                return;
            }
            if (IsStrided(ds)) ds = Any;
            if (IsStrided(dd)) dd = Any;
            if (ds == Any || dd == Any || od == Global)
            {
                // From any offset, what a read there reads (ReadAnywhere): a
                // struct copied out of an array of them.
                if (ds == Any ? anyPairs.Add((~os, od)) : anyPairs.Add((os, od))) Edge(ds == Any ? ReadAnywhere(os) : AllCells(os), Cell(od, Any), 0);
                return;
            }
            EachCell(os, (offset, cell) =>
            {
                // An element's word (IsStrided) copied as a block: to the
                // destination's word at the same residue, moved as the block is.
                if (IsStrided(offset)) Edge(cell, Cell(od, StridedPlus(offset, dd - ds)), 0);
                else if (offset >= ds && (count == Any || offset - ds < count)) Edge(cell, Cell(od, Plain(dd + offset - ds)), 0);
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
            else Edge(OffsetOf(src) == Any ? ReadAnywhere(ObjectOf(src)) : AllCells(ObjectOf(src)), through, 0);
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

    /// <summary>Runtime.InvalidCastTo(object, string): a failed cast's throw.</summary>
    internal const string FailedCast = "m_Runtime_InvalidCastTo_2_V$Any_V$String";

    /// <summary>
    /// The runtime's type checks made with types known only at run time: a
    /// reference stored into an array of a shared generic (ArrayStoreCheck),
    /// `x is T[]` (ArrayOf), and a shared copy's tests (DescribedAs,
    /// ShapedAs). Each reads descriptors and answers yes or no, or throws
    /// what it makes; none keeps what it is handed (Escape.KeepsNothing).
    /// </summary>
    private static readonly string[] TypeChecks =
    {
        "m_Runtime_ArrayStoreCheck_2_V$I64_V$I64",
        "m_Runtime_ArrayOf_2_V$I64_V$I64",
        "m_Runtime_DescribedAs_2_V$I64_V$I64",
        "m_Runtime_ShapedAs_7_V$I64_V$I64_V$I64_V$I64_V$I64_V$I64_V$I64",
    };

    // The collector's notes and the runtime's frees keep no pointer.
    internal static bool Harmless(string callee) =>
        Opt.Escape.IsCollectorNote(callee) || callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive
        || callee.StartsWith("m_Runtime_Free", StringComparison.Ordinal)
        // NOR GROWING AN ARRAY WHERE IT LIES (Runtime.GrowInPlace): it is handed
        // the array's address as a number, writes the array's own length and
        // its region's top, and keeps nothing. Followed, that number reached
        // the region's raw bookkeeping, every List's array was taken for the
        // unknown object's, and every element any list ever held with it --
        // a front end's tokens and tree all went to the heap (1200).
        || callee.StartsWith("m_Runtime_GrowInPlace", StringComparison.Ordinal)
        // NOR DOES A FAILED CAST'S THROW (Escape.KeepsNothing): its exception
        // names the object's type, reads nothing else of it, and keeps none
        // of it; what it makes it throws. Followed as a call, it is a member
        // of the runtime's cycle of exceptions, traces and symbol lookups, and
        // unified there its parameter is one class with everything every
        // cast in that cycle hands it, the unknown object among them: every
        // object any boundary cast -- `(Leaf)r.Child` -- went to the heap.
        || callee == FailedCast
        // NOR THE RUNTIME'S TYPE CHECKS (TypeChecks), for the same reason:
        // the store check every List of a reference type makes as it adds,
        // followed into the same cycle, handed every Row a sheet's list held
        // and every array it grew into to the unknown object (1290).
        || Array.IndexOf(TypeChecks, callee) >= 0
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
            (_callTargets.TryGetValue((copy, i), out HashSet<int>? targets) ? targets : _callTargets[(copy, i)] = new()).Add(callee);
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
    // The copies every call reaches, direct or indirect, from each copy that
    // makes it.
    private readonly Dictionary<(int Copy, Instr Call), HashSet<int>> _callTargets = new();

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
    // The descriptor a call's method is read from when it is named outright,
    // and the offset in it the method is read at; null when it is read through
    // the receiver or any other way.
    private (string Table, long At)? ConstantTable(Function f, Instr i)
    {
        if (i.Operands.Count < 2 || i.Operands[0] is not RegOperand { Reg: var target }) return null;
        if (Single(f, target) is not { Op: Opcode.Load, Operands: [RegOperand { Reg: var table }] } method) return null;
        if (Single(f, table) is not { Op: Opcode.Copy, Operands: [SymOperand { Name: var name, Offset: var at }] }) return null;
        if (!(name.StartsWith("t_", StringComparison.Ordinal) || name.StartsWith("b_", StringComparison.Ordinal))) return null;
        return (name, at + method.Offset);
    }

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
                            || i.Operands[0] is RegOperand { Reg: var at0 } && AddressOf(obj.F, at0, obj.Slot, 0)
                        : i.Operands[0] is RegOperand { Reg: var to } && Derives(obj.F, to, made!, 0)))
                {
                    if (found is { } f0 && (f0.Item1 != t || f0.Item2 != at)) many = true;
                    found = (t, at);
                }
        return _stamps[key] = many ? null : found;
    }

    // Whether register `r` is the address of frame slot `slot`: an object
    // the lifetime passes placed in the frame is stamped through a register
    // that copies the slot's address (`%o = copy &slot; store %o @t_T+48`),
    // as a site's object is through the site's register -- a view of an
    // array an interface call is made on (ICollection<T>.CopyTo, 1180).
    private bool AddressOf(Function f, VReg r, FrameSlot slot, int depth)
    {
        if (depth > 3 || Single(f, r) is not { Op: Opcode.Trunc64 or Opcode.Copy or Opcode.ZExt32 or Opcode.SExt32 } w) return false;
        return w.Operands[0] is SlotOperand { Slot: var s } ? s == slot
            : w.Operands[0] is RegOperand { Reg: var from } && AddressOf(f, from, slot, depth + 1);
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
            _from = node;
            // Merges owe a node what each side lacked, often the same
            // locations many times over: each is carried once.
            if (_owedTwice.Remove(node) || delta.Count > _pts[node].Count) delta = Distinct(delta);
            if (_edges[node] is { } edges)
                for (int e = 0; e < edges.Count; e++)
                {
                    (int to, long shift) = edges[e];
                    if (shift == 0) foreach (int id in delta) Held(to, id);
                    else foreach (int id in delta) AddShifted(to, _locs[id], shift);
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
            // A cycle of k nodes merges into one node k times, and each time
            // it is owed nearly all the set it has grown to: k copies of the
            // set before the node is next solved. Past the set's own size the
            // owed list is made distinct here, so it never holds more than
            // twice what the node holds (1323 asked for a list of 192 MB).
            if (delta.Count > 2 * _pts[into].Count + 64) _delta[into] = Distinct(delta);
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
        // --region-report copies: every function copy, in the order it was
        // made, with its nodes and the locations they hold -- what two builds
        // of the compiler that part on a program are compared by.
        if (wanted.Contains("copies"))
            for (int c = 0; c < _copies.Count; c++)
            {
                int end = c + 1 < _copyBase.Count ? _copyBase[c + 1] : _pts.Count;
                long held = 0;
                for (int k = _copyBase[c]; k < end; k++) held += _pts[k].Count;
                Console.Error.WriteLine($"regions: copy {c} {_copies[c].F.Name} context {_copies[c].Context} nodes {end - _copyBase[c]} held {held}");
            }
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
                lines.Add($"  {(outlives ? "outlives" : "local   ")} {obj.F!.Name} line {obj.Site.Line} {TypeOf(o)}{(IsCallContext(obj.Context) ? " called from line " + CallContextAt(obj.Context).Site.Line : "")}"
                    + (!outlives ? "" : _fromGlobal!.Contains(o) ? " (reached from the unknown object" + EscapeWay(o) + ")" : " (kept by what the boundary is handed or hands back)"));
            }
            Console.Error.WriteLine($"regions: boundary {f.Name} ctx {context}: {local} local, {kept} outlive it");
            foreach (string line in lines) Console.Error.WriteLine(line);
            // WHY THE PROGRAM'S OWN OBJECTS OUTLIVE IT: the chain from what
            // keeps each -- a static, what the boundary is handed or hands
            // back -- to it.
            Dictionary<int, int> from = WhyReached(c);
            SortedSet<string> why = new(StringComparer.Ordinal);
            for (int o = 1; o < _objects.Count; o++)
            {
                var obj = _objects[o];
                if (obj.Site is null || obj.F!.FromLibrary || !reached.Contains(o) || !beneath.Contains(CopyIdOfObject(o))) continue;
                List<string> chain = new();
                for (int at = o, n = 0; n < 12 && from.TryGetValue(at, out int up); at = up, n++)
                {
                    chain.Add(Describe(at));
                    if (up < 0) { chain.Add(up == -1 ? "a static, or code nobody follows" : "the boundary's parameters or return"); break; }
                }
                chain.Reverse();
                why.Add("  kept    " + string.Join(" -> ", chain));
            }
            foreach (string line in why) Console.Error.WriteLine(line);
        }
    }

    // For each object copy `c`'s Outliving reaches: the object it was reached
    // from, -1 for Global itself, -2 for what `c` is handed or hands back.
    private Dictionary<int, int> WhyReached(int c)
    {
        Function f = _copies[c].F;
        Dictionary<int, int> from = new();
        Queue<int> next = new();
        from[Global] = -1;
        next.Enqueue(Global);
        void Start(long l)
        {
            if (from.TryAdd(ObjectOf(l), -2)) next.Enqueue(ObjectOf(l));
        }
        while (next.TryDequeue(out int o))
            foreach (int to in _pointsInto![o])
                if (from.TryAdd(to, o)) next.Enqueue(to);
        foreach (VReg p in f.Params) foreach (long l in _pts[Reg(c, p)]) Start(l);
        foreach (long l in _pts[ReturnNode(c)]) Start(l);
        while (next.TryDequeue(out int o))
            foreach (int to in _pointsInto![o])
                if (from.TryAdd(to, o)) next.Enqueue(to);
        return from;
    }

    // An object as the report names it: its site, and the object or call it
    // was made for.
    private string Describe(int o, int depth = 0)
    {
        var obj = _objects[o];
        if (o == Global) return "Global";
        string what = obj.Site is not null ? $"{obj.F!.Name} line {obj.Site.Line} {TypeOf(o)}"
            : obj.Slot is not null ? $"slot {obj.Slot.Name} of {obj.F!.Name}" : "?";
        if (obj.Site is not null && obj.Context >= 0 && depth < 2) what += " [of " + Describe(obj.Context, depth + 1) + "]";
        else if (obj.Site is not null && IsCallContext(obj.Context)) what += " [called from line " + CallContextAt(obj.Context).Site.Line + "]";
        else if (obj.Site is not null && obj.Context == -1 && IsInstance(obj.F!)) what += " [no object's]";
        return what;
    }

    private Dictionary<int, int>? _escapeParent;

    // For a report: the way from the unknown object to `o`, by its first
    // object, and what put that one where nobody follows.
    private string EscapeWay(int o)
    {
        if (_escapeParent is null)
        {
            _escapeParent = new() { [Global] = -1 };
            Queue<int> next = new();
            next.Enqueue(Global);
            while (next.TryDequeue(out int at))
                foreach (int held in _pointsInto![at])
                    if (_escapeParent.TryAdd(held, at)) next.Enqueue(held);
        }
        int first = o, hops = 0;
        while (_escapeParent.TryGetValue(first, out int parent) && parent != Global && parent >= 0 && hops < 64) { first = parent; hops++; }
        var obj = _objects[first];
        string what = obj.Site is not null ? obj.F!.Name + " line " + obj.Site.Line + " " + TypeOf(first) : "an object";
        return (hops > 0 ? ", through " + what + (hops > 1 ? " and " + (hops - 1) + " more" : "") : "")
            + ", put there by " + (_escapedFrom.TryGetValue(first, out int from) ? DescribeNode(from) : "unknown");
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
