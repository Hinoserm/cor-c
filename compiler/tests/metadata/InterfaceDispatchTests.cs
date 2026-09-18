using Corsac.Lang;

namespace Corsac.Tests.Metadata;

public static class InterfaceDispatchTests
{
    public static void Run(string work)
    {
        string source = Path.Combine(work, "Dispatch.cor");
        File.WriteAllText(source, "interface IFirst { int Read(int value); int Read(long value); } "
            + "interface ISecond { int Read(int value); } "
            + "class Reader : IFirst, ISecond { public int Read(int value) => value; public int Read(long value) => (int)value; }");
        var front = Frontend.Compile(new[] { source }, "dispatch", true) ?? throw new Exception("Interface dispatch binding failed");
        TypeSymbol reader = front.Bound.Types["Reader"];
        if (reader.InterfaceImplementations.Count != 3) throw new Exception("Interface slot aliases were lost");
        MethodSymbol integer = reader.Methods.Single(method => method.Name == "Read" && method.Params[0].Type.Prim == Prim.I32);
        MethodSymbol wide = reader.Methods.Single(method => method.Name == "Read" && method.Params[0].Type.Prim == Prim.I64);
        if (reader.InterfaceImplementations.Values.Count(method => ReferenceEquals(method, integer)) != 2
            || reader.InterfaceImplementations.Values.Count(method => ReferenceEquals(method, wide)) != 1)
            throw new Exception("Same-arity overloads were dispatched by argument count");
        Console.WriteLine("interface dispatch: typed overloads and multiple interface slots passed");
    }
}
