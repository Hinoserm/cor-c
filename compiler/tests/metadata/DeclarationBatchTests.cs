using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class DeclarationBatchTests
{
    public static void Run(string work)
    {
        string owner = Path.Combine(work, "BatchOwner.cor"), definitions = Path.Combine(work, "BatchDefinitions.cor");
        string index = Path.Combine(work, "batch.idx");
        File.WriteAllText(owner, "public class BatchOwner { " + string.Join(" ", Enumerable.Range(0, 12)
            .Select(i => "public Dependency" + i + " Field" + i + ";")) + " }");
        File.WriteAllText(definitions, string.Join(" ", Enumerable.Range(0, 12)
            .Select(i => "public class Dependency" + i + " {}")));
        SourceIndexBuilder.Write(index, new[] { owner, definitions }, "Batch");
        using IndexedDeclarations declarations = new(index, "Batch", new[] { owner });
        var result = Frontend.Compile(new[] { owner }, "batch", true, declarations: declarations);
        if (result is null) throw new Exception("Batched signature discovery failed to bind");
        if (declarations.Passes > 4) throw new Exception("Signature discovery still restarts for each field: " + declarations.Passes);
        for (int i = 0; i < 12; i++)
            if (!result.Value.Bound.Types.ContainsKey("Dependency" + i)) throw new Exception("A batched dependency was omitted");
        string receipt = Path.Combine(work, "batch.deps");
        declarations.WriteDependencies(receipt);
        if (!UnitDependencies.IsCurrent(receipt, index)) throw new Exception("Batch did not preserve dependency receipts");
        Console.WriteLine("declaration batching: twelve independent signatures resolved in " + declarations.Passes + " passes");
    }
}
