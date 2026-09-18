using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class ClosureOwnershipTests
{
    public static void Run(string work)
    {
        string a = Path.Combine(work, "FactoryA.cor"), b = Path.Combine(work, "FactoryB.cor"), mapper = Path.Combine(work, "Mapper.cor");
        string index = Path.Combine(work, "closures.idx");
        File.WriteAllText(mapper, "public delegate int Mapper(int value);");
        File.WriteAllText(a, "public static partial class Factory { public static Mapper First(int offset) => value => value + offset; }");
        File.WriteAllText(b, "public static partial class Factory { public static Mapper Second(int offset) => value => value - offset; }");
        SourceIndexBuilder.Write(index, new[] { a, b, mapper }, "Closures");
        HashSet<string> Read(string[] paths)
        {
            using IndexedDeclarations declarations = new(index, "Closures", paths);
            var front = Frontend.Compile(paths, "closures", true, declarations: declarations) ?? throw new Exception("Closure ownership binding failed");
            TypeSymbol[] closures = front.Bound.Types.Values.Where(type => type.Name.StartsWith("Lambda$", StringComparison.Ordinal)).Distinct().ToArray();
            if (closures.Any(type => type.Decl?.LocalOnly != true)) throw new Exception("Closure descriptor is not implementation-private");
            return closures.Select(type => type.Key).ToHashSet(StringComparer.Ordinal);
        }
        HashSet<string> first = Read(new[] { a }), second = Read(new[] { b }), both = Read(new[] { b, a });
        if (first.Count != 1 || second.Count != 1 || first.Overlaps(second) || !both.SetEquals(first.Concat(second)))
            throw new Exception("Closure identities depend on body enumeration or collide across partial members");
        Console.WriteLine("closure ownership: stable per-method identities and private descriptors passed");
    }
}
