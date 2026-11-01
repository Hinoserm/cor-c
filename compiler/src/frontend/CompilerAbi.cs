#nullable enable
namespace Corsac.Lang;

/// <summary>
/// THE CLASS LIBRARY'S TYPES THE COMPILER'S OWN CODE NAMES, beside Runtime
/// (Lowering.RuntimeType, public): what a program's or a module's compiled
/// code calls, tests for or makes though its source never wrote the name --
/// a delegate's `+`, an async method's flow, a collection expression's
/// spread, a rectangular array's maker, `x is Type`, Type's statics. Kept
/// out of C#'s sight as .NET keeps its own (internal, or named with `__`),
/// and offered by the image that defines them all the same
/// (Lowering.RecordPublicApi): the contract between compiled code and the
/// library is no program's to name and every image's to call. Every place
/// the compiler writes one of these names reads it from here.
/// </summary>
public static class CompilerAbi
{
    /// <summary>A delegate's `+`, `-`, `==` and hash (Binder.DelegateCombination).</summary>
    public const string Delegates = "__Delegates";

    /// <summary>An async method's unhandled exception and its flow saved and restored (Lowering.Async).</summary>
    public const string AsyncRuntime = "AsyncRuntime";

    /// <summary>A collection expression with a spread (Binder, collection expressions).</summary>
    public const string CollectionBuilder = "CollectionExpressionBuilder";

    /// <summary>A rectangular array's initialiser (Parser): its makers.</summary>
    public const string Rectangular = "__Rectangular";

    /// <summary>The object a Type is when held as one: `x is Type` tests for it (Lowering.SystemTypeTested).</summary>
    public const string TypeObject = "RuntimeType";

    /// <summary>Type's statics and the members of a Type the compiler does not answer itself (Binder).</summary>
    public const string SystemType = "SystemType";

    private static readonly HashSet<string> Named = new(StringComparer.Ordinal)
    {
        Delegates, AsyncRuntime, CollectionBuilder, Rectangular, TypeObject, SystemType,
    };

    /// <summary>
    /// Whether `d` is one of them: the class library's own type of that name,
    /// at the top, in no namespace -- never a program's type that took one
    /// of the names.
    /// </summary>
    public static bool Is(TypeDecl d)
        => d.TypeParams.Count == 0 && (d.Outer is null || d.Outer == d.Namespace) && d.Namespace.Length == 0
           && Named.Contains(d.Name) && Binder.ClassLibrarySide(d);
}
