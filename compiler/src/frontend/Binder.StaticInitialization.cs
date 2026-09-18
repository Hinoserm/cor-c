#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    private static void AddSynchronizedInitializer(TypeDecl type, List<Stmt> statements)
    {
        const string failure = "StaticFailure$";
        const string status = "$initStatus";
        const string success = "$initSuccess";
        const string error = "$initError";
        NameExpr Name(string name) => new() { Name = name, Line = type.Line, Col = type.Col };
        LiteralExpr Number(int value) => new() { Kind = Lit.Int, Text = value.ToString(), IntValue = value };
        LiteralExpr Boolean(bool value) => new() { Kind = Lit.Bool, Text = value ? "true" : "false", IntValue = value ? 1 : 0 };
        Expr Address() => Call("Sys", "AddressOf", type, new RefArgExpr { Target = Name(BindResult.ReadyField) });
        ExprStmt Assign(string target, Expr value) => new() { Expr = new AssignExpr { Target = Name(target), Value = value } };
        ExprStmt RuntimeCall(string method) => new() { Expr = Call("Runtime", method, type, Address()) };

        type.Members.Add(new FieldDecl
        {
            Name = BindResult.ReadyField, Mods = Mods.Static | Mods.Private | Mods.Volatile,
            Type = new TypeRef { Name = "int" }, File = type.File, Line = type.Line, Col = type.Col,
        });
        type.Members.Add(new FieldDecl
        {
            Name = failure, Mods = Mods.Static | Mods.Private,
            Type = new TypeRef { Name = "Exception" }, File = type.File, Line = type.Line, Col = type.Col,
        });
        Block body = new();
        body.Statements.AddRange(statements);
        // A source-level return exits the body, not the completion protocol.
        type.Members.Add(new MethodDecl
        {
            Name = "StaticInitBody$", Mods = Mods.Static | Mods.Private,
            OwnedImplementation = !type.Elsewhere,
            Returns = new TypeRef { Name = "void" }, Body = body,
            File = type.File, Line = type.Line, Col = type.Col,
        });
        Block wrapper = new();
        wrapper.Statements.Add(new LocalDecl
        {
            Name = status, Type = new TypeRef { Name = "int" },
            Init = Call("Runtime", "EnterTypeInitialization", type, Address()),
        });
        wrapper.Statements.Add(new IfStmt
        {
            Cond = new BinaryExpr { Op = BinOp.Eq, Left = Name(status), Right = Number(0) },
            Then = new ReturnStmt(),
        });
        wrapper.Statements.Add(new IfStmt
        {
            Cond = new BinaryExpr { Op = BinOp.Eq, Left = Name(status), Right = Number(2) },
            Then = new ThrowStmt { Value = Name(failure) },
        });
        wrapper.Statements.Add(new LocalDecl { Name = success, Type = new TypeRef { Name = "bool" }, Init = Boolean(false) });
        Block guarded = new();
        guarded.Statements.Add(new ExprStmt { Expr = new CallExpr { Target = Name("StaticInitBody$") } });
        guarded.Statements.Add(Assign(success, Boolean(true)));
        Block cleanup = new();
        cleanup.Statements.Add(new IfStmt
        {
            Cond = Name(success), Then = RuntimeCall("CompleteTypeInitialization"), Else = RuntimeCall("FailTypeInitialization"),
        });
        TryStmt attempt = new() { Body = guarded, Finally = cleanup };
        Block failed = new();
        failed.Statements.Add(Assign(failure, Name(error)));
        failed.Statements.Add(Assign(failure, new NewExpr
        {
            Type = new TypeRef { Name = "TypeInitializationException" },
            Args = { new LiteralExpr { Kind = Lit.Str, Text = TypeKey(type) }, Name(error) },
        }));
        failed.Statements.Add(new ThrowStmt { Value = Name(failure) });
        attempt.Catches.Add(new CatchClause { Type = new TypeRef { Name = "Exception" }, Name = error, Body = failed });
        wrapper.Statements.Add(attempt);
        type.Members.Add(new MethodDecl
        {
            Name = "StaticInit$", Mods = Mods.Static | Mods.Public,
            OwnedImplementation = !type.Elsewhere,
            Returns = new TypeRef { Name = "void" }, Body = wrapper,
            File = type.File, Line = type.Line, Col = type.Col,
        });
    }
}
