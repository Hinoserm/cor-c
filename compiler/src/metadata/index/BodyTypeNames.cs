#nullable enable
namespace Corsac.Lang.Metadata;

/// <summary>
/// Every type a declaration NAMES, its bodies included, for prefetching.
///
/// An imported template comes with its body, because a specialisation is
/// compiled from it, and that body names types its signature never mentions:
/// List's methods make a ListEnumerator and throw an
/// ArgumentOutOfRangeException. Reaching those only when the body is bound
/// costs a whole discovery round each, and a round means the unit is thrown
/// away and parsed, merged, monomorphised and bound again.
///
/// A PREFETCH, so nothing depends on this being complete: a name it misses is
/// demanded by the binder exactly as before, one round later. A node type
/// added to the syntax and not added here therefore costs a round and never
/// an answer. Only <see cref="TypeRef"/> positions are reported -- a place
/// the parser already decided is a type -- never a bare identifier that
/// merely looks like one, because loading a declaration nothing asked for
/// would put a name in scope the compilation did not have.
///
/// WRITTEN OUT RATHER THAN REFLECTED OVER. The first version walked the tree
/// with System.Reflection, which worked in the managed compiler and did
/// NOTHING in the Native AOT one, whose unreferenced property metadata is
/// trimmed: the shipped compiler silently kept paying the rounds this is
/// meant to remove. The compiler also has to run on CORSAC eventually, whose
/// runtime is not going to hand out property tables either.
/// </summary>
public static class BodyTypeNames
{
    /// <summary>Hands every type reference under a declaration to <paramref name="found"/>.</summary>
    public static void Walk(TypeDecl declaration, Action<TypeRef> found)
    {
        void Type(TypeRef? reference)
        {
            if (reference is null) return;
            found(reference);
            foreach (TypeRef argument in reference.Args) Type(argument);
            if (reference.UseArgs is not null) foreach (TypeRef argument in reference.UseArgs) Type(argument);
        }
        void Types(IEnumerable<TypeRef>? references)
        {
            if (references is null) return;
            foreach (TypeRef reference in references) Type(reference);
        }

        void Init(InitBody? body)
        {
            if (body is null) return;
            foreach (InitAssign assign in body.Inits) { Expression(assign.Value); Init(assign.Nested); }
            foreach (InitAdd add in body.Adds) foreach (Expr argument in add.Args) Expression(argument);
            foreach (InitIndex index in body.Indexes)
            {
                foreach (Expr argument in index.Args) Expression(argument);
                Expression(index.Value);
            }
        }

        void Expression(Expr? expression)
        {
            switch (expression)
            {
                case null: return;
                case AsExpr e: Expression(e.Operand); Type(e.Type); return;
                case AssignExpr e: Expression(e.Target); Expression(e.Value); return;
                case AwaitExpr e: Expression(e.Operand); return;
                case BinaryExpr e: Expression(e.Left); Expression(e.Right); return;
                case CallExpr e:
                    Expression(e.Target); Type(e.ResultTypeUse);
                    if (e.ArgumentTypeUses is not null) foreach (TypeRef use in e.ArgumentTypeUses.Values) Type(use);
                    foreach (Expr argument in e.Args) Expression(argument);
                    return;
                case CastExpr e: Type(e.Type); Expression(e.Operand); return;
                case ConditionalExpr e: Expression(e.Cond); Expression(e.Then); Expression(e.Else); return;
                case DefaultExpr e: Type(e.Type); return;
                case FromEndExpr e: Expression(e.Offset); return;
                case IndexExpr e: Expression(e.Target); foreach (Expr argument in e.Args) Expression(argument); return;
                case IsExpr e: Expression(e.Operand); Type(e.Type); return;
                case LambdaExpr e:
                    foreach (Param parameter in e.Params) { Type(parameter.Type); Expression(parameter.Default); }
                    Expression(e.Body); Statement(e.BlockBody);
                    return;
                case MemberExpr e: Expression(e.Target); Types(e.TypeArgs); return;
                case NameExpr e: Types(e.TypeArgs); return;
                case NewExpr e:
                    Type(e.Type);
                    foreach (Expr argument in e.Args) Expression(argument);
                    Expression(e.ArraySize);
                    if (e.Elements is not null) foreach (Expr element in e.Elements) Expression(element);
                    Init(e.Body);
                    return;
                case PatternExpr e: Expression(e.Subject); Expression(e.Test); return;
                case RangeExpr e: Expression(e.From); Expression(e.To); return;
                case RefArgExpr e: Expression(e.Target); Type(e.Declare); return;
                case SizeOfExpr e: Type(e.Type); return;
                case SuppressExpr e: Expression(e.Operand); return;
                case SwitchExpr e:
                    Expression(e.Subject);
                    foreach (SwitchArm arm in e.Arms)
                    {
                        Expression(arm.Value); Type(arm.Type); Expression(arm.When); Expression(arm.Result);
                    }
                    return;
                case ThrowExpr e: Expression(e.Value); return;
                case TupleExpr e: foreach (Expr item in e.Items) Expression(item); return;
                case TypeOfExpr e: Type(e.Type); return;
                case UnaryExpr e: Expression(e.Operand); return;
                case WithExpr e: Expression(e.Source); Init(e.Body); return;
                default: return;
            }
        }

        void Bindings(IEnumerable<Binding>? bindings)
        {
            if (bindings is null) return;
            foreach (Binding binding in bindings)
            {
                Type(binding.Type); Expression(binding.Target); Bindings(binding.Nested);
            }
        }

        void Statement(Stmt? statement)
        {
            switch (statement)
            {
                case null: return;
                case Block s: foreach (Stmt inner in s.Statements) Statement(inner); return;
                case DeconstructStmt s: Bindings(s.Names); Expression(s.Value); return;
                case DoStmt s: Expression(s.Cond); Statement(s.Body); return;
                case ExprStmt s: Expression(s.Expr); return;
                case ForStmt s:
                    Statement(s.Init); Expression(s.Cond);
                    foreach (Expr step in s.Step) Expression(step);
                    Statement(s.Body);
                    return;
                case ForeachStmt s: Type(s.Type); Expression(s.Sequence); Bindings(s.Bindings); Statement(s.Body); return;
                case GotoCaseStmt s: Expression(s.Value); return;
                case IfStmt s: Expression(s.Cond); Statement(s.Then); Statement(s.Else); return;
                case LocalDecl s:
                    Type(s.Type); Expression(s.Init);
                    foreach (LocalDecl also in s.Also) Statement(also);
                    return;
                case ReturnStmt s: Expression(s.Value); return;
                case SwitchStmt s:
                    Expression(s.Subject);
                    foreach (SwitchCase branch in s.Cases)
                    {
                        Expression(branch.Pattern);
                        foreach (Stmt inner in branch.Body) Statement(inner);
                    }
                    return;
                case ThrowStmt s: Expression(s.Value); return;
                case TryStmt s:
                    Statement(s.Body);
                    foreach (CatchClause clause in s.Catches)
                    {
                        Type(clause.Type); Expression(clause.When); Statement(clause.Body);
                    }
                    Statement(s.Finally);
                    return;
                case UsingDeclStmt s: Statement(s.Declaration); return;
                case WhileStmt s: Expression(s.Cond); Statement(s.Body); return;
                default: return;
            }
        }

        Types(declaration.Bases);
        Types(declaration.TemplateArgs);
        foreach (Expr argument in declaration.BaseArgs) Expression(argument);
        foreach (TypeParam parameter in declaration.TypeParams) Types(parameter.Constraints);
        foreach (MemberDecl member in declaration.Members)
        {
            switch (member)
            {
                case FieldDecl field:
                    Type(field.Type); Expression(field.Init);
                    foreach (FieldDecl more in field.More) { Type(more.Type); Expression(more.Init); }
                    break;
                case PropertyDecl property:
                    Type(property.Type); Expression(property.Init);
                    foreach (Param parameter in property.Params) { Type(parameter.Type); Expression(parameter.Default); }
                    Statement(property.Getter); Statement(property.Setter);
                    break;
                case MethodDecl method:
                    Type(method.Returns);
                    foreach (TypeParam parameter in method.TypeParams) Types(parameter.Constraints);
                    foreach (Param parameter in method.Params) { Type(parameter.Type); Expression(parameter.Default); }
                    Statement(method.Body);
                    break;
            }
        }
    }
}
