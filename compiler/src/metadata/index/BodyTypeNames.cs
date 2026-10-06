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
    /// <summary>
    /// Hands every type reference under a declaration to <paramref name="found"/>,
    /// and the left of every member access to <paramref name="qualifier"/>.
    ///
    /// A qualifier is where a type is named WITHOUT being in a type position:
    /// `Config.Cpu = CpuKind.I486` names CpuKind nowhere the parser calls a
    /// type, and a const initializer like that is part of the declaration the
    /// index hands over, so the binder needs CpuKind to evaluate it. These
    /// are guesses -- the left of a member access is usually a variable -- so
    /// the caller must treat a hit as speculative and a miss as ordinary.
    /// A qualifier is a single name, or a dotted path (`Elf.Linker`) whose
    /// first name is a capital's, and the caller must look a path up whole,
    /// as the binder does, never by its last name alone.
    ///
    /// With a qualifier callback a generic type on the left of a member
    /// access, `Pool&lt;Node&gt;.Shared`, also comes to <paramref name="found"/>,
    /// as a reference made for the purpose rather than a node of the tree.
    /// </summary>
    public static void Walk(TypeDecl declaration, Action<TypeRef> found, Action<string>? qualifier = null)
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
                    if (e.Returns is not null) Type(e.Returns);
                    Expression(e.Body); Statement(e.BlockBody);
                    return;
                case MemberExpr e:
                    if (qualifier is not null)
                    {
                        if (e.Target is NameExpr name)
                        {
                            if (name.TypeArgs.Count == 0) qualifier(name.Name);
                            // A GENERIC TYPE ON THE LEFT, `Pool<Node>.Shared`:
                            // a name with type arguments followed by a dot is
                            // a type and nothing else (a generic method's
                            // arguments are followed by its call, not a dot),
                            // so it is reported as the type reference it is,
                            // with its arity. Only to the prefetch: a fresh
                            // reference, which nobody rewrites in place.
                            else found(new TypeRef { Name = name.Name, Arguments = name.TypeArgs.ToList(), Line = name.Line, Col = name.Col });
                        }
                        // AND THE WHOLE DOTTED PATH, `Corsac.Lang.Elf.Linker`
                        // of `Corsac.Lang.Elf.Linker.SharedInitName` or
                        // `Elf.Linker` of `Elf.Linker.Layout(...)`. Each
                        // member access in the chain reports its own path, so
                        // every prefix of two names or more is heard, the
                        // left-most single name above. Reporting only that one
                        // name heard `Corsac` and never the type, and the
                        // binder, which looks the whole path up
                        // (Binder.ConstantOwner, CheckMember), demanded it a
                        // pass later -- a whole pass, on Lowering.Enum.cs, for
                        // the one declaration Lowering's SharedInitName names.
                        if (Path(e) is string path) qualifier(path);
                    }
                    Expression(e.Target); Types(e.TypeArgs);
                    return;
                case NameExpr e: Types(e.TypeArgs); return;
                case NewExpr e:
                    Type(e.Type);
                    foreach (Expr argument in e.Args) Expression(argument);
                    Expression(e.ArraySize);
                    if (e.Elements is not null) foreach (Expr element in e.Elements) Expression(element);
                    Init(e.Body);
                    return;
                case PatternExpr e: Expression(e.Subject); Expression(e.Test); return;
                case SequenceExpr e: Statement(e.Effect); Expression(e.Value); return;
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
                case YieldStmt s: Expression(s.Value); return;
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
                case LabeledStmt s: Statement(s.Body); return;
                default: return;
            }
        }

        // The dotted name a chain of member accesses spells, `A.B.C`, when it
        // is nothing but names and begins with a capital: a namespace or a
        // type, as Binder.NamespaceOnly reads one. Null for anything else --
        // `node.Kind`, a call, an index, `?.`, type arguments anywhere -- and
        // for a lone name, which is reported on its own above.
        static string? Path(MemberExpr member)
        {
            Expr head = member.Target;
            while (head is MemberExpr inner)
            {
                if (inner.TypeArgs.Count != 0 || inner.NullConditional) return null;
                head = inner.Target;
            }
            if (head is not NameExpr { TypeArgs.Count: 0 } first || first.Name.Length == 0 || !char.IsUpper(first.Name[0])
                || member.TypeArgs.Count != 0 || member.NullConditional)
                return null;
            return Spelt(member);
        }
        static string Spelt(Expr expression) => expression is MemberExpr member
            ? Spelt(member.Target) + "." + member.Name
            : ((NameExpr)expression).Name;

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
                    // A CONSTRUCTOR'S `: base(...)` OR `: this(...)` is code
                    // the binder binds like any call, and `: base(Kind.Leaf)`
                    // names a type no statement of the body does.
                    if (method.Init is not null) foreach (Expr argument in method.Init.Args) Expression(argument);
                    Statement(method.Body);
                    break;
            }
        }
    }
}
