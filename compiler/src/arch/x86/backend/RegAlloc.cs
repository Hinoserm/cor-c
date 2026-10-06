#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;

namespace Corsac.Lang.X86;

/// <summary>
/// Linear-scan register allocation over EAX, ECX, EDX, EBX, ESI and EDI.
///
/// Liveness comes from a dataflow pass over the machine CFG, because the IR
/// is not SSA and a virtual register may be defined in several places. Each
/// virtual register gets one interval, the exact ranges of positions it is
/// live at, holes and all: another interval may share its register in the
/// holes. The physical registers the selector named (EAX around a
/// divide, ECX for a shift count, the caller-saved three at every call) are
/// not intervals but fixed busy positions that no interval may overlap.
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
internal sealed class Allocator
{
    private static readonly Gpr[] Preference =
    {
        // Caller-saved first: an interval that does not cross a call costs
        // nothing in one of these, while a callee-saved register costs a
        // push and a pop in the prologue and epilogue.
        Gpr.Eax, Gpr.Ecx, Gpr.Edx, Gpr.Ebx, Gpr.Esi, Gpr.Edi,
    };

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

    private readonly MFunction _m;
    private readonly List<MInstr> _lin = new();
    private readonly List<MBlock> _blockOf = new();
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
    private readonly int[][] _busy = new int[8][];
    private readonly int[] _assigned;
    private readonly int[] _spilledFrom;
    private readonly int[] _slot;
    private readonly Dictionary<(int VReg, int Instr), int> _shortReg = new();
    private readonly HashSet<(int Instr, int Operand)> _folded = new();
    /// <summary>Registers whose one definition is a constant: spilled, they are re-made rather than stored.</summary>
    private readonly MImm?[] _remat;
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
    /// <summary>
    /// The first block of the straight run each block belongs to: a block
    /// entered only from the one laid out before it continues that one's run.
    /// </summary>
    private int[] _runHead = Array.Empty<int>();
    private int[] _blockOfInstr = Array.Empty<int>();
    private int[][] _succ = Array.Empty<int[]>();
    private BitSet[] _liveIn = Array.Empty<BitSet>();
    private int _seq;

    private Allocator(MFunction m)
    {
        _m = m;
        foreach (MBlock b in m.Blocks)
        {
            foreach (MInstr i in b.Instrs)
            {
                _lin.Add(i);
                _blockOf.Add(b);
            }
        }
        _n = m.NextVReg;
        _occFirst = new int[_n + 1];
        _start = new int[_n];
        _end = new int[_n];
        _assigned = new int[_n];
        _spilledFrom = new int[_n];
        _slot = new int[_n];
        _remat = new MImm?[_n];
        _ranges = new List<(int, int)>?[_n];
        // A register's ranges made as it is first marked: most numbers are
        // halves never used, values selection dropped, or the machine's own.
        for (int v = 0; v < _n; v++)
        {
            _start[v] = int.MaxValue;
            _end[v] = -1;
            _assigned[v] = -1;
            _spilledFrom[v] = int.MaxValue;
        }
        for (int r = 0; r < 8; r++)
        {
            _busy[r] = new int[_lin.Count * 4 + 1];
        }
    }

    public static void Run(MFunction m)
    {
        PruneUnusedValues(m);
        Allocator a = new(m);
        a.CollectOccurrences();
        a.ComputeLiveness();
        a.Scan();
        a.Rewrite();
    }

    private static void PruneUnusedValues(MFunction m)
    {
        // Selection may make one half of a wide value unused. Do not create
        // spill traffic for its dead copies. MOV changes no flags, and only
        // register/immediate sources qualify: memory reads can be observable.
        bool changed;
        List<(MReg Reg, Role Role, int Operand, bool InMem)> regs = new();
        // The sets and the predicate are made once and emptied each round,
        // not made again for every round and every block.
        HashSet<MInstr> deadFlags = new();
        HashSet<int> used = new();
        Predicate<MInstr> unused = i => i.Operands.Count > 0 && i.Operands[0] is MReg dest && !dest.IsPhys
            && !used.Contains(dest.Id) && (deadFlags.Contains(i)
                || (i.Op == MOp.Mov && !i.Lock && i.Operands.Count == 2 && i.Operands[1] is not MMem));
        do
        {
            deadFlags.Clear();
            foreach (MBlock block in m.Blocks)
            {
                // No assumptions about flags on another block's edges.
                bool flagsLive = true;
                for (int k = block.Instrs.Count - 1; k >= 0; k--)
                {
                    MInstr i = block.Instrs[k];
                    if ((!flagsLive || i.Op == MOp.Not) && PureArithmetic(i)) deadFlags.Add(i);
                    if (i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or
                        or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Neg) flagsLive = false;
                    if (i.Op is MOp.Adc or MOp.Sbb or MOp.Jcc or MOp.Setcc) flagsLive = true;
                    else if (!KnownFlagEffect(i.Op)) flagsLive = true;
                }
            }
            used.Clear();
            foreach (MBlock block in m.Blocks)
            foreach (MInstr instruction in block.Instrs)
                foreach (var operand in RegsOf(instruction, regs))
                    if ((operand.Role & Role.Use) != 0
                        // A dead two-address update does not make its own
                        // discarded value live. Kept flag-producing updates
                        // still contribute that input, preserving their chain.
                        && !(operand.Operand == 0 && deadFlags.Contains(instruction)
                            && (operand.Role & Role.Def) != 0)) used.Add(operand.Reg.Id);
            changed = false;
            foreach (MBlock block in m.Blocks)
                changed |= block.Instrs.RemoveAll(unused) != 0;
        } while (changed);
    }

    private static bool PureArithmetic(MInstr i) => !i.Lock && i.Width == 4
        && i.Operands.Count > 0 && i.Operands[0] is MReg d && !d.IsPhys
        && RegistersAndImmediates(i)
        && i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor
            or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Not or MOp.Shl or MOp.Shr or MOp.Sar
            or MOp.Ror or MOp.Shld or MOp.Shrd;

    private static bool RegistersAndImmediates(MInstr i)
    {
        foreach (MOperand o in i.Operands)
        {
            if (o is not (MReg or MImm)) return false;
        }
        return true;
    }

    // Partial/conditional flag writers do not kill liveness: a zero shift
    // preserves flags, and rotate/multiply do not define all condition bits.
    // Unlisted instructions (notably calls and traps) remain barriers.
    private static bool KnownFlagEffect(MOp op) => op is
        MOp.Mov or MOp.Movsx or MOp.Movzx or MOp.Lea or MOp.Xchg or MOp.Push or MOp.Pop
        or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Test
        or MOp.Imul or MOp.Imul3 or MOp.Mul or MOp.ImulWide or MOp.Div or MOp.Idiv or MOp.Neg or MOp.Not or MOp.Bswap
        or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Ror or MOp.Shld or MOp.Shrd or MOp.Cdq
        or MOp.Setcc or MOp.Jcc or MOp.Jmp or MOp.JmpInd or MOp.JmpTable or MOp.Ret or MOp.Nop or MOp.Pause
        or MOp.RepMovsb or MOp.RepMovsd or MOp.RepStosb or MOp.RepStosd;

    // ---- occurrences ----------------------------------------------------------

    /// <summary>
    /// The registers an instruction names, with what it does with each, into
    /// `into` (emptied first), which is returned. A list the caller keeps for
    /// the whole pass: as an iterator it was an object for every instruction
    /// every walk looked at.
    /// </summary>
    private static List<(MReg Reg, Role Role, int Operand, bool InMem)> RegsOf(MInstr i, List<(MReg Reg, Role Role, int Operand, bool InMem)> into)
    {
        into.Clear();
        // `xor v, v` zeroes v without caring what it held: a definition
        // only, so no reload of a spilled v is needed before it.
        bool zeroing = i.Op == MOp.Xor && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r:
                    into.Add((r, zeroing ? Role.Def : Roles.Of(i.Op, k), k, false));
                    break;
                case MMem m:
                    if (m.Base is not null)
                    {
                        into.Add((m.Base, Role.Use, k, true));
                    }
                    if (m.Index is not null)
                    {
                        into.Add((m.Index, Role.Use, k, true));
                    }
                    break;
            }
        }
        foreach (Gpr g in Roles.ImplicitUses(i))
        {
            into.Add((MReg.Of(g), Role.Use, -1, false));
        }
        foreach (Gpr g in Roles.ImplicitDefs(i))
        {
            into.Add((MReg.Of(g), Role.Def, -1, false));
        }
        return into;
    }

    private static bool Tracked(MReg r) => r.Id != (int)Gpr.Esp && r.Id != (int)Gpr.Ebp;

    private void CollectOccurrences()
    {
        // Counted first, then placed: each register's run starts where the
        // counts before it end, and is filled in instruction order.
        for (int i = 0; i < _lin.Count; i++)
        {
            foreach ((MReg r, _, _, _) in RegsOf(_lin[i], _regsOf))
            {
                if (Tracked(r))
                {
                    _occFirst[r.Id + 1]++;
                }
            }
        }
        for (int v = 0; v < _n; v++)
        {
            _occFirst[v + 1] += _occFirst[v];
        }
        _occAll = new Occurrence[_occFirst[_n]];
        int[] fill = new int[_n];
        Array.Copy(_occFirst, fill, _n);
        for (int i = 0; i < _lin.Count; i++)
        {
            foreach ((MReg r, Role role, int k, bool inMem) in RegsOf(_lin[i], _regsOf))
            {
                if (Tracked(r))
                {
                    _occAll[fill[r.Id]++] = new Occurrence(i, k, role, inMem);
                }
            }
        }

        // A register defined exactly once, by a constant, need never live in
        // a spill slot: wherever it is wanted the constant can be written
        // again, and often folded into the instruction that wanted it.
        for (int v = 8; v < _n; v++)
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
                && _lin[d.Instr].Op == MOp.Mov && _lin[d.Instr].Width == 4 && _lin[d.Instr].Operands[1] is MImm imm)
            {
                _remat[v] = imm;
            }
        }
    }

    // ---- liveness ---------------------------------------------------------------

    private void Mark(int reg, int pos)
    {
        if (reg < 8)
        {
            _busy[reg][pos] = 1;
        }
        else
        {
            _start[reg] = Math.Min(_start[reg], pos);
            _end[reg] = Math.Max(_end[reg], pos);
            // The walk is backward within a block, marking an instruction's
            // late pair before its early one, so a run grows downward.
            List<(int S, int E)> list = _ranges[reg] ??= new List<(int S, int E)>();
            if (list.Count > 0 && pos >= list[^1].S - 2 && pos <= list[^1].E + 1)
            {
                list[^1] = (Math.Min(list[^1].S, pos), Math.Max(list[^1].E, pos));
            }
            else
            {
                list.Add((pos, pos));
            }
        }
    }

    private void ComputeLiveness()
    {
        int nb = _m.Blocks.Count;
        Dictionary<MBlock, int> index = new();
        int[] first = new int[nb];
        int[] count = new int[nb];
        for (int b = 0, at = 0; b < nb; b++)
        {
            index[_m.Blocks[b]] = b;
            first[b] = at;
            count[b] = _m.Blocks[b].Instrs.Count;
            at += count[b];
        }

        BitSet[] use = new BitSet[nb];
        BitSet[] def = new BitSet[nb];
        BitSet[] live = new BitSet[nb];
        for (int b = 0; b < nb; b++)
        {
            use[b] = new BitSet(_n);
            def[b] = new BitSet(_n);
            live[b] = new BitSet(_n);
            for (int i = first[b]; i < first[b] + count[b]; i++)
            {
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i], _regsOf))
                {
                    if (!Tracked(r))
                    {
                        continue;
                    }
                    if ((role & Role.Use) != 0 && !def[b].Get(r.Id))
                    {
                        use[b].Set(r.Id);
                    }
                }
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i], _regsOf))
                {
                    if (Tracked(r) && (role & Role.Def) != 0)
                    {
                        def[b].Set(r.Id);
                    }
                }
            }
        }

        int[][] succ = new int[nb][];
        List<int> to = new();
        for (int b = 0; b < nb; b++)
        {
            to.Clear();
            foreach (MBlock s in _m.Successors(b)) to.Add(index[s]);
            succ[b] = to.ToArray();
        }

        _blockFirst = first;
        _succ = succ;
        int[] preds = new int[nb];
        int[] onlyPred = new int[nb];
        for (int b = 0; b < nb; b++)
        {
            foreach (int s in succ[b])
            {
                preds[s]++;
                onlyPred[s] = b;
            }
        }
        _runHead = new int[nb];
        for (int b = 0; b < nb; b++)
        {
            _runHead[b] = b > 0 && preds[b] == 1 && onlyPred[b] == b - 1 && _m.Blocks[b].Source?.IsLandingPad != true
                ? _runHead[b - 1] : b;
        }
        _blockOfInstr = new int[_lin.Count];
        for (int b = 0; b < nb; b++)
        {
            for (int i = first[b]; i < first[b] + count[b]; i++)
            {
                _blockOfInstr[i] = b;
            }
        }

        // live-out(b) = union over successors s of use(s) | (live-out(s) & ~def(s));
        // iterate backwards over the layout until nothing changes.
        BitSet tmp = new(_n);
        BitSet inS = new(_n);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int b = nb - 1; b >= 0; b--)
            {
                tmp.Clear();
                foreach (int s in succ[b])
                {
                    inS.CopyFrom(live[s]);
                    inS.AndNot(def[s]);
                    inS.Or(use[s]);
                    tmp.Or(inS);
                }
                if (!tmp.Equals(live[b]))
                {
                    live[b].CopyFrom(tmp);
                    changed = true;
                }
            }
        }

        // live-in(b) = use(b) | (live-out(b) & ~def(b)), made in use(b)'s own
        // bits: nothing reads use(b) again, so it needs no set of its own.
        _liveIn = use;
        for (int b = 0; b < nb; b++)
        {
            tmp.CopyFrom(live[b]);
            tmp.AndNot(def[b]);
            use[b].Or(tmp);
        }

        // Backward walk of each block, marking every position each register
        // is live at. Virtual registers take the hull; physical ones the
        // exact positions, since a fixed register is free between its uses.
        BitSet cur = new(_n);
        for (int b = 0; b < nb; b++)
        {
            cur.CopyFrom(live[b]);
            for (int i = first[b] + count[b] - 1; i >= first[b]; i--)
            {
                int p = i * 4;
                if (_lin[i].Op is MOp.Call or MOp.CallInd)
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
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i], _regsOf))
                {
                    if (Tracked(r) && (role & Role.Def) != 0)
                    {
                        Mark(r.Id, p + 2);
                        Mark(r.Id, p + 3);
                        cur.Clear(r.Id);
                    }
                }
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i], _regsOf))
                {
                    if (Tracked(r) && (role & Role.Use) != 0)
                    {
                        cur.Set(r.Id);
                    }
                }
                foreach (int r in cur.Members())
                {
                    Mark(r, p);
                    Mark(r, p + 1);
                }
            }
        }

        for (int v = 8; v < _n; v++)
        {
            List<(int S, int E)>? list = _ranges[v];
            if (list is null || list.Count < 2)
            {
                continue;
            }
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

        // Prefix sums so "is r busy anywhere in [a, b]" is a subtraction.
        for (int r = 0; r < 8; r++)
        {
            int[] arr = _busy[r];
            for (int p = 1; p < arr.Length; p++)
            {
                arr[p] += arr[p - 1];
            }
        }
    }

    private bool Busy(int reg, int start, int end)
    {
        int[] arr = _busy[reg];
        int before = start == 0 ? 0 : arr[start - 1];
        return arr[Math.Min(end, arr.Length - 1)] - before > 0;
    }

    // ---- the scan ----------------------------------------------------------------

    private void Enqueue(Interval iv) => _unhandled.Enqueue(iv, (iv.Start, _seq++));

    private void Scan()
    {
        _callsBefore = new int[_lin.Count + 1];
        for (int i = 0; i < _lin.Count; i++)
        {
            _callsBefore[i + 1] = _callsBefore[i] + (_lin[i].Op is MOp.Call or MOp.CallInd ? 1 : 0);
        }
        for (int v = 8; v < _n; v++)
        {
            if (_end[v] >= 0)
            {
                Enqueue(new Interval { VReg = v, Start = _start[v], End = _end[v], Ranges = _ranges[v]! });
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

    private bool Allocatable(int reg, Interval iv)
    {
        if (reg == (int)Gpr.Esp || reg == (int)Gpr.Ebp)
        {
            return false;
        }
        if (reg > (int)Gpr.Ebx && _m.ByteRegs.Contains(iv.VReg))
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
        foreach (Gpr g in Preference)
        {
            int r = (int)g;
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
            MInstr i = _lin[o.Instr];
            if (i.Op != MOp.Mov || o.InMem || i.Operands.Count != 2)
            {
                continue;
            }
            if (i.Operands[1 - o.Operand] is not MReg other || !Tracked(other))
            {
                continue;
            }
            if (other.IsPhys)
            {
                return other.Id;
            }
            int r = RegAt(other.Id, o.Instr);
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
        // A register is had by evicting everything placed in it that is
        // live where the current interval is; the others sit in its holes.
        foreach (Gpr g in Preference)
        {
            int r = (int)g;
            if (!Allocatable(r, cur))
            {
                continue;
            }
            // Loops, not lambdas: each lambda was a closure over the
            // allocator, and handed to LINQ it took the allocator with it --
            // the one object of the pass the collector, not the pass, freed.
            List<Interval> group = new();
            bool unservedShort = false;
            int next = int.MaxValue;
            foreach (Interval a in _active)
            {
                if (a.Reg != r || !Overlaps(a, cur)) continue;
                group.Add(a);
                if (a.Short && a.Served is null) { unservedShort = true; break; }
                int use = a.Short ? NextServed(a, at) : NextUse(a.VReg, at);
                if (use < next) next = use;
            }
            if (group.Count == 0 || unservedShort)
            {
                continue;
            }
            if (next <= cur.Start)
            {
                // Used by the very instruction the current interval starts at: not evictable.
                continue;
            }
            if (next > victimNext)
            {
                victims = group;
                victimNext = next;
            }
        }

        if (!cur.Short)
        {
            int curNext = NextUseAfterStart(cur);
            if (victims is null || curNext > victimNext)
            {
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
                $"{_m.Source.Name}: no register for v{cur.VReg} at instruction {at}");
        }

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
        if (reg == (int)Gpr.Esp || reg == (int)Gpr.Ebp || (reg > (int)Gpr.Ebx && _m.ByteRegs.Contains(iv.VReg)))
        {
            return iv.Start;
        }
        int until = int.MaxValue;
        foreach ((int s, int e) in iv.Ranges!)
        {
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
            List<(int S, int E)> ra = a.Ranges ?? new() { (a.Start, a.End) };
            int x = 0, y = 0;
            while (x < ra.Count && y < iv.Ranges.Count)
            {
                if (ra[x].E < iv.Ranges[y].S) x++;
                else if (iv.Ranges[y].E < ra[x].S) y++;
                else
                {
                    until = Math.Min(until, Math.Max(ra[x].S, iv.Ranges[y].S));
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
        foreach (Gpr g in Preference)
        {
            int r = (int)g;
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
        foreach ((int s, int e) in cur.Ranges!)
        {
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

    private static bool Foldable(MOp op, int operand) => op switch
    {
        MOp.Mov or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp => true,
        MOp.Push or MOp.Test or MOp.Mul or MOp.ImulWide or MOp.Div or MOp.Idiv or MOp.Neg or MOp.Not
            or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Ror or MOp.Shld or MOp.Shrd or MOp.Setcc or MOp.CallInd => operand == 0,
        MOp.Imul or MOp.Imul3 or MOp.Movzx or MOp.Movsx => operand == 1,
        _ => false,
    };

    /// <summary>
    /// Whether this occurrence can read or write the spill slot directly.
    /// Only one operand of an instruction may be memory, and the register
    /// must not also appear elsewhere in the same instruction.
    /// </summary>
    private bool CanFold(int vreg, Occurrence o)
    {
        MInstr i = _lin[o.Instr];
        if (o.InMem || o.Operand < 0 || !Foldable(i.Op, o.Operand))
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
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (k != o.Operand && (i.Operands[k] is MMem || _folded.Contains((o.Instr, k))))
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
        for (int b = _blockOfInstr[from]; b < _succ.Length; b++)
        {
            foreach (int s in _succ[b])
            {
                if (_blockFirst[s] < from && _liveIn[s].Get(vreg))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// A spill slot for a register: one whose previous owner's interval
    /// ended before this one begins, if there is such a slot, else a new one.
    /// Intervals that never overlap can share the same four bytes.
    /// </summary>
    private int TakeSlot(int vreg)
    {
        for (int k = 0; k < _freeSlots.Count; k++)
        {
            if (_freeSlots[k].FreeFrom < _start[vreg])
            {
                int slot = _freeSlots[k].Slot;
                _freeSlots.RemoveAt(k);
                _freeSlots.Add((slot, _end[vreg]));
                return slot;
            }
        }
        int fresh = _m.Frame.Spill();
        _freeSlots.Add((fresh, _end[vreg]));
        return fresh;
    }

    /// <summary>Whether an operand position accepts an immediate in place of a register.</summary>
    private static bool FoldableImm(MOp op, int operand) => op switch
    {
        MOp.Mov or MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp => operand == 1,
        MOp.Push => true,
        _ => false,
    };

    private bool CanFoldImm(int vreg, Occurrence o)
    {
        MInstr i = _lin[o.Instr];
        if (o.InMem || o.Operand < 0 || !FoldableImm(i.Op, o.Operand))
        {
            return false;
        }
        // A byte or word immediate is fine; a symbol address is not (it is 32 bits wide).
        return i.Width == 4 || _remat[vreg]!.IsPlain;
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
        if (vreg < 8)
        {
            return vreg;
        }
        if (instr < _spilledFrom[vreg])
        {
            return _assigned[vreg];
        }
        return _shortReg.TryGetValue((vreg, instr), out int r) ? r : -1;
    }

    // ---- rewriting -----------------------------------------------------------------

    private void Rewrite()
    {
        HashSet<int> saved = new();
        int index = 0;
        foreach (MBlock b in _m.Blocks)
        {
            List<MInstr> outList = new();
            foreach (MInstr i in b.Instrs)
            {
                RewriteInstr(i, index, outList, saved);
                index++;
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(outList);
        }
        // A FUNCTION THAT CATCHES SAVES THEM ALL: a throw restores only ESP
        // and EBP, so the registers the unwound frames saved and used reach
        // its landing pad as they left them, and only an epilogue that
        // restores every one gives its caller back what it had.
        bool catches = false;
        foreach (MBlock b in _m.Blocks) catches |= b.Source?.IsLandingPad == true;
        foreach (Gpr g in new[] { Gpr.Ebx, Gpr.Esi, Gpr.Edi })
        {
            if (catches || saved.Contains((int)g))
            {
                _m.SavedRegs.Add(g);
            }
        }
    }

    /// <summary>
    /// Where the values live across the call at <paramref name="index"/>
    /// are, once the scan has placed them.
    ///
    /// INTERIM RULE: every live 32-bit value that is not known to be half
    /// of a 64-bit integer counts as a potential reference, because on x86
    /// a reference and an int are both IrType.I32 and the IR does not
    /// distinguish them. The table is therefore a superset of the truth --
    /// safe for a non-moving collector to scan, and not yet safe to move
    /// on, which is why the barriers and polls stay off. Narrowing it is a
    /// matter of tagging reference-typed IR registers, and the format does
    /// not change when that lands.
    ///
    /// A register that was spilled before the call is read from its slot;
    /// one whose value is re-made from a constant is no reference at all.
    /// </summary>
    private Safepoint MapOf(List<int> live, int index)
    {
        Safepoint map = new();
        foreach (int v in live)
        {
            // HALF OF A LONG COUNTS. On i386 the runtime keeps many an address
            // in a long -- a block the allocator answered, a word it read out
            // of an object -- and the collector now marks a register only
            // where the map says it is live: a long's low half in EBX across
            // a call that allocates is a reference as far as it can tell.
            if (v < 8 || _remat[v] is not null)
            {
                continue;
            }
            // ITS SLOT WHEREVER IT HAS ONE, and its register besides when that
            // register survives the call. The collector reads a frame by this
            // map now, and a value spilled at its definition but counted as
            // still in its register until a later point was in neither list:
            // a tuple kept at -64 across three calls was swept from under the
            // frame that held it. A slot listed before it is written costs a
            // stale word read; a slot left out costs the object.
            if (_slot[v] != 0)
            {
                map.SlotOffsets.Add(_slot[v]);
            }
            if (_spilledFrom[v] > index && _assigned[v] is (int)Gpr.Ebx or (int)Gpr.Esi or (int)Gpr.Edi)
            {
                map.Registers |= 1u << _assigned[v];
            }
        }
        map.SlotOffsets.Sort();
        return map;
    }

    // ONE SET OF SCRATCH TABLES for every instruction rewritten, cleared at
    // each: four collections an instruction, and the closure Place was over
    // them, were the register allocator's own garbage.
    private readonly List<MInstr> _rwBefore = new();
    private readonly List<MInstr> _rwAfter = new();
    private readonly HashSet<int> _rwDone = new();
    private readonly Dictionary<int, Role> _rwRoles = new();
    private readonly List<(MReg Reg, Role Role, int Operand, bool InMem)> _regsOf = new();

    private void RewriteInstr(MInstr i, int index, List<MInstr> outList, HashSet<int> saved)
    {
        List<MInstr> before = _rwBefore;
        List<MInstr> after = _rwAfter;
        before.Clear();
        after.Clear();
        _rwDone.Clear();
        _rwRoles.Clear();
        MInstr n = new(i.Op) { Width = i.Width, Cond = i.Cond, Lock = i.Lock, Table = i.Table, CallReloc = i.CallReloc, Line = i.Line, Native = i.Native };

        if (_liveAtCall.TryGetValue(index, out List<int>? live))
        {
            _m.Safepoints[n] = MapOf(live, index);
        }

        // A register named twice in one instruction (`movzx v, v8`) is
        // reloaded and stored according to everything the instruction does
        // with it, not just the first mention.
        foreach ((MReg r, Role role, _, _) in RegsOf(i, _regsOf))
        {
            if (!r.IsPhys)
            {
                _rwRoles[r.Id] = _rwRoles.GetValueOrDefault(r.Id) | role;
            }
        }

        // The defining move of a spilled constant is dropped: see Spill.
        if (i.Op == MOp.Mov && i.Operands[0] is MReg dv && !dv.IsPhys && _remat[dv.Id] is not null
            && _spilledFrom[dv.Id] != int.MaxValue)
        {
            return;
        }

        for (int k = 0; k < i.Operands.Count; k++)
        {
            MOperand o = i.Operands[k];
            switch (o)
            {
                case MReg r when !r.IsPhys && _folded.Contains((index, k)):
                    n.Operands.Add(_remat[r.Id] is MImm imm ? imm : MMem.Spill(_slot[r.Id]));
                    break;
                case MReg r:
                    n.Operands.Add(Place(r, i, index, saved));
                    break;
                case MMem m:
                    // Everything about the operand but its registers survives.
                    // Losing Reloc here turned a `[got + sym@GOTOFF]` back into
                    // an absolute address, which a shared object can only
                    // honour with a text relocation -- and not needing one is
                    // the whole point of position-independent code.
                    n.Operands.Add(new MMem(m.Base is null ? null : Place(m.Base, i, index, saved), m.Disp)
                    {
                        Index = m.Index is null ? null : Place(m.Index, i, index, saved),
                        Scale = m.Scale,
                        Symbol = m.Symbol,
                        Reloc = m.Reloc,
                        Label = m.Label,
                        IsSpill = m.IsSpill,
                    });
                    break;
                default:
                    n.Operands.Add(o);
                    break;
            }
        }
        foreach (Gpr g in Roles.ImplicitDefs(i))
        {
            saved.Add((int)g);
        }

        outList.AddRange(before);
        // A move onto itself is what a coalesced copy becomes; drop it.
        bool self = n.Op == MOp.Mov && n.Operands[0] is MReg a && n.Operands[1] is MReg c && a.Id == c.Id;
        if (!self)
        {
            outList.Add(n);
        }
        outList.AddRange(after);
    }

    private MReg Place(MReg r, MInstr i, int index, HashSet<int> saved)
    {
        if (r.IsPhys)
        {
            saved.Add(r.Id);
            return r;
        }
        int reg = RegAt(r.Id, index);
        if (reg < 0)
        {
            throw new InvalidOperationException($"{_m.Source.Name}: v{r.Id} has no register at instruction {index} ({i.Op}): live {_start[r.Id]}..{_end[r.Id]}, {_occFirst[r.Id + 1] - _occFirst[r.Id]} occurrence(s), assigned {_assigned[r.Id]}, spilled from {_spilledFrom[r.Id]}, {_shortReg.Count} short, {_lin.Count} instruction(s), {_n} register(s)");
        }
        saved.Add(reg);
        if (_spilledFrom[r.Id] != int.MaxValue && _rwDone.Add(r.Id))
        {
            Role role = _rwRoles[r.Id];
            bool held = _noReload.Contains((r.Id, index))
                || (index < _spilledFrom[r.Id] && _keepBefore.Contains(r.Id));
            if (_remat[r.Id] is MImm imm)
            {
                if (!held)
                    _rwBefore.Add(new MInstr(MOp.Mov, new MReg(reg), imm) { Line = i.Line });
            }
            else
            {
                if ((role & Role.Use) != 0 && !held)
                {
                    _rwBefore.Add(new MInstr(MOp.Mov, new MReg(reg), MMem.Spill(_slot[r.Id])) { Line = i.Line });
                }
                if ((role & Role.Def) != 0)
                {
                    _rwAfter.Add(new MInstr(MOp.Mov, MMem.Spill(_slot[r.Id]), new MReg(reg)) { Line = i.Line });
                }
            }
        }
        return new MReg(reg);
    }
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
