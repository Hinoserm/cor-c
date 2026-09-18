using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class InitializerScopeTests
{
    public static void Run(string work)
    {
        string a = Path.Combine(work, "InitA.cor"), b = Path.Combine(work, "InitB.cor"), constants = Path.Combine(work, "InitConstants.cor");
        string index = Path.Combine(work, "initializers.idx");
        File.WriteAllText(a, "using N = One.Numbers; public partial class SplitInit { public int A = N.Value; public static int StaticA { get; } = N.Value; }");
        File.WriteAllText(b, "using N = Two.Numbers; public partial class SplitInit { public int B = N.Value; public static int StaticB { get; } = N.Value; public int Sum() => A + B; }");
        File.WriteAllText(constants, "namespace One { public static class Numbers { public const int Value = 17; } } "
            + "namespace Two { public static class Numbers { public const int Value = 25; } }");
        SourceIndexBuilder.Write(index, new[] { a, b, constants }, "Initializers");
        foreach (string source in new[] { a, b })
        {
            using IndexedDeclarations declarations = new(index, "Initializers", new[] { source });
            var front = Frontend.Compile(new[] { source }, "initializer", true, declarations: declarations)
                ?? throw new Exception("Partial initializer scope failed");
            TypeDecl split = front.Unit.Types.Single(type => type.Name == "SplitInit");
            MethodDecl own = split.Members.OfType<MethodDecl>().Single(method => method.Name == "FieldInit$" + (source == a ? "A" : "B"));
            if (own.OwnedImplementation != true || own.Scope is null) throw new Exception("Initializer lost ownership or scope");
            MethodDecl property = split.Members.OfType<MethodDecl>().Single(method => method.Name == "FieldInit$Static" + (source == a ? "A" : "B"));
            if (property.OwnedImplementation != true || property.Scope is null) throw new Exception("Static auto-property initializer lost ownership or scope");
            string wanted = source == a ? "One.Numbers" : "Two.Numbers";
            string unrelated = source == a ? "Two.Numbers" : "One.Numbers";
            if (!front.Bound.Types.ContainsKey(wanted) || front.Bound.Types.ContainsKey(unrelated))
                throw new Exception("Initializer bound in another fragment's alias scope or loaded its body dependencies");
        }
        Console.WriteLine("initializer scopes: source aliases, owned helpers and lazy dependencies passed");
    }
}
