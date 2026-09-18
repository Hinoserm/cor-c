using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

public static class DefinitionCoalescer
{
    public static int Run(IReadOnlyList<(string Name, ObjectFile Object)> inputs, bool validateOnly = false)
    {
        Dictionary<string, List<(string Name, ObjectFile Object, Symbol Symbol, byte[]? Semantic)>> groups = new(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            Dictionary<string, byte[]> contracts = CoalescingContract.Read(input.Object);
            foreach (Symbol symbol in input.Object.Symbols.Where(symbol => symbol.Global && symbol.IsDefined))
            {
                if (!groups.TryGetValue(symbol.Name, out var group)) groups.Add(symbol.Name, group = new());
                group.Add((input.Name, input.Object, symbol, contracts.GetValueOrDefault(symbol.Name)));
            }
        }
        List<(ObjectFile Object, Symbol Symbol)> remove = new();
        List<string> errors = new();
        foreach (var pair in groups.Where(pair => pair.Value.Count > 1))
        {
            var group = pair.Value.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
            if (group.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != group.Length)
            { errors.Add("Duplicate input identity while coalescing '" + pair.Key + "'"); continue; }
            byte[]? expected = group[0].Semantic;
            if (expected is null || group.Any(value => value.Semantic is null || !value.Semantic.SequenceEqual(expected)))
            {
                errors.Add("'" + pair.Key + "' is defined in both " + group[0].Name + " and " + string.Join(" and ", group.Skip(1).Select(value => value.Name))
                    + " (incompatible or uncertified duplicate definition)");
                continue;
            }
            // The proof covers the function/data semantics, not a demand for
            // byte-identical optimization choices. Pick a stable input owner.
            foreach (var loser in group.Skip(1)) remove.Add((loser.Object, loser.Symbol));
        }
        if (errors.Count > 0) throw new LinkException(errors);
        // Complete every check before changing any input.
        foreach (var item in remove)
            if (item.Object.Symbols.Any(other => other.Name == "__corsac_retained_" + item.Symbol.Name))
                throw new ElfFormatException("Reserved coalescing alias collision: " + item.Symbol.Name);
        if (validateOnly) return remove.Count;
        foreach (var item in remove)
        {
            ObjectFile obj = item.Object;
            Symbol symbol = item.Symbol;
            string retained = "__corsac_retained_" + symbol.Name;
            obj.Symbols.Add(new Symbol { Name = retained, Section = symbol.Section, Offset = symbol.Offset,
                Size = symbol.Size, IsFunction = symbol.IsFunction, Global = false });
            // Retained debug/stack-map tables describe the still-present local
            // bytes, not another unit's potentially differently optimized body.
            Symbol? frames = obj.Symbols.FirstOrDefault(other => other.Name == "__corsac_frames" && other.IsDefined);
            foreach (Section section in obj.Sections)
                for (int i = 0; i < section.Relocs.Count; i++)
                {
                    Relocation relocation = section.Relocs[i];
                    bool metadata = section.Name.EndsWith(".corsac.stackmaps", StringComparison.Ordinal)
                        || (frames?.Section == section && relocation.Offset >= frames.Offset && relocation.Offset < frames.Offset + frames.Size);
                    if (metadata && relocation.Symbol == symbol.Name)
                        section.Relocs[i] = new Relocation(relocation.Offset, retained, relocation.Addend, relocation.Kind);
                }
            obj.Symbols.Remove(symbol);
            obj.Symbols.Add(new Symbol { Name = symbol.Name, IsFunction = symbol.IsFunction });
            obj.SuppressedDefinitions.Add(symbol.Name);
        }
        // Contracts attest pre-link bytes and are consumed by the final link.
        // Later optimization may change those bytes; do not publish stale proof.
        foreach (var input in inputs)
            input.Object.Sections.RemoveAll(section => section.Name == CoalescingContract.SectionName);
        return remove.Count;
    }
}
