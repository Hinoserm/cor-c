#nullable enable
using System.Numerics;

namespace Corsac.Lang.Backends;

/// <summary>
/// DIVISION BY A CONSTANT AS A MULTIPLICATION, the numbers of it, for any
/// machine with a high multiply: the magic number m and the shifts that make
/// the high half of n * m, shifted, the quotient n / d for every n of the
/// width (Granlund and Montgomery, "Division by Invariant Integers using
/// Multiplication", 1994; Hacker's Delight, second edition, chapter 10,
/// figures 10-1 and 10-2, as LLVM's DivisionByConstantInfo has them). In
/// 128-bit arithmetic, so one routine serves every width up to 64 bits; a
/// selector says how its machine multiplies, these say by what.
/// </summary>
internal static class DivisionMagic
{
    /// <summary>
    /// An unsigned division's numbers. The quotient is
    ///   t = high(  (n >> PreShift) * Magic  ), then
    ///   t >> PostShift                         where Add is false, or
    ///   (((n - t) >> 1) + t) >> PostShift      where it is true:
    /// the magic then has one bit more than the width (2^bits + Magic), and
    /// that form adds the bit without overflowing. PreShift is nonzero only
    /// where Add is false.
    /// </summary>
    public readonly record struct Unsigned(ulong Magic, bool Add, int PreShift, int PostShift);

    /// <summary>
    /// A signed division's numbers. The quotient is high(n * Magic), plus n
    /// where d &gt; 0 and Magic &lt; 0, less n where d &lt; 0 and Magic &gt; 0,
    /// shifted right arithmetically by Shift, plus one where that is negative
    /// (truncation toward zero).
    /// </summary>
    public readonly record struct Signed(long Magic, int Shift);

    /// <summary>
    /// The unsigned numbers for d at a width of `bits` (8 to 64), with the
    /// dividend known to have its top `leadingZeros` bits clear: fewer bits of
    /// dividend need fewer bits of magic, and the Add form is needed less
    /// often. An even d whose magic needs the Add form is divided by its
    /// power of two first (PreShift), which always leaves a magic that fits
    /// (LLVM's even-divisor step). d is at least 2 and not a power of two,
    /// and below 2^(bits - leadingZeros): a larger one's quotient is zero.
    /// </summary>
    public static Unsigned ForUnsigned(ulong d, int bits, int leadingZeros = 0) => ForUnsigned(d, bits, leadingZeros, true);

    private static Unsigned ForUnsigned(ulong d, int bits, int leadingZeros, bool evenStep)
    {
        if (bits is < 8 or > 64 || leadingZeros < 0 || leadingZeros >= bits)
            throw new ArgumentOutOfRangeException(nameof(bits));
        if (d < 2 || (d & (d - 1)) == 0 || (UInt128)d >= UInt128.One << (bits - leadingZeros))
            throw new ArgumentOutOfRangeException(nameof(d));
        UInt128 mask = (UInt128.One << bits) - 1;
        UInt128 divisor = d;
        UInt128 allOnes = (UInt128.One << (bits - leadingZeros)) - 1;
        UInt128 signedMin = UInt128.One << (bits - 1), signedMax = signedMin - 1;
        // The largest dividend in range that leaves d - 1 over.
        UInt128 nc = allOnes - (allOnes + 1 - divisor) % divisor;
        int p = bits - 1;
        UInt128 q1 = signedMin / nc, r1 = signedMin - q1 * nc;
        UInt128 q2 = signedMax / divisor, r2 = signedMax - q2 * divisor;
        UInt128 delta;
        bool add = false;
        do
        {
            p++;
            if (r1 >= nc - r1) { q1 = (2 * q1 + 1) & mask; r1 = (2 * r1 - nc) & mask; }
            else { q1 = (2 * q1) & mask; r1 = (2 * r1) & mask; }
            if (r2 + 1 >= divisor - r2)
            {
                if (q2 >= signedMax) add = true;
                q2 = (2 * q2 + 1) & mask; r2 = (2 * r2 + 1 - divisor) & mask;
            }
            else
            {
                if (q2 >= signedMin) add = true;
                q2 = (2 * q2) & mask; r2 = (2 * r2 + 1) & mask;
            }
            delta = divisor - 1 - r2;
        } while (p < 2 * bits && (q1 < delta || (q1 == delta && r1 == 0)));
        if (add && (d & 1) == 0 && evenStep)
        {
            // d = d' * 2^s: n >> s has s bits fewer, and d' with them always
            // has a magic of the width (Hacker's Delight 10-8).
            int s = BitOperations.TrailingZeroCount(d);
            Unsigned odd = ForUnsigned(d >> s, bits, leadingZeros + s, false);
            if (odd.Add || odd.PreShift != 0) throw new InvalidOperationException($"no pre-shifted magic number for {d}");
            return odd with { PreShift = s };
        }
        int post = p - bits;
        if (add)
        {
            // The Add form's halving is one of the shifts.
            if (post == 0) throw new InvalidOperationException($"no magic number for {d}");
            post--;
        }
        return new Unsigned((ulong)((q2 + 1) & mask), add, 0, post);
    }

    /// <summary>
    /// The signed numbers for d at a width of `bits` (8 to 64): d's magnitude
    /// at least 3 and not a power of two (those are a shift and a bias, and
    /// -1, 0 and 1 no division at all). Hacker's Delight figure 10-1.
    /// </summary>
    public static Signed ForSigned(long d, int bits)
    {
        if (bits is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(bits));
        ulong magnitude = d < 0 ? unchecked(0UL - (ulong)d) : (ulong)d;
        if (magnitude < 3 || (magnitude & (magnitude - 1)) == 0 || (UInt128)magnitude >= UInt128.One << (bits - 1))
            throw new ArgumentOutOfRangeException(nameof(d));
        UInt128 two = UInt128.One << (bits - 1);
        UInt128 mask = (UInt128.One << bits) - 1;
        UInt128 ad = magnitude;
        UInt128 t = two + (d < 0 ? UInt128.One : UInt128.Zero);
        UInt128 anc = t - 1 - t % ad;
        int p = bits - 1;
        UInt128 q1 = two / anc, r1 = two - q1 * anc;
        UInt128 q2 = two / ad, r2 = two - q2 * ad;
        UInt128 delta;
        do
        {
            p++;
            q1 = (2 * q1) & mask; r1 = (2 * r1) & mask;
            if (r1 >= anc) { q1 = (q1 + 1) & mask; r1 = (r1 - anc) & mask; }
            q2 = (2 * q2) & mask; r2 = (2 * r2) & mask;
            if (r2 >= ad) { q2 = (q2 + 1) & mask; r2 = (r2 - ad) & mask; }
            delta = ad - r2;
        } while (q1 < delta || (q1 == delta && r1 == 0));
        // The magic read as a signed number of the width, negated for a
        // negative divisor.
        ulong raw = (ulong)((q2 + 1) & mask);
        long m = bits == 64 ? unchecked((long)raw) : (long)(raw << (64 - bits)) >> (64 - bits);
        if (d < 0) m = bits == 64 ? unchecked(-m) : (-m << (64 - bits)) >> (64 - bits);
        return new Signed(m, p - bits);
    }
}
