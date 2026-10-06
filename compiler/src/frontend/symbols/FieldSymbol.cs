#nullable enable
namespace Corsac.Lang;

public sealed class FieldSymbol
{
    /// <summary>Its names in the lowering (FieldKey, its family, its static symbol), made once rather than at every load and store.</summary>
    internal string? KeyMade, FamilyMade, StaticSymbolMade;
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public required TypeSymbol Owner { get; init; }
    public bool Static { get; init; }
    public bool Volatile { get; init; }

    /// <summary>
    /// A FIELD-LIKE EVENT: `event Action? Fired;`. Inside the type that
    /// declares it, it is the field it looks like; anywhere else it may only
    /// be added to and taken from with += and -= (C#'s CS0070), which the
    /// binder enforces (Binder.EventFromOutside).
    /// </summary>
    public bool IsEvent { get; init; }

    /// <summary>
    /// [ThreadStatic]: a static with a value of its own on every thread. Its
    /// storage is no symbol of the image but a cell each thread makes the first
    /// time it touches the field (Runtime.ThreadStaticCell); the image holds
    /// only the field's number among them, given out at that first touch.
    /// </summary>
    public bool ThreadStatic { get; init; }

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

    /// <summary>
    /// A static field its declaration gave a value (`static readonly T X =
    /// ...;`): the type's StaticInit$ sets it before anything can read it.
    /// One without is zero until written -- which, for a struct held by
    /// pointer, the lowering has to make on the first touch.
    /// </summary>
    public bool Initialised { get; init; }

    /// <summary>Byte offset within an instance, or within static storage.</summary>
    public int Offset { get; set; }

    /// <summary>
    /// A STRUCT HELD IN LINE: this instance field is a struct that holds no
    /// reference -- numbers, enums, pointers, and structs in line of their
    /// own -- and its bytes are the object's, at Offset, rather than a
    /// pointer to a block of its own (Binder.LayOut). Reading it is the
    /// address of those bytes, writing it copies bytes in, and a new object
    /// has it zero with nothing made. A struct that holds a reference is
    /// still a block of its own, the pointer here.
    /// </summary>
    public bool Inline { get; set; }
}
