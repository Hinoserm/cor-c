#nullable enable
namespace Corsac.Lang;

public sealed class MethodDecl : MemberDecl
{
    /// <summary>
    /// A SHARED METHOD COPY'S DICTIONARY (Monomorphiser.Specialise): the types
    /// its body makes or asks for over its own type parameters that only run
    /// time knows, as its template writes them, one entry each in the
    /// dictionary every call hands it (NewExpr.NeedSlot). Null for none.
    /// </summary>
    public List<TypeRef>? CopyNeeds { get; set; }
    /// <summary>A shared method copy's template's type parameters, by place: what CopyNeeds are written over.</summary>
    public List<string>? CopyParams { get; set; }
    /// <summary>Null for a constructor.</summary>
    public TypeRef? Returns { get; init; }
    /// <summary>
    /// An auto-property's accessor, which the binder invents: uses through
    /// the class read and write the field, and only an interface or a
    /// virtual call comes in here (UsesCapture judges the field instead).
    /// </summary>
    public bool AutoAccessor { get; set; }
    // Declared beside AutoAccessor so the two bytes share a word.
    public bool IsCtor { get; init; }

    /// <summary>
    /// WHETHER ITS TEXT ALONE SAYS IT NEVER RETURNS (Parser.NeverReturnsByText):
    /// its body is `=> throw ...`, or its last statement is a throw or a loop
    /// with no way out and nothing in it returns. Read off the body's tokens
    /// whether the body was parsed or skipped, and kept by the declaration
    /// index for a body it leaves out (SourceIndexBuilder), so every unit
    /// that sees the method says the same of it -- what code more than one
    /// unit compiles goes by (Binder.CallNeverReturns).
    /// </summary>
    // And this one beside them both.
    public bool NeverReturnsByText { get; set; }
    /// <summary>
    /// A generic method's copy on a word-shaped instantiation with every
    /// argument known, the class's and the method's (Frontend.Specialise):
    /// compiled as that instantiation's own code, not sent to the canonical
    /// class's twin (Lowering.Canonical), so `new Derived&lt;T, K&gt;()` in it
    /// is Derived&lt;string, K&gt; for Holder&lt;string&gt;.
    /// </summary>
    public bool OwnCode { get; set; }
    /// <summary>
    /// An iterator of a canonical class copy's code: the entry of the type
    /// context holding its machine as the instantiation's own (a variant
    /// listing IEnumerable&lt;string&gt;, Lowering.MachineVariant), which the
    /// kickoff stamps the machine with; -1 for none.
    /// </summary>
    public int CanonMachineSlot { get; set; } = -1;
    /// <summary>An async method of a canonical class copy's instance code: the class entry of its Task&lt;X&gt; (Monomorphiser.CanonTask); -1 for none.</summary>
    public int CanonTaskSlot { get; set; } = -1;
    /// <summary>An iterator's shared method copy: the entry of its dictionary standing for its machine as its caller's instantiation (Lowering.MachineVariant); -1 for none.</summary>
    public int MachineNeed { get; set; } = -1;
    /// <summary>An async shared method copy: the entry of its dictionary for its Task&lt;X&gt; as its caller's instantiation (Lowering.TaskNeedStamp); -1 for none.</summary>
    public int TaskNeed { get; set; } = -1;
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
    public Block? Body { get; set; }
    /// <summary>
    /// A partial method declared and never given a body (C# 15.6.9): calls
    /// of it are no calls, arguments and all (Binder), and its body is empty.
    /// </summary>
    public bool PartialWithoutBody { get; set; }
    /// <summary>The <c>: base(...)</c> or <c>: this(...)</c> a constructor chains to.</summary>
    // This and every field below live in MemberRare: few methods set any.
    public CtorInit? Init { get => _rare?.Init; init { if (value is not null) Rare.Init = value; } }

    /// <summary>
    /// A STATIC'S STEP (Binder.StaticInitialisers): the field of the type
    /// whose initialiser this runs, unless the link laid the field down made
    /// (Lto.Preinit), which its flag says.
    /// </summary>
    public string? PreinitField { get => _rare?.PreinitField; init { if (value is not null) Rare.PreinitField = value; } }

    /// <summary>
    /// The parameter this method's result is null only for, named by
    /// <c>[return: NotNullIfNotNull(nameof(path))]</c>.
    ///
    /// .NET writes it on Path.ChangeExtension and its fellows: the result is
    /// declared `string?` because a null in gives a null back, and a caller who
    /// passed a string gets one. Null where the attribute was not written.
    /// </summary>
    public string? NotNullIfNotNull { get => _rare?.NotNullIfNotNull; init { if (value is not null) Rare.NotNullIfNotNull = value; } }

    /// <summary>
    /// A GENERIC LOCAL FUNCTION hoisted into its type (Parser.
    /// ParseGenericLocalFunction): the name it was written under, which is
    /// how its block, and its own body, call it. Null for every other method.
    /// </summary>
    /// <summary>
    /// A STATIC member of a C# 14 extension block: the type it extends, through
    /// which it is called -- `string.Hello()` (Binder.StaticExtension). Null
    /// for every other method.
    /// </summary>
    public TypeRef? StaticExtensionOf
    {
        get => _rare?.StaticExtensionOf;
        set { if (value is not null) Rare.StaticExtensionOf = value; else if (_rare is not null) _rare.StaticExtensionOf = null; }
    }

    public string? HoistedName
    {
        get => _rare?.HoistedName;
        set { if (value is not null) Rare.HoistedName = value; else if (_rare is not null) _rare.HoistedName = null; }
    }

    /// <summary>
    /// The type parameters of what is around a generic local function that
    /// it took on as its own, its captures being of them (Binder.
    /// WriteCaptures): each inferred at a call from the variable handed in.
    /// </summary>
    // Shared and empty for the many methods that carry none; only ever
    // replaced whole (Binder.SettleGenericCaptures), never written through.
    public List<string> CarriedTypeParams
    {
        get => _rare?.CarriedTypeParams ?? NoNames;
        set { if (!ReferenceEquals(value, NoNames)) Rare.CarriedTypeParams = value; else if (_rare is not null) _rare.CarriedTypeParams = null; }
    }
    private static readonly List<string> NoNames = new();

    /// <summary>
    /// The generic local functions a hoisted one can call by their written
    /// names -- its own, its siblings', those of every block around it -- as
    /// (written name, member name) pairs, so its body, checked as a member of
    /// the type, sees them as C# lets it.
    /// </summary>
    public List<(string Name, string Method)> LocalGenerics => _rare?.LocalGenerics ?? NoLocalGenerics;
    /// <summary>To write: made on first use. Almost no method has any, and a
    /// list, a table and two more lists each were a tenth of a compile's
    /// live heap.</summary>
    public List<(string Name, string Method)> WritableLocalGenerics => Rare.LocalGenerics ??= new();
    private static readonly List<(string Name, string Method)> NoLocalGenerics = new();

    /// <summary>
    /// How many of a hoisted generic local function's parameters, at the
    /// front, are the variables it CAPTURED (Binder.SettleGenericCaptures):
    /// each by reference, named as the variable is, so its body reads and
    /// writes the enclosing method's own variable. -1 until they are known.
    /// </summary>
    public int Captures
    {
        get => _rare?.Captures ?? -1;
        set { if (value != -1) Rare.Captures = value; else if (_rare is not null) _rare.Captures = -1; }
    }

    /// <summary>
    /// Where a hoisted generic local function was written: the HoistKey of
    /// the method or hoisted function around it. A copy of that one carries
    /// a copy of this one with it (Frontend.RehostLocals), so the function
    /// sees the copy's type arguments as Roslyn's sees the outer type
    /// parameters it is given.
    /// </summary>
    public string? HoistedIn
    {
        get => _rare?.HoistedIn;
        set { if (value is not null) Rare.HoistedIn = value; else if (_rare is not null) _rare.HoistedIn = null; }
    }

    /// <summary>
    /// In a copy of a method, and in the generic local functions carried with
    /// it: which hoisted function each written block's name now means, by
    /// the name it was hoisted under (Binder.DeclareGenericLocal).
    /// </summary>
    public Dictionary<string, string> Rehosted => _rare?.Rehosted ?? NoRehosted;
    /// <summary>To write: made on first use.</summary>
    public Dictionary<string, string> WritableRehosted => Rare.Rehosted ??= new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> NoRehosted = new(StringComparer.Ordinal);

    /// <summary>
    /// Where its last token is -- a block body's closing brace, an expression
    /// body's semicolon -- kept for a method marked [DoesNotReturn] alone:
    /// where C# reports CS8763 when its end can be reached
    /// (Binder.CheckDoesNotReturn). (0, 0) where it is not kept.
    /// </summary>
    public (int Line, int Col) End
    {
        get => _rare is null ? (0, 0) : (_rare.EndLine, _rare.EndCol);
        set { if (value != (0, 0) || _rare is not null) { Rare.EndLine = value.Line; Rare.EndCol = value.Col; } }
    }

    /// <summary>What a hoisted function names its parent by: unique for a hoisted one, by place for a member.</summary>
    public string HoistKey => HoistedName is not null ? Name : Name + "@" + Line + ":" + Col;
}
