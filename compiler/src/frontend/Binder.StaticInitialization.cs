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
    /// <summary>
    /// A STATIC ARRAY OF A CONSTANT LENGTH, `static T[] x = new T[N];`: laid
    /// down zeroed as a table is (StaticArrayOf), of any element -- every one
    /// its default, a struct's too -- so the type initialiser makes nothing
    /// for it. N is a C# constant expression (literals, consts, enum members,
    /// folded); a static field is no constant, and an array as long as one
    /// says when the type is first touched is made then. At most
    /// ZeroedLimit bytes of elements: a larger one is the heap's, as it was,
    /// rather than the image file's.
    /// </summary>
    private StaticArray? ZeroedArrayOf(FieldDecl f, TypeDecl d)
    {
        if (f.Init is not NewExpr { Elements: null, ArraySize: { } size, Utf8Bytes: null, Body.Inits.Count: 0, Body.Adds.Count: 0, Body.Indexes.Count: 0 } made
            || f.Type is not { ArrayRank: > 0, PointerDepth: 0 } declared
            || made.Type is not { PointerDepth: 0 } element
            || element.Name != declared.Name || element.Args.Count != declared.Args.Count || element.ArrayRank + 1 != declared.ArrayRank
            || IsThreadStatic(f)
            || !_r.Types.TryGetValue(TypeKey(d), out TypeSymbol? owner)
            || ConstantValue(size, owner) is not long length || length < 0)
        {
            return null;
        }
        if (ElementBytes(element) is not int each || length * each > ZeroedLimit)
        {
            return null;
        }
        return new StaticArray { Element = element.ArrayRank > 0 ? "" : element.Name, Zeroed = true, Length = (int)length };
    }

    /// <summary>
    /// The most bytes one element of a zeroed array takes: a keyword type's
    /// own size, eight for an array; null for anything named -- a class, an
    /// enum, a struct -- whose kind looking it up here would answer before
    /// its members may be declared (a program's Process taken for
    /// System.Diagnostics', List<Process> made too soon), and for a nullable
    /// value. An array of those is the link's to make (Lto.Preinit).
    /// </summary>
    private static int? ElementBytes(TypeRef element)
    {
        if (element.ArrayRank > 0) return 8;
        bool nullable = element.Nullable;
        return element.Name switch
        {
            "string" or "object" => 8,
            "bool" or "byte" or "sbyte" => nullable ? null : 1,
            "char" or "short" or "ushort" => nullable ? null : 2,
            "int" or "uint" or "float" => nullable ? null : 4,
            "long" or "ulong" or "double" or "nint" or "nuint" => nullable ? null : 8,
            _ => null,
        };
    }

    /// <summary>
    /// WHAT THE LINK MIGHT MAKE (Lto.Preinit): an object or an array made,
    /// `"..."u8.ToArray()`, or another static read -- the shapes a recipe can
    /// be (Lowering.Preinit), anything else certainly not one.
    /// </summary>
    private static bool PreinitShape(Expr initial) => initial switch
    {
        SuppressExpr suppressed => PreinitShape(suppressed.Operand),
        NewExpr { Utf8Bytes: null } => true,
        CallExpr { Target: MemberExpr { Name: "ToArray", Target: { } span }, Args.Count: 0 } when Utf8Of(span) is not null => true,
        NameExpr or MemberExpr => true,
        _ => false,
    };

    /// <summary>
    /// A `"text"u8` literal's bytes: the parser's ReadOnlySpan<byte> over an
    /// array marked Utf8Bytes, or that array itself.
    /// </summary>
    internal static byte[]? Utf8Of(Expr e) => e switch
    {
        NewExpr { Utf8Bytes: { } bytes } => bytes,
        NewExpr { Args: [NewExpr { Utf8Bytes: { } bytes }] } => bytes,
        _ => null,
    };

    /// <summary>An initialiser that reads nothing: a literal, forgiven or negated, which no later field can be seen through.</summary>
    private static bool PreinitConstant(Expr initial) => initial switch
    {
        LiteralExpr => true,
        SuppressExpr suppressed => PreinitConstant(suppressed.Operand),
        UnaryExpr { Op: UnOp.Neg or UnOp.Not or UnOp.BitNot or UnOp.Plus } unary => PreinitConstant(unary.Operand),
        _ => false,
    };

    /// <summary>The most bytes of elements a zeroed static array is laid down with (ZeroedArrayOf).</summary>
    public const int ZeroedLimit = 65536;

    private static StaticArray? StaticArrayOf(FieldDecl f)
    {
        // A [ThreadStatic] has no word in the image to lay the table's address
        // in: its initialiser runs, on the thread that sets the type up.
        if (f.Init is not NewExpr { Elements: { } elements, Body.Inits.Count: 0, Body.Adds.Count: 0, Body.Indexes.Count: 0 } made
            || f.Type is not { ArrayRank: 1, PointerDepth: 0, Args.Count: 0 } declared
            || IsThreadStatic(f))
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
        body.WritableStatements.AddRange(statements);
        // A source-level return exits the body, not the completion protocol.
        type.Members.Add(new MethodDecl
        {
            Name = "StaticInitBody$", Mods = Mods.Static | Mods.Private,
            OwnedImplementation = !type.Elsewhere,
            Returns = new TypeRef { Name = "void" }, Body = body,
            File = type.File, Line = type.Line, Col = type.Col,
        });
        Block wrapper = new();
        wrapper.WritableStatements.Add(new LocalDecl
        {
            Name = status, Type = new TypeRef { Name = "int" },
            Init = Call("Runtime", "EnterTypeInitialization", type, Address()),
        });
        wrapper.WritableStatements.Add(new IfStmt
        {
            Cond = new BinaryExpr { Op = BinOp.Eq, Left = Name(status), Right = Number(0) },
            Then = new ReturnStmt(),
        });
        wrapper.WritableStatements.Add(new IfStmt
        {
            Cond = new BinaryExpr { Op = BinOp.Eq, Left = Name(status), Right = Number(2) },
            Then = new ThrowStmt { Value = Name(failure) },
        });
        wrapper.WritableStatements.Add(new LocalDecl { Name = success, Type = new TypeRef { Name = "bool" }, Init = Boolean(false) });
        Block guarded = new();
        guarded.WritableStatements.Add(new ExprStmt { Expr = new CallExpr { Target = Name("StaticInitBody$") } });
        guarded.WritableStatements.Add(Assign(success, Boolean(true)));
        Block cleanup = new();
        cleanup.WritableStatements.Add(new IfStmt
        {
            Cond = Name(success), Then = RuntimeCall("CompleteTypeInitialization"), Else = RuntimeCall("FailTypeInitialization"),
        });
        TryStmt attempt = new() { Body = guarded, Finally = cleanup };
        Block failed = new();
        // Only ever a TypeInitializationException in the static: every later
        // access throws that same object again, and a catch that can take it
        // must keep it (Escape's foreign throws), so its type must be known.
        failed.WritableStatements.Add(Assign(failure, new NewExpr
        {
            Type = new TypeRef { Name = "TypeInitializationException" },
            Args = { new LiteralExpr { Kind = Lit.Str, Text = InitializerKey(type) }, Name(error) },
        }));
        failed.WritableStatements.Add(new ThrowStmt { Value = Name(failure) });
        attempt.Catches.Add(new CatchClause { Type = new TypeRef { Name = "Exception" }, Name = error, Body = failed });
        wrapper.WritableStatements.Add(attempt);
        type.Members.Add(new MethodDecl
        {
            Name = "StaticInit$", Mods = Mods.Static | Mods.Public,
            OwnedImplementation = !type.Elsewhere,
            Returns = new TypeRef { Name = "void" }, Body = wrapper,
            File = type.File, Line = type.Line, Col = type.Col,
        });
    }
}
