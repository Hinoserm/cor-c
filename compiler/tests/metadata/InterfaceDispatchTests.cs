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
        File.WriteAllText(source, "interface I { int Read(); } "
            + "class Base : I { public virtual int Read() => 1; } "
            + "class Hidden : Base { public int Read() => 2; } "
            + "class Overridden : Base { public override int Read() => 3; } "
            + "class Remapped : Base, I { public int Read() => 4; }");
        front = Frontend.Compile(new[] { source }, "inheritance", true)
            ?? throw new Exception("Inherited interface binding failed");
        foreach (var pair in new[] { ("Hidden", "Base"), ("Overridden", "Overridden"), ("Remapped", "Remapped") })
            if (front.Bound.Types[pair.Item1].InterfaceImplementations.Values.Single().Owner.Name != pair.Item2)
                throw new Exception("Incorrect inherited interface mapping for " + pair.Item1);
        foreach (string implementation in new[] { "public long Read() => 1;", "public static int Read() => 1;" })
        {
            File.WriteAllText(source, "interface I { int Read(); } class Bad : I { " + implementation + " }");
            if (Frontend.Compile(new[] { source }, "invalid-contract", true) is not null)
                throw new Exception("Invalid interface implementation accepted: " + implementation);
        }
        File.WriteAllText(source, "interface I { void Read(ref int value); } "
            + "class Bad : I { public void Read(int value) { } }");
        if (Frontend.Compile(new[] { source }, "invalid-ref", true) is not null)
            throw new Exception("By-value method implemented a ref contract");
        Console.WriteLine("interface contracts: return/ref rejection, inherited mapping, override and reimplementation passed");
    }
}
