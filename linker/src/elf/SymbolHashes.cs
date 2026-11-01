#nullable enable
namespace Corsac.Lang.Elf;

/// <summary>
/// THE TABLES A LOADER FINDS AN EXPORT BY, the same for every image that
/// exports: a kernel's (KernelExports.Table) and a shared library's or a
/// module's (Linker.Dynamic). The GNU hash -- a bloom filter, buckets and a
/// chain of each symbol's hash, over the symbols from its first in the order
/// of their buckets -- and beside each of them its name's FNV-1a 64-bit hash
/// and its value (names64), through which a loader binds a name reading
/// neither a symbol nor a string.
/// </summary>
public static class SymbolHashes
{
    /// <summary>The GNU hash's bucket count for this many symbols: one in four, odd.</summary>
    public static int GnuBuckets(int count) => Math.Max(1, count / 4) | 1;

    /// <summary>The order the GNU hash wants its symbols in: by bucket, each bucket's in the order given.</summary>
    public static int[] GnuOrder(IReadOnlyList<string> names)
    {
        int buckets = GnuBuckets(names.Count);
        uint[] hashes = names.Select(Elf.GnuHashName).ToArray();
        return Enumerable.Range(0, names.Count).OrderBy(i => hashes[i] % (uint)buckets).ThenBy(i => i).ToArray();
    }

    /// <summary>The bloom filter's words for this many symbols: a power of two, twelve bits a symbol at least.</summary>
    private static int BloomWords(int count, bool wide)
    {
        int bits = wide ? 64 : 32, words = 1;
        while (words * bits < count * 12) words <<= 1;
        return words;
    }

    /// <summary>The GNU hash's bytes, its header to its chain.</summary>
    public static int GnuSize(int count, bool wide)
        => 16 + BloomWords(count, wide) * (wide ? 8 : 4) + (GnuBuckets(count) + count) * 4;

    /// <summary>
    /// The GNU hash over `names`, already in GnuOrder, the first of them the
    /// symbol numbered `first` in its .dynsym.
    /// </summary>
    public static byte[] Gnu(IReadOnlyList<string> names, int first, bool wide)
    {
        int count = names.Count, buckets = GnuBuckets(count), bloomBits = wide ? 64 : 32, bloomShift = wide ? 6 : 5;
        int bloomWords = BloomWords(count, wide);
        uint[] hashes = names.Select(Elf.GnuHashName).ToArray();
        ulong[] bloom = new ulong[bloomWords];
        uint[] bucket = new uint[buckets], chain = new uint[count];
        for (int i = 0; i < count; i++)
        {
            uint h = hashes[i];
            bloom[(h / (uint)bloomBits) % (uint)bloomWords] |= (1UL << (int)(h % (uint)bloomBits)) | (1UL << (int)((h >> bloomShift) % (uint)bloomBits));
            uint b = h % (uint)buckets;
            if (bucket[b] == 0) bucket[b] = (uint)(i + first);
            bool last = i + 1 == count || hashes[i + 1] % (uint)buckets != b;
            chain[i] = last ? h | 1 : h & ~1u;
        }
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        w.Write((uint)buckets); w.Write((uint)first); w.Write((uint)bloomWords); w.Write((uint)bloomShift);
        foreach (ulong bits in bloom) { if (wide) w.Write(bits); else w.Write(checked((uint)bits)); }
        foreach (uint b in bucket) w.Write(b);
        foreach (uint c in chain) w.Write(c);
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// names64: beside each symbol the GNU hash covers, its name's FNV-1a
    /// 64-bit hash and its value, sixteen bytes. TWO NAMES THE SAME AS
    /// NUMBERS, both hashes alike, would bind one for the other: refused,
    /// naming them. A loader matches the GNU hash but its lowest bit (the
    /// chain's end), so the pair is taken as a loader takes it.
    /// </summary>
    public static byte[] Names64(IReadOnlyList<(string Name, ulong Value)> symbols)
    {
        Dictionary<(uint, ulong), string> numbered = new();
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        foreach ((string name, ulong value) in symbols)
        {
            ulong strong = Elf.FnvName(name);
            (uint, ulong) key = (Elf.GnuHashName(name) | 1, strong);
            if (!numbered.TryAdd(key, name))
                throw new LinkException(new[] { "two exports are one name to a loader (the same GNU and FNV-1a 64 hashes): '" + numbered[key] + "' and '" + name + "'" });
            w.Write(strong); w.Write(value);
        }
        w.Flush();
        return stream.ToArray();
    }
}
