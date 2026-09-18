using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class InterfacePlanTests
{
    public static void Run(string work)
    {
        string source = Path.Combine(work, "Interfaces.cs"), path = Path.Combine(work, "interfaces.idx");
        const string used = "namespace A { public interface IUsed { int Get(); } }";
        const string other = "namespace B { public interface IOther { int First(); int Second(); } }";
        const string owner = "class Owner : A.IUsed { public virtual int Get() => 1; public virtual int Local() => 2; }";
        File.WriteAllText(source, used + other);
        SourceIndexBuilder.Write(path, new[] { source }, "Plan");
        using DeclarationCatalog catalog = new(path);
        var families = catalog.Interfaces("Plan");
        Require(families[("A.IUsed", 0)] == 1 && families[("B.IOther", 0)] == 2, "interface counts");
        Require(catalog.PayloadLoads == 0, "interface planning loaded type declarations");
        BindResult first = Binder.Bind(Parser.ParseText(used + owner), indexedInterfaces: families);
        BindResult second = Binder.Bind(Parser.ParseText(other + used + owner), indexedInterfaces: families);
        Require(first.Errors.Count == 0 && second.Errors.Count == 0, "interface layout binding");
        int firstSlot = first.Types["Owner"].FindMethods("Local").Single().VtableSlot;
        int secondSlot = second.Types["Owner"].FindMethods("Local").Single().VtableSlot;
        Require(firstSlot == secondSlot && firstSlot >= 7, "subset-dependent virtual slots");
        Console.WriteLine("  interface reservations: declaration-free planning and subset-independent slots passed");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }
}
