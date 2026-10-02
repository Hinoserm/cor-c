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
}
