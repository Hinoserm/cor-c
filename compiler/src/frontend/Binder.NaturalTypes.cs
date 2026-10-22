#nullable enable
namespace Corsac.Lang;

/// <summary>
/// THE NATURAL TYPE OF A LAMBDA OR A METHOD GROUP (C# 10): `var f = (int x)
/// => x + 1;` is a Func&lt;int, int&gt;, `var a = () =&gt; { };` an Action,
/// `var g = Twice;` the Func of the one method Twice is. C#'s rules: a
/// lambda has one when every parameter's type is written and what it returns
/// can be worked out -- written in front of it, or the type of its body, or
/// the best common type of its returns -- and a method group when it is one
/// method, not generic. Anything else "could not be inferred" (CS8917).
///
/// C# 10 gives one where nothing else says which delegate is meant: `var`,
/// and a conversion to object (there is no System.Delegate here, nor
/// expression trees).
///
/// The type is SPELT on the lambda or the method group (Expr.NaturalType),
/// as if written there, and checked as a target one is from then on. Where
/// no source names that Func or Action, nothing has made it yet: the round
/// is marked to go again (BindResult.Reexpand), the monomorphiser makes it
/// from the spelling, and the next binding has it. Until then it is an error
/// nobody is told of.
///
/// WHERE NO FUNC OR ACTION CAN SAY IT -- a parameter passed by reference, a
/// result returned by one, more than sixteen parameters -- C# synthesises a
/// delegate, and so does this: one of that shape, generic over the types,
/// declared once a unit (Parser.AnonymousDelegates), which the driver adds
/// when the binding asks for it (BindResult.AnonymousDelegates).
/// </summary>
public sealed partial class Binder
{
    /// <summary>The variable's natural delegate type, its initialiser checked against it.</summary>
    private Type NaturalDelegate(LocalDecl d, Sym? group)
    {
        if (d.Init is null || NaturalTypeOf(d.Init, group, $"'{d.Name}'") is not Type type)
        {
            return Type.Error;
        }

        if (d.Init is LambdaExpr held)
        {
            CheckLambda(held, type);
        }
        else
        {
            Type had = group is null ? CheckExpr(d.Init) : _r.TypeOf(d.Init);
            CheckAssignable(had, type, d.Init, What.InitialiserFor(d.Name));
        }
        return type;
    }

    /// <summary>
    /// A lambda's or a method group's natural type, spelt on it; null when it
    /// has none (said so) or is not made yet (the round goes again, quietly).
    /// </summary>
    private Type? NaturalTypeOf(Expr source, Sym? group, string what)
    {
        if (source.NaturalType is null)
        {
            string? why = null;
            TypeRef? spelt = source switch
            {
                LambdaExpr lambda => NaturalLambda(lambda, out why),
                _ when group is not null => NaturalGroup(group, out why),
                _ => null,
            };
            if (spelt is null)
            {
                Error(source, $"the delegate type could not be inferred for {what}: {why ?? "it is not a lambda or a method"}");
                return null;
            }
            source.NaturalType = spelt;
            // A MEMBER WITH A TYPE SPELT IN IT IS EXPANDED AGAIN, not passed
            // through as unchanged (Monomorphiser.RewriteMembers): the
            // expansion is what makes the delegate type the spelling names,
            // and a member passed through left a lambda's natural type with
            // no copy behind it -- "LambdaExpr is not implemented".
            if (_member is not null) _member.Expanded = false;
        }

        // A SYNTHESISED DELEGATE NOT DECLARED YET: the driver declares it.
        string name = source.NaturalType.Name;
        if (name.StartsWith(Parser.AnonymousDelegateName(""), StringComparison.Ordinal)
            && !_r.Types.Values.Any(t => t.Decl?.Name == name))
        {
            _r.AnonymousDelegates.Add(name[Parser.AnonymousDelegateName("").Length..]);
            _r.Reexpand = true;
            return null;
        }

        Type type = Resolve(source.NaturalType, _thisType);
        if (type.IsError) return null;

        // NOT MADE YET: the next round makes it.
        if (Unmade(type))
        {
            _r.Reexpand = true;
            return null;
        }
        return type;
    }

    /// <summary>Whether a lambda or method group converted to this takes its natural type: object.</summary>
    private static bool NaturalTarget(Type wanted) => wanted.Prim == Prim.Any && wanted.Symbol is null && !wanted.IsArray;

    /// <summary>
    /// The shape of the delegate C# synthesises (Parser.AnonymousDelegates)
    /// for these parameters and result, or null when a Func or an Action
    /// says it.
    /// </summary>
    private static string? AnonymousShape(IReadOnlyList<(bool Ref, bool Out, bool In)> parameters, bool returns, bool byReference, bool readOnly,
        IReadOnlyList<string>? marks = null)
    {
        bool marked = marks is not null && marks.Any(mark => mark.Length > 0);
        if (!marked && !byReference && parameters.Count <= 16 && parameters.All(p => !p.Ref && !p.Out && !p.In)) return null;
        string modes = string.Concat(parameters.Select((p, k) => (p.Out ? "o" : p.In ? "i" : p.Ref ? "r" : "v") + (marks is null ? "" : marks[k])));
        char result = !returns ? 'V' : byReference ? readOnly ? 'G' : 'F' : 'R';
        return modes + "_" + result;
    }

    /// <summary>
    /// What a lambda's parameter adds to its natural type's shape (C# 12):
    /// `p` for `params`, `x` and its default spelt in hexadecimal for a
    /// default value -- a delegate with `int x = 5` is not one with `= 6`.
    /// Empty for neither; null where the default is no constant (CS1736).
    /// </summary>
    private string? LambdaMark(Param p, int k) => p.IsParams ? "p" : p.Default is not Expr value ? "" : DefaultMark(value, Resolve(p.Type, _thisType), p.Name, k);

    private string? DefaultMark(Expr value, Type type, string name, int k)
        => DefaultSpelt(value, type, name, k) is string spelt ? "x" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(spelt)).ToLowerInvariant() : null;

    /// <summary>
    /// A default as its natural type declares it (LambdaDefault), checked
    /// against its parameter's type as its target -- `string? t = default`
    /// is a string's default; its part of the shape is `x` and this in
    /// hexadecimal (DefaultMark).
    /// </summary>
    private string? DefaultSpelt(Expr value, Type type, string name, int k)
    {
        if (type.IsError) return null;
        Type? wanted = _wanted;
        _wanted = type;
        Type had = CheckExpr(value);
        _wanted = wanted;
        if (had.IsError) return null;
        CheckAssignable(had, type, value, What.Quoted(name));
        if (LambdaDefault(value, type, k) is not string spelt)
        {
            Error(value, $"CS1736: Default parameter value for '{name}' must be a compile-time constant");
            return null;
        }
        return spelt;
    }

    /// <summary>
    /// A lambda parameter's default as its natural type's delegate declares
    /// it (Parser.AnonymousDelegates): the constant's value, as a literal of
    /// the parameter's type, which that declaration names T and its place.
    /// </summary>
    private string? LambdaDefault(Expr value, Type type, int k)
    {
        Expr at = _r.Rewrites.TryGetValue(value, out Expr? rewritten) ? rewritten : value;
        if (at is DefaultExpr or LiteralExpr { Kind: Lit.Null }) return "default";
        Type plain = type.IsNullableValue ? type.Underlying : type;
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (plain.Prim == Prim.String && !plain.IsArray)
        {
            return ConstantText(at, _thisType!) is string text
                ? "\"" + string.Concat(text.Select(c => c < 128 && char.IsLetterOrDigit(c) || c == ' ' ? c.ToString() : $"\\u{(int)c:X4}")) + "\""
                : null;
        }
        if (plain.Prim == Prim.Bool) return Constant(at) is bool truth ? truth ? "true" : "false" : null;
        if (plain.Symbol is { Kind: TypeKind.Enum }) return ConstantValue(at, _thisType) is long member ? $"(T{k})({member})" : null;
        if (plain.Symbol?.Name == "Decimal")
        {
            if (at is LiteralExpr literal && DecimalLiteralValue(literal) is decimal money) return money.ToString(invariant) + "M";
            return ConstantValue(at, _thisType) is long whole ? whole + "M" : null;
        }
        if (plain.Prim is Prim.F32 or Prim.F64)
        {
            string kind = plain.Prim == Prim.F32 ? "float" : "double";
            double? real = RealConstant(at, _thisType) ?? (double?)ConstantValue(at, _thisType);
            return real switch
            {
                null => null,
                double.NaN => kind + ".NaN",
                double.PositiveInfinity => kind + ".PositiveInfinity",
                double.NegativeInfinity => kind + ".NegativeInfinity",
                double d => d.ToString("R", invariant) + (plain.Prim == Prim.F32 ? "F" : "D"),
            };
        }
        if (plain.Symbol is null && (plain.IsInteger || plain.Prim == Prim.Char) && ConstantValue(at, _thisType) is long w)
        {
            return plain.Prim switch
            {
                Prim.U64 => (ulong)w + "UL",
                Prim.I64 => w + "L",
                Prim.U32 => (uint)w + "U",
                Prim.Char => $"(char)({w})",
                _ => w.ToString(invariant),
            };
        }
        return null;
    }

    /// <summary>A lambda's natural type, or null with the reason.</summary>
    private TypeRef? NaturalLambda(LambdaExpr lam, out string? why)
    {
        why = null;
        if (lam.NoSignature)
        {
            why = "an anonymous method with no parameter list has none";
            return null;
        }
        if (lam.Params.Count > 0 && !lam.TypesWritten)
        {
            why = "a lambda's parameter types must be written for it to have one";
            return null;
        }

        bool byReference = (lam.ReturnMods & Mods.RefReturn) != 0;
        TypeRef? result;
        if (lam.Returns is not null)
        {
            result = lam.Returns;
        }
        else
        {
            Type? produced = LambdaResult(lam);
            if (produced is null)
            {
                why = "what it returns has no type of its own";
                return null;
            }
            if (produced.IsVoid)
            {
                result = null;
            }
            else if (SpellOpen(produced) is TypeRef named)
            {
                result = named;
            }
            else
            {
                why = $"what it returns, a '{produced}', cannot be written";
                return null;
            }
        }

        // An async lambda's is a Task, of what it returns when it returns one.
        if (lam.Async)
        {
            TypeRef task = new() { Name = "Task", Line = lam.Line, Col = lam.Col };
            if (result is not null) task.Arguments.Add(result);
            result = task;
        }

        List<string> marks = new();
        for (int k = 0; k < lam.Params.Count; k++)
        {
            if (LambdaMark(lam.Params[k], k) is not string mark)
            {
                why = $"its parameter '{lam.Params[k].Name}' has a default that is no constant";
                return null;
            }
            marks.Add(mark);
        }
        string? shape = AnonymousShape(lam.Params.Select(p => (p.IsRef, p.IsOut, p.IsReadOnlyRef)).ToList(),
                                       result is not null, byReference, (lam.ReturnMods & Mods.RefReadonlyReturn) != 0, marks);
        TypeRef spelt = new()
        {
            Name = shape is not null ? Parser.AnonymousDelegateName(shape) : result is null ? "Action" : "Func",
            Line = lam.Line, Col = lam.Col,
        };
        spelt.Arguments.AddRange(lam.Params.Select(p => p.Type));
        if (result is not null) spelt.Arguments.Add(result);
        return spelt;
    }

    /// <summary>
    /// What a lambda's body produces with its written parameter types: the
    /// expression's type, or the best common type of the block's returns --
    /// Void for none -- or null when there is none (C# 12.6.3.13).
    /// </summary>
    private Type? LambdaResult(LambdaExpr lam)
    {
        _quiet++;
        PushScope(functionBoundary: true);
        List<Type>? wasInferred = _inferredReturns;
        Type? wasWanted = _wanted;
        int wasSlot = _nextSlot;
        HashSet<LocalSym>? assignedBefore = AssignedBefore(lam);
        try
        {
            for (int i = 0; i < lam.Params.Count; i++)
            {
                Param p = lam.Params[i];
                Declare(lam, p.Name, new ParamSym(i, Resolve(p.Type, _thisType), p.Name, p.IsRef || p.IsOut, p.IsReadOnlyRef));
            }

            _wanted = null;
            if (lam.Body is not null)
            {
                _inferredReturns = null;
                Type produced = CheckExpr(lam.Body);
                if (produced.IsError || produced.Prim == Prim.NullLiteral) return null;
                // An async lambda's body is what its task holds.
                return produced;
            }

            _inferredReturns = new List<Type>();
            CheckBlock(lam.BlockBody!);
            if (_inferredReturns.All(t => t.IsVoid)) return Type.Void;
            Type? common = BestCommonType(_inferredReturns);
            return common is null || common.IsError ? null : common;
        }
        finally
        {
            _inferredReturns = wasInferred;
            _wanted = wasWanted;
            _nextSlot = wasSlot;
            AssignedAfter(assignedBefore);
            PopScope();
            _quiet--;
        }
    }

    /// <summary>A method group's natural type: one method, not generic.</summary>
    private TypeRef? NaturalGroup(Sym group, out string? why)
    {
        why = null;
        List<MethodSymbol> methods = group is CapturedMethodGroupSym held ? held.Methods : ((MethodGroupSym)group).Methods;
        if (methods.Count != 1)
        {
            why = $"the method group has {methods.Count} overloads";
            return null;
        }
        MethodSymbol m = methods[0];
        if (m.TypeParams.Count > 0)
        {
            why = $"'{m.Name}' is generic";
            return null;
        }

        // ITS DEFAULTS AND `params` (C# 12), as a lambda's: `var pick =
        // Pick;` of `int Pick(int x = 7)` is called as `pick()`. Each
        // default as a call to it fills it in (Written), spelt in full.
        List<string> marks = new();
        for (int k = 0; k < m.Params.Count; k++)
        {
            Param? declared = m.Decl is MethodDecl { } md && md.Params.Count == m.Params.Count ? md.Params[k] : null;
            string? mark = declared is null ? "" : declared.IsParams ? "p" : declared.Default is null ? ""
                : DefaultMark(Written(m, declared), m.Params[k].Type, declared.Name, k);
            if (mark is null)
            {
                why = $"its parameter '{m.Params[k].Name}' has a default that is no constant";
                return null;
            }
            marks.Add(mark);
        }
        string? shape = AnonymousShape(m.Params.Select(p => (p.ByRef && !p.ReadOnly && !IsOut(m, p), IsOut(m, p), p.ReadOnly)).ToList(),
                                       !m.Returns.IsVoid, m.RefReturn, m.RefReturnReadOnly, marks);
        TypeRef spelt = new()
        {
            Name = shape is not null ? Parser.AnonymousDelegateName(shape) : m.Returns.IsVoid ? "Action" : "Func",
            Line = 0, Col = 0,
        };
        foreach (ParamSymbol p in m.Params)
        {
            if (SpellOpen(p.Type) is not TypeRef type)
            {
                why = $"its parameter '{p.Name}' has a type that cannot be written";
                return null;
            }
            spelt.Arguments.Add(type);
        }
        if (!m.Returns.IsVoid)
        {
            if (SpellOpen(m.Returns) is not TypeRef returns)
            {
                why = "what it returns cannot be written";
                return null;
            }
            spelt.Arguments.Add(returns);
        }
        return spelt;

        static bool IsOut(MethodSymbol method, ParamSymbol parameter)
            => method.Decl?.Params.FirstOrDefault(written => written.Name == parameter.Name) is { IsOut: true };
    }
}
