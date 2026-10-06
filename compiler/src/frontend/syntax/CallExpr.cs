#nullable enable
namespace Corsac.Lang;

public sealed class CallExpr : Expr
{
    public required Expr Target { get; init; }
    // Source-level tuple naming survives renaming a generic call to its shared
    // machine-code specialization. Names are not part of that code's identity.
    public List<string>? ResultTupleNames { get; set; }
    public TypeRef? ResultTypeUse { get; set; }
    public Dictionary<int, TypeRef>? ArgumentTypeUses { get; set; }
    public List<Expr> Args { get; } = new();
    // Parameter indices in source evaluation order for named local calls.
    // Retained across generic rewriting after ArgNames has been consumed.
    // To read: one shared empty list until written (WritableLocalArgumentOrder), never written through.
    public List<int> LocalArgumentOrder => _localArgumentOrder ?? NoLocalArgumentOrder;
    /// <summary>To write: made on first use; almost every node has none.</summary>
    public List<int> WritableLocalArgumentOrder => _localArgumentOrder ??= new();
    private List<int>? _localArgumentOrder;
    private static readonly List<int> NoLocalArgumentOrder = new();

    /// <summary>
    /// The name written before each argument, or null where none was.
    ///
    /// Parallel to <see cref="Args"/> and the same length once anything has
    /// been named -- `With(nullable: true)`. The binder puts the arguments
    /// into parameter order and clears this, so nothing below it ever sees a
    /// call whose arguments are out of order.
    /// </summary>
    // To read: one shared empty list until written (WritableArgNames), never written through.
    public List<string?> ArgNames => _argNames ?? NoArgNames;
    /// <summary>To write: made on first use; almost every node has none.</summary>
    public List<string?> WritableArgNames => _argNames ??= new();
    private List<string?>? _argNames;
    private static readonly List<string?> NoArgNames = new();

    /// <summary>
    /// Whether the receiver has already been moved into the argument list.
    ///
    /// A member call on a type whose methods are static -- a string's, an
    /// extension method's -- is rewritten so the receiver becomes the first
    /// argument, which makes everything below an ordinary static call. That
    /// rewrite MUTATES the call, and the checker now runs more than once
    /// (generic methods are specialised between rounds), so without a mark on
    /// the call itself the receiver goes in twice and the method is told it
    /// was given one argument too many.
    /// </summary>
    public bool ReceiverAdded { get; set; }

    /// <summary>
    /// The trailing arguments were packed into the params array (Binder,
    /// the expanded form): the last argument IS the array, and a second
    /// check of the call -- an argument packed in turn by an outer call's
    /// params -- takes it as the ordinary form. Asked to expand again,
    /// `Largest(3, 9, 4)` inside `Say("m", ...)` read its own int[] as the
    /// one element and made Largest<int[]>.
    /// </summary>
    public bool ParamsPacked { get; set; }

    /// <summary>
    /// Whether the variables a generic local function captured have been put
    /// in front of the arguments (MethodDecl.Captures), which, like the
    /// receiver above, must happen once however many times the call is
    /// checked.
    /// </summary>
    public bool CapturesPassed { get; set; }

    /// <summary>
    /// `{value:format}` in an interpolated string, written as
    /// value.ToString(format): the binder keeps the call where the value's type
    /// takes a format and otherwise uses the value as it is, as C# does.
    /// </summary>
    public bool FormatHole { get; init; }

    /// <summary>
    /// Where the receiver and each argument were written, for
    /// [CallerArgumentExpression]: pairs of offsets into <see cref="Source"/>,
    /// start and end, the receiver's first (-1, -1 where there is none) and
    /// then one pair per argument as written. Null when the text is unknown.
    /// </summary>
    public int[]? Spans { get; set; }

    /// <summary>The text <see cref="Spans"/> index: the file, or the part of it a sub-parser read.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// WHERE A CALL OF A SHARED METHOD COPY FINDS THE TYPE ARGUMENTS IT HANDS
    /// IT (Monomorphiser.CopyName, Lowering.HiddenTypeArguments): for each of
    /// the method's type parameters, the CanonParam of the type the binder
    /// bound it to -- k for the caller's own shared class's parameter k, read
    /// from its `this`'s type context; -2 - k for the caller's own hidden
    /// argument k; -1 for a type the copy was written over, which it never
    /// asks. Written on the call itself, as ReceiverAdded is, because the
    /// round that sees the generic method is not the last: by then the call
    /// names the copy, and there is nothing left to infer.
    /// </summary>
    public int[]? HiddenTypeArgs { get; set; }

    // The method the checker bound this call to (BindResult.Calls), and which
    // binding bound it (NodeBinding): read back only by that binding.
    internal MethodSymbol? BoundCall;
    internal int BoundCallBy;
}
