using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class SourceIndexTests
{
    public static void Run(string work)
    {
        Require(SourceIndexBuilder.AssemblyIdentity("Example, Version=1.0") == SourceIndexBuilder.AssemblyIdentity("EXAMPLE, Version=1.0.0.0"), "assembly identity normalization");
        string first = Path.Combine(work, "First.cs"), second = Path.Combine(work, "Second.cs");
        string source = """"
            #define INCLUDED
            using Alias = Example.Value;
            namespace Example {
            #if INCLUDED
            public partial class Box<T> {
                public const string Label = @"quoted ""text""";
                public T Echo(T value) { string marker = "IMPLEMENTATION_ONLY"; return value; }
                public int Size => 42;
                public int Property { get { return 3; } set => Accept(value); }
                public class Nested<U> { public U Identity(U value) => value; }
            }
            #endif
            }
            """";
        File.WriteAllText(first, source);
        File.WriteAllText(second, "namespace Example { public partial class Box<T> { public T Other(T value) { return value; } } }");
        string path = Path.Combine(work, "sources.idx");
        SourceIndexBuilder.Write(path, new[] { second, first }, "Example, Version=1.0.0.0");
        string prefix = "T:" + SourceIndexBuilder.AssemblyIdentity("EXAMPLE, Version=1.0.0.0") + "\n";
        SourceDeclaration declaration;
        using (DeclarationIndex index = new(path))
        {
            Require(index.WithPrefix("T:").Count() == 3, "source fragment count");
            SourceDeclaration[] parts = index.Find(prefix + "Example.Box`1").Select(SourceDeclaration.Decode).ToArray();
            Require(parts.Length == 2, "partial fragments share identity");
            declaration = parts.Single(p => p.Path == first);
            Require(declaration.Scope.Aliases.Single() == ("", "Alias", "Example.Value"), "using alias scope preserved");
            Require(!declaration.Text.Contains("IMPLEMENTATION_ONLY") && !declaration.Text.Contains("42"), "implementation text leaked into declarations");
            CompilationUnit header = Parser.ParseText(declaration.Text, declarationsOnly: true);
            Require(header.Types.Any(t => t.Name == "Box" && t.TypeParams.Count == 1), "declaration source is parseable");
            Require(index.Find(prefix + "Example.Box`1+Nested`1").Count() == 1, "nested generic identity");
            Require(declaration.ReadImplementation().Contains("IMPLEMENTATION_ONLY"), "on-demand source body lookup");
        }
        File.WriteAllText(first, source.Replace("42", "100").Replace("IMPLEMENTATION_ONLY", "DIFFERENT_IMPLEMENTATION"));
        bool stale = false;
        try { declaration.ReadImplementation(); } catch (InvalidDataException) { stale = true; }
        Require(stale, "stale body generation accepted");
        SourceIndexBuilder.Write(path, new[] { first, second }, "Example, Version=1.0.0.0");
        using (DeclarationIndex index = new(path))
        {
            SourceDeclaration changed = index.Find(prefix + "Example.Box`1").Select(SourceDeclaration.Decode).Single(p => p.Path == first);
            Require(changed.DeclarationHash.SequenceEqual(declaration.DeclarationHash), "body-only edit changed declaration fingerprint");
            Require(!changed.SourceHash.SequenceEqual(declaration.SourceHash), "body edit did not invalidate implementation fingerprint");
        }
        string large = "class Large { public int Method() { " + string.Concat(Enumerable.Range(0, 10000).Select(i => "int local" + i + " = " + i + ";")) + " return 1; } }";
        CompilationUnit declarations = Parser.ParseText(large, declarationsOnly: true);
        Require(declarations.Types.Single().Members.OfType<MethodDecl>().Single().Body!.Statements.Count == 0,
            "declaration parser retained a large implementation AST");
        Console.WriteLine("  source declarations: scopes, partials, nested arity, lazy bodies, fingerprints and body omission passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
