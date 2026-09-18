using Corsac.Lang.Metadata;

namespace Corsac.Tests.Projects;

public static class DependencyTests
{
    public static void Run(string work)
    {
        string source = Path.Combine(work, "Dependency.cs"), index = Path.Combine(work, "dependencies.idx"), state = Path.Combine(work, "unit.deps");
        string assembly = SourceIndexBuilder.AssemblyIdentity("Dependencies");
        File.WriteAllText(source, "class Known { public int Read() => 1; }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        string key = "T:" + assembly + "\nKnown", query = "B:" + assembly + "\nMissing";
        using (DeclarationCatalog catalog = new(index))
            UnitDependencies.Write(state, catalog, new[] { key }, new HashSet<string>(), new[] { query });
        if (!UnitDependencies.IsCurrent(state, index)) throw new Exception("Fresh unit dependency receipt is stale");
        File.WriteAllText(source, "class Known { public int Read() => 2; }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        if (!UnitDependencies.IsCurrent(state, index)) throw new Exception("Ordinary body edit invalidated its caller");
        File.WriteAllText(source, "class Known { public int Read() => 2; } class Missing { }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        if (UnitDependencies.IsCurrent(state, index)) throw new Exception("Previously missing lookup became visible without invalidation");
        Console.WriteLine("project dependencies: body-only reuse and negative-lookup invalidation passed");
    }
}
