#nullable enable
namespace Corsac.Lang;

public enum UnOp : byte
{
    Neg, Not, BitNot, PreInc, PreDec, PostInc, PostDec,

    /// <summary><c>*p</c> -- the thing at an address.</summary>
    Deref,

    /// <summary><c>&amp;x</c> -- the address of a thing.</summary>
    AddressOf,

    /// <summary>C# checked and unchecked arithmetic contexts.</summary>
    Checked,
    Unchecked,

    /// <summary><c>+x</c>: the value, promoted as the numeric operators promote
    /// (`+b` of a byte is an int), or a type's own op_UnaryPlus.</summary>
    Plus,
}
