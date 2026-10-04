using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Summary-only planning with bounded, selective cross-unit body imports.</summary>
public static class IrLinkOptimizer
{
    public static int Run(List<(string Name, ObjectFile Object)> inputs, Func<IUnitBackend> backend,
        bool enabled = true, int importBytes = 1024 * 1024, int bodyLimit = 32, string? closedImageEntry = null, bool parallelBackends = false,
        string? regionReport = null, bool madeOnly = false)
    {
        if (importBytes < 0 || bodyLimit < 0) throw new ArgumentOutOfRangeException(nameof(importBytes));
        TargetContract.Validate(inputs); ManagedLayoutContract.Validate(inputs);
        DefinitionCoalescer.Run(inputs, validateOnly: true);
        Dictionary<ObjectFile, IrArchive> archives = new();
        Dictionary<ObjectFile, LifetimeHints> hints = new();
        List<LifetimeHints> hintOrder = new();
        HashSet<ObjectFile> regionHints = new();
        Dictionary<string, ObjectFile> owners = new(StringComparer.Ordinal);
        // IN LINK ORDER, not by name: an object's name is a digest of its
        // source's full path, and the same tree checked out elsewhere was
        // ordered otherwise -- other owners, other symbol order, other bytes.
        // Every unit's hints read through one pool: what they state alike is
        // held once (LifetimeHintPool). The link never changes them.
        LifetimeHintPool pool = new();
        foreach (var input in inputs)
        {
            IrArchive? archive = IrArchive.Read(input.Object);
            if (archive is not null) archives.Add(input.Object, archive);
            // Hints without the IR they would recompile are nothing to act on.
            if (archive is not null && LifetimeHints.Read(input.Object, pool) is LifetimeHints unit)
            { hints.Add(input.Object, unit); hintOrder.Add(unit); }
            if (archive is not null && input.Object.Sections.Any(section => section.Name == RegionHints.SectionName)) regionHints.Add(input.Object);
            foreach (Symbol symbol in input.Object.Symbols.Where(symbol => symbol.Global && symbol.IsDefined))
                owners.TryAdd(symbol.Name, input.Object);
        }
        LinkTimings.Phase("archives and hints");
        // Every unit's lifetime summaries, solved together (LifetimeSolver).
        // Virtual calls, each by the overrides the whole image holds for it
        // (VirtualTargets), from the descriptors in the objects themselves.
        // What code outside the IR names: it may call any of it, with anything.
        SortedSet<string> foreign = new(StringComparer.Ordinal);
        foreach (var input in inputs)
            if (!archives.ContainsKey(input.Object))
                foreach (Section section in input.Object.Sections) foreach (Relocation reloc in section.Relocs) foreign.Add(reloc.Symbol);
        // ONLY THE TYPES THE IMAGE MAKES (VirtualTargets.Made): a virtual
        // call's targets on a type nothing stamps an object with run on no
        // object, and drop out of every answer the link gives by dispatch --
        // the lifetimes' merges, the owned fields' callers and borrowers,
        // the regions' calls and the judge's callers. Only where nothing
        // outside makes objects: a closed image (the caller says it is not
        // a shared object, links no shared library and exports nothing),
        // whose foreign code names what it calls ("*" for anything) -- and
        // not under --no-rta.
        VirtualTargets.Made? made = enabled && madeOnly && closedImageEntry is not null && !foreign.Contains("*")
            ? VirtualTargets.MadeIn(inputs, archives.Values) : null;
        VirtualTargets.Made? lifetimeMade = made?.Again();
        Dictionary<string, string[]> virtuals = enabled && hints.Count > 0
            ? VirtualTargets.Resolve(inputs, hintOrder.SelectMany(unit => unit.Named()).Select(named => named.Callee)
                .Concat(hintOrder.SelectMany(unit => unit.Owned?.VirtualNames() ?? Enumerable.Empty<string>()))
                .Where(name => name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal)), lifetimeMade)
            : new(StringComparer.Ordinal);
        if (regionReport is not null) Console.Error.WriteLine("lifetimes: rta " + (lifetimeMade is null ? "off" : lifetimeMade.Summary()));
        LifetimeSolver? lifetimes = enabled && hints.Count > 0 ? new LifetimeSolver(hintOrder, virtuals) : null;
        if (virtuals.Count > 0) Console.Error.WriteLine("LTO virtual calls resolved: " + virtuals.Count);
        LinkTimings.Phase("virtual targets and lifetime solve");
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
                Switches.AllocReport
                    ? line => { if (Switches.AllocReportOnly is not { } which || line.Contains(which, StringComparison.Ordinal)) Console.Error.WriteLine("alloc report: field " + line); } : null) : null;
        if (ownedFields is { IsEmpty: true }) ownedFields = null;
        if (ownedFields is not null)
            Console.Error.WriteLine("LTO owned fields: " + ownedFields.Fields.Count + " of "
                + hintOrder.SelectMany(unit => unit.Owned!.Fields.Keys).Distinct(StringComparer.Ordinal).Count()
                + ", elements owned through " + ownedFields.Elements.Count
                + (Switches.AllocReport ? ": " + string.Join(" ", ownedFields.Fields.Keys.Order(StringComparer.Ordinal)) : ""));
        LinkTimings.Phase("catches and owned fields");
        bool regionsPossible = lifetimes is not null && closedImageEntry is not null && archives.Keys.All(hints.ContainsKey)
            && archives.Keys.All(regionHints.Contains)
            && new[] { RuntimeAbi.RegionEnter, RuntimeAbi.RegionLeave, RuntimeAbi.AllocRegion, RuntimeAbi.RegionCatch }.All(owners.ContainsKey);
        // A closed image keeps only what is reached, and reaching is judged
        // on the IR as the units left it. Two kinds of call are made later:
        // those a regenerated unit gains when the lifetime rules run again
        // (Escape.RunAtLink) -- the runtime's frees, which every unit's hints
        // list -- and those field sites become, through symbols the link
        // defines at the end (DefineFieldSites). Their targets are roots.
        SortedSet<string> linkRoots = new(StringComparer.Ordinal);
        if (lifetimes is not null) foreach (LifetimeHints unit in hints.Values) linkRoots.UnionWith(unit.Helpers);
        if (hints.Values.Any(unit => unit.FieldSites.Count > 0)) { linkRoots.Add(LifetimeHints.FieldFreer); linkRoots.Add(LifetimeHints.FieldKeeper); }
        // And what regions call, wherever a regenerated unit may open one.
        // And the allocator a collection's elements are made beside it with,
        // where the link proved it owns them (Escape, elements through a field).
        if (regionsPossible) { linkRoots.Add(RuntimeAbi.RegionEnter); linkRoots.Add(RuntimeAbi.RegionLeave); linkRoots.Add(RuntimeAbi.AllocRegion); linkRoots.Add(RuntimeAbi.RegionCatch); linkRoots.Add(RuntimeAbi.AllocNear); }
        // And a loop's region at the top of every lap, where the runtime has one.
        bool loopRegionsPossible = regionsPossible && owners.ContainsKey(RuntimeAbi.RegionLoop);
        if (loopRegionsPossible) linkRoots.Add(RuntimeAbi.RegionLoop);
        // An iterator's or an async method's card mark is a call AsyncTransform
        // writes after the IR was archived: the archive never shows it, and a
        // closed image without lifetime hints dropped the helper and failed
        // to link ("undefined symbol m_Runtime_CardMarkObject").
        linkRoots.Add(RuntimeAbi.CardMarkObject);
        // And the barrier on a replaced object's value, which ScalarObjects
        // writes where the unit defines it: a regenerated unit's late passes
        // see the whole unit, what the image keeps of it or not (UnitBackend),
        // and the one holding the runtime called it unkept.
        linkRoots.Add(RuntimeAbi.WriteBarrierValues);
        Dictionary<ObjectFile, HashSet<string>>? reachability = enabled && closedImageEntry is not null
            ? IrReachability.Find(inputs, archives, owners, closedImageEntry, linkRoots) : null;
        LinkTimings.Phase("reachability");
        // REGIONS OVER EVERY UNIT (RegionSolver): the boundaries to open and
        // the allocation sites to make in the innermost open region, for a
        // closed image whose every unit with IR said what its functions do
        // with pointers, and whose runtime has regions. Every unit given an
        // answer is regenerated with it; giving up answers nothing.
        // Only what the image keeps is analysed: a function nothing reaches
        // calls nothing, and what it would hand its callees is nobody's.
        Dictionary<ObjectFile, RegionFacts>? regionFacts = null;
        if (regionsPossible)
        {
            // Read only now, and let go once solved: the backends to come,
            // in this process or beside it, want the memory.
            List<ObjectFile> regionOrder = inputs.Select(input => input.Object).Where(regionHints.Contains).ToList();
            // From the file where the link left them (ElfReader.LeftInFile).
            static RegionHints ReadRegions(ObjectFile obj)
            {
                Section[] sections = obj.Sections.Where(section => section.Name == RegionHints.SectionName).ToArray();
                if (sections.Length != 1 || sections[0].Size > RegionHints.MaximumBytes) throw new ElfFormatException("Invalid region hint section");
                return RegionHints.Read(sections[0].Content());
            }
            List<RegionHints> regionUnits = regionOrder.Select(ReadRegions).ToList();
            // Each symbol's address a constant or the unknown object, from every object's data.
            int constants = RegionConstants.Resolve(regionUnits, regionOrder, inputs.Select(input => input.Object));
            if (regionReport is not null) Console.Error.WriteLine("regions: " + constants + " symbol addresses constants");
            VirtualTargets.Made? regionMade = made?.Again();
            Dictionary<string, string[]> regionVirtuals = VirtualTargets.Resolve(inputs, RegionSolver.VirtualNames(regionUnits), regionMade);
            if (regionReport is not null)
            {
                Console.Error.WriteLine("regions: rta " + (regionMade is null ? "off" : regionMade.Summary()));
                // --region-report +rta: which types nothing makes a call reached.
                if (regionMade is not null && regionReport.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("+rta"))
                    foreach (string type in regionMade.Dropped.Union(lifetimeMade?.Dropped ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal))
                        Console.Error.WriteLine("regions: rta never made " + type);
            }
            RegionFacts?[]? solved = RegionSolver.Solve(regionUnits, regionVirtuals, (table, offset) => VirtualTargets.MethodAt(inputs, table, offset),
                closedImageEntry!, foreign, regionReport,
                (u, name) => reachability?.GetValueOrDefault(regionOrder[u]) is not { } kept || kept.Contains("F:" + name),
                (table, at, offset) => VirtualTargets.HoldsNoReference(inputs, table, at, offset), loopRegionsPossible,
                (table, type) => VirtualTargets.IsA(inputs, table, type));
            if (solved is not null)
            {
                regionFacts = new();
                for (int u = 0; u < regionOrder.Count; u++)
                    if (solved[u] is { IsEmpty: false } unitFacts) regionFacts[regionOrder[u]] = unitFacts;
            }
        }

        // STORAGE BESIDE ITS OWNER: where some boundary or loop opens a region,
        // what a collection grows into, stored into a field it frees itself as
        // it replaces it, is made beside the collection (AllocNear) -- in the
        // collection's region when that is the innermost one open. Nowhere a
        // region opens, it would only ever be the heap's, by a longer way.
        if (ownedFields is { SelfFreed.Count: > 0 } && regionFacts is { Count: > 0 } && owners.ContainsKey(RuntimeAbi.AllocNear))
            ownedFields.Beside = true;

        // WHAT THE SOLVE HELD, given back before the units are regenerated:
        // its graph over every unit -- most of a gigabyte for the compiler's
        // own build, given up on or not -- was garbage the collector had no
        // reason yet to look for, and the process kept its pages through
        // every unit's late passes.
        if (regionsPossible) GC.Collect(2, GCCollectionMode.Aggressive, true);
        LinkTimings.Phase("regions");
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
                    && (programFacts || ownedFields is not null || regionFacts is not null && regionFacts.ContainsKey(obj) || unitHints.Pending.Any(lifetimes.Holds));
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
        // build. The facts are the solver's, asked one plan at a time as a
        // worker takes it, and let go once its backend has them: asked for
        // every plan first, the whole program's answers were held unit by
        // unit through the whole regeneration. Each worker owns a backend
        // process and takes the next plan; the results go back in plan
        // order, so the image is the same however the work fell.
        object solving = new();
        LifetimeFacts? FactsFor(int k)
        {
            var plan = plans[k];
            ObjectFile original = inputs[plan.Index].Object;
            if (lifetimes is null || !hints.TryGetValue(original, out LifetimeHints? own)) return null;
            LifetimeFacts facts;
            // One question of the solver at a time: it is not asked from
            // several threads anywhere else.
            lock (solving)
                facts = lifetimes.For(own, archives[original].Entries.Values.SelectMany(record => record.Calls)
                    .Concat(plan.Imports.SelectMany(import => import.Body.Calls))
                    .Concat(own.Named().Select(named => named.Callee).Where(virtuals.ContainsKey)).Distinct(StringComparer.Ordinal));
            facts.ForeignCatchable = catchable; facts.OwnedFields = ownedFields;
            facts.Regions = regionFacts?.GetValueOrDefault(original);
            return facts;
        }
        LinkTimings.Phase("plans");
        // A BODY BROUGHT IN CARRIES WHAT ITS OWN UNIT WAS TOLD OF IT: the
        // sites chosen of it, which ride on every copy the inliner makes, so
        // a constructor's buffer inlined into a caller in another unit is
        // made in the region as the constructor's own would be. The choice
        // is the site's over every copy of its function that makes it (every
        // boundary that can be open above any of them outlives none of its
        // objects), and the caller's call is one of those, so it holds where
        // the inlined copy runs. And a boundary is never inlined: its region
        // is opened in its own unit (RegionPointsTo.Open); nor is a function
        // with a loop given a region, which is opened there too (OpenLoops).
        Dictionary<ObjectFile, RegionFacts>? siteFacts = regionFacts;
        IrImport Imported(string symbol, IrArchive archive, IrArchiveEntry body)
        {
            byte[] bytes = archive.ReadBody(body.Key);
            if (siteFacts is null || !owners.TryGetValue(symbol, out ObjectFile? owner) || !siteFacts.TryGetValue(owner, out RegionFacts? chosen))
                return new IrImport(symbol, bytes, body.DecodeBytes);
            int[] sites = chosen.Sites.GetViewBetween((symbol, int.MinValue), (symbol, int.MaxValue)).Select(site => site.Ordinal).ToArray();
            bool opens = chosen.Boundaries.Contains(symbol) || chosen.Loops.GetViewBetween((symbol, int.MinValue), (symbol, int.MaxValue)).Count > 0;
            return new IrImport(symbol, bytes, body.DecodeBytes, sites.Length == 0 ? null : sites, opens);
        }
        ObjectFile?[] regenerated = new ObjectFile?[plans.Count];
        string?[] reports = new string?[plans.Count];
        int next = -1;
        Exception? failure = null;
        // THE BIGGEST UNIT FIRST, by its IR records: taken in the order the
        // link was given them, the compiler's largest unit -- near ten
        // thousand functions -- could start late and finish alone while every
        // other backend sat idle. Each answer still goes in its own place.
        int[] order = Enumerable.Range(0, plans.Count)
            .OrderByDescending(k => archives[inputs[plans[k].Index].Object].Entries.Count).ThenBy(k => k).ToArray();
        void Work()
        {
            IUnitBackend? service = null;
            try
            {
                while (Volatile.Read(ref failure) is null)
                {
                    int taken = Interlocked.Increment(ref next);
                    if (taken >= plans.Count) break;
                    int k = order[taken];
                    var plan = plans[k];
                    long began = LinkTimings.Enabled ? Environment.TickCount64 : 0;
                    service ??= backend();
                    ObjectFile original = inputs[plan.Index].Object;
                    IrImport[] imports = plan.Imports.Select(import => Imported(import.Symbol, import.Archive, import.Body)).ToArray();
                    ObjectFile replacement = service.Recompile(original, imports, plan.Retained, FactsFor(k), archives[original]);
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
                        + imports.Sum(import => import.Body.Length) + " bytes; retained records=" + (plan.Retained?.Count.ToString() ?? "all")
                        + (LinkTimings.Enabled ? "; " + (Environment.TickCount64 - began) + "ms" : "");
                    // THE OBJECT IT REPLACES, let go now: its code, tables and
                    // notes were read for the analyses above and for nothing
                    // after, and kept to the end of the regeneration every
                    // unit was held twice over, as it came and as it went.
                    // What is still asked of it is its identity (owners, the
                    // region and hint maps) and its archive, which is read
                    // from its file.
                    if (!ReferenceEquals(replacement, original))
                    {
                        original.Sections.Clear(); original.Sections.TrimExcess();
                        original.Symbols.Clear(); original.Symbols.TrimExcess();
                    }
                }
            }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
            finally { (service as IDisposable)?.Dispose(); }
        }
        // Several only where each worker's backend is a process of its own:
        // a backend the caller handed in is one object, and not to be shared.
        // Half the processors, as far as there is memory for a gigabyte each
        // (no limit where the machine does not say what it has).
        long memory = MachineMemory.MachineAvailable();
        int byMemory = memory <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, Math.Max(1, memory >> 30));
        int workerCount = !parallelBackends ? 1 : Math.Max(1, Math.Min(plans.Count, Switches.LtoJobs > 0
            ? Switches.LtoJobs : Math.Max(1, Math.Min(Environment.ProcessorCount / 2, byMemory))));
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
        LinkTimings.Phase("units regenerated (" + plans.Count + ", " + workerCount + " backends)");
        foreach (var replacement in replacements)
            inputs[replacement.Index] = (inputs[replacement.Index].Name, replacement.Object);
        // Final images do not carry compiler IR or stale native integrity hashes.
        // The sites a regenerated unit's own late passes named, with the
        // compile's: each object's records define its symbols.
        List<LifetimeHints> siteOrder = new(hintOrder);
        foreach (var replacement in replacements)
            if (LifetimeHints.Read(replacement.Object) is LifetimeHints regeneratedSites) siteOrder.Add(regeneratedSites);
        (int sites, int sitesFreed) = DefineFieldSites(inputs, siteOrder, lifetimes);
        foreach (var input in inputs)
            input.Object.Sections.RemoveAll(section => section.Name == IrArchive.SectionName || section.Name == LifetimeHints.SectionName
                || section.Name == RegionHints.SectionName);
        LinkTimings.Phase("field sites");
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
                    if (Switches.TraceFieldSites)
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
