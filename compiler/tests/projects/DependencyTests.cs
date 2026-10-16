using Corsac.Lang.Metadata;

namespace Corsac.Tests.Projects;

public static class DependencyTests
{
    public static void Run(string work)
    {
        string source = Path.Combine(work, "Dependency.cs"), index = Path.Combine(work, "dependencies.idx"), state = Path.Combine(work, "unit.deps");
        string bodies = Path.Combine(work, "bodies.deps");
        string assembly = SourceIndexBuilder.AssemblyIdentity("Dependencies");
        File.WriteAllText(source, "class Known { public int Read() => 1; }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        string key = "T:" + assembly + "\nKnown", query = "B:" + assembly + "\nMissing";
        using (DeclarationCatalog catalog = new(index))
        {
            UnitDependencies.Write(state, catalog, new[] { key }, new HashSet<string>(), new[] { query });
            UnitDependencies.Write(bodies, catalog, new[] { key }, new HashSet<string> { key }, Array.Empty<string>());
        }
        if (!UnitDependencies.IsCurrent(state, index)) throw new Exception("Fresh unit dependency receipt is stale");
        if (UnitDependencies.Stale(bodies, index) is { } fresh) throw new Exception("Fresh receipt of bodies is stale: " + fresh);
        File.WriteAllText(source, "class Known { public int Read() => 2; }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        if (!UnitDependencies.IsCurrent(state, index)) throw new Exception("Ordinary body edit invalidated its caller");
        Expect(bodies, index, "text of Known's file changed (Dependency.cs)");
        File.WriteAllText(source, "class Known { public int Read(int twice) => 2; }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        Expect(state, index, "declaration of Known changed");
        Expect(bodies, index, "declaration of Known changed");
        File.WriteAllText(source, "class Known { public int Read() => 2; } class Missing { }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        Expect(state, index, "lookup B:Missing answers differently");
        File.WriteAllText(source, "class Other { }");
        SourceIndexBuilder.Write(index, new[] { source }, assembly);
        Expect(state, index, "Known is no longer declared");
        Expect(Path.Combine(work, "absent.deps"), index, "no receipt");
        File.WriteAllBytes(state, File.ReadAllBytes(state)[..20]);
        if (UnitDependencies.Stale(state, index) is not { } cut || !cut.StartsWith("receipt unreadable", StringComparison.Ordinal))
            throw new Exception("A receipt cut short was not called unreadable");
        Console.WriteLine("project dependencies: body-only reuse, negative-lookup invalidation and stale reasons passed");
    }

    private static void Expect(string receipt, string index, string reason)
    {
        string? said = UnitDependencies.Stale(receipt, index);
        if (said != reason) throw new Exception("Stale receipt said '" + said + "', not '" + reason + "'");
    }
}
