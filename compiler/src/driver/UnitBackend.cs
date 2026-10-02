using Corsac.Lang;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using Corsac.Lang.Metadata;
using Corsac.Lang.Opt;
using Corsac.Lang.X86;

namespace Corsac;

/// <summary>IR-only compiler backend. Does not parse or bind source files.</summary>
public sealed class UnitBackend : IUnitBackend
{
    /// <summary>Objects the last recompile placed in frames or freed with the link's lifetime answers.</summary>
    public int LifetimesTaken => _lifetimes;
    private int _lifetimes;

    /// <summary>The most a unit whose late passes run at the link may take decoded.</summary>
    private const long PreLateDecodeLimit = 512L * 1024 * 1024;

    public ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports, IReadOnlySet<string>? retained = null,
        LifetimeFacts? facts = null, IrArchive? archive = null)
    {
        _lifetimes = 0;
        // Where the unit's time went, for corc link --timings (LinkTimings).
        long started = Environment.TickCount64, decoded = started, lateDone = started;
        Escape.LinkFacts? link = facts is null ? null : new(facts);
        // The unit says which machine it was compiled for (its ABI note):
        // every pass below reads the word size from Target.Current.
        bool longMode = TargetContract.IsLongMode(original);
        Target.Current = longMode ? Target.X86_64 : Target.X86;
        // Each invocation must restore its own permissions; a previous unit may
        // have selected a newer CPU or explicitly disabled an extension.
        X86CodeGenerationContract cpu = X86CodeGenerationContract.Read(original)
            ?? throw new InvalidDataException("IR unit has no CPU/FPU contract; rebuild the unit before LTO");
        if (!longMode)
        {
            Target.X86.X86Profile = X86Cpu.Parse(cpu.Arguments());
            Target.X86.Cpu = Target.X86.X86Profile.Name;
        }
        // The link's own reading, where it handed one in: checked against
        // this object's native content then, and read again it was every
        // unit's whole object written out and hashed a second time.
        archive ??= IrArchive.Read(original) ?? throw new InvalidDataException("Backend input has no IR archive");
        var visibility = original.Symbols.Where(symbol => symbol.IsDefined && symbol.IsFunction)
            .ToDictionary(symbol => symbol.Name, symbol => symbol.Global, StringComparer.Ordinal);
        // A VERSION 2 ARCHIVE holds the IR from before the late passes, and
        // they run here, over the whole program's answers where the link has
        // them. What they leave is what a version 1 archive held, and the
        // per-function steps below go on from there as before.
        bool preLate = IrUnitCodec.ReadSettings(archive).Settings is { PreLate: true };
        // The late passes need the unit whole, so its decode is bounded by a
        // fixed limit, the same on every machine: sized by what the machine
        // has free (a quarter of what the process's 768 MB heap leaves), a
        // unit holding the runtime and the standard library, 220 MB decoded,
        // could not be linked anywhere.
        // THE LATE PASSES SEE THE UNIT WHOLE, what a closed image's link does
        // not keep of it too: a generic copy every unit compiles (HashSet's
        // Add, Stack's Push) is kept only in the unit that defines it first,
        // and pruned before them here it was a call to nothing the unit knew
        // -- not inlined, and every parameter escaping. An iterator's set
        // handed to Add was the collector's in the compiler's own build,
        // freed when it was linked open (1080). What the image does not keep
        // goes after them (PruneAfterLate).
        var read = IrUnitCodec.ReadWithSettings(archive, memoryBudget: preLate ? PreLateDecodeLimit : 64L * 1024 * 1024,
            retained: preLate ? null : retained, functionHeaders: preLate ? null : visibility);
        var unit = (read.Module, StackMaps: read.StackMaps, read.AccountedBytes);
        Console.Error.WriteLine("IR backend: retained functions=" + unit.Module.Functions.Count + ", data=" + unit.Module.Data.Count
            + ", accounted decode bytes=" + unit.AccountedBytes + (preLate ? ", late passes at link" : ""));
        Module module = unit.Module;
        module.PreserveExports = true;
        decoded = lateDone = Environment.TickCount64;
        if (preLate)
        {
            // THE LINK'S OWN RUN OF THE LATE PASSES names field sites apart
            // from the compile's (Module.AtLink) and hands their records on
            // with the regenerated object. Named alike, they were symbols
            // nobody defined -- or the compile's sites of another field, with
            // that field's verdict: they matched only while the run here
            // repeated the compile's exactly, which nothing promises.
            // The calls the compile kept from the inliner (m.KeepCalls) come
            // with the IR (IrFunctionCodec), so the run here inlines as the
            // compile's did, and an owned-elements candidate's calls are
            // still calls when Escape judges it.
            module.AtLink = true;
            // The whole program's answers the late passes read, for a closed image.
            if (facts?.ForeignCatchable is string[] catchable) module.ForeignCatchable = new(catchable, StringComparer.Ordinal);
            module.OwnedFields = facts?.OwnedFields;
            // The regions the whole program found: sites and loops marked on
            // this IR, which the compile numbered the same way, before any
            // pass moves them.
            if (facts?.Regions is { IsEmpty: false } regions)
            {
                module.RegionFacts = regions;
                RegionPointsTo.MarkSites(module, regions);
                RegionPointsTo.MarkLoops(module, regions);
            }
            module.LinkEscapes = facts?.Escapes;
            foreach (Function function in module.Functions)
                if (visibility.TryGetValue(function.Name, out bool exported) && exported != function.Exported)
                    throw new InvalidDataException("Archived IR identity disagrees with native symbol " + function.Name);
            Pipeline late = Pipeline.Default(optimizeSize: read.Settings!.OptimizeSize, experimentalBatch: read.Settings.ExperimentalBatch);
            late.Workers = Math.Max(1, Math.Min(64, Environment.ProcessorCount));
            late.RunLate(module);
            AsyncTransform.Run(module, Target.Current.WordSize);
            LandingPadHomes.Run(module);
            // Only a collector reads stack maps, and a unit the late passes
            // found needs no heap carries none, as its compile would have.
            unit.StackMaps = unit.StackMaps && module.NeedsHeap;
            if (retained is not null) PruneAfterLate(module, archive, retained);
            lateDone = Environment.TickCount64;
        }
        HashSet<string> originalNames = module.Functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, byte[]> semantics = CoalescingContract.Read(original);
        foreach (IrImport import in imports)
            if (!originalNames.Add(import.Symbol) || import.DecodeBytes < import.Body.Length)
                throw new InvalidDataException("Conflicting or unbounded IR import identity");
        Dictionary<string, IrImport> available = imports.ToDictionary(import => import.Symbol, StringComparer.Ordinal);
        // A function the late passes made has no record of its own: it calls
        // nothing imported and costs what a small function does.
        IrImport[] Selected(int index) => archive.Entries.TryGetValue("F:" + module.Functions[index].Name, out IrArchiveEntry? record)
            ? record.Calls.Where(available.ContainsKey).Select(name => available[name]).ToArray() : Array.Empty<IrImport>();
        long Cost(int index) => checked(3 * ((archive.Entries.TryGetValue("F:" + module.Functions[index].Name, out IrArchiveEntry? record) ? record.DecodeBytes : 0)
            + Selected(index).Sum(import => import.DecodeBytes)) + 512 * 1024);
        Dictionary<string, DataItem>? items = null;
        Function Load(int index)
        {
            Devirtualize devirtualize = new();
            Function header = module.Functions[index];
            Function function;
            if (preLate) function = header;
            else
            {
                IrArchiveEntry entry = archive.Entries["F:" + header.Name];
                function = IrFunctionCodec.Read(archive.ReadBody(entry.Key), new IrReadBudget(entry.DecodeBytes));
            }
            if (function.Name != header.Name || function.Exported != header.Exported)
                throw new InvalidDataException("Deferred IR identity disagrees with native symbol");
            // CORC_DUMP_FUNCTION=<symbol>: that function's IR as the link loads
            // it and as it goes to the backend, on standard error.
            bool dumping = Environment.GetEnvironmentVariable("CORC_DUMP_FUNCTION") == function.Name;
            if (dumping) { System.Text.StringBuilder loaded = new(); function.Dump(loaded); Console.Error.WriteLine("== loaded\n" + loaded); }
            // Unhomed for the rules below; homed again last (LandingPadHomes.Strip).
            LandingPadHomes.Strip(function);
            Module local = new(module.Name) { Entry = function.Name, PreserveExports = true, NeedsHeap = module.NeedsHeap };
            local.Functions.Add(function);
            // Another unit's sites the link chose come marked on its body, and
            // the inliner carries the mark to every copy; a boundary of its
            // own unit stays a call, and opens its region there.
            bool importedSites = false;
            foreach (IrImport import in Selected(index))
            {
                Function body = IrFunctionCodec.Read(import.Body, new IrReadBudget(import.DecodeBytes));
                if (body.Name != import.Symbol) throw new InvalidDataException("Conflicting IR import identity");
                if (import.RegionBoundary) body.NoInlining = true;
                // A body that reads a field elements are owned through comes
                // as its unit archived it, before the reads were kept alive
                // past every use of what they answered: it is called, in its
                // own unit, where they are (Escape, elements through a field).
                if (facts?.OwnedFields is { Elements.Count: > 0 } owned && ReadsElementField(body, owned)) body.NoInlining = true;
                if (import.RegionSites is { } sites && RegionPointsTo.MarkSites(body, sites) > 0) importedSites = true;
                local.Functions.Add(body);
            }
            Pipeline cleanup = new() { Rounds = 3, Workers = 1 };
            cleanup.Passes.Add(new ConstantFold { AcrossFunction = true }); cleanup.Passes.Add(new ConstantAndCopyPropagation());
            cleanup.Passes.Add(new DeadCodeElimination()); cleanup.Passes.Add(new BranchSimplify());
            // With the link's lifetime answers the allocator calls stay calls
            // through the first round, so the lifetime rules can tell them
            // (Escape.RunAtLink, one function at a time, as it is loaded);
            // the second round then folds them, and the frees just added, in
            // as a unit compile does.
            // And the functions the whole program found fresh: inlined first,
            // their results would be branches and no longer calls the rules
            // can recognise.
            string[] allocators = facts is null ? Array.Empty<string>()
                : facts.Fresh.Append(Escape.Allocator).Append(Escape.LeafAllocator).Append(Escape.ObjectAllocator).Order(StringComparer.Ordinal).ToArray();
            new Inline { SmallBody = 40, GrowthLimit = 1024, ConstantBranchBody = 160, FreshOwnerBody = 0, Keep = allocators }.Run(local);
            cleanup.Run(local);
            // WHAT THE LINK'S INLINING PUT IN SIGHT, made direct: a consumer
            // imported from the library (ToList, a List built from a
            // sequence) now walks an iterator this function made, whose
            // type its descriptor says -- the unit's own, or another unit's
            // as this one knew it (ShadowData).
            items ??= Devirtualize.ReadOnlyItems(module);
            for (int round = 0; round < 2; round++)
            {
                devirtualize.Run(function, items);
                cleanup.Run(local);
            }
            if (facts is not null)
            {
                // Kept as it was, to be taken back if a free the pass places
                // would run under a read of an owned field (RunAtLink's -1).
                byte[]? before = Escape.ReadsOwnedField(function, link!) ? IrFunctionCodec.Write(function) : null;
                int taken = Escape.RunAtLink(function, link!, items);
                if (taken < 0)
                {
                    function = IrFunctionCodec.Read(before!, new IrReadBudget(64L * 1024 * 1024));
                    local.Functions[0] = function;
                    taken = 0;
                }
                Interlocked.Add(ref _lifetimes, taken);
                new Inline { SmallBody = 40, GrowthLimit = 1024, ConstantBranchBody = 160, FreshOwnerBody = 0 }.Run(local);
                cleanup.Run(local);
                // AND AGAIN OVER WHAT THAT INLINED: an imported body is the IR
                // its unit archived before its own lifetime pass, so a block
                // it makes and drops -- FromInt's scratch digits -- came in
                // with no free. The objects owned above are frees already,
                // which this run takes for escapes and leaves alone.
                byte[]? again = Escape.ReadsOwnedField(function, link!) ? IrFunctionCodec.Write(function) : null;
                int more = Escape.RunAtLink(function, link!, items);
                if (more < 0)
                {
                    function = IrFunctionCodec.Read(again!, new IrReadBudget(64L * 1024 * 1024));
                    local.Functions[0] = function;
                    more = 0;
                }
                if (more > 0)
                {
                    Interlocked.Add(ref _lifetimes, more);
                    new Inline { SmallBody = 40, GrowthLimit = 1024, ConstantBranchBody = 160, FreshOwnerBody = 0 }.Run(local);
                    cleanup.Run(local);
                }
            }
            // THE LINK'S REGION SITES, what is left of them now its lifetime
            // rules are done (RegionPointsTo.ApplyFacts): an object they placed
            // in the frame or freed where it dies was never the region's.
            // And those of the bodies brought in that it inlined.
            if (module.RegionFacts is not null || importedSites) RegionPointsTo.MakeSitesInRegion(function);
            // Written out last here too: the link's lifetime pass saw them as
            // notes to the collector (CardMarks).
            new CardMarks().Run(local);
            local.Functions.RemoveAll(body => !ReferenceEquals(body, function));
            LandingPadHomes.Run(local);
            if (dumping) { System.Text.StringBuilder regenerated = new(); function.Dump(regenerated); Console.Error.WriteLine("== regenerated\n" + regenerated); }
            return function;
        }
        int workers = Math.Max(1, Math.Min(64, Environment.ProcessorCount));
        // As much as the machine can spare (MachineMemory), less what the
        // unit's own headers took; a function bigger than that is compiled
        // alone. The same object either way.
        long budget = Math.Max(1, MachineMemory.WorkBudget(8L * 1024 * 1024, 512L * 1024 * 1024) - unit.AccountedBytes);
        List<string> errors = new();
        ObjectFile result;
        int peakFunctions;
        long peakBytes;
        if (longMode)
        {
            Corsac.Lang.X64.X64Backend backend = new()
            {
                StackMaps = unit.StackMaps, EmitLinkSummary = true, Workers = workers,
                FunctionLoader = Load, FunctionLoadBytes = Cost, FunctionMemoryBudget = budget,
            };
            result = backend.Generate(module, errors);
            (peakFunctions, peakBytes) = (backend.PeakBatchFunctions, backend.PeakBatchBytes);
        }
        else
        {
            X86Backend backend = new()
            {
                AutomaticPacked = cpu.AutomaticPacked,
                StackMaps = unit.StackMaps, EmitLinkSummary = true, Workers = workers,
                FunctionLoader = Load, FunctionLoadBytes = Cost, FunctionMemoryBudget = budget,
            };
            result = backend.Generate(module, errors);
            (peakFunctions, peakBytes) = (backend.PeakBatchFunctions, backend.PeakBatchBytes);
        }
        Console.Error.WriteLine("IR backend: peak batch functions=" + peakFunctions
            + ", accounted working allowance=" + peakBytes
            + (facts is null ? "" : ", lifetimes placed or freed=" + _lifetimes)
            + (LinkTimings.Enabled ? "; decode " + (decoded - started) + "ms, late passes " + (lateDone - decoded)
                + "ms, functions and code " + (Environment.TickCount64 - lateDone) + "ms" : ""));
        if (errors.Count > 0) throw new InvalidDataException("IR backend: " + string.Join("; ", errors));
        foreach (Section section in original.Sections.Where(section => section.Name is TargetContract.SectionName or ManagedLayoutContract.SectionName
                       or ".corsac.tag" or RegistrySchema.SectionName or NativeLibraries.SectionName))
        {
            Section copy = new(section.Name, section.Kind) { Align = section.Align };
            // A layout a link left in its file (ElfReader.LeftInFile) stays
            // there: the same bytes, read when they are asked for.
            if (section.FileBacked is { } backed) copy.FileBacked = backed;
            else copy.Bytes.AddRange(section.Bytes);
            copy.Relocs.AddRange(section.Relocs); result.Sections.Add(copy);
        }
        DefinitionSemantics.Attach(result, semantics);
        if (preLate && module.LifetimeHints is { FieldSites.Count: > 0 } named)
        {
            LifetimeHints sites = new();
            sites.FieldSites.AddRange(named.FieldSites);
            sites.Attach(result);
        }
        return result;
    }

    /// <summary>Whether a body touches, other than by storing into it, a field the link proved elements are owned through.</summary>
    private static bool ReadsElementField(Function body, OwnedFieldFacts owned)
    {
        foreach (Corsac.Lang.Ir.Block b in body.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Field is { } field && i.Op != Opcode.Store && owned.Elements.ContainsKey(field)) return true;
        return false;
    }

    /// <summary>
    /// WHAT A CLOSED IMAGE DOES NOT KEEP, taken out once the late passes have
    /// read it: every record the link's reachability left out, as the decode
    /// left it out before. What the passes made has no record and stays; so
    /// does a definition of the unit's own that what stays now names -- a
    /// body inlined from a copy the image keeps elsewhere names the unit's
    /// locals, which nothing reached in the IR the link judged. An exported
    /// one is the copy another unit keeps.
    /// </summary>
    private static void PruneAfterLate(Module module, IrArchive archive, IReadOnlySet<string> retained)
    {
        bool Kept(string key) => !archive.Entries.ContainsKey(key) || retained.Contains(key);
        Dictionary<string, Function> functions = new(StringComparer.Ordinal);
        foreach (Function function in module.Functions) functions[function.Name] = function;
        Dictionary<string, DataItem> data = new(StringComparer.Ordinal);
        foreach (DataItem item in module.Data) data[item.Name] = item;
        HashSet<string> keep = new(StringComparer.Ordinal);
        Stack<string> pending = new();
        foreach (Function function in module.Functions) if (Kept("F:" + function.Name)) { keep.Add("F:" + function.Name); pending.Push("F:" + function.Name); }
        foreach (DataItem item in module.Data) if (Kept("D:" + item.Name)) { keep.Add("D:" + item.Name); pending.Push("D:" + item.Name); }
        void Name(string name)
        {
            if (functions.TryGetValue(name, out Function? function) && !function.Exported && keep.Add("F:" + name)) pending.Push("F:" + name);
            else if (data.TryGetValue(name, out DataItem? item) && !item.Exported && keep.Add("D:" + name)) pending.Push("D:" + name);
        }
        while (pending.Count > 0)
        {
            string key = pending.Pop();
            // What the archive counts as a reference (IrUnitCodec.Snapshot):
            // calls, and every symbol an operand names; a datum's relocations.
            if (key.StartsWith("F:", StringComparison.Ordinal))
            {
                foreach (Corsac.Lang.Ir.Block block in functions[key[2..]].Blocks)
                    foreach (Instr instruction in block.Instrs)
                    {
                        if (instruction.Op == Opcode.Call && instruction.Callee is not null) Name(instruction.Callee);
                        foreach (Operand operand in instruction.Operands) if (operand is SymOperand address) Name(address.Name);
                    }
            }
            else foreach (DataReloc relocation in data[key[2..]].Relocs) Name(relocation.Symbol);
        }
        module.Functions.RemoveAll(function => !keep.Contains("F:" + function.Name));
        module.Data.RemoveAll(item => !keep.Contains("D:" + item.Name));
    }
}
