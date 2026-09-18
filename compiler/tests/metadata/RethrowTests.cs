using Corsac.Lang;

namespace Corsac.Tests.Metadata;

public static class RethrowTests
{
    public static void Run()
    {
        CompilationUnit unit = Parser.ParseText("class C<T> { void Run() { try { throw new Failure(); } catch { throw; } } }", "Rethrow.cor");
        TypeDecl type = unit.Types.Single();
        byte[] bytes = Gir.Write(new[] { new Gir.Template { Decl = type } });
        TypeDecl decoded = Gir.Read(bytes, "Rethrow.cor").Single().Decl;
        foreach (TypeDecl candidate in new[] { type, decoded })
        {
            TryStmt statement = (TryStmt)candidate.Members.OfType<MethodDecl>().Single().Body!.Statements.Single();
            if (((ThrowStmt)statement.Body.Statements.Single()).IsRethrow
                || !((ThrowStmt)statement.Catches.Single().Body.Statements.Single()).IsRethrow)
                throw new Exception("Explicit throw and bare rethrow lost their identity");
        }
        Console.WriteLine("rethrow: parser and GIR preserve explicit versus bare throw");
    }
}
