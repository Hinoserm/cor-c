namespace Corsac.Lang;

/// <summary>Keep indexed initializer expressions in the lexical scope and source unit that owns them.</summary>
internal static class InitializerMethods
{
    public static Expr Value(MemberDecl member, TypeRef returns, Expr expression, List<MethodDecl> helpers)
    {
        // A CONSTANT HAS NO SCOPE TO KEEP. `items = null!`, `count = 0`: the
        // value is the same wherever it is written, so it is written in place
        // whichever unit compiles the type. Made a helper call only where the
        // type's implementation came from elsewhere, the same generic instance
        // compiled in two units differed before optimisation -- a call in one,
        // the constant in the other -- and the link refused to share it
        // (DefinitionSemantics certifies the pre-optimisation form).
        if (member.OwnedImplementation is null || IsConstant(expression)) return expression;
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

    /// <summary>A literal, forgiven or negated as written: nothing in it is looked up by name.</summary>
    private static bool IsConstant(Expr expression) => expression switch
    {
        LiteralExpr => true,
        SuppressExpr suppressed => IsConstant(suppressed.Operand),
        UnaryExpr { Op: UnOp.Neg or UnOp.Not or UnOp.BitNot } unary => IsConstant(unary.Operand),
        _ => false,
    };
}
