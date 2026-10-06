#nullable enable
namespace Corsac.Lang;

public sealed class MethodSymbol
{
    /// <summary>Its label (Lowering.Label), made once: every call site asked, and each got a string of its own.</summary>
    internal string? LabelMade;
    public required string Name { get => _name; init => _name = Interned.Name(value); }
    private readonly string _name = "";
    public required Type Returns { get; init; }
    public required TypeSymbol Owner { get; init; }
    /// <summary>To read: one shared empty list until a parameter is added.</summary>
    public List<ParamSymbol> Params => _params ?? NoParams;
    /// <summary>To write: made on first use.</summary>
    public List<ParamSymbol> WritableParams => _params ??= new();
    private List<ParamSymbol>? _params;
    // Never written through: every writer goes by WritableParams.
    private static readonly List<ParamSymbol> NoParams = new();
    public bool Static { get; init; }
    public bool Virtual { get; init; }
    public bool Override { get; init; }
    public bool Abstract { get; init; }
    public bool Async { get; init; }
    public bool IsCtor { get; init; }
    public MethodDecl? Decl { get; init; }

    /// <summary>Returns by reference (Mods.RefReturn): a call to it is a variable.</summary>
    public bool RefReturn => Decl is { } d && (d.Mods & Mods.RefReturn) != 0;

    /// <summary>Returns by `ref readonly`: a variable that is only read.</summary>
    public bool RefReturnReadOnly => Decl is { } d && (d.Mods & Mods.RefReadonlyReturn) != 0;

    /// <summary>
    /// An explicit interface implementation's interface (its simple name) and
    /// the member of it this fills, which is the name without the qualifier;
    /// <see cref="Name"/> is the two joined, so that no call by the plain
    /// name finds it and it never meets a member of that name the type has
    /// of its own.
    /// </summary>
    public string? ExplicitInterface { get => _rare?.ExplicitInterface; init { if (value is not null) Rare.ExplicitInterface = Interned.Name(value); } }
    public string? ExplicitMember { get => _rare?.ExplicitMember; init { if (value is not null) Rare.ExplicitMember = value; } }

    /// <summary>
    /// What few methods carry -- an explicit interface and its member, type
    /// parameters -- in one object made only when one is set. Three words
    /// fewer put a method symbol in a 64-byte block rather than a 72-byte one.
    /// </summary>
    private MethodSymbolRare? _rare;
    private MethodSymbolRare Rare => _rare ??= new();

    // Made only when written: most have none, and a list each was the collector's.
    private static readonly List<string> NoTypeParams = new();
    /// <summary>To read: one shared empty list when there are none, never written through.</summary>
    public List<string> TypeParams => _rare?.TypeParams ?? NoTypeParams;
    /// <summary>To write: made on first use.</summary>
    public List<string> WritableTypeParamNames => Rare.TypeParams ??= new();

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

/// <summary>What few method symbols carry (MethodSymbol._rare).</summary>
internal sealed class MethodSymbolRare
{
    public string? ExplicitInterface;
    public string? ExplicitMember;
    public List<string>? TypeParams;
}
