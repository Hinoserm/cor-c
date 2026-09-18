#nullable enable
namespace Corsac.Lang;

public sealed class FieldSymbol
{
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public required TypeSymbol Owner { get; init; }
    public bool Static { get; init; }
    public bool Volatile { get; init; }

    /// <summary>
    /// This field holds the ADDRESS of a captured local's cell, not its value.
    ///
    /// A closure over a captured variable keeps a pointer to the one cell the
    /// enclosing method also uses, so both see each other's writes. Reading the
    /// field therefore takes two loads and writing takes a load and a store.
    /// Only closure fields are ever this.
    /// </summary>
    public bool Boxed { get; set; }

    /// <summary>
    /// Whoever builds this object must set this member.
    ///
    /// Checked at the construction site rather than here, which is the whole
    /// point of it: a field that must be filled in is one the type can stop
    /// checking for itself, and the check moves to the one place that knows
    /// whether it was.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>Byte offset within an instance, or within static storage.</summary>
    public int Offset { get; set; }
}
