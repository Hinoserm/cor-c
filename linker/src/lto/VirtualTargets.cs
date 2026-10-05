using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT A VIRTUAL CALL CAN REACH, over every descriptor in the image. A unit
/// compiled on its own names each of its virtual calls by one symbol no unit
/// defines -- `__virtual:` its declaring type, `+`, its slot (Escape.
/// VirtualCallee) -- and states its lifetime conditions on that symbol. Here
/// the symbol becomes the methods every descriptor that is the declaring type
/// or derives from it holds at the slot, read from the objects themselves:
/// a descriptor is a data symbol, its method slots, display and interface
/// list are relocations inside it, and the offsets its method table starts
/// at are the addends code stamps objects with. Nothing is decoded; what the
/// link holds is the symbol tables and relocations it holds anyway.
///
/// The answer is conservative where it can be nothing else: a slot one
/// descriptor fills with something that is not a function, or no descriptor
/// fills at all, is not resolved, and a call to it stays an escape. A type no
/// descriptor is or derives from has no objects, and its calls reach nothing.
/// </summary>
public static class VirtualTargets
{
    public const string Prefix = "__virtual:";

    /// <summary>A descriptor's display and interface list, in words (Escape.DescriptorDisplay, DescriptorInterfaces).</summary>
    private const int Display = 3, Interfaces = 4;
    /// <summary>A descriptor's own words (Lowering's Desc*), and how many there are before its method table (Target.DescriptorBytes).</summary>
    private const int DescSelf = 5, DescFlags = 6, DescRefMap = 8, DescGcFlags = 9, DescriptorWords = 12;

    private static bool IsDescriptor(string name) => name.Length > 2 && name[1] == '_' && name[0] is 't' or 'q' or 'v' or 'b';

    /// <summary>
    /// WHETHER AN OBJECT STAMPED WITH A DESCRIPTOR MAY ANSWER AN INTERFACE ITS
    /// TABLES DO NOT NAME: a box (b_) or a string (q_string) answers its
    /// system interfaces -- IComparable, IComparable&lt;T&gt;, IEquatable&lt;T&gt;,
    /// IFormattable (BoxedFaces) -- in its interfaces' slots, and lists none of
    /// them, so that every unit's copy of a box is one table though only some
    /// units make IEquatable&lt;int&gt;. Any interface may then be one of them:
    /// a call declared on one counts every box and the string among the
    /// objects it can run on, and what each holds at the slot among its
    /// targets. More than the box answers is only ever more targets.
    /// ONLY THOSE FOUR: any interface at all made every one of a program's
    /// interface calls one a box might answer, and a box fills no slot of
    /// the runtime's own IOwnsElements -- "no descriptor fills the slot", the
    /// call left unresolved, and with it every method of every live type
    /// reached, every getter's address taken: a program of arrays and
    /// statics needed a collector again (762, 764, 767, 768).
    /// </summary>
    public static bool MayAnswer(string descriptor, string declaring)
        => (descriptor.StartsWith("b_", StringComparison.Ordinal) || descriptor == "q_string") && IsBoxedFace(declaring);

    /// <summary>
    /// Whether an interface's descriptor is one of the system interfaces a box
    /// or a string answers (BoxedFaces.Of): IComparable or IFormattable, or
    /// IComparable&lt;X&gt; or IEquatable&lt;X&gt; of any X -- by its name, in
    /// the global namespace or System's, as Lowering keys it ("i_IComparable",
    /// "i_IEquatable$0024int", "i_System$002eIFormattable"). An X no box
    /// answers for is only more targets.
    /// </summary>
    public static bool IsBoxedFace(string declaring)
    {
        if (!declaring.StartsWith("i_", StringComparison.Ordinal)) return false;
        string name = declaring[2..];
        const string system = "System$002e";
        if (name.StartsWith(system, StringComparison.Ordinal)) name = name[system.Length..];
        int cut = name.IndexOf('$');
        string bare = cut < 0 ? name : name[..cut];
        return cut < 0 ? bare is "IComparable" or "IFormattable" : bare is "IComparable" or "IEquatable";
    }

    /// <summary>
    /// Each of <paramref name="wanted"/> (virtual symbols) that can be
    /// resolved, to the functions it reaches.
    /// With <paramref name="made"/>, only what the descriptors it names hold
    /// (Made, MadeIn): a closed image's types nothing makes run nothing.
    /// </summary>
    public static Dictionary<string, string[]> Resolve(List<(string Name, ObjectFile Object)> inputs, IEnumerable<string> wanted, Made? made = null)
    {
        Dictionary<string, string[]> answers = new(StringComparer.Ordinal);
        Index? index = null;
        foreach (string name in wanted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            int plus = name.LastIndexOf('+');
            if (plus <= Prefix.Length || !long.TryParse(name.AsSpan(plus + 1), out long slot)) continue;
            index ??= IndexOf(inputs);
            string[]? reached = index.Targets(name[Prefix.Length..plus], slot, made);
            if (reached is string[] targets) answers[name] = targets;
            if (Switches.TraceVirtuals)
                Console.Error.WriteLine("virtual " + name + " slot " + slot + " -> " + (reached is null ? "unresolved" : "[" + string.Join(",", reached) + "]"));
        }
        return answers;
    }

    /// <summary>
    /// The function the descriptor <paramref name="descriptor"/> holds
    /// <paramref name="offset"/> bytes in -- what a virtual call made on an
    /// object stamped with it runs -- or null: no function there, or more
    /// than one descriptor by that name (two units' local types).
    /// </summary>
    public static string? MethodAt(List<(string Name, ObjectFile Object)> inputs, string descriptor, long offset)
        => IndexOf(inputs).MethodAt(descriptor, offset);

    /// <summary>
    /// Whether an object stamped with <paramref name="descriptor"/> is a
    /// <paramref name="type"/> (the type itself or one it derives from or
    /// implements); null when the descriptor is not one known here.
    /// </summary>
    public static bool? IsA(List<(string Name, ObjectFile Object)> inputs, string descriptor, string type)
        => IndexOf(inputs).IsA(descriptor, type);

    /// <summary>Whether a word of an object stamped with a descriptor is never read as a reference (Index.HoldsNoReference).</summary>
    public static bool HoldsNoReference(List<(string Name, ObjectFile Object)> inputs, string descriptor, long at, long? offset)
        => IndexOf(inputs).HoldsNoReference(descriptor, at, offset);

    /// <summary>Every type each of <paramref name="types"/> is, itself and all its ancestors, together.</summary>
    public static HashSet<string> Ancestry(List<(string Name, ObjectFile Object)> inputs, IEnumerable<string> types)
    {
        Index index = IndexOf(inputs);
        HashSet<string> together = new(StringComparer.Ordinal);
        foreach (string type in types)
        {
            together.Add(type);
            if (index.ByName.TryGetValue(type, out int d)) together.UnionWith(index.Ancestors(d));
        }
        return together;
    }

    /// <summary>
    /// THE TYPES A CLOSED IMAGE MAKES (rapid type analysis): every descriptor
    /// something in it names, but for the two names that make nothing -- a
    /// descriptor's own self word, and the entries of a descriptor's display
    /// and interface list, which name its ancestors to be asked about and
    /// never to be stamped with. An object has a type only by a store of its
    /// descriptor's method table, and that address comes from a relocation
    /// to the descriptor: a code relocation for an allocation, a box, a
    /// shared generic copy's table entry (TypeContext, a data relocation of
    /// its own), a data relocation for a static object or a string laid
    /// down whole. The allocator takes a byte count; a `with` stamps its
    /// static type by name; nothing in the runtime or the library writes a
    /// descriptor word it read out of another descriptor (it reads them for
    /// type tests only). Counting every other name -- a type test, typeof,
    /// an array's element descriptor -- is only more types.
    ///
    /// WHAT THE LINK MAY YET COMPILE AGAIN counts too: each unit's IR as the
    /// late passes found it (IrUnitCodec.Snapshot), whose every symbol a
    /// function names is in its record's references. A late pass that took
    /// an allocation out of the object (ScalarObjects) may leave it in when
    /// the link runs them again with other facts; the region hints are of
    /// that IR, and name its sites.
    ///
    /// For a closed image only: a shared object, or an image something
    /// outside it creates objects in, makes what no relocation here names.
    /// </summary>
    public sealed class Made
    {
        public readonly HashSet<string> Descriptors;
        /// <summary>How many descriptors the image defines by name (an interface's is none: no object is stamped with one).</summary>
        public int Defined;

        public Made() => Descriptors = new(StringComparer.Ordinal);
        private Made(HashSet<string> descriptors, int defined) { Descriptors = descriptors; Defined = defined; }

        /// <summary>The same types, counted afresh: one tally for each resolve that asks (the lifetimes', the regions').</summary>
        public Made Again() => new(Descriptors, Defined);
        /// <summary>The virtual calls asked about, those that lost a target, the targets they had and those they lost, and those only this resolved.</summary>
        public int Calls, Narrowed, Targets, Removed, ResolvedOnly;
        /// <summary>The descriptors nothing makes that some call reached a slot of, each once.</summary>
        public readonly SortedSet<string> Dropped = new(StringComparer.Ordinal);

        public bool Contains(string descriptor) => Descriptors.Contains(descriptor);

        /// <summary>One line, for the region report.</summary>
        public string Summary() => Descriptors.Count + " of " + Defined
            + " descriptors made; " + Removed + " of " + Targets + " targets dropped from " + Narrowed + " of " + Calls
            + " virtual calls, " + ResolvedOnly + " resolved only so; " + Dropped.Count + " unmade types reached";
    }

    /// <summary>
    /// The descriptors <paramref name="inputs"/> make (Made), named by any
    /// relocation in them or by any function of <paramref name="archives"/>.
    /// </summary>
    public static Made MadeIn(List<(string Name, ObjectFile Object)> inputs, IEnumerable<IrArchive> archives)
        => IndexOf(inputs).MadeTypes(archives);

    /// <summary>
    /// WHERE EACH METHOD IS HELD IN A METHOD TABLE: for every function some
    /// descriptor's slot names, each offset from where a method table begins
    /// (every base objects are stamped with) it lies at in any descriptor;
    /// null for one no descriptor holds. What a call that read its method out
    /// of a descriptor at one of those offsets may run (RegionSolver.Addressed).
    /// </summary>
    public static Func<string, IReadOnlyCollection<long>?> SlotsOf(List<(string Name, ObjectFile Object)> inputs)
    {
        Dictionary<string, HashSet<long>> slots = IndexOf(inputs).Slots();
        return method => slots.TryGetValue(method, out HashSet<long>? found) ? found : null;
    }

    /// <summary>
    /// WHAT A METHOD A DESCRIPTOR HOLDS MAY RUN ON (RegionTypes, a method
    /// called blind): whether an object stamped with a descriptor may be its
    /// `this` -- the descriptor holds it at a slot, or is or derives from one
    /// that does (null: no descriptor holds it, any); and whether an object
    /// of such a type may be one no allocation site in the IR made: one
    /// stamped in data, or by an object with no IR (<paramref name="archived"/>
    /// holds those with), which the region solvers know only as the unknown
    /// object or a constant (null: no descriptor holds it).
    /// </summary>
    public static (Func<string, string, bool?> MayBeThis, Func<string, bool?> MadeOutside) Receivers(
        List<(string Name, ObjectFile Object)> inputs, IReadOnlySet<ObjectFile> archived)
    {
        Index index = IndexOf(inputs);
        HashSet<string> outside = index.StampedOutside(archived);
        return ((method, table) => index.MayBeThis(method, table), method => index.MadeOutside(method, outside));
    }

    // ONE INDEX A LINK: the descriptors, the functions and the method-table
    // offsets read from the objects once, for every question the link asks
    // of them (its virtual calls, then the catches' ancestry).
    private static (List<(string Name, ObjectFile Object)> Inputs, int Count, Index Index)? _last;

    private static Index IndexOf(List<(string Name, ObjectFile Object)> inputs)
    {
        if (_last is { } last && ReferenceEquals(last.Inputs, inputs) && last.Count == inputs.Count) return last.Index;
        Index index = new(inputs);
        _last = (inputs, inputs.Count, index);
        return index;
    }

    private sealed class Index
    {
        private readonly int _word;
        // Every descriptor definition: each global name once, every local one
        // (a closure's type, say) as the separate descriptor it is.
        private readonly List<(string Name, Section Section, long Offset, long Size)> _descriptors = new();
        // The object each descriptor is defined in: its reference map is a
        // local symbol of that object (Lowering.ReferenceMap).
        private readonly List<ObjectFile> _descriptorObjects = new();
        private readonly Dictionary<int, Symbol?> _maps = new();
        private readonly HashSet<string> _functions = new(StringComparer.Ordinal);
        private readonly HashSet<long> _bases = new();
        public readonly Dictionary<string, int> ByName = new(StringComparer.Ordinal);
        // How many descriptors carry each name: a local one may be several.
        private readonly Dictionary<string, int> _named = new(StringComparer.Ordinal);
        private readonly Dictionary<int, List<(long Offset, string Symbol, long Addend)>> _relocs = new();
        // The tables a descriptor's display and interface list point at are
        // data symbols of their own, not descriptors: found by name anywhere.
        private readonly Dictionary<string, (Section Section, long Offset, long Size)> _tables = new(StringComparer.Ordinal);
        private readonly Dictionary<int, HashSet<string>> _ancestry = new();
        // Which descriptors are each type or derive from it: every ancestry
        // turned round once, not every descriptor asked for every call.
        private Dictionary<string, List<int>>? _derived;
        private readonly List<(string Name, ObjectFile Object)> _inputs;
        private Made? _made;

        public Index(List<(string Name, ObjectFile Object)> inputs)
        {
            _inputs = inputs;
            _word = inputs.Any(input => TargetContract.IsLongMode(input.Object)) ? 8 : 4;
            HashSet<string> globalDescriptors = new(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                foreach (Symbol symbol in input.Object.Symbols)
                {
                    if (!symbol.IsDefined) continue;
                    if (symbol.IsFunction) { _functions.Add(symbol.Name); continue; }
                    if (symbol.Section!.Kind is SectionKind.Code or SectionKind.Note) continue;
                    _tables.TryAdd(symbol.Name, (symbol.Section, symbol.Offset, symbol.Size));
                    if (IsDescriptor(symbol.Name) && (!symbol.Global || globalDescriptors.Add(symbol.Name)))
                    {
                        _descriptors.Add((symbol.Name, symbol.Section, symbol.Offset, symbol.Size));
                        _descriptorObjects.Add(input.Object);
                    }
                }
                // WHERE METHOD TABLES BEGIN: the addend objects are stamped
                // with, by code and by data alike -- a string literal, a static
                // object laid down whole, is stamped in data -- which is past
                // the descriptor's words, its table, for every kind of object
                // (Lowering: a class's, an array's, a box's, a string's). Not
                // every addend into a descriptor: code reads a descriptor's own
                // words at addends of their own, and each tried as a base put
                // methods at offsets no object's table has them -- MoveNext at
                // slot 0, where `x.ToString()` of an object reads its method,
                // and every iterator was called with anything (1319).
                foreach (Section section in input.Object.Sections)
                    if (section.Kind != SectionKind.Note)
                        foreach (Relocation r in section.Relocs)
                            if (r.Addend == DescriptorWords * _word && IsDescriptor(r.Symbol)) _bases.Add(r.Addend);
            }
            for (int d = 0; d < _descriptors.Count; d++)
            {
                ByName.TryAdd(_descriptors[d].Name, d);
                _named[_descriptors[d].Name] = _named.GetValueOrDefault(_descriptors[d].Name) + 1;
            }
            if (Switches.TraceVirtuals)
            {
                Console.Error.WriteLine("virtual index: word " + _word + ", descriptors " + _descriptors.Count + ", functions " + _functions.Count
                    + ", tables " + _tables.Count + ", bases " + string.Join(",", _bases.Order()));
                foreach (var (name, section, offset, size) in _descriptors)
                    Console.Error.WriteLine("virtual descriptor " + name + " " + section.Name + "+" + offset + " size " + size);
            }
        }

        private List<(long Offset, string Symbol, long Addend)> Relocs(int d)
        {
            if (_relocs.TryGetValue(d, out var known)) return known;
            List<(long, string, long)> found = new();
            var at = _descriptors[d];
            foreach (Relocation r in at.Section.Relocs)
                if (r.Offset >= at.Offset && (at.Size == 0 || r.Offset < at.Offset + at.Size)) found.Add((r.Offset - at.Offset, r.Symbol, r.Addend));
            return _relocs[d] = found;
        }

        /// <summary>
        /// The descriptors the image makes (VirtualTargets.Made): named by a
        /// relocation anywhere -- every section of every input, code and data,
        /// with IR or without -- or by a function or a data item of a unit's
        /// IR, but not by a descriptor's own self word nor from inside a
        /// table its display or interface list points at. A table is found
        /// as Ancestors finds it, in the descriptor's own object first (a
        /// local table of one unit is not another's of the same name); one
        /// with no size is not left out, whose entries then count.
        /// A RELOCATION AGAINST A SECTION rather than a symbol -- another
        /// toolchain's way to a local symbol -- counts every descriptor
        /// defined in that section of that object: nothing says which.
        /// </summary>
        public Made MadeTypes(IEnumerable<IrArchive> archives)
        {
            if (_made is not null) return _made;
            Made made = new() { Defined = _named.Count };
            int w = _word;
            // What each object defines, by name, for its descriptors' tables.
            Dictionary<ObjectFile, Dictionary<string, Symbol>> defined = new();
            Dictionary<string, Symbol> DefinedIn(ObjectFile obj)
            {
                if (defined.TryGetValue(obj, out var known)) return known;
                Dictionary<string, Symbol> names = new(StringComparer.Ordinal);
                foreach (Symbol symbol in obj.Symbols) if (symbol.IsDefined && !symbol.IsFunction) names.TryAdd(symbol.Name, symbol);
                return defined[obj] = names;
            }
            // Per section, the ranges whose relocations make nothing, and
            // each descriptor's self word.
            Dictionary<Section, List<(long Start, long End)>> skipped = new();
            HashSet<(Section, long, string)> selves = new();
            HashSet<string> tableNames = new(StringComparer.Ordinal);
            Dictionary<Section, List<string>> descriptorsIn = new();
            for (int d = 0; d < _descriptors.Count; d++)
            {
                var (name, section, offset, _) = _descriptors[d];
                selves.Add((section, offset + DescSelf * w, name));
                if (!descriptorsIn.TryGetValue(section, out List<string>? here)) descriptorsIn[section] = here = new();
                here.Add(name);
                foreach (var (at, symbol, _) in Relocs(d))
                {
                    if (at != Display * w && at != Interfaces * w) continue;
                    tableNames.Add(symbol);
                    (Section Section, long Offset, long Size) t;
                    if (DefinedIn(_descriptorObjects[d]).TryGetValue(symbol, out Symbol? own)) t = (own.Section!, own.Offset, own.Size);
                    else if (!_tables.TryGetValue(symbol, out t)) continue;
                    if (t.Size <= 0) continue;
                    if (!skipped.TryGetValue(t.Section, out var ranges)) skipped[t.Section] = ranges = new();
                    ranges.Add((t.Offset, t.Offset + t.Size));
                }
            }
            // In order and run together, a table two descriptors share once.
            foreach (Section section in skipped.Keys.ToList())
            {
                List<(long Start, long End)> ranges = skipped[section];
                ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
                List<(long Start, long End)> merged = new();
                foreach (var range in ranges)
                    if (merged.Count > 0 && range.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
                    else merged.Add(range);
                skipped[section] = merged;
            }
            static bool Inside(List<(long Start, long End)> ranges, long at)
            {
                int lo = 0, hi = ranges.Count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (at < ranges[mid].Start) hi = mid - 1;
                    else if (at >= ranges[mid].End) lo = mid + 1;
                    else return true;
                }
                return false;
            }
            foreach (var (_, obj) in _inputs)
                foreach (Section section in obj.Sections)
                {
                    if (section.Kind == SectionKind.Note) continue;
                    List<(long Start, long End)>? ranges = skipped.GetValueOrDefault(section);
                    foreach (Relocation r in section.Relocs)
                    {
                        if (!IsDescriptor(r.Symbol))
                        {
                            if (r.Symbol.StartsWith('.'))
                                foreach (Section named in obj.Sections)
                                    if (named.Name == r.Symbol && descriptorsIn.TryGetValue(named, out List<string>? all))
                                        made.Descriptors.UnionWith(all);
                            continue;
                        }
                        if (selves.Contains((section, r.Offset, r.Symbol))) continue;
                        if (ranges is not null && Inside(ranges, r.Offset)) continue;
                        made.Descriptors.Add(r.Symbol);
                    }
                }
            foreach (IrArchive archive in archives)
                foreach (IrArchiveEntry entry in archive.Entries.Values)
                {
                    // A function: every descriptor it names. A data item (and
                    // a shadow of another unit's descriptor): what it names,
                    // but itself, and nothing of a display or interface table.
                    bool function = entry.Key.StartsWith("F:", StringComparison.Ordinal);
                    if (!function && !entry.Key.StartsWith("D:", StringComparison.Ordinal) && !entry.Key.StartsWith("S:", StringComparison.Ordinal)) continue;
                    string own = entry.Key[2..];
                    if (!function && tableNames.Contains(own)) continue;
                    foreach (string named in entry.References)
                        if (IsDescriptor(named) && (function || named != own)) made.Descriptors.Add(named);
                }
            return _made = made;
        }

        // Per function: the descriptors that hold it at a method slot.
        private Dictionary<string, List<int>>? _holders;

        private List<int>? HoldersOf(string method)
        {
            if (_holders is null)
            {
                _holders = new(StringComparer.Ordinal);
                for (int d = 0; d < _descriptors.Count; d++)
                    foreach (var (offset, symbol, addend) in Relocs(d))
                        if (addend == 0 && offset >= DescriptorWords * _word && _functions.Contains(symbol))
                        {
                            if (!_holders.TryGetValue(symbol, out List<int>? list)) _holders[symbol] = list = new();
                            if (list.Count == 0 || list[^1] != d) list.Add(d);
                        }
            }
            return _holders.GetValueOrDefault(method);
        }

        public bool? MayBeThis(string method, string table)
        {
            if (HoldersOf(method) is not { } holders) return null;
            foreach (int h in holders)
                if (_descriptors[h].Name == table || IsA(table, _descriptors[h].Name) != false) return true;
            return false;
        }

        /// <summary>The descriptors stamped where no site of the IR is: in a section of data, or in any section of an object with no IR.</summary>
        public HashSet<string> StampedOutside(IReadOnlySet<ObjectFile> archived)
        {
            HashSet<string> outside = new(StringComparer.Ordinal);
            foreach (var (_, obj) in _inputs)
                foreach (Section section in obj.Sections)
                    if (section.Kind != SectionKind.Note && (section.Kind != SectionKind.Code || !archived.Contains(obj)))
                        foreach (Relocation r in section.Relocs)
                            if (r.Addend > 0 && IsDescriptor(r.Symbol)) outside.Add(r.Symbol);
            return outside;
        }

        public bool? MadeOutside(string method, HashSet<string> outside)
        {
            if (HoldersOf(method) is not { } holders) return null;
            foreach (string stamped in outside)
                foreach (int h in holders)
                    if (stamped == _descriptors[h].Name || IsA(stamped, _descriptors[h].Name) != false) return true;
            return false;
        }

        private Dictionary<string, HashSet<long>>? _slots;

        public Dictionary<string, HashSet<long>> Slots()
        {
            if (_slots is not null) return _slots;
            Dictionary<string, HashSet<long>> slots = new(StringComparer.Ordinal);
            for (int d = 0; d < _descriptors.Count; d++)
                foreach (var (offset, symbol, addend) in Relocs(d))
                {
                    if (addend != 0 || offset < DescriptorWords * _word || !_functions.Contains(symbol)) continue;
                    foreach (long b in _bases)
                        if (offset >= b)
                        {
                            if (!slots.TryGetValue(symbol, out HashSet<long>? at)) slots[symbol] = at = new();
                            at.Add(offset - b);
                        }
                }
            return _slots = slots;
        }

        private IEnumerable<string> TableEntries(string table)
        {
            if (!_tables.TryGetValue(table, out var at)) yield break;
            foreach (Relocation r in at.Section.Relocs)
                if (r.Offset >= at.Offset && (at.Size == 0 || r.Offset < at.Offset + at.Size)) yield return r.Symbol;
        }

        public HashSet<string> Ancestors(int d)
        {
            if (_ancestry.TryGetValue(d, out HashSet<string>? known)) return known;
            HashSet<string> found = new(StringComparer.Ordinal) { _descriptors[d].Name };
            Stack<int> pending = new();
            pending.Push(d);
            while (pending.Count > 0)
            {
                int at = pending.Pop();
                foreach (var (offset, symbol, _) in Relocs(at))
                    if (offset == Display * _word || offset == Interfaces * _word)
                        foreach (string up in TableEntries(symbol))
                            if ((up.StartsWith("t_", StringComparison.Ordinal) || up.StartsWith("i_", StringComparison.Ordinal)) && found.Add(up)
                                && ByName.TryGetValue(up, out int upper))
                                pending.Push(upper);
            }
            return _ancestry[d] = found;
        }

        public bool? IsA(string descriptor, string type)
        {
            if (type == "t_object") return true;
            if (_named.GetValueOrDefault(descriptor) != 1 || !ByName.TryGetValue(descriptor, out int d)) return null;
            if (Ancestors(d).Contains(type)) return true;
            // A box's or a string's system interfaces are in no table of it.
            return MayAnswer(descriptor, type) ? null : false;
        }

        public string? MethodAt(string descriptor, long offset)
        {
            if (_named.GetValueOrDefault(descriptor) != 1 || !ByName.TryGetValue(descriptor, out int d)) return null;
            foreach (var (at, symbol, addend) in Relocs(d))
                if (at == offset) return addend == 0 && _functions.Contains(symbol) ? symbol : null;
            return null;
        }

        /// <summary>
        /// Whether the word <paramref name="offset"/> bytes into an object
        /// stamped with <paramref name="descriptor"/> at <paramref name="at"/>
        /// is one the collector never reads as a reference (null: any word of
        /// it). The collector's rules (Gc.ScanBlockWithin) and its own checks
        /// first: the stamp is where the method table begins, and the
        /// descriptor names itself. An array of numbers or a string holds
        /// none; an instance holds one only where its reference map says. No
        /// answer -- more than one descriptor by the name, bytes not here --
        /// is false.
        /// </summary>
        public bool HoldsNoReference(string descriptor, long at, long? offset)
        {
            int w = _word;
            if (at != DescriptorWords * w || _named.GetValueOrDefault(descriptor) != 1 || !ByName.TryGetValue(descriptor, out int d)) return false;
            var (_, section, start, _) = _descriptors[d];
            List<(long Offset, string Symbol, long Addend)> relocs = Relocs(d);
            if (!relocs.Any(r => r.Offset == DescSelf * w && r.Symbol == descriptor && r.Addend == 0)) return false;
            if (Word(section, start + DescFlags * w) is not long flags) return false;
            if ((flags & 1) != 0)
                return (flags & 2) != 0 || Word(section, start + DescGcFlags * w) is long gc && (gc & 1) == 0;
            int mapAt = relocs.FindIndex(r => r.Offset == DescRefMap * w);
            if (mapAt < 0) return true;
            if (offset is not long o || relocs[mapAt].Addend != 0) return false;
            if (o % w != 0) return true;
            if (!_maps.TryGetValue(d, out Symbol? map))
                _maps[d] = map = _descriptorObjects[d].Symbols.FirstOrDefault(s => s.IsDefined && s.Name == relocs[mapAt].Symbol);
            if (map is null || Word(map.Section!, map.Offset) is not long words) return false;
            long word = o / w;
            if (word >= words || Word(map.Section!, map.Offset + (1 + word / 32) * w) is not long bits) return false;
            return ((bits >> (int)(word % 32)) & 1) == 0;
        }

        // A word of a section's bytes, as the target stores it; null when not here.
        private long? Word(Section section, long at)
        {
            if (section.FileBacked is not null || section.HandedOver is not null || at < 0 || at + _word > section.Bytes.Count) return null;
            long value = 0;
            for (int k = _word - 1; k >= 0; k--) value = value << 8 | section.Bytes[(int)at + k];
            return value;
        }

        /// <summary>
        /// The functions a virtual call reaches; empty when no object of the
        /// type exists, or none of them fills the slot; null when a slot holds
        /// what is not code here (a symbol defined elsewhere, data, an
        /// addend). A DESCRIPTOR THAT LEAVES THE SLOT EMPTY runs nothing
        /// there: an abstract class's (no object of it is made), a box asked
        /// for a system interface it does not answer (MayAnswer: IEquatable of
        /// an enum), an interface's descriptor-less implementors. A call on
        /// one of its objects would jump to nothing; that none does is the
        /// program's type safety. Left unresolved, one such call made every
        /// method a descriptor names a root (RegionSolver.Addressed).
        /// </summary>
        public string[]? Targets(string declaring, long slot, Made? made = null)
        {
            if (_derived is null)
            {
                _derived = new(StringComparer.Ordinal);
                for (int d = 0; d < _descriptors.Count; d++)
                    foreach (string type in Ancestors(d))
                    {
                        if (!_derived.TryGetValue(type, out List<int>? list)) _derived[type] = list = new();
                        list.Add(d);
                    }
            }
            bool everyType = declaring == "t_object";
            IEnumerable<int> reaching = everyType ? Enumerable.Range(0, _descriptors.Count)
                : _derived.TryGetValue(declaring, out List<int>? derived) ? derived : Enumerable.Empty<int>();
            // And every box and the string, for an interface (MayAnswer).
            if (!everyType && IsBoxedFace(declaring))
                reaching = reaching.Union(Enumerable.Range(0, _descriptors.Count).Where(d => MayAnswer(_descriptors[d].Name, declaring)));
            HashSet<string> targets = new(StringComparer.Ordinal);
            bool any = everyType, resolved = true;
            // What every descriptor would give, for the report: the targets
            // of those nothing makes, and whether they alone left it unresolved.
            HashSet<string>? unmade = made is null ? null : new(StringComparer.Ordinal);
            bool resolvedAll = true;
            foreach (int descriptor in reaching)
            {
                // ONLY WHAT IS MADE (Made): an object of a type no
                // relocation stamps does not exist in a closed image, and
                // what its descriptor holds is run on none.
                bool exists = made is null || made.Contains(_descriptors[descriptor].Name);
                if (exists) any = true;
                if (Switches.TraceVirtuals)
                    Console.Error.WriteLine("virtual   " + declaring + "+" + slot + " reaches " + _descriptors[descriptor].Name + (exists ? "" : " (never made)") + " relocs "
                        + string.Join(" ", Relocs(descriptor).Select(r => r.Offset + ":" + r.Symbol + (r.Addend == 0 ? "" : "+" + r.Addend))));
                foreach (var (offset, symbol, addend) in Relocs(descriptor))
                    if (_bases.Contains(offset - slot))
                    {
                        bool code = addend == 0 && _functions.Contains(symbol);
                        if (!exists)
                        {
                            unmade!.Add(symbol);
                            if (!code) resolvedAll = false;
                            made!.Dropped.Add(_descriptors[descriptor].Name);
                            continue;
                        }
                        if (!code) resolved = false;
                        targets.Add(symbol);
                    }
            }
            if (made is not null)
            {
                made.Calls++;
                unmade!.ExceptWith(targets);
                made.Targets += targets.Count + unmade.Count;
                if (unmade.Count > 0) { made.Narrowed++; made.Removed += unmade.Count; }
                if (resolved && !resolvedAll) made.ResolvedOnly++;
            }
            // No object of the type exists, or none fills the slot: the call
            // runs nothing.
            if (!any) return Array.Empty<string>();
            return resolved ? targets.Order(StringComparer.Ordinal).ToArray() : null;
        }
    }
}
