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
        File.WriteAllText(source, "class Program { public static int Sum((int, int) pair) => pair.Item1 + pair.Item2; public static int Main() => 42; }");
        var front = Frontend.Compile(new[] { source }, "tuple-owner", false) ?? throw new Exception("Tuple owner binding failed");
        TypeSymbol tuple = front.Bound.Types.Values.Single(type => type.Name.StartsWith("ValueTuple$", StringComparison.Ordinal));
        if (!tuple.Structural) throw new Exception("Structural tuple identity lost");
        List<CompileError> errors = new();
        Module module = Lowering.Lower(front.Bound, front.Unit, "tuple-owner", false, errors, new());
        if (errors.Count != 0) throw new Exception(string.Join("; ", errors));
        DataItem descriptor = module.Data.Single(item => item.Name.StartsWith("t_ValueTuple", StringComparison.Ordinal));
        if (!descriptor.Coalescible || !DefinitionSemantics.Capture(module).ContainsKey(descriptor.Name))
            throw new Exception("Structural descriptor lacks semantic certification");
        if (module.Data.Single(item => item.Name == "t_Program").Coalescible)
            throw new Exception("Ordinary user descriptor incorrectly made coalescible");
        Console.WriteLine("structural ownership: compiler tuple certified; ordinary strong descriptor preserved");
    }
}
