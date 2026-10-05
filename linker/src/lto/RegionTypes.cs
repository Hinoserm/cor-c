namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT TYPES A VIRTUAL CALL'S RECEIVER CAN HOLD, over the whole image, and
/// with them the targets it can run: a pass before the escape graphs
/// (RegionEscape) and the judge are built, which then follow only those. A
/// virtual call's symbol stands for every override of its slot on every
/// type the image makes (VirtualTargets); most calls are made on a receiver
/// that only ever holds one or two of them -- an enumerator straight back
/// from GetEnumerator, a node a parser made -- and following the rest joins
/// unrelated code into one cycle.
///
/// AN INCLUSION SOLVE OVER TYPES, NOT OBJECTS: every site stamped with one
/// descriptor (RegionSite.Table, At) is one abstract object, whatever
/// function made it; what a function cannot type -- a site of no stamp, its
/// frame slots -- is one more of its own; and two stand for everything else:
/// the unknown object (Unknown, what nobody follows) and a constant (a
/// Symbol the link found read-only, RegionConstants). Each abstract object
/// has A CELL A FIELD, by its offset from the object's start (one type's
/// objects lay a field out at one offset), and one for any offset: what is
/// written where nobody can say, read by every load of it. A pointer moved
/// into an object (a copy by a shift, an index) is an address INTO it: a
/// load through one reads every cell of the object, a store through one
/// writes its any-offset cell, and a call made on one runs on the object.
/// Over them, every constraint of every function as RegionEscape reads it:
/// Site, Slot, Unknown, Symbol, Copy, Load, Store, MemCopy, Leak. A call binds its arguments to its targets' parameters and
/// their return to its result; a call nobody can name hands its arguments
/// to the unknown and gives back the unknown. A function called from where
/// nobody follows (rooted) has the unknown for every parameter, and its
/// return goes where nobody follows.
///
/// WHAT GOES WHERE NOBODY FOLLOWS MAY BE WRITTEN BY ANYTHING: an object that
/// reaches the unknown -- leaked, stored into the unknown object, handed to
/// a call nobody can name or returned by a root -- has the unknown object
/// in its any-offset cell, and everything its cells hold reaches the unknown too. Code
/// nobody follows can only reach what was handed to it, so an object that
/// never reaches it holds only what the constraints put there.
///
/// A VIRTUAL CALL BINDS ITS TARGETS AS ITS RECEIVER'S TYPES ARRIVE (on the
/// fly): for a typed object, what that descriptor holds at the slot
/// (Dispatch, as RegionSolver.TargetsOnSite answers it: nothing for a
/// descriptor not of the call's type, the one method among the targets, or
/// null -- any); for the unknown object, a constant or an untyped object,
/// every target. A receiver nothing reaches is given every target too, once
/// the solve has settled, and the solve runs again: a call is never left
/// with fewer targets for want of a type its receiver was never seen to get.
///
/// Sound as the escape engine is: it trusts the same constraints to say
/// everything a node can hold, and the same stamps to say what an object
/// runs. Coarser than it everywhere -- one object a type, a field one cell
/// for every object of it -- and so never fewer types than a receiver holds.
/// </summary>
public sealed class RegionTypes
{
    // A value a node holds: an object, shifted left one, its low bit set for
    // an address into it. The unknown object and a constant are objects 0
    // and 1, never addresses into them.
    private const int Top = 0, Constant = 2;
    private static int ObjectOf(int value) => value >> 1;
    private static bool Into(int value) => (value & 1) != 0;

    // A field further than this is any offset, as the escape engine has it.
    private const int FarthestField = 4096;
    private const int AnyOffset = -1;

    private readonly IReadOnlyList<RegionFunction> _functions;
    private readonly int[]?[][] _targets;
    private readonly string?[][] _keys;
    private readonly bool[] _rooted;
    private readonly Func<int, int, string, long, int[]?> _dispatch;

    // NODES BY INSTANCE: each function's own copy (instance f is function f),
    // and for an instance method of a shared generic copy -- List<__canon>'s
    // Add, run for every List of references -- one more copy for each
    // descriptor its `this` arrives with (InstanceFor), whose sites make
    // objects of that copy's own. Context-insensitive over the one shared
    // body, every List's elements were every other's: the compiler's own
    // link carried String.Equals two thousand types and gave up.
    private readonly List<int> _instBase = new(), _instF = new();
    private readonly List<string?> _instCtx = new();
    private readonly Dictionary<(int, string), int> _instOf = new();
    private readonly int[] _instances;
    private const int MostInstances = 256;
    private readonly List<ValueSet?> _pts = new();
    private readonly List<ValueSet?> _delta = new();
    // What else a node is, made only for the nodes that are anything more
    // than held: copied into others as it is, or as addresses into what it
    // holds (moved), an address loaded or stored through, a receiver.
    private sealed class Uses
    {
        public List<int>? Succ, Moved, Receives;
        public List<(int To, int Offset)>? Loads;
        public List<(int Value, int Offset)>? Stores;
    }
    private readonly List<Uses?> _uses = new();
    private readonly Queue<int> _work = new();
    private readonly int _sink;

    // Objects: the unknown one, a constant, then typed and untyped ones; each
    // one's cells by offset, and the nodes that read every cell of it.
    private readonly List<(string? Table, long At)> _objects = new();
    private readonly Dictionary<long, int> _cells = new();
    private readonly List<List<int>?> _cellsOf = new();
    private readonly List<HashSet<int>?> _allReaders = new();
    private readonly Dictionary<(string, long), int> _typed = new();
    private readonly List<int> _untyped = new(), _untypedSites = new();
    private readonly Dictionary<(string, long, int), int> _typedIn = new();
    private readonly HashSet<int> _escaped = new();

    // Virtual calls, and direct calls into a shared copy's instance method
    // (routed by what `this` is): instance, call, the one target of a direct
    // call (-1 for a virtual one), whether every target is bound, the
    // functions bound and the instances bound.
    private readonly List<(int F, int K)> _calls = new();
    private readonly List<int> _routed = new();
    private readonly List<HashSet<int>> _boundInst = new();
    private readonly List<bool> _full = new();
    private readonly List<HashSet<int>> _bound = new();
    private readonly Dictionary<(int, int, int), int[]?> _dispatched = new();

    private long _steps;
    /// <summary>Past this many values carried, the pass gives up and prunes nothing.</summary>
    ///
    /// SCALED TO THE PROGRAM: thirty values carried a node, between two and
    /// twenty million. Context-insensitive over a shared generic copy, every
    /// List's elements are every other's, and on the compiler's own link the
    /// solve carried a hundred million values in six gigabytes, not done,
    /// where a small program is settled in a few hundred thousand.
    public long Budget { get; init; } = -1;
    private long _budget;
    /// <summary>For a report: the values carried.</summary>
    public long Steps => _steps;

    /// <summary>For a report: the virtual calls, those narrowed, the targets before and after, those left every target for an unknown receiver, and the abstract objects.</summary>
    public int Calls, Narrowed, Before, After, Unknown, Objects;
    /// <summary>For a report: the copies made of shared methods, for the descriptors their `this` arrived with.</summary>
    public int Instances => _instBase.Count - _functions.Count;
    /// <summary>For a report: a line every 50 million values carried, with the largest nodes.</summary>
    public Action<string>? Progress { get; init; }

    private void ReportProgress()
    {
        long held = 0;
        List<(int Node, int Count)> largest = new();
        for (int n = 0; n < _pts.Count; n++)
            if (_pts[n] is { } set)
            {
                held += set.Count;
                if (largest.Count < 8 || set.Count > largest[^1].Count)
                {
                    largest.Add((n, set.Count));
                    largest.Sort((a, b) => b.Count.CompareTo(a.Count));
                    if (largest.Count > 8) largest.RemoveAt(8);
                }
            }
        Progress!($"receiver types: {_steps / 1_000_000}M carried, {_pts.Count} nodes, {_objects.Count} objects, {held} held, {_work.Count} queued, heap {GC.GetTotalMemory(false) >> 20} MB; largest "
            + string.Join("; ", largest.Select(l => NodeName(l.Node) + " " + l.Count)));
    }

    /// <summary>For a report: the functions whose calls left every target are explained (Explained).</summary>
    public Func<int, bool>? Explain { get; init; }
    public List<string> Explained { get; } = new();
    /// <summary>Whether it gave up for its budget.</summary>
    public bool GaveUp;

    /// <summary>
    /// ROOTS CALLED ONLY BLIND, AS A METHOD A DESCRIPTOR HOLDS (RegionSolver):
    /// their `this` is not the unknown object but every object of a type
    /// that may run them (Receives: the type holds the method at a slot, or
    /// derives from one that does), every object of no known type an
    /// allocation made (a site whose stamp the hints could not say), and
    /// the unknown object only where an object of such a type
    /// is made where no site is (ThisMadeOutside: stamped in data, or by
    /// code with no IR). A method read out of a descriptor is called on an
    /// object of that descriptor's type, as every virtual call is; a static
    /// method is in no slot, and a call on a base's method by name is a
    /// call the solve follows. Its other parameters are the unknown object
    /// still.
    /// </summary>
    public bool[]? TypedThis { get; init; }
    public Func<int, string, long, bool>? Receives { get; init; }
    public Func<int, bool>? ThisMadeOutside { get; init; }

    // The objects of no known type made by an allocation (not a frame slot, nor a leaf).
    private readonly HashSet<int> _untypedMade = new();

    /// <summary>
    /// The pass over <paramref name="functions"/>, whose calls' targets are
    /// <paramref name="targets"/> (null: a call nobody can name) and whose
    /// virtual calls are those with a key (<paramref name="keys"/>).
    /// <paramref name="dispatch"/> says what call k of function f runs on an
    /// object stamped with a descriptor and the offset its method table
    /// starts at: the targets among the call's, empty for none, null for any.
    /// </summary>
    public RegionTypes(IReadOnlyList<RegionFunction> functions, int[]?[][] targets, string?[][] keys, bool[] rooted,
        Func<int, int, string, long, int[]?> dispatch)
    {
        _functions = functions; _targets = targets; _keys = keys; _rooted = rooted; _dispatch = dispatch;
        _instances = new int[functions.Count];
        for (int f = 0; f < functions.Count; f++) NewInstance(f, null);
        _sink = NewNode();
        NewObject(null, 0);         // the unknown object
        NewObject(null, 0);         // a constant
    }

    private int NewNode()
    {
        _pts.Add(null); _delta.Add(null); _uses.Add(null);
        return _pts.Count - 1;
    }

    private int Node(int i, int n) => _instBase[i] + n;

    // A copy of function f's nodes, for `this` of descriptor ctx (null: the function's own).
    private int NewInstance(int f, string? ctx)
    {
        int i = _instBase.Count;
        _instBase.Add(_pts.Count); _instF.Add(f); _instCtx.Add(ctx);
        _untyped.Add(-1); _untypedSites.Add(-1);
        for (int n = 0; n < _functions[f].Nodes; n++) NewNode();
        return i;
    }

    // Whether a function is an instance method of a shared generic copy, run for every type argument of references.
    private bool Shared(int f) => _functions[f].Instance && _functions[f].Parameters > 0 && _functions[f].Name.Contains("$__canon", StringComparison.Ordinal);

    // The instance of g a call runs on an object: for a shared copy's method
    // and a typed object, the copy for its descriptor -- made, and its
    // constraints stated, the first time -- past MostInstances the function's own.
    private int InstanceFor(int g, int o)
    {
        if (!Shared(g) || o <= 1 || _objects[o].Table is not { } table) return g;
        if (_instOf.TryGetValue((g, table), out int i)) return i;
        if (_instances[g] >= MostInstances) return g;
        _instances[g]++;
        i = NewInstance(g, table);
        _instOf[(g, table)] = i;
        Instantiate(i);
        return i;
    }
    private Uses UsesOf(int node) => _uses[node] ??= new();
    private bool Has(int f, int n) => n >= 0 && n < _functions[f].Nodes;

    private int NewObject(string? table, long at)
    {
        _objects.Add((table, at));
        _cellsOf.Add(null); _allReaders.Add(null);
        return _objects.Count - 1;
    }

    // A closed static's storage: one untyped object a symbol, in no function.
    private readonly Dictionary<int, int> _staticObjects = new();
    private int Static(int index) => _staticObjects.TryGetValue(index, out int o) ? o : _staticObjects[index] = NewObject(null, 0);

    /// <summary>The objects laid down in data (RegionConstants.DataObject), by the place an Unknown constraint's C names one past.</summary>
    public IReadOnlyList<(string Table, long At)>? DataObjects { get; init; }

    // An object laid down in data: of its descriptor's type, an object of its
    // own, and what its words hold -- laid down before the program ran -- the
    // unknown object as well as whatever the program stores there.
    private readonly Dictionary<int, int> _dataObjectsMade = new();
    private int DataObject(int index)
    {
        if (_dataObjectsMade.TryGetValue(index, out int o)) return o;
        o = _dataObjectsMade[index] = NewObject(DataObjects![index].Table, DataObjects[index].At);
        Add(Cell(o, AnyOffset), Top);
        return o;
    }

    private int Typed(string table, long at) => _typed.TryGetValue((table, at), out int o) ? o : _typed[(table, at)] = NewObject(table, at);
    // What a site makes in instance i: its descriptor's object, or in a copy
    // for a `this` of its own, an object of that copy's (a List's array, made
    // in Grow, is that List's).
    private int TypedIn(string table, long at, int i)
    {
        if (_instCtx[i] is null) return Typed(table, at);
        return _typedIn.TryGetValue((table, at, i), out int o) ? o : _typedIn[(table, at, i)] = NewObject(table, at);
    }
    // AN ARRAY BY ITS SITE: of its descriptor's type, an object of its own.
    // One object a descriptor made every object[] in a program one array,
    // every element stored in any of them read out of all -- a params
    // array, Array.Copy's buffers, a List's storage.
    private readonly Dictionary<(int, int), int> _arrays = new();
    private int ArrayAt(string table, long at, int i, int site)
        => _arrays.TryGetValue((i, site), out int o) ? o : _arrays[(i, site)] = NewObject(table, at);

    private int Untyped(int i) => _untyped[i] >= 0 ? _untyped[i] : _untyped[i] = NewObject(null, 0);
    // An allocation of no known stamp: of its instance's own, apart from its frame slots.
    private int UntypedMade(int i)
    {
        if (_untypedSites[i] >= 0) return _untypedSites[i];
        int o = _untypedSites[i] = NewObject(null, 0);
        _untypedMade.Add(o);
        return o;
    }

    private static int Plain(long offset) => offset < 0 || offset > FarthestField ? AnyOffset : (int)offset;

    /// <summary>The cell of an object's field at an offset (AnyOffset: written where nobody can say); every node reading all of the object reads it too.</summary>
    private int Cell(int o, int offset)
    {
        long key = (long)o << 32 | (uint)offset;
        if (_cells.TryGetValue(key, out int cell)) return cell;
        _cells[key] = cell = NewNode();
        (_cellsOf[o] ??= new()).Add(cell);
        if (_allReaders[o] is { } readers) foreach (int reader in readers.ToArray()) Edge(cell, reader);
        return cell;
    }

    /// <summary>Every cell of an object, those made later too, into a node.</summary>
    private void AllCells(int o, int to)
    {
        if (!(_allReaders[o] ??= new()).Add(to)) return;
        Cell(o, AnyOffset);
        foreach (int cell in _cellsOf[o]!.ToArray()) Edge(cell, to);
    }

    // For a report (Explain): where each node first got the unknown object.
    private readonly Dictionary<int, (int From, string? Why)> _topFrom = new();
    private int _source = -1;
    private string? _sourceWhy;

    private void Add(int node, int value)
    {
        if (!(_pts[node] ??= new()).Add(value)) return;
        if (value == Top && Explain is not null) _topFrom.TryAdd(node, (_source, _sourceWhy));
        if (++_steps > _budget) throw new OverBudget();
        if (Progress is not null && _steps % 50_000_000 == 0) ReportProgress();
        ValueSet delta = _delta[node] ??= new();
        if (delta.Count == 0) _work.Enqueue(node);
        delta.Add(value);
    }

    // An address into an object, of what a node holds: the unknown object and a constant stay what they are.
    private static int Moved(int value) => ObjectOf(value) <= 1 ? value : value | 1;

    private void Edge(int from, int to)
    {
        if (from == to) return;
        (UsesOf(from).Succ ??= new()).Add(to);
        if (_pts[from] is { } held) { int was = _source; _source = from; foreach (int v in held.ToArray()) Add(to, v); _source = was; }
    }

    private void MovedEdge(int from, int to)
    {
        (UsesOf(from).Moved ??= new()).Add(to);
        if (_pts[from] is { } held) { int was = _source; _source = from; foreach (int v in held.ToArray()) Add(to, Moved(v)); _source = was; }
    }

    private void Load(int to, int address, int offset)
    {
        (UsesOf(address).Loads ??= new()).Add((to, offset));
        if (_pts[address] is { } held) { int was = _source; _source = address; foreach (int v in held.ToArray()) Loaded(to, v, offset); _source = was; }
    }

    private void Store(int address, int value, int offset)
    {
        (UsesOf(address).Stores ??= new()).Add((value, offset));
        if (_pts[address] is { } held) { int was = _source; _source = address; foreach (int v in held.ToArray()) Stored(v, value, offset); _source = was; }
    }

    private void Loaded(int to, int value, int offset)
    {
        int o = ObjectOf(value);
        if (value == Top) Add(to, Top);
        else if (value == Constant) Add(to, Constant);
        else if (Into(value) || offset == AnyOffset) AllCells(o, to);
        else { Edge(Cell(o, offset), to); Edge(Cell(o, AnyOffset), to); }
    }

    private void Stored(int value, int stored, int offset)
    {
        // A constant is read-only; what goes into the unknown object goes where nobody follows.
        if (value == Top) ToSink(stored, "stored into the unknown object by " + _storedBy.GetValueOrDefault(stored, "?"));
        else if (value != Constant) Edge(stored, Cell(ObjectOf(value), Into(value) ? AnyOffset : offset));
    }

    // For a report (Explain): each node that goes where nobody follows, and why.
    private readonly List<(int Node, string Why)> _sinkFrom = new();
    private readonly Dictionary<int, string> _storedBy = new();

    private void ToSink(int node, string why)
    {
        if (Explain is not null) _sinkFrom.Add((node, why));
        Edge(node, _sink);
    }

    // A node, for a report: a function's own, or an object's cell.
    private Dictionary<int, (int O, int Offset)>? _cellNames;
    private string NodeName(int node)
    {
        for (int i = 0; i < _instBase.Count; i++)
            if (node >= _instBase[i] && node < _instBase[i] + _functions[_instF[i]].Nodes)
                return _functions[_instF[i]].Name + (_instCtx[i] is { } ctx ? " [" + ctx + "]" : "") + " node " + (node - _instBase[i]);
        _cellNames ??= _cells.ToDictionary(pair => pair.Value, pair => ((int)(pair.Key >> 32), (int)(uint)pair.Key));
        if (_cellNames.TryGetValue(node, out var cell)) return (_objects[cell.O].Table ?? (cell.O == 0 ? "unknown" : "untyped")) + " cell " + cell.Offset;
        return node == _sink ? "where nobody follows" : "node " + node;
    }

    /// <summary>For a report: how the unknown object came to a function's node, back to where it began.</summary>
    public string WhyUnknown(string function, int local)
    {
        int f = -1;
        for (int g = 0; g < _functions.Count; g++) if (_functions[g].Name.Contains(function, StringComparison.Ordinal)) { f = g; break; }
        if (f < 0) return function + ": no such function";
        // A negative local: call -local - 1's receiver.
        if (local < 0) local = _functions[f].Calls[-local - 1].Arguments[0];
        int node = Node(f, local);
        List<string> chain = new() { NodeName(node) };
        HashSet<int> seen = new() { node };
        while (_topFrom.TryGetValue(node, out var came))
        {
            if (came.From < 0) { chain.Add(came.Why ?? "?"); break; }
            if (!seen.Add(came.From)) { chain.Add("(a cycle)"); break; }
            node = came.From;
            chain.Add(NodeName(node));
            if (chain.Count > 60) break;
        }
        return string.Join(" <- ", chain);
    }

    /// <summary>For a report: why an object of a descriptor reaches the unknown, through what holds it.</summary>
    public string WhyEscaped(string table)
    {
        int start = -1;
        for (int o = 2; o < _objects.Count; o++) if (_objects[o].Table == table) { start = o; break; }
        if (start < 0 || !_escaped.Contains(start)) return table + ": does not reach the unknown";
        bool Holds(ValueSet? held, int o) => held is not null && (held.Contains(o << 1) || held.Contains(o << 1 | 1));
        Dictionary<int, int> from = new() { [start] = -1 };
        Queue<int> next = new();
        next.Enqueue(start);
        while (next.TryDequeue(out int at))
        {
            foreach (var (node, why) in _sinkFrom)
                if (Holds(_pts[node], at))
                {
                    List<string> chain = new();
                    for (int o = at; o != start && o >= 0; o = from[o]) chain.Add("held by " + (_objects[o].Table ?? "an untyped object"));
                    chain.Reverse();
                    chain.Add(why);
                    return table + ": " + string.Join(" <- ", chain);
                }
            foreach (int p in _escaped)
                if (!from.ContainsKey(p) && _cellsOf[p] is { } cells && cells.Any(cell => Holds(_pts[cell], at))) { from[p] = at; next.Enqueue(p); }
        }
        return table + ": no labelled cause";
    }

    private void Escape(int value)
    {
        int o = ObjectOf(value);
        if (o <= 1 || !_escaped.Add(o)) return;
        _source = -1; _sourceWhy = "the object reaches the unknown";
        Add(Cell(o, AnyOffset), Top);
        AllCells(o, _sink);
    }

    private void Nobody(int i, RegionCall call)
    {
        int f = _instF[i];
        foreach (int a in call.Arguments) if (Has(f, a)) ToSink(Node(i, a), _functions[f].Name + " hands it to a call nobody names (" + (call.Callee ?? "an address") + ")");
        _source = -1; _sourceWhy = _functions[f].Name + " calls nobody (" + (call.Callee ?? "an address") + ")";
        if (Has(f, call.Dest)) Add(Node(i, call.Dest), Top);
    }

    // A direct call from instance i bound to instance gi of its target, every argument.
    private void Bind(int i, RegionCall call, int gi)
    {
        int f = _instF[i];
        RegionFunction callee = _functions[_instF[gi]];
        for (int k = 0; k < call.Arguments.Length && k < callee.Parameters; k++)
            if (Has(f, call.Arguments[k])) Edge(Node(i, call.Arguments[k]), Node(gi, k));
        if (Has(f, call.Dest) && callee.Parameters < callee.Nodes) Edge(Node(gi, callee.Parameters), Node(i, call.Dest));
    }

    // For a report (Explain): what first gave each call every target.
    private readonly Dictionary<int, string> _fullBy = new();

    // A call's target instance bound but for its receiver: the other
    // arguments and the result. `this` is handed each value the receiver
    // holds that runs the target (Received).
    private void BindRest(int i, RegionCall call, int gi)
    {
        int f = _instF[i];
        RegionFunction callee = _functions[_instF[gi]];
        for (int k = 1; k < call.Arguments.Length && k < callee.Parameters; k++)
            if (Has(f, call.Arguments[k])) Edge(Node(i, call.Arguments[k]), Node(gi, k));
        if (Has(f, call.Dest) && callee.Parameters < callee.Nodes) Edge(Node(gi, callee.Parameters), Node(i, call.Dest));
    }

    private void BindAll(int c, string? why = null)
    {
        if (_full[c]) return;
        _full[c] = true;
        if (Explain is not null && why is not null) _fullBy[c] = why;
        var (i, k) = _calls[c];
        int f = _instF[i];
        int[] targets = _routed[c] >= 0 ? new[] { _routed[c] } : _targets[f][k]!;
        foreach (int g in targets)
        {
            _bound[c].Add(g);
            if (_boundInst[c].Add(g)) BindRest(i, _functions[f].Calls[k], g);
        }
    }

    // A VALUE THE RECEIVER MAY BE goes to the `this` of the targets it runs,
    // and to no other: handed the whole receiver, a method that answers its
    // `this` (String.ToString) gave back every object any call of its slot
    // was made on -- an iterator a concatenation called ToString on was a
    // string, and through a root that answers one, the unknown object's,
    // its every delegate call run on every delegate. The unknown object, a
    // constant, an untyped object, or one whose descriptor may run any,
    // goes to every target. And to the INSTANCE of each target it runs: a
    // shared copy's method, its copy for the object's descriptor
    // (InstanceFor). An address into an object is a call on it.
    private void Received(int c, int value)
    {
        var (i, k) = _calls[c];
        int f = _instF[i];
        RegionCall call = _functions[f].Calls[k];
        int o = ObjectOf(value);
        int[]? runs = null;
        if (_routed[c] >= 0) runs = new[] { _routed[c] };
        else
        {
            string? every = null;
            if (o <= 1 || _objects[o].Table is not { } table) every = o == 0 ? "the unknown object" : o == 1 ? "a constant" : "an untyped object";
            else
            {
                if (!_dispatched.TryGetValue((f, k, o), out runs)) _dispatched[(f, k, o)] = runs = _dispatch(f, k, table, _objects[o].At);
                if (runs is null) every = "an object of " + table + " that runs any";
            }
            if (every is not null)
            {
                BindAll(c, every);
                runs = _targets[f][k]!;
            }
        }
        foreach (int g in runs!)
        {
            _bound[c].Add(g);
            int gi = InstanceFor(g, o);
            if (_boundInst[c].Add(gi)) BindRest(i, call, gi);
            if (_functions[g].Parameters > 0 && Has(g, 0)) Add(Node(gi, 0), value);
        }
    }

    private void Settle()
    {
        while (_work.TryDequeue(out int n))
        {
            _source = n; _sourceWhy = null;
            int[] delta = _delta[n]!.ToArray();
            _delta[n] = null;
            foreach (int v in delta)
            {
                if (_uses[n] is { } uses)
                {
                    if (uses.Succ is { } succ) for (int s = 0; s < succ.Count; s++) Add(succ[s], v);
                    if (uses.Moved is { } moved) for (int s = 0; s < moved.Count; s++) Add(moved[s], Moved(v));
                    if (uses.Loads is { } loads) for (int l = 0; l < loads.Count; l++) Loaded(loads[l].To, v, loads[l].Offset);
                    if (uses.Stores is { } stores) for (int s = 0; s < stores.Count; s++) Stored(v, stores[s].Value, stores[s].Offset);
                    if (uses.Receives is { } receives) for (int r = 0; r < receives.Count; r++) Received(receives[r], v);
                }
                if (n == _sink) Escape(v);
            }
        }
    }

    private sealed class OverBudget : Exception { }

    /// <summary>
    /// Each virtual call's targets, narrowed to what its receiver's types
    /// run, written into the targets the pass was given; false, and nothing
    /// written, when it gave up.
    /// </summary>
    public bool Prune()
    {
        _budget = Budget >= 0 ? Budget : Math.Clamp(30L * _pts.Count, 2_000_000, 20_000_000);
        try { Solve(); }
        catch (OverBudget) { GaveUp = true; return false; }
        Objects = _objects.Count;
        // Each virtual call of each function, over every instance of it.
        Dictionary<(int, int), (bool Full, HashSet<int> Bound, int First)> merged = new();
        for (int c = 0; c < _calls.Count; c++)
        {
            if (_routed[c] >= 0) continue;
            var (i, k) = _calls[c];
            int f = _instF[i];
            if (!merged.TryGetValue((f, k), out var m)) m = (false, new HashSet<int>(), c);
            m.Bound.UnionWith(_bound[c]);
            merged[(f, k)] = (m.Full || _full[c], m.Bound, m.First);
        }
        foreach (((int f, int k), (bool full, HashSet<int> bound, int first)) in merged)
        {
            int[] those = _targets[f][k]!;
            Calls++; Before += those.Length;
            if (full)
            {
                After += those.Length; Unknown++;
                if (Explain is { } explain && explain(f))
                    Explained.Add($"{_functions[f].Name} call {k} ({_keys[f][k]}): every target, for {_fullBy.GetValueOrDefault(first, "a receiver nothing reaches")}{(_rooted[f] ? (TypedThis?[f] == true ? "; a typed root" : "; a root") : "")}; this holds {string.Join(", ", (_pts[Node(f, 0)] ?? new ValueSet()).Take(6).Select(v => ObjectOf(v) <= 1 ? (ObjectOf(v) == 0 ? "unknown" : "constant") : _objects[ObjectOf(v)].Table ?? "untyped"))}");
                continue;
            }
            int[] kept = those.Where(bound.Contains).ToArray();
            After += kept.Length;
            if (kept.Length < those.Length) Narrowed++;
            _targets[f][k] = kept;
        }
        return true;
    }

    private readonly List<int> _typedRoots = new();

    /// <summary>
    /// Instance i's constraints and calls, stated: function f's own for its
    /// own instance, the same over the instance's nodes for a copy run on a
    /// `this` of one descriptor -- its sites making objects of its own, and
    /// roots only in a function's own instance.
    /// </summary>
    private void Instantiate(int i)
    {
        int f = _instF[i];
        bool own = i == f;
        RegionFunction function = _functions[f];
        foreach (RegionConstraint c in function.Constraints)
        {
            if (!Has(f, c.A)) continue;
            int a = Node(i, c.A);
            switch (c.Kind)
            {
                case RegionConstraintKind.Site:
                    RegionSite site = c.B >= 0 && c.B < function.Sites.Length ? function.Sites[c.B] : default;
                    Add(a, (site.Table is { } table
                        ? table.StartsWith("q_array", StringComparison.Ordinal) ? ArrayAt(table, site.At, i, c.B) : TypedIn(table, site.At, i)
                        : site.Words == RegionWords.Leaf ? Untyped(i) : UntypedMade(i)) << 1);
                    break;
                case RegionConstraintKind.Slot: Add(a, Untyped(i) << 1); break;
                // A closed static (RegionConstants.ClosedStatic, one past
                // it in B): an object of its own, holding what is stored.
                case RegionConstraintKind.Unknown when c.B > 0: Add(a, Static(c.B - 1) << 1); break;
                case RegionConstraintKind.Unknown when c.C > 0 && DataObjects is { } laid && c.C <= laid.Count: Add(a, DataObject((int)c.C - 1) << 1); break;
                case RegionConstraintKind.Unknown: _source = -1; _sourceWhy = function.Name + " says unknown"; Add(a, Top); break;
                // A symbol left after RegionConstants is a constant; one
                // it never judged may be anything.
                case RegionConstraintKind.Symbol: Add(a, function.ConstantsKnown ? Constant : Top); break;
                // Moved by anything -- a shift, an index, an offset nobody
                // knows -- an address into what it held.
                case RegionConstraintKind.Copy:
                    if (!Has(f, c.B)) break;
                    if (c.C == 0) Edge(Node(i, c.B), a); else MovedEdge(Node(i, c.B), a);
                    break;
                case RegionConstraintKind.Load: if (Has(f, c.B)) Load(a, Node(i, c.B), Plain(c.C)); break;
                case RegionConstraintKind.Store:
                    if (!Has(f, c.B)) break;
                    if (Explain is not null) _storedBy.TryAdd(Node(i, c.B), function.Name + " node " + c.B + " at " + c.C + " through node " + c.A);
                    Store(a, Node(i, c.B), Plain(c.C));
                    break;
                // Every word of one, at any offset of the other.
                case RegionConstraintKind.MemCopy:
                    if (!Has(f, c.B)) break;
                    int through = NewNode();
                    Load(through, Node(i, c.B), AnyOffset);
                    Store(a, through, AnyOffset);
                    break;
                case RegionConstraintKind.Leak: ToSink(a, function.Name + " leaks it"); break;
            }
        }
        if (own && _rooted[f])
        {
            bool typed = TypedThis?[f] == true && Receives is not null && function.Instance && function.Parameters > 0;
            _source = -1; _sourceWhy = function.Name + " is a root";
            for (int p = typed ? 1 : 0; p < function.Parameters && p < function.Nodes; p++) Add(Node(i, p), Top);
            if (typed) _typedRoots.Add(f);
            if (function.Parameters < function.Nodes) ToSink(Node(i, function.Parameters), function.Name + ", a root, returns it");
        }
        for (int k = 0; k < function.Calls.Count; k++)
        {
            RegionCall call = function.Calls[k];
            if (_targets[f][k] is not { } those) { Nobody(i, call); continue; }
            bool hasReceiver = call.Arguments.Length > 0 && Has(f, call.Arguments[0]);
            if (_keys[f][k] is null)
            {
                foreach (int g in those)
                {
                    // A DIRECT CALL INTO A SHARED COPY'S METHOD, `this.Grow()`
                    // inside List<__canon>.Add: routed by what `this` is, to
                    // the copy for its descriptor, as a virtual call is.
                    if (Shared(g) && hasReceiver) Watch(i, k, g, call);
                    else Bind(i, call, g);
                }
                continue;
            }
            Watch(i, k, -1, call);
        }
    }

    // A call whose receiver's values pick what runs (Received): a virtual
    // one (routed -1), or a direct one into a shared copy's method (routed g).
    private void Watch(int i, int k, int routed, RegionCall call)
    {
        int c = _calls.Count;
        _calls.Add((i, k)); _routed.Add(routed); _full.Add(false); _bound.Add(new()); _boundInst.Add(new());
        if (call.Arguments.Length == 0 || !Has(_instF[i], call.Arguments[0])) { BindAll(c); return; }
        int receiver = Node(i, call.Arguments[0]);
        (UsesOf(receiver).Receives ??= new()).Add(c);
        if (_pts[receiver] is { } held) { _source = receiver; foreach (int o in held.ToArray()) Received(c, o); }
    }

    private void Solve()
    {
        for (int f = 0; f < _functions.Count; f++) Instantiate(f);
        // Every object exists now (a site's or a slot's constraint makes it):
        // each root called blind is handed those it may run on.
        foreach (int f in _typedRoots)
        {
            int self = Node(f, 0);
            for (int o = 2; o < _objects.Count; o++)
                if (_objects[o].Table is { } table ? Receives!(f, table, _objects[o].At) : _untypedMade.Contains(o)) Add(self, o << 1);
            // An object of such a type laid down in data -- a constant one
            // among them -- or made by code with no IR: the unknown object.
            _source = -1; _sourceWhy = _functions[f].Name + " has a this made outside";
            if (ThisMadeOutside?.Invoke(f) != false) Add(self, Top);
        }
        Settle();
        // A receiver nothing reached runs every target: never fewer for want
        // of a type the solve did not see it get. Each so bound may reach
        // more, and the solve goes on.
        for (bool more = true; more;)
        {
            more = false;
            for (int c = 0; c < _calls.Count; c++)
            {
                if (_full[c]) continue;
                var (i, k) = _calls[c];
                int r = _functions[_instF[i]].Calls[k].Arguments[0];
                if (_pts[Node(i, r)] is { Count: > 0 }) continue;
                BindAll(c);
                more = true;
            }
            Settle();
        }
    }
}

/// <summary>
/// WHAT A NODE HOLDS, as bits: value v at bit v, the words grown as far as
/// the largest value held. A value is an object shifted left one with its
/// address-into bit (RegionTypes.ObjectOf), so a whole program's few tens of
/// thousands of objects are a few kilobytes at most, where a hash set of
/// ints took some ninety bytes a value: the compiler's own link held fifty
/// million of them in four and a half gigabytes, and was not a tenth done.
/// </summary>
internal sealed class ValueSet : IEnumerable<int>
{
    private ulong[] _words = Array.Empty<ulong>();
    public int Count { get; private set; }

    public bool Add(int value)
    {
        int word = value >> 6;
        if (word >= _words.Length) Array.Resize(ref _words, Math.Max(word + 1, Math.Min(_words.Length * 2, word + 64)));
        ulong bit = 1UL << (value & 63);
        if ((_words[word] & bit) != 0) return false;
        _words[word] |= bit;
        Count++;
        return true;
    }

    public bool Contains(int value)
    {
        int word = value >> 6;
        return word < _words.Length && (_words[word] & 1UL << (value & 63)) != 0;
    }

    public int[] ToArray()
    {
        int[] values = new int[Count];
        int at = 0;
        for (int w = 0; w < _words.Length; w++)
            for (ulong bits = _words[w]; bits != 0; bits &= bits - 1)
                values[at++] = (w << 6) | System.Numerics.BitOperations.TrailingZeroCount(bits);
        return values;
    }

    public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)ToArray()).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
