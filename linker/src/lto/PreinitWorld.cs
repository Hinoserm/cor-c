using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// THE PROGRAM'S MEMORY AS THE LINK SEES IT, for running whole type
/// initialisers before the program does (Preinit, stage 2): every unit's data
/// by its symbol, the statics the link defines, and the objects an
/// initialiser makes, each a region of bytes with the words in it that hold
/// an address. A machine (Preinit.Machine, the compiler's) runs the IR over
/// it; what a run that finishes leaves is laid down as the image's data.
///
/// AS IF THE INITIALISER RAN AT STARTUP: a type with no static constructor of
/// its own is beforefieldinit in .NET, and its initialiser may run at any time
/// before its statics are first touched -- at startup among them. So one is
/// run here over the statics as they stand when the image starts, the types
/// it touches made first as they would be, and its writes to another type's
/// statics (a gate registering its name) are kept with it. A type with a
/// static constructor runs at its first touch exactly, and is never run here.
///
/// A RUN IS ONE TRANSACTION: every region it writes is saved first, and a run
/// that does anything the link cannot do -- a system call, a port, a throw,
/// code with no IR -- is undone whole, the types it entered left to run
/// as they always did.
/// </summary>
public sealed class PreinitWorld
{
    /// <summary>A value a register or a word holds.</summary>
    public abstract record Val;
    /// <summary>A number: an integer's bits, or a float's.</summary>
    public sealed record Num(long Bits) : Val;
    /// <summary>An address into a region.</summary>
    public sealed record Ptr(Region Region, long Offset) : Val;
    /// <summary>A routine's address, or a label's within one.</summary>
    public sealed record Code(string Label, long Offset) : Val;
    /// <summary>A unit's relocation, read on first use: the symbol, which may be data or code.</summary>
    private sealed record Unresolved(string Symbol, long Addend) : Val;

    /// <summary>A symbol's address, found when it is first read through.</summary>
    public static Val Lazy(string symbol, long addend) => new Unresolved(symbol, addend);

    public enum RegionKind { Data, Storage, Heap, Frame, Thread }

    /// <summary>Bytes, and the words of them that hold an address (their bytes zero).</summary>
    public sealed class Region
    {
        public required RegionKind Kind { get; init; }
        /// <summary>The symbol a unit's data or the link's storage is defined by.</summary>
        public string? Symbol { get; init; }
        public required byte[] Bytes;
        public Dictionary<int, Val> Refs = new();
        /// <summary>Read-only data: a descriptor, a literal. Never written.</summary>
        public bool ReadOnly { get; init; }
        /// <summary>The section's alignment, for laying it down again.</summary>
        public int Align { get; init; } = 16;
        internal int SavedIn = -1;
        /// <summary>Written by a run that was kept: laid down again by the link.</summary>
        public bool Written;
        public override string ToString() => Symbol ?? Kind.ToString();
    }

    /// <summary>Thrown by anything the link cannot do; the run is undone.</summary>
    public sealed class Refused(string why) : Exception(why);

    /// <summary>A type whose initialiser may be run, and the symbols of it the runs need.</summary>
    public sealed record TypeEntry(Preinit.TypeNote Note)
    {
        public bool Frozen;
    }

    public int WordSize { get; }
    public int ArrayCountOffset { get; }
    public bool LongMode { get; }

    private readonly Dictionary<string, (ObjectFile Object, Symbol Symbol)> _defined = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Region> _regions = new(StringComparer.Ordinal);
    private readonly Dictionary<Section, SortedRelocations> _sorted = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Section, byte[]> _contents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, TypeEntry> _byInitializer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeEntry> _byReady = new(StringComparer.Ordinal);
    private readonly List<Region> _heap = new();
    private readonly List<(Region Region, byte[] Bytes, Dictionary<int, Val> Refs, bool Written)> _undo = new();
    private readonly HashSet<TypeEntry> _entering = new();
    private readonly List<TypeEntry> _entered = new();
    private int _transaction = -1, _transactions;
    private Region? _thread;

    /// <summary>The instructions a run may take, and the bytes it may make, before it is given up.</summary>
    public long Steps;
    public const long MostSteps = 20_000_000;
    private long _made;
    private const long MostMade = 16L << 20;

    /// <summary>What each kept run read of another type's mutable statics (Preinit's report).</summary>
    public readonly List<string> Report = new();
    private readonly HashSet<string> _readsThisRun = new(StringComparer.Ordinal);

    /// <summary>The link's inputs, where the machine finds each routine's IR.</summary>
    public IReadOnlyList<ObjectFile> Objects { get; }

    public PreinitWorld(IReadOnlyList<ObjectFile> objects, int wordSize, int arrayCountOffset, bool longMode)
    {
        Objects = objects;
        WordSize = wordSize;
        ArrayCountOffset = arrayCountOffset;
        LongMode = longMode;
        foreach (ObjectFile obj in objects)
            foreach (Symbol s in obj.Symbols)
                if (s.IsDefined && s.Section!.Kind != SectionKind.Note) _defined.TryAdd(s.Name, (obj, s));
    }

    // ---- the types -----------------------------------------------------------------

    public void AddType(Preinit.TypeNote note)
    {
        TypeEntry entry = new(note);
        _byInitializer.TryAdd(note.Initializer, entry);
        _byReady.TryAdd(note.Ready, entry);
    }

    /// <summary>The type whose initialiser this routine is (its StaticInit$), or null.</summary>
    public TypeEntry? InitializerOf(string label) => _byInitializer.GetValueOrDefault(label);

    public IEnumerable<TypeEntry> Types => _byInitializer.Values;

    /// <summary>
    /// A TYPE TOUCHED: nothing when it is made or being made (as the runtime's
    /// EnterTypeInitialization answers its own thread), its body run otherwise
    /// and its state word set to completed. Only a beforefieldinit type's.
    /// </summary>
    public void Enter(TypeEntry type, Action<string> runBody)
    {
        Region ready = Data(type.Note.Ready) ?? throw new Refused("no state word '" + type.Note.Ready + "'");
        Val state = Load(new Ptr(ready, 0), WordSize, signed: false);
        if (state is Num { Bits: 1 } || _entering.Contains(type)) return;
        if (state is not Num { Bits: 0 }) throw new Refused("'" + type.Note.Owner + "' was given up on before");
        if (!type.Note.BeforeFieldInit) throw new Refused("'" + type.Note.Owner + "' has a static constructor, run when it is first touched");
        _entering.Add(type);
        _entered.Add(type);
        runBody(type.Note.Body);
        _entering.Remove(type);
        Store(new Ptr(ready, 0), WordSize, new Num(1));
    }

    // ---- transactions --------------------------------------------------------------

    public void Begin()
    {
        _transaction = ++_transactions;
        _undo.Clear();
        _entered.Clear();
        _entering.Clear();
        _readsThisRun.Clear();
        Steps = 0;
        _made = 0;
    }

    /// <summary>The run kept: the types it made are made, unless a word it leaves holds a stack's or a thread's address.</summary>
    public List<TypeEntry> Commit()
    {
        foreach (var saved in _undo)
            foreach (Val v in saved.Region.Refs.Values)
                if (v is Ptr { Region.Kind: RegionKind.Frame or RegionKind.Thread })
                {
                    Rollback();
                    throw new Refused("'" + saved.Region + "' keeps an address of a stack or a thread");
                }
        foreach (var saved in _undo) saved.Region.Written = true;
        List<TypeEntry> made = _entered.ToList();
        foreach (TypeEntry t in made) t.Frozen = true;
        if (_readsThisRun.Count > 0 && made.Count > 0)
            Report.Add(made[0].Note.Owner + " read " + string.Join(", ", _readsThisRun.Order(StringComparer.Ordinal)));
        _undo.Clear();
        _transaction = -1;
        return made;
    }

    public void Rollback()
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            var (region, bytes, refs, written) = _undo[i];
            region.Bytes = bytes;
            region.Refs = refs;
            region.Written = written;
            region.SavedIn = -1;
        }
        _undo.Clear();
        _transaction = -1;
    }

    private void Save(Region r)
    {
        if (r.Kind is RegionKind.Frame or RegionKind.Thread || r.SavedIn == _transaction) return;
        _undo.Add((r, (byte[])r.Bytes.Clone(), new Dictionary<int, Val>(r.Refs), r.Written));
        r.SavedIn = _transaction;
    }

    // ---- regions -------------------------------------------------------------------

    /// <summary>
    /// A symbol's data: a unit's, read from its section on first use, or the
    /// link's storage (DefineStorage). Null for a symbol defined nowhere.
    /// </summary>
    public Region? Data(string symbol)
    {
        if (_regions.TryGetValue(symbol, out Region? had)) return had;
        if (!_defined.TryGetValue(symbol, out var at) || at.Symbol.Section!.Kind == SectionKind.Code) return null;
        Section section = at.Symbol.Section!;
        long size = at.Symbol.Size;
        if (size <= 0)
        {
            // No size written: up to the next symbol in the section, or its end.
            long next = section.Size;
            foreach (Symbol s in at.Object.Symbols)
                if (ReferenceEquals(s.Section, section) && s.Offset > at.Symbol.Offset && s.Offset < next) next = s.Offset;
            size = next - at.Symbol.Offset;
        }
        if (size < 0 || size > 64L << 20) throw new Refused("'" + symbol + "' is no data the link can read");
        byte[] bytes = new byte[size];
        if (section.Kind != SectionKind.Uninitialised)
        {
            if (!_contents.TryGetValue(section, out byte[]? content)) _contents[section] = content = section.Content();
            System.Array.Copy(content, at.Symbol.Offset, bytes, 0, Math.Min(size, content.Length - at.Symbol.Offset));
        }
        Region region = new()
        {
            Kind = RegionKind.Data, Symbol = symbol, Bytes = bytes,
            ReadOnly = section.Kind == SectionKind.ReadOnlyData, Align = Math.Max(1, Math.Min(16, section.Align)),
        };
        if (section.Kind != SectionKind.Uninitialised)
        {
            if (!_sorted.TryGetValue(section, out SortedRelocations? sorted)) _sorted[section] = sorted = new SortedRelocations(section);
            foreach (Relocation r in sorted.Within(at.Symbol.Offset, at.Symbol.Offset + size))
            {
                if (r.Kind is not (RelocKind.Abs32 or RelocKind.Abs64)) continue;
                region.Refs[(int)(r.Offset - at.Symbol.Offset)] = new Unresolved(r.Symbol, r.Addend);
            }
        }
        _regions[symbol] = region;
        return region;
    }

    /// <summary>Storage the link defines itself: a candidate's word or flag (Preinit.Candidate).</summary>
    public Region DefineStorage(string symbol, int bytes)
    {
        if (_regions.TryGetValue(symbol, out Region? had)) return had;
        Region region = new() { Kind = RegionKind.Storage, Symbol = symbol, Bytes = new byte[bytes] };
        _regions[symbol] = region;
        return region;
    }

    /// <summary>What a symbol's address is: its data, or a routine's address.</summary>
    public Val Address(string symbol, long offset)
    {
        if (Data(symbol) is Region r) return new Ptr(r, offset);
        if (_defined.TryGetValue(symbol, out var at) && at.Symbol.Section!.Kind == SectionKind.Code) return new Code(symbol, offset);
        throw new Refused("'" + symbol + "' is defined nowhere the link can see");
    }

    /// <summary>Whether a routine of this name is code in the image.</summary>
    public bool IsCode(string symbol) => _defined.TryGetValue(symbol, out var at) && at.Symbol.Section!.Kind == SectionKind.Code;

    /// <summary>An object's bytes, zeroed, as an allocator hands them out.</summary>
    public Ptr Allocate(long bytes)
    {
        if (bytes < 0 || bytes > 16L << 20 || (_made += bytes) > MostMade) throw new Refused("an allocation of " + bytes + " bytes");
        Region region = new() { Kind = RegionKind.Heap, Bytes = new byte[(bytes + WordSize - 1) / WordSize * WordSize] };
        _heap.Add(region);
        return new Ptr(region, 0);
    }

    /// <summary>An object the link made itself (stage 1), as a region.</summary>
    public Region Adopt(byte[] bytes)
    {
        Region region = new() { Kind = RegionKind.Heap, Bytes = bytes };
        _heap.Add(region);
        return region;
    }

    public Ptr Frame(int bytes) => new(new Region { Kind = RegionKind.Frame, Bytes = new byte[Math.Max(0, bytes)] }, 0);

    /// <summary>The running thread's block: zero, as a thread's is before anything is asked of it.</summary>
    public Ptr ThreadBlock() => new(_thread ??= new Region { Kind = RegionKind.Thread, Bytes = new byte[64 * 1024] }, 0);

    // ---- loads and stores ----------------------------------------------------------

    public Val Load(Val address, int size, bool signed)
    {
        if (address is not Ptr p) throw new Refused(address is Num { Bits: 0 } ? "a null dereference" : "a load from " + address);
        Region r = p.Region;
        if (p.Offset < 0 || p.Offset + size > r.Bytes.Length) throw new Refused("a load past '" + r + "'");
        int at = (int)p.Offset;
        foreach ((int offset, Val v) in r.Refs)
        {
            if (offset + WordSize <= at || offset >= at + size) continue;
            if (offset != at || size != WordSize) throw new Refused("part of an address loaded from '" + r + "'");
            return Resolve(r, offset, v);
        }
        if (r.Kind == RegionKind.Data && r.Symbol is string s && !r.ReadOnly) Noted(s);
        long bits = 0;
        for (int b = size - 1; b >= 0; b--) bits = (bits << 8) | r.Bytes[at + b];
        if (size < 8) bits = signed ? (bits << (64 - 8 * size)) >> (64 - 8 * size) : bits & ((1L << (8 * size)) - 1);
        return new Num(bits);
    }

    private Val Resolve(Region r, int offset, Val v)
    {
        if (v is not Unresolved u) return v;
        Val resolved = Address(u.Symbol, u.Addend);
        r.Refs[offset] = resolved;
        return resolved;
    }

    public void Store(Val address, int size, Val value)
    {
        if (address is not Ptr p) throw new Refused(address is Num { Bits: 0 } ? "a null dereference" : "a store to " + address);
        Region r = p.Region;
        if (r.ReadOnly) throw new Refused("a store into read-only '" + r + "'");
        if (p.Offset < 0 || p.Offset + size > r.Bytes.Length) throw new Refused("a store past '" + r + "'");
        Save(r);
        int at = (int)p.Offset;
        foreach (int offset in r.Refs.Keys.Where(o => o + WordSize > at && o < at + size).ToList()) r.Refs.Remove(offset);
        System.Array.Clear(r.Bytes, at, size);
        switch (value)
        {
            case Num n:
                for (int b = 0; b < size; b++) r.Bytes[at + b] = (byte)(n.Bits >> (8 * b));
                break;
            case Ptr or Code or Unresolved:
                if (size != WordSize) throw new Refused("an address stored in " + size + " bytes");
                r.Refs[at] = value;
                break;
        }
    }

    /// <summary>Bytes copied, the addresses among them with them.</summary>
    public void Copy(Val to, Val from, long count)
    {
        if (count == 0) return;
        if (to is not Ptr d || from is not Ptr s) throw new Refused("a copy through " + to + " from " + from);
        if (count < 0 || s.Offset < 0 || d.Offset < 0 || s.Offset + count > s.Region.Bytes.Length || d.Offset + count > d.Region.Bytes.Length)
            throw new Refused("a copy past '" + d.Region + "' or '" + s.Region + "'");
        if (d.Region.ReadOnly) throw new Refused("a copy into read-only '" + d.Region + "'");
        Save(d.Region);
        int from0 = (int)s.Offset, to0 = (int)d.Offset, n = (int)count;
        List<(int, Val)> moving = new();
        foreach ((int offset, Val v) in s.Region.Refs)
        {
            if (offset + WordSize <= from0 || offset >= from0 + n) continue;
            if (offset < from0 || offset + WordSize > from0 + n) throw new Refused("part of an address copied from '" + s.Region + "'");
            moving.Add((offset - from0 + to0, Resolve(s.Region, offset, v)));
        }
        byte[] bytes = s.Region.Bytes.AsSpan(from0, n).ToArray();
        foreach (int offset in d.Region.Refs.Keys.Where(o => o + WordSize > to0 && o < to0 + n).ToList()) d.Region.Refs.Remove(offset);
        bytes.CopyTo(d.Region.Bytes, to0);
        foreach ((int offset, Val v) in moving) d.Region.Refs[offset] = v;
    }

    public void Fill(Val to, byte value, long count)
    {
        if (count == 0) return;
        if (to is not Ptr d || count < 0 || d.Offset < 0 || d.Offset + count > d.Region.Bytes.Length) throw new Refused("a fill past " + to);
        if (d.Region.ReadOnly) throw new Refused("a fill of read-only '" + d.Region + "'");
        Save(d.Region);
        int at = (int)d.Offset, n = (int)count;
        foreach (int offset in d.Region.Refs.Keys.Where(o => o + WordSize > at && o < at + n).ToList()) d.Region.Refs.Remove(offset);
        System.Array.Fill(d.Region.Bytes, value, at, n);
    }

    /// <summary>A read of another type's mutable static, for the report: its owner from the symbol (s_Owner_field).</summary>
    private void Noted(string symbol)
    {
        if (!symbol.StartsWith("s_", StringComparison.Ordinal) || symbol.Contains("StaticReady$", StringComparison.Ordinal)) return;
        foreach (TypeEntry t in _entered)
            if (symbol.StartsWith("s_" + t.Note.Owner + "_", StringComparison.Ordinal)) return;
        // The collector's own state, read by every store's barrier.
        if (symbol.StartsWith("s_Runtime_", StringComparison.Ordinal) || symbol.StartsWith("s_Gc_", StringComparison.Ordinal)) return;
        if (_transaction >= 0) _readsThisRun.Add(symbol);
    }

    // ---- laying it down ------------------------------------------------------------

    /// <summary>Every region a kept run wrote that a unit defined: the link defines it again, as it was left.</summary>
    public IEnumerable<Region> Overridden => _regions.Values.Where(r => r.Kind == RegionKind.Data && r.Written);

    public IEnumerable<Region> Storages => _regions.Values.Where(r => r.Kind == RegionKind.Storage);

    /// <summary>A word's address as the link writes it: a symbol and an addend, or null for an object it lays down.</summary>
    public static (string? Symbol, long Addend, Region? Object) Target(Val v) => v switch
    {
        Ptr { Region.Kind: RegionKind.Heap } p => (null, p.Offset, p.Region),
        Ptr { Region.Symbol: string s } p => (s, p.Offset, null),
        Code c => (c.Label, c.Offset, null),
        Unresolved u => (u.Symbol, u.Addend, null),
        _ => throw new InvalidOperationException("preinit: an address the link cannot write: " + v),
    };
}
