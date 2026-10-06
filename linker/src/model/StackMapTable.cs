namespace Corsac.Lang.Ir;

/// <summary>
/// THE STACK-MAP TABLE, written by the code generator (X86Backend.EmitStackMaps),
/// rewritten by the link when it cuts duplicate code (Lto.DuplicateCutter), and
/// read by the collector (Gc.MapSite in runtime/src/core/gc.cor). One codec for
/// the two writers, so they cannot disagree; the runtime's reader is the third
/// copy of the layout and must change with this one.
///
/// VERSION 6: VARINTS, DELTAS AND SHARED BITMAPS. Version 5 gave every function
/// sixteen bytes and every call site eight, and the bitmaps of large frames a
/// pooled copy each: about half a megabyte in ring 0, which is resident for
/// as long as the machine runs. Here a call site is two varints -- its return
/// address from the one before it, and its registers and bitmap in one -- and
/// a bitmap of up to sixteen words lives inside that varint. A larger one is
/// written once in the table's pool and named by its offset there, the most
/// used first so the commonest names are the shortest.
///
/// All little-endian; offsets are from the table's first byte.
///
///   header, 36 bytes:
///     +0  'CSM1'            +4  6 (the version)
///     +8  function count    +12 call-site count
///     +16 the BASE, relocated: the address code offsets count from
///     +20 checkpoint count  +24 the span: code bytes from the base the
///                               table covers (one past its last return address)
///     +28 the stream's offset             +32 the bitmap pool's offset
///   checkpoints, 12 bytes each, every 32nd call site, ascending:
///     +0 the ANCHOR there: the code offset (from the base) that site's delta
///        counts from   +4 the site's offset in the stream
///     +8 the offset of the record of the function it belongs to
///   the stream: per function, ascending by address
///     uleb  start - anchor (the anchor is the previous function's last
///           return address, 0 at first); the start is the new anchor
///     uleb  frame size << 3 | saved registers (bit 0 EBX, 1 ESI, 2 EDI)
///     uleb  its IR frame slots' bitmap reference
///     per call site, ascending:
///       uleb  return address - anchor, never 0; the return address is the
///             new anchor
///       uleb  bitmap reference << 4 | live registers (bit 0 EBX, 1 ESI,
///             2 EDI) | 8 when the call has no map (its frame is read whole)
///     uleb  0, the end of the function's call sites
///   the pool: per bitmap, uleb byte count, then the bytes
///
///   A BITMAP REFERENCE is odd for one held inline -- ref >> 1, bit b the word
///   at EBP - 4(b+1), at most sixteen words -- and even for a pooled one,
///   ref >> 1 its offset in the pool, bit b of byte k the word at
///   EBP - 4(8k + b + 1). An empty bitmap is 1.
///
/// THE LOOKUP STAYS LOGARITHMIC. A return address finds the last checkpoint
/// whose anchor is below it by binary search, then reads forward at most
/// thirty-two sites (and the function records between them) until it reaches
/// the address or passes it. Every site after a checkpoint lies above its
/// anchor, and every site before it at or below, so the site, if there is
/// one, is in that stretch. Nothing allocates.
///
/// A return address that is the next function's first byte (a call that
/// never returns, last in its function) is found as the call it is: the
/// sites are one ascending run across functions, not searched per function.
/// </summary>
public static class StackMapTable
{
    public const uint Magic = 0x314d5343;           // 'CSM1'
    public const uint Version = 6;
    public const int HeaderBytes = 36;
    public const int BaseOffset = 16;
    public const int CheckpointBytes = 12;
    public const int SitesPerCheckpoint = 32;

    /// <summary>The widest bitmap a reference carries inline.</summary>
    private const int InlineWords = 16;

    /// <summary>
    /// One function with call sites. <see cref="Start"/> is from the table's
    /// base; <see cref="Saved"/> is the callee-saved registers its prologue
    /// pushes, by hardware number; <see cref="Objects"/> are its IR frame
    /// slots, as word numbers (b the word at EBP - 4(b+1)), ascending.
    /// </summary>
    public sealed class Function
    {
        public int Start;
        public int FrameSize;
        public uint Saved;
        public int[] Objects = [];
        public List<Site> Sites = new();
    }

    /// <summary>
    /// One call: its return address from its function's start, whether it
    /// has no map, the callee-saved registers live across it by hardware
    /// number, and its live spill slots as word numbers, ascending.
    /// </summary>
    public readonly record struct Site(int Return, bool NoMap, uint Live, int[] Slots);

    /// <summary>The word numbers of a set of EBP-relative byte offsets (all negative).</summary>
    public static int[] Words(IEnumerable<int> offsets)
    {
        SortedSet<int> words = new();
        foreach (int off in offsets)
        {
            words.Add(-off / 4 - 1);
        }
        return words.ToArray();
    }

    /// <summary>
    /// The table's bytes. Its base word is zero, for the caller to relocate
    /// at <see cref="BaseOffset"/> when there is any function at all.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<Function> functions)
    {
        ChunkedBytes table = new();
        Encode(functions, table);
        return table.ToArray();
    }

    /// <summary>
    /// The table's bytes appended to <paramref name="into"/> (a section's own
    /// bytes): the stream of sites, the pool and the table itself are chunks,
    /// where a large unit's were three lists of it at once and an array
    /// copied out of the last, each one contiguous.
    /// </summary>
    public static void Encode(IReadOnlyList<Function> functions, ChunkedBytes into)
    {
        // THE POOL, MOST USED FIRST: a reference is a varint of its offset, so
        // the bitmaps named most often are the ones placed where it is short.
        Dictionary<string, (int Uses, int First, byte[] Bytes)> pooled = new(StringComparer.Ordinal);
        void Count(int[] words)
        {
            if (Inline(words)) return;
            string key = string.Join(',', words);
            if (pooled.TryGetValue(key, out var seen)) pooled[key] = (seen.Uses + 1, seen.First, seen.Bytes);
            else pooled.Add(key, (1, pooled.Count, Dense(words)));
        }
        foreach (Function f in functions)
        {
            Count(f.Objects);
            foreach (Site s in f.Sites) Count(s.Slots);
        }
        ChunkedBytes pool = new();
        Dictionary<string, int> placed = new(StringComparer.Ordinal);
        foreach (var entry in pooled.OrderByDescending(p => p.Value.Uses).ThenBy(p => p.Value.First))
        {
            placed.Add(entry.Key, pool.Count);
            Uleb(pool, (ulong)entry.Value.Bytes.Length);
            pool.AddRange(entry.Value.Bytes);
        }
        ulong Reference(int[] words)
        {
            if (Inline(words))
            {
                ulong bits = 0;
                foreach (int w in words) bits |= 1ul << w;
                return bits << 1 | 1;
            }
            return (ulong)placed[string.Join(',', words)] << 1;
        }

        ChunkedBytes stream = new();
        List<(long Anchor, int Site, int Record)> checkpoints = new();
        long anchor = 0;
        long span = 0;
        int sites = 0;
        foreach (Function f in functions)
        {
            if (f.Start < anchor) throw new InvalidOperationException("stack maps: functions out of address order");
            if (f.FrameSize < 0) throw new InvalidOperationException("stack maps: a negative frame size");
            int record = stream.Count;
            Uleb(stream, (ulong)(f.Start - anchor));
            anchor = f.Start;
            Uleb(stream, (ulong)f.FrameSize << 3 | Pack(f.Saved));
            Uleb(stream, Reference(f.Objects));
            foreach (Site s in f.Sites)
            {
                long at = (long)f.Start + s.Return;
                if (at <= anchor) throw new InvalidOperationException("stack maps: call sites out of address order");
                if (sites % SitesPerCheckpoint == 0) checkpoints.Add((anchor, stream.Count, record));
                Uleb(stream, (ulong)(at - anchor));
                anchor = at;
                Uleb(stream, Reference(s.Slots) << 4 | (s.NoMap ? 8u : Pack(s.Live)));
                sites++;
                span = at + 1;
            }
            Uleb(stream, 0);
        }

        int streamAt = HeaderBytes + checkpoints.Count * CheckpointBytes;
        int poolAt = streamAt + stream.Count;
        ChunkedBytes table = into;
        Put(table, Magic);
        Put(table, Version);
        Put(table, (uint)functions.Count);
        Put(table, (uint)sites);
        Put(table, 0);                      // the base, relocated
        Put(table, (uint)checkpoints.Count);
        Put(table, (uint)span);
        Put(table, (uint)streamAt);
        Put(table, (uint)poolAt);
        foreach (var c in checkpoints)
        {
            Put(table, (uint)c.Anchor);
            Put(table, (uint)(streamAt + c.Site));
            Put(table, (uint)(streamAt + c.Record));
        }
        table.AddRange(stream);
        table.AddRange(pool);
    }

    /// <summary>
    /// The functions of a version-6 table, or null when the bytes are not
    /// one: another version, or a table this cannot read to its end.
    /// </summary>
    public static List<Function>? Decode(IReadOnlyList<byte> b)
    {
        if (b.Count < HeaderBytes || U32(b, 0) != Magic || U32(b, 4) != Version) return null;
        int count = (int)U32(b, 8), streamAt = (int)U32(b, 28), poolAt = (int)U32(b, 32);
        if (streamAt < HeaderBytes || poolAt < streamAt || poolAt > b.Count) return null;
        List<Function> functions = new(count);
        try
        {
            int p = streamAt;
            long anchor = 0;
            int[] Bitmap(ulong reference)
            {
                List<int> words = new();
                if ((reference & 1) != 0)
                {
                    ulong bits = reference >> 1;
                    for (int w = 0; bits != 0; w++, bits >>= 1)
                        if ((bits & 1) != 0) words.Add(w);
                    return words.ToArray();
                }
                int q = poolAt + (int)(reference >> 1);
                int bytes = (int)ReadUleb(b, ref q);
                for (int k = 0; k < bytes; k++)
                    for (int bit = 0; bit < 8; bit++)
                        if ((b[q + k] >> bit & 1) != 0) words.Add(k * 8 + bit);
                return words.ToArray();
            }
            while (p < poolAt)
            {
                Function f = new();
                anchor += (long)ReadUleb(b, ref p);
                f.Start = (int)anchor;
                ulong frame = ReadUleb(b, ref p);
                f.FrameSize = (int)(frame >> 3);
                f.Saved = Unpack((uint)frame & 7);
                f.Objects = Bitmap(ReadUleb(b, ref p));
                while (true)
                {
                    ulong delta = ReadUleb(b, ref p);
                    if (delta == 0) break;
                    anchor += (long)delta;
                    ulong payload = ReadUleb(b, ref p);
                    bool noMap = (payload & 8) != 0;
                    f.Sites.Add(new Site((int)(anchor - f.Start), noMap, noMap ? 0 : Unpack((uint)payload & 7), Bitmap(payload >> 4)));
                }
                functions.Add(f);
            }
            if (p != poolAt || functions.Count != count) return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
        return functions;
    }

    private static bool Inline(int[] words) => words.Length == 0 || words[^1] < InlineWords;

    private static byte[] Dense(int[] words)
    {
        byte[] bytes = new byte[words[^1] / 8 + 1];
        foreach (int w in words) bytes[w / 8] |= (byte)(1 << (w % 8));
        return bytes;
    }

    /// <summary>EBX, ESI and EDI (hardware numbers 3, 6 and 7) as bits 0, 1 and 2.</summary>
    private static uint Pack(uint mask)
    {
        if ((mask & ~0xC8u) != 0) throw new InvalidOperationException("stack maps: a register other than EBX, ESI or EDI");
        return (mask >> 3 & 1) | (mask >> 5 & 2) | (mask >> 5 & 4);
    }

    private static uint Unpack(uint bits) => (bits & 1) << 3 | (bits & 2) << 5 | (bits & 4) << 5;

    private static uint U32(IReadOnlyList<byte> b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    private static void Put(List<byte> b, uint v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }

    private static void Put(ChunkedBytes b, uint v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }

    internal static void Uleb(ChunkedBytes into, ulong v)
    {
        do
        {
            byte x = (byte)(v & 0x7F);
            v >>= 7;
            into.Add(v != 0 ? (byte)(x | 0x80) : x);
        }
        while (v != 0);
    }

    internal static void Uleb(List<byte> into, ulong v)
    {
        do
        {
            byte x = (byte)(v & 0x7F);
            v >>= 7;
            into.Add(v != 0 ? (byte)(x | 0x80) : x);
        }
        while (v != 0);
    }

    internal static ulong ReadUleb(IReadOnlyList<byte> b, ref int p)
    {
        ulong v = 0;
        int shift = 0;
        while (true)
        {
            byte x = b[p++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
            shift += 7;
        }
    }
}
