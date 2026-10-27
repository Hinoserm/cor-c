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
/// ONLY WHERE NOTHING COULD TELL: a type with no static constructor of its
/// own is beforefieldinit in .NET, and may be made at any time before its
/// statics are first touched; .NET makes it at the first touch, and a run
/// here must leave what that would have left, whenever that is. So, as
/// NativeAOT preinitialises: a run writes only what its own type holds --
/// its statics and the objects it makes -- and reads, beyond those and the
/// image's read-only data, only another type's readonly statics, once that
/// type is made, and strings. A counter of another type's bumped (1030), a
/// name registered with another type's table, a mutable static of another
/// type read (a processor count the boot sets): each could differ at the
/// first touch, and the type is made at run time as it always was. A type
/// with a static constructor runs at its first touch exactly, and is never
/// run here.
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
    /// <summary>A routine's address, or a label's within one; Local, the unit whose own symbol it is.</summary>
    public sealed record Code(string Label, long Offset, ObjectFile? Local = null) : Val;
    /// <summary>A relocation, read on first use: the symbol, which may be data or code, as the unit that names it sees it.</summary>
    private sealed record Unresolved(string Symbol, long Addend, ObjectFile? From) : Val;

    /// <summary>A symbol's address, found when it is first read through.</summary>
    public static Val Lazy(string symbol, long addend) => new Unresolved(symbol, addend, null);

    public enum RegionKind { Data, Storage, Heap, Frame, Thread }

    /// <summary>Bytes, and the words of them that hold an address (their bytes zero).</summary>
    public sealed class Region
    {
        public required RegionKind Kind { get; init; }
        /// <summary>The symbol a unit's data or the link's storage is defined by.</summary>
        public string? Symbol { get; init; }
        /// <summary>A unit's own symbol, not global: that unit's, which no other unit's name reaches.</summary>
        public ObjectFile? Local { get; init; }
        /// <summary>The type whose this is: a static's owner, or the type whose run made an object. Null for the image's own data.</summary>
        public string? Maker { get; init; }
        public required byte[] Bytes;
        public Dictionary<int, Val> Refs = new();
        /// <summary>Read-only data: a descriptor, a literal. Never written.</summary>
        public bool ReadOnly { get; init; }
        /// <summary>A symbol the image does not define, made for the run alone (Address): never laid down, nor kept.</summary>
        public bool Dropped { get; init; }
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

    // THE IMAGE'S NAMES, and each unit's own: a unit's literal str_51 is its
    // own, another unit's str_51 another, and each unit finds its own first.
    private readonly Dictionary<string, (ObjectFile Object, Symbol Symbol)> _defined = new(StringComparer.Ordinal);
    private readonly Dictionary<ObjectFile, Dictionary<string, Symbol>> _locals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ObjectFile, int> _number = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Region> _regions = new(StringComparer.Ordinal);
    private readonly Dictionary<Section, SortedRelocations> _sorted = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Section, byte[]> _contents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, TypeEntry> _byInitializer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeEntry> _byReady = new(StringComparer.Ordinal);
    private readonly List<Region> _heap = new();
    private readonly List<(Region Region, byte[] Bytes, Dictionary<int, Val> Refs, bool Written)> _undo = new();
    private readonly HashSet<TypeEntry> _entering = new();
    private readonly List<TypeEntry> _entered = new();
    // THE TYPE WHOSE BODY IS RUNNING, innermost last: whose statics and objects a run may write.
    private readonly List<TypeEntry> _running = new();
    private readonly Dictionary<string, Preinit.StaticNote> _statics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readyWords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);

    /// <summary>The descriptor every string object names: a string is never written after it is made, and anyone may read one.</summary>
    public string? StringDescriptor { get; init; }

    /// <summary>The unit-local symbols the laid-down data names, each to be given its alias (Preinit.Lay).</summary>
    public readonly List<(ObjectFile Unit, string Local, string Alias)> Aliases = new();
    private int _transaction = -1, _transactions;
    private Region? _thread;

    /// <summary>The instructions a run may take, and the bytes it may make, before it is given up.</summary>
    public long Steps;
    public const long MostSteps = 20_000_000;
    private long _made;
    private const long MostMade = 16L << 20;

    /// <summary>What was made at the link, and why each type that was not runs at its first touch (Preinit's report).</summary>
    public readonly List<string> Report = new();

    /// <summary>The link's inputs, where the machine finds each routine's IR.</summary>
    public IReadOnlyList<ObjectFile> Objects { get; }

    public PreinitWorld(IReadOnlyList<ObjectFile> objects, int wordSize, int arrayCountOffset, bool longMode)
    {
        Objects = objects;
        WordSize = wordSize;
        ArrayCountOffset = arrayCountOffset;
        LongMode = longMode;
        foreach (ObjectFile obj in objects)
        {
            _number.TryAdd(obj, _number.Count);
            foreach (Symbol s in obj.Symbols)
            {
                if (!s.IsDefined || s.Section!.Kind == SectionKind.Note) continue;
                if (s.Global) _defined.TryAdd(s.Name, (obj, s));
                else (_locals.TryGetValue(obj, out var own) ? own : _locals[obj] = new(StringComparer.Ordinal)).TryAdd(s.Name, s);
            }
        }
    }

    /// <summary>A symbol as a unit sees it: its own first, then the image's.</summary>
    private (ObjectFile Object, Symbol Symbol, bool Local)? Find(string symbol, ObjectFile? from)
    {
        if (from is not null && _locals.TryGetValue(from, out var own) && own.TryGetValue(symbol, out Symbol? mine)) return (from, mine, true);
        if (_defined.TryGetValue(symbol, out var at)) return (at.Object, at.Symbol, false);
        // A unit nobody named: its own symbol, when one unit alone has it.
        if (from is null)
        {
            (ObjectFile, Symbol)? only = null;
            foreach ((ObjectFile obj, var names) in _locals)
                if (names.TryGetValue(symbol, out Symbol? s)) { if (only is not null) return null; only = (obj, s); }
            if (only is (ObjectFile o, Symbol found)) return (o, found, true);
        }
        return null;
    }

    // ---- the types -----------------------------------------------------------------

    public void AddType(Preinit.TypeNote note)
    {
        TypeEntry entry = new(note);
        _byInitializer.TryAdd(note.Initializer, entry);
        _byReady.TryAdd(note.Ready, entry);
        _readyWords.Add(note.Ready);
        _owners.Add(note.Owner);
    }

    public void AddStatic(Preinit.StaticNote note) => _statics.TryAdd(note.Symbol, note);

    /// <summary>Whether a type is made: one with no initialiser always is, one with one once a kept run or this run made it.</summary>
    private bool Made(string owner)
        => !_owners.Contains(owner) || _byInitializer.Values.Any(t => t.Note.Owner == owner && (t.Frozen || (_entered.Contains(t) && !_entering.Contains(t))));

    private string? Running => _running.Count > 0 ? _running[^1].Note.Owner : null;

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
        Region ready = Data(type.Note.Ready, null) ?? throw new Refused("no state word '" + type.Note.Ready + "'");
        Val state = Load(new Ptr(ready, 0), WordSize, signed: false);
        if (state is Num { Bits: 1 } || _entering.Contains(type)) return;
        if (state is not Num { Bits: 0 }) throw new Refused("'" + type.Note.Owner + "' was given up on before");
        if (!type.Note.BeforeFieldInit) throw new Refused("'" + type.Note.Owner + "' has a static constructor, run when it is first touched");
        _entering.Add(type);
        _entered.Add(type);
        _running.Add(type);
        runBody(type.Note.Body);
        Store(new Ptr(ready, 0), WordSize, new Num(1));
        _running.RemoveAt(_running.Count - 1);
        _entering.Remove(type);
    }

    // ---- transactions --------------------------------------------------------------

    public void Begin()
    {
        _transaction = ++_transactions;
        _undo.Clear();
        _entered.Clear();
        _entering.Clear();
        _running.Clear();
        Steps = 0;
        _made = 0;
    }

    /// <summary>
    /// The run kept: the types it made are made -- unless a word it leaves
    /// holds a stack's or a thread's address, which the image cannot, or a
    /// unit's own symbol in a unit the link-time optimiser may make again
    /// before the data is laid down (one carrying IR): the alias it would be
    /// named by has nowhere to go then.
    /// </summary>
    public List<TypeEntry> Commit()
    {
        // WHAT THE RUN LEAVES, not all it wrote: the data and storage it
        // changed and the objects they reach. A temporary it made and dropped
        // -- a span over a literal on its way to ToArray -- is laid down by
        // nobody, whatever it named.
        HashSet<Region> kept = new(ReferenceEqualityComparer.Instance);
        Stack<Region> walk = new();
        foreach (var saved in _undo)
            if (saved.Region.Kind is RegionKind.Data or RegionKind.Storage && kept.Add(saved.Region)) walk.Push(saved.Region);
        while (walk.TryPop(out Region? r))
            foreach (int offset in r.Refs.Keys.ToList())
            {
                Val v;
                try { v = Resolve(r, offset, r.Refs[offset]); }
                catch (Refused refused) { Rollback(); throw new Refused("'" + r + "' keeps " + refused.Message); }
                string? why = v switch
                {
                    Ptr { Region.Kind: RegionKind.Frame or RegionKind.Thread } => "an address of a stack or a thread",
                    Ptr { Region.Dropped: true } p => "'" + p.Region + "', which the image does not define",
                    Ptr { Region.Local: ObjectFile own } p when MadeAgain(own) => "'" + p.Region + "', a unit's own",
                    Code { Local: ObjectFile own } c when MadeAgain(own) => "'" + c.Label + "', a unit's own",
                    _ => null,
                };
                if (why is not null)
                {
                    Rollback();
                    throw new Refused("'" + r + "' keeps " + why);
                }
                if (v is Ptr { Region.Kind: RegionKind.Heap } heap && kept.Add(heap.Region)) walk.Push(heap.Region);
            }
        foreach (var saved in _undo) saved.Region.Written = true;
        List<TypeEntry> made = _entered.ToList();
        foreach (TypeEntry t in made) t.Frozen = true;
        _undo.Clear();
        _transaction = -1;
        return made;
    }

    /// <summary>Whether the link-time optimiser may make a unit again: it carries its IR.</summary>
    private static bool MadeAgain(ObjectFile unit) => unit.Sections.Any(section => section.Name == IrArchive.SectionName);

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
    public Region? Data(string symbol, ObjectFile? from)
    {
        if (_regions.TryGetValue(symbol, out Region? had) && had.Kind == RegionKind.Storage) return had;
        if (Find(symbol, from) is not var (owner, sym, local) || sym.Section!.Kind == SectionKind.Code) return null;
        string key = local ? symbol + "\u0001" + _number[owner] : symbol;
        if (_regions.TryGetValue(key, out had)) return had;
        var at = (Object: owner, Symbol: sym);
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
            Kind = RegionKind.Data, Symbol = symbol, Local = local ? owner : null, Bytes = bytes,
            Maker = !local && _statics.TryGetValue(symbol, out Preinit.StaticNote? st) ? st.Owner : null,
            ReadOnly = section.Kind == SectionKind.ReadOnlyData, Align = Math.Max(1, Math.Min(16, section.Align)),
        };
        if (section.Kind != SectionKind.Uninitialised)
        {
            if (!_sorted.TryGetValue(section, out SortedRelocations? sorted)) _sorted[section] = sorted = new SortedRelocations(section);
            foreach (Relocation r in sorted.Within(at.Symbol.Offset, at.Symbol.Offset + size))
            {
                if (r.Kind is not (RelocKind.Abs32 or RelocKind.Abs64)) continue;
                region.Refs[(int)(r.Offset - at.Symbol.Offset)] = new Unresolved(r.Symbol, r.Addend, owner);
            }
        }
        _regions[key] = region;
        return region;
    }

    /// <summary>Storage the link defines itself: a candidate's word or flag (Preinit.Candidate), its type's.</summary>
    public Region DefineStorage(string symbol, int bytes, string owner)
    {
        if (_regions.TryGetValue(symbol, out Region? had)) return had;
        Region region = new() { Kind = RegionKind.Storage, Symbol = symbol, Bytes = new byte[bytes], Maker = owner };
        _regions[symbol] = region;
        return region;
    }

    /// <summary>What a symbol's address is, as the unit `from` names it: its data, or a routine's address.</summary>
    public Val Address(string symbol, long offset, ObjectFile? from)
    {
        if (Data(symbol, from) is Region r) return new Ptr(r, offset);
        if (Find(symbol, from) is var (owner, sym, local) && sym.Section!.Kind == SectionKind.Code) return new Code(symbol, offset, local ? owner : null);
        // THE COLLECTOR'S FREE KNOB THE IMAGE DROPPED: a word the late passes
        // left nothing reading -- Gc.IgnoredFrees, once the free that read it
        // was put in its caller (List.Grow) -- read as the zero it starts as;
        // it only steers frees, which are nothing here. The run's IR is the
        // snapshot from before those passes. Marking and Cards, which say
        // what a store does, are not guessed.
        if (symbol == "s_Gc_IgnoredFrees")
        {
            if (!_regions.TryGetValue(symbol, out Region? knob))
                _regions[symbol] = knob = new() { Kind = RegionKind.Storage, Symbol = symbol, Bytes = new byte[8], Maker = "the collector", Dropped = true };
            return new Ptr(knob, offset);
        }
        // A CONTEXT TOKEN THE IMAGE DROPPED (Lowering.ContextToken, "tk_"): a
        // shared generic copy's static handed its instantiation's context --
        // Span<string>.Move, from Array.Copy -- whose call the late passes
        // made without it. Handed on, it is an address; read, it is the
        // image's, which MayRead refuses; kept, Commit refuses it.
        if (symbol.StartsWith("tk_", StringComparison.Ordinal))
        {
            if (!_regions.TryGetValue(symbol, out Region? token))
                _regions[symbol] = token = new() { Kind = RegionKind.Data, Symbol = symbol, Bytes = new byte[WordSize], Align = WordSize, Dropped = true };
            return new Ptr(token, offset);
        }
        throw new Refused("'" + symbol + "' is defined nowhere the link can see");
    }

    /// <summary>An object's bytes, zeroed, as an allocator hands them out.</summary>
    public Ptr Allocate(long bytes)
    {
        if (bytes < 0 || bytes > 16L << 20 || (_made += bytes) > MostMade) throw new Refused("an allocation of " + bytes + " bytes");
        Region region = new() { Kind = RegionKind.Heap, Bytes = new byte[(bytes + WordSize - 1) / WordSize * WordSize], Maker = Running };
        _heap.Add(region);
        return new Ptr(region, 0);
    }

    /// <summary>An object the link made itself (stage 1) for a type's static, as a region.</summary>
    public Region Adopt(byte[] bytes, string owner)
    {
        Region region = new() { Kind = RegionKind.Heap, Bytes = bytes, Maker = owner };
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
        MayRead(r);
        int at = (int)p.Offset;
        foreach ((int offset, Val v) in r.Refs)
        {
            if (offset + WordSize <= at || offset >= at + size) continue;
            if (offset != at || size != WordSize) throw new Refused("part of an address loaded from '" + r + "'");
            return Resolve(r, offset, v);
        }
        long bits = 0;
        for (int b = size - 1; b >= 0; b--) bits = (bits << 8) | r.Bytes[at + b];
        if (size < 8) bits = signed ? (bits << (64 - 8 * size)) >> (64 - 8 * size) : bits & ((1L << (8 * size)) - 1);
        return new Num(bits);
    }

    private Val Resolve(Region r, int offset, Val v)
    {
        if (v is not Unresolved u) return v;
        Val resolved = Address(u.Symbol, u.Addend, u.From);
        r.Refs[offset] = resolved;
        return resolved;
    }

    public void Store(Val address, int size, Val value)
    {
        if (address is not Ptr p) throw new Refused(address is Num { Bits: 0 } ? "a null dereference" : "a store to " + address);
        Region r = p.Region;
        if (r.ReadOnly) throw new Refused("a store into read-only '" + r + "'");
        if (p.Offset < 0 || p.Offset + size > r.Bytes.Length) throw new Refused("a store past '" + r + "'");
        MayWrite(r);
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
        // A UNIT'S OWN DATUM is laid down again by the link only under a
        // name every unit can reach, which its own is not.
        if (r.Local is not null && r.Kind == RegionKind.Data) throw new Refused("a store into '" + r + "', a unit's own");
    }

    /// <summary>Bytes copied, the addresses among them with them.</summary>
    public void Copy(Val to, Val from, long count)
    {
        if (count == 0) return;
        if (to is not Ptr d || from is not Ptr s) throw new Refused("a copy through " + to + " from " + from);
        if (count < 0 || s.Offset < 0 || d.Offset < 0 || s.Offset + count > s.Region.Bytes.Length || d.Offset + count > d.Region.Bytes.Length)
            throw new Refused("a copy past '" + d.Region + "' or '" + s.Region + "'");
        if (d.Region.ReadOnly) throw new Refused("a copy into read-only '" + d.Region + "'");
        MayRead(s.Region);
        MayWrite(d.Region);
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
        MayWrite(d.Region);
        Save(d.Region);
        int at = (int)d.Offset, n = (int)count;
        foreach (int offset in d.Region.Refs.Keys.Where(o => o + WordSize > at && o < at + n).ToList()) d.Region.Refs.Remove(offset);
        System.Array.Fill(d.Region.Bytes, value, at, n);
    }

    // THE COLLECTOR'S STATE, read by every reference store's barrier test and
    // by a free put in place in its caller (List.Grow's): which way the store
    // or the free goes, never what the program holds.
    private static readonly HashSet<string> Collector = new(StringComparer.Ordinal) { "s_Runtime_Marking", "s_Runtime_Cards", "s_Gc_IgnoredFrees" };

    /// <summary>
    /// WHAT A RUN MAY READ, as NativeAOT's preinitialisation may: the image's
    /// read-only data; what the running type holds; a type's state word (a
    /// touch's test, which Enter answers); a string; another type's readonly
    /// static once that type is made. A mutable static or object of another
    /// type's may be other at that type's first touch than it is here.
    /// </summary>
    private void MayRead(Region r)
    {
        if (r.Kind is RegionKind.Frame or RegionKind.Thread || r.ReadOnly) return;
        if (r.Maker is not null && r.Maker == Running) return;
        if (r.Symbol is string s && (_readyWords.Contains(s) || Collector.Contains(s))) return;
        if (r.Kind == RegionKind.Heap && IsString(r)) return;
        if (r.Kind is RegionKind.Data or RegionKind.Storage && r.Local is null && r.Symbol is string sym
            && _statics.TryGetValue(sym, out Preinit.StaticNote? st) && st.ReadOnly && Made(st.Owner)) return;
        throw new Refused((Running ?? "the run") + " reads '" + r + "', " + (r.Maker is null ? "the image's" : r.Maker + "'s"));
    }

    /// <summary>WHAT A RUN MAY WRITE: what the running type holds, its statics and the objects its run makes. Nothing of another type's.</summary>
    private void MayWrite(Region r)
    {
        if (r.Kind is RegionKind.Frame or RegionKind.Thread) return;
        if (Running is string me && r.Maker == me) return;
        throw new Refused((Running ?? "the run") + " writes '" + r + "', " + (r.Maker is null ? "the image's" : r.Maker + "'s"));
    }

    /// <summary>Whether an object is a string: its descriptor the strings' own.</summary>
    private bool IsString(Region r)
        => StringDescriptor is string d && r.Refs.TryGetValue(0, out Val? v)
           && (v is Ptr { Region.Symbol: string named } && named == d || v is Unresolved u && u.Symbol == d);

    // ---- laying it down ------------------------------------------------------------

    /// <summary>Every region a kept run wrote that a unit defined: the link defines it again, as it was left.</summary>
    public IEnumerable<Region> Overridden => _regions.Values.Where(r => r.Kind == RegionKind.Data && r.Written);

    public IEnumerable<Region> Storages => _regions.Values.Where(r => r.Kind == RegionKind.Storage);

    /// <summary>
    /// A word's address as the link writes it: a symbol and an addend, or the
    /// object it lays down. A unit's own symbol is named through an alias the
    /// unit is given, global, at the same place.
    /// </summary>
    public (string? Symbol, long Addend, Region? Object) Target(Val v)
    {
        switch (v)
        {
            case Ptr { Region.Kind: RegionKind.Heap } p: return (null, p.Offset, p.Region);
            case Ptr { Region: { Symbol: string s, Local: ObjectFile own } } p: return (Alias(own, s), p.Offset, null);
            case Ptr { Region.Symbol: string s } p: return (s, p.Offset, null);
            case Code { Local: ObjectFile own } c: return (Alias(own, c.Label), c.Offset, null);
            case Code c: return (c.Label, c.Offset, null);
            // A RELOCATION NEVER READ THROUGH is written as the unit wrote it,
            // by its name -- a unit's own through its alias -- and never read:
            // laid down at the link's end, the sections it would be read out of
            // are the link's by then (a library's frame pool rewritten), and a
            // symbol past its section's end stopped libcorsacrt's link.
            case Unresolved u:
            {
                var found = Find(u.Symbol, u.From);
                return found is { Local: true } own ? (Alias(own.Object, u.Symbol), u.Addend, null) : (u.Symbol, u.Addend, null);
            }
            default: throw new InvalidOperationException("preinit: an address the link cannot write: " + v);
        }
    }

    /// <summary>
    /// Every name what these regions hold reaches, through the objects they
    /// hold: the descriptors, literals and routines the link's data would
    /// name, without giving any unit's own symbol an alias.
    /// </summary>
    public HashSet<string> Names(IEnumerable<Region> roots)
    {
        HashSet<string> named = new(StringComparer.Ordinal);
        HashSet<Region> seen = new(ReferenceEqualityComparer.Instance);
        Stack<Region> walk = new();
        foreach (Region r in roots) if (seen.Add(r)) walk.Push(r);
        while (walk.TryPop(out Region? r))
            foreach (Val v in r.Refs.Values)
                switch (v)
                {
                    case Ptr { Region.Kind: RegionKind.Heap } p: if (seen.Add(p.Region)) walk.Push(p.Region); break;
                    case Ptr { Region.Symbol: string symbol }: named.Add(symbol); break;
                    case Code c: named.Add(c.Label); break;
                    case Unresolved u: named.Add(u.Symbol); break;
                }
        return named;
    }

    /// <summary>Whether a value can be written into the image: an address of a stack's or a thread's cannot.</summary>
    public static bool Kept(Val v) => v is not Ptr { Region.Kind: RegionKind.Frame or RegionKind.Thread };

    private readonly Dictionary<(ObjectFile, string), string> _aliases = new();
    private static int _aliasCount;

    /// <summary>A unit's own symbol's name for the link's data: an alias given it when the data is laid down (Preinit.Lay), never before.</summary>
    private string Alias(ObjectFile owner, string symbol)
    {
        if (_aliases.TryGetValue((owner, symbol), out string? had)) return had;
        string alias = "preinit$alias" + System.Threading.Interlocked.Increment(ref _aliasCount) + "$" + symbol;
        Aliases.Add((owner, symbol, alias));
        _aliases[(owner, symbol)] = alias;
        return alias;
    }
}
