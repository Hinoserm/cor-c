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
        LifetimeFacts? facts = null)
    {
        _lifetimes = 0;
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
        IrArchive archive = IrArchive.Read(original) ?? throw new InvalidDataException("Backend input has no IR archive");
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
        var read = IrUnitCodec.ReadWithSettings(archive, memoryBudget: preLate ? PreLateDecodeLimit : 64L * 1024 * 1024,
            retained: retained, functionHeaders: preLate ? null : visibility);
        var unit = (read.Module, StackMaps: read.StackMaps, read.AccountedBytes);
        Console.Error.WriteLine("IR backend: retained functions=" + unit.Module.Functions.Count + ", data=" + unit.Module.Data.Count
            + ", accounted decode bytes=" + unit.AccountedBytes + (preLate ? ", late passes at link" : ""));
        Module module = unit.Module;
        module.PreserveExports = true;
        if (preLate)
        {
            // THE LINK'S OWN RUN OF THE LATE PASSES names field sites apart
            // from the compile's (Module.AtLink) and hands their records on
            // with the regenerated object. Named alike, they were symbols
            // nobody defined -- or the compile's sites of another field, with
            // that field's verdict: they matched only while the run here
            // repeated the compile's exactly, which nothing promises (the
            // calls a unit keeps, m.KeepCalls, are not archived).
            module.AtLink = true;
            // The whole program's answers the late passes read, for a closed image.
            if (facts?.ForeignCatchable is string[] catchable) module.ForeignCatchable = new(catchable, StringComparer.Ordinal);
            module.OwnedFields = facts?.OwnedFields;
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
        Function Load(int index)
        {
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
            Module local = new(module.Name) { Entry = function.Name, PreserveExports = true, NeedsHeap = module.NeedsHeap };
            local.Functions.Add(function);
            foreach (IrImport import in Selected(index))
            {
                Function body = IrFunctionCodec.Read(import.Body, new IrReadBudget(import.DecodeBytes));
                if (body.Name != import.Symbol) throw new InvalidDataException("Conflicting IR import identity");
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
            if (facts is not null)
            {
                // Kept as it was, to be taken back if a free the pass places
                // would run under a read of an owned field (RunAtLink's -1).
                byte[]? before = Escape.ReadsOwnedField(function, link!) ? IrFunctionCodec.Write(function) : null;
                int taken = Escape.RunAtLink(function, link!);
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
                int more = Escape.RunAtLink(function, link!);
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
            + (facts is null ? "" : ", lifetimes placed or freed=" + _lifetimes));
        if (errors.Count > 0) throw new InvalidDataException("IR backend: " + string.Join("; ", errors));
        foreach (Section section in original.Sections.Where(section => section.Name is TargetContract.SectionName or ManagedLayoutContract.SectionName
                       or ".corsac.tag" or RegistrySchema.SectionName or NativeLibraries.SectionName))
        {
            Section copy = new(section.Name, section.Kind) { Align = section.Align };
            copy.Bytes.AddRange(section.Bytes); copy.Relocs.AddRange(section.Relocs); result.Sections.Add(copy);
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
}
