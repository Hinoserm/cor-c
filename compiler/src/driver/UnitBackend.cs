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
    public ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports, IReadOnlySet<string>? retained = null)
    {
        Target.Current = Target.X86;
        // Each invocation must restore its own permissions; a previous unit may
        // have selected a newer CPU or explicitly disabled an extension.
        X86CodeGenerationContract cpu = X86CodeGenerationContract.Read(original)
            ?? throw new InvalidDataException("IR unit has no CPU/FPU contract; rebuild the unit before LTO");
        Target.X86.X86Profile = X86Cpu.Parse(cpu.Arguments());
        Target.X86.Cpu = Target.X86.X86Profile.Name;
        IrArchive archive = IrArchive.Read(original) ?? throw new InvalidDataException("Backend input has no IR archive");
        var visibility = original.Symbols.Where(symbol => symbol.IsDefined && symbol.IsFunction)
            .ToDictionary(symbol => symbol.Name, symbol => symbol.Global, StringComparer.Ordinal);
        var unit = IrUnitCodec.Read(archive, retained: retained, functionHeaders: visibility);
        Console.Error.WriteLine("IR backend: retained functions=" + unit.Module.Functions.Count + ", data=" + unit.Module.Data.Count
            + ", accounted decode bytes=" + unit.AccountedBytes);
        Module module = unit.Module;
        module.PreserveExports = true;
        HashSet<string> originalNames = module.Functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, byte[]> semantics = CoalescingContract.Read(original);
        foreach (IrImport import in imports)
            if (!originalNames.Add(import.Symbol) || import.DecodeBytes < import.Body.Length)
                throw new InvalidDataException("Conflicting or unbounded IR import identity");
        Dictionary<string, IrImport> available = imports.ToDictionary(import => import.Symbol, StringComparer.Ordinal);
        IrImport[] Selected(int index) => archive.Entries["F:" + module.Functions[index].Name].Calls
            .Where(available.ContainsKey).Select(name => available[name]).ToArray();
        long Cost(int index) => checked(3 * (archive.Entries["F:" + module.Functions[index].Name].DecodeBytes
            + Selected(index).Sum(import => import.DecodeBytes)) + 512 * 1024);
        Function Load(int index)
        {
            Function header = module.Functions[index];
            IrArchiveEntry entry = archive.Entries["F:" + header.Name];
            Function function = IrFunctionCodec.Read(archive.ReadBody(entry.Key), new IrReadBudget(entry.DecodeBytes));
            if (function.Name != header.Name || function.Exported != header.Exported)
                throw new InvalidDataException("Deferred IR identity disagrees with native symbol");
            Module local = new(module.Name) { Entry = function.Name, PreserveExports = true, NeedsHeap = module.NeedsHeap };
            local.Functions.Add(function);
            foreach (IrImport import in Selected(index))
            {
                Function body = IrFunctionCodec.Read(import.Body, new IrReadBudget(import.DecodeBytes));
                if (body.Name != import.Symbol) throw new InvalidDataException("Conflicting IR import identity");
                local.Functions.Add(body);
            }
            new Inline { SmallBody = 40, GrowthLimit = 1024, ConstantBranchBody = 160, FreshOwnerBody = 0 }.Run(local);
            local.Functions.RemoveAll(body => !ReferenceEquals(body, function));
            Pipeline cleanup = new() { Rounds = 3, Workers = 1 };
            cleanup.Passes.Add(new ConstantFold()); cleanup.Passes.Add(new ConstantAndCopyPropagation());
            cleanup.Passes.Add(new DeadCodeElimination()); cleanup.Passes.Add(new BranchSimplify());
            cleanup.Run(local);
            LandingPadHomes.Run(local);
            return function;
        }
        X86Backend backend = new()
        {
            AutomaticPacked = cpu.AutomaticPacked,
            StackMaps = unit.StackMaps, EmitLinkSummary = true, Workers = Math.Max(1, Math.Min(64, Environment.ProcessorCount)),
            FunctionLoader = Load, FunctionLoadBytes = Cost, FunctionMemoryBudget = 64L * 1024 * 1024 - unit.AccountedBytes,
        };
        List<string> errors = new();
        ObjectFile result = backend.Generate(module, errors);
        Console.Error.WriteLine("IR backend: peak batch functions=" + backend.PeakBatchFunctions
            + ", accounted working allowance=" + backend.PeakBatchBytes);
        if (errors.Count > 0) throw new InvalidDataException("IR backend: " + string.Join("; ", errors));
        foreach (Section section in original.Sections.Where(section => section.Name is TargetContract.SectionName or ManagedLayoutContract.SectionName
                       or ".corsac.tag" or RegistrySchema.SectionName))
        {
            Section copy = new(section.Name, section.Kind) { Align = section.Align };
            copy.Bytes.AddRange(section.Bytes); copy.Relocs.AddRange(section.Relocs); result.Sections.Add(copy);
        }
        DefinitionSemantics.Attach(result, semantics);
        return result;
    }
}
