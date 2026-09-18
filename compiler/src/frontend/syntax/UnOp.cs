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
}
