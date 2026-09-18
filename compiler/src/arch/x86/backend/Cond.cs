#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>A condition code, numbered as the low nibble of Jcc/SETcc.</summary>
public enum Cond : byte
{
    O = 0, No = 1, B = 2, Ae = 3, E = 4, Ne = 5, Be = 6, A = 7,
    S = 8, Ns = 9, P = 10, Np = 11, L = 12, Ge = 13, Le = 14, G = 15,
}
