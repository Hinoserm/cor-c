using Corsac.Lang;

namespace Corsac.Tests.Metadata;

/// <summary>
/// A shared generic copy only named in a body (`o is P&lt;string&gt;`) is
/// declared when lowering asks -- except one whose declaration could be
/// refused, which is declared while binding can still say so
/// (Binder.MayCollide). Each shape here makes two methods alike under its
/// arguments: the copy is refused with a message, never a compiler fault.
/// C# itself accepts these (TODO: methods made alike by substitution); what
/// is checked is that the refusal is a message and not an exception.
/// </summary>
public static class NamedCopyTests
{
    public static void Run(string work)
    {
        string[] runtime = RuntimeDeclarations.Sources();
        Refused(work, runtime, "parameter", "class P<T> { public int M(T a) => 1; public int M(string a) => 2; } "
            + "static class Program { static void Main() { object o = new object(); System.Console.WriteLine(o is P<string>); } }");
        Refused(work, runtime, "argument", "using System.Collections.Generic; class P<T> { public int M(List<T> a) => 1; public int M(List<string> a) => 2; } "
            + "static class Program { static void Main() { object o = new object(); System.Console.WriteLine(o is P<string>); } }");
        Refused(work, runtime, "constraint", "class Shape {} class Circle : Shape {} class Bag<T> where T : Shape { public int Add(T x) => 1; public int Add(Circle c) => 2; } "
            + "static class Program { static void Main() { object o = new object(); System.Console.WriteLine(o is Bag<Circle>); } }");
        Console.WriteLine("named copies: one two methods of which its arguments make alike refused with a message, as declared while binding");
    }

    static void Refused(string work, string[] runtime, string name, string source)
    {
        string path = Path.Combine(work, "NamedCopy" + name + ".cor");
        File.WriteAllText(path, source);
        TextWriter saved = Console.Error;
        StringWriter captured = new();
        Console.SetError(captured);
        object? front;
        try { front = Frontend.Compile(new[] { path }.Concat(runtime).ToArray(), "namedcopy", false, libraryPaths: runtime, elsewherePaths: runtime); }
        catch (InvalidOperationException fault) { throw new Exception("named copy (" + name + "): a compiler fault, not a message: " + fault.Message); }
        finally { Console.SetError(saved); }
        if (front is not null || !captured.ToString().Contains("already defines a member"))
            throw new Exception("named copy (" + name + "): not refused with a message: " + captured);
    }
}
