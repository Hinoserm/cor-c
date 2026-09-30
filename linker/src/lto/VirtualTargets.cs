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
        List<(string Name, string Declaring, long Slot)> calls = new();
        foreach (string name in wanted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            int plus = name.LastIndexOf('+');
            if (plus <= Prefix.Length || !long.TryParse(name.AsSpan(plus + 1), out long slot)) continue;
            calls.Add((name, name[Prefix.Length..plus], slot));
        }
        if (calls.Count == 0) return answers;
        int word = inputs.Any(input => TargetContract.IsLongMode(input.Object)) ? 8 : 4;

        // Every definition, the first of each global name (link order, as
        // symbol resolution picks); a descriptor's relocations by offset.
        // Every descriptor definition: each global name once, every local one
        // (a closure's type, say) as the separate descriptor it is.
        List<(string Name, Section Section, long Offset, long Size)> descriptors = new();
        HashSet<string> globalDescriptors = new(StringComparer.Ordinal);
        HashSet<string> functions = new(StringComparer.Ordinal);
        HashSet<long> bases = new();
        foreach (var input in inputs)
        {
            foreach (Symbol symbol in input.Object.Symbols)
            {
                if (!symbol.IsDefined) continue;
                if (symbol.IsFunction) functions.Add(symbol.Name);
                else if (IsDescriptor(symbol.Name) && symbol.Section!.Kind is not (SectionKind.Code or SectionKind.Note)
                    && (!symbol.Global || globalDescriptors.Add(symbol.Name)))
                    descriptors.Add((symbol.Name, symbol.Section, symbol.Offset, symbol.Size));
            }
            // Where method tables begin: the addends objects are stamped with.
            foreach (Section section in input.Object.Sections)
                if (section.Kind == SectionKind.Code)
                    foreach (Relocation r in section.Relocs)
                        if (r.Addend > 0 && IsDescriptor(r.Symbol)) bases.Add(r.Addend);
        }
        Dictionary<string, int> byName = new(StringComparer.Ordinal);
        for (int d = 0; d < descriptors.Count; d++) byName.TryAdd(descriptors[d].Name, d);
        Dictionary<int, List<(long Offset, string Symbol, long Addend)>> relocsOf = new();
        List<(long Offset, string Symbol, long Addend)> Relocs(int d)
        {
            if (relocsOf.TryGetValue(d, out var known)) return known;
            List<(long, string, long)> found = new();
            var at = descriptors[d];
            foreach (Relocation r in at.Section.Relocs)
                if (r.Offset >= at.Offset && (at.Size == 0 || r.Offset < at.Offset + at.Size)) found.Add((r.Offset - at.Offset, r.Symbol, r.Addend));
            return relocsOf[d] = found;
        }
        // The tables a descriptor's display and interface list point at are
        // data symbols of their own, not descriptors: found by name anywhere.
        Dictionary<string, (Section Section, long Offset, long Size)> tables = new(StringComparer.Ordinal);
        foreach (var input in inputs)
            foreach (Symbol symbol in input.Object.Symbols)
                if (symbol.IsDefined && !symbol.IsFunction && symbol.Section!.Kind is not (SectionKind.Code or SectionKind.Note))
                    tables.TryAdd(symbol.Name, (symbol.Section, symbol.Offset, symbol.Size));
        IEnumerable<string> TableEntries(string table)
        {
            if (!tables.TryGetValue(table, out var at)) yield break;
            foreach (Relocation r in at.Section.Relocs)
                if (r.Offset >= at.Offset && (at.Size == 0 || r.Offset < at.Offset + at.Size)) yield return r.Symbol;
        }
        Dictionary<int, HashSet<string>> ancestry = new();
        HashSet<string> Ancestors(int d)
        {
            if (ancestry.TryGetValue(d, out HashSet<string>? known)) return known;
            HashSet<string> found = new(StringComparer.Ordinal) { descriptors[d].Name };
            Stack<int> pending = new();
            pending.Push(d);
            while (pending.Count > 0)
            {
                int at = pending.Pop();
                foreach (var (offset, symbol, _) in Relocs(at))
                    if (offset == Display * word || offset == Interfaces * word)
                        foreach (string up in TableEntries(symbol))
                            if ((up.StartsWith("t_", StringComparison.Ordinal) || up.StartsWith("i_", StringComparison.Ordinal)) && found.Add(up)
                                && byName.TryGetValue(up, out int upper))
                                pending.Push(upper);
            }
            return ancestry[d] = found;
        }

        foreach (var (name, declaring, slot) in calls)
        {
            bool everyType = declaring == "t_object";
            HashSet<string> targets = new(StringComparer.Ordinal);
            bool any = everyType, resolved = true;
            for (int descriptor = 0; descriptor < descriptors.Count; descriptor++)
            {
                if (!everyType && !Ancestors(descriptor).Contains(declaring)) continue;
                any = true;
                foreach (var (offset, symbol, addend) in Relocs(descriptor))
                    if (addend == 0 && bases.Contains(offset - slot))
                    {
                        if (!functions.Contains(symbol)) resolved = false;
                        targets.Add(symbol);
                    }
            }
            // No object of the type exists: the call is never made.
            if (!any) { answers[name] = Array.Empty<string>(); continue; }
            if (resolved && targets.Count > 0) answers[name] = targets.Order(StringComparer.Ordinal).ToArray();
        }
        return answers;
    }
}
