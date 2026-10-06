using System.Security.Cryptography;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

public static class LinkTimeOptimizer
{
    /// <summary>Static, non-interposable links only. Validates before changing any bytes.</summary>
    public static int Run(IReadOnlyList<(string Name, ObjectFile Object)> inputs, bool enabled = true)
    {
        TargetContract.Validate(inputs);
        ManagedLayoutContract.Validate(inputs);
        DefinitionCoalescer.Run(inputs, validateOnly: true);
        foreach (var input in inputs) _ = IrArchive.Read(input.Object);
        Dictionary<string, (ObjectFile Object, Symbol Symbol)> globals = new(StringComparer.Ordinal);
        Dictionary<ObjectFile, OptimizationSummary> summaries = new();
        Dictionary<ObjectFile, Dictionary<string, int>> constants = new();
        // IN LINK ORDER, not by name: an object's name is a digest of its
        // source's full path, and the same tree checked out elsewhere was
        // ordered otherwise -- other owners, other symbol order, other bytes.
        foreach (var input in inputs)
        {
            foreach (Symbol symbol in input.Object.Symbols.Where(s => s.IsDefined && s.Global))
                globals.TryAdd(symbol.Name, (input.Object, symbol));
            OptimizationSummary? summary;
            try { summary = OptimizationSummary.Read(input.Object); }
            catch (ElfFormatException error) { throw new ElfFormatException(input.Name + ": " + error.Message); }
            if (summary is null) continue;
            summaries.Add(input.Object, summary);
            Dictionary<string, int> values = new(StringComparer.Ordinal);
            // EACH NAME'S DEFINITIONS, found once: every return asked the whole
            // symbol table for its name, returns times symbols in each unit.
            Dictionary<string, List<Symbol>>? definitions = summary.Returns.Count == 0 ? null : DefinitionsByName(input.Object);
            foreach (ConstantReturn returned in summary.Returns)
            {
                if (input.Object.SuppressedDefinitions.Contains(returned.Symbol)) continue;
                Symbol[] matches = definitions!.TryGetValue(returned.Symbol, out List<Symbol>? named) ? named.ToArray() : Array.Empty<Symbol>();
                if (matches.Length != 1 || !matches[0].IsFunction || matches[0].Section!.Kind != SectionKind.Code)
                    throw new ElfFormatException(input.Name + ": invalid LTO function definition " + returned.Symbol);
                Symbol symbol = matches[0];
                int length = symbol.Section!.Bytes.Count;
                if (symbol.Offset < 0 || symbol.Size <= 0 || symbol.Offset > length
                    || symbol.Size > length - symbol.Offset
                    || !OptimizationSummary.HashCode(symbol.Section, (int)symbol.Offset, (int)symbol.Size).SequenceEqual(returned.CodeHash))
                    throw new ElfFormatException(input.Name + ": LTO function hash mismatch " + returned.Symbol);
                values.Add(returned.Symbol, returned.Value);
            }
            constants.Add(input.Object, values);
        }
        List<(Section Text, Relocation Relocation, int Value)> changes = new();
        foreach (var input in inputs)
        {
            if (!summaries.TryGetValue(input.Object, out OptimizationSummary? summary)) continue;
            Section text = input.Object.Section(".text");
            // THE RELOCATIONS BY OFFSET AND THE DEFINITIONS BY NAME, made once a
            // unit: each call asked every relocation of the unit's code twice
            // (its own, and any overlapping it) and every symbol once -- calls
            // times relocations, in every unit of the image. A relocation's
            // four bytes overlap the call's from four before it to three
            // after, so those offsets are all the overlap test can be.
            Dictionary<int, List<Relocation>>? at = summary.Calls.Count == 0 ? null : RelocationsByOffset(text);
            Dictionary<string, List<Symbol>>? named = summary.Calls.Count == 0 ? null : DefinitionsByName(input.Object);
            foreach (DirectCall call in summary.Calls)
            {
                Relocation[] matches = at!.TryGetValue(call.Offset, out List<Relocation>? here) ? here.ToArray() : Array.Empty<Relocation>();
                bool overlapped = false;
                for (int near = call.Offset - 4; near <= call.Offset + 3 && !overlapped; near++)
                    overlapped = near != call.Offset && at.ContainsKey(near);
                if (call.Offset > text.Bytes.Count - 4 || text.Bytes[call.Offset - 1] != 0xe8
                    || matches.Length != 1 || matches[0].Kind is not (RelocKind.Rel32 or RelocKind.Plt32) || matches[0].Addend != -4
                    || matches[0].Symbol != call.Symbol
                    || overlapped)
                    throw new ElfFormatException(input.Name + ": invalid LTO direct call " + call.Symbol);
                // Local definitions shadow globals, exactly as native symbol resolution does.
                Symbol? local = named!.TryGetValue(call.Symbol, out List<Symbol>? candidates) ? candidates.FirstOrDefault(s => !s.Global) : null;
                ObjectFile? owner = local is not null ? input.Object
                    : globals.TryGetValue(call.Symbol, out var found) ? found.Object : null;
                if (owner is not null && constants.TryGetValue(owner, out var values) && values.TryGetValue(call.Symbol, out int value))
                    changes.Add((text, matches[0], value));
            }
        }
        if (enabled) foreach (var change in changes)
        {
            int offset = change.Relocation.Offset;
            change.Text.Bytes[offset - 1] = 0xb8;
            for (int i = 0; i < 4; i++) change.Text.Bytes[offset + i] = (byte)((uint)change.Value >> (8 * i));
            change.Text.Relocs.Remove(change.Relocation);
        }
        // After the calls are rewritten: coalescing cuts duplicate copies out
        // of their sections (DuplicateCutter), which moves the relocations
        // the changes above were found at.
        DefinitionCoalescer.Run(inputs);
        // Summaries describe pre-link code. Do not leave stale summaries in output objects.
        foreach (var input in inputs) input.Object.Sections.RemoveAll(s => s.Name == OptimizationSummary.SectionName || s.Name == IrArchive.SectionName
            || s.Name == LifetimeHints.SectionName || s.Name == RegionHints.SectionName);
        return enabled ? changes.Count : 0;
    }

    /// <summary>An object's defined symbols by name, each name's in the order the table holds them.</summary>
    private static Dictionary<string, List<Symbol>> DefinitionsByName(ObjectFile obj)
    {
        Dictionary<string, List<Symbol>> named = new(StringComparer.Ordinal);
        foreach (Symbol symbol in obj.Symbols)
        {
            if (!symbol.IsDefined) continue;
            if (!named.TryGetValue(symbol.Name, out List<Symbol>? list)) named[symbol.Name] = list = new(1);
            list.Add(symbol);
        }
        return named;
    }

    /// <summary>A section's relocations by the offset they patch, each offset's in the order the section holds them.</summary>
    private static Dictionary<int, List<Relocation>> RelocationsByOffset(Section section)
    {
        Dictionary<int, List<Relocation>> at = new();
        foreach (Relocation r in section.Relocs)
        {
            if (!at.TryGetValue(r.Offset, out List<Relocation>? list)) at[r.Offset] = list = new(1);
            list.Add(r);
        }
        return at;
    }
}
