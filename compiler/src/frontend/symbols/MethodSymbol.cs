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

    /// <summary>
    /// A GENERIC VIRTUAL METHOD: `virtual T GetValue&lt;T&gt;()`, an override of
    /// one, or a generic method of an interface. It has no vtable slot -- a
    /// slot holds one address and this has one per type argument, compiled
    /// wherever the argument is known -- so a call to it goes through a
    /// dispatcher that tests the receiver against every class that overrides
    /// it (see Binder.GenericVirtualCall).
    /// </summary>
    public bool GenericVirtual => TypeParams.Count > 0 && !Static
        && (Virtual || Override || Abstract || Owner.Kind == TypeKind.Interface);

    /// <summary>Slot in the owner's vtable, or -1 when dispatch is static.</summary>
    public int VtableSlot { get; set; } = -1;

    /// <summary>Code address once emitted.</summary>
    public int Address { get; set; } = -1;

    public string Signature => $"{Owner.Name}.{Name}({string.Join(", ", Params.Select(p => p.Type))})";
    public override string ToString() => Signature;
}
