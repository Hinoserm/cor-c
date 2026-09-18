using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Lower;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class StructuralOwnershipTests
{
    public static void Run(string work)
    {
        string source = Path.Combine(work, "TupleOwner.cor");
        File.WriteAllText(source, "class Program { public static int Main() { Program instance = new Program(); var pair = (17, 25); return pair.Item1 + pair.Item2; } }");
        string[] runtime = RuntimeDeclarations.Sources();
        var front = Frontend.Compile(new[] { source }.Concat(runtime).ToArray(), "tuple-owner", false,
            libraryPaths: runtime, elsewherePaths: runtime) ?? throw new Exception("Tuple owner binding failed");
        if (!front.Bound.Types.Values.Any(type => type.Structural && type.Name.StartsWith("ValueTuple$", StringComparison.Ordinal)))
            throw new Exception("Structural tuple identity lost");
        List<CompileError> errors = new();
        Module module = Lowering.Lower(front.Bound, front.Unit, "tuple-owner", false, errors, new());
        if (errors.Count != 0) throw new Exception(string.Join("; ", errors));
        DataItem[] descriptors = module.Data.Where(item => item.Name.StartsWith("t_ValueTuple", StringComparison.Ordinal)).ToArray();
        var certificates = DefinitionSemantics.Capture(module);
        if (descriptors.Length == 0 || descriptors.Any(descriptor => !descriptor.Coalescible || !certificates.ContainsKey(descriptor.Name)))
            throw new Exception("Structural descriptor lacks semantic certification");
        if (module.Data.Single(item => item.Name == "t_Program").Coalescible)
            throw new Exception("Ordinary user descriptor incorrectly made coalescible");
        Console.WriteLine("structural ownership: compiler tuple certified; ordinary strong descriptor preserved");
    }
}
