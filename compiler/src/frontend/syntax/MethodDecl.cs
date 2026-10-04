#nullable enable
namespace Corsac.Lang;

public sealed class MethodDecl : MemberDecl
{
    /// <summary>Null for a constructor.</summary>
    public TypeRef? Returns { get; init; }
    // Made only when written: most have none, and a list each was the collector's.
    private static readonly List<TypeParam> NoTypeParams = new();
    private List<TypeParam>? _typeParams;
    /// <summary>To read: one shared empty list when there are none, never written through.</summary>
    public List<TypeParam> TypeParams => _typeParams ?? NoTypeParams;
    /// <summary>To write: made on first use.</summary>
    public List<TypeParam> WritableTypeParams => _typeParams ??= new();
    public List<Param> Params { get; } = new();
    public Block? Body { get; init; }
    public bool IsCtor { get; init; }
    /// <summary>The <c>: base(...)</c> or <c>: this(...)</c> a constructor chains to.</summary>
    public CtorInit? Init { get; init; }

    /// <summary>
    /// The parameter this method's result is null only for, named by
    /// <c>[return: NotNullIfNotNull(nameof(path))]</c>.
    ///
    /// .NET writes it on Path.ChangeExtension and its fellows: the result is
    /// declared `string?` because a null in gives a null back, and a caller who
    /// passed a string gets one. Null where the attribute was not written.
    /// </summary>
    public string? NotNullIfNotNull { get; init; }

    /// <summary>
    /// A GENERIC LOCAL FUNCTION hoisted into its type (Parser.
    /// ParseGenericLocalFunction): the name it was written under, which is
    /// how its block, and its own body, call it. Null for every other method.
    /// </summary>
    public string? HoistedName { get; set; }

    /// <summary>
    /// The generic local functions a hoisted one can call by their written
    /// names -- its own, its siblings', those of every block around it -- as
    /// (written name, member name) pairs, so its body, checked as a member of
    /// the type, sees them as C# lets it.
    /// </summary>
    public List<(string Name, string Method)> LocalGenerics { get; } = new();

    /// <summary>
    /// How many of a hoisted generic local function's parameters, at the
    /// front, are the variables it CAPTURED (Binder.SettleGenericCaptures):
    /// each by reference, named as the variable is, so its body reads and
    /// writes the enclosing method's own variable. -1 until they are known.
    /// </summary>
    public int Captures { get; set; } = -1;
}
