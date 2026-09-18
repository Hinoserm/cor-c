using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class ExtensionLookupTests
{
    public static void Run(string work)
    {
        string providers = Path.Combine(work, "Extensions.cor");
        string caller = Path.Combine(work, "ExtensionCaller.cor");
        string index = Path.Combine(work, "extensions.idx");
        File.WriteAllText(providers, "namespace A { public static class Extensions { public static int Twice(this int value) => value * 2; } } "
            + "namespace B { public static class Extensions { public static int Twice(this int value) => value * 3; } } "
            + "namespace Unused { public class Noise { public int Value; } }");
        SourceIndexBuilder.Write(index, new[] { providers }, "Extensions");
        File.WriteAllText(caller, "using A; class Caller { public static int Run() { int value = 2; return value.Twice(); } }");
        using (IndexedDeclarations declarations = new(index, "Extensions", new[] { caller }))
        {
            var front = Frontend.Compile(new[] { caller }, "extensions", true, declarations: declarations)
                ?? throw new Exception("Indexed extension was not discovered through its using namespace");
            if (!front.Bound.Types.ContainsKey("A.Extensions") || front.Bound.Types.ContainsKey("B.Extensions")
                || front.Bound.Types.ContainsKey("Unused.Noise"))
                throw new Exception("Extension discovery loaded unrelated namespaces");
            if (declarations.PayloadLoads != 1) throw new Exception("Extension discovery loaded unrelated declarations");
        }
        File.WriteAllText(caller, "class Caller { public static int Run() { int value = 2; return value.Twice(); } }");
        if (Frontend.Compile(new[] { providers, caller }, "inaccessible-extension", true) is not null)
            throw new Exception("An unimported namespace supplied an extension method");
        Console.WriteLine("extension lookup: scoped indexed discovery without loading unrelated namespaces passed");
    }
}
