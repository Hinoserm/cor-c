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
    /// </summary>
    public static bool MayAnswer(string descriptor, string declaring)
        => declaring.StartsWith("i_", StringComparison.Ordinal)
           && (descriptor.StartsWith("b_", StringComparison.Ordinal) || descriptor == "q_string");

    /// <summary>
    /// Each of <paramref name="wanted"/> (virtual symbols) that can be
    /// resolved, to the functions it reaches.
    /// </summary>
    public static Dictionary<string, string[]> Resolve(List<(string Name, ObjectFile Object)> inputs, IEnumerable<string> wanted)
    {
        Dictionary<string, string[]> answers = new(StringComparer.Ordinal);
        Index? index = null;
        foreach (string name in wanted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            int plus = name.LastIndexOf('+');
            if (plus <= Prefix.Length || !long.TryParse(name.AsSpan(plus + 1), out long slot)) continue;
            index ??= IndexOf(inputs);
            string[]? reached = index.Targets(name[Prefix.Length..plus], slot);
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

        public Index(List<(string Name, ObjectFile Object)> inputs)
        {
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
                // Where method tables begin: the addends objects are stamped with.
                foreach (Section section in input.Object.Sections)
                    if (section.Kind == SectionKind.Code)
                        foreach (Relocation r in section.Relocs)
                            if (r.Addend > 0 && IsDescriptor(r.Symbol)) _bases.Add(r.Addend);
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

        /// <summary>The functions a virtual call reaches; empty when no object of the type exists; null when a slot is not code.</summary>
        public string[]? Targets(string declaring, long slot)
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
            if (!everyType && declaring.StartsWith("i_", StringComparison.Ordinal))
                reaching = reaching.Union(Enumerable.Range(0, _descriptors.Count).Where(d => MayAnswer(_descriptors[d].Name, declaring)));
            HashSet<string> targets = new(StringComparer.Ordinal);
            bool any = everyType, resolved = true;
            foreach (int descriptor in reaching)
            {
                any = true;
                if (Switches.TraceVirtuals)
                    Console.Error.WriteLine("virtual   " + declaring + "+" + slot + " reaches " + _descriptors[descriptor].Name + " relocs "
                        + string.Join(" ", Relocs(descriptor).Select(r => r.Offset + ":" + r.Symbol + (r.Addend == 0 ? "" : "+" + r.Addend))));
                foreach (var (offset, symbol, addend) in Relocs(descriptor))
                    if (addend == 0 && _bases.Contains(offset - slot))
                    {
                        if (!_functions.Contains(symbol)) resolved = false;
                        targets.Add(symbol);
                    }
            }
            // No object of the type exists: the call is never made.
            if (!any) return Array.Empty<string>();
            return resolved && targets.Count > 0 ? targets.Order(StringComparer.Ordinal).ToArray() : null;
        }
    }
}
