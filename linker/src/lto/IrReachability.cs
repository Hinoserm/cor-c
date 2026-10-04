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
        // Each unit's own copies of what another unit keeps, already looked
        // through (below).
        Dictionary<ObjectFile, HashSet<string>> copies = archives.Keys.ToDictionary(obj => obj, _ => new HashSet<string>(StringComparer.Ordinal));
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
            // THE UNIT'S OWN COPY OF WHAT ANOTHER UNIT KEEPS. A generic copy
            // every unit compiles (Dictionary's ContainsKey at __canon) is
            // kept only where it is defined first, and the unit's own copy is
            // left out -- but the late passes run again at the link over the
            // unit whole (UnitBackend), and may inline that copy into a
            // caller that stays. What it calls is then called from here, and
            // the owner's copy need not call it at all: the units are
            // optimised apart, and a body the owner inlined at its compile is
            // still a call in another's. Dictionary's Same was kept nowhere,
            // its only callers were copies left out, and the kernel failed to
            // link with three units calling it. So what the unit's own copy
            // names is reached as well, and kept where it is owned; the copy
            // itself is not kept (PruneAfterLate keeps the owner's).
            if (reference.Origin is not null && owner != reference.Origin
                && archives.TryGetValue(reference.Origin, out IrArchive? own) && copies[reference.Origin].Add(reference.Symbol)
                && (own.Entries.TryGetValue("F:" + reference.Symbol, out IrArchiveEntry? copy)
                    || own.Entries.TryGetValue("D:" + reference.Symbol, out copy)))
                foreach (string dependency in copy.References) pending.Push((reference.Origin, dependency));
            if (owner is null || !archives.TryGetValue(owner, out IrArchive? archive)) continue;
            string key = "F:" + reference.Symbol;
            if (!archive.Entries.ContainsKey(key)) key = "D:" + reference.Symbol;
            if (!archive.Entries.TryGetValue(key, out IrArchiveEntry? definition) || !retained[owner].Add(key)) continue;
            foreach (string dependency in definition.References) pending.Push((owner, dependency));
        }
        return retained;
    }
}
