using System.Security.Cryptography;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

public static class LinkTimeOptimizer
{
    /// <summary>Static, non-interposable links only. Validates before changing any bytes.</summary>
    public static int Run(IReadOnlyList<(string Name, ObjectFile Object)> inputs, bool enabled = true)
    {
        Dictionary<string, (ObjectFile Object, Symbol Symbol)> globals = new(StringComparer.Ordinal);
        Dictionary<ObjectFile, OptimizationSummary> summaries = new();
        Dictionary<ObjectFile, Dictionary<string, int>> constants = new();
        foreach (var input in inputs)
        {
            foreach (Symbol symbol in input.Object.Symbols.Where(s => s.IsDefined && s.Global))
                if (!globals.TryAdd(symbol.Name, (input.Object, symbol)))
                    throw new LinkException(new[] { "duplicate definition '" + symbol.Name + "' in " + input.Name });
            OptimizationSummary? summary;
            try { summary = OptimizationSummary.Read(input.Object); }
            catch (ElfFormatException error) { throw new ElfFormatException(input.Name + ": " + error.Message); }
            if (summary is null) continue;
            summaries.Add(input.Object, summary);
            Dictionary<string, int> values = new(StringComparer.Ordinal);
            foreach (ConstantReturn returned in summary.Returns)
            {
                Symbol[] matches = input.Object.Symbols.Where(s => s.Name == returned.Symbol && s.IsDefined).ToArray();
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
            foreach (DirectCall call in summary.Calls)
            {
                Relocation[] matches = text.Relocs.Where(r => r.Offset == call.Offset).ToArray();
                if (call.Offset > text.Bytes.Count - 4 || text.Bytes[call.Offset - 1] != 0xe8
                    || matches.Length != 1 || matches[0].Kind != RelocKind.Rel32 || matches[0].Addend != -4
                    || matches[0].Symbol != call.Symbol
                    || text.Relocs.Any(r => r.Offset != call.Offset && r.Offset < call.Offset + 4 && r.Offset + 4 > call.Offset - 1))
                    throw new ElfFormatException(input.Name + ": invalid LTO direct call " + call.Symbol);
                // Local definitions shadow globals, exactly as native symbol resolution does.
                Symbol? local = input.Object.Symbols.FirstOrDefault(s => s.Name == call.Symbol && s.IsDefined && !s.Global);
                ObjectFile? owner = local is not null ? input.Object
                    : globals.TryGetValue(call.Symbol, out var found) ? found.Object : null;
                if (owner is not null && constants.TryGetValue(owner, out var values) && values.TryGetValue(call.Symbol, out int value))
                    changes.Add((text, matches[0], value));
            }
        }
        if (!enabled) return 0;
        foreach (var change in changes)
        {
            int offset = change.Relocation.Offset;
            change.Text.Bytes[offset - 1] = 0xb8;
            for (int i = 0; i < 4; i++) change.Text.Bytes[offset + i] = (byte)((uint)change.Value >> (8 * i));
            change.Text.Relocs.Remove(change.Relocation);
        }
        // Summaries describe pre-link code. Do not leave stale summaries in output objects.
        foreach (var input in inputs) input.Object.Sections.RemoveAll(s => s.Name == OptimizationSummary.SectionName);
        return changes.Count;
    }
}
