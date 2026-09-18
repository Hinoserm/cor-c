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
        IrArchive archive = IrArchive.Read(original) ?? throw new InvalidDataException("Backend input has no IR archive");
        var unit = IrUnitCodec.Read(archive, retained: retained);
        Module module = unit.Module;
        module.PreserveExports = true;
        HashSet<string> originalNames = module.Functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, byte[]> semantics = CoalescingContract.Read(original);
        foreach (IrImport import in imports)
        {
            Function function = IrFunctionCodec.Read(import.Body);
            if (function.Name != import.Symbol || !originalNames.Add(function.Name)) throw new InvalidDataException("Conflicting IR import identity");
            module.Functions.Add(function);
        }
        new Inline { SmallBody = 40, GrowthLimit = 1024, ConstantBranchBody = 160, FreshOwnerBody = 0 }.Run(module);
        HashSet<string> imported = imports.Select(import => import.Symbol).ToHashSet(StringComparer.Ordinal);
        module.Functions.RemoveAll(function => imported.Contains(function.Name));
        // Keep this backend stage deliberately bounded: inlining and cleanup,
        // not a second whole-program escape/specialization pipeline.
        Pipeline cleanup = new() { Rounds = 3, Workers = Math.Max(1, Math.Min(64, Environment.ProcessorCount)) };
        cleanup.Passes.Add(new ConstantFold()); cleanup.Passes.Add(new ConstantAndCopyPropagation());
        cleanup.Passes.Add(new DeadCodeElimination()); cleanup.Passes.Add(new BranchSimplify());
        cleanup.Run(module);
        LandingPadHomes.Run(module);
        X86Backend backend = new() { StackMaps = unit.StackMaps, EmitLinkSummary = true, Workers = cleanup.Workers };
        List<string> errors = new();
        ObjectFile result = backend.Generate(module, errors);
        if (errors.Count > 0) throw new InvalidDataException("IR backend: " + string.Join("; ", errors));
        foreach (Section section in original.Sections.Where(section => section.Name is TargetContract.SectionName or ManagedLayoutContract.SectionName or ".corsac.tag"))
        {
            Section copy = new(section.Name, section.Kind) { Align = section.Align };
            copy.Bytes.AddRange(section.Bytes); copy.Relocs.AddRange(section.Relocs); result.Sections.Add(copy);
        }
        DefinitionSemantics.Attach(result, semantics);
        return result;
    }
}
