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
/// has one cell, every field and element of it at once. Over them, every
/// constraint of every function as RegionEscape reads it: Site, Slot,
/// Unknown, Symbol, Copy (any shift: the same objects), Load, Store,
/// MemCopy, Leak. A call binds its arguments to its targets' parameters and
/// their return to its result; a call nobody can name hands its arguments
/// to the unknown and gives back the unknown. A function called from where
/// nobody follows (rooted) has the unknown for every parameter, and its
/// return goes where nobody follows.
///
/// WHAT GOES WHERE NOBODY FOLLOWS MAY BE WRITTEN BY ANYTHING: an object that
/// reaches the unknown -- leaked, stored into the unknown object, handed to
/// a call nobody can name or returned by a root -- has the unknown object
/// in its cell, and everything its cell holds reaches the unknown too. Code
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
/// runs. Coarser than it everywhere -- one cell an object, one object a
/// type -- and so never fewer types than a receiver holds.
/// </summary>
public sealed class RegionTypes
{
    private const int Top = 0, Constant = 1;

    private readonly IReadOnlyList<RegionFunction> _functions;
    private readonly int[]?[][] _targets;
    private readonly string?[][] _keys;
    private readonly bool[] _rooted;
    private readonly Func<int, int, string, long, int[]?> _dispatch;

    // Nodes: each function's own, from its base; then cells and the sink.
    private readonly int[] _base;
    private readonly List<HashSet<int>?> _pts = new();
    private readonly List<List<int>?> _delta = new();
    // What else a node is, made only for the nodes that are anything more
    // than held: copied into others, an address loaded or stored through,
    // a receiver. (Each edge is made once: by its constraint, by an object
    // arriving at an address once, by a target bound once.)
    private sealed class Uses
    {
        public List<int>? Succ, Loads, Stores, Receives;
    }
    private readonly List<Uses?> _uses = new();
    private readonly Queue<int> _work = new();
    private readonly int _sink;

    // Objects: Top, Constant, then typed and untyped ones.
    private readonly List<(string? Table, long At)> _objects = new();
    private readonly List<int> _cell = new();
    private readonly Dictionary<(string, long), int> _typed = new();
    private readonly int[] _untyped;
    private readonly HashSet<int> _escaped = new();

    // Virtual calls: function, call, whether every target is bound, and those bound.
    private readonly List<(int F, int K)> _calls = new();
    private readonly List<bool> _full = new();
    private readonly List<HashSet<int>> _bound = new();
    private readonly Dictionary<(int, int, int), int[]?> _dispatched = new();

    private long _steps;
    /// <summary>Past this many objects carried, the pass gives up and prunes nothing.</summary>
    public long Budget { get; init; } = 2_000_000_000;

    /// <summary>For a report: the virtual calls, those narrowed, the targets before and after, those left every target for an unknown receiver, and the abstract objects.</summary>
    public int Calls, Narrowed, Before, After, Unknown, Objects;
    /// <summary>Whether it gave up for its budget.</summary>
    public bool GaveUp;

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
        _base = new int[functions.Count];
        int nodes = 0;
        for (int f = 0; f < functions.Count; f++) { _base[f] = nodes; nodes += functions[f].Nodes; }
        for (int n = 0; n < nodes; n++) NewNode();
        _sink = NewNode();
        _objects.Add((null, 0)); _cell.Add(-1);         // Top
        _objects.Add((null, 0)); _cell.Add(-1);         // Constant
        _untyped = new int[functions.Count];
        Array.Fill(_untyped, -1);
    }

    private int NewNode()
    {
        _pts.Add(null); _delta.Add(null); _uses.Add(null);
        return _pts.Count - 1;
    }

    private int Node(int f, int n) => _base[f] + n;
    private Uses UsesOf(int node) => _uses[node] ??= new();
    private bool Has(int f, int n) => n >= 0 && n < _functions[f].Nodes;

    private int NewObject(string? table, long at)
    {
        _objects.Add((table, at));
        _cell.Add(NewNode());
        return _objects.Count - 1;
    }

    private int Typed(string table, long at) => _typed.TryGetValue((table, at), out int o) ? o : _typed[(table, at)] = NewObject(table, at);
    private int Untyped(int f) => _untyped[f] >= 0 ? _untyped[f] : _untyped[f] = NewObject(null, 0);

    private void Add(int node, int o)
    {
        if (!(_pts[node] ??= new()).Add(o)) return;
        if (++_steps > Budget) throw new OverBudget();
        List<int> delta = _delta[node] ??= new();
        if (delta.Count == 0) _work.Enqueue(node);
        delta.Add(o);
    }

    private void Edge(int from, int to)
    {
        if (from == to) return;
        (UsesOf(from).Succ ??= new()).Add(to);
        if (_pts[from] is { } held) foreach (int o in held.ToArray()) Add(to, o);
    }

    private void Load(int to, int address)
    {
        (UsesOf(address).Loads ??= new()).Add(to);
        if (_pts[address] is { } held) foreach (int o in held.ToArray()) Loaded(to, o);
    }

    private void Store(int address, int value)
    {
        (UsesOf(address).Stores ??= new()).Add(value);
        if (_pts[address] is { } held) foreach (int o in held.ToArray()) Stored(o, value);
    }

    private void Loaded(int to, int o)
    {
        if (o == Top) Add(to, Top);
        else if (o == Constant) Add(to, Constant);
        else Edge(_cell[o], to);
    }

    private void Stored(int o, int value)
    {
        // A constant is read-only; what goes into the unknown object goes where nobody follows.
        if (o == Top) Edge(value, _sink);
        else if (o != Constant) Edge(value, _cell[o]);
    }

    private void Escape(int o)
    {
        if (o == Top || o == Constant || !_escaped.Add(o)) return;
        Add(_cell[o], Top);
        Edge(_cell[o], _sink);
    }

    private void Nobody(int f, RegionCall call)
    {
        foreach (int a in call.Arguments) if (Has(f, a)) Edge(Node(f, a), _sink);
        if (Has(f, call.Dest)) Add(Node(f, call.Dest), Top);
    }

    private void Bind(int f, RegionCall call, int g)
    {
        RegionFunction callee = _functions[g];
        for (int k = 0; k < call.Arguments.Length && k < callee.Parameters; k++)
            if (Has(f, call.Arguments[k])) Edge(Node(f, call.Arguments[k]), Node(g, k));
        if (Has(f, call.Dest) && callee.Parameters < callee.Nodes) Edge(Node(g, callee.Parameters), Node(f, call.Dest));
    }

    private void BindAll(int c)
    {
        if (_full[c]) return;
        _full[c] = true;
        var (f, k) = _calls[c];
        foreach (int g in _targets[f][k]!) if (_bound[c].Add(g)) Bind(f, _functions[f].Calls[k], g);
    }

    private void Received(int c, int o)
    {
        if (_full[c]) return;
        var (f, k) = _calls[c];
        if (o == Top || o == Constant || _objects[o].Table is not { } table) { BindAll(c); return; }
        if (!_dispatched.TryGetValue((f, k, o), out int[]? runs)) _dispatched[(f, k, o)] = runs = _dispatch(f, k, table, _objects[o].At);
        if (runs is null) { BindAll(c); return; }
        foreach (int g in runs) if (_bound[c].Add(g)) Bind(f, _functions[f].Calls[k], g);
    }

    private void Settle()
    {
        while (_work.TryDequeue(out int n))
        {
            List<int> delta = _delta[n]!;
            _delta[n] = null;
            foreach (int o in delta)
            {
                if (_uses[n] is { } uses)
                {
                    if (uses.Succ is { } succ) for (int s = 0; s < succ.Count; s++) Add(succ[s], o);
                    if (uses.Loads is { } loads) for (int l = 0; l < loads.Count; l++) Loaded(loads[l], o);
                    if (uses.Stores is { } stores) for (int s = 0; s < stores.Count; s++) Stored(o, stores[s]);
                    if (uses.Receives is { } receives) for (int r = 0; r < receives.Count; r++) Received(receives[r], o);
                }
                if (n == _sink) Escape(o);
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
        try { Solve(); }
        catch (OverBudget) { GaveUp = true; return false; }
        Objects = _objects.Count;
        for (int c = 0; c < _calls.Count; c++)
        {
            var (f, k) = _calls[c];
            int[] those = _targets[f][k]!;
            Calls++; Before += those.Length;
            if (_full[c]) { After += those.Length; Unknown++; continue; }
            int[] kept = those.Where(_bound[c].Contains).ToArray();
            After += kept.Length;
            if (kept.Length < those.Length) Narrowed++;
            _targets[f][k] = kept;
        }
        return true;
    }

    private void Solve()
    {
        for (int f = 0; f < _functions.Count; f++)
        {
            RegionFunction function = _functions[f];
            foreach (RegionConstraint c in function.Constraints)
            {
                if (!Has(f, c.A)) continue;
                int a = Node(f, c.A);
                switch (c.Kind)
                {
                    case RegionConstraintKind.Site:
                        RegionSite site = c.B >= 0 && c.B < function.Sites.Length ? function.Sites[c.B] : default;
                        Add(a, site.Table is { } table ? Typed(table, site.At) : Untyped(f));
                        break;
                    case RegionConstraintKind.Slot: Add(a, Untyped(f)); break;
                    case RegionConstraintKind.Unknown: Add(a, Top); break;
                    // A symbol left after RegionConstants is a constant; one
                    // it never judged may be anything.
                    case RegionConstraintKind.Symbol: Add(a, function.ConstantsKnown ? Constant : Top); break;
                    case RegionConstraintKind.Copy: if (Has(f, c.B)) Edge(Node(f, c.B), a); break;
                    case RegionConstraintKind.Load: if (Has(f, c.B)) Load(a, Node(f, c.B)); break;
                    case RegionConstraintKind.Store: if (Has(f, c.B)) Store(a, Node(f, c.B)); break;
                    case RegionConstraintKind.MemCopy:
                        if (!Has(f, c.B)) break;
                        int through = NewNode();
                        Load(through, Node(f, c.B));
                        Store(a, through);
                        break;
                    case RegionConstraintKind.Leak: Edge(a, _sink); break;
                }
            }
            if (_rooted[f])
            {
                for (int p = 0; p < function.Parameters && p < function.Nodes; p++) Add(Node(f, p), Top);
                if (function.Parameters < function.Nodes) Edge(Node(f, function.Parameters), _sink);
            }
            for (int k = 0; k < function.Calls.Count; k++)
            {
                RegionCall call = function.Calls[k];
                if (_targets[f][k] is not { } those) { Nobody(f, call); continue; }
                if (_keys[f][k] is null) { foreach (int g in those) Bind(f, call, g); continue; }
                int c = _calls.Count;
                _calls.Add((f, k)); _full.Add(false); _bound.Add(new());
                if (call.Arguments.Length == 0 || !Has(f, call.Arguments[0])) { BindAll(c); continue; }
                int receiver = Node(f, call.Arguments[0]);
                (UsesOf(receiver).Receives ??= new()).Add(c);
                if (_pts[receiver] is { } held) foreach (int o in held.ToArray()) Received(c, o);
            }
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
                var (f, k) = _calls[c];
                int r = _functions[f].Calls[k].Arguments[0];
                if (_pts[Node(f, r)] is { Count: > 0 }) continue;
                BindAll(c);
                more = true;
            }
            Settle();
        }
    }
}
