using Corsac.Lang;

namespace Corsac.Tests.Metadata;

/// <summary>
/// A static method naming an instance member of its own class is C#'s
/// CS0120, and must be reported as that. It used to bind to a read with no
/// receiver, and the optimiser then crashed on the undefined value with no
/// file or line (2026-09-23, the kernel's AIC-7xxx driver, twice).
/// </summary>
public static class StaticContextTests
{
    public static void Run(string work)
    {
        string[] runtime = RuntimeDeclarations.Sources();

        Rejected(work, runtime, "field", "class Holder { public string Who = \"x\"; static string Name() { return Who; } }");
        Rejected(work, runtime, "property", "class Holder { public string Who { get { return \"x\"; } } static string Name() { return Who; } }");
        Rejected(work, runtime, "method", "class Holder { int Count() { return 1; } static int Twice() { return Count() * 2; } }");

        // Still fine: statics from a static method, anything from an
        // instance one, a static overload beside an instance one, and a
        // lambda in a static method reading what it captured.
        Accepted(work, runtime, "static members", "class Holder { static int Seen; static int Count() { return Seen; } static int Twice() { return Count() * 2; } }");
        Accepted(work, runtime, "instance members", "class Holder { int Seen; int Count() { return Seen; } int Twice() { return Count() * 2 + Seen; } }");
        Accepted(work, runtime, "mixed overloads", "class Holder { int Pick(int a) { return a; } static int Pick(string s) { return 1; } static int Use() { return Pick(\"s\"); } }");
        Accepted(work, runtime, "lambda capture", "using System; class Holder { static int Use() { int k = 2; Func<int> f = () => k + 1; return f(); } }");

        Console.WriteLine("static context: instance members refused from static methods (CS0120), everything else unchanged");
    }

    static (bool compiled, string errors) Compile(string work, string[] runtime, string name, string source)
    {
        string path = Path.Combine(work, "StaticContext" + name.Replace(" ", "") + ".cor");
        File.WriteAllText(path, source);
        TextWriter saved = Console.Error;
        StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            var front = Frontend.Compile(new[] { path }.Concat(runtime).ToArray(), "staticcontext", true,
                libraryPaths: runtime, elsewherePaths: runtime);
            return (front is not null, captured.ToString());
        }
        finally { Console.SetError(saved); }
    }

    static void Rejected(string work, string[] runtime, string what, string source)
    {
        (bool compiled, string errors) = Compile(work, runtime, what, source);
        if (compiled || !errors.Contains("An object reference is required for the non-static field, method, or property"))
            throw new Exception("static context: an instance " + what + " from a static method was not refused as CS0120: " + errors);
    }

    static void Accepted(string work, string[] runtime, string what, string source)
    {
        (bool compiled, string errors) = Compile(work, runtime, what, source);
        if (!compiled) throw new Exception("static context: " + what + " was refused: " + errors);
    }
}
