#nullable enable
namespace Corsac.Lang;

public sealed class MethodSymbol
{
    public required string Name { get; init; }
    public required Type Returns { get; init; }
    public required TypeSymbol Owner { get; init; }
    public List<ParamSymbol> Params { get; } = new();
    public bool Static { get; init; }
    public bool Virtual { get; init; }
    public bool Override { get; init; }
    public bool Abstract { get; init; }
    public bool Async { get; init; }
    public bool IsCtor { get; init; }
    public MethodDecl? Decl { get; init; }
    public List<string> TypeParams { get; } = new();

    /// <summary>Slot in the owner's vtable, or -1 when dispatch is static.</summary>
    public int VtableSlot { get; set; } = -1;

    /// <summary>Code address once emitted.</summary>
    public int Address { get; set; } = -1;

    public string Signature => $"{Owner.Name}.{Name}({string.Join(", ", Params.Select(p => p.Type))})";
    public override string ToString() => Signature;
}
