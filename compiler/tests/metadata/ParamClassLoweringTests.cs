using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Lower;

namespace Corsac.Tests.Metadata;

/// <summary>
/// The class a parameter is declared as, kept for the passes that narrow a
/// call on it (Function.ParamClasses), only where every object it can hold
/// is of that class or derives from it: a class of the program's is kept;
/// object and System.Array are not -- every array is an Array, and no
/// array's descriptor names Array among its ancestors, so a call through an
/// Array parameter narrowed to Array's subclasses left every array out.
/// </summary>
public static class ParamClassLoweringTests
{
    public static void Run(string work)
    {
        string source = Path.Combine(work, "ParamClassLowering.cor");
        File.WriteAllText(source, "using System; using System.Collections; "
            + "class Shape { public virtual int Sides() => 0; } "
            + "class Program { "
            + "static int OfArray(Array a) => ((ICollection)a).Count; "
            + "static int OfObject(object o) => o.GetHashCode(); "
            + "static int OfShape(Shape s) => s.Sides(); "
            + "public static int Main() => OfArray(new int[3]) + OfObject(1) + OfShape(new Shape()); }");
        string[] runtime = RuntimeDeclarations.Sources();
        var front = Frontend.Compile(new[] { source }.Concat(runtime).ToArray(), "param-class-lowering", false,
            libraryPaths: runtime, elsewherePaths: runtime) ?? throw new Exception("Parameter class binding failed");
        List<CompileError> errors = new();
        Module module = Lowering.Lower(front.Bound, front.Unit, "param-class-lowering", false, errors, new());
        if (errors.Count != 0) throw new Exception(string.Join("; ", errors));

        Function Named(string display) => module.Functions.SingleOrDefault(f => f.Display is string d && d.StartsWith(display, StringComparison.Ordinal))
            ?? throw new Exception("No function " + display + " was made");
        bool Classed(Function f) => f.ParamClasses is { Count: > 0 };
        if (!Classed(Named("Program.OfShape(")))
            throw new Exception("A parameter declared as a class of the program keeps no class");
        if (Classed(Named("Program.OfArray(")))
            throw new Exception("A parameter declared System.Array keeps Array as its class, which no array names");
        if (Classed(Named("Program.OfObject(")))
            throw new Exception("A parameter declared object keeps a class");
        Console.WriteLine("parameter classes: a program's class kept, object and System.Array refused");
    }
}
