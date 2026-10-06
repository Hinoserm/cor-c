namespace Corsac.Lang.Ir;

/// <summary>
/// THE FRAME TABLE'S BYTES: what each stretch of code is, for a fault or a
/// trace to name. The code generator writes a table per object
/// (X86.FrameTable), the link rewrites it when it cuts duplicate code
/// (Lto.DuplicateCutter) and moves its names and its shared line programs
/// into the image's one pool (Lto.FramePool), and the runtime reads it
/// (Runtime.LookupIn and Runtime.LineIn in runtime/src/core/runtime.cor),
/// which must change with this.
///
/// Read only by a linear walk -- a fault, a trace, a sample dump -- so every
/// field is a delta from the entry before it and nothing is indexed.
///
///   header, 24 bytes:
///     +0  'CFR5' (names in the table) or 'CFR6' (names in the pool)
///     +4  entry count
///     +8  the BASE, relocated: the first function's address
///     +12 'CFR5': the strings' offset; 'CFR6': the pool's address, relocated
///     +16 the table's own line programs' offset
///     +20 the shared line programs' offset: 'CFR5' from the table, 'CFR6'
///         from the pool
///   entries, in address order, each a LEAD BYTE and then its fields:
///     lead  bits 0-2  start - the end of the entry before (start + size),
///                     the first counting from the base; 7 says it does not
///                     fit, and an sleb of it follows
///           bit 3     the file changed: a uleb of it follows (the first
///                     entry's "before" is file 0)
///           bits 4-5  its line program: 0 none, 1 its own, 2 a shared one
///           bits 6-7  the size's low two bits
///     [sleb start - the end before, when bits 0-2 are 7]
///     uleb  size &gt;&gt; 2
///     sleb  name - the name of the entry before (the first counts from 0)
///     [uleb file, when bit 3 is set]
///     own:    uleb its program's length in bytes, then sleb its first line
///             - the first line of the last entry with its own program (0
///             before any); own programs follow one another in entry order
///             from +16, so where one starts is the sum of the lengths before
///     shared: uleb where it starts among the shared programs
///   own line programs, then (in 'CFR5') the shared ones, then ('CFR5' only)
///   the strings, each ending in a zero byte; a name or file is the offset of
///   one. In 'CFR6' it is the offset of a name in the pool.
///
/// A LINE PROGRAM is the (code offset, line) pairs of one function, a pair
/// for each place the line changes, the line of a byte being that of the
/// last pair at or before it (none before the first). Its first line is
/// said outside its ops -- in the entry for its own, at its head for a
/// shared one -- so a function's own program costs no bytes for a line that
/// is usually a few past the function before's:
///     own:     uleb first offset, ops to the end of its length
///     shared:  uleb length of the rest, sleb first line, uleb first offset,
///              ops to the end of that length
/// Each op moves the offset forward by A (at least 1) and the line by L (never
/// 0: a pair that leaves the line alone is dropped) in the style of DWARF's
/// special opcodes, the common cases in one byte and nearly all the rest in
/// two. Taking D = L - 1 for L &gt; 0 and D = L otherwise:
///     b &lt; 160        A = b / 2 + 1 (1..80), D = b % 2 (L 1 or 2)
///     160 &lt;= b &lt; 255 c = (b - 160) * 256 + the next byte;
///                    A = c / 48 + 1 (1..506), D = c % 48 - 24 (L -24..24)
///     b = 255        uleb A, sleb L
/// The split was fitted to the compiler's own image: nearly four ops in ten
/// are one byte and all but a few of the rest two, 1.74 bytes on average
/// against 2.16 for a uleb and an sleb.
///
/// SHARED PROGRAMS are those more than one function carries -- the same
/// generic body compiled into many units, a one-line accessor -- stored once,
/// the most used first so their offsets are the shortest, and only where that
/// costs less than each carrying its own. A table shares among its own
/// functions; at the link the whole image shares in the pool.
///
/// THE POOL (__corsac_frame_pool), one per image, made at the link:
///
///   +0 'CFP2'   +4 token count N   +8 the names' offset
///   +12 N + 1 words: where token i starts, from the pool; it ends where
///       token i + 1 starts
///   the tokens' bytes, then the names: uleb token count, then each token's
///   index as a uleb; then the image's shared line programs, where every
///   'CFR6' table's +20 says
///
/// NAMES ARE SPELLED IN TOKENS because they repeat in pieces, not whole:
/// `Dictionary$KeyCollection$Corsac$Lang$Opt$...` and a hundred generic
/// bodies like it share almost every word. A token is a run of separators
/// (. $ _ ( ) , [ ] &lt; &gt; ` / and space) and the run of other bytes after it;
/// the most used tokens get the smallest indices, so most cost one byte.
/// </summary>
public static class FrameTableFormat
{
    public const uint Local = 0x35524643;           // 'CFR5'
    public const uint Pooled = 0x36524643;          // 'CFR6'
    public const uint PoolMagic = 0x32504643;       // 'CFP2'
    public const int HeaderBytes = 24;
    public const int BaseOffset = 8;
    public const int PoolOffset = 12;
    public const int OwnOffset = 16;
    public const int SharedOffset = 20;

    // The ops' split (see the class comment); Runtime.LineIn has the same numbers.
    private const int OneByte = 160;
    private const int OneByteLines = 2;
    private const int TwoByteLines = 48;
    private const int TwoByteLowest = -24;
    private const int Escape = 255;

    /// <summary>
    /// One function: its start from the base, its size, its name and file
    /// (offsets of strings, or of names in the pool), and its line program as
    /// pairs, normalised (<see cref="Normal"/>), empty for none.
    /// </summary>
    public readonly record struct Entry(long Start, int Size, int Name, int File, (int Offset, int Line)[] Lines);

    /// <summary>
    /// The pairs a reader needs: offsets ascending, one pair per offset (the
    /// last said wins, as it did when read), and none that leaves the line
    /// where it was. The answer for every byte is unchanged.
    /// </summary>
    public static (int Offset, int Line)[] Normal(IEnumerable<(int Offset, int Line)> lines)
    {
        List<(int Offset, int Line)> kept = new();
        foreach ((int offset, int line) in lines)
        {
            if (kept.Count > 0 && offset <= kept[^1].Offset) kept[^1] = (kept[^1].Offset, line);
            else kept.Add((offset, line));
            if (kept.Count >= 2 && kept[^1].Line == kept[^2].Line) kept.RemoveAt(kept.Count - 1);
        }
        return kept.ToArray();
    }

    /// <summary>A program's ops after its first pair.</summary>
    private static void Ops(List<byte> into, (int Offset, int Line)[] lines)
    {
        for (int i = 1; i < lines.Length; i++)
        {
            int a = lines[i].Offset - lines[i - 1].Offset;
            int l = lines[i].Line - lines[i - 1].Line;
            int d = l > 0 ? l - 1 : l;
            if (a >= 1 && a <= OneByte / OneByteLines && l != 0 && d >= 0 && d < OneByteLines)
            {
                into.Add((byte)((a - 1) * OneByteLines + d));
            }
            else if (a >= 1 && a <= (Escape - OneByte) * 256 / TwoByteLines && d >= TwoByteLowest && d < TwoByteLowest + TwoByteLines && l != 0)
            {
                int c = (a - 1) * TwoByteLines + d - TwoByteLowest;
                into.Add((byte)(OneByte + (c >> 8)));
                into.Add((byte)c);
            }
            else
            {
                into.Add(Escape);
                StackMapTable.Uleb(into, (ulong)a);
                Sleb(into, l);
            }
        }
    }

    /// <summary>A function's own program: its first line is the entry's.</summary>
    private static byte[] Own((int Offset, int Line)[] lines)
    {
        List<byte> b = new();
        StackMapTable.Uleb(b, (ulong)lines[0].Offset);
        Ops(b, lines);
        return b.ToArray();
    }

    /// <summary>A shared program, its length and first line at its head.</summary>
    private static byte[] SharedBytes((int Offset, int Line)[] lines)
    {
        List<byte> rest = new();
        Sleb(rest, lines[0].Line);
        StackMapTable.Uleb(rest, (ulong)lines[0].Offset);
        Ops(rest, lines);
        List<byte> b = new();
        StackMapTable.Uleb(b, (ulong)rest.Count);
        b.AddRange(rest);
        return b.ToArray();
    }

    /// <summary>
    /// The line programs a table, or a whole image, shares: every program
    /// counted, then the ones worth sharing laid out once, most used first.
    /// </summary>
    public sealed class LinePrograms
    {
        private readonly Dictionary<(int, int)[], (int Uses, int First)> _seen = new(PairsComparer.Instance);
        private readonly Dictionary<(int, int)[], int> _at = new(PairsComparer.Instance);
        private readonly List<byte> _bytes = new();

        public void Count((int Offset, int Line)[] lines)
        {
            if (lines.Length == 0) return;
            if (_seen.TryGetValue(lines, out var seen)) _seen[lines] = (seen.Uses + 1, seen.First);
            else _seen.Add(lines, (1, _seen.Count));
        }

        /// <summary>
        /// Lays out the programs used more than once, where storing one copy
        /// and a reference from each user costs less than each carrying its
        /// own (its length, its first line's delta, guessed at two bytes, and
        /// its bytes).
        /// </summary>
        public byte[] Seal()
        {
            foreach (var p in _seen.Where(p => p.Value.Uses > 1).OrderByDescending(p => p.Value.Uses).ThenBy(p => p.Value.First))
            {
                byte[] shared = SharedBytes(p.Key);
                byte[] own = Own(p.Key);
                long apart = p.Value.Uses * (long)(UlebLength(own.Length) + 2 + own.Length);
                long together = p.Value.Uses * (long)UlebLength(_bytes.Count) + shared.Length;
                if (together >= apart) continue;
                _at.Add(p.Key, _bytes.Count);
                _bytes.AddRange(shared);
            }
            return _bytes.ToArray();
        }

        /// <summary>Where a program starts among the shared ones, -1 when it is not shared.</summary>
        public int Find((int Offset, int Line)[] lines) => lines.Length > 0 && _at.TryGetValue(lines, out int at) ? at : -1;
    }

    private sealed class PairsComparer : IEqualityComparer<(int, int)[]>
    {
        public static readonly PairsComparer Instance = new();

        public bool Equals((int, int)[]? x, (int, int)[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode((int, int)[] pairs)
        {
            HashCode hash = new();
            foreach ((int a, int b) in pairs) { hash.Add(a); hash.Add(b); }
            return hash.ToHashCode();
        }
    }

    private static int UlebLength(long v)
    {
        int n = 1;
        while (v >= 128) { v >>= 7; n++; }
        return n;
    }

    /// <summary>
    /// A table's bytes, its base (and a pool's address) zero for the caller to
    /// relocate. A 'CFR5' table (<paramref name="strings"/> given) shares its
    /// programs among its own entries and carries the shared ones itself; a
    /// 'CFR6' one refers to <paramref name="shared"/>, sealed, whose bytes lie
    /// <paramref name="sharedAt"/> into the pool.
    /// </summary>
    public static byte[] Build(uint magic, IReadOnlyList<Entry> entries, IReadOnlyList<byte>? strings, LinePrograms? shared = null, int sharedAt = 0)
    {
        byte[] sharedBytes = [];
        if (shared is null)
        {
            shared = new LinePrograms();
            foreach (Entry e in entries) shared.Count(e.Lines);
            sharedBytes = shared.Seal();
        }
        List<byte> coded = new();
        List<byte> own = new();
        long end = 0;
        int name = 0, file = 0, line = 0;
        foreach (Entry e in entries)
        {
            long pad = e.Start - end;
            int at = shared.Find(e.Lines);
            int kind = e.Lines.Length == 0 ? 0 : at >= 0 ? 2 : 1;
            int lead = (pad >= 0 && pad < 7 ? (int)pad : 7) | (e.File != file ? 8 : 0) | kind << 4 | (e.Size & 3) << 6;
            coded.Add((byte)lead);
            if ((lead & 7) == 7) Sleb(coded, pad);
            Uleb(coded, (ulong)e.Size >> 2);
            Sleb(coded, e.Name - name);
            if (e.File != file) Uleb(coded, (ulong)e.File);
            if (kind == 1)
            {
                byte[] program = Own(e.Lines);
                Uleb(coded, (ulong)program.Length);
                Sleb(coded, e.Lines[0].Line - line);
                line = e.Lines[0].Line;
                own.AddRange(program);
            }
            else if (kind == 2)
            {
                Uleb(coded, (ulong)at);
            }
            end = e.Start + e.Size;
            name = e.Name;
            file = e.File;
        }
        int ownAt = HeaderBytes + coded.Count;
        int sharedHere = ownAt + own.Count;
        int stringsAt = sharedHere + sharedBytes.Length;
        List<byte> all = new(stringsAt + (strings?.Count ?? 0));
        Put(all, magic);
        Put(all, (uint)entries.Count);
        Put(all, 0);                                    // the base, relocated
        Put(all, strings is null ? 0u : (uint)stringsAt);
        Put(all, (uint)ownAt);
        Put(all, strings is null ? (uint)sharedAt : (uint)sharedHere);
        all.AddRange(coded);
        all.AddRange(own);
        all.AddRange(sharedBytes);
        if (strings is not null) all.AddRange(strings);
        return all.ToArray();
    }

    /// <summary>
    /// The entries of the 'CFR5' table at <paramref name="at"/>, their line
    /// programs decoded; null when it is not such a table or does not read
    /// to its strings.
    /// </summary>
    public static List<Entry>? Read(IReadOnlyList<byte> b, int at, int size)
    {
        if (size < HeaderBytes || at + size > b.Count) return null;
        if (U32(b, at) != Local) return null;
        int count = (int)U32(b, at + 4), stringsAt = (int)U32(b, at + 12), ownAt = (int)U32(b, at + OwnOffset), sharedAt = (int)U32(b, at + SharedOffset);
        if (ownAt < HeaderBytes || ownAt > sharedAt || sharedAt > stringsAt || stringsAt > size) return null;
        List<Entry> entries = new(count);
        int p = at + HeaderBytes, own = at + ownAt;
        long end = 0;
        int name = 0, file = 0, line = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                int lead = b[p++];
                long pad = (lead & 7) == 7 ? ReadSleb(b, ref p) : lead & 7;
                long start = end + pad;
                int length = (int)(ReadUleb(b, ref p) << 2) | (lead >> 6 & 3);
                name += (int)ReadSleb(b, ref p);
                if ((lead & 8) != 0) file = (int)ReadUleb(b, ref p);
                (int, int)[] lines = [];
                switch (lead >> 4 & 3)
                {
                    case 0:
                        break;
                    case 1:
                        int bytes = (int)ReadUleb(b, ref p);
                        line += (int)ReadSleb(b, ref p);
                        if (own + bytes > at + sharedAt) return null;
                        lines = Decode(b, own, own + bytes, line);
                        own += bytes;
                        break;
                    case 2:
                        int q = at + sharedAt + (int)ReadUleb(b, ref p);
                        int rest = (int)ReadUleb(b, ref q);
                        int stop = q + rest;
                        if (stop > at + stringsAt) return null;
                        int first = (int)ReadSleb(b, ref q);
                        lines = Decode(b, q, stop, first);
                        break;
                    default:
                        return null;
                }
                entries.Add(new Entry(start, length, name, file, lines));
                end = start + length;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
        if (p != at + ownAt || own != at + sharedAt) return null;
        return entries;
    }

    /// <summary>The pairs of a program whose first line is <paramref name="line"/>, from its first offset to <paramref name="end"/>.</summary>
    private static (int, int)[] Decode(IReadOnlyList<byte> b, int p, int end, int line)
    {
        List<(int, int)> pairs = new();
        int offset = (int)ReadUleb(b, ref p);
        pairs.Add((offset, line));
        while (p < end)
        {
            int op = b[p++];
            int a, l;
            if (op < OneByte)
            {
                a = op / OneByteLines + 1;
                l = op % OneByteLines + 1;
            }
            else if (op < Escape)
            {
                int c = (op - OneByte) << 8 | b[p++];
                a = c / TwoByteLines + 1;
                l = c % TwoByteLines + TwoByteLowest;
                if (l >= 0) l++;
            }
            else
            {
                a = (int)ReadUleb(b, ref p);
                l = (int)ReadSleb(b, ref p);
            }
            offset += a;
            line += l;
            pairs.Add((offset, line));
        }
        if (p != end) throw new IndexOutOfRangeException();
        return pairs.ToArray();
    }

    /// <summary>
    /// The image's name pool, built as names are added: each distinct text
    /// once, spelled in shared tokens (see the class comment).
    /// </summary>
    public sealed class NamePool
    {
        private readonly List<byte[]> _texts = new();
        private readonly Dictionary<byte[], int> _ids = new(BytesComparer.Instance);
        private int[] _at = [];

        /// <summary>The id of a text, the same for the same bytes.</summary>
        public int Add(byte[] text)
        {
            if (_ids.TryGetValue(text, out int id)) return id;
            id = _texts.Count;
            _texts.Add(text);
            _ids.Add(text, id);
            return id;
        }

        /// <summary>Where the name with this id lies in the names, once <see cref="Build"/> has run.</summary>
        public int Offset(int id) => _at[id];

        public byte[] Build()
        {
            // Tokens by use, most used first, ties in the order first met.
            Dictionary<byte[], (int Uses, int First)> tokens = new(BytesComparer.Instance);
            List<List<byte[]>> spelled = new(_texts.Count);
            foreach (byte[] text in _texts)
            {
                List<byte[]> parts = Tokens(text);
                spelled.Add(parts);
                foreach (byte[] t in parts)
                {
                    if (tokens.TryGetValue(t, out var seen)) tokens[t] = (seen.Uses + 1, seen.First);
                    else tokens.Add(t, (1, tokens.Count));
                }
            }
            List<byte[]> order = tokens.OrderByDescending(t => t.Value.Uses).ThenBy(t => t.Value.First).Select(t => t.Key).ToList();
            Dictionary<byte[], int> index = new(BytesComparer.Instance);
            for (int i = 0; i < order.Count; i++) index.Add(order[i], i);

            List<byte> names = new();
            _at = new int[_texts.Count];
            for (int i = 0; i < _texts.Count; i++)
            {
                _at[i] = names.Count;
                Uleb(names, (ulong)spelled[i].Count);
                foreach (byte[] t in spelled[i]) Uleb(names, (ulong)index[t]);
            }

            int bytesAt = 12 + (order.Count + 1) * 4;
            List<byte> pool = new();
            Put(pool, PoolMagic);
            Put(pool, (uint)order.Count);
            Put(pool, 0);                               // the names' offset, below
            int at = bytesAt;
            foreach (byte[] t in order)
            {
                Put(pool, (uint)at);
                at += t.Length;
            }
            Put(pool, (uint)at);
            foreach (byte[] t in order) pool.AddRange(t);
            int namesAt = pool.Count;
            pool[8] = (byte)namesAt;
            pool[9] = (byte)(namesAt >> 8);
            pool[10] = (byte)(namesAt >> 16);
            pool[11] = (byte)(namesAt >> 24);
            pool.AddRange(names);
            return pool.ToArray();
        }

        private static bool Separator(byte c) => c < 128 && ".$_()[],<>`/ ".IndexOf((char)c) >= 0;

        /// <summary>A run of separators and the run of other bytes after it, each a token.</summary>
        private static List<byte[]> Tokens(byte[] text)
        {
            List<byte[]> parts = new();
            int i = 0;
            while (i < text.Length)
            {
                int start = i;
                while (i < text.Length && Separator(text[i])) i++;
                while (i < text.Length && !Separator(text[i])) i++;
                byte[] part = new byte[i - start];
                Array.Copy(text, start, part, 0, part.Length);
                parts.Add(part);
            }
            return parts;
        }
    }

    public sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public static readonly BytesComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x is null || y is null || x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        // FNV-1a over the bytes.
        public int GetHashCode(byte[] bytes)
        {
            uint hash = 2166136261;
            foreach (byte b in bytes) hash = (hash ^ b) * 16777619;
            return (int)hash;
        }
    }

    private static uint U32(IReadOnlyList<byte> b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    private static void Put(List<byte> b, uint v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }

    public static void Uleb(List<byte> into, ulong v) => StackMapTable.Uleb(into, v);

    public static void Sleb(List<byte> into, long v)
    {
        while (true)
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            bool sign = (b & 0x40) != 0;
            if ((v == 0 && !sign) || (v == -1 && sign))
            {
                into.Add(b);
                return;
            }
            into.Add((byte)(b | 0x80));
        }
    }

    public static ulong ReadUleb(IReadOnlyList<byte> b, ref int p) => StackMapTable.ReadUleb(b, ref p);

    public static long ReadSleb(IReadOnlyList<byte> b, ref int p)
    {
        long v = 0;
        int shift = 0;
        while (true)
        {
            byte x = b[p++];
            v |= (long)(x & 0x7F) << shift;
            shift += 7;
            if ((x & 0x80) == 0)
            {
                if (shift < 64 && (x & 0x40) != 0) v |= -1L << shift;
                return v;
            }
        }
    }
}
