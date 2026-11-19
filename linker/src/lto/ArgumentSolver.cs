namespace Corsac.Lang.Lto;

/// <summary>
/// What every unit's backend is told of arguments when the link recompiles
/// it (ArgumentSolver): the parameters that are gone from each function,
/// each with the value that stands in its place inside it, and the same
/// for every call through a virtual slot whose whole family lost them.
/// The same answer goes to every unit: a function and its callers are in
/// different ones, and both change or neither.
/// </summary>
public sealed class ArgumentFacts
{
    /// <summary>
    /// Per function, the parameters removed, in ascending order: a call of
    /// it by name passes none of them, and the function begins by giving
    /// each its value -- the constant or the address every caller in the
    /// program passed -- or nothing, where it never read it.
    /// </summary>
    public SortedDictionary<string, ArgumentDrop[]> Dropped { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per virtual slot (its name as the units call it, VirtualTargets),
    /// the arguments a call through it no longer passes, ascending, the
    /// object being 0: every function the slot can reach lost the same.
    /// </summary>
    public SortedDictionary<string, int[]> DroppedSlots { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Dropped.Count == 0 && DroppedSlots.Count == 0;
}

/// <summary>A parameter removed, and what it was at every call: a value to put in its place, or Unknown where the function never read it.</summary>
public readonly record struct ArgumentDrop(int Parameter, ArgumentValue Value);

/// <summary>
/// THE WHOLE PROGRAM'S ARGUMENTS, judged over every unit's hints together
/// (ArgumentHints) as the lifetimes and the owned fields are: what no unit
/// can know alone is what every caller of a function passes it.
///
/// PROPAGATION first (Callahan, Cooper, Kennedy and Torczon's jump
/// functions, as GCC's IPA-CP has them): each parameter starts as unseen,
/// each call lowers it by what stands in that argument's place -- a
/// constant or an address is itself, the caller's own parameter handed on
/// is whatever that parameter turns out to be, anything else is unknown --
/// and the lowering runs until nothing moves. A parameter still one value
/// at the end is that value at every call the program makes.
///
/// THEN WHAT MAY CHANGE. A function keeps its parameters as they are
/// where anything the link cannot see calls it: foreign code (C's ways in,
/// a name an object without IR refers to), another image (what a kernel
/// offers its modules, an exported name), a call through a word (its
/// address taken in code, or in data that is no descriptor), the entry.
/// A function in a descriptor's slot changes only with every function its
/// slots can reach, and only where every call through those slots is
/// known: the image closed, and no unit with a call through an address it
/// could name by nothing.
/// </summary>
public static class ArgumentSolver
{
    private const int Unseen = 0, One = 1, Many = 2;

    private struct Cell
    {
        public int State;
        public ArgumentValue Value;

        /// <summary>Lowers the cell by a value met at a call; whether it moved.</summary>
        public bool Meet(ArgumentValue met)
        {
            if (State == Many) return false;
            if (!met.Known) { State = Many; return true; }
            if (State == Unseen) { State = One; Value = met; return true; }
            if (Value == met) return false;
            State = Many;
            return true;
        }
    }

    /// <summary>
    /// The facts, or null where nothing changes. `unchangeable` says of a
    /// name that something outside the units' IR may call it; `virtuals`
    /// has every function each virtual slot's name can reach (VirtualTargets.
    /// Resolve) and is empty where the types are open; `closed` is whether
    /// the image is whole (no shared object, nothing exported unseen).
    /// </summary>
    public static ArgumentFacts? Solve(IReadOnlyList<ArgumentHints> units, IReadOnlyDictionary<string, string[]> virtuals,
        Func<string, bool> unchangeable, bool closed, Action<string>? report = null)
    {
        // Each function once: the first unit's in link order. A generic
        // body compiled into several units is the same function in each,
        // with the same parameters; where two units disagree on how many,
        // nothing is known of it.
        Dictionary<string, ArgumentFunction> functions = new(StringComparer.Ordinal);
        HashSet<string> disagreed = new(StringComparer.Ordinal), addressed = new(StringComparer.Ordinal), slotted = new(StringComparer.Ordinal);
        // Functions some call passes other than their parameters' number of
        // arguments: which of a call's operands is which is not known.
        HashSet<string> misfit = new(StringComparer.Ordinal);
        bool unnamed = false;
        foreach (ArgumentHints unit in units)
        {
            foreach ((string name, ArgumentFunction f) in unit.Functions)
                if (!functions.TryAdd(name, f) && !functions[name].Kinds.AsSpan().SequenceEqual(f.Kinds)) disagreed.Add(name);
            addressed.UnionWith(unit.Addressed);
            slotted.UnionWith(unit.Slotted);
            unnamed |= unit.Unnamed;
        }
        if (functions.Count == 0) return null;

        // Every function the slots reach, and the slots reaching each.
        Dictionary<string, List<string>> slotsOf = new(StringComparer.Ordinal);
        foreach ((string slot, string[] targets) in virtuals)
            foreach (string target in targets)
            {
                if (!slotsOf.TryGetValue(target, out List<string>? of)) slotsOf[target] = of = new();
                of.Add(slot);
            }

        Dictionary<string, Cell[]> cells = new(StringComparer.Ordinal);
        foreach ((string name, ArgumentFunction f) in functions) cells[name] = new Cell[f.Kinds.Length];

        // The calls, every copy of a function's (the units' copies of one
        // generic body make the same calls: met twice, they lower nothing
        // further), each resolved to the functions it reaches.
        List<(string Caller, ArgumentCall Call, string[] Targets)> calls = new();
        HashSet<string> called = new(StringComparer.Ordinal);
        HashSet<string> slotsCalled = new(StringComparer.Ordinal);
        bool unresolved = false;
        foreach (ArgumentHints unit in units)
            foreach ((string caller, ArgumentFunction f) in unit.Functions)
                foreach (ArgumentCall call in f.Calls)
                {
                    if (call.Callee.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal))
                    {
                        slotsCalled.Add(call.Callee);
                        if (virtuals.TryGetValue(call.Callee, out string[]? targets)) { calls.Add((caller, call, targets)); called.UnionWith(targets); }
                        else unresolved = true;
                    }
                    else if (functions.ContainsKey(call.Callee)) { calls.Add((caller, call, new[] { call.Callee })); called.Add(call.Callee); }
                }

        bool Lower(string target, int parameter, ArgumentValue met)
        {
            Cell[] of = cells[target];
            return parameter < of.Length && of[parameter].Meet(met);
        }
        // Until nothing moves: a parameter handed on is read as it stands,
        // and read again when it has been lowered.
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach ((string caller, ArgumentCall call, string[] targets) in calls)
            {
                Cell[]? own = cells.GetValueOrDefault(caller);
                foreach (string target in targets)
                {
                    if (!functions.TryGetValue(target, out ArgumentFunction? callee)) continue;
                    // A call of other than its parameters says nothing of any of them.
                    bool fits = call.Arguments.Length == callee.Kinds.Length;
                    if (!fits) misfit.Add(target);
                    for (int k = 0; k < callee.Kinds.Length; k++)
                    {
                        ArgumentValue met = default;
                        if (fits)
                        {
                            met = call.Arguments[k];
                            if (met.Kind == ArgumentValueKind.Parameter)
                            {
                                Cell from = own is not null && met.Number < own.Length ? own[(int)met.Number] : new Cell { State = Many };
                                // Unseen yet: nothing to say this round.
                                if (from.State == Unseen) continue;
                                met = from.State == One ? from.Value : default;
                            }
                        }
                        moved |= Lower(target, k, met);
                    }
                }
            }
        }

        // What may change at all.
        bool Free(string name) => functions.TryGetValue(name, out ArgumentFunction? f) && !f.Fixed && !disagreed.Contains(name)
            && !misfit.Contains(name) && !addressed.Contains(name) && !unchangeable(name);
        // A slot's family is known whole only in a closed image where every
        // call through an address was named and every slot called resolved.
        bool slotsKnown = closed && !unnamed && !unresolved;

        ArgumentFacts facts = new();
        int constants = 0, dead = 0, viaSlots = 0;
        ArgumentDrop[]? DropsOf(string name, Func<int, bool> allowed)
        {
            ArgumentFunction f = functions[name];
            Cell[] of = cells[name];
            List<ArgumentDrop>? drops = null;
            for (int k = 0; k < of.Length; k++)
            {
                if (!allowed(k)) continue;
                if (f.Reads[k] == 0) (drops ??= new()).Add(new(k, default));
                else if (Constant(of[k]) && f.Shapes[k] != ArgumentShape.Written) (drops ??= new()).Add(new(k, of[k].Value));
            }
            return drops?.ToArray();
        }
        bool Droppable(string name, int k)
        {
            ArgumentFunction f = functions[name];
            return k < f.Kinds.Length && (f.Reads[k] == 0 || Constant(cells[name][k]) && f.Shapes[k] != ArgumentShape.Written);
        }
        // A NUMBER ALONE is put in a parameter's place: an address is known
        // as well, but of a symbol that may be its own unit's and no other's
        // to name, and a float's bits are not written here at all.
        bool Constant(Cell cell) => cell.State == One && cell.Value.Kind == ArgumentValueKind.Constant;

        // Functions no slot holds: each by itself.
        foreach (string name in functions.Keys)
        {
            if (slotted.Contains(name) || slotsOf.ContainsKey(name) || !Free(name)) continue;
            // NEVER CALLED BY ANYTHING SEEN: dead, or called by what no IR
            // shows (a helper the backend calls, a name the link itself
            // refers to). Its parameters are as its unseen callers have them.
            if (!called.Contains(name)) continue;
            if (DropsOf(name, _ => true) is { } drops) Take(name, drops, false);
        }

        // Functions in slots: by family -- every function joined to another
        // through a slot both stand in -- and only what every member can lose.
        if (slotsKnown)
        {
            Dictionary<string, string> parent = new(StringComparer.Ordinal);
            string Find(string x)
            {
                while (parent.TryGetValue(x, out string? up) && up != x) x = parent[x] = parent.GetValueOrDefault(up, up);
                return x;
            }
            foreach ((string slot, string[] targets) in virtuals)
            {
                if (targets.Length == 0) continue;
                foreach (string target in targets) parent.TryAdd(target, target);
                string root = Find(targets[0]);
                foreach (string target in targets) parent[Find(target)] = root;
            }
            Dictionary<string, List<string>> families = new(StringComparer.Ordinal);
            foreach (string target in parent.Keys.Order(StringComparer.Ordinal))
            {
                string root = Find(target);
                if (!families.TryGetValue(root, out List<string>? members)) families[root] = members = new();
                members.Add(target);
            }
            foreach (List<string> members in families.Values.OrderBy(m => m[0], StringComparer.Ordinal))
            {
                // Every member free to change, every slot it stands in one
                // of the resolved ones, and none held in a slot nothing
                // named (a descriptor's the units' calls never reach is
                // called by nobody the link sees: its callers are unknown).
                if (!members.All(Free)) continue;
                if (members.Any(member => !functions.ContainsKey(member))) continue;
                int parameters = functions[members[0]].Kinds.Length;
                if (members.Any(member => functions[member].Kinds.Length != parameters)) continue;
                List<int> shared = new();
                // (The object, 0, stays: a call through a slot reads its descriptor.)
                for (int k = 1; k < parameters; k++) if (members.All(member => Droppable(member, k))) shared.Add(k);
                if (shared.Count == 0) continue;
                foreach (string member in members) if (DropsOf(member, shared.Contains) is { } drops) Take(member, drops, true);
                int[] gone = shared.ToArray();
                foreach (string slot in members.SelectMany(member => slotsOf[member]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    facts.DroppedSlots[slot] = gone;
            }
        }

        void Take(string name, ArgumentDrop[] drops, bool bySlot)
        {
            facts.Dropped[name] = drops;
            foreach (ArgumentDrop drop in drops) { if (drop.Value.Known) constants++; else dead++; }
            if (bySlot) viaSlots += drops.Length;
        }

        report?.Invoke("arguments: " + facts.Dropped.Count + " functions lose " + (constants + dead) + " parameters (" + constants
            + " one value at every call, " + dead + " never read; " + viaSlots + " through slots" + (slotsKnown ? "" : ", slots not known whole") + ")");
        return facts.IsEmpty ? null : facts;
    }
}
