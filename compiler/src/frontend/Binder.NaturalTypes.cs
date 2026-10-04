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
/// The type is SPELT on the declaration (LocalDecl.NaturalType), as if
/// written there, and checked as a declared one is from then on. Where no
/// source names that Func or Action, nothing has made it yet: the round is
/// marked to go again (BindResult.Reexpand), the monomorphiser makes it from
/// the spelling, and the next binding has it. Until then the variable is an
/// error nobody is told of.
///
/// Where C# synthesises a delegate type no Func or Action can say -- a
/// parameter passed by reference, a result returned by one, more than sixteen
/// parameters -- this refuses rather than invents one.
/// </summary>
public sealed partial class Binder
{
    /// <summary>The variable's natural delegate type, its initialiser checked against it.</summary>
    private Type NaturalDelegate(LocalDecl d, Sym? group)
    {
        if (d.NaturalType is null)
        {
            string? why = null;
            TypeRef? spelt = d.Init switch
            {
                LambdaExpr lambda => NaturalLambda(lambda, out why),
                _ when group is not null => NaturalGroup(group, out why),
                _ => null,
            };
            if (spelt is null)
            {
                Error(d, $"the delegate type could not be inferred for '{d.Name}': {why ?? "it is not a lambda or a method"}");
                return Type.Error;
            }
            d.NaturalType = spelt;
        }

        Type type = Resolve(d.NaturalType, _thisType);
        if (type.IsError) return type;

        // NOT MADE YET: the next round makes it.
        if (Unmade(type))
        {
            _r.Reexpand = true;
            return Type.Error;
        }

        if (d.Init is LambdaExpr held)
        {
            CheckLambda(held, type);
        }
        else if (d.Init is not null)
        {
            Type had = group is null ? CheckExpr(d.Init) : _r.TypeOf(d.Init);
            CheckAssignable(had, type, d.Init, $"initialiser for '{d.Name}'");
        }
        return type;
    }

    /// <summary>A lambda's Func or Action, or null with the reason.</summary>
    private TypeRef? NaturalLambda(LambdaExpr lam, out string? why)
    {
        why = null;
        if (lam.Params.Count > 0 && !lam.TypesWritten)
        {
            why = "a lambda's parameter types must be written for it to have one";
            return null;
        }
        if ((lam.ReturnMods & Mods.RefReturn) != 0 || lam.Params.Any(p => p.IsRef || p.IsOut || p.IsReadOnlyRef))
        {
            why = "it passes or returns by reference, which no Func or Action can say";
            return null;
        }
        if (lam.Params.Count > 16)
        {
            why = "it takes more parameters than any Func or Action";
            return null;
        }

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
            else if (RefOf(produced) is TypeRef named)
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

        TypeRef spelt = new() { Name = result is null ? "Action" : "Func", Line = lam.Line, Col = lam.Col };
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
        try
        {
            for (int i = 0; i < lam.Params.Count; i++)
            {
                Declare(lam, lam.Params[i].Name, new ParamSym(i, Resolve(lam.Params[i].Type, _thisType), lam.Params[i].Name, false));
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
            PopScope();
            _quiet--;
        }
    }

    /// <summary>A method group's Func or Action: one method, not generic, nothing by reference.</summary>
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
        if (m.RefReturn || m.Params.Any(p => p.ByRef || p.ReadOnly))
        {
            why = $"'{m.Name}' passes or returns by reference, which no Func or Action can say";
            return null;
        }
        if (m.Params.Count > 16)
        {
            why = $"'{m.Name}' takes more parameters than any Func or Action";
            return null;
        }

        TypeRef spelt = new() { Name = m.Returns.IsVoid ? "Action" : "Func", Line = 0, Col = 0 };
        foreach (ParamSymbol p in m.Params)
        {
            if (RefOf(p.Type) is not TypeRef type)
            {
                why = $"its parameter '{p.Name}' has a type that cannot be written";
                return null;
            }
            spelt.Arguments.Add(type);
        }
        if (!m.Returns.IsVoid)
        {
            if (RefOf(m.Returns) is not TypeRef returns)
            {
                why = "what it returns cannot be written";
                return null;
            }
            spelt.Arguments.Add(returns);
        }
        return spelt;
    }
}
