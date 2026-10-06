#nullable enable
namespace Corsac.Lang;

public sealed class MethodDecl : MemberDecl
{
    /// <summary>Null for a constructor.</summary>
    public TypeRef? Returns { get; init; }
    /// <summary>
    /// An auto-property's accessor, which the binder invents: uses through
    /// the class read and write the field, and only an interface or a
    /// virtual call comes in here (UsesCapture judges the field instead).
    /// </summary>
    public bool AutoAccessor { get; set; }
    // Made only when written: most have none, and a list each was the collector's.
    private static readonly List<TypeParam> NoTypeParams = new();
    private List<TypeParam>? _typeParams;
    /// <summary>To read: one shared empty list when there are none, never written through.</summary>
    public List<TypeParam> TypeParams => _typeParams ?? NoTypeParams;
    /// <summary>To write: made on first use.</summary>
    public List<TypeParam> WritableTypeParams => _typeParams ??= new();
    /// <summary>To read: one shared empty list until a parameter is added.</summary>
    public List<Param> Params => _params ?? NoParams;
    /// <summary>To write: made on first use.</summary>
    public List<Param> WritableParams => _params ??= new();
    private List<Param>? _params;
    /// <summary>Takes a list the parser read; none for `()`.</summary>
    internal void AdoptParams(List<Param>? read) { if (read is { Count: > 0 }) { if (_params is null) _params = read; else _params.AddRange(read); } }
    // Never written through: every writer goes by WritableParams.
    private static readonly List<Param> NoParams = new();
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
    /// The type parameters of what is around a generic local function that
    /// it took on as its own, its captures being of them (Binder.
    /// WriteCaptures): each inferred at a call from the variable handed in.
    /// </summary>
    // Shared and empty for the many methods that carry none; only ever
    // replaced whole (Binder.SettleGenericCaptures), never written through.
    public List<string> CarriedTypeParams { get; set; } = NoNames;
    private static readonly List<string> NoNames = new();

    /// <summary>
    /// The generic local functions a hoisted one can call by their written
    /// names -- its own, its siblings', those of every block around it -- as
    /// (written name, member name) pairs, so its body, checked as a member of
    /// the type, sees them as C# lets it.
    /// </summary>
    public List<(string Name, string Method)> LocalGenerics => _localGenerics ?? NoLocalGenerics;
    /// <summary>To write: made on first use. Almost no method has any, and a
    /// list, a table and two more lists each were a tenth of a compile's
    /// live heap.</summary>
    public List<(string Name, string Method)> WritableLocalGenerics => _localGenerics ??= new();
    private List<(string Name, string Method)>? _localGenerics;
    private static readonly List<(string Name, string Method)> NoLocalGenerics = new();

    /// <summary>
    /// How many of a hoisted generic local function's parameters, at the
    /// front, are the variables it CAPTURED (Binder.SettleGenericCaptures):
    /// each by reference, named as the variable is, so its body reads and
    /// writes the enclosing method's own variable. -1 until they are known.
    /// </summary>
    public int Captures { get; set; } = -1;

    /// <summary>
    /// Where a hoisted generic local function was written: the HoistKey of
    /// the method or hoisted function around it. A copy of that one carries
    /// a copy of this one with it (Frontend.RehostLocals), so the function
    /// sees the copy's type arguments as Roslyn's sees the outer type
    /// parameters it is given.
    /// </summary>
    public string? HoistedIn { get; set; }

    /// <summary>
    /// In a copy of a method, and in the generic local functions carried with
    /// it: which hoisted function each written block's name now means, by
    /// the name it was hoisted under (Binder.DeclareGenericLocal).
    /// </summary>
    public Dictionary<string, string> Rehosted => _rehosted ?? NoRehosted;
    /// <summary>To write: made on first use.</summary>
    public Dictionary<string, string> WritableRehosted => _rehosted ??= new(StringComparer.Ordinal);
    private Dictionary<string, string>? _rehosted;
    private static readonly Dictionary<string, string> NoRehosted = new(StringComparer.Ordinal);

    /// <summary>What a hoisted function names its parent by: unique for a hoisted one, by place for a member.</summary>
    public string HoistKey => HoistedName is not null ? Name : Name + "@" + Line + ":" + Col;
}
