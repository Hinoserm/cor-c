#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

public static class CondExt
{
    /// <summary>The condition that is true exactly when this one is false.</summary>
    public static Cond Negate(this Cond c) => (Cond)((int)c ^ 1);

    /// <summary>The condition for the same comparison with its operands exchanged.</summary>
    public static Cond Swap(this Cond c) => c switch
    {
        Cond.B => Cond.A, Cond.A => Cond.B, Cond.Ae => Cond.Be, Cond.Be => Cond.Ae,
        Cond.L => Cond.G, Cond.G => Cond.L, Cond.Le => Cond.Ge, Cond.Ge => Cond.Le,
        _ => c,
    };

    public static string Mnemonic(this Cond c) => c switch
    {
        Cond.O => "o", Cond.No => "no", Cond.B => "b", Cond.Ae => "ae", Cond.E => "e", Cond.Ne => "ne",
        Cond.Be => "be", Cond.A => "a", Cond.S => "s", Cond.Ns => "ns", Cond.P => "p", Cond.Np => "np",
        Cond.L => "l", Cond.Ge => "ge", Cond.Le => "le", _ => "g",
    };
}
