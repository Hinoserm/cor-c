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

    private static bool IsDescriptor(string name) => name.Length > 2 && name[1] == '_' && name[0] is 't' or 'q' or 'v' or 'b';

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
            if (index.Targets(name[Prefix.Length..plus], slot) is string[] targets) answers[name] = targets;
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
                        _descriptors.Add((symbol.Name, symbol.Section, symbol.Offset, symbol.Size));
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

        public string? MethodAt(string descriptor, long offset)
        {
            if (_named.GetValueOrDefault(descriptor) != 1 || !ByName.TryGetValue(descriptor, out int d)) return null;
            foreach (var (at, symbol, addend) in Relocs(d))
                if (at == offset) return addend == 0 && _functions.Contains(symbol) ? symbol : null;
            return null;
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
            HashSet<string> targets = new(StringComparer.Ordinal);
            bool any = everyType, resolved = true;
            foreach (int descriptor in reaching)
            {
                any = true;
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
