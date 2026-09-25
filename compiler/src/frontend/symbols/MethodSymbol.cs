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

    /// <summary>
    /// An explicit interface implementation's interface (its simple name) and
    /// the member of it this fills, which is the name without the qualifier;
    /// <see cref="Name"/> is the two joined, so that no call by the plain
    /// name finds it and it never meets a member of that name the type has
    /// of its own.
    /// </summary>
    public string? ExplicitInterface { get; init; }
    public string? ExplicitMember { get; init; }
    public List<string> TypeParams { get; } = new();

    /// <summary>Slot in the owner's vtable, or -1 when dispatch is static.</summary>
    public int VtableSlot { get; set; } = -1;

    /// <summary>Code address once emitted.</summary>
    public int Address { get; set; } = -1;

    public string Signature => $"{Owner.Name}.{Name}({string.Join(", ", Params.Select(p => p.Type))})";
    public override string ToString() => Signature;
}
