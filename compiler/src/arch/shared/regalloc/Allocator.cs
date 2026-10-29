#nullable enable
namespace Corsac.Lang.RegAlloc;

/// <summary>
/// Linear-scan register allocation, ONE FOR EVERY MACHINE: i386 and
/// x86-64 alike lower their functions into the allocator's form (AFunction,
/// ITarget) -- the registers each instruction names and what it does with
/// them, which operands may be memory or a constant -- and the target's
/// register file says which registers each class has and in what order they
/// are tried (RegisterFile). Nothing here knows an instruction set.
///
/// Liveness comes from a dataflow pass over the machine CFG, because the IR
/// is not SSA and a virtual register may be defined in several places. Each
/// virtual register gets one interval, the exact ranges of positions it is
/// live at, holes and all: another interval may share its register in the
/// holes. The physical registers a selector named (a divide's, a shift
/// count's, the caller-saved ones at every call) are not intervals but
/// fixed busy positions that no interval may overlap.
///
/// Positions: instruction i owns 4i .. 4i+3. A reload lands at 4i, reads
/// happen at 4i+1, writes at 4i+2, a spill store at 4i+3. An interval that
/// ends with a read at instruction i and one that starts with a write there
/// can share a register, as every instruction the selector emits reads its
/// operands before it writes its result.
///
/// Spilling: when no register is free the interval with the furthest next
/// use gives way -- which may be the one being allocated. A spilled
/// register lives in a frame slot: every write stores to it, every read
/// reloads from it, and each such read or write is a short interval of its
/// own covering just that instruction, which the scan allocates like any
/// other. Where the instruction accepts a memory operand the slot is used
/// in place and no register is needed at all. Three things keep that from
/// costing a load per read: reads before the spill point keep the register
/// when nothing after it flows back to them; an interval about to be
/// spilled keeps a register up to its first conflict in its first block;
/// and a reload's register carries the value on to the following reads in
/// the same straight run of blocks while it stays free.
/// </summary>
public sealed class Allocator
{
    /// <summary>The registers an interval's value may have, in the order they are tried: its class's (RegClass.Preference).</summary>
    private int[] PreferenceFor(int vreg) => _rf.Classes[_t.ClassOf(vreg)].Preference;

    private sealed class Interval
    {
        public int VReg;
        public int Start;
        public int End;
        public bool Short;
        public int Instr;
        public int Reg = -1;
        /// <summary>A short interval queued but absorbed into an earlier one's reach.</summary>
        public bool Cancelled;
        public Role Role;
        /// <summary>
        /// For a short interval carried on past its own instruction: every
        /// instruction it serves, its own first, with what each does to it.
        /// </summary>
        public List<(int Instr, Role Role)>? Served;
        /// <summary>
        /// Where a whole register's value is actually live, sorted and
        /// disjoint, within Start..End; null for a short interval, which is
        /// live over all of Start..End.
        /// </summary>
        public List<(int S, int E)>? Ranges;
    }

    private readonly record struct Occurrence(int Instr, int Operand, Role Role, bool InMem);

    private readonly AFunction _f;
    private readonly ITarget _t;
    private readonly RegisterFile _rf;
    /// <summary>The first virtual register's number, and how many physical ones there are.</summary>
    private readonly int _fv, _physCount;
    private readonly List<AInstr> _lin = new();
    private readonly List<ABlock> _blockOf = new();
    private readonly int _n;
    /// <summary>
    /// Every register's occurrences, in instruction order, laid end to end
    /// by register: register v's are _occAll[_occFirst[v] .. _occFirst[v + 1]).
    /// ONE ARRAY, NOT A LIST PER REGISTER: a function names hundreds of
    /// virtual registers, and a list each was hundreds of objects made and
    /// dropped for every function compiled.
    /// </summary>
    private Occurrence[] _occAll = Array.Empty<Occurrence>();
    private readonly int[] _occFirst;
    private readonly int[] _start;
    private readonly int[] _end;
    /// <summary>The positions each virtual register is live at, as ranges.</summary>
    private readonly List<(int S, int E)>?[] _ranges;
    private readonly int[] _runS, _runE;
    /// <summary>
    /// Each physical register's fixed busy positions: a prefix-summed table
    /// four words an instruction, made only for a register the function fixes
    /// somewhere (null: never busy) -- or, in a function too large for
    /// tables (BusyTables), a sorted list of the positions, searched by halves.
    /// Tables for every register of x86-64's thirty-two were three quarters
    /// of a gigabyte for a 1.4-million-instruction Main.
    /// </summary>
    private readonly int[]?[] _busy;
    private readonly List<int>?[]? _busyList;
    /// <summary>The positions past which a function's busy registers are kept as lists, not tables.</summary>
    private const int BusyTables = 1 << 22;
    // How much of each busy table this function uses: the tables are a
    // thread's, kept from one function to the next (SpareBusy).
    private readonly int _busyLength;

    // THE BUSY TABLES KEPT, A THREAD'S: eight of four words an instruction,
    // made for every function, were the largest thing the allocator left to
    // the collector. Grown as a function needs, cleared as far as it uses
    // them; one past KeptBusy is made for that function alone, so that one
    // huge function does not hold its tables for the rest of the run.
    [ThreadStatic] private static int[]?[]? _spareBusy;
    private const int KeptBusy = 1 << 16;
    private readonly int[] _assigned;
    private readonly int[] _spilledFrom;
    private readonly int[] _slot;
    private readonly Dictionary<(int VReg, int Instr), int> _shortReg = new();
    private readonly HashSet<(int Instr, int Operand)> _folded = new();
    /// <summary>Registers whose one definition is a constant: spilled, they are re-made rather than stored.</summary>
    private readonly object?[] _remat;
    /// <summary>Values whose one definition loads them from a home (AInstr.HomeSlot): the home, else 0.</summary>
    private int[] _home = Array.Empty<int>();
    /// <summary>Spill slots whose owners' intervals have ended, with the position each is free from.</summary>
    private readonly List<(int Slot, int FreeFrom)> _freeSlots = new();
    /// <summary>Virtual registers live across each call, by instruction index: the raw material of a stack map.</summary>
    private readonly Dictionary<int, List<int>> _liveAtCall = new();
    private readonly PriorityQueue<Interval, (int, int)> _unhandled = new();
    private readonly List<Interval> _active = new();
    /// <summary>Short intervals queued and not yet reached by the scan.</summary>
    private readonly Dictionary<(int VReg, int Instr), Interval> _pending = new();
    /// <summary>Reads of a spilled register served by a register that still holds it: no reload.</summary>
    private readonly HashSet<(int VReg, int Instr)> _noReload = new();
    /// <summary>Calls before each instruction index: a prefix count.</summary>
    private int[] _callsBefore = Array.Empty<int>();
    /// <summary>
    /// Spilled registers whose reads before the spill point still find the
    /// value in the register they had: nothing from the spill point on can
    /// flow back to them.
    /// </summary>
    private readonly HashSet<int> _keepBefore = new();
    private int[] _blockFirst = Array.Empty<int>();
    // Every block's successors, a run a block (ComputeLiveness).
    private int[] _succStart = Array.Empty<int>();
    private int[] _succAll = Array.Empty<int>();
    private int[] _predStart = Array.Empty<int>();
    private int[] _predAll = Array.Empty<int>();
    private int _blockCount;
    /// <summary>
    /// The first block of the straight run each block belongs to: a block
    /// entered only from the one laid out before it continues that one's run.
    /// </summary>
    private int[] _runHead = Array.Empty<int>();
    private int[] _blockOfInstr = Array.Empty<int>();
    private BitRows _liveIn = new();
    private int _seq;
    /// <summary>The values split at their loops (LoopSplit), or null: their pieces share a slot.</summary>
    private LoopSplit? _split;
    /// <summary>Each split value's shared slot, by its original (LoopSplit.Root).</summary>
    private readonly Dictionary<int, int> _rootSlot = new();

    private Allocator(AFunction m)
    {
        _f = m;
        _t = m.Target;
        _rf = m.Registers;
        _fv = _rf.FirstVirtual;
        _physCount = _rf.PhysCount;
        _busy = new int[]?[_physCount];
        foreach (ABlock b in m.Blocks)
        {
            foreach (AInstr i in b.Instrs)
            {
                _lin.Add(i);
                _blockOf.Add(b);
            }
        }
        _n = m.NextVReg;
        // THE TABLES BY VIRTUAL REGISTER kept a thread, as the busy tables
        // are: ten arrays the function's size were made and dropped for every
        // function the allocator saw. Read only up to _n.
        _occFirst = Table(ref _keptOccFirst, _n + 1, clear: true);
        _start = Table(ref _keptStart, _n, clear: false);
        _end = Table(ref _keptEnd, _n, clear: false);
        _assigned = Table(ref _keptAssigned, _n, clear: false);
        _spilledFrom = Table(ref _keptSpilledFrom, _n, clear: false);
        _slot = Table(ref _keptSlot, _n, clear: true);
        _remat = Table(ref _keptRemat, _n, clear: true);
        _ranges = Table(ref _keptRanges, _n, clear: true);
        _runS = Table(ref _keptRunS, _n, clear: false);
        _runE = Table(ref _keptRunE, _n, clear: true);
        Array.Fill(_runS, -1, 0, _n);
        // A register's ranges made as it is first marked: most numbers are
        // halves never used, values selection dropped, or the machine's own.
        for (int v = 0; v < _n; v++)
        {
            _start[v] = int.MaxValue;
            _end[v] = -1;
            _assigned[v] = -1;
            _spilledFrom[v] = int.MaxValue;
        }
        _busyLength = _lin.Count * 4 + 1;
        if (_busyLength > BusyTables) _busyList = new List<int>?[_physCount];
    }

    /// <summary>A register's busy table, made the first time the function fixes it: the thread's kept one where it is long enough.</summary>
    private int[] BusyTable(int reg)
    {
        int[]?[] spare = _spareBusy is { } kept0 && kept0.Length >= _physCount ? kept0 : _spareBusy = new int[]?[_physCount];
        int[]? kept = spare[reg];
        if (kept is not null && kept.Length >= _busyLength)
        {
            Array.Clear(kept, 0, _busyLength);
            return kept;
        }
        int[] made = new int[_busyLength];
        if (_busyLength <= KeptBusy) spare[reg] = made;
        return made;
    }

    /// <summary>Allocates the function's registers, and has its target rebuild it from what was decided.</summary>
    public static void Run(AFunction m)
    {
        CopyFold.Run(m);
        LoopSplit? split = LoopSplit.Run(m);
        Allocator a = new(m) { _split = split };
        a.CollectOccurrences();
        a.ComputeLiveness();
        a.Scan();
        a.HeldBeforeSpill();
        a.Rewrite();
        // What the kept tables point at goes with the function.
        Array.Clear(a._remat, 0, a._n);
        Array.Clear(a._ranges, 0, a._n);
        _keptIndex?.Clear();
    }

    [ThreadStatic] private static int[]? _keptOccFirst, _keptStart, _keptEnd, _keptAssigned, _keptSpilledFrom, _keptSlot, _keptRunS, _keptRunE;
    [ThreadStatic] private static object?[]? _keptRemat;
    [ThreadStatic] private static int[]? _keptFill, _keptFirst, _keptCount, _keptSuccStart, _keptSucc, _keptPreds, _keptOnlyPred, _keptRunHead, _keptBlockOfInstr, _keptCallsBefore;
    [ThreadStatic] private static int[]? _keptPredStart, _keptPredAll, _keptPredFill, _keptWork, _keptLastDef, _keptGlobalOf;
    [ThreadStatic] private static List<int>? _keptGlobals;
    private int[] _globalOf = Array.Empty<int>();
    private List<int> _globals = new();
    [ThreadStatic] private static bool[]? _keptOwed;
    [ThreadStatic] private static Occurrence[]? _keptOccAll;
    [ThreadStatic] private static Dictionary<ABlock, int>? _keptIndex;
    [ThreadStatic] private static List<ABlock>? _keptSuccessors;
    [ThreadStatic] private static List<(int, int)>?[]? _keptRanges;
    private const int KeptRegs = 1 << 18;

    // At least `length` of a kept table, cleared that far when asked; one
    // longer than KeptRegs is the function's alone.
    private static T[] Table<T>(ref T[]? kept, int length, bool clear, bool grow = false, int used = 0)
    {
        if (kept is not null && kept.Length >= length)
        {
            if (clear) Array.Clear(kept, 0, length);
            return kept;
        }
        // GROWN where a list is filled into it (grow): doubled, what was
        // written so far kept, and kept whatever its size, as it is the
        // only home of what it holds until the function is done.
        T[] made = new T[grow ? Math.Max(length, Math.Max(16, (kept?.Length ?? 0) * 2)) : length];
        if (grow && kept is not null && used > 0) Array.Copy(kept, made, used);
        if (grow || length <= KeptRegs) kept = made;
        return made;
    }

    // ---- occurrences ----------------------------------------------------------

    private bool Tracked(int r) => r >= _fv || _rf.Tracked(r);

    private void CollectOccurrences()
    {
        // Counted first, then placed: each register's run starts where the
        // counts before it end, and is filled in instruction order.
        for (int i = 0; i < _lin.Count; i++)
        {
            foreach ((int r, _, _, _) in _lin[i].Regs)
            {
                if (Tracked(r))
                {
                    _occFirst[r + 1]++;
                }
            }
        }
        for (int v = 0; v < _n; v++)
        {
            _occFirst[v + 1] += _occFirst[v];
        }
        _occAll = Table(ref _keptOccAll, _occFirst[_n], clear: false);
        int[] fill = Table(ref _keptFill, _n, clear: false);
        Array.Copy(_occFirst, fill, _n);
        for (int i = 0; i < _lin.Count; i++)
        {
            foreach ((int r, Role role, int k, bool inMem) in _lin[i].Regs)
            {
                if (Tracked(r))
                {
                    _occAll[fill[r]++] = new Occurrence(i, k, role, inMem);
                }
            }
        }

        // A register defined exactly once, by a constant, need never live in
        // a spill slot: wherever it is wanted the constant can be written
        // again, and often folded into the instruction that wanted it.
        for (int v = _fv; v < _n; v++)
        {
            Occurrence? def = null;
            int defs = 0;
            for (int occ = _occFirst[v], occEnd = _occFirst[v + 1]; occ < occEnd; occ++)
            {
                Occurrence o = _occAll[occ];
                if ((o.Role & Role.Def) != 0)
                {
                    defs++;
                    def = o;
                }
            }
            if (defs == 1 && def is { Role: Role.Def, InMem: false, Operand: 0 } d
                && _lin[d.Instr].Constant is { } imm)
            {
                _remat[v] = imm;
            }
            else if (defs == 1 && def is { Role: Role.Def, InMem: false, Operand: 0 } h && _lin[h.Instr].HomeSlot is { } home)
            {
                if (_home.Length < _n) _home = new int[_n];
                _home[v] = home;
            }
        }
    }

    // ---- liveness ---------------------------------------------------------------

    private void Mark(int reg, int pos)
    {
        if (reg < _fv)
        {
            if (_busyList is not null) (_busyList[reg] ??= new()).Add(pos);
            else (_busy[reg] ??= BusyTable(reg))[pos] = 1;
        }
        else
        {
            _start[reg] = Math.Min(_start[reg], pos);
            _end[reg] = Math.Max(_end[reg], pos);
            // The walk is backward within a block, marking an instruction's
            // late pair before its early one, so a run grows downward. THE
            // RUN BEING GROWN IN TWO WORDS (_runS, _runE), a list made only
            // when a second, apart from it, begins: most registers live in
            // one run, and a list each was the allocator's most numerous
            // leaving to the collector.
            int runS = _runS[reg];
            if (runS >= 0 && pos >= runS - 2 && pos <= _runE[reg] + 1)
            {
                _runS[reg] = Math.Min(runS, pos);
                _runE[reg] = Math.Max(_runE[reg], pos);
            }
            else
            {
                if (runS >= 0) (_ranges[reg] ??= new List<(int S, int E)>()).Add((runS, _runE[reg]));
                _runS[reg] = pos;
                _runE[reg] = pos;
            }
        }
    }

    private void ComputeLiveness()
    {
        int nb = _f.Blocks.Count;
        // THE BLOCKS' TABLES KEPT A THREAD as the registers' are (Table): an
        // index, a run a block and every successor in one array, read only up
        // to this function's counts.
        Dictionary<ABlock, int> index = _keptIndex ??= new(ReferenceEqualityComparer.Instance);
        index.Clear();
        int[] first = Table(ref _keptFirst, nb, clear: false);
        int[] count = Table(ref _keptCount, nb, clear: false);
        for (int b = 0, at = 0; b < nb; b++)
        {
            index[_f.Blocks[b]] = b;
            first[b] = at;
            count[b] = _f.Blocks[b].Instrs.Count;
            at += count[b];
        }

        // ONLY A REGISTER READ IN SOME BLOCK BEFORE THAT BLOCK WRITES IT can
        // be live into a block, or out of one: one every block writes before
        // it reads is in no block's use set, so in no live-out set either, at
        // any step of the solve. The blocks' sets are kept for those registers
        // alone, numbered apart (_globalOf, _globals): 1321's Main has 34,761
        // registers and 14 thousand blocks, and three tables a block wide for
        // every register were 180 MB, solved over in full.
        int[] lastDef = Table(ref _keptLastDef, _n, clear: false);
        Array.Fill(lastDef, -1, 0, _n);
        _globalOf = Table(ref _keptGlobalOf, _n, clear: false);
        Array.Fill(_globalOf, -1, 0, _n);
        List<int> globals = _globals = _keptGlobals ??= new();
        globals.Clear();
        for (int b = 0; b < nb; b++)
        {
            for (int i = first[b]; i < first[b] + count[b]; i++)
            {
                foreach ((int r, Role role, _, _) in _lin[i].Regs)
                    if (Tracked(r) && (role & Role.Use) != 0 && lastDef[r] != b && _globalOf[r] < 0)
                    {
                        _globalOf[r] = globals.Count;
                        globals.Add(r);
                    }
                foreach ((int r, Role role, _, _) in _lin[i].Regs)
                    if (Tracked(r) && (role & Role.Def) != 0) lastDef[r] = b;
            }
        }
        int g = globals.Count;

        // A BLOCK'S SETS AS ROWS of one table each, kept a thread (BitRows):
        // three sets and their arrays a block were the allocator's largest
        // leaving to the collector after its intervals.
        BitRows use = BitRows.Kept(0, nb, g), def = BitRows.Kept(1, nb, g), live = BitRows.Kept(2, nb, g);
        for (int b = 0; b < nb; b++)
        {
            for (int i = first[b]; i < first[b] + count[b]; i++)
            {
                foreach ((int r, Role role, _, _) in _lin[i].Regs)
                {
                    if (!Tracked(r) || _globalOf[r] is not (>= 0 and var at))
                    {
                        continue;
                    }
                    if ((role & Role.Use) != 0 && !def.Get(b, at))
                    {
                        use.Set(b, at);
                    }
                }
                foreach ((int r, Role role, _, _) in _lin[i].Regs)
                {
                    if (Tracked(r) && (role & Role.Def) != 0 && _globalOf[r] is >= 0 and var at)
                    {
                        def.Set(b, at);
                    }
                }
            }
        }

        // Every block's successors in one array, a run a block from succStart.
        int[] succStart = Table(ref _keptSuccStart, nb + 1, clear: false);
        List<ABlock> successors = _keptSuccessors ??= new();
        int edges = 0;
        for (int b = 0; b < nb; b++)
        {
            succStart[b] = edges;
            _f.SuccessorsInto(b, successors);
            int[] all = Table(ref _keptSucc, edges + successors.Count, clear: false, grow: true, used: edges);
            foreach (ABlock s in successors) all[edges++] = index[s];
        }
        succStart[nb] = edges;
        successors.Clear();
        int[] succ = _keptSucc ?? Array.Empty<int>();
        _succStart = succStart;
        _succAll = succ;
        _blockCount = nb;

        _blockFirst = first;
        int[] preds = Table(ref _keptPreds, nb, clear: true);
        int[] onlyPred = Table(ref _keptOnlyPred, nb, clear: false);
        for (int b = 0; b < nb; b++)
        {
            for (int e = succStart[b]; e < succStart[b + 1]; e++)
            {
                int s = succ[e];
                preds[s]++;
                onlyPred[s] = b;
            }
        }
        _runHead = Table(ref _keptRunHead, nb, clear: false);
        for (int b = 0; b < nb; b++)
        {
            _runHead[b] = b > 0 && preds[b] == 1 && onlyPred[b] == b - 1 && !_f.Blocks[b].LandingPad
                ? _runHead[b - 1] : b;
        }
        _blockOfInstr = Table(ref _keptBlockOfInstr, _lin.Count, clear: false);
        for (int b = 0; b < nb; b++)
        {
            for (int i = first[b]; i < first[b] + count[b]; i++)
            {
                _blockOfInstr[i] = b;
            }
        }

        // live-out(b) = union over successors s of use(s) | (live-out(s) & ~def(s));
        // to the least fixed point, by a worklist: a block is worked out again
        // only when a successor's live-out has changed, and then its
        // predecessors are owed another look. Swept backwards over the layout
        // until nothing changed instead, a chain of blocks each jumping to one
        // laid out before it moved what is live one block a sweep -- 1321's
        // Main, 14 thousand blocks, took 1077 sweeps of every block, the
        // largest part of its compile. Any order reaches the same fixed point.
        BitSet tmp = new(g);
        BitSet inS = new(g);
        int[] predStart = Table(ref _keptPredStart, nb + 1, clear: true);
        for (int e = 0; e < edges; e++) predStart[succ[e] + 1]++;
        for (int b = 0; b < nb; b++) predStart[b + 1] += predStart[b];
        int[] predAll = Table(ref _keptPredAll, Math.Max(1, edges), clear: false, grow: true);
        int[] predFill = Table(ref _keptPredFill, nb, clear: false);
        Array.Copy(predStart, predFill, nb);
        for (int b = 0; b < nb; b++)
            for (int e = succStart[b]; e < succStart[b + 1]; e++) predAll[predFill[succ[e]]++] = b;
        _predStart = predStart;
        _predAll = predAll;
        // Every block owed a first look, the last laid out first, as the sweep began.
        int[] work = Table(ref _keptWork, nb, clear: false);
        bool[] owed = _keptOwed is { } keptOwed && keptOwed.Length >= nb ? keptOwed : _keptOwed = new bool[Math.Max(nb, 16)];
        int top = 0;
        for (int b = 0; b < nb; b++) { work[top++] = b; owed[b] = true; }
        while (top > 0)
        {
            int b = work[--top];
            owed[b] = false;
            tmp.Clear();
            for (int e = succStart[b]; e < succStart[b + 1]; e++)
            {
                int s = succ[e];
                inS.CopyFrom(live, s);
                inS.AndNot(def, s);
                inS.Or(use, s);
                tmp.Or(inS);
            }
            if (!live.RowEquals(b, tmp))
            {
                live.CopyRow(b, tmp);
                for (int e = predStart[b]; e < predStart[b + 1]; e++)
                {
                    int p = predAll[e];
                    if (!owed[p]) { owed[p] = true; work[top++] = p; }
                }
            }
        }

        // live-in(b) = use(b) | (live-out(b) & ~def(b)), made in use(b)'s own
        // bits: nothing reads use(b) again, so it needs no set of its own.
        _liveIn = use;
        for (int b = 0; b < nb; b++)
        {
            tmp.CopyFrom(live, b);
            tmp.AndNot(def, b);
            use.OrRow(b, tmp);
        }

        // Backward walk of each block, marking every position each register
        // is live at. Virtual registers take the hull; physical ones the
        // exact positions, since a fixed register is free between its uses.
        BitSet cur = new(_n);
        for (int b = 0; b < nb; b++)
        {
            // The block's live-out, each register by its own number again.
            cur.Clear();
            for (int w = 0, at = b * live.Words; w < live.Words; w++)
                for (ulong word = live.Bits[at + w]; word != 0; word &= word - 1)
                    cur.Set(globals[(w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word)]);
            for (int i = first[b] + count[b] - 1; i >= first[b]; i--)
            {
                int p = i * 4;
                if (_lin[i].IsCall)
                {
                    // live-out of the call is live ACROSS it: what the
                    // collector must find and, when it moves an object,
                    // must write back.
                    List<int> members = new();
                    foreach (int r in cur.Members()) members.Add(r);
                    _liveAtCall[i] = members;
                }
                foreach (int r in cur.Members())
                {
                    Mark(r, p + 2);
                    Mark(r, p + 3);
                }
                foreach ((int r, Role role, _, _) in _lin[i].Regs)
                {
                    if (Tracked(r) && (role & Role.Def) != 0)
                    {
                        Mark(r, p + 2);
                        Mark(r, p + 3);
                        cur.Clear(r);
                    }
                }
                foreach ((int r, Role role, _, _) in _lin[i].Regs)
                {
                    if (Tracked(r) && (role & Role.Use) != 0)
                    {
                        cur.Set(r);
                    }
                }
                foreach (int r in cur.Members())
                {
                    Mark(r, p);
                    Mark(r, p + 1);
                }
            }
        }

        for (int v = _fv; v < _n; v++)
        {
            List<(int S, int E)>? list = _ranges[v];
            // One run and no list: its span, which a null Ranges means.
            if (list is null)
            {
                continue;
            }
            list.Add((_runS[v], _runE[v]));
            list.Sort();
            // Merged in place: the merged run is never longer than what has
            // been read, so writing at `w` overwrites only ranges already read.
            int w = 0;
            for (int k = 1; k < list.Count; k++)
            {
                if (list[k].S <= list[w].E + 1)
                {
                    list[w] = (list[w].S, Math.Max(list[w].E, list[k].E));
                }
                else
                {
                    list[++w] = list[k];
                }
            }
            list.RemoveRange(w + 1, list.Count - w - 1);
        }

        // Prefix sums so "is r busy anywhere in [a, b]" is a subtraction;
        // a list sorted and each position once, for a search by halves.
        for (int r = 0; r < _physCount; r++)
        {
            if (_busyList?[r] is { } list)
            {
                list.Sort();
                int w = 0;
                for (int k = 0; k < list.Count; k++) if (w == 0 || list[k] != list[w - 1]) list[w++] = list[k];
                list.RemoveRange(w, list.Count - w);
            }
            if (_busy[r] is not { } arr) continue;
            for (int p = 1; p < _busyLength; p++)
            {
                arr[p] += arr[p - 1];
            }
        }
    }

    private bool Busy(int reg, int start, int end)
    {
        if (_busyList is not null)
        {
            if (_busyList[reg] is not { } list) return false;
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid] < start) lo = mid + 1; else hi = mid;
            }
            return lo < list.Count && list[lo] <= end;
        }
        if (_busy[reg] is not { } arr) return false;
        int before = start == 0 ? 0 : arr[start - 1];
        return arr[Math.Min(end, _busyLength - 1)] - before > 0;
    }

    // ---- the scan ----------------------------------------------------------------

    private void Enqueue(Interval iv) => _unhandled.Enqueue(iv, (iv.Start, _seq++));

    private void Scan()
    {
        FindLoops();
        if (Tracing)
            for (int i = 0; i < _lin.Count; i++)
                Console.Error.WriteLine($"regalloc {_f.Name} {i}: b{_blockOfInstr[i]} loop {_loopOf[_blockOfInstr[i]]}  {_t.Text(_lin[i])}");
        _callsBefore = Table(ref _keptCallsBefore, _lin.Count + 1, clear: false);
        _callsBefore[0] = 0;
        for (int i = 0; i < _lin.Count; i++)
        {
            _callsBefore[i + 1] = _callsBefore[i] + (_lin[i].IsCall ? 1 : 0);
        }
        for (int v = _fv; v < _n; v++)
        {
            if (_end[v] >= 0)
            {
                Enqueue(new Interval { VReg = v, Start = _start[v], End = _end[v], Ranges = _ranges[v] });
            }
        }

        while (_unhandled.TryDequeue(out Interval? cur, out _))
        {
            if (cur.Cancelled)
            {
                continue;
            }
            if (cur.Short)
            {
                _pending.Remove((cur.VReg, cur.Instr));
            }
            // By hand, not RemoveAll: a predicate capturing `cur` was a
            // closure and a delegate for every interval the scan placed.
            int kept = 0;
            for (int k = 0; k < _active.Count; k++)
            {
                if (_active[k].End >= cur.Start) _active[kept++] = _active[k];
            }
            _active.RemoveRange(kept, _active.Count - kept);

            int reg = FreeRegister(cur);
            if (reg < 0)
            {
                reg = MakeRoom(cur);
            }
            if (reg < 0)
            {
                continue;
            }
            cur.Reg = reg;
            _active.Add(cur);
            if (cur.Short)
            {
                _shortReg[(cur.VReg, cur.Instr)] = reg;
                Reach(cur);
            }
            else
            {
                _assigned[cur.VReg] = reg;
            }
        }
    }

    /// <summary>Whether a physical register may hold a value at all: one the allocator tracks, of the value's class, byte-capable where the value must be.</summary>
    private bool Usable(int reg, int vreg)
    {
        if (!_rf.Tracked(reg)) return false;
        int c = _t.ClassOf(vreg);
        if (_t.ClassOf(reg) != c) return false;
        return !_t.ByteOnly(vreg) || _rf.Classes[c].ByteCapable is not { } bytes || Array.IndexOf(bytes, reg) >= 0;
    }

    private bool Allocatable(int reg, Interval iv)
    {
        if (!Usable(reg, iv.VReg))
        {
            return false;
        }
        if (iv.Ranges is null)
        {
            return !Busy(reg, iv.Start, iv.End);
        }
        foreach ((int s, int e) in iv.Ranges)
        {
            if (Busy(reg, s, e))
            {
                return false;
            }
        }
        return true;
    }

    // An interval's ranges, one -- its whole span -- where it keeps no list.
    private static int RangeCount(Interval iv) => iv.Ranges?.Count ?? 1;
    private static (int S, int E) RangeAt(Interval iv, int k) => iv.Ranges is { } ranges ? ranges[k] : (iv.Start, iv.End);

    /// <summary>Whether two intervals are live at a common position: a hole in one may hold the other.</summary>
    private static bool Overlaps(Interval a, Interval b)
    {
        if (a.End < b.Start || b.End < a.Start)
        {
            return false;
        }
        if (a.Ranges is null)
        {
            return b.Ranges is null || Touches(b.Ranges, a.Start, a.End);
        }
        if (b.Ranges is null)
        {
            return Touches(a.Ranges, b.Start, b.End);
        }
        List<(int S, int E)> ra = a.Ranges;
        List<(int S, int E)> rb = b.Ranges;
        int x = 0, y = 0;
        while (x < ra.Count && y < rb.Count)
        {
            if (ra[x].E < rb[y].S)
            {
                x++;
            }
            else if (rb[y].E < ra[x].S)
            {
                y++;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether another placed interval in cur's register is live anywhere in
    /// cur.Start..end, cur's reach were it carried to `end`.
    /// </summary>
    private bool SpanTaken(Interval cur, int end)
    {
        foreach (Interval a in _active)
        {
            if (a == cur || a.Reg != cur.Reg || a.End < cur.Start || end < a.Start) continue;
            if (a.Ranges is null || Touches(a.Ranges, cur.Start, end)) return true;
        }
        return false;
    }

    /// <summary>Whether sorted ranges meet the positions start..end.</summary>
    private static bool Touches(List<(int S, int E)> ranges, int start, int end)
    {
        int lo = 0, hi = ranges.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ranges[mid].E < start) lo = mid + 1; else hi = mid;
        }
        return lo < ranges.Count && ranges[lo].S <= end;
    }

    /// <summary>Whether a register is taken by any placed interval live where this one is.</summary>
    private bool Taken(int reg, Interval iv) => Taken(reg, iv, iv);

    /// <summary>Whether a placed interval other than `except` holds the register where `iv` is live.</summary>
    private bool Taken(int reg, Interval iv, Interval except)
    {
        foreach (Interval a in _active)
        {
            if (a != except && a.Reg == reg && Overlaps(a, iv)) return true;
        }
        return false;
    }

    private int FreeRegister(Interval iv)
    {
        int hint = Hint(iv);
        if (hint >= 0 && Allocatable(hint, iv) && !Taken(hint, iv))
        {
            return hint;
        }
        foreach (int r in PreferenceFor(iv.VReg))
        {
            if (Allocatable(r, iv) && !Taken(r, iv))
            {
                return r;
            }
        }
        return -1;
    }

    /// <summary>
    /// A register this interval would like: whatever the other side of a
    /// move involving it already has, so the move can disappear.
    /// </summary>
    private int Hint(Interval iv)
    {
        for (int occ = _occFirst[iv.VReg], occEnd = _occFirst[iv.VReg + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            if (iv.Short && o.Instr != iv.Instr)
            {
                continue;
            }
            AInstr i = _lin[o.Instr];
            if (i.CopyDst < 0 || o.InMem || o.Operand is not (0 or 1))
            {
                continue;
            }
            int other = o.Operand == 0 ? i.CopySrc : i.CopyDst;
            if (!Tracked(other))
            {
                continue;
            }
            if (other < _fv)
            {
                return other;
            }
            int r = RegAt(other, o.Instr);
            if (r >= 0)
            {
                return r;
            }
        }
        return -1;
    }

    /// <summary>The position of the next read or write of a register at or after an instruction; MaxValue if none.</summary>
    private int NextUse(int vreg, int instr)
    {
        for (int occ = _occFirst[vreg], occEnd = _occFirst[vreg + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            if (o.Instr >= instr)
            {
                return o.Instr * 4;
            }
        }
        return int.MaxValue;
    }

    /// <summary>--trace-regalloc NAME: every choice of what gives up its register, in functions whose name holds NAME.</summary>
    public static string? Trace;
    private bool Tracing => Trace is { } t && _f.Name.Contains(t, StringComparison.Ordinal);

    /// <summary>Whether every occurrence of a value at an instruction is a read that may take its spill slot instead.</summary>
    private bool FoldsAt(int vreg, int at)
    {
        if (_remat[vreg] is not null) return false;
        bool any = false;
        for (int occ = _occFirst[vreg], occEnd = _occFirst[vreg + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            if (o.Instr != at) continue;
            if (o.Role != Role.Use || !CanFold(vreg, o)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// No register is free for the interval: spill it or the active
    /// interval whose next use is furthest away, whichever is further.
    /// Returns the register the current interval gets, or -1 if it was
    /// the one spilled.
    /// </summary>
    private int MakeRoom(Interval cur)
    {
        int at = cur.Start / 4;
        List<Interval>? victims = null;
        int victimNext = -1;
        // IN A LOOP, WHAT A LAP PAYS decides (LapCost), the furthest next use
        // only between equals: by distance alone a loop's counter, next read
        // at the bottom of the lap, was given up before a value read once in
        // one arm, and a value written three times a lap -- each write then a
        // store -- before one written once.
        int loop = at < _lin.Count ? _loopOf[_blockOfInstr[at]] : -1;
        long victimCost = long.MaxValue;
        // A register is had by evicting everything placed in it that is
        // live where the current interval is; the others sit in its holes.
        foreach (int r in PreferenceFor(cur.VReg))
        {
            if (!Allocatable(r, cur))
            {
                if (Tracing) Console.Error.WriteLine($"regalloc {_f.Name} at {at} for v{cur.VReg}: {_t.RegName(r)} not usable or held by a fixed use where it is live");
                continue;
            }
            // Loops, not lambdas: each lambda was a closure over the
            // allocator, and handed to LINQ it took the allocator with it --
            // the one object of the pass the collector, not the pass, freed.
            List<Interval> group = new();
            bool unservedShort = false;
            int next = int.MaxValue, foldedHere = 0;
            foreach (Interval a in _active)
            {
                if (a.Reg != r || !Overlaps(a, cur)) continue;
                group.Add(a);
                if (a.Short && a.Served is null) { unservedShort = true; break; }
                int use = a.Short ? NextServed(a, at) : NextUse(a.VReg, at);
                // READ HERE FROM ITS SLOT AS WELL: a value this instruction
                // only reads, in an operand that may be memory, gives way here
                // as well as anywhere -- `mov t, [slot]` is the copy, not a
                // reload more (Appel and George's CISC operands).
                if (use <= cur.Start && !a.Short && FoldsAt(a.VReg, at)) { use = NextUse(a.VReg, at + 1); foldedHere++; }
                if (use < next) next = use;
            }
            // Only one operand of an instruction may be memory: two values read
            // here from their slots would leave the second wanting a register.
            if (group.Count == 0 || unservedShort || foldedHere > 1)
            {
                if (Tracing) Console.Error.WriteLine($"regalloc {_f.Name} at {at} for v{cur.VReg}: {_t.RegName(r)} {(unservedShort ? "holds a reload for one instruction" : "has nothing live there to give up")}");
                continue;
            }
            if (next <= cur.Start)
            {
                // Used by the very instruction the current interval starts at: not evictable.
                continue;
            }
            long cost = 0;
            if (loop >= 0) foreach (Interval a in group) cost += a.Short ? LapCostServed(a, at, loop) : LapCost(a.VReg, at, loop);
            if (Tracing)
                Console.Error.WriteLine($"regalloc {_f.Name} at {at} loop {loop} for v{cur.VReg}{(cur.Short ? "s" : "")}: {_t.RegName(r)} holds {string.Join(",", group.Select(a => "v" + a.VReg + (a.Short ? "s" : "")))} cost {cost} next {next}");
            if (loop >= 0 ? cost < victimCost || cost == victimCost && next > victimNext : next > victimNext)
            {
                victims = group;
                victimNext = next;
                victimCost = cost;
            }
        }

        if (!cur.Short)
        {
            int curNext = NextUseAfterStart(cur);
            // The current interval read again in its own run needs a register
            // there at once: given up, it would only take one back.
            bool curSoon = curNext != int.MaxValue && curNext / 4 < _lin.Count && _runHead[_blockOfInstr[curNext / 4]] == _runHead[_blockOfInstr[at]];
            long curCost = loop >= 0 ? (curSoon ? long.MaxValue : LapCost(cur.VReg, at, loop)) : 0;
            if (victims is null || (loop >= 0 ? curCost < victimCost || curCost == victimCost && curNext > victimNext : curNext > victimNext))
            {
                if (Tracing) Console.Error.WriteLine($"regalloc {_f.Name} at {at}: v{cur.VReg} itself given up (cost {curCost} next {curNext})");
                int split = SplitPrefix(cur);
                if (split >= 0)
                {
                    return split;
                }
                Spill(cur.VReg, at);
                return -1;
            }
        }
        // NOTHING LEFT THAT CAN SIMPLY WAIT, so take the register from
        // somebody who is being read RIGHT HERE and can be read from the
        // frame instead.
        //
        // This is what a 64-bit shift ran into. `shld v15, v14, cl` needs two
        // registers of its own beside the count, and in a body where the
        // selector had already pinned four physical ones -- a signal handler's
        // loop, where Deliver is inlined twice -- one register was left and
        // both halves wanted it. Every active interval was skipped as a victim
        // because its next use was this very instruction, and the allocator
        // gave up with "no register for v14".
        //
        // But `shld`'s first operand may be memory, and so may the first
        // operand of most of this machine's instructions. An interval whose
        // every appearance here can be folded into its spill slot does not
        // need the register at all: spilling it from this instruction turns
        // those appearances into memory operands and hands the register over.
        // Nothing is skipped and nothing is approximated -- the value still
        // goes where it was going, through the frame instead of a register.
        if (victims is null)
        {
            foreach (Interval a in _active)
            {
                if (a.Short || !Allocatable(a.Reg, cur) || _remat[a.VReg] is not null || !Overlaps(a, cur)
                    || Taken(a.Reg, cur, a))
                {
                    continue;
                }

                bool foldable = false;

                for (int occ = _occFirst[a.VReg], occEnd = _occFirst[a.VReg + 1]; occ < occEnd; occ++)

                {

                    Occurrence o = _occAll[occ];
                    if (o.Instr != at)
                    {
                        continue;
                    }
                    if (!CanFold(a.VReg, o))
                    {
                        foldable = false;
                        break;
                    }
                    foldable = true;
                }

                if (foldable)
                {
                    victims = new() { a };
                    break;
                }
            }
        }

        if (victims is null)
        {
            // Every register holds something read by this instruction in a
            // position that cannot be memory. No instruction the selector
            // emits has that many.
            throw new InvalidOperationException(
                $"{_f.Name}: no register for v{cur.VReg} at instruction {at}");
        }

        if (Tracing) Console.Error.WriteLine($"regalloc {_f.Name} at {at}: {string.Join(",", victims.Select(a => "v" + a.VReg + (a.Short ? "s" : "")))} give up {_t.RegName(victims[0].Reg)} to v{cur.VReg}");
        foreach (Interval victim in victims)
        {
            _active.Remove(victim);
            if (victim.Short)
            {
                Truncate(victim, at);
            }
            else
            {
                Spill(victim.VReg, at);
            }
        }
        return victims[0].Reg;
    }

    // ---- splitting ------------------------------------------------------------------

    /// <summary>
    /// The first position of an interval at which a register is not free
    /// for it -- fixed there, or held by a placed interval -- or MaxValue.
    /// </summary>
    private int FreeUntil(int reg, Interval iv)
    {
        if (!Usable(reg, iv.VReg))
        {
            return iv.Start;
        }
        int until = int.MaxValue;
        for (int k = 0; k < RangeCount(iv); k++)
        {
            (int s, int e) = RangeAt(iv, k);
            if (Busy(reg, s, e))
            {
                int lo = s, hi = e;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (Busy(reg, s, mid)) hi = mid; else lo = mid + 1;
                }
                until = lo;
                break;
            }
        }
        foreach (Interval a in _active)
        {
            if (a.Reg != reg || a.End < iv.Start)
            {
                continue;
            }
            int x = 0, y = 0, na = RangeCount(a), ni = RangeCount(iv);
            while (x < na && y < ni)
            {
                (int S, int E) rx = RangeAt(a, x), ry = RangeAt(iv, y);
                if (rx.E < ry.S) x++;
                else if (ry.E < rx.S) y++;
                else
                {
                    until = Math.Min(until, Math.Max(rx.S, ry.S));
                    break;
                }
            }
        }
        return until;
    }

    /// <summary>
    /// No register is free for the whole of an interval that is about to
    /// be spilled: give it one for as long as some register stays free --
    /// up to a call, a string move's fixed ESI/EDI/ECX, a divide's EDX --
    /// and spill it only from there. Its reads before the split keep the
    /// register; every write still stores, so the slot is current wherever
    /// the frame takes over. Returns the register, or -1 if no register
    /// covers even the interval's first instruction that touches it.
    /// </summary>
    private int SplitPrefix(Interval cur)
    {
        int first = _occFirst[cur.VReg + 1] > _occFirst[cur.VReg] ? _occAll[_occFirst[cur.VReg]].Instr : int.MaxValue;
        int best = -1, bestUntil = -1;
        foreach (int r in PreferenceFor(cur.VReg))
        {
            int until = FreeUntil(r, cur);
            if (until > bestUntil)
            {
                best = r;
                bestUntil = until;
            }
        }
        if (best < 0 || bestUntil == int.MaxValue)
        {
            return -1;
        }
        // The register is needed only up to the last read or write before
        // the conflict: the slot serves from there.
        int from = -1;
        for (int occ = _occFirst[cur.VReg], occEnd = _occFirst[cur.VReg + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            if (o.Instr >= bestUntil / 4)
            {
                break;
            }
            from = o.Instr + 1;
        }
        // Only within the block it starts in: a prefix carried across
        // blocks holds its register over stretches with no use at all,
        // and the intervals that come after pay for it.
        if (from <= first || _blockOfInstr[from - 1] != _blockOfInstr[first])
        {
            return -1;
        }
        List<(int S, int E)> clipped = new();
        for (int k = 0; k < RangeCount(cur); k++)
        {
            (int s, int e) = RangeAt(cur, k);
            if (s >= from * 4)
            {
                break;
            }
            clipped.Add((s, Math.Min(e, from * 4 - 1)));
        }
        if (clipped.Count == 0)
        {
            return -1;
        }
        cur.Ranges = clipped;
        cur.End = clipped[^1].E;
        Spill(cur.VReg, from);
        return best;
    }

    // ---- carrying a reload on -------------------------------------------------------

    /// <summary>
    /// A spilled register just got a register for one instruction. While
    /// that register stays free, keep the value in it for the register's
    /// next reads and writes along the same straight run, so they need no reload: a
    /// value read three times in a row is loaded once, not three times.
    ///
    /// The reach stops where its run of blocks ends (a block entered from
    /// anywhere but the one before it), at any call (a collector may
    /// move what the slot holds, and the stack map names only the slot),
    /// at any fixed use of the register, and at a write folded into the
    /// slot, which the register would not see. Every write in the reach
    /// still stores to the slot, so the slot is always current and the
    /// reach can be cut short again when another interval needs the
    /// register (see Truncate).
    /// </summary>
    private void Reach(Interval cur)
    {
        int v = cur.VReg;
        int i = cur.Instr;
        int run = _runHead[_blockOfInstr[i]];
        for (int occ = _occFirst[v], occEnd = _occFirst[v + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            int j = o.Instr;
            if (j <= i)
            {
                continue;
            }
            if (_runHead[_blockOfInstr[j]] != run || _callsBefore[j] - _callsBefore[i] != 0)
            {
                break;
            }
            if (_folded.Contains((j, o.Operand)))
            {
                if ((o.Role & Role.Def) != 0)
                {
                    break;
                }
                continue;
            }
            if (cur.Served is not null && cur.Served[^1].Instr == j)
            {
                continue;
            }
            if (!_pending.TryGetValue((v, j), out Interval? next) || Busy(cur.Reg, cur.Start, next.End)
                || SpanTaken(cur, next.End))
            {
                break;
            }
            _pending.Remove((v, j));
            next.Cancelled = true;
            cur.Served ??= new List<(int, Role)> { (i, cur.Role) };
            cur.Served.Add((j, next.Role));
            cur.End = next.End;
            _shortReg[(v, j)] = cur.Reg;
            if ((next.Role & Role.Use) != 0)
            {
                _noReload.Add((v, j));
            }
        }
    }

    private static int NextServed(Interval a, int at)
    {
        foreach ((int instr, _) in a.Served!)
        {
            if (instr >= at)
            {
                return instr * 4;
            }
        }
        return int.MaxValue;
    }

    /// <summary>
    /// Give a carried reload's register back from instruction `at` on:
    /// the instructions from there are queued again as short intervals of
    /// their own, each reloading from the slot as it would have.
    /// </summary>
    private void Truncate(Interval a, int at)
    {
        List<(int Instr, Role Role)> served = a.Served!;
        int keep = 0;
        while (keep < served.Count && served[keep].Instr < at) keep++;
        if (keep == served.Count) keep = -1;
        for (int k = keep; k < served.Count; k++)
        {
            (int instr, Role role) = served[k];
            _shortReg.Remove((a.VReg, instr));
            _noReload.Remove((a.VReg, instr));
            EnqueueShort(a.VReg, instr, role);
        }
        served.RemoveRange(keep, served.Count - keep);
        (int lastInstr, Role lastRole) = served[^1];
        a.End = (lastRole & Role.Def) != 0 ? lastInstr * 4 + 3 : lastInstr * 4 + 1;
    }

    private int NextUseAfterStart(Interval cur)
    {
        int at = cur.Start / 4;
        for (int occ = _occFirst[cur.VReg], occEnd = _occFirst[cur.VReg + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            if (o.Instr > at)
            {
                return o.Instr * 4;
            }
        }
        return int.MaxValue;
    }

    /// <summary>
    /// Whether this occurrence can read or write the spill slot directly.
    /// Only one operand of an instruction may be memory, and the register
    /// must not also appear elsewhere in the same instruction.
    /// </summary>
    private bool CanFold(int vreg, Occurrence o)
    {
        AInstr i = _lin[o.Instr];
        if (o.InMem || o.Operand < 0 || (i.MayMemory >> o.Operand & 1) == 0)
        {
            return false;
        }
        // A WRITE INTO THE SLOT only where it writes all of it: x86-64's
        // 32-bit or byte write leaves the top half as it was, and a reload
        // is the whole eight bytes (AInstr.MayMemoryWrite).
        if ((o.Role & Role.Def) != 0 && (i.MayMemoryWrite >> o.Operand & 1) == 0)
        {
            return false;
        }
        int hits = 0;
        for (int occ = _occFirst[vreg], occEnd = _occFirst[vreg + 1]; occ < occEnd; occ++)
        {
            Occurrence other = _occAll[occ];
            if (other.Instr == o.Instr)
            {
                hits++;
            }
        }
        if (hits > 1)
        {
            return false;
        }
        for (int k = 0; k < i.OperandCount; k++)
        {
            if (k != o.Operand && ((i.IsMemory >> k & 1) != 0 || _folded.Contains((o.Instr, k))))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Move a register to the frame from instruction `from` on. Reads and
    /// writes before it keep the register it had; every later one becomes
    /// a memory operand where the instruction allows, else a short
    /// interval to be allocated as the scan reaches it.
    /// </summary>
    private void Spill(int vreg, int from)
    {
        // A register split before (see SplitPrefix) may be evicted from
        // its prefix later: only the instructions between the new spill
        // point and the old one are left to hand to the frame.
        int until = _spilledFrom[vreg];
        bool again = until != int.MaxValue;
        _spilledFrom[vreg] = from;
        if (_remat[vreg] is null && (!again || _keepBefore.Contains(vreg)) && NothingFlowsBack(vreg, from))
        {
            _keepBefore.Add(vreg);
        }
        else
        {
            _keepBefore.Remove(vreg);
        }
        bool remat = _remat[vreg] is not null;
        if (_slot[vreg] == 0 && !remat)
        {
            _slot[vreg] = TakeSlot(vreg);
        }
        int lastInstr = -1;
        Role merged = Role.None;
        for (int occ = _occFirst[vreg], occEnd = _occFirst[vreg + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            if (remat && (o.Role & Role.Def) != 0)
            {
                // The defining move is gone: the constant is written where it is used instead.
                continue;
            }
            if (o.Instr >= until || (again && o.Instr < from))
            {
                // Settled by the earlier spill, or kept in the register as before.
                continue;
            }
            // Before the spill point a register that keeps its value there
            // must see every write: none may go to the slot alone.
            bool kept = o.Instr < from && _keepBefore.Contains(vreg);
            if (!kept && (remat ? CanFoldImm(vreg, o) : CanFold(vreg, o)))
            {
                _folded.Add((o.Instr, o.Operand));
                continue;
            }
            if (o.Instr < from)
            {
                continue;
            }
            if (o.Instr != lastInstr && lastInstr >= 0)
            {
                EnqueueShort(vreg, lastInstr, merged);
                merged = Role.None;
            }
            lastInstr = o.Instr;
            merged |= o.Role;
        }
        if (lastInstr >= 0)
        {
            EnqueueShort(vreg, lastInstr, merged);
        }
    }

    // Whether a register is live into block `s`: one of those some block reads
    // before it writes (_globalOf), and in the block's live-in row.
    private bool LiveInto(int s, int vreg) => (uint)vreg < (uint)_n && _globalOf[vreg] is >= 0 and var at && _liveIn.Get(s, at);

    /// <summary>
    /// Whether no edge leaves the code from `from` on for a block before it
    /// where the register is live on entry. Then every read before `from`
    /// is reached only by paths that stay before it, where the register it
    /// was given still holds it, and needs no reload from the slot.
    /// </summary>

    private bool NothingFlowsBack(int vreg, int from)
    {
        if (from >= _lin.Count)
        {
            return true;
        }
        for (int b = _blockOfInstr[from]; b < _blockCount; b++)
        {
            for (int e = _succStart[b]; e < _succStart[b + 1]; e++)
            {
                int s = _succAll[e];
                if (_blockFirst[s] < from && LiveInto(s, vreg))
                {
                    return false;
                }
            }
        }
        return true;
    }

    // ---- loops, and what a spill costs a lap ------------------------------------

    /// <summary>Each block's innermost loop (-1 for none), and each loop's blocks and head.</summary>
    private int[] _loopOf = Array.Empty<int>();
    private readonly List<(int Head, bool[] Body)> _loops = new();

    /// <summary>
    /// The loops: an edge to a block still on the depth-first walk from the
    /// entry is a back edge, and its loop the blocks that reach its tail
    /// without passing its head (every back edge to a head one loop). Not
    /// the layout: a cold block laid out before the code that jumps to it is
    /// no loop's head.
    /// </summary>
    private void FindLoops()
    {
        int nb = _blockCount;
        _loops.Clear();
        _loopOf = new int[nb];
        Array.Fill(_loopOf, -1);
        if (nb == 0) return;
        byte[] state = new byte[nb];
        int[] stack = new int[nb], next = new int[nb];
        List<(int Tail, int Head)> backs = new();
        int top = 0;
        stack[top++] = 0; state[0] = 1; next[0] = _succStart[0];
        while (top > 0)
        {
            int b = stack[top - 1];
            if (next[b] < _succStart[b + 1])
            {
                int t = _succAll[next[b]++];
                if (state[t] == 1) backs.Add((b, t));
                else if (state[t] == 0) { state[t] = 1; next[t] = _succStart[t]; stack[top++] = t; }
            }
            else { state[b] = 2; top--; }
        }
        Dictionary<int, bool[]> byHead = new();
        foreach ((int tail, int head) in backs)
        {
            if (!byHead.TryGetValue(head, out bool[]? body)) { body = new bool[nb]; body[head] = true; byHead[head] = body; }
            Stack<int> work = new();
            if (!body[tail]) { body[tail] = true; work.Push(tail); }
            while (work.Count > 0)
            {
                int b = work.Pop();
                for (int e = _predStart[b]; e < _predStart[b + 1]; e++)
                    if (state[_predAll[e]] != 0 && !body[_predAll[e]]) { body[_predAll[e]] = true; work.Push(_predAll[e]); }
            }
        }
        List<(int Head, bool[] Body, int Size)> sized = byHead.Select(kv => (kv.Key, kv.Value, kv.Value.Count(x => x))).OrderBy(x => x.Item3).ToList();
        foreach ((int head, bool[] body, int size) in sized) _loops.Add((head, body));
        for (int l = 0; l < _loops.Count; l++)
            for (int b = 0; b < nb; b++)
                if (_loops[l].Body[b] && _loopOf[b] < 0) _loopOf[b] = l;
    }

    /// <summary>
    /// WHAT GIVING UP A VALUE AT `at` COSTS EACH LAP of `loop`, in frame
    /// accesses: a store for every write of it anywhere in the loop -- a
    /// spilled value's every write stores, those before the spill point too,
    /// so its slot is current wherever it is read -- a reload for each
    /// straight run that reads it from here on (a reload's register carries
    /// through a run), and one where the back edge comes in when the value
    /// is carried round and read before here (HeldBeforeSpill). A constant
    /// remade where it is read costs no store and half a load.
    /// </summary>
    private int LapCost(int vreg, int at, int loop)
    {
        bool[] body = _loops[loop].Body;
        bool remat = _remat[vreg] is not null;
        int cost = 0, lastRun = -1;
        bool before = false;
        for (int occ = _occFirst[vreg], occEnd = _occFirst[vreg + 1]; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            int b = _blockOfInstr[o.Instr];
            if (!body[b]) continue;
            if ((o.Role & Role.Def) != 0 && !remat) cost += 2;
            if (o.Instr < at) { if ((o.Role & Role.Use) != 0) before = true; continue; }
            if ((o.Role & Role.Use) != 0 && _runHead[b] != lastRun) { cost += remat ? 1 : 2; lastRun = _runHead[b]; }
        }
        if (before && LiveInto(_loops[loop].Head, vreg)) cost += remat ? 1 : 2;
        return cost;
    }

    /// <summary>A carried reload's: each of its reads from here on, in the loop, reloads again.</summary>
    private int LapCostServed(Interval a, int at, int loop)
    {
        bool[] body = _loops[loop].Body;
        int n = 0;
        foreach ((int instr, Role role) in a.Served!) if (instr >= at && body[_blockOfInstr[instr]]) n += 2;
        return n;
    }

    /// <summary>
    /// WHERE THE REGISTER STILL HOLDS IT, NO RELOAD. A register spilled from
    /// a point whose value flows back to code before it -- a loop's counter
    /// given up late in the body -- keeps the register it had over every
    /// position before that point (nothing placed after starts there), and
    /// every write before it stores to the slot as well. Each read there
    /// reloaded from the slot all the same, and the GUI kernel's vertex loop
    /// read its counter from the frame four times a lap. The register is
    /// stale only where control comes in from where it does not hold the
    /// value: a back edge from the spilled part, a landing pad, a block
    /// outside the stretch, or after a write that went to the slot alone. So
    /// a read reloads only when one of those paths reaches it; the rest are
    /// read where they are, the slot being current on every path either way.
    /// </summary>
    private void HeldBeforeSpill()
    {
        for (int v = _fv; v < _n; v++)
        {
            int from = _spilledFrom[v];
            int occStart = _occFirst[v], occEnd = _occFirst[v + 1];
            if (from == int.MaxValue || _remat[v] is not null || _keepBefore.Contains(v) || occStart == occEnd
                || _occAll[occStart].Instr >= from)
            {
                continue;
            }
            int lo = _blockOfInstr[_occAll[occStart].Instr];
            int hi = _blockOfInstr[Math.Min(from, _lin.Count) - 1];
            // A block's entry holds the value while every way in holds it at
            // its end: assumed of all, given up to a fixed point.
            if (_held.Length < hi - lo + 1) _held = new bool[Math.Max(hi - lo + 1, 2 * _held.Length)];
            bool[] held = _held;
            Array.Clear(held, 0, hi - lo + 1);
            Stack<int> work = _heldWork;
            work.Clear();
            for (int b = lo; b <= hi; b++)
            {
                if (LiveInto(b, v)) { held[b - lo] = true; work.Push(b); }
            }
            while (work.Count > 0)
            {
                int b = work.Pop();
                if (!held[b - lo] || HeldOnEntry(v, b, lo, hi, from, held)) continue;
                held[b - lo] = false;
                for (int e = _succStart[b]; e < _succStart[b + 1]; e++)
                {
                    int s = _succAll[e];
                    if (s >= lo && s <= hi && held[s - lo]) work.Push(s);
                }
            }
            for (int occ = occStart; occ < occEnd; occ++)
            {
                Occurrence o = _occAll[occ];
                if (o.Instr >= from) break;
                int b = _blockOfInstr[o.Instr];
                bool inReg = HeldBefore(v, o.Instr, held[b - lo]);
                if (!inReg || (o.Role & Role.Use) == 0) continue;
                // A read folded into the slot reads the register instead
                // where it holds the value: `mov edi, eax`, not from the frame.
                // Not a write: one folded into the slot leaves it stale.
                if (_folded.Contains((o.Instr, o.Operand)))
                {
                    if ((o.Role & Role.Def) != 0) continue;
                    _folded.Remove((o.Instr, o.Operand));
                }
                _noReload.Add((v, o.Instr));
            }
        }
    }

    private bool[] _held = new bool[64];
    private readonly Stack<int> _heldWork = new();

    /// Whether every way into block `b` leaves the value in its register.
    private bool HeldOnEntry(int v, int b, int lo, int hi, int from, bool[] held)
    {
        // The entry block is also entered from the caller, which no edge shows.
        if (b == 0 || _predStart[b] == _predStart[b + 1] || _f.Blocks[b].LandingPad)
        {
            return false;
        }
        for (int e = _predStart[b]; e < _predStart[b + 1]; e++)
        {
            int p = _predAll[e];
            int last = _blockFirst[p] + _f.Blocks[p].Instrs.Count - 1;
            if (p < lo || p > hi || last >= from || !HeldBefore(v, last + 1, held[p - lo], p))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Whether the register holds the value just before instruction `instr`
    /// of its block (`block`, that of `instr` unless given), entered holding
    /// it or not: after a read or write in the register it does, after a
    /// write folded into the slot alone it does not, and a read of the slot
    /// leaves it as it was.
    /// </summary>
    private bool HeldBefore(int v, int instr, bool onEntry, int block = -1)
    {
        if (block < 0) block = _blockOfInstr[instr];
        int first = _blockFirst[block];
        bool inReg = onEntry;
        // The block's first occurrence, by halves: a counter has hundreds.
        int occ = _occFirst[v], occEnd = _occFirst[v + 1], hiOcc = occEnd;
        while (occ < hiOcc)
        {
            int mid = (occ + hiOcc) / 2;
            if (_occAll[mid].Instr < first) occ = mid + 1; else hiOcc = mid;
        }
        for (; occ < occEnd; occ++)
        {
            Occurrence o = _occAll[occ];
            if (o.Instr >= instr) break;
            if (!_folded.Contains((o.Instr, o.Operand))) inReg = true;
            else if ((o.Role & Role.Def) != 0) inReg = false;
        }
        return inReg;
    }

    /// <summary>
    /// A spill slot for a register: one whose previous owner's interval
    /// ended before this one begins, if there is such a slot, else a new one.
    /// Intervals that never overlap can share the same four bytes.
    /// </summary>
    private int TakeSlot(int vreg)
    {
        // A HOME IS THE SLOT, for the value loaded from it and for its pieces:
        // never anyone else's after (it goes on no free list) -- nothing but
        // its one load reads it.
        int homed = HomeOf(vreg);
        if (homed != 0) return homed;
        // A split value's pieces share one slot, held over all of them (LoopSplit).
        int start = _start[vreg], end = _end[vreg], root = -1;
        if (_split is not null && _split.Pieces.TryGetValue(_split.RootOf(vreg), out List<int>? pieces))
        {
            root = _split.RootOf(vreg);
            if (_rootSlot.TryGetValue(root, out int shared)) return shared;
            foreach (int v in pieces)
            {
                if (v >= _n || _end[v] < 0) continue;
                start = Math.Min(start, _start[v]);
                end = Math.Max(end, _end[v]);
            }
        }
        int taken = -1;
        for (int k = 0; k < _freeSlots.Count; k++)
        {
            if (_freeSlots[k].FreeFrom < start)
            {
                taken = _freeSlots[k].Slot;
                _freeSlots.RemoveAt(k);
                break;
            }
        }
        if (taken < 0) taken = _t.NewSlot(vreg);
        _freeSlots.Add((taken, end));
        if (root >= 0) _rootSlot[root] = taken;
        return taken;
    }

    /// <summary>A value's home (AInstr.HomeSlot), or its split root's: 0 for none.</summary>
    private int HomeOf(int vreg)
    {
        if (_home.Length == 0) return 0;
        int root = _split?.RootOf(vreg) ?? vreg;
        return (uint)root < (uint)_home.Length ? _home[root] : 0;
    }

    private bool CanFoldImm(int vreg, Occurrence o)
    {
        AInstr i = _lin[o.Instr];
        if (o.InMem || o.Operand < 0 || (i.MayImmediate >> o.Operand & 1) == 0)
        {
            return false;
        }
        // Where only a narrower immediate fits, a plain number is fine; a symbol's address is not (ITarget.PlainConstant).
        return (i.MayImmediateWide >> o.Operand & 1) != 0 || _t.PlainConstant(_remat[vreg]!);
    }

    private void EnqueueShort(int vreg, int instr, Role role)
    {
        int p = instr * 4;
        Interval iv = new()
        {
            VReg = vreg,
            Short = true,
            Instr = instr,
            Role = role,
            Start = (role & Role.Use) != 0 ? p : p + 2,
            End = (role & Role.Def) != 0 ? p + 3 : p + 1,
        };
        _pending[(vreg, instr)] = iv;
        Enqueue(iv);
    }

    /// <summary>The register a virtual register occupies at an instruction, or -1 if it has none there.</summary>
    private int RegAt(int vreg, int instr)
    {
        if (vreg < _fv)
        {
            return vreg;
        }
        if (instr < _spilledFrom[vreg])
        {
            return _assigned[vreg];
        }
        return _shortReg.TryGetValue((vreg, instr), out int r) ? r : -1;
    }

    // ---- what was decided, for the target to rebuild from ------------------------------

    /// <summary>The target rebuilds the function, asking the decisions instruction by instruction (IPlacement).</summary>
    private void Rewrite() => _t.Rebuild(_f, new Decisions(this));

    /// <summary>
    /// WHAT THE SCAN DECIDED, instruction by instruction in layout order, as
    /// the target asks while it rebuilds: worked out for one instruction at a
    /// time, the one asked about. Each register an instruction names has its
    /// place; a spilled value's reads load it first and its writes store it
    /// after (once an instruction, whatever it does with it), unless a register
    /// still holds it; a constant's is remade.
    /// </summary>
    private sealed class Decisions : IPlacement
    {
        private readonly Allocator _a;
        private int _index = -1;
        private (Place Place, int Reg, int Slot, object? Constant)[] _places = Array.Empty<(Place, int, int, object?)>();
        private readonly List<(int Reg, int Slot, object? Constant)> _before = new();
        private readonly List<(int Reg, int Slot)> _after = new();
        private readonly HashSet<int> _done = new();
        private readonly Dictionary<int, Role> _roles = new();
        private readonly HashSet<int> _touched = new();
        private bool _dropped;
        /// <summary>The piece whose store back the copy decided leaves out (a copy between spilled pieces of one slot), or -1.</summary>
        private int _noStore = -1;

        public Decisions(Allocator a) => _a = a;

        public IReadOnlyCollection<int> Touched => _touched;

        public (Place Place, int Reg, int Slot, object? Constant) Of(int index, int k) { Decide(index); return _places[k]; }
        public IReadOnlyList<(int Reg, int Slot, object? Constant)> Before(int index) { Decide(index); return _before; }
        public IReadOnlyList<(int Reg, int Slot)> After(int index) { Decide(index); return _after; }
        public bool Dropped(int index) { Decide(index); return _dropped; }

        /// <summary>
        /// Where the values live across the call at `index` are, once the scan
        /// has placed them. INTERIM RULE: every live value that is not
        /// remade from a constant counts as a potential reference -- on i386 a
        /// reference and an int are both 32 bits and the IR does not tell them
        /// apart -- so the table is a superset of the truth, safe for a
        /// collector that does not move. Its slot wherever it has one, and its
        /// register besides when that register survives the call: a value
        /// spilled at its definition but counted as still in its register
        /// until a later point was in neither list once, and a tuple kept in
        /// the frame across three calls was swept from under it.
        /// </summary>
        public (List<int> Slots, List<int> Registers)? LiveAcross(int index)
        {
            Allocator a = _a;
            if (!a._liveAtCall.TryGetValue(index, out List<int>? live)) return null;
            List<int> slots = new(), registers = new();
            foreach (int v in live)
            {
                if (v < a._fv || a._remat[v] is not null) continue;
                // A home is an incoming argument's word, which the frame
                // outside reads as what it is pushing: no map names it.
                if (a._slot[v] != 0 && a.HomeOf(v) == 0) slots.Add(a._slot[v]);
                if (a._spilledFrom[v] > index && a._assigned[v] >= 0
                    && Array.IndexOf(a._rf.Classes[a._t.ClassOf(v)].CalleeSaved, a._assigned[v]) >= 0)
                    registers.Add(a._assigned[v]);
            }
            slots.Sort();
            return (slots, registers);
        }

        private void Decide(int index)
        {
            if (index == _index) return;
            _index = index;
            Allocator a = _a;
            AInstr i = a._lin[index];
            Named[] regs = i.Regs;
            if (_places.Length < regs.Length) _places = new (Place, int, int, object?)[Math.Max(regs.Length, 2 * _places.Length)];
            _before.Clear();
            _after.Clear();
            _done.Clear();
            _roles.Clear();
            _dropped = false;
            _noStore = -1;
            // A register named twice in one instruction (`movzx v, v8`) is
            // reloaded and stored according to everything the instruction does
            // with it, not just the first mention.
            foreach (Named n in regs)
                if (n.Reg >= a._fv) _roles[n.Reg] = _roles.GetValueOrDefault(n.Reg) | n.Role;

            // A COPY BETWEEN PIECES OF ONE SPLIT VALUE, both spilled: they share
            // the slot, which holds the source's value already -- every write of
            // a spilled value stores. Into the slot alone, no work at all; into
            // a register, a load and no store back.
            if (a._split is not null && a._split.Copies.Contains(i) && i.CopyDst >= a._fv && i.CopySrc >= a._fv
                && a._spilledFrom[i.CopyDst] != int.MaxValue && a._spilledFrom[i.CopySrc] != int.MaxValue
                && a._slot[i.CopyDst] == a._slot[i.CopySrc] && a._slot[i.CopyDst] != 0)
            {
                if (a._folded.Contains((index, 0))) { _dropped = true; return; }
                _noStore = i.CopyDst;
            }
            // The defining move of a spilled constant goes: see Spill. (A home's
            // load stays, even spilled from it: the register it loads may be
            // carried on to the reads after it, Reach. Only its store goes.)
            if (regs.Length > 0 && regs[0] is { Operand: 0, InMem: false } d && d.Reg >= a._fv
                && i.Constant is not null && a._remat[d.Reg] is not null && a._spilledFrom[d.Reg] != int.MaxValue)
            {
                _dropped = true;
                return;
            }

            for (int k = 0; k < regs.Length; k++)
            {
                Named n = regs[k];
                if (n.Operand < 0)
                {
                    // Unnamed: a physical register the instruction reads or writes as it must.
                    if ((n.Role & Role.Def) != 0) _touched.Add(n.Reg);
                    _places[k] = (Place.Register, n.Reg, 0, null);
                    continue;
                }
                if (n.Reg < a._fv)
                {
                    _touched.Add(n.Reg);
                    _places[k] = (Place.Register, n.Reg, 0, null);
                }
                else if (!n.InMem && a._folded.Contains((index, n.Operand)))
                {
                    _places[k] = a._remat[n.Reg] is { } constant ? (Place.Constant, -1, 0, constant) : (Place.Slot, -1, a._slot[n.Reg], null);
                }
                else
                {
                    _places[k] = (Place.Register, PlaceReg(n.Reg, i, index), 0, null);
                }
            }
            // A move onto itself is what a coalesced copy becomes; it goes, what is around it stays.
            if (i.DropsSelf && regs.Length >= 2 && _places[0].Place == Place.Register && _places[1].Place == Place.Register
                && regs[0].Operand == 0 && regs[1].Operand == 1 && _places[0].Reg == _places[1].Reg)
            {
                _dropped = true;
            }
        }

        private int PlaceReg(int v, AInstr i, int index)
        {
            Allocator a = _a;
            int reg = a.RegAt(v, index);
            if (reg < 0)
            {
                throw new InvalidOperationException($"{a._f.Name}: v{v} has no register at instruction {index} ({a._t.Text(i)}): live {a._start[v]}..{a._end[v]}, {a._occFirst[v + 1] - a._occFirst[v]} occurrence(s), assigned {a._assigned[v]}, spilled from {a._spilledFrom[v]}, {a._shortReg.Count} short, {a._lin.Count} instruction(s), {a._n} register(s)");
            }
            _touched.Add(reg);
            if (a._spilledFrom[v] != int.MaxValue && _done.Add(v))
            {
                Role role = _roles[v];
                bool held = a._noReload.Contains((v, index)) || (index < a._spilledFrom[v] && a._keepBefore.Contains(v));
                if (a._remat[v] is { } constant)
                {
                    if (!held) _before.Add((reg, 0, constant));
                }
                else
                {
                    if ((role & Role.Use) != 0 && !held) _before.Add((reg, a._slot[v], null));
                    // A home's one load writes nothing back: the slot holds it.
                    if ((role & Role.Def) != 0 && v != _noStore && !(i.HomeSlot is not null && v < a._home.Length && a._home[v] != 0))
                        _after.Add((reg, a._slot[v]));
                }
            }
            return reg;
        }
    }

}

/// <summary>
/// A set a row, every row the same width, in one array: the allocator's sets
/// for each block. Kept a thread by kind (Kept), grown as a function needs
/// and cleared as far as it uses; a table past KeptWords is the function's
/// alone, so one huge function does not keep it for the rest of the run.
/// </summary>
internal sealed class BitRows
{
    internal ulong[] Bits = Array.Empty<ulong>();
    internal int Words;

    [ThreadStatic] private static BitRows?[]? _kept;
    private const int KeptWords = 1 << 18;

    public static BitRows Kept(int kind, int rows, int size)
    {
        int words = (size + 63) / 64, need = rows * words;
        BitRows[] kept = (_kept ??= new BitRows?[3])!;
        BitRows table = need <= KeptWords ? kept[kind] ??= new BitRows() : new BitRows();
        table.Words = words;
        if (table.Bits.Length < need) table.Bits = new ulong[need];
        else Array.Clear(table.Bits, 0, need);
        return table;
    }

    public bool Get(int row, int i) => (Bits[row * Words + (i >> 6)] & (1UL << (i & 63))) != 0;
    public void Set(int row, int i) => Bits[row * Words + (i >> 6)] |= 1UL << (i & 63);

    public void OrRow(int row, BitSet o)
    {
        ulong[] from = o.Words;
        int at = row * Words;
        for (int k = 0; k < Words; k++) Bits[at + k] |= from[k];
    }

    public void CopyRow(int row, BitSet o) => Array.Copy(o.Words, 0, Bits, row * Words, Words);

    public bool RowEquals(int row, BitSet o) => Bits.AsSpan(row * Words, Words).SequenceEqual(o.Words);
}

/// <summary>A fixed-size set of small integers.</summary>
internal sealed class BitSet : IEquatable<BitSet>
{
    private readonly ulong[] _bits;

    public BitSet(int size) => _bits = new ulong[(size + 63) / 64];

    public bool Get(int i) => (_bits[i >> 6] & (1UL << (i & 63))) != 0;
    public void Set(int i) => _bits[i >> 6] |= 1UL << (i & 63);
    public void Clear(int i) => _bits[i >> 6] &= ~(1UL << (i & 63));

    public void Clear() => Array.Clear(_bits);

    public void Or(BitSet o)
    {
        for (int k = 0; k < _bits.Length; k++)
        {
            _bits[k] |= o._bits[k];
        }
    }

    public void AndNot(BitSet o)
    {
        for (int k = 0; k < _bits.Length; k++)
        {
            _bits[k] &= ~o._bits[k];
        }
    }

    public void CopyFrom(BitSet o) => Array.Copy(o._bits, _bits, _bits.Length);

    // A row of a BitRows table of the same width, as a set.
    public void CopyFrom(BitRows rows, int row) => Array.Copy(rows.Bits, row * rows.Words, _bits, 0, _bits.Length);

    public void Or(BitRows rows, int row)
    {
        ulong[] bits = rows.Bits;
        int at = row * rows.Words;
        for (int k = 0; k < _bits.Length; k++) _bits[k] |= bits[at + k];
    }

    public void AndNot(BitRows rows, int row)
    {
        ulong[] bits = rows.Bits;
        int at = row * rows.Words;
        for (int k = 0; k < _bits.Length; k++) _bits[k] &= ~bits[at + k];
    }

    internal ulong[] Words => _bits;

    /// <summary>The members, lowest first, by a struct walk: an iterator here was an object for every instruction the intervals were built over.</summary>
    public MemberWalk Members() => new(_bits);

    public struct MemberWalk
    {
        private readonly ulong[] _bits;
        private int _word;
        private ulong _left;
        private int _current;
        public MemberWalk(ulong[] bits) { _bits = bits; _word = -1; _left = 0; _current = -1; }
        public MemberWalk GetEnumerator() => this;
        public int Current => _current;
        public bool MoveNext()
        {
            while (_left == 0)
            {
                if (++_word >= _bits.Length) return false;
                _left = _bits[_word];
            }
            _current = _word * 64 + System.Numerics.BitOperations.TrailingZeroCount(_left);
            _left &= _left - 1;
            return true;
        }
    }

    public bool Equals(BitSet? o) => o is not null && _bits.AsSpan().SequenceEqual(o._bits);
    public override bool Equals(object? obj) => Equals(obj as BitSet);
    public override int GetHashCode() => 0;
}
