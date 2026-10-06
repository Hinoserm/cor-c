#nullable enable
namespace Corsac.Lang.X64;

/// <summary>What each operand of each opcode does to its register, and what each instruction touches unnamed.</summary>
internal static class Roles
{
    [Flags]
    public enum Role : byte
    {
        None = 0,
        Use = 1,
        Def = 2,
        UseDef = 3,
    }

    public static Role Of(MInstr i, int operand)
    {
        switch (i.Op)
        {
            case MOp.Mov:
            case MOp.Movsx:
            case MOp.Movzx:
            case MOp.Lea:
            case MOp.Pop:
            case MOp.Setcc:
            case MOp.Imul3:
            case MOp.MovF:
            case MOp.MovGx:
            case MOp.CvtIntToF:
            case MOp.CvtFToInt:
            case MOp.CvtFToF:
            case MOp.SqrtF:
            case MOp.MovFromCr:
            case MOp.ReadFlags:
                return operand == 0 ? Role.Def : Role.Use;
            case MOp.Add:
            case MOp.Sub:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Adc:
            case MOp.Sbb:
            case MOp.Imul:
            case MOp.Neg:
            case MOp.Not:
            case MOp.Bswap:
            case MOp.Shl:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Rol:
            case MOp.Ror:
            case MOp.Cmovcc:
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.XorF:
                return operand == 0 ? Role.UseDef : Role.Use;
            case MOp.Xchg:
                return Role.UseDef;
            case MOp.Xadd:
                return operand == 0 ? Role.Use : Role.UseDef;
            default:
                return Role.Use;
        }
    }

    private static readonly int[] CallerSavedGprs =
    {
        (int)Gpr.Rax, (int)Gpr.Rcx, (int)Gpr.Rdx, (int)Gpr.Rsi, (int)Gpr.Rdi,
        (int)Gpr.R8, (int)Gpr.R9, (int)Gpr.R10, (int)Gpr.R11,
    };

    private static readonly int[] SyscallArgs =
    {
        (int)Gpr.Rdi, (int)Gpr.Rsi, (int)Gpr.Rdx, (int)Gpr.R10, (int)Gpr.R8, (int)Gpr.R9,
    };

    /// <summary>Registers an instruction reads without naming them, by id.</summary>
    public static IEnumerable<int> ImplicitUses(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.Cwd:
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Cmpxchg:
                yield return (int)Gpr.Rax;
                break;
            case MOp.Div:
            case MOp.Idiv:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsq:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosq:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.In:
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Out:
                yield return (int)Gpr.Rdx;
                yield return (int)Gpr.Rax;
                break;
            case MOp.RepInsw:
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.RepOutsw:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.LoadSegments:
                yield return (int)Gpr.Rax;
                break;
            case MOp.Rdmsr:
                yield return (int)Gpr.Rcx;
                break;
            case MOp.Wrmsr:
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cpuid:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.Call:
            case MOp.CallInd:
                if (i.NativeAl)
                {
                    yield return (int)Gpr.Rax;
                }
                for (int k = 0; k < i.IntArgs; k++)
                {
                    yield return (int)Selector.IntArgRegs[k];
                }
                for (int k = 0; k < i.FloatArgs; k++)
                {
                    yield return MReg.XmmBase + k;
                }
                break;
            case MOp.Syscall:
            case MOp.SoftInt:
                yield return (int)Gpr.Rax;
                for (int k = 0; k < i.IntArgs; k++)
                {
                    yield return SyscallArgs[k];
                }
                break;
            case MOp.JmpInd:
                // Unwind's exception travels in RAX to the landing pad.
                yield return (int)Gpr.Rax;
                break;
            case MOp.Epilogue:
                if (i.IntArgs > 0)
                {
                    yield return (int)Gpr.Rax;
                }
                if (i.FloatArgs > 0)
                {
                    yield return MReg.XmmBase;
                }
                break;
        }
    }

    /// <summary>Registers an instruction writes without naming them, by id.</summary>
    public static IEnumerable<int> ImplicitDefs(MInstr i)
    {
        switch (i.Op)
        {
            case MOp.RepInsw:
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.RepOutsw:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.LoadCs:
                yield return (int)Gpr.Rax;
                break;
            case MOp.Rdmsr:
            case MOp.Rdtsc:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cpuid:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rbx;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cwd:
                yield return (int)Gpr.Rdx;
                break;
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rdx;
                break;
            case MOp.Cmpxchg:
            case MOp.In:
                yield return (int)Gpr.Rax;
                break;
            case MOp.RepMovsb:
            case MOp.RepMovsq:
                yield return (int)Gpr.Rsi;
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.RepStosb:
            case MOp.RepStosq:
                yield return (int)Gpr.Rdi;
                yield return (int)Gpr.Rcx;
                break;
            case MOp.Call:
            case MOp.CallInd:
                // The callee may destroy every register the convention does
                // not make it preserve: nine general ones and all sixteen XMM.
                foreach (int r in CallerSavedGprs)
                {
                    yield return r;
                }
                for (int k = 0; k < 16; k++)
                {
                    yield return MReg.XmmBase + k;
                }
                break;
            case MOp.Syscall:
            case MOp.SoftInt:
                yield return (int)Gpr.Rax;
                yield return (int)Gpr.Rcx;
                yield return (int)Gpr.R11;
                break;
        }
    }
}

/// <summary>
/// Linear-scan register allocation over the fourteen allocatable general
/// registers and the sixteen XMM registers, the x86 allocator's method with
/// two classes: an interval is the hull of the positions its register is
/// live at, the registers the selector named are busy exactly where they
/// are live, and a register nobody can have is spilled to an eight-byte slot
/// -- read in place where the instruction takes memory, else reloaded into a
/// short interval of its own.
///
/// Positions: instruction i owns 4i .. 4i+3 -- reloads at 4i, reads at
/// 4i+1, writes at 4i+2, spill stores at 4i+3.
/// </summary>
internal sealed class Allocator
{
    private static readonly int[] IntPreference =
    {
        // Caller-saved first: free when the value does not cross a call.
        (int)Gpr.Rax, (int)Gpr.Rcx, (int)Gpr.Rdx, (int)Gpr.Rsi, (int)Gpr.Rdi,
        (int)Gpr.R8, (int)Gpr.R9, (int)Gpr.R10, (int)Gpr.R11,
        (int)Gpr.Rbx, (int)Gpr.R12, (int)Gpr.R13, (int)Gpr.R14, (int)Gpr.R15,
    };

    private static readonly int[] FloatPreference = Enumerable.Range(MReg.XmmBase, 16).ToArray();

    internal static readonly Gpr[] CalleeSaved = { Gpr.Rbx, Gpr.R12, Gpr.R13, Gpr.R14, Gpr.R15 };

    private const int Physical = MReg.FirstVirtual;

    private sealed class Interval
    {
        public int VReg;
        public int Start;
        public int End;
        public bool Short;
        public int Instr;
        public int Reg = -1;
    }

    private readonly record struct Occurrence(int Instr, int Operand, Roles.Role Role, bool InMem);

    private readonly MFunction _m;
    private readonly List<MInstr> _lin = new();
    private readonly int _n;
    private readonly List<Occurrence>[] _occ;
    private readonly int[] _start;
    private readonly int[] _end;
    private readonly int[][] _busy = new int[Physical][];
    private readonly int[] _assigned;
    private readonly int[] _spilledFrom;
    private readonly int[] _slot;
    private readonly Dictionary<(int VReg, int Instr), int> _shortReg = new();
    private readonly HashSet<(int Instr, int Operand)> _folded = new();
    private readonly MImm?[] _remat;
    private readonly List<(int Slot, int FreeFrom)> _freeSlots = new();
    private readonly Dictionary<int, List<int>> _liveAtCall = new();
    private readonly PriorityQueue<Interval, (int, int)> _unhandled = new();
    private readonly List<Interval> _active = new();
    private int _seq;

    private Allocator(MFunction m)
    {
        _m = m;
        foreach (MBlock b in m.Blocks)
        {
            _lin.AddRange(b.Instrs);
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
        for (int r = 0; r < Physical; r++)
        {
            _busy[r] = new int[_lin.Count * 4 + 1];
        }
    }

    public static void Run(MFunction m)
    {
        Allocator a = new(m);
        a.CollectOccurrences();
        a.ComputeLiveness();
        a.Scan();
        a.Rewrite();
    }

    private bool IsFloat(int vreg) => vreg < Physical ? vreg >= MReg.XmmBase : _m.FloatRegs.Contains(vreg);

    // ---- occurrences ----------------------------------------------------------

    private static IEnumerable<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> RegsOf(MInstr i)
    {
        // `xor v, v` and `xorps x, x` make zero whatever v held: a definition only.
        bool zeroing = (i.Op is MOp.Xor or MOp.XorF) && i.Operands.Count == 2
            && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r:
                    yield return (r, zeroing ? Roles.Role.Def : Roles.Of(i, k), k, false);
                    break;
                case MMem m:
                    if (m.Base is not null)
                    {
                        yield return (m.Base, Roles.Role.Use, k, true);
                    }
                    if (m.Index is not null)
                    {
                        yield return (m.Index, Roles.Role.Use, k, true);
                    }
                    break;
            }
        }
        foreach (int g in Roles.ImplicitUses(i))
        {
            yield return (new MReg(g), Roles.Role.Use, -1, false);
        }
        foreach (int g in Roles.ImplicitDefs(i))
        {
            yield return (new MReg(g), Roles.Role.Def, -1, false);
        }
    }

    private static bool Tracked(MReg r) => r.Id != (int)Gpr.Rsp && r.Id != (int)Gpr.Rbp;

    private void CollectOccurrences()
    {
        for (int i = 0; i < _lin.Count; i++)
        {
            foreach ((MReg r, Roles.Role role, int k, bool inMem) in RegsOf(_lin[i]))
            {
                if (Tracked(r))
                {
                    _occ[r.Id].Add(new Occurrence(i, k, role, inMem));
                }
            }
        }

        // A register whose one definition is a constant is made again where
        // it is wanted rather than kept in a slot.
        for (int v = Physical; v < _n; v++)
        {
            if (IsFloat(v))
            {
                continue;
            }
            Occurrence? def = null;
            int defs = 0;
            foreach (Occurrence o in _occ[v])
            {
                if ((o.Role & Roles.Role.Def) != 0)
                {
                    defs++;
                    def = o;
                }
            }
            if (defs == 1 && def is { Role: Roles.Role.Def, InMem: false, Operand: 0 } d
                && _lin[d.Instr].Op == MOp.Mov && _lin[d.Instr].Operands[1] is MImm { IsPlain: true } imm)
            {
                // The width is part of the value: a 4-byte move of -1 is 0xFFFFFFFF.
                _remat[v] = _lin[d.Instr].Width == 4 ? new MImm((uint)imm.Value) : imm;
            }
        }
    }

    // ---- liveness ----------------------------------------------------------------

    private void Mark(int reg, int pos)
    {
        if (reg < Physical)
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
                foreach ((MReg r, Roles.Role role, _, _) in RegsOf(_lin[i]))
                {
                    if (Tracked(r) && (role & Roles.Role.Use) != 0 && !def[b].Get(r.Id))
                    {
                        use[b].Set(r.Id);
                    }
                }
                foreach ((MReg r, Roles.Role role, _, _) in RegsOf(_lin[i]))
                {
                    if (Tracked(r) && (role & Roles.Role.Def) != 0)
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

        BitSet cur = new(_n);
        for (int b = 0; b < nb; b++)
        {
            cur.CopyFrom(live[b]);
            for (int i = first[b] + count[b] - 1; i >= first[b]; i--)
            {
                int p = i * 4;
                if (_lin[i].Op is MOp.Call or MOp.CallInd)
                {
                    _liveAtCall[i] = cur.Members().ToList();
                }
                foreach (int r in cur.Members())
                {
                    Mark(r, p + 2);
                    Mark(r, p + 3);
                }
                foreach ((MReg r, Roles.Role role, _, _) in RegsOf(_lin[i]))
                {
                    if (Tracked(r) && (role & Roles.Role.Def) != 0)
                    {
                        Mark(r.Id, p + 2);
                        Mark(r.Id, p + 3);
                        cur.Clear(r.Id);
                    }
                }
                foreach ((MReg r, Roles.Role role, _, _) in RegsOf(_lin[i]))
                {
                    if (Tracked(r) && (role & Roles.Role.Use) != 0)
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

        for (int r = 0; r < Physical; r++)
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

    // ---- the scan -----------------------------------------------------------------

    private void Enqueue(Interval iv) => _unhandled.Enqueue(iv, (iv.Start, _seq++));

    private void Scan()
    {
        for (int v = Physical; v < _n; v++)
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

    private int[] PreferenceFor(int vreg) => IsFloat(vreg) ? FloatPreference : IntPreference;

    private bool Allocatable(int reg, Interval iv)
        => (reg >= MReg.XmmBase) == IsFloat(iv.VReg) && reg != (int)Gpr.Rsp && reg != (int)Gpr.Rbp
           && !Busy(reg, iv.Start, iv.End);

    private int FreeRegister(Interval iv)
    {
        int hint = Hint(iv);
        if (hint >= 0 && Allocatable(hint, iv) && _active.All(a => a.Reg != hint))
        {
            return hint;
        }
        foreach (int r in PreferenceFor(iv.VReg))
        {
            if (Allocatable(r, iv) && _active.All(a => a.Reg != r))
            {
                return r;
            }
        }
        return -1;
    }

    private int Hint(Interval iv)
    {
        foreach (Occurrence o in _occ[iv.VReg])
        {
            if (iv.Short && o.Instr != iv.Instr)
            {
                continue;
            }
            MInstr i = _lin[o.Instr];
            if (i.Op is not (MOp.Mov or MOp.MovF) || o.InMem || i.Operands.Count != 2 || o.Operand < 0)
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

        if (victim is null)
        {
            // Nothing can wait: take the register of something read right
            // here whose every appearance here can be its spill slot instead.
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
            throw new InvalidOperationException($"{_m.Source.Name}: no register for v{cur.VReg} at instruction {at}");
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

    /// <summary>Whether this operand of this instruction may be a memory operand in place of a register.</summary>
    private static bool Foldable(MInstr i, int operand)
    {
        switch (i.Op)
        {
            case MOp.Mov:
                // Never a memory destination for a 64-bit immediate: there is no such form.
                return operand == 1 || i.Operands[1] is not MImm { FitsInt32: false };
            case MOp.Add:
            case MOp.Sub:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Adc:
            case MOp.Sbb:
            case MOp.Cmp:
                return true;
            case MOp.Push:
            case MOp.Test:
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
            case MOp.Neg:
            case MOp.Not:
            case MOp.Shl:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Rol:
            case MOp.Ror:
            case MOp.Setcc:
            case MOp.CallInd:
                return operand == 0;
            case MOp.Imul:
            case MOp.Imul3:
            case MOp.Movsx:
            case MOp.Movzx:
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.SqrtF:
            case MOp.UcomiF:
            case MOp.CvtIntToF:
            case MOp.CvtFToInt:
            case MOp.CvtFToF:
                return operand == 1;
            case MOp.MovF:
                return true;
            case MOp.MovGx:
                // The general register's side may be memory; the XMM side may not.
                return i.Operands[operand] is MReg r && !r.IsFloat;
            default:
                return false;
        }
    }

    private bool CanFold(int vreg, Occurrence o)
    {
        MInstr i = _lin[o.Instr];
        if (o.InMem || o.Operand < 0 || !Foldable(i, o.Operand))
        {
            return false;
        }
        // A NARROW WRITE LEAVES THE SLOT'S TOP HALF AS IT WAS, and a reload is
        // the whole eight bytes: a 32-bit value written into its slot in place
        // would come back with somebody's old upper half. A register's 32-bit
        // result has that half zero, so an integer written narrower than the
        // word is made in a register and stored whole.
        if ((o.Role & Roles.Role.Def) != 0 && i.Width < 8 && !IsFloat(vreg))
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

    private void Spill(int vreg, int from)
    {
        _spilledFrom[vreg] = from;
        bool remat = _remat[vreg] is not null;
        if (_slot[vreg] == 0 && !remat)
        {
            _slot[vreg] = TakeSlot(vreg);
        }
        int lastInstr = -1;
        Roles.Role merged = Roles.Role.None;
        foreach (Occurrence o in _occ[vreg])
        {
            if (remat && (o.Role & Roles.Role.Def) != 0)
            {
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
                merged = Roles.Role.None;
            }
            lastInstr = o.Instr;
            merged |= o.Role;
        }
        if (lastInstr >= 0)
        {
            EnqueueShort(vreg, lastInstr, merged);
        }
    }

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

    /// <summary>Whether a rematerialised constant can stand in this operand position as an immediate.</summary>
    private bool CanFoldImm(int vreg, Occurrence o)
    {
        MInstr i = _lin[o.Instr];
        if (o.InMem || o.Operand < 0)
        {
            return false;
        }
        MImm imm = _remat[vreg]!;
        // Only a constant an instruction can carry whatever its other operand
        // turns out to be: a 64-bit one folded into a move whose destination
        // is later spilled would ask for `mov m64, imm64`, which x86-64 does
        // not have. A wider constant is made in a register of its own.
        bool fits = i.Width == 4 || imm.FitsInt32;
        switch (i.Op)
        {
            case MOp.Mov:
                return o.Operand == 1 && fits;
            case MOp.Add:
            case MOp.Sub:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Adc:
            case MOp.Sbb:
            case MOp.Cmp:
                return o.Operand == 1 && fits;
            case MOp.Push:
                return imm.FitsInt32;
            default:
                return false;
        }
    }

    private void EnqueueShort(int vreg, int instr, Roles.Role role)
    {
        int p = instr * 4;
        Enqueue(new Interval
        {
            VReg = vreg,
            Short = true,
            Instr = instr,
            Start = (role & Roles.Role.Use) != 0 ? p : p + 2,
            End = (role & Roles.Role.Def) != 0 ? p + 3 : p + 1,
        });
    }

    private int RegAt(int vreg, int instr)
    {
        if (vreg < Physical)
        {
            return vreg;
        }
        if (instr < _spilledFrom[vreg])
        {
            return _assigned[vreg];
        }
        return _shortReg.TryGetValue((vreg, instr), out int r) ? r : -1;
    }

    // ---- rewriting ---------------------------------------------------------------

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
        foreach (Gpr g in CalleeSaved)
        {
            if (_m.SavesEverything || saved.Contains((int)g))
            {
                _m.SavedRegs.Add(g);
            }
        }
    }

    /// <summary>
    /// Where the words live across the call at an instruction are, once
    /// placed: the x86 rule, every live integer that is not known to be a
    /// plain number counted as a possible reference.
    /// </summary>
    private Safepoint MapOf(List<int> live, int index)
    {
        Safepoint map = new();
        foreach (int v in live)
        {
            if (v < Physical || IsFloat(v) || _m.NotReferences.Contains(v) || _remat[v] is not null)
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
            if (Array.IndexOf(CalleeSaved, (Gpr)_assigned[v]) >= 0)
            {
                map.Registers |= 1u << _assigned[v];
            }
        }
        map.SlotOffsets.Sort();
        return map;
    }

    private MReg PhysicalOf(int reg) => new(reg);

    private void RewriteInstr(MInstr i, int index, List<MInstr> outList, HashSet<int> saved)
    {
        List<MInstr> before = new();
        List<MInstr> after = new();
        HashSet<int> done = new();
        MInstr n = new(i.Op)
        {
            Width = i.Width, SourceWidth = i.SourceWidth, Cond = i.Cond, Lock = i.Lock, Table = i.Table, TableSymbol = i.TableSymbol,
            Line = i.Line, IntArgs = i.IntArgs, FloatArgs = i.FloatArgs, NativeAl = i.NativeAl,
        };

        if (_liveAtCall.TryGetValue(index, out List<int>? live))
        {
            _m.Safepoints[n] = MapOf(live, index);
        }

        Dictionary<int, Roles.Role> roles = new();
        foreach ((MReg r, Roles.Role role, _, _) in RegsOf(i))
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
                throw new InvalidOperationException($"{_m.Source.Name}: v{r.Id} has no register at instruction {index} ({i.Op})");
            }
            saved.Add(reg);
            if (_spilledFrom[r.Id] != int.MaxValue && done.Add(r.Id))
            {
                Roles.Role role = roles[r.Id];
                bool isFloat = IsFloat(r.Id);
                if (_remat[r.Id] is MImm imm)
                {
                    before.Add(new MInstr(MOp.Mov, PhysicalOf(reg), imm) { Width = 8, Line = i.Line });
                }
                else
                {
                    MOp move = isFloat ? MOp.MovF : MOp.Mov;
                    if ((role & Roles.Role.Use) != 0)
                    {
                        before.Add(new MInstr(move, PhysicalOf(reg), MMem.Spill(_slot[r.Id])) { Width = 8, Line = i.Line });
                    }
                    if ((role & Roles.Role.Def) != 0)
                    {
                        after.Add(new MInstr(move, MMem.Spill(_slot[r.Id]), PhysicalOf(reg)) { Width = 8, Line = i.Line });
                    }
                }
            }
            return PhysicalOf(reg);
        }

        // The defining move of a spilled constant is dropped: it is made where it is used.
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
                    n.Operands.Add(new MMem(m.Base is null ? null : Place(m.Base), m.Disp)
                    {
                        Index = m.Index is null ? null : Place(m.Index),
                        Scale = m.Scale,
                        Symbol = m.Symbol,
                        Got = m.Got,
                        Label = m.Label,
                        IsSpill = m.IsSpill,
                        Segment = m.Segment,
                    });
                    break;
                default:
                    n.Operands.Add(o);
                    break;
            }
        }
        foreach (int g in Roles.ImplicitDefs(i))
        {
            saved.Add(g);
        }

        outList.AddRange(before);
        // A move onto itself is what a coalesced copy becomes -- but a 4-byte
        // one clears the upper half, which is its meaning when it was written
        // as `mov r32, r32`, so only the full-width and float ones go.
        bool self = (n.Op == MOp.Mov && n.Width == 8) || n.Op == MOp.MovF;
        if (self && n.Operands.Count == 2 && n.Operands[0] is MReg a && n.Operands[1] is MReg c && a.Id == c.Id)
        {
            outList.AddRange(after);
            return;
        }
        outList.Add(n);
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
