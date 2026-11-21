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

    /// <summary>
    /// Per virtual slot, the arguments a call through it passes as 0, in
    /// place, ascending, the object being 0: no function the slot can reach
    /// reads them, and every one keeps its parameters where they were -- a
    /// family a call nothing names may reach (ArgumentSolver), whose layout
    /// such a call relies on. What the argument was made of is then dead at
    /// the call, and the callee never looks.
    /// </summary>
    public SortedDictionary<string, int[]> UnsetSlots { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per function, the way in the link gave it for its commonest call
    /// (Function.ConstantEntry): the parameters it sets, ascending, each
    /// with its number. Indexed as the function's parameters are AFTER
    /// what Dropped took from it. A call by name that passes exactly those
    /// numbers there goes in by it and loads none of them.
    /// </summary>
    public SortedDictionary<string, ArgumentDrop[]> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per function, the versions of it the link asked for: each the
    /// function with some parameters given their numbers and gone, under a
    /// name of its own, made once by the unit that defines the function
    /// (ArgumentSummary.MakeVersions) and called by every call by name that
    /// passes exactly those numbers there. Places as Entries has them.
    /// </summary>
    public SortedDictionary<string, ArgumentVersion[]> Versions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per function, the parameters that were a struct's address and are
    /// its words now (ArgumentSolver: a struct every caller passes by value
    /// and the function only reads through): each by its place -- as
    /// Entries has places -- with the offsets read, ascending. The function
    /// takes a word for each offset where the address was, and a call of it
    /// by name loads them from the address it would have passed. Such a
    /// function has no way in and no versions.
    /// </summary>
    public SortedDictionary<string, ArgumentSplit[]> Splits { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Dropped.Count == 0 && DroppedSlots.Count == 0 && UnsetSlots.Count == 0 && Entries.Count == 0 && Versions.Count == 0 && Splits.Count == 0;

    /// <summary>The name of a version of `function`: its place among the function's versions.</summary>
    public static string VersionName(string function, int ordinal)
        => function + VersionMark + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What ends the name of a version of a function: then its place among the function's versions.</summary>
    public const string VersionMark = "$with$";

    /// <summary>What ends the name of a function's way in with arguments: then the parameters it sets, a bit each, in hex.</summary>
    public const string EntryMark = "$entry$";

    /// <summary>The name of the way in to `function` that sets the parameters of `given`.</summary>
    public static string EntryName(string function, IEnumerable<int> given)
    {
        int mask = 0;
        foreach (int parameter in given) mask |= 1 << parameter;
        return function + EntryMark + mask.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a name is one the facts made -- a version or a way in: called only by the units of its image, by that name, never by another image's.</summary>
    public static bool Made(string name)
        => name.Contains(EntryMark, StringComparison.Ordinal) || name.Contains(VersionMark, StringComparison.Ordinal);

    /// <summary>The arguments a call by this name finds set already, a bit each by its place, or 0: the name of no such way in.</summary>
    public static int EntryGiven(string callee)
    {
        int at = callee.LastIndexOf(EntryMark, StringComparison.Ordinal);
        if (at < 0) return 0;
        ReadOnlySpan<char> digits = callee.AsSpan(at + EntryMark.Length);
        return digits.Length is > 0 and <= 2 && int.TryParse(digits, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out int mask) ? mask : 0;
    }
}

/// <summary>A parameter removed, and what it was at every call: a value to put in its place, or Unknown where the function never read it.</summary>
public readonly record struct ArgumentDrop(int Parameter, ArgumentValue Value);

/// <summary>A struct's address made its words: the parameter's place, and the offsets of the words, ascending.</summary>
public readonly record struct ArgumentSplit(int Place, int[] Offsets);

/// <summary>A version of a function: its name, and the parameters it has no more with the number each is inside it, ascending.</summary>
public readonly record struct ArgumentVersion(string Name, ArgumentDrop[] Bound);

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
/// known: the image closed, and no call through a word that may have been
/// read out of a descriptor reaching it -- each such call judged by where
/// its target comes from (ArgumentHints: CellCalls, ParamCalls, Unnamed;
/// a slot's word read raw is a call through the slot, ArgumentSummary.
/// RawSlot). A family so reached keeps its parameters in place, one no
/// member reads passed as 0 through its slots (ArgumentFacts.UnsetSlots).
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
    /// the image is whole (no shared object, nothing exported unseen);
    /// `elsewhere` says of a name that its body is another image's, which
    /// the calls reach as it was compiled -- unchangeable, and nothing made
    /// beside it either, a way in or a version that image does not export;
    /// `outside` -- for an image not closed whose slots are its own but for
    /// what it shows another image (a kernel to its modules) -- says of a
    /// function that a slot call of that other image may reach it: its
    /// family keeps every parameter where it is, and every other family is
    /// judged as in a closed image (`virtuals` then this image's answers).
    /// </summary>
    public static ArgumentFacts? Solve(IReadOnlyList<ArgumentHints> units, IReadOnlyDictionary<string, string[]> virtuals,
        Func<string, bool> unchangeable, bool closed, Action<string>? report = null, Func<string, bool>? elsewhere = null,
        Func<string, bool>? outside = null)
    {
        if (elsewhere is not null)
        {
            Func<string, bool> given = unchangeable;
            unchangeable = name => given(name) || elsewhere(name);
        }
        // Each function once: the first unit's in link order. A generic
        // body compiled into several units is the same function in each,
        // with the same parameters; where two units disagree on how many,
        // nothing is known of it -- nor where one name is two units' own
        // functions, not copies of one (a name local to each).
        Dictionary<string, ArgumentFunction> functions = new(StringComparer.Ordinal);
        HashSet<string> disagreed = new(StringComparer.Ordinal), addressed = new(StringComparer.Ordinal), slotted = new(StringComparer.Ordinal);
        // Functions some call passes other than their parameters' number of
        // arguments: which of a call's operands is which is not known.
        HashSet<string> misfit = new(StringComparer.Ordinal);
        // How many arguments each unnamed call passes (ArgumentHints.Unnamed).
        HashSet<int> unnamed = new();
        foreach (ArgumentHints unit in units)
        {
            foreach ((string name, ArgumentFunction f) in unit.Functions)
                if (!functions.TryAdd(name, f) && (!functions[name].Kinds.AsSpan().SequenceEqual(f.Kinds) || !f.Copies || !functions[name].Copies)) disagreed.Add(name);
            addressed.UnionWith(unit.Addressed);
            slotted.UnionWith(unit.Slotted);
            unnamed.UnionWith(unit.Unnamed);
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
        // (Which functions some call does not fit: known before any cell is read.)
        foreach (ArgumentHints unit in units)
            foreach (ArgumentFunction f in unit.Functions.Values)
                foreach (ArgumentCall call in f.Calls)
                {
                    IEnumerable<string> reached = call.Callee.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)
                        ? virtuals.GetValueOrDefault(call.Callee) ?? Array.Empty<string>() : new[] { call.Callee };
                    foreach (string target in reached)
                        if (functions.TryGetValue(target, out ArgumentFunction? callee) && callee.Kinds.Length != call.Arguments.Length) misfit.Add(target);
                }

        // The calls, every copy of a function's (the units' copies of one
        // generic body make the same calls: met twice, they lower nothing
        // further), each resolved to the functions it reaches.
        List<(string Caller, ArgumentCall Call, string[] Targets)> calls = new();
        HashSet<string> called = new(StringComparer.Ordinal);
        HashSet<string> slotsCalled = new(StringComparer.Ordinal);
        // Calls through a slot the link has no answer for (some descriptor
        // holds there what is not a function of the units' -- foreign code
        // -- or the name was not among those the link resolved).
        int unresolved = 0;
        foreach (ArgumentHints unit in units)
            foreach ((string caller, ArgumentFunction f) in unit.Functions)
                foreach (ArgumentCall call in f.Calls)
                {
                    if (call.Callee.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal))
                    {
                        slotsCalled.Add(call.Callee);
                        if (virtuals.TryGetValue(call.Callee, out string[]? targets)) { calls.Add((caller, call, targets)); called.UnionWith(targets); }
                        // Of what it reaches nothing is known but that it is
                        // passed as many arguments: as an unnamed call is.
                        // (Every function in one slot has the slot's
                        // parameters, the call's count; one that has not is
                        // a misfit of the calls the link did answer, and a
                        // function only such a call reaches is called by
                        // nothing seen: Many either way.)
                        else { unresolved++; unnamed.Add(call.Arguments.Length); }
                    }
                    else if (functions.ContainsKey(call.Callee)) { calls.Add((caller, call, new[] { call.Callee })); called.Add(call.Callee); }
                }

        // UNNAMED CALLS THROUGH A STATIC CELL (ArgumentHints.CellCalls): of
        // what a function's address taken was put there, where every unit
        // wrote the cell only with numbers and named addresses and nothing
        // took its address (CellsWritten), and nothing outside the units may
        // write it -- else counted as any unnamed call.
        HashSet<string> written = new(StringComparer.Ordinal);
        foreach (ArgumentHints unit in units) written.UnionWith(unit.CellsWritten);
        Dictionary<string, List<string>> cellParameters = new(StringComparer.Ordinal);
        foreach (ArgumentHints unit in units)
            foreach ((string cell, SortedSet<string> parameters) in unit.CellParams)
                (cellParameters.TryGetValue(cell, out List<string>? those) ? those : cellParameters[cell] = new()).AddRange(parameters);
        // A PARAMETER EVERY CALL GIVES A NUMBER OR A NAMED ADDRESS, or hands
        // on one of its own of which the same holds: of a function every
        // call of which the units show -- none foreign, none through an
        // address or a slot, each passing its parameters -- judged without
        // a cycle (one met again says no).
        Dictionary<string, List<(string Caller, ArgumentCall Call)>> byName = new(StringComparer.Ordinal);
        foreach ((string caller, ArgumentCall call, string[] targets) in calls)
            if (!call.Callee.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal))
                (byName.TryGetValue(call.Callee, out var sites) ? sites : byName[call.Callee] = new()).Add((caller, call));
        Dictionary<string, bool> namedOnly = new(StringComparer.Ordinal);
        bool NamedOnly(string parameter, int depth)
        {
            if (namedOnly.TryGetValue(parameter, out bool known)) return known;
            int colon = parameter.IndexOf(':');
            if (colon <= 0 || !int.TryParse(parameter.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int k)) return false;
            string name = parameter[(colon + 1)..];
            if (depth == 0 || !functions.TryGetValue(name, out ArgumentFunction? f) || k >= f.Kinds.Length || f.Fixed || disagreed.Contains(name)
                || misfit.Contains(name) || addressed.Contains(name) || slotted.Contains(name) || slotsOf.ContainsKey(name) || unchangeable(name))
                return namedOnly[parameter] = false;
            // (Called by nothing at all: its parameter is never given anything.)
            if (!byName.TryGetValue(name, out var sites)) return namedOnly[parameter] = true;
            namedOnly[parameter] = false;
            bool all = true;
            foreach ((string caller, ArgumentCall call) in sites)
            {
                if (call.Arguments.Length != f.Kinds.Length) { all = false; break; }
                ArgumentValue given = call.Arguments[k];
                if (given.Kind is ArgumentValueKind.Constant or ArgumentValueKind.Address) continue;
                if (given.Kind == ArgumentValueKind.Parameter && NamedOnly(given.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + caller, depth - 1)) continue;
                all = false;
                break;
            }
            return namedOnly[parameter] = all;
        }
        bool CellNamed(string cell) => !written.Contains(cell) && !unchangeable(cell)
            && (!cellParameters.TryGetValue(cell, out List<string>? from) || from.All(parameter => NamedOnly(parameter, 8)));
        int throughCells = 0;
        foreach (ArgumentHints unit in units)
        {
            foreach ((string cell, SortedSet<int> counts) in unit.CellCalls)
            {
                if (!CellNamed(cell)) unnamed.UnionWith(counts);
                else throughCells++;
            }
            foreach ((string parameter, SortedSet<int> counts) in unit.ParamCalls)
            {
                if (!NamedOnly(parameter, 8)) unnamed.UnionWith(counts);
                else throughCells++;
            }
        }
        // What may change at all.
        bool Free(string name) => functions.TryGetValue(name, out ArgumentFunction? f) && !f.Fixed && !disagreed.Contains(name)
            && !misfit.Contains(name) && !addressed.Contains(name) && !unchangeable(name);
        // A slot's family is known whole only in a closed image, and only
        // where no call through an address nothing names may reach it: a
        // slot's word read raw (the runtime's own reading of a descriptor)
        // would be called so, with at least as many arguments as the
        // function has parameters (ArgumentHints.Unnamed) -- nor a call
        // through a slot the link could not answer, of what it passes, the
        // same -- none such passes as many as a member has, or more.
        // (Or not closed, but every slot call another image may make said by
        // `outside`: each function it may reach.)
        bool slotsKnown = closed || outside is not null;
        bool Outside(string name) => outside?.Invoke(name) == true;
        int reachedOutside = 0;
        int unnamedMost = unnamed.Count == 0 ? -1 : unnamed.Max();
        bool UnnamedReach(string name) => functions[name].Kinds.Length <= unnamedMost;
        int heldByUnnamed = 0, unsetThroughSlots = 0;
        // A FUNCTION SOMETHING UNSEEN MAY CALL HAS PARAMETERS NOBODY KNOWS:
        // one that may not change (foreign, kept for others, its address
        // taken), one in a slot whose callers are not all known, one no
        // call here names at all. Unknown from the start -- not unseen: a
        // delegate's own Invoke, called through its slot by whatever holds
        // the delegate, hands its parameters on to the method it stands
        // for, and left unseen it said nothing of them, so the method's
        // direct calls alone decided what it "always" got.
        foreach ((string name, ArgumentFunction f) in functions)
        {
            bool bySlot = slotted.Contains(name) || slotsOf.ContainsKey(name);
            if (Free(name) && called.Contains(name) && (!bySlot || slotsKnown && !UnnamedReach(name) && !Outside(name))) continue;
            Cell[] of = cells[name];
            for (int k = 0; k < of.Length; k++) of[k].State = Many;
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

        // Each function's calls by name, as the units said them.
        Dictionary<string, List<ArgumentCall>> direct = new(StringComparer.Ordinal);
        foreach ((string _, ArgumentCall call, string[] targets) in calls)
        {
            if (call.Callee.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)) continue;
            if (!direct.TryGetValue(targets[0], out List<ArgumentCall>? of)) direct[targets[0]] = of = new();
            of.Add(call);
        }
        bool directBefore(string name, out List<ArgumentCall>? sites) => direct.TryGetValue(name, out sites);


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
                // (One the function writes too: the value is its register's
                // first, given where the function begins, and the writes
                // after are the register's as they were the parameter's.)
                else if (Constant(of[k])) (drops ??= new()).Add(new(k, of[k].Value));
            }
            return drops?.ToArray();
        }
        bool Droppable(string name, int k)
        {
            ArgumentFunction f = functions[name];
            return k < f.Kinds.Length && (f.Reads[k] == 0 || Constant(cells[name][k]));
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
                // Every member known, one unit's copy as every other's, of
                // one count of parameters.
                if (members.Any(member => !functions.ContainsKey(member) || disagreed.Contains(member))) continue;
                // ONE ANOTHER IMAGE MAY REACH THROUGH A SLOT: its calls pass
                // what the slot always took, and its overrides may read any
                // of it -- nothing changes, not even an unread argument.
                if (members.Any(Outside)) { reachedOutside++; continue; }
                int parameters = functions[members[0]].Kinds.Length;
                if (members.Any(member => functions[member].Kinds.Length != parameters)) continue;
                IEnumerable<string> familySlots = members.SelectMany(member => slotsOf[member]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                // Every member free to change, and the family reached by no
                // call nothing names nor any the link has no answer for
                // (UnnamedReach, a slot's word read raw): a member's callers
                // are the resolved slots' calls alone. ELSE THE PARAMETERS
                // STAY WHERE THEY ARE, and one no member reads is passed as 0
                // through the slots (UnsetSlots): whatever else calls a member
                // finds the layout it had.
                bool held = UnnamedReach(members[0]);
                if (held) heldByUnnamed++;
                if (held || !members.All(Free))
                {
                    // (Only where every member's reads are its parameters'
                    // own -- one register twice among them is Fixed -- and
                    // every call's operands are its parameters, in place.)
                    if (members.Any(member => functions[member].Fixed || misfit.Contains(member))) continue;
                    List<int> unread = new();
                    for (int k = 1; k < parameters; k++) if (members.All(member => functions[member].Reads[k] == 0)) unread.Add(k);
                    if (unread.Count == 0) continue;
                    int[] unset = unread.ToArray();
                    foreach (string slot in familySlots) facts.UnsetSlots[slot] = unset;
                    unsetThroughSlots += unset.Length;
                    continue;
                }
                List<int> shared = new();
                // (The object, 0, stays: a call through a slot reads its descriptor.)
                for (int k = 1; k < parameters; k++) if (members.All(member => Droppable(member, k))) shared.Add(k);
                if (shared.Count == 0) continue;
                foreach (string member in members) if (DropsOf(member, shared.Contains) is { } drops) Take(member, drops, true);
                int[] gone = shared.ToArray();
                foreach (string slot in familySlots) facts.DroppedSlots[slot] = gone;
            }
        }

        void Take(string name, ArgumentDrop[] drops, bool bySlot)
        {
            facts.Dropped[name] = drops;
            foreach (ArgumentDrop drop in drops) { if (drop.Value.Known) constants++; else dead++; }
            if (bySlot) viaSlots += drops.Length;
        }

        // A STRUCT PASSED BY VALUE COMES AS ITS WORDS (ArgumentFacts.Splits;
        // the scalar replacement of aggregates across calls, as GCC's
        // IPA-SRA): where a function does nothing with a struct's address
        // but read words through it (ArgumentFunction.Fields) and every
        // call of it passes that struct by value, the words are passed --
        // the first three in registers like any -- and the caller's struct
        // need be in memory no longer for the call's sake. C# gives the
        // function the struct as it was at the call; loaded at the call is
        // exactly that. For a function nothing unseen calls, in no slot,
        // called by name with its own number of arguments.
        int splitWords = 0;
        foreach (string name in functions.Keys)
        {
            if (slotted.Contains(name) || slotsOf.ContainsKey(name) || !Free(name) || !called.Contains(name) || !directBefore(name, out List<ArgumentCall>? sites)) continue;
            ArgumentFunction f = functions[name];
            HashSet<int> gone = new(facts.Dropped.GetValueOrDefault(name)?.Select(drop => drop.Parameter) ?? Enumerable.Empty<int>());
            List<ArgumentSplit>? splits = null;
            int left = 0;
            for (int k = 0; k < f.Kinds.Length; k++)
            {
                if (gone.Contains(k)) continue;
                int place = left++;
                if (f.Fields[k] is not { } offsets || k >= 64) continue;
                if (!sites.All(call => (call.ByValue >> k & 1) != 0)) continue;
                (splits ??= new()).Add(new(place, offsets));
                splitWords += offsets.Length;
            }
            if (splits is not null) facts.Splits[name] = splits.ToArray();
        }

        // A WAY IN FOR THE COMMONEST CALL (ArgumentFacts.Entries): of the
        // parameters left that come in registers -- the first three words,
        // as the backend has them -- the numbers the most calls by name
        // agree on, set once in front of the function where each of those
        // calls set them itself. Free where it runs on into the function
        // (no jump, no instruction more than the calls had), and a load
        // fewer at every call that uses it: five bytes a number, two a
        // zero. One a function, where it saves more bytes than it is. Not
        // beside a function other units define too (each would make one).
        // The function itself is as it was, so anything may still call it:
        // its address taken, a name kept for others, a slot.
        int entrySites = 0;
        static int Bytes(long value) => value == 0 ? 2 : 5;
        foreach (string name in functions.Keys)
        {
            ArgumentFunction f = functions[name];
            if (f.Fixed || f.Copies || disagreed.Contains(name) || misfit.Contains(name) || facts.Splits.ContainsKey(name) || elsewhere?.Invoke(name) == true
                || !direct.TryGetValue(name, out List<ArgumentCall>? sites)) continue;
            // Its parameters as they are once Dropped has taken its own, and
            // which of those come in registers.
            HashSet<int> gone = new(facts.Dropped.GetValueOrDefault(name)?.Select(drop => drop.Parameter) ?? Enumerable.Empty<int>());
            int[] place = new int[f.Kinds.Length];
            int left = 0, words = 0;
            bool[] register = new bool[f.Kinds.Length];
            for (int k = 0; k < f.Kinds.Length; k++)
            {
                if (gone.Contains(k)) { place[k] = -1; continue; }
                place[k] = left++;
                // (Its place one of the eight a way in's name can say, two hex
                // digits of a bit each: ArgumentFacts.EntryGiven.)
                register[k] = f.Kinds[k] == ArgumentKind.Word && words++ < 3 && place[k] < 8;
            }
            // Each call's numbers in those places, as (place, number) pairs.
            List<(List<(int Place, long Value)> Set, int Count)> said = new();
            foreach (ArgumentCall call in sites)
            {
                List<(int, long)> set = new();
                for (int k = 0; k < f.Kinds.Length; k++)
                    if (register[k] && call.Arguments[k].Kind == ArgumentValueKind.Constant) set.Add((place[k], call.Arguments[k].Number));
                if (set.Count > 0) said.Add((set, call.Count));
            }
            if (said.Count == 0) continue;
            // The candidates: every call's whole set, and every pair alone.
            List<List<(int Place, long Value)>> candidates = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            void Offer(List<(int Place, long Value)> set)
            {
                if (seen.Add(string.Join(";", set.Select(pair => pair.Place + "=" + pair.Value)))) candidates.Add(set);
            }
            foreach ((List<(int Place, long Value)> set, int _) in said)
            {
                Offer(set);
                foreach ((int Place, long Value) pair in set) Offer(new() { pair });
            }
            List<(int Place, long Value)>? best = null;
            long bestScore = 0;
            int bestSites = 0;
            foreach (List<(int Place, long Value)> candidate in candidates)
            {
                int matching = 0;
                foreach ((List<(int Place, long Value)> set, int count) in said)
                    if (candidate.All(set.Contains)) matching += count;
                long cost = candidate.Sum(pair => Bytes(pair.Value));
                long score = matching * cost - cost;
                // More bytes saved; then fewer numbers set; then the lower places, the lower numbers.
                if (matching >= 2 && (score > bestScore || score == bestScore && best is not null && Before(candidate, best)))
                { best = candidate; bestScore = score; bestSites = matching; }
            }
            if (best is null || bestScore <= 0) continue;
            facts.Entries[name] = best.Select(pair => new ArgumentDrop(pair.Place, ArgumentValue.Of(pair.Value))).ToArray();
            entrySites += bestSites;
        }
        // A VERSION FOR A NUMBER THAT DECIDES A BRANCH (ArgumentFacts.Versions;
        // Cooper, Hall and Kennedy's cloning, as GCC's IPA-CP and LLVM's
        // function specialiser do it): where calls by name pass a parameter
        // the function branches on the same number, a copy of the function
        // with that number inside it -- the branch decided, a way of it gone
        // -- for those calls, which pass it no more. With no profile to say
        // what is hot, and a processor whose cache is eight kilobytes among
        // those this is for, a version is made only where it costs no bytes
        // -- what its calls stop loading is at least what it is, counted as
        // the function without what read the parameter -- or where one of
        // those calls stands in a loop and the function is short. At most
        // two a function, and all of them together no more than one part in
        // two hundred of what the units hold.
        const int ShortBody = 48, BytesAnInstruction = 3;
        long allowed = Math.Max(64, functions.Values.Sum(f => (long)f.Size) / 200), grown = 0;
        int versionSites = 0;
        foreach (string name in functions.Keys)
        {
            ArgumentFunction f = functions[name];
            if (f.Fixed || f.Copies || f.Recursive || disagreed.Contains(name) || misfit.Contains(name) || facts.Splits.ContainsKey(name) || elsewhere?.Invoke(name) == true
                || !direct.TryGetValue(name, out List<ArgumentCall>? sites)) continue;
            HashSet<int> gone = new(facts.Dropped.GetValueOrDefault(name)?.Select(drop => drop.Parameter) ?? Enumerable.Empty<int>());
            List<(int Place, long Value, long Weight, int Sites, int Cost)> wanted = new();
            int left = 0;
            for (int k = 0; k < f.Kinds.Length; k++)
            {
                if (gone.Contains(k)) continue;
                int place = left++;
                if (f.Shapes[k] != ArgumentShape.Compared || f.Kinds[k] == ArgumentKind.Float) continue;
                foreach (IGrouping<long, ArgumentCall> same in sites.Where(call => call.Arguments[k].Kind == ArgumentValueKind.Constant)
                    .GroupBy(call => call.Arguments[k].Number).OrderBy(group => group.Key))
                {
                    int count = same.Sum(call => call.Count);
                    bool looped = same.Any(call => call.Depth > 0);
                    long weight = same.Sum(call => (long)call.Count * (1 + 3 * call.Depth));
                    int cost = Math.Max(1, f.Size - f.Folds[k]);
                    bool free = (long)count * Bytes(same.Key) >= (long)cost * BytesAnInstruction;
                    if (free || looped && f.Size <= ShortBody) wanted.Add((place, same.Key, weight, count, free ? 0 : cost));
                }
            }
            if (wanted.Count == 0) continue;
            List<ArgumentVersion> made = new();
            foreach (var want in wanted.OrderByDescending(w => w.Weight).ThenBy(w => w.Place).ThenBy(w => w.Value).Take(2))
            {
                if (grown + want.Cost > allowed) continue;
                grown += want.Cost;
                versionSites += want.Sites;
                made.Add(new(ArgumentFacts.VersionName(name, made.Count), new[] { new ArgumentDrop(want.Place, ArgumentValue.Of(want.Value)) }));
            }
            if (made.Count > 0) facts.Versions[name] = made.ToArray();
        }

        static bool Before(List<(int Place, long Value)> a, List<(int Place, long Value)> b)
        {
            if (a.Count != b.Count) return a.Count < b.Count;
            for (int k = 0; k < a.Count; k++)
            {
                if (a[k].Place != b[k].Place) return a[k].Place < b[k].Place;
                if (a[k].Value != b[k].Value) return a[k].Value < b[k].Value;
            }
            return false;
        }

        string SlotsSaid()
        {
            if (!slotsKnown) return ", slots not known whole: the image is not closed";
            string open = outside is null ? "" : ", " + reachedOutside + " families another image may reach through a slot";
            string said = heldByUnnamed == 0 ? "" : ", " + heldByUnnamed + " families held by calls nothing names (" + unnamedMost + " arguments at most)";
            if (throughCells > 0) said += ", " + throughCells + " calls through cells or parameters holding only named addresses";
            if (unsetThroughSlots > 0) said += ", " + unsetThroughSlots + " parameters no family member reads passed as 0 through slots";
            said = open + said;
            return unresolved == 0 ? said : said + ", " + unresolved + " slot calls without an answer";
        }
        report?.Invoke("arguments: " + facts.Dropped.Count + " functions lose " + (constants + dead) + " parameters (" + constants
            + " one value at every call, " + dead + " never read; " + viaSlots + " through slots" + SlotsSaid()
            + "); " + facts.Entries.Count + " functions given a way in with their commonest numbers, for " + entrySites + " calls; "
            + facts.Versions.Values.Sum(versions => versions.Length) + " versions of " + facts.Versions.Count + " functions for " + versionSites + " calls; "
            + facts.Splits.Count + " functions take " + splitWords + " words where they took a struct's address");
        return facts.IsEmpty ? null : facts;
    }
}
