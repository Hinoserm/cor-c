namespace Corsac.Lang.Ir;

/// <summary>
/// THE STACK-MAP TABLE, written by the code generator (X86Backend.EmitStackMaps),
/// rewritten by the link when it cuts duplicate code (Lto.DuplicateCutter), and
/// read by the collector (Gc.MapSite in runtime/src/core/gc.cor). One codec for
/// the two writers, so they cannot disagree; the runtime's reader is the third
/// copy of the layout and must change with this one.
///
/// VARINTS, DELTAS AND SHARED BITMAPS (version 0, one layout until a release).
/// Sixteen bytes a function and eight a call site, with the bitmaps of large
/// frames a pooled copy each, were about half a megabyte in ring 0, resident
/// for as long as the machine runs. Here a call site is two varints -- its return
/// address from the one before it, and its registers and bitmap in one -- and
/// a bitmap of up to sixteen words lives inside that varint. A larger one is
/// written once in the table's pool and named by its offset there, the most
/// used first so the commonest names are the shortest.
///
/// All little-endian; offsets are from the table's first byte.
///
///   header, 36 bytes:
///     +0  'CSM1'            +4  0 (the version)
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
///   the pool: per bitmap, uleb n << 1 | sparse, then for a dense one n
///     bytes (bit b of byte k the word 8k + b), for a sparse one n ulebs:
///     the first word, then each word's distance past the one before less
///     one -- whichever is shorter. A large frame's live spill slots are a
///     few words among hundreds: dense, the GUI module's pool was 590 KB of
///     zero bytes, resident.
///     Or a DIFFERENCE, when shorter: uleb 0 (a dense bitmap of no bytes,
///     never pooled), uleb the offset in the pool of a ROOT -- a bitmap of
///     the same function written whole, never itself a difference; the
///     varint may be padded with 0x80 bytes -- then the words the root has
///     and this lacks and the words this adds, each a uleb count and a list
///     as a sparse bitmap's. Sites of one frame keep most of the same
///     slots: the GUI module's pool went from 486 KB to about 220.
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
///
/// ONE CODEC FOR EVERY TARGET (StackMapLayout). What differs between
/// machines is how wide a word is, which callee-saved registers a map can
/// name, and how wide a code address is; everything else -- the stream, the
/// checkpoints, the bitmaps, the pool, the lookup -- is the same. Version 0
/// above is i386's, kept byte for byte (a 32-bit image, its runtime and a
/// kernel built before this read and write exactly what they did). Every
/// other target writes VERSION 1, which says its own shape in its header:
///
///   header, 56 bytes:
///     +0  'CSM1'            +4  1 (the version)
///     +8  function count    +12 call-site count
///     +16 the BASE as the signed 32-bit distance from this word to it
///         (PC32), +20 zero: an image loaded anywhere describes itself
///         without the loader, and the table is read-only in a shared object
///     +24 checkpoint count  +28 the span
///     +32 the stream's offset             +36 the bitmap pool's offset
///     +40 the word's bytes (4 or 8)       +41 R, how many registers a map names
///     +42 the R registers' hardware numbers, a byte each, in the order
///         their bits are packed (bit k the k-th) -- the order the prologue
///         saves them in, the k-th present at base - frame size - word(k+1);
///         unused bytes 0, to +56
///   the stream as version 0's, with R bits where it has three:
///     uleb  frame size << R | saved registers
///     uleb  bitmap reference << (R + 1) | 1 << R when the call has no map
///           | live registers
///   A bitmap's word b is the word at BASE REGISTER - word(b + 1): EBP's on
///   i386, RBP's on x86-64, the frame pointer's on any other.
/// </summary>
public static class StackMapTable
{
    public const uint Magic = 0x314d5343;           // 'CSM1'
    public const uint Version = 0;
    /// <summary>Version 0's header (i386); a layout says its own (StackMapLayout.HeaderBytes).</summary>
    public const int HeaderBytes = 36;
    public const int BaseOffset = 16;
    /// <summary>The version every target but i386 writes: its word, its registers and its address width in the header.</summary>
    public const uint GeneralVersion = 1;
    public const int GeneralHeaderBytes = 56;
    /// <summary>The most registers a general table's header names.</summary>
    public const int MostRegisters = 14;
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
    public static int[] Words(IEnumerable<int> offsets) => Words(offsets, 4);

    /// <summary>The word numbers of a set of byte offsets from the frame's base register (all negative), for words of `wordBytes`.</summary>
    public static int[] Words(IEnumerable<int> offsets, int wordBytes)
    {
        SortedSet<int> words = new();
        foreach (int off in offsets)
        {
            words.Add(-off / wordBytes - 1);
        }
        return words.ToArray();
    }

    /// <summary>
    /// The table's bytes. Its base word is zero, for the caller to relocate
    /// at <see cref="BaseOffset"/> when there is any function at all.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<Function> functions) => Encode(functions, StackMapLayout.I386);

    /// <summary>The table's bytes in a target's layout.</summary>
    public static byte[] Encode(IReadOnlyList<Function> functions, StackMapLayout layout)
    {
        ChunkedBytes table = new();
        Encode(functions, table, layout);
        return table.ToArray();
    }

    /// <summary>
    /// The table's bytes appended to <paramref name="into"/> (a section's own
    /// bytes): the stream of sites, the pool and the table itself are chunks,
    /// where a large unit's were three lists of it at once and an array
    /// copied out of the last, each one contiguous.
    /// </summary>
    public static void Encode(IReadOnlyList<Function> functions, ChunkedBytes into) => Encode(functions, into, StackMapLayout.I386);

    /// <summary>The table's bytes in a target's layout, appended to <paramref name="into"/>.</summary>
    public static void Encode(IReadOnlyList<Function> functions, ChunkedBytes into, StackMapLayout layout)
    {
        int registerBits = layout.Registers.Length;
        // THE POOL, MOST USED FIRST: a reference is a varint of its offset, so
        // the bitmaps named most often are the ones placed where it is short.
        //
        // A BITMAP AS ANOTHER'S DIFFERENCE. A large frame's call sites keep
        // most of the same spill slots live, so a bitmap first named in a
        // function is written as one of that function's whole bitmaps (a
        // root) less the words it lacks and plus the words it adds, when
        // that is shorter: two thirds of the GUI module's pool. A difference
        // names only a root, so the collector's walk is the root's words,
        // one compare each against the next lacking one, and then the
        // added ones.
        Dictionary<string, (int Uses, int First, int[] Words)> pooled = new(StringComparer.Ordinal);
        Dictionary<string, int[]> rootOf = new(StringComparer.Ordinal);
        foreach (Function f in functions)
        {
            List<int[]> roots = new();
            void Count(int[] words)
            {
                if (Inline(words)) return;
                string key = string.Join(',', words);
                if (pooled.TryGetValue(key, out var seen))
                {
                    pooled[key] = (seen.Uses + 1, seen.First, seen.Words);
                    return;
                }
                pooled.Add(key, (1, pooled.Count, words));
                int best = Pooled(words).Length;
                int[]? root = null;
                for (int r = Math.Max(0, roots.Count - RootsAsked); r < roots.Count; r++)
                {
                    int cost = 1 + RootReferenceGuess + DifferenceBytes(roots[r], words);
                    if (cost < best) (best, root) = (cost, roots[r]);
                }
                if (root is null) roots.Add(words);
                else rootOf.Add(key, root);
            }
            Count(f.Objects);
            foreach (Site s in f.Sites) Count(s.Slots);
        }
        List<string> order = pooled.OrderByDescending(p => p.Value.Uses).ThenBy(p => p.Value.First).Select(p => p.Key).ToList();
        Dictionary<string, byte[]> bytes = new(StringComparer.Ordinal);
        Dictionary<string, int> widths = new(StringComparer.Ordinal);
        foreach (string key in order)
        {
            bytes[key] = rootOf.TryGetValue(key, out int[]? root) ? Difference(root, pooled[key].Words, 0, widths, key) : Pooled(pooled[key].Words);
        }
        // Each difference's root offset as the layout places it. A varint
        // that would shrink keeps its width (padded), so no entry ever
        // shortens and the offsets settle.
        Dictionary<string, int> placed = new(StringComparer.Ordinal);
        while (true)
        {
            int offset = 0;
            foreach (string key in order)
            {
                placed[key] = offset;
                offset += bytes[key].Length;
            }
            bool moved = false;
            foreach (string key in order)
            {
                if (!rootOf.TryGetValue(key, out int[]? root)) continue;
                byte[] again = Difference(root, pooled[key].Words, placed[string.Join(',', root)], widths, key);
                if (again.Length != bytes[key].Length) moved = true;
                bytes[key] = again;
            }
            if (!moved) break;
        }
        ChunkedBytes pool = new();
        foreach (string key in order)
        {
            pool.AddRange(bytes[key]);
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
            Uleb(stream, (ulong)f.FrameSize << registerBits | layout.Pack(f.Saved));
            Uleb(stream, Reference(f.Objects));
            foreach (Site s in f.Sites)
            {
                long at = (long)f.Start + s.Return;
                if (at <= anchor) throw new InvalidOperationException("stack maps: call sites out of address order");
                if (sites % SitesPerCheckpoint == 0) checkpoints.Add((anchor, stream.Count, record));
                Uleb(stream, (ulong)(at - anchor));
                anchor = at;
                Uleb(stream, Reference(s.Slots) << (registerBits + 1) | (s.NoMap ? 1ul << registerBits : layout.Pack(s.Live)));
                sites++;
                span = at + 1;
            }
            Uleb(stream, 0);
        }

        int streamAt = layout.HeaderBytes + checkpoints.Count * CheckpointBytes;
        int poolAt = streamAt + stream.Count;
        ChunkedBytes table = into;
        Put(table, Magic);
        Put(table, layout.Version);
        Put(table, (uint)functions.Count);
        Put(table, (uint)sites);
        Put(table, 0);                      // the base, relocated
        if (layout.Version != Version) Put(table, 0);   // its upper half, where an address is eight bytes
        Put(table, (uint)checkpoints.Count);
        Put(table, (uint)span);
        Put(table, (uint)streamAt);
        Put(table, (uint)poolAt);
        if (layout.Version != Version)
        {
            table.Add((byte)layout.WordBytes);
            table.Add((byte)registerBits);
            for (int k = 0; k < MostRegisters; k++) table.Add(k < registerBits ? (byte)layout.Registers[k] : (byte)0);
        }
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
    /// The functions of a table, or null when the bytes are not one: another
    /// magic or version, or a table this cannot read to its end.
    /// </summary>
    public static List<Function>? Decode(IReadOnlyList<byte> b) => Decode(b, out _);

    /// <summary>The functions of a table and the layout it is written in (its header's), or null when the bytes are not one.</summary>
    public static List<Function>? Decode(IReadOnlyList<byte> b, out StackMapLayout layout)
    {
        layout = StackMapLayout.I386;
        if (b.Count < HeaderBytes || U32(b, 0) != Magic) return null;
        uint version = U32(b, 4);
        if (version == GeneralVersion)
        {
            if (b.Count < GeneralHeaderBytes) return null;
            int wordBytes = b[40], named = b[41];
            if (wordBytes is not (4 or 8) || named > MostRegisters) return null;
            int[] registers = new int[named];
            for (int k = 0; k < named; k++) registers[k] = b[42 + k];
            layout = new StackMapLayout(wordBytes, registers);
            // (A general table in i386's own shape is still a general table: its header says so.)
            layout = layout.AsGeneral();
        }
        else if (version != Version) return null;
        int registerBits = layout.Registers.Length;
        ulong registerMask = (1ul << registerBits) - 1;
        bool general = version == GeneralVersion;
        int count = (int)U32(b, 8), streamAt = (int)U32(b, general ? 32 : 28), poolAt = (int)U32(b, general ? 36 : 32);
        if (streamAt < layout.HeaderBytes || poolAt < streamAt || poolAt > b.Count) return null;
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
                return Pooled(poolAt + (int)(reference >> 1));
            }
            int[] Pooled(int q)
            {
                List<int> words = new();
                ulong head = ReadUleb(b, ref q);
                int n = (int)(head >> 1);
                if (head == 0)
                {
                    int[] root = Pooled(poolAt + (int)ReadUleb(b, ref q));
                    SortedSet<int> those = new(root);
                    foreach (int w in Listed(ref q)) those.Remove(w);
                    foreach (int w in Listed(ref q)) those.Add(w);
                    return those.ToArray();
                }
                if ((head & 1) != 0)
                {
                    int word = -1;
                    for (int k = 0; k < n; k++)
                    {
                        word += (int)ReadUleb(b, ref q) + 1;
                        words.Add(word);
                    }
                    return words.ToArray();
                }
                for (int k = 0; k < n; k++)
                    for (int bit = 0; bit < 8; bit++)
                        if ((b[q + k] >> bit & 1) != 0) words.Add(k * 8 + bit);
                return words.ToArray();
            }
            List<int> Listed(ref int q)
            {
                List<int> words = new();
                int n = (int)ReadUleb(b, ref q);
                int word = -1;
                for (int k = 0; k < n; k++)
                {
                    word += (int)ReadUleb(b, ref q) + 1;
                    words.Add(word);
                }
                return words;
            }
            while (p < poolAt)
            {
                Function f = new();
                anchor += (long)ReadUleb(b, ref p);
                f.Start = (int)anchor;
                ulong frame = ReadUleb(b, ref p);
                f.FrameSize = (int)(frame >> registerBits);
                f.Saved = layout.Unpack((uint)(frame & registerMask));
                f.Objects = Bitmap(ReadUleb(b, ref p));
                while (true)
                {
                    ulong delta = ReadUleb(b, ref p);
                    if (delta == 0) break;
                    anchor += (long)delta;
                    ulong payload = ReadUleb(b, ref p);
                    bool noMap = (payload >> registerBits & 1) != 0;
                    f.Sites.Add(new Site((int)(anchor - f.Start), noMap, noMap ? 0 : layout.Unpack((uint)(payload & registerMask)), Bitmap(payload >> (registerBits + 1))));
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

    /// <summary>How many of a function's latest roots a new bitmap is measured against.</summary>
    private const int RootsAsked = 256;
    /// <summary>A root's offset as a difference names it, while the layout is not known.</summary>
    private const int RootReferenceGuess = 3;

    /// <summary>The bytes of the two lists that turn <paramref name="root"/> into <paramref name="words"/>.</summary>
    private static int DifferenceBytes(int[] root, int[] words)
    {
        int lacking = 0, adding = 0, bytes = 0, lastLacking = -1, lastAdding = -1;
        int i = 0, j = 0;
        while (i < root.Length || j < words.Length)
        {
            if (j == words.Length || i < root.Length && root[i] < words[j])
            {
                bytes += UlebLength((ulong)(root[i] - lastLacking - 1));
                lastLacking = root[i++];
                lacking++;
            }
            else if (i == root.Length || words[j] < root[i])
            {
                bytes += UlebLength((ulong)(words[j] - lastAdding - 1));
                lastAdding = words[j++];
                adding++;
            }
            else
            {
                i++;
                j++;
            }
        }
        return bytes + UlebLength((ulong)lacking) + UlebLength((ulong)adding);
    }

    /// <summary>
    /// A bitmap as a root's difference: a zero head (a dense bitmap of no
    /// bytes, which is never pooled), the root's offset in the pool, then the
    /// words the root has and this lacks and the words this adds, each a
    /// count and the sparse list. The offset is written at least as wide as
    /// it was before (<paramref name="widths"/>).
    /// </summary>
    private static byte[] Difference(int[] root, int[] words, int rootAt, Dictionary<string, int> widths, string key)
    {
        List<int> lacking = new(), adding = new();
        int i = 0, j = 0;
        while (i < root.Length || j < words.Length)
        {
            if (j == words.Length || i < root.Length && root[i] < words[j]) lacking.Add(root[i++]);
            else if (i == root.Length || words[j] < root[i]) adding.Add(words[j++]);
            else { i++; j++; }
        }
        int width = Math.Max(UlebLength((ulong)rootAt), widths.GetValueOrDefault(key));
        widths[key] = width;
        ChunkedBytes bytes = new();
        bytes.Add(0);
        for (int k = 0; k < width - 1; k++) bytes.Add((byte)(rootAt >> 7 * k & 0x7F | 0x80));
        bytes.Add((byte)(rootAt >> 7 * (width - 1)));
        foreach (List<int> list in new[] { lacking, adding })
        {
            Uleb(bytes, (ulong)list.Count);
            int previous = -1;
            foreach (int w in list)
            {
                Uleb(bytes, (ulong)(w - previous - 1));
                previous = w;
            }
        }
        return bytes.ToArray();
    }

    private static int UlebLength(ulong v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    private static bool Inline(int[] words) => words.Length == 0 || words[^1] < InlineWords;

    /// <summary>A bitmap as the pool holds it: its head and its body, dense or sparse, whichever is shorter.</summary>
    private static byte[] Pooled(int[] words)
    {
        byte[] bytes = new byte[words[^1] / 8 + 1];
        foreach (int w in words) bytes[w / 8] |= (byte)(1 << (w % 8));
        ChunkedBytes dense = new();
        Uleb(dense, (ulong)bytes.Length << 1);
        dense.AddRange(bytes);
        ChunkedBytes sparse = new();
        Uleb(sparse, (ulong)words.Length << 1 | 1);
        int previous = -1;
        foreach (int w in words)
        {
            Uleb(sparse, (ulong)(w - previous - 1));
            previous = w;
        }
        return sparse.Count < dense.Count ? sparse.ToArray() : dense.ToArray();
    }


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

/// <summary>
/// WHAT OF A STACK-MAP TABLE DEPENDS ON THE MACHINE (StackMapTable): how
/// wide a word of the frame is, and which callee-saved registers a map can
/// name -- by hardware number, in the order the function's prologue saves
/// them, which is the order of their bits in every packed mask. The k-th of
/// them a function saves lies at its frame base less its frame size less
/// (k + 1) words, counting only those it saves: what the collector walks to
/// find where a caller's register was kept.
///
/// i386 is EBX, ESI, EDI over four-byte words and writes version 0, the
/// table as it has always been. x86-64 (System V) is RBX, R12, R13, R14,
/// R15 over eight-byte words. Another machine names its own: Alpha's s0-s5,
/// ARM's r4-r11, AArch64's x19-x28, MIPS's s0-s7 -- up to fourteen, by a
/// hardware number below thirty-two each.
/// </summary>
public sealed class StackMapLayout
{
    public int WordBytes { get; }
    public int[] Registers { get; }
    private readonly bool _general;

    public StackMapLayout(int wordBytes, int[] registers)
    {
        if (wordBytes is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(wordBytes), "a stack map's word is four or eight bytes");
        if (registers.Length > StackMapTable.MostRegisters) throw new ArgumentOutOfRangeException(nameof(registers), "a stack map names at most fourteen registers");
        foreach (int r in registers)
            if (r < 0 || r > 31) throw new ArgumentOutOfRangeException(nameof(registers), "a stack map's register is a hardware number below thirty-two");
        if (registers.Distinct().Count() != registers.Length) throw new ArgumentException("a stack map's registers are each named once", nameof(registers));
        WordBytes = wordBytes;
        Registers = registers;
    }

    private StackMapLayout(StackMapLayout of, bool general) : this(of.WordBytes, of.Registers) => _general = general;

    /// <summary>i386: four-byte words, EBX, ESI and EDI (hardware numbers 3, 6, 7). Version 0.</summary>
    public static readonly StackMapLayout I386 = new(4, new[] { 3, 6, 7 });

    /// <summary>x86-64, System V: eight-byte words, RBX, R12, R13, R14, R15 (hardware numbers 3, 12-15).</summary>
    public static readonly StackMapLayout X64 = new(8, new[] { 3, 12, 13, 14, 15 });

    /// <summary>This layout written as a general table even where it is i386's shape (a table decoded as one stays one).</summary>
    public StackMapLayout AsGeneral() => _general ? this : new StackMapLayout(this, true);

    /// <summary>Version 0 for i386's own layout, the general version for every other.</summary>
    public uint Version => !_general && WordBytes == 4 && Registers.Length == 3 && Registers[0] == 3 && Registers[1] == 6 && Registers[2] == 7
        ? StackMapTable.Version : StackMapTable.GeneralVersion;

    public int HeaderBytes => Version == StackMapTable.Version ? StackMapTable.HeaderBytes : StackMapTable.GeneralHeaderBytes;

    /// <summary>A mask by hardware number as the table packs it: bit k the k-th register named.</summary>
    public uint Pack(uint mask)
    {
        uint packed = 0, left = mask;
        for (int k = 0; k < Registers.Length; k++)
            if ((mask >> Registers[k] & 1) != 0) { packed |= 1u << k; left &= ~(1u << Registers[k]); }
        if (left != 0) throw new InvalidOperationException("stack maps: a register the target's layout does not name (mask " + mask.ToString("x") + ")");
        return packed;
    }

    /// <summary>A packed mask by hardware number.</summary>
    public uint Unpack(uint bits)
    {
        uint mask = 0;
        for (int k = 0; k < Registers.Length; k++)
            if ((bits >> k & 1) != 0) mask |= 1u << Registers[k];
        return mask;
    }
}
