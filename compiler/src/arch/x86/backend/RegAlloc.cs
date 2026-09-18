#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;

namespace Corsac.Lang.X86;

/// <summary>
/// Linear-scan register allocation over EAX, ECX, EDX, EBX, ESI and EDI.
///
/// Liveness comes from a dataflow pass over the machine CFG, because the IR
/// is not SSA and a virtual register may be defined in several places. Each
/// virtual register gets one interval, the hull of every position it is
/// live at; the physical registers the selector named (EAX around a
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
/// in place and no register is needed at all.
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
    }

    private readonly record struct Occurrence(int Instr, int Operand, Role Role, bool InMem);

    private readonly MFunction _m;
    private readonly List<MInstr> _lin = new();
    private readonly List<MBlock> _blockOf = new();
    private readonly int _n;
    private readonly List<Occurrence>[] _occ;
    private readonly int[] _start;
    private readonly int[] _end;
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
        _occ = new List<Occurrence>[_n];
        _start = new int[_n];
        _end = new int[_n];
        _assigned = new int[_n];
        _spilledFrom = new int[_n];
        _slot = new int[_n];
        _remat = new MImm?[_n];
        for (int v = 0; v < _n; v++)
        {
            _occ[v] = new List<Occurrence>();
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
        do
        {
            HashSet<MInstr> deadFlags = new();
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
            HashSet<int> used = new();
            foreach (MBlock block in m.Blocks)
            foreach (MInstr instruction in block.Instrs)
                foreach (var operand in RegsOf(instruction))
                    if ((operand.Role & Role.Use) != 0
                        // A dead two-address update does not make its own
                        // discarded value live. Kept flag-producing updates
                        // still contribute that input, preserving their chain.
                        && !(operand.Operand == 0 && deadFlags.Contains(instruction)
                            && (operand.Role & Role.Def) != 0)) used.Add(operand.Reg.Id);
            changed = false;
            foreach (MBlock block in m.Blocks)
                changed |= block.Instrs.RemoveAll(i => i.Operands.Count > 0 && i.Operands[0] is MReg dest && !dest.IsPhys
                    && !used.Contains(dest.Id) && (deadFlags.Contains(i)
                        || (i.Op == MOp.Mov && !i.Lock && i.Operands.Count == 2 && i.Operands[1] is not MMem))) != 0;
        } while (changed);
    }

    private static bool PureArithmetic(MInstr i) => !i.Lock && i.Width == 4
        && i.Operands.Count > 0 && i.Operands[0] is MReg d && !d.IsPhys
        && i.Operands.All(o => o is MReg or MImm)
        && i.Op is MOp.Add or MOp.Adc or MOp.Sub or MOp.Sbb or MOp.And or MOp.Or or MOp.Xor
            or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Not or MOp.Shl or MOp.Shr or MOp.Sar
            or MOp.Ror or MOp.Shld or MOp.Shrd;

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

    private static IEnumerable<(MReg Reg, Role Role, int Operand, bool InMem)> RegsOf(MInstr i)
    {
        // `xor v, v` zeroes v without caring what it held: a definition
        // only, so no reload of a spilled v is needed before it.
        bool zeroing = i.Op == MOp.Xor && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r:
                    yield return (r, zeroing ? Role.Def : Roles.Of(i.Op, k), k, false);
                    break;
                case MMem m:
                    if (m.Base is not null)
                    {
                        yield return (m.Base, Role.Use, k, true);
                    }
                    if (m.Index is not null)
                    {
                        yield return (m.Index, Role.Use, k, true);
                    }
                    break;
            }
        }
        foreach (Gpr g in Roles.ImplicitUses(i))
        {
            yield return (MReg.Of(g), Role.Use, -1, false);
        }
        foreach (Gpr g in Roles.ImplicitDefs(i))
        {
            yield return (MReg.Of(g), Role.Def, -1, false);
        }
    }

    private static bool Tracked(MReg r) => r.Id != (int)Gpr.Esp && r.Id != (int)Gpr.Ebp;

    private void CollectOccurrences()
    {
        for (int i = 0; i < _lin.Count; i++)
        {
            foreach ((MReg r, Role role, int k, bool inMem) in RegsOf(_lin[i]))
            {
                if (Tracked(r))
                {
                    _occ[r.Id].Add(new Occurrence(i, k, role, inMem));
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
            foreach (Occurrence o in _occ[v])
            {
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
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i]))
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
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i]))
                {
                    if (Tracked(r) && (role & Role.Def) != 0)
                    {
                        def[b].Set(r.Id);
                    }
                }
            }
        }

        int[][] succ = new int[nb][];
        for (int b = 0; b < nb; b++)
        {
            succ[b] = _m.Successors(b).Select(s => index[s]).ToArray();
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
                    _liveAtCall[i] = cur.Members().ToList();
                }
                foreach (int r in cur.Members())
                {
                    Mark(r, p + 2);
                    Mark(r, p + 3);
                }
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i]))
                {
                    if (Tracked(r) && (role & Role.Def) != 0)
                    {
                        Mark(r.Id, p + 2);
                        Mark(r.Id, p + 3);
                        cur.Clear(r.Id);
                    }
                }
                foreach ((MReg r, Role role, _, _) in RegsOf(_lin[i]))
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
        for (int v = 8; v < _n; v++)
        {
            if (_end[v] >= 0)
            {
                Enqueue(new Interval { VReg = v, Start = _start[v], End = _end[v] });
            }
        }

        while (_unhandled.TryDequeue(out Interval? cur, out _))
        {
            _active.RemoveAll(a => a.End < cur.Start);

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
        return !Busy(reg, iv.Start, iv.End);
    }

    private int FreeRegister(Interval iv)
    {
        int hint = Hint(iv);
        if (hint >= 0 && Allocatable(hint, iv) && _active.All(a => a.Reg != hint))
        {
            return hint;
        }
        foreach (Gpr g in Preference)
        {
            int r = (int)g;
            if (Allocatable(r, iv) && _active.All(a => a.Reg != r))
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
        foreach (Occurrence o in _occ[iv.VReg])
        {
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
        foreach (Occurrence o in _occ[vreg])
        {
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
        Interval? victim = null;
        int victimNext = -1;
        foreach (Interval a in _active)
        {
            if (a.Short || !Allocatable(a.Reg, cur))
            {
                continue;
            }
            int next = NextUse(a.VReg, at);
            if (next <= cur.Start)
            {
                // Used by the very instruction the current interval starts at: not evictable.
                continue;
            }
            if (next > victimNext)
            {
                victim = a;
                victimNext = next;
            }
        }

        if (!cur.Short)
        {
            int curNext = NextUseAfterStart(cur);
            if (victim is null || curNext > victimNext)
            {
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
        if (victim is null)
        {
            foreach (Interval a in _active)
            {
                if (a.Short || !Allocatable(a.Reg, cur) || _remat[a.VReg] is not null)
                {
                    continue;
                }

                bool foldable = false;

                foreach (Occurrence o in _occ[a.VReg])
                {
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
                    victim = a;
                    break;
                }
            }
        }

        if (victim is null)
        {
            // Every register holds something read by this instruction in a
            // position that cannot be memory. No instruction the selector
            // emits has that many.
            throw new InvalidOperationException(
                $"{_m.Source.Name}: no register for v{cur.VReg} at instruction {at}");
        }

        _active.Remove(victim);
        Spill(victim.VReg, at);
        return victim.Reg;
    }

    private int NextUseAfterStart(Interval cur)
    {
        int at = cur.Start / 4;
        foreach (Occurrence o in _occ[cur.VReg])
        {
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
        foreach (Occurrence other in _occ[vreg])
        {
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
        _spilledFrom[vreg] = from;
        bool remat = _remat[vreg] is not null;
        if (_slot[vreg] == 0 && !remat)
        {
            _slot[vreg] = TakeSlot(vreg);
        }
        int lastInstr = -1;
        Role merged = Role.None;
        foreach (Occurrence o in _occ[vreg])
        {
            if (remat && (o.Role & Role.Def) != 0)
            {
                // The defining move is gone: the constant is written where it is used instead.
                continue;
            }
            if (remat ? CanFoldImm(vreg, o) : CanFold(vreg, o))
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
        Enqueue(new Interval
        {
            VReg = vreg,
            Short = true,
            Instr = instr,
            Start = (role & Role.Use) != 0 ? p : p + 2,
            End = (role & Role.Def) != 0 ? p + 3 : p + 1,
        });
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
        foreach (Gpr g in new[] { Gpr.Ebx, Gpr.Esi, Gpr.Edi })
        {
            if (saved.Contains((int)g))
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
            if (v < 8 || _m.WideHalves.Contains(v) || _remat[v] is not null)
            {
                continue;
            }
            if (_spilledFrom[v] <= index || _assigned[v] < 0)
            {
                if (_slot[v] != 0)
                {
                    map.SlotOffsets.Add(_slot[v]);
                }
                continue;
            }
            // Only a callee-saved register survives a call; anything else
            // here would mean the busy marks at calls were not honoured.
            if (_assigned[v] is (int)Gpr.Ebx or (int)Gpr.Esi or (int)Gpr.Edi)
            {
                map.Registers |= 1u << _assigned[v];
            }
        }
        map.SlotOffsets.Sort();
        return map;
    }

    private void RewriteInstr(MInstr i, int index, List<MInstr> outList, HashSet<int> saved)
    {
        List<MInstr> before = new();
        List<MInstr> after = new();
        HashSet<int> done = new();
        MInstr n = new(i.Op) { Width = i.Width, Cond = i.Cond, Lock = i.Lock, Table = i.Table, CallReloc = i.CallReloc, Line = i.Line };

        if (_liveAtCall.TryGetValue(index, out List<int>? live))
        {
            _m.Safepoints[n] = MapOf(live, index);
        }

        // A register named twice in one instruction (`movzx v, v8`) is
        // reloaded and stored according to everything the instruction does
        // with it, not just the first mention.
        Dictionary<int, Role> roles = new();
        foreach ((MReg r, Role role, _, _) in RegsOf(i))
        {
            if (!r.IsPhys)
            {
                roles[r.Id] = roles.GetValueOrDefault(r.Id) | role;
            }
        }

        MReg Place(MReg r)
        {
            if (r.IsPhys)
            {
                saved.Add(r.Id);
                return r;
            }
            int reg = RegAt(r.Id, index);
            if (reg < 0)
            {
                throw new InvalidOperationException($"{_m.Source.Name}: v{r.Id} has no register at instruction {index} ({i.Op}): live {_start[r.Id]}..{_end[r.Id]}, {_occ[r.Id].Count} occurrence(s), assigned {_assigned[r.Id]}, spilled from {_spilledFrom[r.Id]}, {_shortReg.Count} short, {_lin.Count} instruction(s), {_n} register(s)");
            }
            saved.Add(reg);
            if (_spilledFrom[r.Id] != int.MaxValue && done.Add(r.Id))
            {
                Role role = roles[r.Id];
                if (_remat[r.Id] is MImm imm)
                {
                    before.Add(new MInstr(MOp.Mov, new MReg(reg), imm) { Line = i.Line });
                }
                else
                {
                    if ((role & Role.Use) != 0)
                    {
                        before.Add(new MInstr(MOp.Mov, new MReg(reg), MMem.Spill(_slot[r.Id])) { Line = i.Line });
                    }
                    if ((role & Role.Def) != 0)
                    {
                        after.Add(new MInstr(MOp.Mov, MMem.Spill(_slot[r.Id]), new MReg(reg)) { Line = i.Line });
                    }
                }
            }
            return new MReg(reg);
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
                    n.Operands.Add(Place(r));
                    break;
                case MMem m:
                    // Everything about the operand but its registers survives.
                    // Losing Reloc here turned a `[got + sym@GOTOFF]` back into
                    // an absolute address, which a shared object can only
                    // honour with a text relocation -- and not needing one is
                    // the whole point of position-independent code.
                    n.Operands.Add(new MMem(m.Base is null ? null : Place(m.Base), m.Disp)
                    {
                        Index = m.Index is null ? null : Place(m.Index),
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

    public IEnumerable<int> Members()
    {
        for (int k = 0; k < _bits.Length; k++)
        {
            ulong w = _bits[k];
            while (w != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(w);
                yield return k * 64 + bit;
                w &= w - 1;
            }
        }
    }

    public bool Equals(BitSet? o) => o is not null && _bits.AsSpan().SequenceEqual(o._bits);
    public override bool Equals(object? obj) => Equals(obj as BitSet);
    public override int GetHashCode() => 0;
}
