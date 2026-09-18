using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class PartialOwnershipTests
{
    public static void Run(string work)
    {
        string first = Path.Combine(work, "PartialA.cor"), second = Path.Combine(work, "PartialB.cor");
        string index = Path.Combine(work, "partial.idx");
        File.WriteAllText(first, "public static partial class Split { public static int Value; public static int First() => Second(); }");
        File.WriteAllText(second, "public static partial class Split { public static int Second() => Value; public static int Property => Value; }");
        SourceIndexBuilder.Write(index, new[] { second, first }, "PartialTest");
        foreach (string source in new[] { first, second })
        {
            using IndexedDeclarations declarations = new(index, "PartialTest", new[] { source });
            var front = Frontend.Compile(new[] { source }, "partial", true, declarations: declarations)
                ?? throw new Exception("Partial ownership did not bind");
            TypeDecl split = front.Unit.Types.Single(type => type.Name == "Split");
            if (split.SourcePath != first || split.Elsewhere != (source == second)) throw new Exception("Unstable partial metadata owner");
            MethodDecl a = split.Members.OfType<MethodDecl>().Single(method => method.Name == "First");
            MethodDecl b = split.Members.OfType<MethodDecl>().Single(method => method.Name == "Second");
            if (a.OwnedImplementation != (source == first) || b.OwnedImplementation != (source == second))
                throw new Exception("Partial method ownership was lost");
            var getter = front.Bound.Methods.Values.Single(method => method.Owner.Name == "Split" && method.Name == "get_Property");
            if (getter.Decl?.OwnedImplementation != (source == second)) throw new Exception("Accessor ownership was lost");
            if (declarations.PayloadLoads != 2) throw new Exception("Each of the two partial declaration records must load once; got " + declarations.PayloadLoads);
        }
        Console.WriteLine("partial ownership: canonical metadata, source bodies and accessors passed");
    }
}
