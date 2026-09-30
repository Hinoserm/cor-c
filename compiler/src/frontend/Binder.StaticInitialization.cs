#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    /// <summary>
    /// A STATIC ARRAY OF CONSTANTS, read off the declaration before anything is
    /// declared: a one-dimensional array of a keyword type -- bool, char, the
    /// integers, float, double, string -- initialised with literals only, each
    /// in range for the element. Such a field is laid down in the image as an
    /// array object with its address in the field (Lowering), so a lookup
    /// table costs no code at startup, no type initialiser at every touch, and
    /// nothing on the heap: one of COR-C's deliberate departures from .NET,
    /// which builds it at run time. The array is still writable, as a C#
    /// array is; the collector reads the data section as roots, so a
    /// reference stored into a string table later is still seen.
    ///
    /// Null for anything else -- an enum, a named constant, an expression --
    /// which is initialised at run time as before, and checked there.
    /// </summary>
    private static StaticArray? StaticArrayOf(FieldDecl f)
    {
        if (f.Init is not NewExpr { Elements: { } elements, Body.Inits.Count: 0, Body.Adds.Count: 0, Body.Indexes.Count: 0 } made
            || f.Type is not { ArrayRank: 1, PointerDepth: 0, Args.Count: 0 } declared)
        {
            return null;
        }
        string element = declared.Name;
        if (made.Type.Name.Length > 0 && (made.Type.Name != element || made.Type.ArrayRank != 0))
        {
            return null;
        }
        if (made.ArraySize is not null
            && (made.ArraySize is not LiteralExpr { Kind: Lit.Int } size || size.IntValue != elements.Count))
        {
            return null;
        }
        if (declared.ElementNullable && element != "string")
        {
            return null;
        }

        StaticArray table = new() { Element = element };
        foreach (Expr e in elements)
        {
            bool negative = false;
            Expr at = e;
            if (at is UnaryExpr { Op: UnOp.Neg, Operand: LiteralExpr negated })
            {
                negative = true;
                at = negated;
            }
            if (at is not LiteralExpr lit)
            {
                return null;
            }
            switch (element)
            {
                case "string":
                    if (negative) return null;
                    if (lit.Kind == Lit.Str) table.Strings.Add(lit.Text);
                    else if (lit.Kind == Lit.Null) table.Strings.Add(null);
                    else return null;
                    break;
                case "float" or "double":
                    if (lit.Kind is not (Lit.Real or Lit.Int or Lit.Char)) return null;
                    if (lit.Kind == Lit.Real && element == "double" && lit.Text.EndsWith("m", StringComparison.OrdinalIgnoreCase)) return null;
                    if (lit.Kind == Lit.Real && element == "float" && !lit.Text.EndsWith("f", StringComparison.OrdinalIgnoreCase)) return null;
                    double real = lit.Kind == Lit.Real ? lit.RealValue : lit.IntValue;
                    table.Reals.Add(negative ? -real : real);
                    break;
                case "bool":
                    if (negative || lit.Kind != Lit.Bool) return null;
                    table.Integers.Add(lit.IntValue != 0 ? 1 : 0);
                    break;
                default:
                    if (!IntegerElement(element, lit, negative, out long bits)) return null;
                    table.Integers.Add(bits);
                    break;
            }
        }
        return table;
    }

    /// <summary>
    /// A literal as an element of an integer (or char) array, when C# would
    /// take it there without a cast: in range, a char only where char widens.
    /// </summary>
    private static bool IntegerElement(string element, LiteralExpr lit, bool negative, out long bits)
    {
        bits = 0;
        if (lit.Kind == Lit.Char)
        {
            if (negative || element is "byte" or "sbyte" or "short") return false;
            bits = lit.IntValue;
            return element is "char" or "ushort" or "int" or "uint" or "long" or "ulong";
        }
        if (lit.Kind != Lit.Int || element == "char")
        {
            return false;
        }
        // A LITERAL PAST long.MaxValue is a ulong's, and only a ulong takes it.
        bool unsignedBig = lit.IntValue < 0;
        if (unsignedBig && (negative || element != "ulong"))
        {
            return false;
        }
        long value = negative ? -lit.IntValue : lit.IntValue;
        (long low, long high) = element switch
        {
            "byte" => (0L, 255L),
            "sbyte" => (-128L, 127L),
            "short" => (-32768L, 32767L),
            "ushort" => (0L, 65535L),
            "int" => (int.MinValue, int.MaxValue),
            "uint" => (0L, uint.MaxValue),
            "long" => (long.MinValue, long.MaxValue),
            "ulong" => (0L, long.MaxValue),
            _ => (1L, 0L),
        };
        if (low > high)
        {
            return false;
        }
        if (!unsignedBig && (value < low || value > high))
        {
            return false;
        }
        bits = value;
        return true;
    }

    /// <summary>
    /// The name a type's initializer failure is reported under: a
    /// specialisation's is its TEMPLATE's key, `EqualityComparer`1`, and not
    /// its own. The initializer is made on the template in some units and on
    /// the specialisation in others, and a name that followed which one it was
    /// made on gave the one definition two bodies -- two certified semantics
    /// -- and the link refused them as a duplicate.
    /// </summary>
    private static string InitializerKey(TypeDecl type)
    {
        if (!type.Specialised || type.Template is not string template || type.TemplateArgs.Count == 0)
        {
            return TypeKey(type);
        }
        int dot = template.LastIndexOf('.');
        return dot < 0 ? Arity(template, type.TemplateArgs.Count)
            : template[..(dot + 1)] + Arity(template[(dot + 1)..], type.TemplateArgs.Count);
    }

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
            // A WORD: the runtime compares and swaps the initialising thread's
            // block address into it (Runtime.EnterTypeInitialization).
            Name = BindResult.ReadyField, Mods = Mods.Static | Mods.Private | Mods.Volatile,
            Type = new TypeRef { Name = "nint" }, File = type.File, Line = type.Line, Col = type.Col,
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
        // Only ever a TypeInitializationException in the static: every later
        // access throws that same object again, and a catch that can take it
        // must keep it (Escape's foreign throws), so its type must be known.
        failed.Statements.Add(Assign(failure, new NewExpr
        {
            Type = new TypeRef { Name = "TypeInitializationException" },
            Args = { new LiteralExpr { Kind = Lit.Str, Text = InitializerKey(type) }, Name(error) },
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
