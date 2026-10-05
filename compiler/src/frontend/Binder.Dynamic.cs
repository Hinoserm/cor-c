#nullable enable
namespace Corsac.Lang;

/// <summary>
/// C#'S `dynamic`: an expression typed dynamic is an object here (Prim.Any,
/// Type.Dynamic set), and every operation on one is bound when the program
/// runs. Each is rewritten here into a call of the class library's binder,
/// System.DynamicRuntime (Microsoft.CSharp), as Roslyn lowers them onto
/// Microsoft.CSharp's call sites -- with C#'s semantics at run time:
///
///   d.X                  DynamicRuntime.GetMember(d, "X")
///   d.X = v              DynamicRuntime.SetMember(d, "X", v)
///   d.X op= v            DynamicRuntime.CompoundMember(d, "X", op, v)  (an event's += and -= too)
///   d.M&lt;T&gt;(a, n: b, ref c)  DynamicRuntime.InvokeMember(d, "M", call), the call a LateCall:
///                        the arguments, their names, how each is passed and the type
///                        arguments; a ref or out argument's new value written back after
///   d(a, b)              DynamicRuntime.Invoke(d, call): the delegate's own ILateInvocable
///   d[i, j]              DynamicRuntime.GetIndex(d, call); a store SetIndex, CompoundIndex
///   l op r               DynamicRuntime.Binary(op, l, r)     (either operand dynamic)
///   l &amp;&amp; r, l || r     l when operator false (true) says so, else Binary(&amp;, |), as C# 12.14
///   op d, d++, ++d       Unary, and the increments as a read, an op and a write
///   (T)d, T t = d        (T)DynamicRuntime.Convert(d, typeof(T), explicit)
///   if (d)               DynamicRuntime.IsTrue(d)
///   foreach (T x in d)   over DynamicRuntime.Enumerate(d), each converted to T
///   F(d), o.M(d, n: x)   a static or a known receiver's call with a dynamic argument:
///                        the overloads known here, the best chosen when it runs
///                        (DynamicValues.Choose, C# 12.6.4.3), LateOverloads
///
/// The op numbers are .NET's System.Linq.Expressions.ExpressionType. What
/// a class answers to a name at run time the compiler writes for it before
/// binding (DynamicProvider, DynamicDeclarations).
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

    /// <summary>Names for the temporaries a late call's by-reference arguments go through.</summary>
    private int _lateTemps;

    private const string LateBinder = "DynamicRuntime";

    // ---- the pieces a rewrite is made of ----------------------------------------------------

    private static LiteralExpr LateText(string text, Node at)
        => new() { Kind = Lit.Str, Text = text, Line = at.Line, Col = at.Col };

    private static LiteralExpr LateNumber(int value, Node at)
        => new() { Kind = Lit.Int, Text = value.ToString(), IntValue = value, Line = at.Line, Col = at.Col };

    private static LiteralExpr LateTruth(bool value, Node at)
        => new() { Kind = Lit.Bool, Text = value ? "true" : "false", IntValue = value ? 1 : 0, Line = at.Line, Col = at.Col };

    private static LiteralExpr LateNull(Node at)
        => new() { Kind = Lit.Null, Text = "null", Line = at.Line, Col = at.Col };

    private static TypeRef LateRef(string name, Node at, bool nullable = false, int rank = 0)
        => new() { Name = name, Nullable = nullable, ArrayRank = rank, Line = at.Line, Col = at.Col };

    /// <summary>`new E[] { … }`.</summary>
    private static NewExpr LateArray(TypeRef element, IEnumerable<Expr> values, Node at)
        => new() { Type = element, Elements = values.ToList(), Line = at.Line, Col = at.Col };

    private static TypeOfExpr LateTypeOfExpr(TypeRef t, Node at) => new() { Type = t, Line = at.Line, Col = at.Col };

    private static CallExpr LateCall(string method, Node at, params Expr[] arguments) => Call(LateBinder, method, at, arguments);

    /// <summary>What a rewrite of `e` stands on: what it was rewritten to already, or itself.</summary>
    private Expr LateOperand(Expr e) => _r.Rewrites.TryGetValue(e, out Expr? made) ? made : e;

    /// <summary>The ExpressionType of a two-operand operator, or -1 for one that cannot be bound late.</summary>
    private static int LateBinaryCode(BinOp op) => op switch
    {
        BinOp.Add => 0, BinOp.Sub => 42, BinOp.Mul => 26, BinOp.Div => 12, BinOp.Rem => 25,
        BinOp.And => 2, BinOp.Or => 36, BinOp.Xor => 14, BinOp.Shl => 19, BinOp.Shr => 41,
        BinOp.Eq => 13, BinOp.Ne => 35, BinOp.Lt => 20, BinOp.Gt => 15, BinOp.Le => 21, BinOp.Ge => 16,
        _ => -1,
    };

    /// <summary>How an argument is passed, as LateCall.Kinds counts: 0 by value, 1 ref, 2 out.</summary>
    private static int LateKind(Expr argument) => argument is RefArgExpr ra ? (ra.IsOut ? 2 : 1) : 0;

    /// <summary>
    /// `new System.Dynamic.LateCall(args, names, kinds, typeArgs, declared)`:
    /// each argument's value (a ref one's as it stands, an out one's null),
    /// the names where any is written, the passing where any is by
    /// reference, the type arguments where written, and -- for a call whose
    /// overloads are known here -- each argument's own type when it is not
    /// dynamic (what C# compares against when it says "exactly matches").
    /// </summary>
    private NewExpr LateCallObject(IReadOnlyList<Expr> values, IReadOnlyList<Expr> written, IReadOnlyList<string?> names,
                                   IReadOnlyList<TypeRef>? typeArgs, IReadOnlyList<TypeRef?>? declared, Node at)
    {
        NewExpr args = LateArray(LateRef("object", at, nullable: true), values, at);
        Expr nameList = names.Any(n => n is not null)
            ? LateArray(LateRef("string", at, nullable: true), names.Select(n => n is null ? (Expr)LateNull(at) : LateText(n, at)), at)
            : LateNull(at);
        Expr kindList = written.Any(a => a is RefArgExpr)
            ? LateArray(LateRef("int", at), written.Select(a => (Expr)LateNumber(LateKind(a), at)), at)
            : LateNull(at);
        Expr typeList = typeArgs is { Count: > 0 }
            ? LateArray(LateRef("System.Type", at), typeArgs.Select(t => (Expr)LateTypeOfExpr(t, at)), at)
            : LateNull(at);
        Expr declaredList = declared is not null && declared.Any(d => d is not null)
            ? LateArray(LateRef("System.Type", at, nullable: true), declared.Select(d => d is null ? (Expr)LateNull(at) : LateTypeOfExpr(d, at)), at)
            : LateNull(at);
        NewExpr made = new() { Type = LateRef("System.Dynamic.LateCall", at), Line = at.Line, Col = at.Col };
        made.Args.Add(args);
        made.Args.Add(nameList);
        made.Args.Add(kindList);
        made.Args.Add(typeList);
        made.Args.Add(declaredList);
        return made;
    }

    /// <summary>
    /// An argument's value as a late call hands it over: a ref one's
    /// variable as it stands, an out one's null; an `out var` declared here,
    /// dynamic, as C# declares one at a dynamically bound call.
    /// </summary>
    private Expr LateArgumentValue(Expr argument)
    {
        if (argument is not RefArgExpr ra) return LateOperand(argument);
        if (ra.Declare is null && ra.Name is not null && Lookup(ra.Name) is not LocalSym)
        {
            LocalSym declared = new(NewSlot(), Type.DynamicAny, ra.Name);
            Declare(ra, ra.Name, declared);
            _assigned.Add(declared);
            CheckExpr(ra.Target);
        }
        return ra.IsOut ? LateNull(ra) : LateOperand(ra.Target);
    }

    /// <summary>`x = (T)Convert(Element(call, i), typeof(T))`: a ref or out argument's new value, back in its variable.</summary>
    private Stmt LateWriteBack(RefArgExpr ra, Expr call, int argument)
    {
        Type held = _r.TypeOf(ra.Target);
        Expr value = LateCall("Element", ra, call, LateNumber(argument, ra));
        if (!held.Dynamic && !(held.Prim == Prim.Any && held.Symbol is null && !held.IsArray)
            && RefOf(held) is TypeRef spelt && LateTypeOf(held) is TypeRef asked)
        {
            value = LateConverted(value, spelt, asked, explicitly: false, ra);
        }
        return new ExprStmt
        {
            Expr = new AssignExpr { Target = ra.Target, Value = value, Line = ra.Line, Col = ra.Col },
            Line = ra.Line, Col = ra.Col,
        };
    }

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
            // C#'s CS0307: type arguments on something that is not a method.
            Error(m, $"'{m.Name}' cannot be used with type arguments: it is not called");
            return Type.Error;
        }
        CallExpr read = LateCall("GetMember", m, LateOperand(m.Target), LateText(m.Name, m));
        _r.Rewrites[m] = read;
        CheckExpr(read);
        return Type.DynamicAny;
    }

    /// <summary>
    /// The call of a member of a dynamic receiver, or of a dynamic value
    /// itself (CheckCall, once the target is checked). The receiver first,
    /// then the arguments into the LateCall, then the call; with a ref or
    /// out argument, each one's new value written back after it, and the
    /// call's value the expression's. Null when it is neither.
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
            if (argument is LambdaExpr)
            {
                // C#'s CS1977.
                Error(argument, "a lambda cannot be passed to a dynamically bound call without a cast to a delegate type");
                return Type.Error;
            }
            if (IsFunctionSource(argument))
            {
                // C#'s CS1976.
                Error(argument, "a method group cannot be passed to a dynamically bound call without a cast to a delegate type");
                return Type.Error;
            }
        }
        List<string?> names = new();
        for (int i = 0; i < c.Args.Count; i++) names.Add(i < c.ArgNames.Count ? c.ArgNames[i] : null);
        List<Expr> values = c.Args.Select(LateArgumentValue).ToList();
        List<TypeRef>? typeArgs = member ? ((MemberExpr)c.Target).TypeArgs : null;
        NewExpr call = LateCallObject(values, c.Args, names, typeArgs, null, c);
        Expr receiver = member ? LateOperand(((MemberExpr)c.Target).Target) : LateOperand(c.Target);
        string name = member ? ((MemberExpr)c.Target).Name : "";

        Expr made;
        if (!c.Args.Any(a => a is RefArgExpr))
        {
            made = member ? LateCall("InvokeMember", c, receiver, LateText(name, c), call) : LateCall("Invoke", c, receiver, call);
        }
        else
        {
            // THE RECEIVER, THE CALL'S ARGUMENTS, THE RESULT, each evaluated
            // once, in that order (subjects outermost first); then the
            // write-backs, and the result.
            SubjectExpr Held(int outer) => new() { Outer = outer, Line = c.Line, Col = c.Col };
            Block back = new() { Line = c.Line, Col = c.Col };
            for (int i = 0; i < c.Args.Count; i++)
            {
                if (c.Args[i] is RefArgExpr ra) back.Statements.Add(LateWriteBack(ra, Held(1), i));
            }
            SequenceExpr tail = new() { Effect = back, Value = Held(0), Line = c.Line, Col = c.Col };
            Expr invoke = member
                ? LateCall("InvokeMember", c, Held(1), LateText(name, c), Held(0))
                : LateCall("Invoke", c, Held(1), Held(0));
            made = new PatternExpr
            {
                Subject = receiver,
                Test = new PatternExpr
                {
                    Subject = call,
                    Test = new PatternExpr { Subject = invoke, Test = tail, Line = c.Line, Col = c.Col },
                    Line = c.Line, Col = c.Col,
                },
                Line = c.Line, Col = c.Col,
            };
        }
        _r.Rewrites[c] = made;
        CheckExpr(made);
        return Type.DynamicAny;
    }

    /// <summary>An indexing of a dynamic receiver: GetIndex, which a store replaces.</summary>
    private Type LateIndex(IndexExpr ix)
    {
        _lateIndexes.Add(ix);
        foreach (Expr a in ix.Args) CheckExpr(a);
        CallExpr read = LateCall("GetIndex", ix, LateOperand(ix.Target), LateKeys(ix, ix));
        _r.Rewrites[ix] = read;
        CheckExpr(read);
        return Type.DynamicAny;
    }

    /// <summary>An indexer's keys as a LateCall.</summary>
    private NewExpr LateKeys(IndexExpr ix, Node at)
    {
        List<string?> names = ix.Args.Select(_ => (string?)null).ToList();
        return LateCallObject(ix.Args.Select(LateOperand).ToList(), ix.Args, names, null, null, at);
    }

    /// <summary>
    /// An assignment whose target is bound late -- a member or an element of
    /// a dynamic receiver -- or a compound one whose operator is: to a
    /// dynamic variable, or into any variable from a dynamic value. Null
    /// otherwise.
    /// </summary>
    private Type? LateAssignment(AssignExpr a, Type target)
    {
        Expr value = LateOperand(a.Value);
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
            NewExpr keys = LateKeys(index, a);
            CallExpr made = a.Op is BinOp op
                ? LateCall("CompoundIndex", a, receiver, keys, LateNumber(LateCompoundCode(op, a), a), value)
                : LateCall("SetIndex", a, receiver, keys, value);
            _r.Rewrites[a] = made;
            CheckExpr(made);
            return Type.DynamicAny;
        }
        // AND `x op= d`, or `d op= v`: the operator bound late, its result
        // converted to x's type (C# 12.21.4).
        bool lateValue = _r.ExprType.TryGetValue(a.Value, out Type? valueType) && valueType.Dynamic;
        if (a.Op is BinOp compound && (target.Dynamic || lateValue) && !target.IsError)
        {
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
                        Right = LateNull(b),
                        Line = b.Line, Col = b.Col,
                    },
                    Then = new CastExpr { Type = LateRef("object", b, nullable: true), Operand = LateOperand(b.Right), Line = b.Line, Col = b.Col },
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
            // `>>>` has no ExpressionType; C# refuses it on a dynamic operand too.
            Error(b, $"operator '{b.Op}' cannot be applied to a dynamic operand");
            return Type.Error;
        }
        CallExpr made = LateCall("Binary", b, LateNumber(code, b), LateOperand(b.Left), LateOperand(b.Right));
        _r.Rewrites[b] = made;
        CheckExpr(made);
        return Type.DynamicAny;
    }

    /// <summary>
    /// `&&` and `||` with a dynamic operand, as C# 12.14 binds them: the
    /// left operand once; `x && y` is x when operator false says so of it,
    /// else `x &amp; y` bound late; `x || y` is x when operator true says so,
    /// else `x | y`. The right operand only when the left does not settle
    /// it. The result is dynamic. Null when neither is dynamic.
    /// </summary>
    private Type? LateLogical(BinaryExpr b)
    {
        if (!_usesDynamic) return null;
        if (!DynamicOperand(b.Left) && !DynamicOperand(b.Right)) return null;
        bool and = b.Op == BinOp.AndAlso;
        PatternExpr made = new()
        {
            Subject = new CastExpr { Type = LateRef("object", b, nullable: true), Operand = b.Left, Line = b.Line, Col = b.Col },
            Test = new ConditionalExpr
            {
                Cond = LateCall(and ? "IsFalse" : "IsTrue", b, new SubjectExpr { Line = b.Line, Col = b.Col }),
                Then = new SubjectExpr { Line = b.Line, Col = b.Col },
                Else = LateCall("Binary", b, LateNumber(and ? 2 : 36, b), new SubjectExpr { Line = b.Line, Col = b.Col }, b.Right),
                Line = b.Line, Col = b.Col,
            },
            Line = b.Line, Col = b.Col,
        };
        _r.Rewrites[b] = made;
        CheckExpr(made);
        return Type.DynamicAny;
    }

    /// <summary>
    /// Whether an operand of `&&` or `||` is dynamic. A chain of them is
    /// dynamic only when one of its own operands is, so the chain is answered
    /// from its leaves: peeking the whole left side at every link checked
    /// `a && b && c && ...` twice per link, exponential in its length.
    /// </summary>
    private bool DynamicOperand(Expr e)
        => e is BinaryExpr { Op: BinOp.AndAlso or BinOp.OrElse } chain
            ? DynamicOperand(chain.Left) || DynamicOperand(chain.Right)
            : Peek(e).Dynamic;

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
                made = LateCall("IncrementIndex", u, LateOperand(index.Target), LateKeys(index, u), LateNumber(code, u), LateTruth(postfix, u));
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

    /// <summary>
    /// The type `typeof` names for a conversion to `to`: a reference without
    /// its `?`, a Nullable's underlying value type (the binder converts to
    /// that, a null being let through by ConvertNullable).
    /// </summary>
    private static TypeRef? LateTypeOf(Type to) => RefOf(to.AsNonNullable());

    /// <summary>
    /// `(T)DynamicRuntime.Convert(value, typeof(T), explicitly)`; to a type
    /// written with `?`, ConvertNullable, which lets a null through to a
    /// Nullable as to a reference.
    /// </summary>
    private static CastExpr LateConverted(Expr value, TypeRef to, TypeRef asked, bool explicitly, Node at)
        => new()
        {
            Type = to,
            Operand = LateCall(to.Nullable && to.ArrayRank == 0 ? "ConvertNullable" : "Convert", at, value, LateTypeOfExpr(asked, at), LateTruth(explicitly, at)),
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
    /// `foreach` over a dynamic value (C# 13.9.5 over dynamic): over
    /// DynamicRuntime.Enumerate(d), the variable dynamic when it was written
    /// `var`, and each element converted explicitly to the type written
    /// otherwise. Null when the sequence is not dynamic.
    /// </summary>
    private Stmt? LateForeach(ForeachStmt fe, Type seq)
    {
        if (!_usesDynamic || !seq.Dynamic || seq.IsError) return null;
        Expr sequence = LateCall("Enumerate", fe.Sequence, fe.Sequence);
        if (fe.Type is null || fe.Type.Name is "var" or "dynamic" || fe.Bindings is not null)
        {
            return new ForeachStmt
            {
                Type = fe.Bindings is null ? LateRef("dynamic", fe) : fe.Type,
                Name = fe.Name, Sequence = sequence, Body = fe.Body, Bindings = fe.Bindings,
                Line = fe.Line, Col = fe.Col,
            };
        }
        string element = "__late_element" + _lateTemps++;
        Block body = new() { Line = fe.Line, Col = fe.Col };
        body.Statements.Add(new LocalDecl
        {
            Type = fe.Type, Name = fe.Name,
            Init = new CastExpr { Type = fe.Type, Operand = new NameExpr { Name = element, Line = fe.Line, Col = fe.Col }, Line = fe.Line, Col = fe.Col },
            Line = fe.Line, Col = fe.Col,
        });
        body.Statements.Add(fe.Body);
        return new ForeachStmt
        {
            Type = LateRef("dynamic", fe), Name = element, Sequence = sequence, Body = body,
            Line = fe.Line, Col = fe.Col,
        };
    }

    // ---- a delegate's own late invocation -----------------------------------------------------

    /// <summary>
    /// A closure's class takes its delegate interface's default
    /// implementations (C# 8) as any class implementing it would
    /// (AssignSlots): ILateInvocable's __LateInvoke, which every delegate
    /// type has (DynamicDeclarations), so that the binder calls the
    /// delegate itself.
    /// </summary>
    private void ImplementDefaults(TypeSymbol closure)
    {
        foreach (TypeSymbol iface in AllInterfaces(closure))
        {
            string ifaceName = ExplicitName(iface);
            foreach (MethodSymbol want in iface.Methods)
            {
                if (want.Static || want.TypeParams.Count > 0 || want.Decl?.LocalCopy == true || want.VtableSlot < 0) continue;
                if (closure.Methods.Any(m => m.VtableSlot == want.VtableSlot) || closure.InterfaceImplementations.ContainsKey(want.VtableSlot)) continue;
                MethodSymbol? body = AllInterfaces(closure)
                    .Select(face => face.Methods.FirstOrDefault(m => m.ExplicitMember == want.Name && m.ExplicitInterface == ifaceName
                                                                  && m.Decl?.Body is not null && MethodSignatures.Implements(m, want)))
                    .FirstOrDefault(found => found is not null)
                    ?? (want.Decl?.Body is not null ? want : null);
                if (body is not null) closure.InterfaceImplementations[want.VtableSlot] = body;
            }
        }
    }
}

public sealed partial class Binder
{
    /// <summary>
    /// A call whose overloads are known here but an argument is dynamic
    /// (CheckCall, once the arguments and the group are known and before
    /// either is rearranged): bound when the program runs, among the
    /// candidates that could take the arguments, as C# binds it (12.6.4).
    ///
    /// The receiver (a value's) and every argument evaluated once, in order
    /// (each a PatternExpr's subject). Each candidate's parameters are
    /// matched to the arguments here -- positions and names are written
    /// down -- a `params` method's expanded form a candidate of its own,
    /// and an argument the static types already refuse rules a candidate
    /// out. When the program runs, each candidate applies when every
    /// dynamic argument converts to its parameter (an `is` test, or
    /// DynamicRuntime.Fits for null and a wider number), and of those that
    /// apply DynamicValues.Choose takes the better function member by C#'s
    /// rules, with the better conversion targets worked out here
    /// (`implicitly`, the checker's own) and each static argument compared
    /// by its own type. The chosen one is called with the dynamic arguments
    /// converted (Convert), an omitted parameter's default filled in and a
    /// ref or out argument through a temporary of the parameter's type
    /// written back after. None, or none better than the rest: the
    /// binder's error (NoOverload). The result is dynamic.
    ///
    /// A generic method is a candidate when its type arguments are written
    /// at the call (`F&lt;int&gt;(d)`); one whose type arguments would be
    /// inferred from a dynamic argument's run-time type takes no part,
    /// there being no method to call for a type no code was made for.
    ///
    /// Null -- the call left to the checker's own choice -- when one
    /// candidate is left and nothing is named or passed by reference (its
    /// dynamic arguments are converted where they are passed).
    /// </summary>
    private Type? LateOverloads(CallExpr c, MethodGroupSym group, List<Type> allArgs,
                                Func<Type, Type, bool> implicitly, Func<Type, Type, Expr, bool> fits)
    {
        MemberExpr? through = c.Target as MemberExpr;
        bool moved = through is not null && _r.Receivers.ContainsKey(through);
        if (moved && group.Methods.Any(m => m.Decl?.Params.FirstOrDefault()?.IsThis == true))
        {
            // C#'s CS1973.
            Error(c, $"'{through!.Name}' is an extension method and cannot be dynamically dispatched; cast the dynamic arguments, or call it without the extension method syntax");
            return Type.Error;
        }
        int skip = moved && c.ReceiverAdded ? 1 : 0;
        List<Expr> written = c.Args.Skip(skip).ToList();
        List<Type> args = allArgs.Skip(skip).ToList();
        if (written.Any(a => a is LambdaExpr))
        {
            // C#'s CS1977.
            Error(c, "a lambda cannot be passed to a dynamically bound call without a cast to a delegate type");
            return Type.Error;
        }
        if (written.Any(IsFunctionSource))
        {
            // C#'s CS1976.
            Error(c, "a method group cannot be passed to a dynamically bound call without a cast to a delegate type");
            return Type.Error;
        }
        int n = written.Count;
        List<string?> names = new();
        for (int i = 0; i < n; i++) names.Add(i + skip < c.ArgNames.Count ? c.ArgNames[i + skip] : null);
        bool named = names.Any(x => x is not null);
        bool byReference = written.Any(a => a is RefArgExpr);
        int first = moved ? 1 : 0;

        // A GENERIC RECEIVER'S MEMBERS take its type arguments: List<int>'s
        // Add takes an int.
        Dictionary<string, Type>? fromReceiver = through is not null && !moved && group.Methods.Count > 0
                                                 && _r.ExprType.TryGetValue(through.Target, out Type? receiverType)
                                                     ? Received(receiverType, group.Methods[0].Owner)
                                                     : null;
        List<TypeRef> explicitTypes = through?.TypeArgs ?? (c.Target as NameExpr)?.TypeArgs ?? new List<TypeRef>();
        Type ParameterType(MethodSymbol m, int p)
        {
            Type t = fromReceiver is null ? m.Params[p].Type : Close(m.Params[p].Type, fromReceiver);
            if (m.TypeParams.Count > 0 && explicitTypes.Count == m.TypeParams.Count)
            {
                Dictionary<string, Type> given = new(StringComparer.Ordinal);
                for (int k = 0; k < explicitTypes.Count; k++) given[m.TypeParams[k]] = Resolve(explicitTypes[k], _thisType);
                t = Close(t, given);
            }
            return t;
        }

        // EACH CANDIDATE'S PARAMETERS MATCHED TO THE ARGUMENTS (C# 12.6.2):
        // map[p] the argument, -1 a default, -2 the expanded rest; p counted
        // after a receiver that is the method's first parameter.
        List<(MethodSymbol Method, Type[] Wants, int[] Map, bool Expanded)> candidates = new();
        foreach (MethodSymbol m in group.Methods)
        {
            if (m.IsCtor || m.Params.Count < first) continue;
            if (m.TypeParams.Count > 0 && explicitTypes.Count != m.TypeParams.Count) continue;
            if (m.TypeParams.Count == 0 && explicitTypes.Count > 0) continue;
            Type[] wants = Enumerable.Range(first, m.Params.Count - first).Select(p => ParameterType(m, p)).ToArray();
            if (wants.Any(Unmade)) continue;
            if (LateMap(m, first, wants, names, written, args, fits, expanded: false) is int[] normal) candidates.Add((m, wants, normal, false));
            if (m.Params.Count > first && m.Params[^1].IsParams && wants[^1].IsArray
                && LateMap(m, first, wants, names, written, args, fits, expanded: true) is int[] spread) candidates.Add((m, wants, spread, true));
        }
        if (candidates.Count == 0)
        {
            Error(c, $"no overload of '{group.Methods[0].Name}' takes these arguments");
            return Type.Error;
        }
        if (candidates.Count == 1 && !named && !byReference && !candidates[0].Expanded) return null;

        Expr? receiver = null;
        bool typeQualified = false;
        switch (c.Target)
        {
            case NameExpr:
                break;
            case MemberExpr m when m.Target is BaseExpr:
                Error(c, "a base call with a dynamic argument cannot be dynamically dispatched; cast the dynamic arguments");
                return Type.Error;
            case MemberExpr m:
                typeQualified = _r.Resolved.TryGetValue(m.Target, out Sym? on) && on is TypeNameSym;
                if (!typeQualified) receiver = m.Target;
                break;
            default:
                return null;
        }

        // THE GROUP the choice is made over: every parameter type once, which
        // of them is the better target than which, each candidate's.
        List<Type> types = new();
        int TypeId(Type t)
        {
            int at = types.FindIndex(x => x.Equals(t) && x.Nullable == t.Nullable);
            if (at >= 0) return at;
            types.Add(t);
            return types.Count - 1;
        }
        List<int[]> parameterIds = new();
        List<int> elementIds = new();
        foreach ((MethodSymbol _, Type[] wants, int[] _, bool expanded) in candidates)
        {
            parameterIds.Add(wants.Select(TypeId).ToArray());
            elementIds.Add(expanded ? TypeId(wants[^1].Element!) : -1);
        }
        foreach (Type t in types)
        {
            if (LateTypeOf(t) is null || RefOf(t) is null)
            {
                Error(c, $"'{t}' cannot be a parameter of a dynamically bound call");
                return Type.Error;
            }
        }
        byte[] better = new byte[types.Count * types.Count];
        for (int a = 0; a < types.Count; a++)
            for (int b = 0; b < types.Count; b++)
                if (a != b && LateBetterTarget(types[a], types[b], implicitly)) better[a * types.Count + b] = 1;

        Node at = c;
        NewExpr groupObject = new() { Type = LateRef("System.Dynamic.LateGroup", at), Line = at.Line, Col = at.Col };
        groupObject.Args.Add(LateArray(LateRef("System.Type", at), types.Select(t => (Expr)LateTypeOfExpr(LateTypeOf(t)!, at)), at));
        groupObject.Args.Add(LateArray(LateRef("int", at, rank: 1), parameterIds.Select(ids => (Expr)LateArray(LateRef("int", at), ids.Select(id => (Expr)LateNumber(id, at)), at)), at));
        groupObject.Args.Add(LateArray(LateRef("int", at), elementIds.Select(id => (Expr)LateNumber(id, at)), at));
        groupObject.Args.Add(LateArray(LateRef("bool", at), candidates.Select(k => (Expr)LateTruth(k.Method.TypeParams.Count > 0, at)), at));
        groupObject.Args.Add(LateArray(LateRef("byte", at), better.Select(v => (Expr)new CastExpr { Type = LateRef("byte", at), Operand = LateNumber(v, at), Line = at.Line, Col = at.Col }), at));
        groupObject.Args.Add(LateArray(LateRef("string", at), candidates.Select(k => (Expr)LateText(LateDisplay(k.Method), at)), at));

        // THE SUBJECTS, outermost first: the receiver, each argument, the
        // LateCall, the choice. A SubjectExpr names one by how many levels
        // out it is from where it is read.
        int firstArgument = receiver is null ? 0 : 1;
        int callAt = firstArgument + n, choiceAt = callAt + 1;
        SubjectExpr Held(int position, int depth) => new() { Outer = depth - 1 - position, Line = at.Line, Col = at.Col };

        // Read where the LateCall is the subject being made: depth callAt.
        List<Expr> heldValues = new();
        List<TypeRef?> declared = new();
        for (int i = 0; i < n; i++)
        {
            heldValues.Add(Held(firstArgument + i, callAt));
            declared.Add(args[i].Dynamic || args[i].IsError || written[i] is RefArgExpr ? null : LateTypeOf(args[i]));
        }
        NewExpr callObject = LateCallObject(heldValues, written, names, null, declared, at);

        // Read where the choice is the subject being made: depth choiceAt.
        List<Expr> applies = new();
        foreach ((MethodSymbol _, Type[] wants, int[] map, bool expanded) in candidates)
        {
            Expr? test = null;
            for (int i = 0; i < n; i++)
            {
                if (!args[i].Dynamic || written[i] is RefArgExpr) continue;
                Type want = LateParameterOf(wants, map, expanded, i);
                if (want.Prim == Prim.Any && want.Symbol is null && !want.IsArray) continue;
                Expr one = LateFits(Held(firstArgument + i, choiceAt), want, at);
                test = test is null ? one : new BinaryExpr { Op = BinOp.AndAlso, Left = test, Right = one, Line = at.Line, Col = at.Col };
            }
            applies.Add(test ?? LateTruth(true, at));
        }
        CallExpr choose = Call("DynamicValues", "Choose", at,
            Held(callAt, choiceAt),
            groupObject,
            LateArray(LateRef("int", at, nullable: true, rank: 1), candidates.Select(k => (Expr)LateArray(LateRef("int", at), k.Map.Select(v => (Expr)LateNumber(v, at)), at)), at),
            LateArray(LateRef("bool", at), applies, at));

        // THE CHAIN on the choice, read inside the choice's subject.
        int depth = choiceAt + 1;
        Expr chain = LateCall("NoOverload", at, Held(choiceAt, depth), groupObject, Held(callAt, depth));
        for (int k = candidates.Count - 1; k >= 0; k--)
        {
            (MethodSymbol method, Type[] wants, int[] map, bool expanded) = candidates[k];
            Expr value = LateCandidateCall(c, method, first, wants, map, expanded, typeQualified, written, args,
                                           i => Held(firstArgument + i, depth), () => Held(0, depth));
            chain = new ConditionalExpr
            {
                Cond = new BinaryExpr { Op = BinOp.Eq, Left = Held(choiceAt, depth), Right = LateNumber(k, at), Line = at.Line, Col = at.Col },
                Then = value, Else = chain, Line = at.Line, Col = at.Col,
            };
        }

        Expr whole = new PatternExpr
        {
            Subject = callObject,
            Test = new PatternExpr { Subject = choose, Test = chain, Line = at.Line, Col = at.Col },
            Line = at.Line, Col = at.Col,
        };
        for (int i = n - 1; i >= 0; i--)
        {
            // EACH AS ITS OWN TYPE, so a static one is passed as written (a
            // short to an int parameter widened, not unboxed); a null as an
            // object, having no type to hold.
            Expr value = written[i] is RefArgExpr ? LateArgumentValue(written[i])
                       : args[i].Prim == Prim.NullLiteral ? new CastExpr { Type = LateRef("object", at, nullable: true), Operand = LateOperand(written[i]), Line = at.Line, Col = at.Col }
                       : LateOperand(written[i]);
            whole = new PatternExpr { Subject = value, Test = whole, Line = at.Line, Col = at.Col };
        }
        if (receiver is not null)
        {
            whole = new PatternExpr { Subject = LateOperand(receiver), Test = whole, Line = at.Line, Col = at.Col };
        }
        _r.Rewrites[c] = whole;
        CheckExpr(whole);
        return Type.DynamicAny;
    }

    /// <summary>
    /// A candidate's parameters (those after `first`, wanting `wants`)
    /// matched to the call's arguments: positional ones in place, named ones
    /// by name, a gap only where a default fills it; in the expanded form
    /// the arguments past the fixed parameters the array's elements, none
    /// named. Each argument passed as its parameter is taken (ref for ref,
    /// out for out, a ref one's variable of the parameter's own type), and
    /// each that is not dynamic one the static types already take (`fits`).
    /// Null when they do not match.
    /// </summary>
    private static int[]? LateMap(MethodSymbol m, int first, Type[] wants, List<string?> names, List<Expr> written, List<Type> args,
                                  Func<Type, Type, Expr, bool> fits, bool expanded)
    {
        int count = wants.Length;
        int fixedCount = expanded ? count - 1 : count;
        int[] map = new int[count];
        Array.Fill(map, -1);
        if (expanded) map[^1] = -2;
        bool sawName = false;
        for (int i = 0; i < names.Count; i++)
        {
            int p;
            if (names[i] is string name)
            {
                sawName = true;
                p = m.Params.FindIndex(x => x.Name == name) - first;
                if (p < 0 || p >= fixedCount) return null;
            }
            else
            {
                // C# 7.2: a positional argument after a named one only where
                // the named one stood in its own position.
                p = i;
                if (sawName && (p >= count || map[p] != -1)) return null;
                if (p >= fixedCount)
                {
                    if (!expanded || written[i] is RefArgExpr) return null;
                    if (!args[i].Dynamic && !args[i].IsError && !fits(args[i], wants[^1].Element!, written[i])) return null;
                    continue;
                }
            }
            if (map[p] != -1) return null;
            map[p] = i;
            ParamSymbol param = m.Params[p + first];
            bool reference = written[i] is RefArgExpr;
            bool wantsReference = param.ByRef && !param.ReadOnly;
            if (reference != wantsReference) return null;
            if (!reference && !args[i].Dynamic && !args[i].IsError && !fits(args[i], wants[p], written[i])) return null;
            if (reference && !args[i].Dynamic && !args[i].IsError
                && !args[i].AsNonNullable().Equals(wants[p].AsNonNullable())
                && !MethodSignatures.SameType(args[i].AsNonNullable(), wants[p].AsNonNullable())) return null;
        }
        for (int p = 0; p < fixedCount; p++)
        {
            if (map[p] == -1 && m.Decl?.Params.ElementAtOrDefault(p + first)?.Default is null) return null;
        }
        return map;
    }

    /// <summary>The parameter type argument `i` meets in a candidate (the array's element in the expanded rest).</summary>
    private static Type LateParameterOf(Type[] wants, int[] map, bool expanded, int i)
    {
        for (int p = 0; p < map.Length; p++) if (map[p] == i) return wants[p];
        return expanded ? wants[^1].Element! : Type.Any;
    }

    /// <summary>
    /// `value is T || DynamicRuntime.Fits(value, typeof(T))`; for a
    /// Nullable T?, `value == null ||` that of T.
    /// </summary>
    private static Expr LateFits(SubjectExpr value, Type want, Node at)
    {
        TypeRef asked = LateTypeOf(want)!;
        if (want.IsNullableValue)
        {
            return new BinaryExpr
            {
                Op = BinOp.OrElse,
                Left = new BinaryExpr { Op = BinOp.Eq, Left = new SubjectExpr { Outer = value.Outer, Line = at.Line, Col = at.Col }, Right = LateNull(at), Line = at.Line, Col = at.Col },
                Right = LateFits(value, want.Underlying, at),
                Line = at.Line, Col = at.Col,
            };
        }
        return new BinaryExpr
        {
            Op = BinOp.OrElse,
            Left = new IsExpr
            {
                Operand = value,
                Type = new TypeRef { Name = asked.Name, Arguments = asked.Args, ArrayRank = asked.ArrayRank, ElementNullable = asked.ElementNullable, Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            },
            Right = LateCall("Fits", at, new SubjectExpr { Outer = value.Outer, Line = at.Line, Col = at.Col }, LateTypeOfExpr(asked, at)),
            Line = at.Line, Col = at.Col,
        };
    }

    /// <summary>
    /// Whether T1 is the better conversion target (C# 12.6.4.7): an
    /// implicit conversion from T1 to T2 and none back, or T1 a signed
    /// integral type and T2 an unsigned one (sbyte over byte, ushort, uint,
    /// ulong; short over ushort, uint, ulong; int over uint, ulong; long
    /// over ulong).
    /// </summary>
    private static bool LateBetterTarget(Type t1, Type t2, Func<Type, Type, bool> implicitly)
    {
        bool to = implicitly(t1, t2), back = implicitly(t2, t1);
        if (to && !back) return true;
        if (t1.Symbol is not null || t2.Symbol is not null || t1.IsNullableValue || t2.IsNullableValue) return false;
        return (t1.Prim, t2.Prim) switch
        {
            (Prim.I8, Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64) => true,
            (Prim.I16, Prim.U16 or Prim.U32 or Prim.U64) => true,
            (Prim.I32, Prim.U32 or Prim.U64) => true,
            (Prim.I64, Prim.U64) => true,
            _ => false,
        };
    }

    /// <summary>A method as C#'s messages write it: `Owner.Name(int, string)`.</summary>
    private static string LateDisplay(MethodSymbol m)
        => m.Owner.Name + "." + m.Name + "(" + string.Join(", ", m.Params.Select(p => (p.ByRef ? "ref " : "") + p.Type)) + ")";

    /// <summary>
    /// One candidate's call: its parameters in order -- a dynamic argument
    /// converted to the parameter, a static one as it is, named ones put
    /// back in place, a default where nothing was given, the expanded rest
    /// an array -- a by-reference one through a temporary of the
    /// parameter's own type written back after; its value as an object,
    /// null for a void method. The callee is written as the call wrote it,
    /// so the checker resolves the same group and takes the one candidate
    /// that the arguments, now each of its parameter's type, fit exactly.
    /// </summary>
    private Expr LateCandidateCall(CallExpr c, MethodSymbol m, int first, Type[] wants, int[] map, bool expanded, bool typeQualified,
                                   List<Expr> written, List<Type> args, Func<int, SubjectExpr> held, Func<SubjectExpr> heldReceiver)
    {
        Node at = c;
        Expr callee;
        switch (c.Target)
        {
            case NameExpr bare:
            {
                NameExpr again = new() { Name = bare.Name, Line = at.Line, Col = at.Col };
                again.WritableTypeArgs.AddRange(bare.TypeArgs);
                callee = again;
                break;
            }
            case MemberExpr member:
            {
                MemberExpr again = new() { Target = typeQualified ? member.Target : heldReceiver(), Name = member.Name, Line = at.Line, Col = at.Col };
                again.WritableTypeArgs.AddRange(member.TypeArgs);
                callee = again;
                break;
            }
            default:
                throw new InvalidOperationException("a late candidate's target");
        }
        CallExpr call = new() { Target = callee, Line = at.Line, Col = at.Col };
        List<Stmt> before = new(), after = new();

        Expr Passed(int i, Type want)
        {
            TypeRef spelt = RefOf(want)!;
            if (!args[i].Dynamic)
            {
                // AS ITS PARAMETER'S TYPE, so the checker takes this candidate
                // and no other of the group.
                return new CastExpr { Type = spelt, Operand = held(i), Line = at.Line, Col = at.Col };
            }
            return LateConverted(held(i), spelt, LateTypeOf(want)!, explicitly: false, at);
        }

        for (int p = 0; p < wants.Length; p++)
        {
            Type want = wants[p];
            int i = map[p];
            if (i == -2)
            {
                Type element = want.Element!;
                List<Expr> items = new();
                for (int k = 0; k < written.Count; k++)
                {
                    if (!map.Contains(k)) items.Add(Passed(k, element));
                }
                call.Args.Add(LateArray(RefOf(element)!, items, at));
                call.WritableArgNames.Add(null);
                continue;
            }
            if (i == -1)
            {
                call.Args.Add(Written(m, m.Decl!.Params[p + first]));
                call.WritableArgNames.Add(null);
                continue;
            }
            if (written[i] is RefArgExpr ra)
            {
                // THROUGH A TEMPORARY OF THE PARAMETER'S TYPE: what the
                // variable holds goes in (for ref), and what the call leaves
                // comes back.
                string temp = "__late_ref" + _lateTemps++;
                TypeRef tempType = RefOf(want)!;
                Expr initial = ra.IsOut
                    ? new DefaultExpr { Type = tempType, Line = at.Line, Col = at.Col }
                    : args[i].Dynamic || args[i].IsError
                        ? LateConverted(LateOperand(ra.Target), tempType, LateTypeOf(want)!, explicitly: false, at)
                        : LateOperand(ra.Target);
                before.Add(new LocalDecl { Type = tempType, Name = temp, Init = initial, Line = at.Line, Col = at.Col });
                call.Args.Add(new RefArgExpr { Target = new NameExpr { Name = temp, Line = at.Line, Col = at.Col }, IsOut = ra.IsOut, Line = at.Line, Col = at.Col });
                call.WritableArgNames.Add(null);
                Type variable = _r.TypeOf(ra.Target);
                Expr back = new NameExpr { Name = temp, Line = at.Line, Col = at.Col };
                if (!variable.IsError && !variable.Dynamic && RefOf(variable) is TypeRef variableType) back = new CastExpr { Type = variableType, Operand = back, Line = at.Line, Col = at.Col };
                after.Add(new ExprStmt
                {
                    Expr = new AssignExpr { Target = ra.Target, Value = back, Line = at.Line, Col = at.Col },
                    Line = at.Line, Col = at.Col,
                });
                continue;
            }
            call.Args.Add(Passed(i, want));
            call.WritableArgNames.Add(null);
        }

        bool returns = !m.Returns.IsVoid;
        Expr Result() => returns
            ? new CastExpr { Type = LateRef("object", at, nullable: true), Operand = call, Line = at.Line, Col = at.Col }
            : new SequenceExpr { Effect = new ExprStmt { Expr = call, Line = at.Line, Col = at.Col }, Value = LateNull(at), Line = at.Line, Col = at.Col };
        if (before.Count == 0 && after.Count == 0) return Result();

        // `T t = …; object? r = M(ref t); x = t; r`, as sequences: each
        // declaration's name belongs to the scope the expression is in.
        string result = "__late_result" + _lateTemps++;
        Expr tail = new NameExpr { Name = result, Line = at.Line, Col = at.Col };
        for (int k = after.Count - 1; k >= 0; k--) tail = new SequenceExpr { Effect = after[k], Value = tail, Line = at.Line, Col = at.Col };
        Stmt invoke = new LocalDecl { Type = LateRef("object", at, nullable: true), Name = result, Init = Result(), Line = at.Line, Col = at.Col };
        tail = new SequenceExpr { Effect = invoke, Value = tail, Line = at.Line, Col = at.Col };
        for (int k = before.Count - 1; k >= 0; k--) tail = new SequenceExpr { Effect = before[k], Value = tail, Line = at.Line, Col = at.Col };
        return tail;
    }
}
