using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Summary-only planning with bounded, selective cross-unit body imports.</summary>
public static class IrLinkOptimizer
{
    public static int Run(List<(string Name, ObjectFile Object)> inputs, Func<IUnitBackend> backend,
        bool enabled = true, int importBytes = 1024 * 1024, int bodyLimit = 32, string? closedImageEntry = null)
    {
        if (importBytes < 0 || bodyLimit < 0) throw new ArgumentOutOfRangeException(nameof(importBytes));
        TargetContract.Validate(inputs); ManagedLayoutContract.Validate(inputs);
        DefinitionCoalescer.Run(inputs, validateOnly: true);
        Dictionary<ObjectFile, IrArchive> archives = new();
        Dictionary<string, ObjectFile> owners = new(StringComparer.Ordinal);
        foreach (var input in inputs.OrderBy(input => input.Name, StringComparer.Ordinal))
        {
            IrArchive? archive = IrArchive.Read(input.Object);
            if (archive is not null) archives.Add(input.Object, archive);
            foreach (Symbol symbol in input.Object.Symbols.Where(symbol => symbol.Global && symbol.IsDefined))
                owners.TryAdd(symbol.Name, input.Object);
        }
        Dictionary<ObjectFile, HashSet<string>>? reachability = enabled && closedImageEntry is not null
            ? IrReachability.Find(inputs, archives, owners, closedImageEntry) : null;
        List<(int Index, List<(string Symbol, IrArchive Archive, IrArchiveEntry Body)> Imports, HashSet<string>? Retained)> plans = new();
        if (enabled)
            for (int index = 0; index < inputs.Count; index++)
            {
                ObjectFile obj = inputs[index].Object;
                if (!archives.TryGetValue(obj, out IrArchive? archive)) continue;
                HashSet<string>? retained = reachability?.GetValueOrDefault(obj);
                bool prune = retained is not null && archive.Entries.Keys.Count(key => key != "M:unit") != retained.Count;
                HashSet<string> defined = obj.Symbols.Where(symbol => symbol.IsDefined).Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
                List<(string Symbol, IrArchive Archive, IrArchiveEntry Body)> imports = new(); int used = 0;
                foreach (string call in archive.Entries.Values.Where(record => retained is null || retained.Contains(record.Key))
                    .SelectMany(record => record.Calls).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    if (defined.Contains(call) || !owners.TryGetValue(call, out ObjectFile? owner) || !archives.TryGetValue(owner, out IrArchive? provider)
                        || !provider.Entries.TryGetValue("F:" + call, out IrArchiveEntry? body) || !body.Importable
                        || body.Instructions > 160 || body.Length > importBytes - used || imports.Count >= bodyLimit) continue;
                    imports.Add((call, provider, body)); used += body.Length;
                }
                if (imports.Count > 0 || prune) plans.Add((index, imports, prune ? retained : null));
            }
        IUnitBackend? service = null;
        List<(int Index, ObjectFile Object)> replacements = new();
        try
        {
            foreach (var plan in plans)
            {
                service ??= backend();
                ObjectFile original = inputs[plan.Index].Object;
                IrImport[] imports = plan.Imports.Select(import => new IrImport(import.Symbol, import.Archive.ReadBody(import.Body.Key))).ToArray();
                ObjectFile replacement = service.Recompile(original, imports, plan.Retained);
                TargetContract.Validate(new[] { ("original", original), ("regenerated", replacement) });
                ManagedLayoutContract.Validate(new[] { ("original", original), ("regenerated", replacement) });
                IrArchive originalArchive = archives[original];
                bool Kept(Symbol symbol) => plan.Retained is null || plan.Retained.Contains("F:" + symbol.Name) || plan.Retained.Contains("D:" + symbol.Name)
                    || !originalArchive.Entries.ContainsKey("F:" + symbol.Name) && !originalArchive.Entries.ContainsKey("D:" + symbol.Name);
                string[] before = original.Symbols.Where(symbol => symbol.Global && symbol.IsDefined && Kept(symbol)).Select(symbol => symbol.Name).Order(StringComparer.Ordinal).ToArray();
                string[] after = replacement.Symbols.Where(symbol => symbol.Global && symbol.IsDefined).Select(symbol => symbol.Name).Order(StringComparer.Ordinal).ToArray();
                if (!before.SequenceEqual(after)) throw new ElfFormatException("IR backend changed the unit's exported definitions");
                replacements.Add((plan.Index, replacement));
                Console.Error.WriteLine("LTO IR: " + inputs[plan.Index].Name + ": imported " + imports.Length + " bodies, "
                    + imports.Sum(import => import.Body.Length) + " bytes; retained records=" + (plan.Retained?.Count.ToString() ?? "all"));
            }
        }
        finally { (service as IDisposable)?.Dispose(); }
        foreach (var replacement in replacements)
            inputs[replacement.Index] = (inputs[replacement.Index].Name, replacement.Object);
        // Final images do not carry compiler IR or stale native integrity hashes.
        foreach (var input in inputs) input.Object.Sections.RemoveAll(section => section.Name == IrArchive.SectionName);
        return replacements.Count;
    }
}
