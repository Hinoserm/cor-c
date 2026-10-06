#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    private sealed record LocalFunctionContext(List<LocalScope> Scopes, TypeSymbol? Scope,
        TypeSymbol? Self, TypeSymbol? Lexical, MemberDecl? Member);
    private readonly Dictionary<LocalDecl, LocalFunctionContext> _localFunctionContexts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FieldSymbol, LocalDecl> _capturedLocalFunctions = new(ReferenceEqualityComparer.Instance);

    private LocalDecl? LocalFunctionDeclaration(Sym symbol)
    {
        if (symbol is LocalSym local && _declOf.TryGetValue(local, out LocalDecl? declaration)
            && declaration.LocalFunction) return declaration;
        if (symbol is FieldSym field && _capturedLocalFunctions.TryGetValue(field.Field, out LocalDecl? captured)) return captured;
        return null;
    }

    private Expr LocalDefault(LocalDecl declaration, Param parameter)
    {
        if (_writtenDefaults.TryGetValue(parameter, out Expr? written)) return written;
        LocalFunctionContext context = _localFunctionContexts[declaration];
        List<LocalScope> previous = new(_scopes);
        TypeSymbol? scope = _scope, self = _thisType, lexical = _lexicalType;
        MemberDecl? member = _member;
        _scopes.Clear(); _scopes.AddRange(context.Scopes);
        _scope = context.Scope; _thisType = context.Self; _lexicalType = context.Lexical; _member = context.Member;
        Expr SpellLocal(Expr expression)
        {
            if (expression is NameExpr name && Lookup(name.Name) is ConstSym constant)
            {
                if (constant.Text is not null) return new LiteralExpr { Kind = Lit.Str, Text = constant.Text, Line = name.Line, Col = name.Col };
                if (constant.Type.Prim == Prim.Bool) return new LiteralExpr { Kind = Lit.Bool,
                    Text = constant.Value == 0 ? "false" : "true", IntValue = constant.Value, Line = name.Line, Col = name.Col };
                return new CastExpr { Type = new TypeRef { Name = constant.Type.Symbol?.Key ?? constant.Type.ToString() },
                    Operand = new LiteralExpr { Kind = Lit.Int, Text = constant.Value.ToString(), IntValue = constant.Value },
                    Line = name.Line, Col = name.Col };
            }
            if (expression is BinaryExpr binary) return new BinaryExpr { Op = binary.Op,
                Left = SpellLocal(binary.Left), Right = SpellLocal(binary.Right), Line = binary.Line, Col = binary.Col };
            if (expression is UnaryExpr unary) return new UnaryExpr { Op = unary.Op,
                Operand = SpellLocal(unary.Operand), Line = unary.Line, Col = unary.Col };
            return Spell(expression);
        }
        try
        {
            written = SpellLocal(parameter.Default!);
            _writtenDefaults.Add(parameter, written);
        }
        finally
        {
            _scopes.Clear(); _scopes.AddRange(previous);
            _scope = scope; _thisType = self; _lexicalType = lexical; _member = member;
        }
        return written!;
    }

    private void CompleteLocalArguments(CallExpr call, LocalDecl declaration)
    {
        if (declaration.Init is not LambdaExpr lambda) return;
        bool named = call.ArgNames.Any(name => name is not null);

        // A `params` TAIL IS PACKED, as a method's is: the arguments after the
        // fixed ones become one array, unless a single one there already is
        // the array. After the first round the call carries the array, and it
        // converts, so it is not packed again.
        if (!named && lambda.Params.Count > 0 && lambda.Params[^1].IsParams && call.Args.Count >= lambda.Params.Count - 1)
        {
            int fixedCount = lambda.Params.Count - 1;
            Type array = Resolve(lambda.Params[^1].Type, _thisType);
            bool expanded = call.Args.Count != lambda.Params.Count;
            if (!expanded)
            {
                _quiet++;
                Type given = CheckExpr(call.Args[^1]);
                _quiet--;
                expanded = !given.IsError && given.Prim != Prim.NullLiteral && !Convertible(given, array);
            }
            if (expanded && array.Element is Type element && RefOf(element) is TypeRef each)
            {
                NewExpr packed = new() { Type = each, Elements = call.Args.Skip(fixedCount).ToList(), Line = call.Line, Col = call.Col };
                call.Args.RemoveRange(fixedCount, call.Args.Count - fixedCount);
                call.Args.Add(packed);
                if (call.ArgNames.Count > call.Args.Count) call.WritableArgNames.RemoveRange(call.Args.Count, call.ArgNames.Count - call.Args.Count);
            }
        }
        if (!named && call.Args.Count == lambda.Params.Count) return;
        Expr?[] placed = new Expr?[lambda.Params.Count];
        int[] from = new int[lambda.Params.Count];
        Array.Fill(from, -1);
        List<int> order = new();
        for (int n = 0; n < call.Args.Count; n++)
        {
            int index = n < call.ArgNames.Count && call.ArgNames[n] is { } name
                ? lambda.Params.FindIndex(parameter => parameter.Name == name) : n;
            if (index < 0 || index >= placed.Length || placed[index] is not null)
            {
                Error(call, $"invalid or duplicate argument for local function '{declaration.Name}'");
                return;
            }
            placed[index] = call.Args[n]; order.Add(index); from[index] = n;
        }
        for (int n = 0; n < placed.Length; n++)
        {
            if (placed[n] is not null) continue;
            if (lambda.Params[n].Default is null)
            {
                Error(call, $"missing argument '{lambda.Params[n].Name}' for local function '{declaration.Name}'");
                return;
            }
            placed[n] = CallerValue(lambda.Params[n], lambda.Params, CallLine(call), k => from[k] < 0 ? null : SpanText(call.Spans, call.Source, from[k] + 1))
                        ?? LocalDefault(declaration, lambda.Params[n]);
            order.Add(n);
        }
        call.Args.Clear(); call.Args.AddRange(placed!);
        call.ForgetArgNames();
        if (named) { call.WritableLocalArgumentOrder.Clear(); call.WritableLocalArgumentOrder.AddRange(order); }
    }
}
