using System.Numerics;

namespace Corsac.Lang.Lto;

/// <summary>
/// A set of allocation sites, as the escape engine keeps them for good
/// (RegionEscape: a stand-in's sites, a summary's shape): sorted numbers
/// while few, one bit a site past that. Never changed once made; a set grown
/// is a new one.
///
/// ONE BIT A SITE, NOT FOUR BYTES, once that is smaller. Past WidenAfter a
/// stand-in holds every site beneath its targets, most of the program's,
/// and the compiler's own link kept 7,985 of them as sorted lists of 780
/// million sites, each list once more as its leaves under the stand-in's
/// holder: six of its fourteen gigabytes, where a 32-bit linker has three.
/// As bits, a set of the program's 76,506 sites is 9.6 KB.
/// </summary>
internal sealed class SiteSet
{
    public static readonly SiteSet Empty = new(Array.Empty<int>(), null, 0);

    // Exactly one of the two: the sites sorted, or a bit each.
    private readonly int[]? _list;
    private readonly ulong[]? _bits;
    public readonly int Count;

    private SiteSet(int[]? list, ulong[]? bits, int count)
    {
        _list = list;
        _bits = bits;
        Count = count;
    }

    private static int Words(int universe) => (universe + 63) >> 6;

    // As bits once those take less room than the list (a site is four bytes,
    // a bit an eighth of one each site of the universe).
    private static bool AsBits(int count, int universe) => (long)count * 32 > universe;

    /// <summary>A set of sorted distinct sites, the list kept as it is when that is the smaller.</summary>
    public static SiteSet OfSorted(int[] sites, int universe)
    {
        if (sites.Length == 0) return Empty;
        if (!AsBits(sites.Length, universe)) return new SiteSet(sites, null, sites.Length);
        ulong[] bits = new ulong[Words(universe)];
        foreach (int site in sites) bits[site >> 6] |= 1UL << (site & 63);
        return new SiteSet(null, bits, sites.Length);
    }

    /// <summary>A set of distinct sites, in any order.</summary>
    public static SiteSet Of(IEnumerable<int> sites, int universe)
    {
        ulong[] bits = new ulong[Words(universe)];
        int count = 0;
        foreach (int site in sites)
        {
            ulong bit = 1UL << (site & 63);
            if ((bits[site >> 6] & bit) == 0) count++;
            bits[site >> 6] |= bit;
        }
        return FromBits(bits, count, universe);
    }

    // Bits of `count` sites, kept as they are or made a list.
    private static SiteSet FromBits(ulong[] bits, int count, int universe)
    {
        if (count == 0) return Empty;
        if (AsBits(count, universe)) return new SiteSet(null, bits, count);
        int[] list = new int[count];
        int at = 0;
        for (int w = 0; w < bits.Length; w++)
            for (ulong word = bits[w]; word != 0; word &= word - 1)
                list[at++] = (w << 6) | BitOperations.TrailingZeroCount(word);
        return new SiteSet(list, null, count);
    }

    /// <summary>
    /// A walk's sites as a set: the bits it set, in the words it touched
    /// (each named once, in any order). The walk's words are left as they
    /// were given; the caller clears them.
    /// </summary>
    public static SiteSet OfWalk(ulong[] walkBits, List<int> touched, int count, int universe)
    {
        if (count == 0) return Empty;
        if (AsBits(count, universe))
        {
            ulong[] bits = new ulong[Words(universe)];
            foreach (int w in touched) bits[w] = walkBits[w];
            return new SiteSet(null, bits, count);
        }
        touched.Sort();
        int[] list = new int[count];
        int at = 0;
        foreach (int w in touched)
            for (ulong word = walkBits[w]; word != 0; word &= word - 1)
                list[at++] = (w << 6) | BitOperations.TrailingZeroCount(word);
        return new SiteSet(list, null, count);
    }

    public bool Has(int site)
    {
        if (_bits is { } bits) return (bits[site >> 6] >> (site & 63) & 1) != 0;
        return Array.BinarySearch(_list!, site) >= 0;
    }

    /// <summary>Whether every site of this set is one of `had`'s.</summary>
    public bool IsSubsetOf(SiteSet had)
    {
        if (Count == 0) return true;
        if (Count > had.Count) return false;
        if (_list is { } list)
        {
            if (had._bits is { } hadBits)
            {
                foreach (int site in list) if ((hadBits[site >> 6] >> (site & 63) & 1) == 0) return false;
                return true;
            }
            // Both sorted: one walk.
            int[] other = had._list!;
            int i = 0;
            foreach (int site in list)
            {
                while (i < other.Length && other[i] < site) i++;
                if (i >= other.Length || other[i] != site) return false;
            }
            return true;
        }
        // As bits, and `had` at least as large: bits too, as every set is
        // made (AsBits); a list `had` is answered site by site all the same.
        ulong[] bits = _bits!;
        if (had._bits is { } theirs)
        {
            for (int w = 0; w < bits.Length; w++) if ((bits[w] & ~theirs[w]) != 0) return false;
            return true;
        }
        for (int w = 0; w < bits.Length; w++)
            for (ulong word = bits[w]; word != 0; word &= word - 1)
                if (Array.BinarySearch(had._list!, (w << 6) | BitOperations.TrailingZeroCount(word)) < 0) return false;
        return true;
    }

    /// <summary>Both sets' sites as one set; either itself when it already holds the other's.</summary>
    public static SiteSet Union(SiteSet a, SiteSet b, int universe)
    {
        if (b.IsSubsetOf(a)) return a;
        if (a.IsSubsetOf(b)) return b;
        if (a._list is { } x && b._list is { } y && !AsBits(x.Length + y.Length, universe))
        {
            int[] all = new int[x.Length + y.Length];
            int i = 0, j = 0, n = 0;
            while (i < x.Length && j < y.Length)
            {
                if (x[i] < y[j]) all[n++] = x[i++];
                else if (x[i] > y[j]) all[n++] = y[j++];
                else { all[n++] = x[i++]; j++; }
            }
            while (i < x.Length) all[n++] = x[i++];
            while (j < y.Length) all[n++] = y[j++];
            if (n < all.Length) Array.Resize(ref all, n);
            return new SiteSet(all, null, n);
        }
        ulong[] bits = new ulong[Words(universe)];
        a.OrInto(bits);
        b.OrInto(bits);
        int count = 0;
        foreach (ulong word in bits) count += BitOperations.PopCount(word);
        return FromBits(bits, count, universe);
    }

    /// <summary>Its sites set in `bits`, one bit a site.</summary>
    public void OrInto(ulong[] bits)
    {
        if (_bits is { } mine) { for (int w = 0; w < mine.Length; w++) bits[w] |= mine[w]; return; }
        foreach (int site in _list!) bits[site >> 6] |= 1UL << (site & 63);
    }

    /// <summary>
    /// Its sites set in a walk's bits (SitesOf): each word that was empty
    /// named in `touched`, and how many sites are new to it.
    /// </summary>
    public int OrIntoWalk(ulong[] walkBits, List<int> touched)
    {
        int added = 0;
        if (_bits is { } mine)
        {
            for (int w = 0; w < mine.Length; w++)
            {
                ulong word = mine[w];
                if (word == 0) continue;
                ulong old = walkBits[w];
                if (old == 0) touched.Add(w);
                added += BitOperations.PopCount(word & ~old);
                walkBits[w] = old | word;
            }
            return added;
        }
        foreach (int site in _list!)
        {
            int w = site >> 6;
            ulong bit = 1UL << (site & 63), old = walkBits[w];
            if ((old & bit) != 0) continue;
            if (old == 0) touched.Add(w);
            walkBits[w] = old | bit;
            added++;
        }
        return added;
    }

    /// <summary>Its sites, rising.</summary>
    public IEnumerable<int> Sites()
    {
        if (_list is { } list) { foreach (int site in list) yield return site; yield break; }
        ulong[] bits = _bits!;
        for (int w = 0; w < bits.Length; w++)
            for (ulong word = bits[w]; word != 0; word &= word - 1)
                yield return (w << 6) | BitOperations.TrailingZeroCount(word);
    }

    /// <summary>Its sites, rising, as an array of their own.</summary>
    public int[] ToArray()
    {
        if (_list is { } list) return (int[])list.Clone();
        int[] all = new int[Count];
        int at = 0;
        foreach (int site in Sites()) all[at++] = site;
        return all;
    }

    /// <summary>The bytes it holds, for a report.</summary>
    public long Bytes => _bits is { } bits ? 8L * bits.Length : 4L * (_list?.Length ?? 0);
}
