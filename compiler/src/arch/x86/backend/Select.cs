#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Instruction selection: one IR function into machine instructions over
/// virtual registers.
///
/// The output is in a form the allocator can work with directly: every
/// two-address instruction is preceded by a move into its destination (the
/// allocator coalesces the move away when it can), fixed-register
/// instructions read and write the physical registers by name, and I64
/// values are two virtual registers from here on. Floating-point values
/// never enter a virtual register at all: each lives in a frame slot and
/// every operation is fld / op / fstp, as the design says.
/// </summary>
internal sealed partial class Selector
{
    private readonly Function _f;
    private readonly MFunction _m;
    private readonly List<string> _errors;
    private readonly Dictionary<int, MReg> _lo = new();
    private readonly Dictionary<int, MReg> _hi = new();
    private readonly Dictionary<Block, MBlock> _heads = new();
    private readonly Dictionary<int, int> _useCount = new();
    private readonly Dictionary<VReg, Instr?> _definitions = new();
    private readonly Dictionary<VReg, List<Instr>> _rangeDefinitions = new();
    private int _rangeVisits;
    private MBlock _cur = null!;
    private Block _sourceBlock = null!;
    private int _splits;
    private readonly bool _automaticPacked;

    private static readonly MReg Eax = MReg.Of(Gpr.Eax);
    private static readonly MReg Ecx = MReg.Of(Gpr.Ecx);
    private static readonly MReg Edx = MReg.Of(Gpr.Edx);
    private static readonly MReg Ebx = MReg.Of(Gpr.Ebx);
    private static readonly MReg Esi = MReg.Of(Gpr.Esi);
    private static readonly MReg Edi = MReg.Of(Gpr.Edi);
    private static readonly MReg Esp = MReg.Of(Gpr.Esp);
    private static readonly MReg Ebp = MReg.Of(Gpr.Ebp);

    private Selector(Function f, List<string> errors, bool automaticPacked)
    {
        _f = f;
        _m = new MFunction(f);
        _errors = errors;
        _automaticPacked = automaticPacked;
        foreach (VReg parameter in f.Params) _definitions[parameter] = null;
        foreach (Instr instruction in f.Blocks.SelectMany(b => b.Instrs))
            if (instruction.Dest is { } dest)
            {
                _definitions[dest] = _definitions.ContainsKey(dest) ? null : instruction;
                if (!_rangeDefinitions.TryGetValue(dest, out List<Instr>? definitions))
                    _rangeDefinitions[dest] = definitions = new();
                definitions.Add(instruction);
            }
    }

    public static MFunction Run(Function f, List<string> errors, bool automaticPacked = true)
    {
        Selector s = new(f, errors, automaticPacked);
        s.Select();
        return s._m;
    }

    private void Error(string what) => _errors.Add($"{_f.Name}: {what}");

    // ---- emission ----------------------------------------------------------

    /// <summary>The source line of the IR instruction being selected, stamped on everything it becomes.</summary>
    private int _line;

    private MInstr Emit(MInstr i)
    {
        i.Line = _line;
        _cur.Instrs.Add(i);
        return i;
    }

    private MInstr Emit(MOp op, params MOperand[] ops) => Emit(new MInstr(op, ops));

    private MInstr EmitW(MOp op, int width, params MOperand[] ops) => Emit(new MInstr(op, ops) { Width = width });

    private void Jcc(Cond c, MBlock target) => Emit(new MInstr(MOp.Jcc, new MLabel(target)) { Cond = c });

    private void Jmp(MBlock target) => Emit(MOp.Jmp, new MLabel(target));

    /// <summary>Start a fresh block right after the current one and continue there.</summary>
    private MBlock Split()
    {
        MBlock b = _m.NewBlock($"{_cur.Name}.{_splits++}", _cur);
        _cur = b;
        return b;
    }

    /// <summary>A block placed after the current one, for code the current block jumps around.</summary>
    private MBlock Aside() => _m.NewBlock($"{_cur.Name}.{_splits++}", _cur);

    private MReg Temp() => _m.NewReg();

    private static MImm Imm(long v) => new(v);

    private void Mov(MOperand dst, MOperand src) => Emit(MOp.Mov, dst, src);

    // ---- symbol references ------------------------------------------------
    //
    // Every reference to a named thing passes through here, so a
    // position-independent mode can later swap the absolute forms for
    // GOT-relative ones without touching the selection of any opcode.

    /// <summary>The address of a symbol as an immediate: an Abs32 relocation.</summary>
    private static MImm SymbolAddress(string name, long addend) => MImm.Sym(name, addend);

    /// <summary>A memory operand at a symbol plus a displacement.</summary>
    private static MMem SymbolMem(string name, long disp) => MMem.Abs(name, checked((int)disp));

    private void CallSymbol(string name) => Emit(new MInstr(MOp.Call, SymbolAddress(name, 0)) { CallReloc = RelocKind.Rel32 });

    // ---- operands ----------------------------------------------------------

    private MReg Lo(VReg v)
    {
        if (!_lo.TryGetValue(v.Id, out MReg? r))
        {
            r = _m.NewReg();
            _lo[v.Id] = r;
            // Both halves of a 64-bit integer are integers: telling the
            // stack maps so is the one bit of precision available while a
            // reference and an int are both an I32 in the IR.
            if (v.Type == IrType.I64)
            {
                _m.WideHalves.Add(r.Id);
            }
        }
        return r;
    }

    private MReg Hi(VReg v)
    {
        if (!_hi.TryGetValue(v.Id, out MReg? r))
        {
            r = _m.NewReg();
            _hi[v.Id] = r;
            _m.WideHalves.Add(r.Id);
        }
        return r;
    }

    /// <summary>An I32 operand in a register, loading a constant or address if it is one.</summary>
    private MReg R(Operand o)
    {
        switch (o)
        {
            case RegOperand r:
                return Lo(r.Reg);
            case SlotOperand s:
            {
                MReg t = Temp();
                Emit(MOp.Lea, t, MMem.Frame(_m.Frame.SlotOffset(s.Slot)));
                return t;
            }
            default:
            {
                MReg t = Temp();
                Mov(t, RM(o));
                return t;
            }
        }
    }

    /// <summary>An I32 operand as a register or an immediate: the source side of an ALU instruction.</summary>
    private MOperand RM(Operand o) => o switch
    {
        RegOperand r => Lo(r.Reg),
        ImmOperand i => Imm((int)i.Value),
        SymOperand s => SymbolAddress(s.Name, s.Offset),
        SlotOperand => R(o),
        _ => throw new InvalidOperationException(o.GetType().Name),
    };

    /// <summary>The two halves of an I64 operand as ALU sources.</summary>
    private (MOperand Lo, MOperand Hi) PairRM(Operand o) => o switch
    {
        RegOperand r => (Lo(r.Reg), ZeroHigh(o) ? Imm(0) : Hi(r.Reg)),
        ImmOperand i => (Imm((int)i.Value), Imm((int)(i.Value >> 32))),
        _ => throw new InvalidOperationException($"{o} is not an I64"),
    };

    private (MReg Lo, MReg Hi) Pair(Operand o)
    {
        if (o is RegOperand r)
        {
            return (Lo(r.Reg), Hi(r.Reg));
        }
        (MOperand lo, MOperand hi) = PairRM(o);
        MReg tl = Temp();
        MReg th = Temp();
        Mov(tl, lo);
        Mov(th, hi);
        return (tl, th);
    }

    private static int FWidth(IrType t) => t == IrType.F32 ? 4 : 8;

    /// <summary>The home slot of a floating-point virtual register.</summary>
    private MMem FHome(VReg v) => MMem.Frame(_m.Frame.FloatHome(v));

    private MMem FHome(Operand o) => o is RegOperand r ? FHome(r.Reg) : throw new InvalidOperationException($"{o} is not a float register");

    private void Fld(Operand o) => EmitW(MOp.Fld, FWidth(o.Type), FHome(o));

    private void Fstp(VReg d) => EmitW(MOp.Fstp, FWidth(d.Type), FHome(d));

    private static MMem Displaced(MMem source, int delta) => new(source.Base, source.Disp + delta)
    { Index = source.Index, Scale = source.Scale, Symbol = source.Symbol, Reloc = source.Reloc, Label = source.Label, IsSpill = source.IsSpill };

    // Copy bits, not numbers: this also preserves signaling-NaN payloads and
    // avoids touching x87 state for ordinary same-width floating-point moves.
    private void CopyFloatBits(MMem destination, MMem source, int bytes)
    {
        MReg low = Temp(); Mov(low, source);
        if (bytes == 4) { Mov(destination, low); return; }
        MReg high = Temp(); Mov(high, Displaced(source, 4));
        Mov(destination, low); Mov(Displaced(destination, 4), high);
    }

    /// <summary>The memory an address operand plus displacement names.</summary>
    private MMem Address(Operand addr, long offset)
    {
        int disp = checked((int)offset);
        switch (addr)
        {
            case RegOperand r when r.Type == IrType.I32:
                return new MMem(Lo(r.Reg), disp);
            case SymOperand s:
                return SymbolMem(s.Name, s.Offset + disp);
            case SlotOperand s:
                return MMem.Frame(_m.Frame.SlotOffset(s.Slot) + disp);
            case ImmOperand i:
                return new MMem(null, checked((int)i.Value + disp));
            default:
                Error($"cannot address through {addr} of type {addr.Type}");
                return new MMem(null, 0);
        }
    }

    private static bool Aliases(VReg? dest, Operand o) => dest is not null && o is RegOperand r && r.Reg == dest;

    // ---- the walk -----------------------------------------------------------

    private void Select()
    {
        foreach (Block b in _f.Blocks)
        {
            _heads[b] = _m.NewBlock(b.Label, null, b);
        }
        foreach (Block b in _f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                foreach (Operand o in i.Operands)
                {
                    if (o is RegOperand r)
                    {
                        _useCount[r.Reg.Id] = _useCount.GetValueOrDefault(r.Reg.Id) + 1;
                    }
                }
            }
        }

        _cur = _heads[_f.Entry];
        Emit(MOp.Prologue);
        LoadParams();

        foreach (Block b in _f.Blocks)
        {
            _cur = _heads[b];
            _sourceBlock = b;
            _constants.Clear();
            for (int n = 0; n < b.Instrs.Count; n++)
            {
                Instr i = b.Instrs[n];
                _line = i.Line;
                NoteConstant(i);
                if (TryPackedArithmetic(b.Instrs, n, out int consumed) || TryPackedRounding(b.Instrs, n, out consumed)
                    || TryPackedShifts(b.Instrs, n, out consumed)
                    || TryPackedConversions(b.Instrs, n, out consumed)
                    || TryPackedFloatArithmetic(b.Instrs, n, out consumed)
                    || TryPackedDotProducts(b.Instrs, n, out consumed)
                    || TryPackedComparisons(b.Instrs, n, out consumed)
                    || TryRotate(b.Instrs, n, out consumed))
                {
                    n += consumed - 1;
                    continue;
                }
                if (IsCompare(i.Op) && i.Dest is not null && n + 1 < b.Instrs.Count
                    && FusesInto(i, b.Instrs[n + 1]))
                {
                    SelectFusedBranch(i, b.Instrs[n + 1]);
                    n++;
                    continue;
                }
                SelectInstr(i);
            }
            if (b.Terminator is null)
            {
                Error($"block {b.Label} has no terminator");
            }
        }
    }

    /// <summary>
    /// Incoming arguments sit above the return address; the integer ones are
    /// loaded into their virtual registers here and the float ones simply
    /// have their home declared to be the argument slot.
    /// </summary>
    private void LoadParams()
    {
        int off = 8;
        foreach (VReg p in _f.Params)
        {
            switch (p.Type)
            {
                case IrType.I32:
                    Mov(Lo(p), MMem.Frame(off));
                    off += 4;
                    break;
                case IrType.I64:
                    Mov(Lo(p), MMem.Frame(off));
                    Mov(Hi(p), MMem.Frame(off + 4));
                    off += 8;
                    break;
                case IrType.F32:
                    _m.Frame.PlaceFloatParam(p, off);
                    off += 4;
                    break;
                case IrType.F64:
                    _m.Frame.PlaceFloatParam(p, off);
                    off += 8;
                    break;
                default:
                    Error($"parameter {p} has type {p.Type}");
                    break;
            }
        }
    }

    /// <summary>
    /// Registers this block has so far set to an integer constant and not
    /// redefined, so a branch on one of them can be resolved here rather
    /// than testing a register everyone can see is zero.
    /// </summary>
    private readonly Dictionary<int, long> _constants = new();

    private void NoteConstant(Instr i)
    {
        if (i.Dest is null)
        {
            return;
        }
        if (i.Op == Opcode.Copy && i.Operands[0] is ImmOperand imm && i.Dest.Type == IrType.I32)
        {
            _constants[i.Dest.Id] = imm.Value;
        }
        else
        {
            _constants.Remove(i.Dest.Id);
        }
    }

    private static bool IsCompare(Opcode op) => op is >= Opcode.Eq and <= Opcode.GeU or >= Opcode.FEq and <= Opcode.FGe;

    /// <summary>
    /// A compare whose only consumer is the branch right after it need not
    /// produce a 0/1: the flags go straight into the jcc.
    /// </summary>
    private bool FusesInto(Instr cmp, Instr next)
        => next.Op == Opcode.Branch && next.Operands[0] is RegOperand r && r.Reg == cmp.Dest
           && _useCount.GetValueOrDefault(r.Reg.Id) == 1;

    private bool TryRotate(List<Instr> instructions, int at, out int consumed)
    {
        consumed = 0;
        bool SingleUse(Instr i) => i.Dest is { } d && _useCount.GetValueOrDefault(d.Id) == 1
            && _definitions.TryGetValue(d, out Instr? definition) && ReferenceEquals(definition, i);
        bool Input(Instr i, VReg? r) => i.Operands.Count == 2 && i.Operands[0] is RegOperand operand && operand.Reg == r;
        bool Mask(Instr i) => i.Op == Opcode.And && i.Dest?.Type == IrType.I64
            && i.Operands.Count == 2 && i.Operands[1] is ImmOperand { Value: 4294967295 };
        bool Combine(Instr i, VReg? a, VReg? b) => i.Op == Opcode.Or && i.Operands.Count == 2
            && i.Operands[0] is RegOperand x && i.Operands[1] is RegOperand y
            && ((x.Reg == a && y.Reg == b) || (x.Reg == b && y.Reg == a));
        bool IsRight(Instr i, bool wide) => i.Op == Opcode.ShrU || (wide && i.Op == Opcode.ShrS);
        bool ShiftPair(Instr a, Instr b, bool wide) =>
            (a.Op == Opcode.Shl && IsRight(b, wide)) || (IsRight(a, wide) && b.Op == Opcode.Shl);

        VReg? source = null;
        Instr right, left, result;
        if (at + 5 < instructions.Count && Mask(instructions[at]) && Mask(instructions[at + 2])
            && instructions[at].Operands[0] is RegOperand first
            && instructions[at + 2].Operands[0] is RegOperand second && first.Reg == second.Reg
            && first.Type == IrType.I64)
        {
            right = instructions[at + 1]; left = instructions[at + 3]; result = instructions[at + 5];
            if (right.Op == Opcode.Shl) (right, left) = (left, right);
            if (right.Op is not (Opcode.ShrS or Opcode.ShrU) || left.Op != Opcode.Shl
                || !Input(instructions[at + 1], instructions[at].Dest)
                || !Input(instructions[at + 3], instructions[at + 2].Dest)
                || !Combine(instructions[at + 4], right.Dest, left.Dest)
                || !Mask(result) || !Input(result, instructions[at + 4].Dest)) return false;
            source = first.Reg;
            consumed = 6;
        }
        else if (at + 3 < instructions.Count && ShiftPair(instructions[at], instructions[at + 1], true)
            && instructions[at].Dest?.Type == IrType.I64
            && instructions[at].Operands[0] is RegOperand shared && ZeroHigh(shared)
            && Input(instructions[at + 1], shared.Reg)
            && Combine(instructions[at + 2], instructions[at].Dest, instructions[at + 1].Dest)
            && Mask(instructions[at + 3]) && Input(instructions[at + 3], instructions[at + 2].Dest))
        {
            right = instructions[at]; left = instructions[at + 1]; result = instructions[at + 3];
            if (right.Op == Opcode.Shl) (right, left) = (left, right);
            source = shared.Reg;
            consumed = 4;
        }
        else if (at + 2 < instructions.Count && ShiftPair(instructions[at], instructions[at + 1], false)
            && instructions[at].Dest?.Type == IrType.I32
            && instructions[at].Operands[0] is RegOperand first32
            && Input(instructions[at + 1], first32.Reg)
            && Combine(instructions[at + 2], instructions[at].Dest, instructions[at + 1].Dest))
        {
            right = instructions[at]; left = instructions[at + 1]; result = instructions[at + 2];
            if (right.Op == Opcode.Shl) (right, left) = (left, right);
            source = first32.Reg;
            consumed = 3;
        }
        else return false;
        if (right.Operands[1] is not ImmOperand rightCount || left.Operands[1] is not ImmOperand leftCount
            || rightCount.Value < 1 || rightCount.Value > 31 || leftCount.Value != 32 - rightCount.Value)
            return false;
        for (int n = at; n < at + consumed - 1; n++)
            if (!SingleUse(instructions[n]) || instructions[n].Dest == source
                || instructions[n].Dest?.Type != result.Dest?.Type) return false;
        if (source.Type != result.Dest?.Type) return false;
        NoteConstant(result);
        _line = result.Line;
        Mov(Lo(result.Dest!), Lo(source));
        Emit(MOp.Ror, Lo(result.Dest!), Imm(rightCount.Value));
        if (result.Dest!.Type == IrType.I64) Mov(Hi(result.Dest), Imm(0));
        return true;
    }

    private void SelectInstr(Instr i)
    {
        switch (i.Op)
        {
            case Opcode.Copy:
                SelectCopy(i);
                break;
            case Opcode.Add:
            case Opcode.Sub:
            case Opcode.And:
            case Opcode.Or:
            case Opcode.Xor:
                SelectAlu(i);
                break;
            case Opcode.Mul:
                SelectMul(i);
                break;
            case Opcode.DivS:
            case Opcode.DivU:
            case Opcode.RemS:
            case Opcode.RemU:
                SelectDiv(i);
                break;
            case Opcode.Shl:
            case Opcode.ShrS:
            case Opcode.ShrU:
                SelectShift(i);
                break;
            case Opcode.Neg:
            case Opcode.Not:
                SelectUnary(i);
                break;
            case Opcode.ByteSwap:
            {
                VReg d = i.Dest!;
                if (d.Type == IrType.I32)
                {
                    Mov(Lo(d), RM(i.Operands[0])); ByteSwapWord(Lo(d));
                }
                else
                {
                    // Snapshot both halves before assigning either one:
                    // reversing an I64 also exchanges its low/high words.
                    var source = PairRM(i.Operands[0]);
                    MReg low = Temp(), high = Temp();
                    Mov(low, source.Hi); Mov(high, source.Lo);
                    ByteSwapWord(low); ByteSwapWord(high);
                    Mov(Lo(d), low); Mov(Hi(d), high);
                }
                break;
            }
            case Opcode.Eq:
            case Opcode.Ne:
            case Opcode.LtS:
            case Opcode.LeS:
            case Opcode.GtS:
            case Opcode.GeS:
            case Opcode.LtU:
            case Opcode.LeU:
            case Opcode.GtU:
            case Opcode.GeU:
                SelectCompare(i);
                break;
            case Opcode.FAdd:
            case Opcode.FSub:
            case Opcode.FMul:
            case Opcode.FDiv:
                SelectFloatAlu(i);
                break;
            case Opcode.FNeg:
                Fld(i.Operands[0]);
                Emit(MOp.Fchs);
                Fstp(i.Dest!);
                break;
            case Opcode.FSqrt:
                Fld(i.Operands[0]);
                Emit(MOp.Fsqrt);
                Fstp(i.Dest!);
                break;
            case Opcode.FEq:
            case Opcode.FNe:
            case Opcode.FLt:
            case Opcode.FLe:
            case Opcode.FGt:
            case Opcode.FGe:
                SelectFloatCompare(i);
                break;
            case Opcode.SExt8:
            case Opcode.SExt16:
            case Opcode.ZExt8:
            case Opcode.ZExt16:
                SelectNarrow(i);
                break;
            case Opcode.Trunc64:
                Mov(Lo(i.Dest!), PairRM(i.Operands[0]).Lo);
                break;
            case Opcode.SExt32:
            {
                Mov(Lo(i.Dest!), RM(i.Operands[0]));
                Mov(Hi(i.Dest!), Lo(i.Dest!));
                Emit(MOp.Sar, Hi(i.Dest!), Imm(31));
                break;
            }
            case Opcode.ZExt32:
                // A move of zero rather than xor: the allocator then knows the
                // half is a constant it can remake instead of spilling, and
                // the peephole turns what survives into xor anyway.
                Mov(Lo(i.Dest!), RM(i.Operands[0]));
                Mov(Hi(i.Dest!), Imm(0));
                break;
            case Opcode.FConv:
                Fld(i.Operands[0]);
                Fstp(i.Dest!);
                break;
            case Opcode.IToF:
            case Opcode.UToF:
                SelectIntToFloat(i);
                break;
            case Opcode.FToI:
            case Opcode.FToU:
                SelectFloatToInt(i);
                break;
            case Opcode.Bits:
                SelectBits(i);
                break;
            case Opcode.Load:
                SelectLoad(i);
                break;
            case Opcode.ArrayLength:
                Mov(Lo(i.Dest!), Address(i.Operands[0], Target.Current.ArrayCountOffset));
                break;
            case Opcode.InitArrayLength:
                Mov(Address(i.Operands[0], Target.Current.ArrayCountOffset), RM(i.Operands[1]));
                break;
            case Opcode.Store:
                SelectStore(i);
                break;
            case Opcode.MemCopy:
                SelectMemCopy(i);
                break;
            case Opcode.MemSet:
                if (SelectFrameSet(i)) break;
                Mov(Edi, RM(i.Operands[0]));
                Mov(Eax, RM(i.Operands[1]));
                Mov(Ecx, RM(i.Operands[2]));
                Emit(MOp.RepStosb);
                break;
            case Opcode.AtomicSwap:
            case Opcode.AtomicAdd:
            case Opcode.AtomicAnd:
            case Opcode.AtomicOr:
            case Opcode.AtomicXor:
            case Opcode.AtomicCas:
                SelectAtomic(i);
                break;
            case Opcode.Fence:
                Emit(MOp.LockOrEsp);
                break;
            case Opcode.Call:
            case Opcode.CallIndirect:
                SelectCall(i);
                break;
            case Opcode.Ret:
                SelectRet(i);
                break;
            case Opcode.Jump:
                Jmp(_heads[i.Targets[0]]);
                break;
            case Opcode.Branch:
                SelectBranch(i);
                break;
            case Opcode.Switch:
                SelectSwitch(i);
                break;
            case Opcode.Unreachable:
                // No ud2 on a 486; a breakpoint trap is the loudest thing it has.
                Emit(MOp.Int3);
                break;
            case Opcode.Unwind:
                SelectUnwind(i);
                break;
            case Opcode.LabelAddr:
                Mov(Lo(i.Dest!), MImm.Of(_heads[i.Targets[0]]));
                break;
            case Opcode.StackPointer:
                Mov(Lo(i.Dest!), Esp);
                break;
            case Opcode.FramePointer:
                Mov(Lo(i.Dest!), Ebp);
                break;
            case Opcode.Syscall:
                SelectSyscall(i);
                break;
            case Opcode.Trap:
                Emit(MOp.Int3);
                break;
            case Opcode.Pause:
                Emit(MOp.Pause);
                break;
            default:
                Error($"unsupported opcode {i.Op}");
                break;
        }
    }

    // ---- moves --------------------------------------------------------------

    private void SelectCopy(Instr i)
    {
        VReg d = i.Dest!;
        Operand src = i.Operands[0];
        switch (d.Type)
        {
            case IrType.I32:
                if (src is SlotOperand s)
                {
                    Emit(MOp.Lea, Lo(d), MMem.Frame(_m.Frame.SlotOffset(s.Slot)));
                }
                else
                {
                    Mov(Lo(d), RM(src));
                }
                break;
            case IrType.I64:
            {
                (MOperand lo, MOperand hi) = PairRM(src);
                Mov(Lo(d), lo);
                Mov(Hi(d), hi);
                break;
            }
            default:
                if (src is RegOperand r && r.Reg == d)
                {
                    break;
                }
                CopyFloatBits(FHome(d), FHome(src), FWidth(d.Type));
                break;
        }
    }

    // ---- integer arithmetic ---------------------------------------------------

    private static MOp AluOp(Opcode op) => op switch
    {
        Opcode.Add => MOp.Add,
        Opcode.Sub => MOp.Sub,
        Opcode.And => MOp.And,
        Opcode.Or => MOp.Or,
        _ => MOp.Xor,
    };

    /// <summary>
    /// The destination registers for a result, or fresh temporaries when the
    /// IR reuses a source register as the destination in a way the
    /// two-address form would clobber. Returns whether temporaries were used;
    /// the caller then moves them into place.
    /// </summary>
    private (MReg Lo, MReg Hi, bool Temp) DestPair(Instr i, int firstSource = 1)
    {
        VReg d = i.Dest!;
        bool clobbers = false;
        for (int k = firstSource; k < i.Operands.Count; k++)
        {
            clobbers |= Aliases(d, i.Operands[k]);
        }
        if (clobbers)
        {
            return (Temp(), d.Type == IrType.I64 ? Temp() : Lo(d), true);
        }
        return (Lo(d), d.Type == IrType.I64 ? Hi(d) : Lo(d), false);
    }

    private void Commit(Instr i, (MReg Lo, MReg Hi, bool Temp) dest)
    {
        if (!dest.Temp)
        {
            return;
        }
        Mov(Lo(i.Dest!), dest.Lo);
        if (i.Dest!.Type == IrType.I64)
        {
            Mov(Hi(i.Dest!), dest.Hi);
        }
    }

    private void SelectAlu(Instr i)
    {
        var dest = DestPair(i);
        MOp op = AluOp(i.Op);
        if (i.Op == Opcode.Add && i.Dest!.Type == IrType.I64)
        {
            _rangeVisits = 0;
            int leftBits = UnsignedBits(i.Operands[0]), rightBits = UnsignedBits(i.Operands[1]);
            if (Math.Max(leftBits, rightBits) + 1 <= 32)
            {
                // Both unsigned inputs and their complete sum fit one word.
                // Merely knowing each input fits is insufficient: that case
                // can still carry and must keep the ordinary ADD/ADC pair.
                Mov(dest.Lo, PairRM(i.Operands[0]).Lo);
                Emit(MOp.Add, dest.Lo, PairRM(i.Operands[1]).Lo);
                Mov(dest.Hi, Imm(0));
                Commit(i, dest);
                return;
            }
        }
        if (i.Op == Opcode.And && i.Dest!.Type.IsInt())
        {
            Operand value = i.Operands[0], maskOperand = i.Operands[1];
            if (value is ImmOperand) (value, maskOperand) = (maskOperand, value);
            if (maskOperand is ImmOperand mask)
            {
                _rangeVisits = 0;
                int bits = Math.Min(i.Dest.Type.Bytes() * 8, UnsignedBits(value));
                ulong possible = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
                if ((unchecked((ulong)mask.Value) & possible) == possible)
                {
                    if (i.Dest.Type == IrType.I32) Mov(dest.Lo, RM(value));
                    else
                    {
                        var pair = PairRM(value);
                        Mov(dest.Lo, pair.Lo); Mov(dest.Hi, pair.Hi);
                    }
                    Commit(i, dest);
                    return;
                }
            }
        }
        if (i.Dest!.Type == IrType.I32)
        {
            BitOrAlu(op, dest.Lo, RM(i.Operands[0]), RM(i.Operands[1]));
        }
        else
        {
            (MOperand alo, MOperand ahi) = PairRM(i.Operands[0]);
            (MOperand blo, MOperand bhi) = PairRM(i.Operands[1]);
            if (op is MOp.And or MOp.Or or MOp.Xor)
            {
                BitOrAlu(op, dest.Lo, alo, blo);
                BitOrAlu(op, dest.Hi, ahi, bhi);
            }
            else
            {
                Mov(dest.Lo, alo);
                Mov(dest.Hi, ahi);
                Emit(op, dest.Lo, blo);
                MOp hiOp = op == MOp.Add ? MOp.Adc : op == MOp.Sub ? MOp.Sbb : op;
                Emit(hiOp, dest.Hi, bhi);
            }
        }
        Commit(i, dest);
    }

    // IR bitwise operations do not expose flags. In particular, an I64 mask
    // may have an identity/absorbing half even when the whole mask does not.
    // Do not apply these rules to add/sub: the high half consumes carry.
    private void BitOrAlu(MOp op, MReg dest, MOperand left, MOperand right)
    {
        if (op is MOp.And or MOp.Or or MOp.Xor)
        {
            if (left is MImm { IsPlain: true } && right is not MImm) (left, right) = (right, left);
            if (right is MImm { IsPlain: true } imm)
            {
                int mask = unchecked((int)imm.Value);
                if ((op == MOp.And && mask == 0) || (op == MOp.Or && mask == -1))
                { Mov(dest, Imm(mask)); return; }
                if ((op == MOp.And && mask == -1) || (op is MOp.Or or MOp.Xor && mask == 0))
                { Mov(dest, left); return; }
                if (op == MOp.Xor && mask == -1)
                { Mov(dest, left); Emit(MOp.Not, dest); return; }
            }
        }
        Mov(dest, left);
        Emit(op, dest, right);
    }

    // Structural facts only: no assumption about mutable memory contents or
    // signed ranges. A multiply-defined register (including a parameter
    // assigned again) is unknown. Depth bounds make cycles conservative.
    private bool ZeroHigh(Operand operand, int depth = 0)
    {
        if (operand is ImmOperand immediate) return immediate.Value >= 0 && immediate.Value <= uint.MaxValue;
        if (depth == 0 && operand is RegOperand candidate
            && _definitions.TryGetValue(candidate.Reg, out Instr? unique) && unique is null)
        {
            _rangeVisits = 0;
            return UnsignedBits(operand) <= 32;
        }
        if (depth >= 8 || operand is not RegOperand register
            || !_definitions.TryGetValue(register.Reg, out Instr? def) || def is null) return false;
        if (def.Op == Opcode.ZExt32) return true;
        if (def.Op == Opcode.Load && !def.Signed && def.Size <= 4) return true;
        if (def.Op is Opcode.Shl or Opcode.Add or Opcode.ShrS)
        {
            _rangeVisits = 0;
            return UnsignedBits(operand) <= 32;
        }
        if (def.Op == Opcode.And && def.Operands.Count == 2)
            return ZeroHigh(def.Operands[0], depth + 1) || ZeroHigh(def.Operands[1], depth + 1);
        if (def.Op is Opcode.Or or Opcode.Xor && def.Operands.Count == 2)
            return ZeroHigh(def.Operands[0], depth + 1) && ZeroHigh(def.Operands[1], depth + 1);
        return def.Op == Opcode.ShrU && def.Dest?.Type == IrType.I64
            && def.Operands.Count == 2 && def.Operands[1] is ImmOperand shift && (shift.Value & 63) >= 32;
    }

    // Conservative unsigned upper bound, expressed as occupied bits. Unknown
    // values use 64. Every definition contributes to a register's bound;
    // parameters include an unknown entry value even when assigned later.
    private int UnsignedBits(Operand operand, int depth = 0)
    {
        if (operand is ImmOperand imm)
        {
            if (imm.Value < 0) return 64;
            ulong value = (ulong)imm.Value;
            int bits = 0;
            while (value != 0) { bits++; value >>= 1; }
            return bits;
        }
        if (depth >= 8 || ++_rangeVisits > 256 || operand is not RegOperand r
            || _f.Params.Contains(r.Reg) || !_rangeDefinitions.TryGetValue(r.Reg, out List<Instr>? definitions)) return 64;
        int bound = 0;
        foreach (Instr definition in definitions)
        {
            bound = Math.Max(bound, DefinitionBits(definition, depth));
            if (bound == 64) break;
        }
        return bound;
    }

    private int DefinitionBits(Instr def, int depth)
    {
        if (def.Op == Opcode.Load && !def.Signed && def.Size is 1 or 2 or 4 or 8)
            return def.Size * 8;
        if (def.Op == Opcode.ZExt8) return 8;
        if (def.Op == Opcode.ZExt16) return 16;
        if (def.Op == Opcode.ZExt32) return Math.Min(32, UnsignedBits(def.Operands[0], depth + 1));
        if (def.Op == Opcode.Copy && def.Operands[0].Type == def.Dest?.Type)
            return UnsignedBits(def.Operands[0], depth + 1);
        if (def.Operands.Count != 2 || def.Dest?.Type != IrType.I64) return 64;
        int left = UnsignedBits(def.Operands[0], depth + 1);
        if ((def.Op == Opcode.ShrU || (def.Op == Opcode.ShrS && left <= 63))
            && def.Operands[1] is ImmOperand shift)
            return Math.Max(0, left - (int)(shift.Value & 63));
        if (def.Op == Opcode.Shl && def.Operands[1] is ImmOperand leftShift)
            return left == 0 ? 0 : Math.Min(64, left + (int)(leftShift.Value & 63));
        int right = UnsignedBits(def.Operands[1], depth + 1);
        return def.Op switch
        {
            Opcode.And => Math.Min(left, right),
            Opcode.Or or Opcode.Xor => Math.Max(left, right),
            Opcode.Add => Math.Min(64, Math.Max(left, right) + 1),
            Opcode.Mul => left == 0 || right == 0 ? 0 : Math.Min(64, left + right),
            _ => 64,
        };
    }

    private void SelectMul(Instr i)
    {
        var dest = DestPair(i);
        if (i.Dest!.Type == IrType.I32)
        {
            if (i.Operands[1] is ImmOperand imm)
            {
                Emit(MOp.Imul3, dest.Lo, R(i.Operands[0]), Imm((int)imm.Value));
            }
            else
            {
                Mov(dest.Lo, RM(i.Operands[0]));
                Emit(MOp.Imul, dest.Lo, RM(i.Operands[1]));
            }
            Commit(i, dest);
            return;
        }

        Operand left = i.Operands[0], right = i.Operands[1];
        if (left is ImmOperand && right is not ImmOperand) (left, right) = (right, left);
        _rangeVisits = 0;
        if (UnsignedBits(left) + UnsignedBits(right) <= 32)
        {
            // The entire product, not merely each input, fits unsigned I32.
            // IMUL's low word is the same unsigned product; no widening MUL,
            // EDX:EAX clobbers or cross products are needed.
            MReg product = Temp();
            if (right is ImmOperand factor)
                Emit(MOp.Imul3, product, Pair(left).Lo, Imm(unchecked((int)factor.Value)));
            else
            {
                Mov(product, PairRM(left).Lo);
                Emit(MOp.Imul, product, PairRM(right).Lo);
            }
            Mov(dest.Lo, product); Mov(dest.Hi, Imm(0));
            Commit(i, dest);
            return;
        }
        if (right is ImmOperand constant)
        {
            (MOperand low, MOperand high) = PairRM(left);
            uint bottom = unchecked((uint)constant.Value);
            int top = unchecked((int)(constant.Value >> 32));
            if (bottom == 0)
            {
                // The only surviving product is low(left)*high(constant).
                Mov(dest.Hi, low);
                Emit(MOp.Imul3, dest.Hi, dest.Hi, Imm(top));
                Mov(dest.Lo, Imm(0));
                Commit(i, dest);
                return;
            }
            if (top == 0)
            {
                MReg upper = Temp(), multiplier = Temp();
                bool narrow = ZeroHigh(left);
                if (!narrow) { Mov(upper, high); Emit(MOp.Imul3, upper, upper, Imm(unchecked((int)bottom))); }
                Mov(multiplier, Imm(unchecked((int)bottom)));
                Mov(Eax, low); Emit(MOp.Mul, multiplier);
                Mov(dest.Lo, Eax); Mov(dest.Hi, Edx);
                if (!narrow) Emit(MOp.Add, dest.Hi, upper);
                Commit(i, dest);
                return;
            }
        }

        // (ah:al) * (bh:bl) mod 2^64 = al*bl + ((ah*bl + al*bh) << 32).
        // One widening mul for the low product and two truncating imuls for
        // the cross terms; the ah*bh term never reaches the low 64 bits.
        (MReg al, MReg ah) = Pair(i.Operands[0]);
        (MReg bl, MReg bh) = Pair(i.Operands[1]);
        bool aNarrow = ZeroHigh(i.Operands[0]), bNarrow = ZeroHigh(i.Operands[1]);
        MReg cross = Temp();
        if (!aNarrow) { Mov(cross, ah); Emit(MOp.Imul, cross, bl); }
        if (!bNarrow)
        {
            MReg cross2 = aNarrow ? cross : Temp();
            Mov(cross2, al); Emit(MOp.Imul, cross2, bh);
            if (!aNarrow) Emit(MOp.Add, cross, cross2);
        }
        Mov(Eax, al);
        Emit(MOp.Mul, bl);
        Mov(dest.Lo, Eax);
        Mov(dest.Hi, Edx);
        if (!aNarrow || !bNarrow) Emit(MOp.Add, dest.Hi, cross);
        Commit(i, dest);
    }

    private void SelectDiv(Instr i)
    {
        bool signed = i.Op is Opcode.DivS or Opcode.RemS;
        bool rem = i.Op is Opcode.RemS or Opcode.RemU;
        if (signed && i.Dest!.Type == IrType.I32 && i.Operands[1] is ImmOperand signedDivisor
            && signedDivisor.Value is 3 or 5 or 10)
        {
            // Signed high product followed by a sign correction truncates
            // toward zero, including int.MinValue, without abs overflow.
            int shift = signedDivisor.Value == 3 ? 0 : signedDivisor.Value == 5 ? 1 : 2;
            int magic = signedDivisor.Value == 3 ? 0x55555556 : 0x66666667;
            MReg numerator = Temp(), multiplier = Temp(), quotient = Temp(), sign = Temp();
            Mov(numerator, RM(i.Operands[0]));
            Mov(sign, numerator); Emit(MOp.Sar, sign, Imm(31));
            Mov(multiplier, Imm(magic)); Mov(Eax, numerator);
            Emit(MOp.ImulWide, multiplier);
            Mov(quotient, Edx);
            if (shift != 0) Emit(MOp.Sar, quotient, Imm(shift));
            Emit(MOp.Sub, quotient, sign);
            if (rem)
            {
                MReg product = Temp();
                Emit(MOp.Imul3, product, quotient, Imm((int)signedDivisor.Value));
                Mov(Lo(i.Dest), numerator); Emit(MOp.Sub, Lo(i.Dest), product);
            }
            else Mov(Lo(i.Dest), quotient);
            return;
        }
        if (i.Operands[1] is ImmOperand constant && constant.Value is 3 or 5 or 10
            && ((!signed && i.Dest!.Type == IrType.I32)
                || (i.Dest!.Type == IrType.I64 && ZeroHigh(i.Operands[0]))))
        {
            // Exact unsigned quotients over [0, 2^32-1]: high32(n*m) >> s.
            // ceil(2^33/3), ceil(2^34/5), ceil(2^35/10), respectively.
            int shift = constant.Value == 3 ? 1 : constant.Value == 5 ? 2 : 3;
            int magic = constant.Value == 3 ? unchecked((int)0xaaaaaaab) : unchecked((int)0xcccccccd);
            MReg numerator = Temp(), multiplier = Temp(), quotient = Temp();
            Mov(numerator, i.Dest.Type == IrType.I64 ? PairRM(i.Operands[0]).Lo : RM(i.Operands[0]));
            Mov(multiplier, Imm(magic)); Mov(Eax, numerator);
            Emit(MOp.Mul, multiplier);
            Mov(quotient, Edx); Emit(MOp.Shr, quotient, Imm(shift));
            if (rem)
            {
                MReg product = Temp();
                Emit(MOp.Imul3, product, quotient, Imm((int)constant.Value));
                Mov(Lo(i.Dest), numerator); Emit(MOp.Sub, Lo(i.Dest), product);
            }
            else Mov(Lo(i.Dest), quotient);
            if (i.Dest.Type == IrType.I64) Mov(Hi(i.Dest), Imm(0));
            return;
        }
        if (i.Dest!.Type == IrType.I64 && ZeroHigh(i.Operands[0]) && ZeroHigh(i.Operands[1]))
        {
            // Both I64 values are nonnegative and fit in 32 unsigned bits.
            // Signed/unsigned division therefore agree. Keep DIV's zero-
            // divisor trap rather than folding away exceptional behavior.
            MReg divisor = Temp();
            Mov(divisor, PairRM(i.Operands[1]).Lo);
            Mov(Eax, PairRM(i.Operands[0]).Lo);
            Mov(Edx, Imm(0));
            Emit(MOp.Div, divisor);
            Mov(Lo(i.Dest), rem ? Edx : Eax);
            Mov(Hi(i.Dest), Imm(0));
            return;
        }
        if (i.Dest!.Type == IrType.I32)
        {
            // The divisor must not be an immediate and must not be EAX or
            // EDX; a fresh temporary loaded from whatever it is satisfies
            // both, and coalescing removes the move when it was a register.
            MReg divisor = Temp();
            Mov(divisor, RM(i.Operands[1]));
            Mov(Eax, RM(i.Operands[0]));
            if (signed)
            {
                Emit(MOp.Cdq);
            }
            else
            {
                Emit(MOp.Xor, Edx, Edx);
            }
            Emit(signed ? MOp.Idiv : MOp.Div, divisor);
            Mov(Lo(i.Dest), rem ? Edx : Eax);
            return;
        }

        // The 486 has no 64-bit divide: a cdecl runtime helper does it.
        string helper = (signed, rem) switch
        {
            (true, false) => "__divdi3",
            (false, false) => "__udivdi3",
            (true, true) => "__moddi3",
            _ => "__umoddi3",
        };
        (MOperand blo, MOperand bhi) = PairRM(i.Operands[1]);
        (MOperand alo, MOperand ahi) = PairRM(i.Operands[0]);
        Emit(MOp.Push, bhi);
        Emit(MOp.Push, blo);
        Emit(MOp.Push, ahi);
        Emit(MOp.Push, alo);
        CallSymbol(helper);
        Emit(MOp.Add, Esp, Imm(16));
        Mov(Lo(i.Dest), Eax);
        Mov(Hi(i.Dest), Edx);
    }

    private void SelectShift(Instr i)
    {
        MOp op = i.Op switch
        {
            Opcode.Shl => MOp.Shl,
            Opcode.ShrU => MOp.Shr,
            _ => MOp.Sar,
        };
        var dest = DestPair(i);
        Operand count = i.Operands[1];

        if (i.Dest!.Type == IrType.I32)
        {
            Mov(dest.Lo, RM(i.Operands[0]));
            if (count is ImmOperand c)
            {
                Emit(op, dest.Lo, Imm(c.Value & 31));
            }
            else
            {
                Mov(Ecx, RM(count));
                EmitCl(op, dest.Lo);
            }
            Commit(i, dest);
            return;
        }

        (MOperand alo, MOperand ahi) = PairRM(i.Operands[0]);
        if (op == MOp.Shl && count is ImmOperand smallCount)
        {
            int n = (int)(smallCount.Value & 63);
            _rangeVisits = 0;
            if (n < 32 && UnsignedBits(i.Operands[0]) + n <= 32)
            {
                Mov(dest.Lo, alo);
                if (n != 0) Emit(MOp.Shl, dest.Lo, Imm(n));
                Mov(dest.Hi, Imm(0));
                Commit(i, dest);
                return;
            }
        }
        if (op != MOp.Shl && count is ImmOperand narrowCount && ZeroHigh(i.Operands[0]))
        {
            int n = (int)(narrowCount.Value & 63);
            // A nonnegative I64 with no upper bits needs only a logical
            // low-word shift, including when the IR requested signed shift.
            Mov(dest.Lo, n >= 32 ? Imm(0) : alo);
            if (n > 0 && n < 32) Emit(MOp.Shr, dest.Lo, Imm(n));
            Mov(dest.Hi, Imm(0));
            Commit(i, dest);
            return;
        }
        if (count is ImmOperand wideCount && (wideCount.Value & 63) >= 32)
        {
            int n = (int)(wideCount.Value & 63) - 32;
            // Only one source half contributes. Do not first copy the half
            // that is immediately overwritten: it creates needless spills.
            if (op == MOp.Shl)
            {
                Mov(dest.Hi, alo);
                if (n != 0) Emit(MOp.Shl, dest.Hi, Imm(n));
                Mov(dest.Lo, Imm(0));
            }
            else
            {
                Mov(dest.Lo, ahi);
                if (op == MOp.Shr) Mov(dest.Hi, Imm(0));
                else { Mov(dest.Hi, ahi); Emit(MOp.Sar, dest.Hi, Imm(31)); }
                if (n != 0) Emit(op, dest.Lo, Imm(n));
            }
            Commit(i, dest);
            return;
        }
        Mov(dest.Lo, alo);
        Mov(dest.Hi, ahi);

        if (count is ImmOperand ci)
        {
            int n = (int)(ci.Value & 63);
            if (n == 0)
            {
                // Nothing to shift.
            }
            else if (n < 32)
            {
                if (op == MOp.Shl)
                {
                    Emit(MOp.Shld, dest.Hi, dest.Lo, Imm(n));
                    Emit(MOp.Shl, dest.Lo, Imm(n));
                }
                else
                {
                    Emit(MOp.Shrd, dest.Lo, dest.Hi, Imm(n));
                    Emit(op, dest.Hi, Imm(n));
                }
            }
            else if (op == MOp.Shl)
            {
                Mov(dest.Hi, dest.Lo);
                Emit(MOp.Xor, dest.Lo, dest.Lo);
                if (n > 32)
                {
                    Emit(MOp.Shl, dest.Hi, Imm(n - 32));
                }
            }
            else
            {
                Mov(dest.Lo, dest.Hi);
                if (op == MOp.Shr)
                {
                    Emit(MOp.Xor, dest.Hi, dest.Hi);
                }
                else
                {
                    Emit(MOp.Sar, dest.Hi, Imm(31));
                }
                if (n > 32)
                {
                    Emit(op, dest.Lo, Imm(n - 32));
                }
            }
            Commit(i, dest);
            return;
        }

        // A variable count: the double shift handles counts under 32 (the
        // hardware masks CL to five bits), and a branch fixes up the rest.
        Mov(Ecx, RM(count));
        if (op == MOp.Shl)
        {
            EmitCl(MOp.Shld, dest.Hi, dest.Lo);
            EmitCl(MOp.Shl, dest.Lo);
        }
        else
        {
            EmitCl(MOp.Shrd, dest.Lo, dest.Hi);
            EmitCl(op, dest.Hi);
        }
        Emit(MOp.Test, Ecx, Imm(32));
        MBlock big = Aside();
        MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", big);
        Jcc(Cond.E, done);
        _cur = big;
        if (op == MOp.Shl)
        {
            Mov(dest.Hi, dest.Lo);
            Emit(MOp.Xor, dest.Lo, dest.Lo);
        }
        else
        {
            Mov(dest.Lo, dest.Hi);
            if (op == MOp.Shr)
            {
                Emit(MOp.Xor, dest.Hi, dest.Hi);
            }
            else
            {
                Emit(MOp.Sar, dest.Hi, Imm(31));
            }
        }
        _cur = done;
        Commit(i, dest);
    }

    /// <summary>A shift by CL: the count operand is the byte register.</summary>
    private void EmitCl(MOp op, MReg dest, MReg? src = null)
    {
        if (src is null)
        {
            Emit(op, dest, Ecx);
        }
        else
        {
            Emit(op, dest, src, Ecx);
        }
    }

    private void SelectUnary(Instr i)
    {
        MOp op = i.Op == Opcode.Neg ? MOp.Neg : MOp.Not;
        VReg d = i.Dest!;
        if (d.Type == IrType.I32)
        {
            Mov(Lo(d), RM(i.Operands[0]));
            Emit(op, Lo(d));
            return;
        }
        (MOperand lo, MOperand hi) = PairRM(i.Operands[0]);
        Mov(Lo(d), lo);
        Mov(Hi(d), hi);
        if (op == MOp.Not)
        {
            Emit(MOp.Not, Lo(d));
            Emit(MOp.Not, Hi(d));
        }
        else
        {
            // -(h:l) = ~(h:l) + 1 = (-l, ~h + borrow); `neg lo` sets carry when lo was nonzero.
            Emit(MOp.Neg, Lo(d));
            Emit(MOp.Adc, Hi(d), Imm(0));
            Emit(MOp.Neg, Hi(d));
        }
    }

    // ---- comparisons ----------------------------------------------------------

    private static Cond CondOf(Opcode op) => op switch
    {
        Opcode.Eq => Cond.E,
        Opcode.Ne => Cond.Ne,
        Opcode.LtS => Cond.L,
        Opcode.LeS => Cond.Le,
        Opcode.GtS => Cond.G,
        Opcode.GeS => Cond.Ge,
        Opcode.LtU => Cond.B,
        Opcode.LeU => Cond.Be,
        Opcode.GtU => Cond.A,
        _ => Cond.Ae,
    };

    /// <summary>Set the flags for an integer compare; returns the condition that means "true".</summary>
    private Cond IntCompare(Instr i)
    {
        Cond c = CondOf(i.Op);
        Operand a = i.Operands[0];
        Operand b = i.Operands[1];
        bool narrow64 = a.Type == IrType.I64 && ZeroHigh(a) && ZeroHigh(b);
        if (narrow64)
        {
            // Positive I64 operands are ordered by their unsigned low words,
            // not by the sign bit of those low words.
            c = c switch { Cond.L => Cond.B, Cond.Le => Cond.Be, Cond.G => Cond.A, Cond.Ge => Cond.Ae, _ => c };
        }
        if (a.Type == IrType.I32 || narrow64)
        {
            if (a is not RegOperand && b is RegOperand)
            {
                (a, b) = (b, a);
                c = c.Swap();
            }
            MReg ra = R(a);
            if (b is ImmOperand { Value: 0 })
            {
                // Against zero, test is a byte shorter than cmp and says the same.
                Emit(MOp.Test, ra, ra);
            }
            else
            {
                Emit(MOp.Cmp, ra, RM(b));
            }
            return c;
        }

        if (c is Cond.E or Cond.Ne)
        {
            if (a is not RegOperand && b is RegOperand)
            {
                (a, b) = (b, a);
            }
            (MOperand alo, MOperand ahi) = PairRM(a);
            (MOperand blo, MOperand bhi) = PairRM(b);
            // ZF of (alo ^ blo) | (ahi ^ bhi); a zero half of b needs no xor at all.
            MReg t = Temp();
            Mov(t, alo);
            if (blo is not MImm { IsPlain: true, Value: 0 })
            {
                Emit(MOp.Xor, t, blo);
            }
            if (bhi is MImm { IsPlain: true, Value: 0 })
            {
                Emit(MOp.Or, t, ahi);
            }
            else
            {
                MReg u = Temp();
                Mov(u, ahi);
                Emit(MOp.Xor, u, bhi);
                Emit(MOp.Or, t, u);
            }
            return c;
        }

        // A subtract with borrow leaves SF, OF and CF describing the whole
        // 64-bit difference, but ZF only its high half; so less-than and
        // greater-or-equal read straight off it, and the other two are the
        // same test with the operands exchanged.
        if (c is Cond.Le or Cond.G or Cond.Be or Cond.A)
        {
            (a, b) = (b, a);
            c = c.Swap();
        }
        (MReg xl, MReg xh) = Pair(a);
        (MOperand yl, MOperand yh) = PairRM(b);
        MReg h = Temp();
        Emit(MOp.Cmp, xl, yl);
        Mov(h, xh);
        Emit(MOp.Sbb, h, yh);
        return c;
    }

    /// <summary>Materialise a condition as 0 or 1 in a byte-capable register, widened.</summary>
    private void SetCond(Cond c, VReg dest)
    {
        MReg d = Lo(dest);
        _m.ByteRegs.Add(d.Id);
        Emit(new MInstr(MOp.Setcc, d) { Cond = c, Width = 1 });
        EmitW(MOp.Movzx, 1, d, d);
    }

    private void SelectCompare(Instr i)
    {
        Cond c = IntCompare(i);
        SetCond(c, i.Dest!);
    }

    /// <summary>
    /// How an x87 compare result maps to flags. After fucompp / fnstsw /
    /// sahf, CF is "below", ZF is "equal" and PF is "unordered", with all
    /// three set for unordered; every ordered relation is chosen so that
    /// pattern reads as false, and equality has to test parity separately.
    /// </summary>
    private readonly record struct FloatCond(Cond Cond, bool ParityFalse, bool ParityTrue);

    private FloatCond FloatCompare(Instr i)
    {
        Operand a = i.Operands[0];
        Operand b = i.Operands[1];
        // ST(0) is whichever was loaded second; the flags describe ST(0) ? ST(1).
        bool aOnTop = i.Op is Opcode.FGt or Opcode.FGe or Opcode.FEq or Opcode.FNe;
        Fld(aOnTop ? b : a);
        Fld(aOnTop ? a : b);
        Emit(MOp.Fucompp);
        Emit(MOp.Fnstsw);
        Emit(MOp.Sahf);
        return i.Op switch
        {
            Opcode.FEq => new FloatCond(Cond.E, true, false),
            Opcode.FNe => new FloatCond(Cond.Ne, false, true),
            Opcode.FLt or Opcode.FGt => new FloatCond(Cond.A, false, false),
            _ => new FloatCond(Cond.Ae, false, false),
        };
    }

    private void SelectFloatCompare(Instr i)
    {
        FloatCond fc = FloatCompare(i);
        MReg d = Lo(i.Dest!);
        _m.ByteRegs.Add(d.Id);
        Emit(new MInstr(MOp.Setcc, d) { Cond = fc.Cond, Width = 1 });
        if (fc.ParityFalse || fc.ParityTrue)
        {
            MReg p = Temp();
            _m.ByteRegs.Add(p.Id);
            Emit(new MInstr(MOp.Setcc, p) { Cond = fc.ParityFalse ? Cond.Np : Cond.P, Width = 1 });
            EmitW(fc.ParityFalse ? MOp.And : MOp.Or, 1, d, p);
        }
        EmitW(MOp.Movzx, 1, d, d);
    }

    private void SelectFloatAlu(Instr i)
    {
        MOp op = i.Op switch
        {
            Opcode.FAdd => MOp.Fadd,
            Opcode.FSub => MOp.Fsub,
            Opcode.FMul => MOp.Fmul,
            _ => MOp.Fdiv,
        };
        Fld(i.Operands[0]);
        EmitW(op, FWidth(i.Operands[1].Type), FHome(i.Operands[1]));
        Fstp(i.Dest!);
    }

    // ---- conversions ------------------------------------------------------------

    private void SelectNarrow(Instr i)
    {
        VReg d = i.Dest!;
        MReg src = R(i.Operands[0]);
        MReg lo = Lo(d);
        switch (i.Op)
        {
            case Opcode.ZExt8:
                // `and` rather than movzx from a byte register: any register can do it.
                Mov(lo, src);
                Emit(MOp.And, lo, Imm(0xFF));
                break;
            case Opcode.ZExt16:
                EmitW(MOp.Movzx, 2, lo, src);
                break;
            case Opcode.SExt16:
                EmitW(MOp.Movsx, 2, lo, src);
                break;
            default:
                _m.ByteRegs.Add(src.Id);
                EmitW(MOp.Movsx, 1, lo, src);
                break;
        }
        if (d.Type == IrType.I64)
        {
            if (i.Op is Opcode.ZExt8 or Opcode.ZExt16)
            {
                Emit(MOp.Xor, Hi(d), Hi(d));
            }
            else
            {
                Mov(Hi(d), lo);
                Emit(MOp.Sar, Hi(d), Imm(31));
            }
        }
    }

    private void SelectIntToFloat(Instr i)
    {
        Operand src = i.Operands[0];
        int s = _m.Frame.Scratch;
        bool wide = src.Type == IrType.I64;
        if (wide)
        {
            (MOperand lo, MOperand hi) = PairRM(src);
            Mov(MMem.Frame(s), lo);
            Mov(MMem.Frame(s + 4), hi);
        }
        else
        {
            Mov(MMem.Frame(s), RM(src));
            if (i.Op == Opcode.UToF)
            {
                // fild is signed: a 32-bit unsigned value is the 64-bit value with a zero high half.
                Mov(MMem.Frame(s + 4), Imm(0));
            }
        }
        EmitW(MOp.Fild, wide || i.Op == Opcode.UToF ? 8 : 4, MMem.Frame(s));
        if (wide && i.Op == Opcode.UToF)
        {
            // fild read the value as signed; a set top bit means it was 2^64 too small.
            // 2^64 is a power of two, so a single-precision constant is exact.
            Emit(MOp.Test, MMem.Frame(s + 4), Imm(int.MinValue));
            MBlock fix = Aside();
            MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", fix);
            Jcc(Cond.E, done);
            _cur = fix;
            Emit(MOp.Push, Imm(0x5F800000));
            EmitW(MOp.Fadd, 4, new MMem(Esp, 0));
            Emit(MOp.Add, Esp, Imm(4));
            _cur = done;
        }
        Fstp(i.Dest!);
    }

    private void SelectFloatToIntCore(Instr i, bool unsignedHighHalf = false)
    {
        VReg d = i.Dest!;
        int s = _m.Frame.Scratch;
        Fld(i.Operands[0]);
        if (unsignedHighHalf)
        {
            // Convert the upper half of UInt64 through a signed FISTP, without
            // overflowing its signed range. The power-of-two subtraction is exact.
            Emit(MOp.Push, Imm(0x5f000000));
            EmitW(MOp.Fsub, 4, new MMem(Esp, 0)); Emit(MOp.Add, Esp, Imm(4));
        }
        // fistp rounds by the control word, which defaults to nearest; C#
        // truncates. Save the word, set RC=11 (chop), convert, restore.
        EmitW(MOp.Fnstcw, 2, MMem.Frame(s));
        MReg cw = Temp();
        EmitW(MOp.Movzx, 2, cw, MMem.Frame(s));
        Emit(MOp.Or, cw, Imm(0xC00));
        EmitW(MOp.Mov, 2, MMem.Frame(s + 2), cw);
        EmitW(MOp.Fldcw, 2, MMem.Frame(s + 2));
        // An unsigned 32-bit result needs the 64-bit store: the 32-bit form
        // cannot represent anything above 2^31 - 1.
        bool qword = d.Type == IrType.I64 || i.Op == Opcode.FToU;
        EmitW(MOp.Fistp, qword ? 8 : 4, MMem.Frame(s + 8));
        EmitW(MOp.Fldcw, 2, MMem.Frame(s));
        Mov(Lo(d), MMem.Frame(s + 8));
        if (d.Type == IrType.I64)
        {
            Mov(Hi(d), MMem.Frame(s + 12));
            if (unsignedHighHalf) Emit(MOp.Or, Hi(d), Imm(int.MinValue));
        }
    }

    private void SelectBits(Instr i)
    {
        VReg d = i.Dest!;
        Operand src = i.Operands[0];
        switch (d.Type)
        {
            case IrType.I32:
                Mov(Lo(d), FHome(src));
                break;
            case IrType.I64:
            {
                MMem home = FHome(src);
                Mov(Lo(d), home);
                Mov(Hi(d), new MMem(home.Base, home.Disp + 4));
                break;
            }
            case IrType.F32:
                Mov(FHome(d), RM(src));
                break;
            default:
            {
                (MOperand lo, MOperand hi) = PairRM(src);
                MMem home = FHome(d);
                Mov(home, lo);
                Mov(new MMem(home.Base, home.Disp + 4), hi);
                break;
            }
        }
    }

    // ---- memory -------------------------------------------------------------------

    private void SelectLoad(Instr i)
    {
        VReg d = i.Dest!;
        MMem m = Address(i.Operands[0], i.Offset);
        if (d.Type.IsFloat())
        {
            if (i.Size == FWidth(d.Type)) { CopyFloatBits(FHome(d), m, i.Size); return; }
            EmitW(MOp.Fld, i.Size, m);
            Fstp(d);
            return;
        }
        MReg lo = Lo(d);
        switch (i.Size)
        {
            case 1:
            case 2:
                EmitW(i.Signed ? MOp.Movsx : MOp.Movzx, i.Size, lo, m);
                break;
            case 4:
                Mov(lo, m);
                break;
            case 8:
            {
                if (d.Type != IrType.I64)
                {
                    Error("8-byte load into a 32-bit register");
                    return;
                }
                MMem m4 = Displaced(m, 4);
                // Load the half that is not the base register last, so the
                // address survives until both halves are read.
                if (m.Base is not null && m.Base.Id == lo.Id)
                {
                    Mov(Hi(d), m4);
                    Mov(lo, m);
                }
                else
                {
                    Mov(lo, m);
                    Mov(Hi(d), m4);
                }
                break;
            }
            default:
                Error($"load of size {i.Size}");
                break;
        }
        if (d.Type == IrType.I64 && i.Size < 8)
        {
            if (i.Signed)
            {
                Mov(Hi(d), lo);
                Emit(MOp.Sar, Hi(d), Imm(31));
            }
            else
            {
                Emit(MOp.Xor, Hi(d), Hi(d));
            }
        }
    }

    private void SelectStore(Instr i)
    {
        MMem m = Address(i.Operands[0], i.Offset);
        Operand v = i.Operands[1];
        if (v.Type.IsFloat())
        {
            if (i.Size == FWidth(v.Type)) { CopyFloatBits(m, FHome(v), i.Size); return; }
            Fld(v);
            EmitW(MOp.Fstp, i.Size, m);
            return;
        }
        switch (i.Size)
        {
            case 1:
            case 2:
            {
                MOperand src = v.Type == IrType.I64 ? PairRM(v).Lo : RM(v);
                if (src is MReg r && i.Size == 1)
                {
                    _m.ByteRegs.Add(r.Id);
                }
                if (src is MImm imm)
                {
                    src = Imm(imm.Value & (i.Size == 1 ? 0xFF : 0xFFFF));
                }
                EmitW(MOp.Mov, i.Size, m, src);
                break;
            }
            case 4:
                Mov(m, v.Type == IrType.I64 ? PairRM(v).Lo : RM(v));
                break;
            case 8:
            {
                (MOperand lo, MOperand hi) = PairRM(v);
                Mov(m, lo);
                Mov(Displaced(m, 4), hi);
                break;
            }
            default:
                Error($"store of size {i.Size}");
                break;
        }
    }

    private bool SelectFrameSet(Instr i)
    {
        if (SelectMmxFrameZero(i)) return true;
        if (i.Operands[2] is not ImmOperand count || count.Value < 0 || count.Value > 32
            || i.Operands[1] is not ImmOperand fill) return false;
        if (count.Value == 0) return true;
        Operand address = i.Operands[0];
        for (int depth = 0; depth < 8 && address is RegOperand r; depth++)
        {
            if (!_definitions.TryGetValue(r.Reg, out Instr? definition) || definition is null
                || definition.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)) return false;
            address = definition.Operands[0];
        }
        // Restrict widened stores to known frame storage, never arbitrary
        // pointers or device memory. Frame slot alignment also avoids AC faults.
        if (address is not SlotOperand slot || slot.Slot.Align < 4 || count.Value > slot.Slot.Bytes) return false;
        int start = _m.Frame.SlotOffset(slot.Slot);
        int length = (int)count.Value;
        uint repeated = unchecked((uint)(byte)fill.Value * 0x01010101u);
        int offset = 0;
        while (offset + 4 <= length)
        {
            EmitW(MOp.Mov, 4, MMem.Frame(start + offset), Imm(unchecked((int)repeated)));
            offset += 4;
        }
        if (offset + 2 <= length)
        {
            EmitW(MOp.Mov, 2, MMem.Frame(start + offset), Imm(repeated & 65535));
            offset += 2;
        }
        if (offset < length) EmitW(MOp.Mov, 1, MMem.Frame(start + offset), Imm(repeated & 255));
        return true;
    }

    private void SelectMemCopy(Instr i)
    {
        if (SelectMmxFrameCopy(i)) return;
        Mov(Edi, RM(i.Operands[0]));
        Mov(Esi, RM(i.Operands[1]));
        if (i.Operands[2] is ImmOperand n)
        {
            // A constant length: whole words with movsd, the remainder with movsb.
            if (n.Value / 4 > 0)
            {
                Mov(Ecx, Imm(n.Value / 4));
                Emit(MOp.RepMovsd);
            }
            if (n.Value % 4 > 0)
            {
                Mov(Ecx, Imm(n.Value % 4));
                Emit(MOp.RepMovsb);
            }
            return;
        }
        Mov(Ecx, RM(i.Operands[2]));
        Emit(MOp.RepMovsb);
    }

    private void SelectAtomic(Instr i)
    {
        VReg d = i.Dest!;
        if (d.Type != IrType.I32)
        {
            Error("64-bit atomics are not available on a 486");
            return;
        }
        MMem m = Address(i.Operands[0], 0);
        switch (i.Op)
        {
            case Opcode.AtomicSwap:
                // xchg with a memory operand locks the bus by itself.
                Mov(Lo(d), RM(i.Operands[1]));
                Emit(MOp.Xchg, m, Lo(d));
                break;
            case Opcode.AtomicAdd:
                Mov(Lo(d), RM(i.Operands[1]));
                Emit(new MInstr(MOp.Xadd, m, Lo(d)) { Lock = true });
                break;
            case Opcode.AtomicCas:
            {
                MReg value = R(i.Operands[2]);
                Mov(Eax, RM(i.Operands[1]));
                Emit(new MInstr(MOp.Cmpxchg, m, value) { Lock = true });
                Mov(Lo(d), Eax);
                break;
            }
            default:
            {
                // No single locked instruction returns the old value of a
                // bitwise op: read, compute, and cmpxchg until it sticks.
                MOp op = i.Op == Opcode.AtomicAnd ? MOp.And : i.Op == Opcode.AtomicOr ? MOp.Or : MOp.Xor;
                MOperand v = RM(i.Operands[1]);
                Mov(Eax, m);
                MBlock loop = Split();
                MReg t = Temp();
                Mov(t, Eax);
                Emit(op, t, v);
                Emit(new MInstr(MOp.Cmpxchg, m, t) { Lock = true });
                Jcc(Cond.Ne, loop);
                Split();
                Mov(Lo(d), Eax);
                break;
            }
        }
    }

    // ---- calls ----------------------------------------------------------------------

    /// <summary>Push one argument; returns the bytes it took.</summary>
    private int PushArg(Operand o)
    {
        switch (o.Type)
        {
            case IrType.I64:
            {
                (MOperand lo, MOperand hi) = PairRM(o);
                Emit(MOp.Push, hi);
                Emit(MOp.Push, lo);
                return 8;
            }
            case IrType.F64:
            {
                MMem home = FHome(o);
                Emit(MOp.Push, new MMem(home.Base, home.Disp + 4));
                Emit(MOp.Push, home);
                return 8;
            }
            case IrType.F32:
                Emit(MOp.Push, FHome(o));
                return 4;
            default:
                Emit(MOp.Push, RM(o));
                return 4;
        }
    }

    private void SelectCall(Instr i)
    {
        VReg? d = i.Dest;
        if (i.Op == Opcode.Call && i.Callee == "__exception")
        {
            // The pseudo-call at the head of a landing pad: the unwinder
            // arrived with the exception in EAX and nothing else defined.
            if (d is not null)
            {
                Mov(Lo(d), Eax);
            }
            return;
        }

        if (i.Op == Opcode.Call && i.Callee is { } callee && callee.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal))
        {
            SelectMachineIntrinsic(callee, i);
            return;
        }

        int first = i.Op == Opcode.CallIndirect ? 1 : 0;
        int bytes = 0;
        for (int k = i.Operands.Count - 1; k >= first; k--)
        {
            bytes += PushArg(i.Operands[k]);
        }
        if (i.Op == Opcode.Call)
        {
            CallSymbol(i.Callee!);
        }
        else
        {
            Emit(MOp.CallInd, R(i.Operands[0]));
        }
        if (bytes > 0)
        {
            Emit(MOp.Add, Esp, Imm(bytes));
        }
        if (d is null)
        {
            return;
        }
        switch (d.Type)
        {
            case IrType.I32:
                Mov(Lo(d), Eax);
                break;
            case IrType.I64:
                Mov(Lo(d), Eax);
                Mov(Hi(d), Edx);
                break;
            default:
                Fstp(d);
                break;
        }
    }

    private void SelectRet(Instr i)
    {
        if (i.Operands.Count > 0)
        {
            Operand v = i.Operands[0];
            switch (v.Type)
            {
                case IrType.I32:
                    Mov(Eax, RM(v));
                    break;
                case IrType.I64:
                {
                    (MOperand lo, MOperand hi) = PairRM(v);
                    Mov(Eax, lo);
                    Mov(Edx, hi);
                    break;
                }
                default:
                    Fld(v);
                    break;
            }
        }
        Emit(MOp.Epilogue);
    }

    // ---- control ---------------------------------------------------------------------

    private void SelectBranch(Instr i)
    {
        MBlock t = _heads[i.Targets[0]];
        MBlock f = _heads[i.Targets[1]];
        Operand c = i.Operands[0];
        if (c is ImmOperand imm)
        {
            Jmp(imm.Value != 0 ? t : f);
            return;
        }
        if (c is RegOperand cr && _constants.TryGetValue(cr.Reg.Id, out long known))
        {
            Jmp(known != 0 ? t : f);
            return;
        }
        MReg r = R(c);
        Emit(MOp.Test, r, r);
        Jcc(Cond.Ne, t);
        Jmp(f);
    }

    private void SelectFusedBranch(Instr cmp, Instr br)
    {
        MBlock t = _heads[br.Targets[0]];
        MBlock f = _heads[br.Targets[1]];
        if (cmp.Op is >= Opcode.FEq and <= Opcode.FGe)
        {
            FloatCond fc = FloatCompare(cmp);
            if (fc.ParityFalse)
            {
                Jcc(Cond.P, f);
            }
            else if (fc.ParityTrue)
            {
                Jcc(Cond.P, t);
            }
            Jcc(fc.Cond, t);
            Jmp(f);
            return;
        }
        Cond c = IntCompare(cmp);
        Jcc(c, t);
        Jmp(f);
    }

    private void SelectSwitch(Instr i)
    {
        MReg idx = R(i.Operands[0]);
        Emit(MOp.Cmp, idx, Imm(i.Targets.Count));
        Jcc(Cond.Ae, _heads[i.Default!]);
        List<MBlock> table = i.Targets.Select(b => _heads[b]).ToList();
        Emit(new MInstr(MOp.JmpTable, idx) { Table = table });
    }

    private void SelectUnwind(Instr i)
    {
        // Once ESP and EBP change, no frame-relative operand is valid, so
        // everything is in fixed registers before the first of them moves.
        Mov(Ecx, RM(i.Operands[0]));
        Mov(Eax, RM(i.Operands[1]));
        Mov(Esp, new MMem(Ecx, 8));
        Mov(Ebp, new MMem(Ecx, 12));
        Emit(MOp.JmpInd, new MMem(Ecx, 4));
    }

    // ---- the instructions only a driver or a kernel may execute -----------------------

    /// <summary>
    /// A machine instruction the language offers as a `Sys.*` intrinsic and
    /// that no sequence of ordinary IR can express: port I/O, the interrupt
    /// flag, the descriptor-table registers, the control registers.
    ///
    /// Lowering emits these as calls to reserved names rather than as new IR
    /// opcodes so that every pass between lowering and here treats them as
    /// what they are -- something with effects, which may not be moved,
    /// duplicated or deleted -- without having to be taught one by one. The
    /// call is never emitted: it is replaced, here, by the instruction.
    ///
    /// Operands go to the registers the hardware fixes (the port in DX, the
    /// datum in AL/AX/EAX, the string forms' ESI/EDI/ECX) and the allocator
    /// learns the dependency from Roles.ImplicitUses, exactly as it does for
    /// the system-call trap.
    /// </summary>
    private void ByteSwapWord(MReg value)
    {
        if (Target.Current.X86Profile.Name != "386") { Emit(MOp.Bswap, value); return; }
        // 386 has no BSWAP: exchange adjacent bytes, then exchange 16-bit halves.
        // General-register temporaries avoid constraining allocation to AL/AH.
        MReg other = Temp(); Mov(other, value);
        Emit(MOp.Shl, value, Imm(8)); Emit(MOp.And, value, Imm(unchecked((int)0xff00ff00)));
        Emit(MOp.Shr, other, Imm(8)); Emit(MOp.And, other, Imm(0x00ff00ff));
        Emit(MOp.Or, value, other); Emit(MOp.Ror, value, Imm(16));
    }

    private void SelectMachineIntrinsic(string name, Instr i)
    {
        switch (name)
        {
            case MachineIntrinsics.In8:
            case MachineIntrinsics.In16:
            case MachineIntrinsics.In32:
            {
                int w = name == MachineIntrinsics.In8 ? 1 : name == MachineIntrinsics.In16 ? 2 : 4;
                Mov(Edx, RM(i.Operands[0]));
                // The narrow forms write only AL or AX, so the rest of EAX is
                // whatever was there: zero it first and the result is the
                // canonical unsigned byte or word the language promises.
                if (w != 4)
                {
                    Emit(MOp.Xor, Eax, Eax);
                }
                EmitW(MOp.In, w);
                // A read whose value is dropped -- the 400 ns delay of
                // reading a status port -- has no destination to move to.
                if (i.Dest is not null)
                {
                    Mov(Lo(i.Dest), Eax);
                }
                return;
            }
            case MachineIntrinsics.Out8:
            case MachineIntrinsics.Out16:
            case MachineIntrinsics.Out32:
            {
                int w = name == MachineIntrinsics.Out8 ? 1 : name == MachineIntrinsics.Out16 ? 2 : 4;
                Mov(Edx, RM(i.Operands[0]));
                Mov(Eax, RM(i.Operands[1]));
                EmitW(MOp.Out, w);
                return;
            }
            case MachineIntrinsics.InString16:
            case MachineIntrinsics.OutString16:
            {
                bool read = name == MachineIntrinsics.InString16;
                Mov(Edx, RM(i.Operands[0]));
                Mov(read ? Edi : Esi, RM(i.Operands[1]));
                Mov(Ecx, RM(i.Operands[2]));
                Emit(read ? MOp.RepInsw : MOp.RepOutsw);
                return;
            }
            case MachineIntrinsics.Cli:
                Emit(MOp.Cli);
                return;
            case MachineIntrinsics.Sti:
                Emit(MOp.Sti);
                return;
            case MachineIntrinsics.Hlt:
                Emit(MOp.Hlt);
                return;
            case MachineIntrinsics.Lidt:
            case MachineIntrinsics.Lgdt:
            case MachineIntrinsics.Invlpg:
            {
                MReg addr = R(i.Operands[0]);
                MOp op = name == MachineIntrinsics.Lidt ? MOp.Lidt
                       : name == MachineIntrinsics.Lgdt ? MOp.Lgdt
                       : MOp.Invlpg;
                Emit(op, new MMem(addr, 0));
                return;
            }
            case MachineIntrinsics.ReadCr:
                Emit(MOp.MovFromCr, Lo(i.Dest!), ControlRegister(i.Operands[0]));
                return;
            case MachineIntrinsics.WriteCr:
                Emit(MOp.MovToCr, R(i.Operands[1]), ControlRegister(i.Operands[0]));
                return;
            case MachineIntrinsics.ThreadBlock:
                // The self pointer at gs:[0]. A machine intrinsic and not an
                // ordinary load because nothing before here knows that GS is
                // not DS, and a pass that hoisted it above a SetGs would read
                // another thread's block.
                if (i.Dest is not null)
                {
                    Emit(MOp.GsSelf, Lo(i.Dest));
                }
                return;
            case MachineIntrinsics.SetGs:
                Emit(MOp.SetGs, R(i.Operands[0]));
                return;
            case MachineIntrinsics.LoadSegments:
                // DS, ES, FS, GS and SS all take the one data selector, which
                // the instruction reads from AX. CS is the far jump's business
                // and not this one's.
                Mov(Eax, RM(i.Operands[0]));
                Emit(MOp.LoadSegments);
                return;
            default:
                Error($"unknown machine intrinsic {name}");
                return;
        }
    }

    /// <summary>
    /// The control register number, which has to have been a constant: the
    /// instruction encodes it and there is no form that reads one from a
    /// register.
    /// </summary>
    private MImm ControlRegister(Operand o)
    {
        long n = o switch
        {
            ImmOperand imm => imm.Value,
            RegOperand r when _constants.TryGetValue(r.Reg.Id, out long k) => k,
            _ => -1,
        };
        if (n is < 0 or > 7)
        {
            Error("the control register number must be a constant 0 to 7");
            n = 0;
        }
        return Imm(n);
    }

    // ---- the operating system --------------------------------------------------------

    /// <summary>
    /// Linux i386: number in EAX, arguments in EBX, ECX, EDX, ESI, EDI, EBP,
    /// result in EAX. The sixth argument displaces the frame pointer for the
    /// duration of the trap. This is the only OS-specific selection.
    /// </summary>
    private void SelectSyscall(Instr i)
    {
        MReg[] regs = { Ebx, Ecx, Edx, Esi, Edi };
        int args = i.Operands.Count - 1;
        if (args > 6)
        {
            Error("syscall with more than six arguments");
            return;
        }
        Mov(Eax, RM(i.Operands[0]));
        for (int k = 0; k < Math.Min(args, 5); k++)
        {
            Mov(regs[k], RM(i.Operands[k + 1]));
        }
        bool sixth = args == 6;
        if (sixth)
        {
            // The argument is read before EBP changes: any spill reload the
            // allocator adds lands ahead of this move, while EBP still holds the frame.
            MReg a6 = R(i.Operands[6]);
            Emit(MOp.Push, Ebp);
            Mov(Ebp, a6);
        }
        Emit(MOp.SyscallTrap);
        if (sixth)
        {
            Emit(MOp.Pop, Ebp);
        }
        Mov(Lo(i.Dest!), Eax);
    }
}
