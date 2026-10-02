using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Closed-image roots through IR code/data and all retained native-object relocations.</summary>
public static class IrReachability
{
    public static Dictionary<ObjectFile, HashSet<string>> Find(IReadOnlyList<(string Name, ObjectFile Object)> inputs,
        IReadOnlyDictionary<ObjectFile, IrArchive> archives, IReadOnlyDictionary<string, ObjectFile> owners, string entry,
        IEnumerable<string>? roots = null)
    {
        Dictionary<ObjectFile, HashSet<string>> retained = archives.Keys.ToDictionary(obj => obj, _ => new HashSet<string>(StringComparer.Ordinal));
        Dictionary<ObjectFile, HashSet<string>> locals = inputs.ToDictionary(input => input.Object,
            input => input.Object.Symbols.Where(symbol => symbol.IsDefined && !symbol.Global).Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal));
        Stack<(ObjectFile? Origin, string Symbol)> pending = new();
        pending.Push((null, entry));
        // Symbols the link itself will refer to (field sites' targets).
        foreach (string root in roots ?? Enumerable.Empty<string>()) pending.Push((null, root));
        // Without compiler IR, native code cannot be split safely. Keep all
        // of it, including every relocation and address-taken callback root.
        foreach (var input in inputs.Where(input => !archives.ContainsKey(input.Object)))
            foreach (Relocation relocation in input.Object.Sections.SelectMany(section => section.Relocs))
                pending.Push((input.Object, relocation.Symbol));
        while (pending.Count > 0)
        {
            var reference = pending.Pop();
            ObjectFile? owner = reference.Origin is not null && locals[reference.Origin].Contains(reference.Symbol)
                ? reference.Origin : owners.GetValueOrDefault(reference.Symbol);
            // A DEFINITION THE UNIT'S IR HOLDS AND ITS OBJECT DOES NOT: the
            // archive is the IR from before the late passes, and the compile's
            // late inliner may have taken a function whole into every caller
            // and dropped it -- a constant-specialised version of Unpack, once
            // a guard behind another for the same type had gone and left it
            // small (StaticInitGuards). The link runs those passes again over
            // the archived callers, which still call it, so the body is kept.
            if (owner is null && reference.Origin is not null && archives.TryGetValue(reference.Origin, out IrArchive? origin)
                && (origin.Entries.ContainsKey("F:" + reference.Symbol) || origin.Entries.ContainsKey("D:" + reference.Symbol)))
                owner = reference.Origin;
            if (owner is null || !archives.TryGetValue(owner, out IrArchive? archive)) continue;
            string key = "F:" + reference.Symbol;
            if (!archive.Entries.ContainsKey(key)) key = "D:" + reference.Symbol;
            if (!archive.Entries.TryGetValue(key, out IrArchiveEntry? definition) || !retained[owner].Add(key)) continue;
            foreach (string dependency in definition.References) pending.Push((owner, dependency));
        }
        return retained;
    }
}
