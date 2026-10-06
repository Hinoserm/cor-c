namespace Corsac.Lang.Ir;

/// <summary>
/// THE FRAME TABLE'S BYTES: what each stretch of code is, for a fault or a
/// trace to name. The code generator writes a table per object
/// (X86.FrameTable), the link rewrites it when it cuts duplicate code
/// (Lto.DuplicateCutter) and moves its names into the image's one pool
/// (Lto.FramePool), and the runtime reads it (Runtime.LookupIn in
/// runtime/src/core/runtime.cor), which must change with this.
///
/// Read only by a linear walk -- a fault, a trace, a sample dump -- so every
/// field is a delta from the entry before it and nothing is indexed.
///
///   header, 20 bytes:
///     +0  'CFR3' (names in the table) or 'CFR4' (names in the pool)
///     +4  entry count
///     +8  the BASE, relocated: the first function's address
///     +12 'CFR3': the strings' offset; 'CFR4': the pool's address, relocated
///     +16 the line programs' offset
///   entries, in address order:
///     sleb  start - the end of the entry before (start + size); the first
///           counts from the base
///     uleb  size
///     sleb  name - the name of the entry before (the first counts from 0)
///     uleb  file: 0 the same as the entry before's (0 before the first),
///           else the file + 1
///     uleb  the length in bytes of its line program, 0 for none; the
///           programs follow in entry order, so where one starts is the
///           sum of the lengths before it
///   line programs: uleb count, then per line (uleb offset delta, sleb line delta)
///   'CFR3' only: the strings, each ending in a zero byte; a name or file is
///   the offset of one. In 'CFR4' it is the offset of a name in the pool.
///
/// THE POOL (__corsac_frame_pool), one per image, made at the link:
///
///   +0 'CFP1'   +4 token count N   +8 the names' offset
///   +12 N + 1 words: where token i starts, from the pool; it ends where
///       token i + 1 starts
///   the tokens' bytes, then the names: uleb token count, then each token's
///   index as a uleb
///
/// NAMES ARE SPELLED IN TOKENS because they repeat in pieces, not whole:
/// `Dictionary$KeyCollection$Corsac$Lang$Opt$...` and a hundred generic
/// bodies like it share almost every word. A token is a run of separators
/// (. $ _ ( ) , [ ] &lt; &gt; ` / and space) and the run of other bytes after it;
/// the most used tokens get the smallest indices, so most cost one byte.
/// </summary>
public static class FrameTableFormat
{
    public const uint Local = 0x33524643;           // 'CFR3'
    public const uint Pooled = 0x34524643;          // 'CFR4'
    public const uint PoolMagic = 0x31504643;       // 'CFP1'
    public const int HeaderBytes = 20;
    public const int BaseOffset = 8;
    public const int PoolOffset = 12;

    /// <summary>
    /// One function: its start from the base, its size, its name and file
    /// (offsets of strings, or of names in the pool), and the length of its
    /// line program, zero for none.
    /// </summary>
    public readonly record struct Entry(long Start, int Size, int Name, int File, int Program);

    /// <summary>A table's bytes, its base (and a pool's address) zero for the caller to relocate.</summary>
    public static byte[] Build(uint magic, IReadOnlyList<Entry> entries, IReadOnlyList<byte> programs, IReadOnlyList<byte>? strings)
    {
        List<byte> coded = new();
        long end = 0;
        int name = 0, file = 0;
        foreach (Entry e in entries)
        {
            Sleb(coded, e.Start - end);
            Uleb(coded, (ulong)e.Size);
            Sleb(coded, e.Name - name);
            Uleb(coded, e.File == file ? 0ul : (ulong)e.File + 1);
            Uleb(coded, (ulong)e.Program);
            end = e.Start + e.Size;
            name = e.Name;
            file = e.File;
        }
        int lines = HeaderBytes + coded.Count;
        List<byte> all = new(lines + programs.Count + (strings?.Count ?? 0));
        Put(all, magic);
        Put(all, (uint)entries.Count);
        Put(all, 0);                                    // the base, relocated
        Put(all, strings is null ? 0u : (uint)(lines + programs.Count));
        Put(all, (uint)lines);
        all.AddRange(coded);
        all.AddRange(programs);
        if (strings is not null) all.AddRange(strings);
        return all.ToArray();
    }

    /// <summary>
    /// The entries of the table at <paramref name="at"/> and where each one's
    /// line program starts (-1 for none), from the table; null when it is not
    /// a table of this format or does not read to its line programs.
    /// </summary>
    public static List<(Entry Entry, int ProgramAt)>? Read(IReadOnlyList<byte> b, int at, int size)
    {
        if (size < HeaderBytes || at + size > b.Count) return null;
        uint magic = U32(b, at);
        if (magic != Local && magic != Pooled) return null;
        int count = (int)U32(b, at + 4), lines = (int)U32(b, at + 16);
        if (lines < HeaderBytes || lines > size) return null;
        List<(Entry, int)> entries = new(count);
        int p = at + HeaderBytes;
        long end = 0;
        int name = 0, file = 0, program = lines;
        try
        {
            for (int i = 0; i < count; i++)
            {
                long start = end + ReadSleb(b, ref p);
                int length = (int)ReadUleb(b, ref p);
                name += (int)ReadSleb(b, ref p);
                ulong f = ReadUleb(b, ref p);
                if (f != 0) file = (int)(f - 1);
                int bytes = (int)ReadUleb(b, ref p);
                entries.Add((new Entry(start, length, name, file, bytes), bytes == 0 ? -1 : program));
                program += bytes;
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
        if (p != at + lines || program > size) return null;
        return entries;
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
