#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// WHAT A FUNCTION IS MADE OF, AS IT WAS WHEN AN ANALYSIS OF IT WAS KEPT:
/// its parameters, its register count, its blocks in order with their landing
/// pad marks, and each block's instructions in order with what each writes,
/// reads and goes to. The proof --verify-analyses holds every analysis the
/// lifetime rules keep to (AnalysisCache): a function that still matches is
/// the function the analysis was made of -- an instruction is the same object
/// (its operation and callee cannot change), writing the same register,
/// reading the same operands (a register's operand is the one shared object,
/// RegOperand.Of; any other is the object it was), in the same place. A kept
/// answer whose function no longer matches is an in-place edit that did not
/// say so (Function.Edited), and the run stops there.
/// </summary>
internal sealed class FunctionShape
{
    private readonly Function _f;
    /// <summary>The function the shape was taken of.</summary>
    public Function For => _f;
    private readonly object?[] _parts;
    private readonly int _registers;

    private static readonly object Pad = new(), NotPad = new(), EndBlock = new(), EndInstr = new();

    private FunctionShape(Function f, object?[] parts)
    {
        _f = f;
        _parts = parts;
        _registers = f.RegCount;
    }

    public static FunctionShape Of(Function f)
    {
        // Written straight into an array of the exact size: no list grown,
        // no delegate a part.
        object?[] parts = new object?[Size(f)];
        int at = 0;
        foreach (VReg p in f.Params) parts[at++] = p;
        parts[at++] = EndBlock;
        foreach (Block b in f.Blocks)
        {
            parts[at++] = b;
            parts[at++] = b.IsLandingPad ? Pad : NotPad;
            foreach (Instr i in b.Instrs)
            {
                parts[at++] = i;
                parts[at++] = i.Dest;
                for (int n = 0; n < i.Operands.Count; n++) parts[at++] = i.Operands[n];
                foreach (Block t in i.Targets) parts[at++] = t;
                parts[at++] = i.Default;
                parts[at++] = EndInstr;
            }
            parts[at++] = EndBlock;
        }
        if (at != parts.Length) Array.Resize(ref parts, at);
        return new FunctionShape(f, parts);
    }

    /// <summary>Whether `f` is still the function this shape was taken of, part for part.</summary>
    public bool Matches(Function f)
    {
        if (!ReferenceEquals(f, _f) || f.RegCount != _registers) return false;
        int at = 0;
        object?[] parts = _parts;
        bool same = true;
        // The same walk Of took, stopped at the first difference.
        foreach (VReg p in f.Params) { if (!Next(p)) return false; }
        if (!Next(EndBlock)) return false;
        foreach (Block b in f.Blocks)
        {
            if (!Next(b) || !Next(b.IsLandingPad ? Pad : NotPad)) return false;
            foreach (Instr i in b.Instrs)
            {
                if (!Next(i) || !Next(i.Dest)) return false;
                for (int n = 0; n < i.Operands.Count; n++) if (!Next(i.Operands[n])) return false;
                foreach (Block t in i.Targets) if (!Next(t)) return false;
                if (!Next(i.Default) || !Next(EndInstr)) return false;
            }
            if (!Next(EndBlock)) return false;
        }
        return same && at == parts.Length;

        bool Next(object? part)
        {
            if (at >= parts.Length || !ReferenceEquals(parts[at], part)) return false;
            at++;
            return true;
        }
    }

    private static int Size(Function f)
    {
        int n = f.Params.Count + 1;
        foreach (Block b in f.Blocks)
        {
            n += 3;
            foreach (Instr i in b.Instrs) n += 4 + i.Operands.Count + i.Targets.Count;
        }
        return n;
    }
}

/// <summary>
/// THE LIFETIME RULES' ANALYSES OF THE FUNCTIONS THEY ARE ASKING ABOUT, kept
/// with each function's shape and handed back while the function still has
/// it (FunctionShape): a run asks the same functions for their register
/// writes, their definitions and their liveness again and again -- per
/// allocation judged, per phase, and of a callee in between -- and most of
/// the time nothing between two questions changed the function.
/// A FEW FUNCTIONS, MOST RECENT FIRST: the questions come a function at a
/// time, with its callees asked about in between, so one kept function was
/// mostly the wrong one.
/// KEYED BY WHAT THE FUNCTION SAYS OF ITS EDITS AND BY ITS COUNTS (KeyOf), at
/// the cost of a count of its instructions per question. A snapshot of its
/// whole shape, checked part for part, was tried first and cost as much to
/// take as the analyses it saved; it stays as the proof --verify-analyses
/// checks every kept answer against. Kept for the length of a run (Escape.Run), and only
/// while one runs. Every analysis kept here is read and never written by its
/// users, but for the landing pads' table a liveness works out when asked.
/// </summary>
internal static class AnalysisCache
{
    private const int KeptFunctions = 8;

    private sealed class Entry
    {
        public required Function F;
        public (int Edits, int Registers, int Blocks, int Instructions) Key;
        // Under --verify-analyses, the function's whole shape when the entry
        // was keyed, to prove the key right at every answer it gives.
        public FunctionShape? Proof;
        public RegisterWrites? Writes;
        public Liveness? Liveness;
        public Defs? DefsWithCfg, DefsWithoutCfg;
        // One flow graph for whatever of the above needs one, and the rest
        // kept by their askers' own kinds (Kept).
        public Cfg? Cfg;
        public readonly object?[] Other = new object?[OtherKinds];
    }

    /// <summary>The other kinds kept (Kept): the promotion order, the use index, the single definitions, the address scan (OwnedFieldEscape).</summary>
    public const int PromotionOrder = 0, UseIndex = 1, SingleDefs = 2, AddressScans = 3, OtherKinds = 4;

    private static readonly bool Verifying = Switches.VerifyAnalyses;

    [ThreadStatic] private static List<Entry>? _entries;

    // A FUNCTION THIS LARGE KEEPS ITS ENTRY until it changes, whatever is asked
    // of other functions between: one of 1321's, the dynamic dispatch of 372
    // thousand instructions, asked about in turn with its callers and
    // callees, was pushed out of the eight kept and its definitions,
    // liveness and instruction order made again each time it came back.
    private const int KeptWhole = 16384;
    [ThreadStatic] private static Dictionary<Function, Entry>? _large;

    // The blocks that read each register of a large function (BlockReaders),
    // kept while the lifetime passes run and brought up to date as it changes.
    [ThreadStatic] private static Dictionary<Function, BlockReaders>? _readers;

    public static void Open() { _entries = new(KeptFunctions); _large = new(ReferenceEqualityComparer.Instance); _readers = new(ReferenceEqualityComparer.Instance); }

    public static void Close() { _entries = null; _large = null; _readers = null; }

    /// <summary>
    /// The blocks reading each register of `f` as it stands now, for a
    /// function of BlockReaders.Large instructions or more while the cache is
    /// open; null for any other, which a question walks whole as it always did.
    /// </summary>
    public static BlockReaders? ReadersOf(Function f)
    {
        if (_readers is not { } all) return null;
        if (all.TryGetValue(f, out BlockReaders? known))
        {
            known.Refresh(Verifying);
            return known;
        }
        int instructions = 0;
        foreach (Block b in f.Blocks) instructions += b.Instrs.Count;
        if (instructions < BlockReaders.Large) return null;
        return all[f] = new BlockReaders(f);
    }

    /// <summary>
    /// What is kept of `f` in the state it is in now: its entry, moved to
    /// the front, emptied and given a new shape when `f` has changed since;
    /// a new entry, the oldest let go, when it was not kept. One shape for
    /// every kind of analysis, taken once a state.
    /// </summary>
    private static Entry Now(Function f, List<Entry> entries)
    {
        var key = KeyOf(f);
        if (key.Item4 >= KeptWhole && _large is { } large)
        {
            if (!large.TryGetValue(f, out Entry? e))
            {
                large[f] = e = new() { F = f, Key = key, Proof = Verifying ? FunctionShape.Of(f) : null };
                return e;
            }
            if (!e.Key.Equals(key))
            {
                e.Key = key;
                e.Writes = null; e.Liveness = null; e.DefsWithCfg = null; e.DefsWithoutCfg = null; e.Cfg = null;
                Array.Clear(e.Other);
                if (Verifying) e.Proof = FunctionShape.Of(f);
            }
            else if (Verifying && !e.Proof!.Matches(f))
                throw new InvalidOperationException("analysis cache: " + f.Name + " was edited in place without saying so (Function.Edited)");
            return e;
        }
        for (int k = 0; k < entries.Count; k++)
        {
            Entry e = entries[k];
            if (!ReferenceEquals(e.F, f)) continue;
            if (k != 0) { entries.RemoveAt(k); entries.Insert(0, e); }
            if (!e.Key.Equals(key))
            {
                e.Key = key;
                e.Writes = null; e.Liveness = null; e.DefsWithCfg = null; e.DefsWithoutCfg = null; e.Cfg = null;
                Array.Clear(e.Other);
                if (Verifying) e.Proof = FunctionShape.Of(f);
            }
            else if (Verifying && !e.Proof!.Matches(f))
                throw new InvalidOperationException("analysis cache: " + f.Name + " was edited in place without saying so (Function.Edited)");
            return e;
        }
        if (entries.Count == KeptFunctions) entries.RemoveAt(KeptFunctions - 1);
        Entry made = new() { F = f, Key = key, Proof = Verifying ? FunctionShape.Of(f) : null };
        entries.Insert(0, made);
        return made;
    }

    /// <summary>
    /// The function's edits (Function.Edited) and its counts: an insertion or
    /// a removal changes a count whether or not its author said so; an
    /// instruction replaced by another is said (Edited).
    /// </summary>
    internal static (int, int, int, int) KeyOf(Function f)
    {
        int instructions = 0;
        foreach (Block b in f.Blocks) instructions += b.Instrs.Count;
        return (f.Edits, f.RegCount, f.Blocks.Count, instructions);
    }

    public static RegisterWrites WritesOf(Function f)
    {
        if (_entries is not { } entries) return new RegisterWrites(f);
        Entry e = Now(f, entries);
        return e.Writes ??= new RegisterWrites(f);
    }

    public static Liveness LivenessOf(Function f)
    {
        if (_entries is not { } entries) return new Liveness(f);
        Entry e = Now(f, entries);
        if (e.Liveness is { } kept)
        {
            // As a fresh one has them: where its landing pads are entered is
            // worked out when first asked (Escape.PadLiveAt), from the function
            // as it is then, and the last user may have asked before edits
            // the next one makes before asking.
            kept.PadRegions = null;
            kept.PadFrom = null;
            return kept;
        }
        return e.Liveness = new Liveness(e.Cfg ??= new Cfg(f));
    }

    /// <summary>
    /// An analysis of a kind of the asker's own (`which`, one of the kinds
    /// above), made by `make` when none of the function in this state is
    /// kept. Never written by its users, as the rest.
    /// </summary>
    public static T Kept<T>(Function f, int which, Func<Function, T> make) where T : class
    {
        if (_entries is not { } entries) return make(f);
        Entry e = Now(f, entries);
        if (e.Other[which] is T kept) return kept;
        T made = make(f);
        e.Other[which] = made;
        return made;
    }

    /// <summary>The function's flow graph in this state, shared by the analyses kept that need one.</summary>
    public static Cfg CfgOf(Function f)
    {
        if (_entries is not { } entries) return new Cfg(f);
        Entry e = Now(f, entries);
        return e.Cfg ??= new Cfg(f);
    }

    public static Defs DefsOf(Function f, bool buildCfg = true)
    {
        if (_entries is not { } entries) return new Defs(f, buildCfg: buildCfg);
        Entry e = Now(f, entries);
        return buildCfg ? e.DefsWithCfg ??= new Defs(e.Cfg ??= new Cfg(f)) : e.DefsWithoutCfg ??= new Defs(f, buildCfg: false);
    }
}

/// <summary>
/// WHICH BLOCKS READ EACH REGISTER, AND WHICH WRITE IT, of one large function
/// -- and which blocks name a frame slot, and which read a register of no
/// number here -- by each block's place in the function's blocks. Made once,
/// and brought up to date when the function has changed (Refresh): every
/// block whose instructions are no longer the very ones it had is read
/// again, and what it reads now added. A register a block no longer reads may
/// stay listed for it: a list may name a block too many, never one too few,
/// and every question that asks it looks at the block's instructions itself.
/// The lifetime passes ask one question of a function for each of its
/// allocations, each owned record, each field read; a question of an object
/// bears only on the blocks that read what holds it, and the rest of a large
/// function -- 1321's dynamic dispatch, 372 thousand instructions -- was
/// walked for nothing, thousands of times.
/// </summary>
internal sealed class BlockReaders
{
    /// <summary>The fewest instructions a function has for its blocks to be indexed (AnalysisCache.ReadersOf).</summary>
    public const int Large = 4096;

    private readonly Function _f;
    private (int, int, int, int) _key;
    /// <summary>The function's blocks, in order, as they were when last brought up to date.</summary>
    public Block[] Blocks = Array.Empty<Block>();
    private List<Instr>[] _seen = Array.Empty<List<Instr>>();
    private List<int>?[] _reads = Array.Empty<List<int>?>(), _writes = Array.Empty<List<int>?>();
    /// <summary>The blocks that name a frame slot, and those that read a register of no number here.</summary>
    public readonly List<int> Slots = new(), Odd = new();
    private bool[] _slot = Array.Empty<bool>(), _odd = Array.Empty<bool>();

    public BlockReaders(Function f)
    {
        _f = f;
        Rebuild();
    }

    /// <summary>The blocks that read `r`, or null for none listed.</summary>
    public List<int>? Readers(VReg r) => (uint)r.Id < (uint)_reads.Length ? _reads[r.Id] : null;

    /// <summary>The blocks that write `r`, or null for none listed.</summary>
    public List<int>? Writers(VReg r) => (uint)r.Id < (uint)_writes.Length ? _writes[r.Id] : null;

    /// <summary>Up to date with the function: what changed read again (the whole function, if its blocks changed).</summary>
    public void Refresh(bool always = false)
    {
        var key = AnalysisCache.KeyOf(_f);
        if (!always && key.Equals(_key)) return;
        _key = key;
        List<Block> blocks = _f.Blocks;
        bool same = blocks.Count == Blocks.Length;
        for (int k = 0; same && k < Blocks.Length; k++) same = ReferenceEquals(blocks[k], Blocks[k]);
        if (!same)
        {
            Rebuild();
            return;
        }
        if (_f.RegCount > _reads.Length)
        {
            // Doubled: every change makes a few registers, and grown to fit
            // each time the two tables were copied whole at every change.
            int room = Math.Max(_f.RegCount, _reads.Length * 2);
            Array.Resize(ref _reads, room);
            Array.Resize(ref _writes, room);
        }
        for (int k = 0; k < Blocks.Length; k++)
        {
            List<Instr> now = Blocks[k].Instrs, was = _seen[k];
            bool unchanged = now.Count == was.Count;
            for (int j = 0; unchanged && j < was.Count; j++) unchanged = ReferenceEquals(now[j], was[j]);
            if (!unchanged) Scan(k);
        }
    }

    private void Rebuild()
    {
        _key = AnalysisCache.KeyOf(_f);
        Blocks = _f.Blocks.ToArray();
        _seen = new List<Instr>[Blocks.Length];
        _reads = new List<int>?[_f.RegCount];
        _writes = new List<int>?[_f.RegCount];
        Slots.Clear();
        Odd.Clear();
        _slot = new bool[Blocks.Length];
        _odd = new bool[Blocks.Length];
        for (int k = 0; k < Blocks.Length; k++) Scan(k);
    }

    private void Scan(int k)
    {
        List<Instr> instrs = Blocks[k].Instrs;
        // What the block is now, for the next look: its own list kept and
        // filled again, not an array a look.
        List<Instr> seen = _seen[k] ??= new List<Instr>(instrs.Count);
        seen.Clear();
        seen.AddRange(instrs);
        foreach (Instr i in instrs)
        {
            foreach (Operand o in i.Operands)
            {
                if (o is SlotOperand)
                {
                    if (!_slot[k]) { _slot[k] = true; Slots.Add(k); }
                }
                else if (o is RegOperand { Reg: var r })
                {
                    if ((uint)r.Id < (uint)_reads.Length) Note(ref _reads[r.Id], k);
                    else if (!_odd[k]) { _odd[k] = true; Odd.Add(k); }
                }
            }
            if (i.Dest is { } d && (uint)d.Id < (uint)_writes.Length) Note(ref _writes[d.Id], k);
        }
    }

    // A block listed for a register, once, the list in order: made in order
    // of the blocks, a block read again is found where it stands and put in
    // its place only when it is not there.
    private static void Note(ref List<int>? list, int k)
    {
        list ??= new List<int>(2);
        if (list.Count == 0 || list[^1] < k) { list.Add(k); return; }
        if (list[^1] == k) return;
        int at = list.BinarySearch(k);
        if (at < 0) list.Insert(~at, k);
    }

    /// <summary>Marks in `bits` every block listed for `r` in `lists`.</summary>
    public static void Mark(ulong[] bits, List<int>? blocks)
    {
        if (blocks is null) return;
        foreach (int k in blocks) bits[k >> 6] |= 1UL << k;
    }

    /// <summary>A set of blocks, one bit each.</summary>
    public ulong[] NewSet() => new ulong[(Blocks.Length + 63) / 64 + 1];

    /// <summary>The first block at or after `from` in `bits`, or -1.</summary>
    public int Next(ulong[] bits, int from)
    {
        if (from >= Blocks.Length) return -1;
        int w = from >> 6;
        ulong word = bits[w] & (~0UL << (from & 63));
        while (word == 0)
        {
            if (++w >= bits.Length) return -1;
            word = bits[w];
        }
        int at = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
        return at < Blocks.Length ? at : -1;
    }
}
