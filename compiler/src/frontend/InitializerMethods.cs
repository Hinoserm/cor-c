namespace Corsac.Lang;

/// <summary>Keep indexed initializer expressions in the lexical scope and source unit that owns them.</summary>
internal static class InitializerMethods
{
    public static Expr Value(MemberDecl member, TypeRef returns, Expr expression, List<MethodDecl> helpers)
    {
        if (member.OwnedImplementation is null) return expression;
        string name = "FieldInit$" + member.Name;
        helpers.Add(new MethodDecl
        {
            Name = name, Mods = Mods.Static | Mods.Private, Returns = returns,
            Body = new Block { Statements = { new ReturnStmt { Value = expression, Line = member.Line, Col = member.Col } } },
            OwnedImplementation = member.OwnedImplementation,
            File = member.File, Scope = member.Scope, Namespace = member.Namespace,
            Line = member.Line, Col = member.Col,
        });
        return new CallExpr { Target = new NameExpr { Name = name, Line = member.Line, Col = member.Col }, Line = member.Line, Col = member.Col };
    }
}
