#nullable enable
namespace Corsac.Lang;

public enum Prim : byte
{
    Void,
    Bool,
    I8, I16, I32, I64,

    /// <summary>
    /// byte, ushort, uint, ulong.
    ///
    /// Not a spelling of the signed ones: an unsigned value LOADS differently
    /// (zero-extended, not sign-extended), DIVIDES differently, and SHIFTS
    /// RIGHT differently. The instruction set has Ldu, DivU, ModU and Shr
    /// beside Ld, Div, Mod and Sar precisely because these are different
    /// operations, and a byte that sign-extends reads 200 as -56.
    /// </summary>
    U8, U16, U32, U64,

    /// <summary>
    /// nint and nuint: an integer the width of a MACHINE WORD.
    ///
    /// The runtime and the collector hold every address in a long, which on
    /// a 486 is a register pair for every add and compare of something the
    /// machine does in one register. These are C#'s name for that one
    /// register: 32 bits where the word is, 64 where it is. Not a spelling of
    /// int or long -- their width is the target's, and the conversion rules
    /// are C#'s own: implicit from int, implicit to long, explicit the other
    /// way, and never mixed with the type of the other signedness.
    /// </summary>
    NInt, NUInt,

    F32, F64,
    Char,
    String,
    /// <summary>The type of <c>null</c> before it is assigned to anything.</summary>
    NullLiteral,
    /// <summary>
    /// Accepts any type. Exists for exactly one thing: reinterpreting a value
    /// as the machine word it already is, which is what lets a generic
    /// container hash a key without the language having a root object type.
    /// </summary>
    Any,
    /// <summary>
    /// A TYPE, as a value: what typeof(T) and GetType() produce.
    ///
    /// One machine word, holding the address of a type's descriptor. Comparing
    /// two of them is comparing two addresses, which is exactly right -- there
    /// is one descriptor per type, so two values are equal when and only when
    /// they name the same type.
    ///
    /// A primitive rather than a class in the standard library because the
    /// descriptor is emitted by the code generator, and a class whose layout
    /// had to agree with the code generator by hand is a layout that disagrees
    /// eventually. See docs/image-format.md.
    /// </summary>
    Type,

    /// <summary>A type that failed to bind. Poisons quietly so one mistake yields one message.</summary>
    Error,
}
