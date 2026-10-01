using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Summary-only planning with bounded, selective cross-unit body imports.</summary>
public static class IrLinkOptimizer
{
    public static int Run(List<(string Name, ObjectFile Object)> inputs, Func<IUnitBackend> backend,
        bool enabled = true, int importBytes = 1024 * 1024, int bodyLimit = 32, string? closedImageEntry = null, bool parallelBackends = false)
    {
        if (importBytes < 0 || bodyLimit < 0) throw new ArgumentOutOfRangeException(nameof(importBytes));
        TargetContract.Validate(inputs); ManagedLayoutContract.Validate(inputs);
        DefinitionCoalescer.Run(inputs, validateOnly: true);
        Dictionary<ObjectFile, IrArchive> archives = new();
        Dictionary<ObjectFile, LifetimeHints> hints = new();
        List<LifetimeHints> hintOrder = new();
        Dictionary<string, ObjectFile> owners = new(StringComparer.Ordinal);
        // IN LINK ORDER, not by name: an object's name is a digest of its
        // source's full path, and the same tree checked out elsewhere was
        // ordered otherwise -- other owners, other symbol order, other bytes.
        foreach (var input in inputs)
        {
            IrArchive? archive = IrArchive.Read(input.Object);
            if (archive is not null) archives.Add(input.Object, archive);
            // Hints without the IR they would recompile are nothing to act on.
            if (archive is not null && LifetimeHints.Read(input.Object) is LifetimeHints unit)
            { hints.Add(input.Object, unit); hintOrder.Add(unit); }
            foreach (Symbol symbol in input.Object.Symbols.Where(symbol => symbol.Global && symbol.IsDefined))
                owners.TryAdd(symbol.Name, input.Object);
        }
        // Every unit's lifetime summaries, solved together (LifetimeSolver).
        // Virtual calls, each by the overrides the whole image holds for it
        // (VirtualTargets), from the descriptors in the objects themselves.
        Dictionary<string, string[]> virtuals = enabled && hints.Count > 0
            ? VirtualTargets.Resolve(inputs, hintOrder.SelectMany(unit => unit.Named()).Select(named => named.Callee)
                .Concat(hintOrder.SelectMany(unit => unit.Owned?.VirtualNames() ?? Enumerable.Empty<string>()))
                .Where(name => name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)))
            : new(StringComparer.Ordinal);
        LifetimeSolver? lifetimes = enabled && hints.Count > 0 ? new LifetimeSolver(hintOrder, virtuals) : null;
        if (virtuals.Count > 0) Console.Error.WriteLine("LTO virtual calls resolved: " + virtuals.Count);
        // THE WHOLE PROGRAM'S ANSWERS, for a closed image only -- a library's
        // consumers could throw anything -- and only where every unit with
        // IR said what it throws.
        string[]? catchable = lifetimes is not null && closedImageEntry is not null && archives.Keys.All(hints.ContainsKey)
            ? ForeignCatchable(inputs, hintOrder, lifetimes) : null;
        bool programFacts = catchable is not null;
        if (programFacts) Console.Error.WriteLine("LTO catches: " + (catchable!.Length == 0 ? "every catch frees what it caught" : catchable.Length + " types keep what they catch"));
        // FIELDS THAT OWN WHAT THEY HOLD, judged over every unit's hints as a
        // flat compile judges them over its module (OwnedFieldSolver); every
        // regenerated unit frees what a store replaces and gives the types it
        // defines their owned-field maps. A body that stores into one may be
        // imported into another unit like any other: the importing unit is
        // regenerated with the same answer, and a store frees what it
        // replaces only in an object the function made and keeps to itself
        // (Escape.PrivateOwner), which is what inlining a constructor or a
        // setter into the function that made the object gives it -- a
        // Holder's Grow freeing the buffer it replaces. Every unit with IR must have said, as for catches; and every unit
        // with hints is then regenerated, so none keeps a store that does
        // not free what it replaces, or a type without its map.
        OwnedFieldFacts? ownedFields = lifetimes is not null && closedImageEntry is not null && archives.Keys.All(hints.ContainsKey)
            ? OwnedFieldSolver.Solve(hintOrder, lifetimes, virtuals, closedImageEntry,
                Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 0 } which
                    ? line => { if (which.Length == 1 || line.Contains(which, StringComparison.Ordinal)) Console.Error.WriteLine("alloc report: field " + line); } : null) : null;
        if (ownedFields is { Fields.Count: 0 }) ownedFields = null;
        if (ownedFields is not null)
            Console.Error.WriteLine("LTO owned fields: " + ownedFields.Fields.Count + " of "
                + hintOrder.SelectMany(unit => unit.Owned!.Fields.Keys).Distinct(StringComparer.Ordinal).Count()
                + (Environment.GetEnvironmentVariable("CORSAC_ALLOC_REPORT") is { Length: > 0 } ? ": " + string.Join(" ", ownedFields.Fields.Keys.Order(StringComparer.Ordinal)) : ""));
        // A closed image keeps only what is reached, and reaching is judged
        // on the IR as the units left it. Two kinds of call are made later:
        // those a regenerated unit gains when the lifetime rules run again
        // (Escape.RunAtLink) -- the runtime's frees, which every unit's hints
        // list -- and those field sites become, through symbols the link
        // defines at the end (DefineFieldSites). Their targets are roots.
        SortedSet<string> linkRoots = new(StringComparer.Ordinal);
        if (lifetimes is not null) foreach (LifetimeHints unit in hints.Values) linkRoots.UnionWith(unit.Helpers);
        if (hints.Values.Any(unit => unit.FieldSites.Count > 0)) { linkRoots.Add(LifetimeHints.FieldFreer); linkRoots.Add(LifetimeHints.FieldKeeper); }
        Dictionary<ObjectFile, HashSet<string>>? reachability = enabled && closedImageEntry is not null
            ? IrReachability.Find(inputs, archives, owners, closedImageEntry, linkRoots) : null;
        int lifetimeUnits = 0;
        List<(int Index, List<(string Symbol, IrArchive Archive, IrArchiveEntry Body)> Imports, HashSet<string>? Retained)> plans = new();
        if (enabled)
            for (int index = 0; index < inputs.Count; index++)
            {
                ObjectFile obj = inputs[index].Object;
                if (!archives.TryGetValue(obj, out IrArchive? archive)) continue;
                HashSet<string>? retained = reachability?.GetValueOrDefault(obj);
                bool prune = retained is not null && archive.Entries.Keys.Count(key => key != "M:unit") != retained.Count;
                HashSet<string> defined = obj.Symbols.Where(symbol => symbol.IsDefined).Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
                // A unit with an object it left to the collector only for want
                // of what another unit does, which the whole program now says.
                // Every unit of a closed image the whole program has answers for
                // gains by them (its catches), whatever its pending conditions.
                bool gains = lifetimes is not null && hints.TryGetValue(obj, out LifetimeHints? unitHints)
                    && (programFacts || ownedFields is not null || unitHints.Pending.Any(lifetimes.Holds));
                if (gains) lifetimeUnits++;
                List<(string Symbol, IrArchive Archive, IrArchiveEntry Body)> imports = new(); int used = 0;
                // The runtime's frees are calls such a unit is about to make.
                IEnumerable<string> helpers = gains ? hints[obj].Helpers : Enumerable.Empty<string>();
                foreach (string call in archive.Entries.Values.Where(record => retained is null || retained.Contains(record.Key))
                    .SelectMany(record => record.Calls).Concat(helpers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    if (defined.Contains(call) || !owners.TryGetValue(call, out ObjectFile? owner) || !archives.TryGetValue(owner, out IrArchive? provider)
                        || !provider.Entries.TryGetValue("F:" + call, out IrArchiveEntry? body) || !body.Importable
                        || body.Instructions > 160 || body.Length > importBytes - used || imports.Count >= bodyLimit) continue;
                    imports.Add((call, provider, body)); used += body.Length;
                }
                if (imports.Count > 0 || prune || gains) plans.Add((index, imports, prune ? retained : null));
            }
        // EVERY UNIT REGENERATED BY A BACKEND OF ITS OWN, several at once:
        // one process taking 383 units in turn was half of the compiler's own
        // build. The facts are the solver's, asked here one plan at a time;
        // each worker owns a backend process and takes the next plan; the
        // results go back in plan order, so the image is the same however
        // the work fell.
        LifetimeFacts?[] planFacts = new LifetimeFacts?[plans.Count];
        for (int k = 0; k < plans.Count; k++)
        {
            var plan = plans[k];
            ObjectFile original = inputs[plan.Index].Object;
            LifetimeFacts? facts = lifetimes is not null && hints.TryGetValue(original, out LifetimeHints? own)
                ? lifetimes.For(own, archives[original].Entries.Values.SelectMany(record => record.Calls)
                    .Concat(plan.Imports.SelectMany(import => import.Body.Calls))
                    .Concat(own.Named().Select(named => named.Callee).Where(virtuals.ContainsKey)).Distinct(StringComparer.Ordinal))
                : null;
            if (facts is not null) { facts.ForeignCatchable = catchable; facts.OwnedFields = ownedFields; }
            planFacts[k] = facts;
        }
        ObjectFile?[] regenerated = new ObjectFile?[plans.Count];
        string?[] reports = new string?[plans.Count];
        int next = -1;
        Exception? failure = null;
        void Work()
        {
            IUnitBackend? service = null;
            try
            {
                while (Volatile.Read(ref failure) is null)
                {
                    int k = Interlocked.Increment(ref next);
                    if (k >= plans.Count) break;
                    var plan = plans[k];
                    service ??= backend();
                    ObjectFile original = inputs[plan.Index].Object;
                    IrImport[] imports = plan.Imports.Select(import => new IrImport(import.Symbol, import.Archive.ReadBody(import.Body.Key), import.Body.DecodeBytes)).ToArray();
                    ObjectFile replacement = service.Recompile(original, imports, plan.Retained, planFacts[k]);
                    X86CodeGenerationContract.ValidateRegeneration(original, replacement);
                    TargetContract.Validate(new[] { ("original", original), ("regenerated", replacement) });
                    ManagedLayoutContract.Validate(new[] { ("original", original), ("regenerated", replacement) });
                    IrArchive originalArchive = archives[original];
                    bool Kept(Symbol symbol) => plan.Retained is null || plan.Retained.Contains("F:" + symbol.Name) || plan.Retained.Contains("D:" + symbol.Name)
                        || !originalArchive.Entries.ContainsKey("F:" + symbol.Name) && !originalArchive.Entries.ContainsKey("D:" + symbol.Name);
                    string[] before = original.Symbols.Where(symbol => symbol.Global && symbol.IsDefined && Kept(symbol)).Select(symbol => symbol.Name).Order(StringComparer.Ordinal).ToArray();
                    string[] after = replacement.Symbols.Where(symbol => symbol.Global && symbol.IsDefined).Select(symbol => symbol.Name).Order(StringComparer.Ordinal).ToArray();
                    if (!before.SequenceEqual(after)) throw new ElfFormatException("IR backend changed the unit's exported definitions");
                    regenerated[k] = replacement;
                    reports[k] = "LTO IR: " + inputs[plan.Index].Name + ": imported " + imports.Length + " bodies, "
                        + imports.Sum(import => import.Body.Length) + " bytes; retained records=" + (plan.Retained?.Count.ToString() ?? "all");
                }
            }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
            finally { (service as IDisposable)?.Dispose(); }
        }
        // Several only where each worker's backend is a process of its own:
        // a backend the caller handed in is one object, and not to be shared.
        int workerCount = !parallelBackends ? 1 : Math.Max(1, Math.Min(plans.Count, int.TryParse(Environment.GetEnvironmentVariable("CORC_LTO_JOBS"), out int asked) && asked > 0
            ? asked : Math.Min(12, Math.Max(1, Environment.ProcessorCount / 2))));
        if (workerCount == 1) Work();
        else
        {
            Thread[] threads = new Thread[workerCount];
            for (int w = 0; w < workerCount; w++)
            {
                threads[w] = new Thread(Work, 16 * 1024 * 1024) { IsBackground = true, Name = "lto-" + w };
                threads[w].Start();
            }
            foreach (Thread thread in threads) thread.Join();
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        List<(int Index, ObjectFile Object)> replacements = new();
        for (int k = 0; k < plans.Count; k++)
        {
            replacements.Add((plans[k].Index, regenerated[k]!));
            Console.Error.WriteLine(reports[k]);
        }
        foreach (var replacement in replacements)
            inputs[replacement.Index] = (inputs[replacement.Index].Name, replacement.Object);
        // Final images do not carry compiler IR or stale native integrity hashes.
        (int sites, int sitesFreed) = DefineFieldSites(inputs, hintOrder, lifetimes);
        foreach (var input in inputs)
            input.Object.Sections.RemoveAll(section => section.Name == IrArchive.SectionName || section.Name == LifetimeHints.SectionName);
        if (lifetimes is not null || sites > 0)
            Console.Error.WriteLine("LTO lifetimes: units with hints=" + hints.Count + ", units gaining=" + lifetimeUnits
                + ", field sites=" + sites + " freed=" + sitesFreed);
        return replacements.Count;
    }

    /// <summary>
    /// WHAT A CATCH MAY BE HANDED THAT WAS NOT JUST MADE, over every unit
    /// (Escape.ForeignThrows, as a flat compile judges it): a throw of
    /// something unnamed makes it anything; one of another function's result,
    /// nothing if the whole program finds that function fresh; one read from
    /// a static, whatever any unit stores there. Every such type and its
    /// ancestors, or null when it could be anything.
    /// </summary>
    private static string[]? ForeignCatchable(List<(string Name, ObjectFile Object)> inputs, List<LifetimeHints> units, LifetimeSolver solver)
    {
        Dictionary<string, List<string>> stored = new(StringComparer.Ordinal);
        foreach (LifetimeHints unit in units)
            foreach ((string field, string type) in unit.StaticStores)
            {
                if (!stored.TryGetValue(field, out List<string>? list)) stored[field] = list = new();
                list.Add(type);
            }
        SortedSet<string> types = new(StringComparer.Ordinal);
        foreach (LifetimeHints unit in units)
            foreach (string thrown in unit.Throws)
            {
                if (thrown.StartsWith("f:", StringComparison.Ordinal) && solver.IsFresh(thrown[2..])) continue;
                if (!thrown.StartsWith("s:", StringComparison.Ordinal)) return null;
                foreach (string type in stored.GetValueOrDefault(thrown[2..]) ?? new List<string>())
                {
                    if (type == "*") return null;
                    types.Add(type);
                }
            }
        return VirtualTargets.Ancestry(inputs, types).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// FIELD SITES: a unit that owns an object whose fields another unit
    /// fills cannot tell whether they hold only objects made for them, so it
    /// frees each such field through a symbol of its own (LifetimeHints.
    /// FieldSites). Every such symbol is defined here, whatever else the link
    /// does: as Runtime.FreeField where the whole program leaves the field
    /// clean, and as Runtime.KeepField, which does nothing, everywhere else --
    /// all of them, with the link-time optimizer off. Defined in the object
    /// that defines the routine, after any unit has been regenerated.
    /// </summary>
    // IN INPUT ORDER (hintOrder), never a dictionary's: keyed by object, its
    // order was the objects' identity hashes, the symbols it adds landed in a
    // different order in each link, and no two images were the same bytes.
    private static (int Sites, int Freed) DefineFieldSites(List<(string Name, ObjectFile Object)> inputs,
        List<LifetimeHints> hints, LifetimeSolver? solver)
    {
        if (hints.All(unit => unit.FieldSites.Count == 0)) return (0, 0);
        (ObjectFile Object, Symbol Symbol)? Find(string name)
        {
            foreach (var input in inputs)
                foreach (Symbol symbol in input.Object.Symbols)
                    if (symbol.Name == name && symbol.IsDefined && symbol.Global) return (input.Object, symbol);
            return null;
        }
        var freer = Find(LifetimeHints.FieldFreer);
        var keeper = Find(LifetimeHints.FieldKeeper)
            ?? throw new ElfFormatException("Field sites need Runtime.KeepField, which no object defines");
        // ONE DEFINITION A SITE, however many units list it. A generic copy
        // -- an iterator of a generic method, say -- is compiled by every unit
        // that uses it, the link keeps one, and each unit names its sites
        // alike; defined once for each, the routine's object held the name
        // twice. Freed only when every unit that lists the site finds the
        // field clean: the one copy kept must be right for all of them.
        Dictionary<string, bool> verdicts = new(StringComparer.Ordinal);
        List<string> order = new();
        foreach (LifetimeHints unit in hints)
            foreach ((LifetimeFields fields, List<(string Symbol, long Offset)> list) in unit.FieldSites)
            {
                SolvedFields? solved = solver?.Solve(fields);
                foreach ((string name, long offset) in list)
                {
                    bool clean = freer is not null && solved is { Opaque: false }
                        && solved.Fresh.Contains(offset) && !solved.Dirty.Contains(offset);
                    if (Environment.GetEnvironmentVariable("CORC_TRACE_FIELD_SITES") is not null)
                        Console.Error.WriteLine("field site " + name + " offset " + offset + (clean ? " FREED" : " kept")
                            + " solved fresh=[" + string.Join(",", solved?.Fresh ?? Array.Empty<long>()) + "] dirty=["
                            + string.Join(",", solved?.Dirty ?? Array.Empty<long>()) + "] opaque=" + (solved?.Opaque ?? true)
                            + " merges=" + string.Join(" ", fields.Merges.Take(8).Select(m => m.Callee + ":" + m.Argument)));
                    if (verdicts.TryGetValue(name, out bool was)) verdicts[name] = was && clean;
                    else { verdicts[name] = clean; order.Add(name); }
                }
            }
        int sites = 0, freed = 0;
        foreach (string name in order)
        {
            bool clean = verdicts[name];
            (ObjectFile owner, Symbol target) = clean ? freer!.Value : keeper;
            owner.Symbols.Add(new Symbol { Name = name, Section = target.Section, Offset = target.Offset,
                Size = target.Size, IsFunction = true, Global = true });
            sites++;
            if (clean) freed++;
        }
        return (sites, freed);
    }
}
