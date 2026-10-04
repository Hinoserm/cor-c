#nullable enable
namespace Corsac.Lang;

/// <summary>
/// C#'S `dynamic`: an expression typed dynamic is an object here (Prim.Any,
/// Type.Dynamic set), and every operation on one is bound when the program
/// runs. Each is rewritten here into a call of the class library's binder,
/// System.DynamicRuntime, as Roslyn lowers them onto Microsoft.CSharp's call
/// sites -- and with the same C# semantics at run time:
///
///   d.X                 DynamicRuntime.GetMember(d, "X")
///   d.X = v             DynamicRuntime.SetMember(d, "X", v)
///   d.X op= v           DynamicRuntime.CompoundMember(d, "X", op, v)
///   d.M(a, b)           DynamicRuntime.InvokeMember(d, "M", new object?[] { a, b })
///   d(a, b)             DynamicRuntime.Invoke(d, new object?[] { a, b })
///   d[i, j]             DynamicRuntime.GetIndex(d, new object?[] { i, j })
///   d[i] = v, d[i] op= v   SetIndex, CompoundIndex
///   l op r              DynamicRuntime.Binary(op, l, r)     (either operand dynamic)
///   op d, d++, ++d      Unary, and the increments as a read, an op and a write
///   (T)d, T t = d       (T)DynamicRuntime.Convert(d, typeof(T), explicit)
///   if (d), d && e      DynamicRuntime.IsTrue(d)
///   foreach (x in d)    foreach (dynamic x in DynamicRuntime.Enumerate(d))
///
/// The op numbers are .NET's System.Linq.Expressions.ExpressionType. What a
/// program's own classes answer to a name at run time is written for them
/// before binding (DynamicDeclarations); the library's binder knows the
/// class library's shapes, IDispatch, DynamicObject and ExpandoObject.
///
/// Only in a unit that writes `dynamic` (CompilationUnit.UsesDynamic): every
/// test below is behind that, so nothing else pays for it.
/// </summary>
public sealed partial class Binder
{
    private bool _usesDynamic;

    /// <summary>Member accesses on a dynamic receiver, read or about to be written or called.</summary>
    private readonly HashSet<MemberExpr> _lateMembers = new(ReferenceEqualityComparer.Instance);

    /// <summary>Indexings of a dynamic receiver.</summary>
    private readonly HashSet<IndexExpr> _lateIndexes = new(ReferenceEqualityComparer.Instance);

    private const string LateBinder = "DynamicRuntime";

    // ---- the pieces a rewrite is made of ----------------------------------------------------

    private static LiteralExpr LateText(string text, Node at)
        => new() { Kind = Lit.Str, Text = text, Line = at.Line, Col = at.Col };

    private static LiteralExpr LateNumber(int value, Node at)
        => new() { Kind = Lit.Int, Text = value.ToString(), IntValue = value, Line = at.Line, Col = at.Col };

    private static LiteralExpr LateTruth(bool value, Node at)
        => new() { Kind = Lit.Bool, Text = value ? "true" : "false", IntValue = value ? 1 : 0, Line = at.Line, Col = at.Col };

    /// <summary>`new object?[] { … }`, the arguments as the binder takes them.</summary>
    private static NewExpr LateArguments(IEnumerable<Expr> values, Node at)
        => new()
        {
            Type = new TypeRef { Name = "object", Nullable = true, Line = at.Line, Col = at.Col },
            Elements = values.ToList(),
            Line = at.Line, Col = at.Col,
        };

    private static CallExpr LateCall(string method, Node at, params Expr[] arguments) => Call(LateBinder, method, at, arguments);

    /// <summary>What a rewrite of `e` stands on: what it was rewritten to already, or itself.</summary>
    private Expr LateOperand(Expr e) => _r.Rewrites.TryGetValue(e, out Expr? made) ? made : e;

    /// <summary>
    /// A value handed to the binder (an argument, a value stored, an
    /// operand): what LateOperand says, a delegate held for calling with
    /// objects (DynamicRuntime.Callable), as C#'s binder can call the one it
    /// is given.
    /// </summary>
    private Expr LateValue(Expr e)
    {
        Expr operand = LateOperand(e);
        return _r.ExprType.TryGetValue(e, out Type? t) && Delegated(t) ? LateCall("Callable", e, operand) : operand;
    }

    /// <summary>A Func or an Action of up to three parameters: what DynamicRuntime.Callable holds.</summary>
    private static bool Delegated(Type t)
    {
        if (t.IsError || t.Dynamic || t.IsArray || t.Symbol is not { Kind: TypeKind.Interface } face) return false;
        string template = face.Decl?.Template ?? face.Name;
        if (template != "Func" && template != "Action") return false;
        int parameters = face.FindMethods("Invoke").FirstOrDefault()?.Params.Count ?? -1;
        return parameters >= 0 && parameters <= 3;
    }

    /// <summary>The ExpressionType of a two-operand operator, or -1 for one that cannot be bound late.</summary>
    private static int LateBinaryCode(BinOp op) => op switch
    {
        BinOp.Add => 0, BinOp.Sub => 42, BinOp.Mul => 26, BinOp.Div => 12, BinOp.Rem => 25,
        BinOp.And => 2, BinOp.Or => 36, BinOp.Xor => 14, BinOp.Shl => 19, BinOp.Shr => 41,
        BinOp.Eq => 13, BinOp.Ne => 35, BinOp.Lt => 20, BinOp.Gt => 15, BinOp.Le => 21, BinOp.Ge => 16,
        _ => -1,
    };

    // ---- member access, calls, indexing ---------------------------------------------------

    /// <summary>
    /// A member of a dynamic receiver (CheckMemberCore, once the receiver
    /// is known to be one). Being called, it is left for the call to bind
    /// (InvokeMember, not a read and then an invocation: IDispatch tells a
    /// method from a property by that); otherwise it reads as GetMember,
    /// which an assignment to it replaces (LateAssignment).
    /// </summary>
    private Type LateMember(MemberExpr m)
    {
        _lateMembers.Add(m);
        if (ReferenceEquals(m, _callee)) return Type.DynamicAny;
        if (m.TypeArgs.Count > 0)
        {
            Error(m, $"'{m.Name}': type arguments cannot be given to a dynamically bound member");
            return Type.Error;
        }
        CallExpr read = LateCall("GetMember", m, LateOperand(m.Target), LateText(m.Name, m));
        _r.Rewrites[m] = read;
        CheckExpr(read);
        return Type.DynamicAny;
    }

    /// <summary>
    /// The call of a member of a dynamic receiver, or of a dynamic value
    /// itself (CheckCall, once the target is checked). Null when it is
    /// neither.
    /// </summary>
    private Type? LateInvocation(CallExpr c, Type targetType)
    {
        bool member = c.Target is MemberExpr callee && _lateMembers.Contains(callee);
        if (!member && !(targetType.Dynamic && !targetType.IsError
                         && !(_r.Resolved.TryGetValue(c.Target, out Sym? s) && s is MethodGroupSym or CapturedMethodGroupSym)))
        {
            return null;
        }
        foreach (Expr argument in c.Args)
        {
            if (argument is RefArgExpr)
            {
                Error(argument, "a 'ref' or 'out' argument cannot be passed to a dynamically bound call here");
                return Type.Error;
            }
            if (argument is LambdaExpr)
            {
                // C#'s CS1977.
                Error(argument, "a lambda cannot be passed to a dynamically bound call without a cast to a delegate type");
                return Type.Error;
            }
        }
        if (member && ((MemberExpr)c.Target).TypeArgs.Count > 0)
        {
            Error(c, $"'{((MemberExpr)c.Target).Name}': type arguments cannot be given to a dynamically bound call here");
            return Type.Error;
        }
        NewExpr arguments = LateArguments(c.Args.Select(LateValue), c);
        CallExpr made = member
            ? LateCall("InvokeMember", c, LateOperand(((MemberExpr)c.Target).Target), LateText(((MemberExpr)c.Target).Name, c), arguments)
            : LateCall("Invoke", c, LateOperand(c.Target), arguments);
        _r.Rewrites[c] = made;
        CheckExpr(made);
        return Type.DynamicAny;
    }

    /// <summary>An indexing of a dynamic receiver: GetIndex, which a store replaces.</summary>
    private Type LateIndex(IndexExpr ix)
    {
        _lateIndexes.Add(ix);
        foreach (Expr a in ix.Args) CheckExpr(a);
        CallExpr read = LateCall("GetIndex", ix, LateOperand(ix.Target), LateArguments(ix.Args.Select(LateValue), ix));
        _r.Rewrites[ix] = read;
        CheckExpr(read);
        return Type.DynamicAny;
    }

    /// <summary>
    /// An assignment whose target is bound late -- a member or an element of
    /// a dynamic receiver -- or a compound one to a dynamic variable (the
    /// operator bound late, the store an ordinary one). Null otherwise.
    /// </summary>
    private Type? LateAssignment(AssignExpr a, Type target)
    {
        Expr value = LateValue(a.Value);
        if (a.Target is MemberExpr member && _lateMembers.Contains(member))
        {
            Expr receiver = LateOperand(member.Target);
            CallExpr made = a.Op is BinOp op
                ? LateCall("CompoundMember", a, receiver, LateText(member.Name, a), LateNumber(LateCompoundCode(op, a), a), value)
                : LateCall("SetMember", a, receiver, LateText(member.Name, a), value);
            _r.Rewrites[a] = made;
            CheckExpr(made);
            return Type.DynamicAny;
        }
        if (a.Target is IndexExpr index && _lateIndexes.Contains(index))
        {
            Expr receiver = LateOperand(index.Target);
            NewExpr keys = LateArguments(index.Args.Select(LateValue), a);
            CallExpr made = a.Op is BinOp op
                ? LateCall("CompoundIndex", a, receiver, keys, LateNumber(LateCompoundCode(op, a), a), value)
                : LateCall("SetIndex", a, receiver, keys, value);
            _r.Rewrites[a] = made;
            CheckExpr(made);
            return Type.DynamicAny;
        }
        // AND `x op= d` INTO AN ORDINARY VARIABLE, d dynamic: the operator
        // bound late, its result converted to x's type (C# 12.21.4), x read
        // and written once each where it is a variable or a member of one.
        bool lateValue = _r.ExprType.TryGetValue(a.Value, out Type? valueType) && valueType.Dynamic;
        if (a.Op is BinOp compound && (target.Dynamic || lateValue) && !target.IsError)
        {
            // `d += v`: d = Binary(Add, d, v), d read and written once each
            // where it is a variable, which is all a dynamic one can be here.
            AssignExpr made = new()
            {
                Target = a.Target,
                Value = LateCall("Binary", a, LateNumber(LateCompoundCode(compound, a), a), a.Target, value),
                Line = a.Line, Col = a.Col,
            };
            _r.Rewrites[a] = made;
            CheckExpr(made);
            return Type.DynamicAny;
        }
        return null;
    }

    private int LateCompoundCode(BinOp op, Node at)
    {
        int code = LateBinaryCode(op);
        if (code < 0) Error(at, $"'{op}=' cannot be bound late");
        return code;
    }

    // ---- operators ------------------------------------------------------------------

    /// <summary>
    /// A two-operand operator with a dynamic operand (CheckBinary, once both
    /// are checked): Binary, its result dynamic; `??` as C# has it, the
    /// right side only when the left is null. Null when neither operand is
    /// dynamic.
    /// </summary>
    private Type? LateBinary(BinaryExpr b, Type l, Type r)
    {
        if (!l.Dynamic && !r.Dynamic) return null;
        // A PATTERN'S OWN TEST -- `d is null`, `d is 3` -- is a test of the
        // value as an object, not an operator to bind.
        if (b.PatternNullTest || b.PatternConstant) return null;
        if (b.Op == BinOp.Coalesce)
        {
            PatternExpr once = new()
            {
                Subject = LateOperand(b.Left),
                Test = new ConditionalExpr
                {
                    Cond = new BinaryExpr
                    {
                        Op = BinOp.Eq,
                        Left = new SubjectExpr { Line = b.Line, Col = b.Col },
                        Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = b.Line, Col = b.Col },
                        Line = b.Line, Col = b.Col,
                    },
                    Then = new CastExpr { Type = new TypeRef { Name = "object", Nullable = true, Line = b.Line, Col = b.Col }, Operand = LateOperand(b.Right), Line = b.Line, Col = b.Col },
                    Else = new SubjectExpr { Line = b.Line, Col = b.Col },
                    Line = b.Line, Col = b.Col,
                },
                Line = b.Line, Col = b.Col,
            };
            _r.Rewrites[b] = once;
            CheckExpr(once);
            return Type.DynamicAny;
        }
        int code = LateBinaryCode(b.Op);
        if (code < 0)
        {
            Error(b, $"'{b.Op}' cannot be bound late");
            return Type.Error;
        }
        CallExpr made = LateCall("Binary", b, LateNumber(code, b), LateValue(b.Left), LateValue(b.Right));
        _r.Rewrites[b] = made;
        CheckExpr(made);
        return Type.DynamicAny;
    }

    /// <summary>
    /// `&&` and `||` with a dynamic operand: each dynamic one asked IsTrue
    /// (C#'s operator true on the run-time value), the other side only when
    /// the first does not settle it; a bool. Null when neither is dynamic.
    /// </summary>
    private Type? LateLogical(BinaryExpr b)
    {
        if (!_usesDynamic) return null;
        Type l = Peek(b.Left), r = Peek(b.Right);
        if (!l.Dynamic && !r.Dynamic) return null;
        BinaryExpr made = new()
        {
            Op = b.Op,
            Left = l.Dynamic ? LateCall("IsTrue", b.Left, b.Left) : b.Left,
            Right = r.Dynamic ? LateCall("IsTrue", b.Right, b.Right) : b.Right,
            Line = b.Line, Col = b.Col,
        };
        _r.Rewrites[b] = made;
        return CheckExpr(made);
    }

    /// <summary>
    /// A one-operand operator on a dynamic operand (the UnaryExpr case, once
    /// the operand is checked): Unary; `++` and `--` the value read, moved
    /// one and written back, a member's and an element's through their
    /// receiver once (IncrementMember, IncrementIndex), the value the
    /// expression has the old one after it and the new one before. Null
    /// when the operand is not dynamic.
    /// </summary>
    private Type? LateUnary(UnaryExpr u, Type t)
    {
        if (!t.Dynamic || t.IsError) return null;
        bool increment = u.Op is UnOp.PreInc or UnOp.PostInc;
        bool decrement = u.Op is UnOp.PreDec or UnOp.PostDec;
        if (increment || decrement)
        {
            int code = increment ? 54 : 49;
            bool postfix = u.Op is UnOp.PostInc or UnOp.PostDec;
            Expr made;
            if (u.Operand is MemberExpr member && _lateMembers.Contains(member))
            {
                made = LateCall("IncrementMember", u, LateOperand(member.Target), LateText(member.Name, u), LateNumber(code, u), LateTruth(postfix, u));
            }
            else if (u.Operand is IndexExpr index && _lateIndexes.Contains(index))
            {
                made = LateCall("IncrementIndex", u, LateOperand(index.Target), LateArguments(index.Args.Select(LateOperand), u), LateNumber(code, u), LateTruth(postfix, u));
            }
            else
            {
                // A variable: Post(old, x = Unary(op, x)) answers the old value
                // after the write, the arguments being read in order; a prefix
                // one is the assignment itself.
                AssignExpr store = new()
                {
                    Target = u.Operand,
                    Value = LateCall("Unary", u, LateNumber(code, u), u.Operand),
                    Line = u.Line, Col = u.Col,
                };
                made = postfix ? LateCall("Post", u, u.Operand, store) : store;
            }
            _r.Rewrites[u] = made;
            CheckExpr(made);
            return Type.DynamicAny;
        }
        int unary = u.Op switch { UnOp.Neg => 28, UnOp.Plus => 29, UnOp.Not => 34, UnOp.BitNot => 82, _ => -1 };
        if (unary < 0) return null;
        CallExpr call = LateCall("Unary", u, LateNumber(unary, u), LateOperand(u.Operand));
        _r.Rewrites[u] = call;
        CheckExpr(call);
        return Type.DynamicAny;
    }

    // ---- conversions ------------------------------------------------------------------

    /// <summary>
    /// A dynamic value where something else is wanted -- assigned, passed,
    /// returned (CheckAssignable): C#'s implicit conversion from dynamic,
    /// made at run time by the binder (Convert, not explicit) and then the
    /// object the binder answers cast to the type, which is then exactly
    /// it. True when the conversion was written.
    /// </summary>
    private bool LateConversion(Type from, Type to, Node at)
    {
        if (!_usesDynamic || !from.Dynamic || from.IsError || to.IsError || at is not Expr value) return false;
        if (to.Dynamic || (to.Prim == Prim.Any && to.Symbol is null && !to.IsArray)) return false;
        if (_r.Rewrites.TryGetValue(value, out Expr? already) && _r.UserConversions.Contains(already)) return true;
        if (RefOf(to) is not TypeRef spelt || LateTypeOf(to) is not TypeRef asked) return false;
        Expr made = LateConverted(LateOperand(value), spelt, asked, explicitly: false, value);
        _r.Rewrites[value] = made;
        _r.UserConversions.Add(made);
        CheckExpr(made);
        return true;
    }

    /// <summary>The type `typeof` names for a conversion to `to`: a reference without its `?`.</summary>
    private static TypeRef? LateTypeOf(Type to) => RefOf(to.IsNullableValue || !to.Nullable ? to : to.AsNonNullable());

    /// <summary>
    /// A Func or an Action of up to three parameters converted to dynamic:
    /// held by an adapter the binder calls with objects (DynamicRuntime.
    /// Callable), since a delegate here is an interface with an Invoke and
    /// the binder has no other way to call one it knows only as an object.
    /// Converted back (Convert), the delegate itself. True when written.
    /// </summary>
    private bool LateCallable(Type from, Node at)
    {
        if (!_usesDynamic || at is not Expr value || !Delegated(from)) return false;
        if (_r.Rewrites.TryGetValue(value, out Expr? already) && _r.UserConversions.Contains(already)) return true;
        CallExpr made = LateCall("Callable", value, LateOperand(value));
        _r.Rewrites[value] = made;
        _r.UserConversions.Add(made);
        CheckExpr(made);
        return true;
    }

    /// <summary>`(T)DynamicRuntime.Convert(value, typeof(T), explicitly)`.</summary>
    private static CastExpr LateConverted(Expr value, TypeRef to, TypeRef asked, bool explicitly, Node at)
        => new()
        {
            Type = to,
            Operand = LateCall("Convert", at, value, new TypeOfExpr { Type = asked, Line = at.Line, Col = at.Col }, LateTruth(explicitly, at)),
            Line = at.Line, Col = at.Col,
        };

    /// <summary>
    /// `(T)d`: the binder's explicit conversion, then the cast (the CastExpr
    /// case, once both sides are known). Null when the operand is not
    /// dynamic, or T is object or dynamic itself (nothing to bind).
    /// </summary>
    private Type? LateCast(CastExpr cast, Type operand, Type wanted)
    {
        if (!_usesDynamic || !operand.Dynamic || operand.IsError || wanted.IsError) return null;
        if (wanted.Dynamic || (wanted.Prim == Prim.Any && wanted.Symbol is null && !wanted.IsArray)) return null;
        if (LateTypeOf(wanted) is not TypeRef asked) return null;
        CastExpr made = LateConverted(LateOperand(cast.Operand), cast.Type, asked, explicitly: true, cast);
        _r.Rewrites[cast] = made;
        _r.UserConversions.Add(made);
        return CheckExpr(made);
    }

    /// <summary>A dynamic condition: IsTrue (CheckCondition). True when written.</summary>
    private bool LateCondition(Expr e, Type t)
    {
        if (!_usesDynamic || !t.Dynamic || t.IsError) return false;
        CallExpr made = LateCall("IsTrue", e, LateOperand(e));
        _r.Rewrites[e] = made;
        CheckExpr(made);
        return true;
    }

    /// <summary>
    /// `foreach` over a dynamic value: over DynamicRuntime.Enumerate(d), an
    /// IEnumerable&lt;object?&gt;, with the variable dynamic when it was
    /// written `var`. Null when the sequence is not dynamic.
    /// </summary>
    private Stmt? LateForeach(ForeachStmt fe, Type seq)
    {
        if (!_usesDynamic || !seq.Dynamic || seq.IsError) return null;
        TypeRef? declared = fe.Type is null || fe.Type.Name == "var"
            ? new TypeRef { Name = "dynamic", Line = fe.Line, Col = fe.Col }
            : fe.Type;
        ForeachStmt made = new()
        {
            Type = declared,
            Name = fe.Name,
            Sequence = LateCall("Enumerate", fe.Sequence, fe.Sequence),
            Body = fe.Body,
            Bindings = fe.Bindings,
            Line = fe.Line, Col = fe.Col,
        };
        return made;
    }
}

public sealed partial class Binder
{
    /// <summary>
    /// A call with a dynamic argument whose overloads are known here but
    /// more than one of which could take it (CheckCall, the candidates that
    /// accept the arguments with each dynamic one fitting any parameter):
    /// bound when the program runs, as C# binds it, by the argument's
    /// run-time type. The receiver and the arguments evaluated once, in
    /// order (each a PatternExpr's subject); then each candidate in order of
    /// specificity -- `moreSpecific(a, b)` saying a parameter type converts
    /// to another and not back -- tried by whether every dynamic argument
    /// fits its parameter (DynamicRuntime.Fits), the first that does
    /// called with them converted; none, DynamicRuntime.NoOverload. The
    /// result is dynamic. Null when there is nothing to choose between at
    /// run time (one candidate: the conversions are written where the
    /// arguments are passed), or the call is of a shape this does not take
    /// apart (a receiver moved into the arguments, `base`).
    /// </summary>
    private Type? LateOverloads(CallExpr c, List<MethodSymbol> candidates, List<Type> args, Func<Type, Type, bool> moreSpecific)
    {
        if (candidates.Count <= 1 || c.ReceiverAdded) return null;
        if (c.Target is MemberExpr moved && _r.Receivers.ContainsKey(moved))
        {
            // C#'s CS1973: an extension method is not dispatched dynamically.
            Error(c, $"'{moved.Name}' is an extension method and cannot be called with a dynamic argument; cast the argument");
            return Type.Error;
        }
        if (c.Args.Any(a => a is RefArgExpr or LambdaExpr) || c.ArgNames.Any(n => n is not null)) return null;

        List<int> late = Enumerable.Range(0, args.Count).Where(i => args[i].Dynamic).ToList();
        if (late.Count == 0) return null;

        // The receiver, when it is a value, is the outermost subject.
        Expr? receiver = null;
        string name;
        bool typeQualified = false;
        List<TypeRef> typeArgs;
        switch (c.Target)
        {
            case NameExpr bare:
                name = bare.Name;
                typeArgs = bare.TypeArgs;
                break;
            case MemberExpr m when m.Target is not BaseExpr:
                name = m.Name;
                typeArgs = m.TypeArgs;
                typeQualified = _r.Resolved.TryGetValue(m.Target, out Sym? on) && on is TypeNameSym;
                if (!typeQualified) receiver = m.Target;
                break;
            default:
                return null;
        }

        // MOST SPECIFIC FIRST: a candidate goes before another whose dynamic
        // parameters its own all convert to; the order they were found in
        // otherwise.
        List<MethodSymbol> ordered = new(candidates);
        bool Before(MethodSymbol a, MethodSymbol b)
        {
            bool strictly = false;
            foreach (int i in late)
            {
                Type pa = a.Params[i].Type, pb = b.Params[i].Type;
                if (pa.Equals(pb)) continue;
                if (!moreSpecific(pa, pb)) return false;
                strictly = true;
            }
            return strictly;
        }
        for (int i = 1; i < ordered.Count; i++)
        {
            for (int j = i; j > 0 && Before(ordered[j], ordered[j - 1]); j--)
            {
                MethodSymbol held = ordered[j];
                ordered[j] = ordered[j - 1];
                ordered[j - 1] = held;
            }
        }

        SubjectExpr Held(int argument) => new() { Outer = args.Count - 1 - argument, Line = c.Line, Col = c.Col };
        SubjectExpr HeldReceiver() => new() { Outer = args.Count, Line = c.Line, Col = c.Col };

        Expr chain = LateCall("NoOverload", c, LateText(name, c), LateArguments(late.Select(i => (Expr)Held(i)), c));
        for (int k = ordered.Count - 1; k >= 0; k--)
        {
            MethodSymbol m = ordered[k];
            Expr callee = c.Target switch
            {
                NameExpr => new NameExpr { Name = name, Line = c.Line, Col = c.Col },
                MemberExpr written when typeQualified => new MemberExpr { Target = written.Target, Name = name, Line = c.Line, Col = c.Col },
                _ => new MemberExpr { Target = HeldReceiver(), Name = name, Line = c.Line, Col = c.Col },
            };
            if (callee is NameExpr calledName) calledName.TypeArgs.AddRange(typeArgs);
            else ((MemberExpr)callee).TypeArgs.AddRange(typeArgs);

            CallExpr call = new() { Target = callee, Line = c.Line, Col = c.Col };
            Expr? test = null;
            for (int i = 0; i < args.Count; i++)
            {
                if (!args[i].Dynamic)
                {
                    call.Args.Add(Held(i));
                    continue;
                }
                Type want = m.Params[i].Type;
                if (RefOf(want) is not TypeRef spelt || LateTypeOf(want) is not TypeRef asked) return null;
                call.Args.Add(LateConverted(Held(i), spelt, asked, explicitly: false, c));
                // A REFERENCE OR A BOXED VALUE OF THE TYPE by a type test (a
                // derived class, an interface); null and a number that
                // widens by the binder.
                Expr fits = new BinaryExpr
                {
                    Op = BinOp.OrElse,
                    Left = new IsExpr
                    {
                        Operand = Held(i),
                        Type = new TypeRef { Name = asked.Name, Arguments = asked.Args, ArrayRank = asked.ArrayRank, ElementNullable = asked.ElementNullable, Line = c.Line, Col = c.Col },
                        Line = c.Line, Col = c.Col,
                    },
                    Right = LateCall("Fits", c, Held(i), new TypeOfExpr { Type = asked, Line = c.Line, Col = c.Col }),
                    Line = c.Line, Col = c.Col,
                };
                test = test is null ? fits : new BinaryExpr { Op = BinOp.AndAlso, Left = test, Right = fits, Line = c.Line, Col = c.Col };
            }
            Expr value = m.Returns.IsVoid
                ? new SequenceExpr
                {
                    Effect = new ExprStmt { Expr = call, Line = c.Line, Col = c.Col },
                    Value = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = c.Line, Col = c.Col },
                    Line = c.Line, Col = c.Col,
                }
                : new CastExpr { Type = new TypeRef { Name = "object", Nullable = true, Line = c.Line, Col = c.Col }, Operand = call, Line = c.Line, Col = c.Col };
            chain = new ConditionalExpr { Cond = test!, Then = value, Else = chain, Line = c.Line, Col = c.Col };
        }

        // The subjects, outermost first: the receiver, then each argument.
        Expr whole = chain;
        for (int i = args.Count - 1; i >= 0; i--)
        {
            whole = new PatternExpr { Subject = LateOperand(c.Args[i]), Test = whole, Line = c.Line, Col = c.Col };
        }
        if (receiver is not null)
        {
            whole = new PatternExpr { Subject = LateOperand(receiver), Test = whole, Line = c.Line, Col = c.Col };
        }
        _r.Rewrites[c] = whole;
        CheckExpr(whole);
        return Type.DynamicAny;
    }
}
