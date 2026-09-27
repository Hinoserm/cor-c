#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X64;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Instruction selection for x86-64: one IR function into machine
/// instructions over virtual registers, in the shape the allocator works on.
///
/// Every value is one register: an I32 or an I64 in a general register (the
/// 32-bit forms of the instructions for I32, the 64-bit ones for I64 and for
/// every address), an F32 or F64 in an XMM register. Two-address instructions
/// are preceded by a move into their destination, which the allocator
/// coalesces away when it can; fixed-register instructions -- the divide,
/// the shifts by CL, the string moves, a call's arguments -- name the
/// physical registers directly, and the allocator treats those as busy.
///
/// THE CALLING CONVENTION IS SYSTEM V AMD64: integer arguments in RDI, RSI,
/// RDX, RCX, R8, R9, floating-point ones in XMM0..XMM7, the rest on the
/// stack, eight bytes each; results in RAX or XMM0. RBX, RBP and R12..R15
/// are preserved by the callee, everything else is not. The stack is
/// sixteen-byte aligned at every call.
/// </summary>
internal sealed class Selector
{
    private readonly Function _f;
    private readonly MFunction _m;
    private readonly List<string> _errors;
    private readonly Dictionary<int, MReg> _regs = new();
    private readonly Dictionary<Block, MBlock> _heads = new();
    private readonly Dictionary<int, int> _useCount = new();
    private readonly Dictionary<int, long> _constants = new();
    private MBlock _cur = null!;
    private int _splits;
    private int _tables;
    private int _line;

    internal static readonly Gpr[] IntArgRegs = { Gpr.Rdi, Gpr.Rsi, Gpr.Rdx, Gpr.Rcx, Gpr.R8, Gpr.R9 };
    internal const int FloatArgRegs = 8;

    private static readonly MReg Rax = MReg.Of(Gpr.Rax);
    private static readonly MReg Rcx = MReg.Of(Gpr.Rcx);
    private static readonly MReg Rdx = MReg.Of(Gpr.Rdx);
    private static readonly MReg Rsi = MReg.Of(Gpr.Rsi);
    private static readonly MReg Rdi = MReg.Of(Gpr.Rdi);
    private static readonly MReg Rsp = MReg.Of(Gpr.Rsp);
    private static readonly MReg Rbp = MReg.Of(Gpr.Rbp);
    private static readonly MReg R8 = MReg.Of(Gpr.R8);
    private static readonly MReg R9 = MReg.Of(Gpr.R9);
    private static readonly MReg R10 = MReg.Of(Gpr.R10);
    private static readonly MReg Xmm0 = MReg.Xmm(0);

    private Selector(Function f, List<string> errors, Func<string, bool>? isExternal)
    {
        _f = f;
        _m = new MFunction(f);
        _errors = errors;
        _external = isExternal;
    }

    /// <summary>
    /// Select one function. <paramref name="isExternal"/> names what another
    /// image defines -- in a shared object everything this object does not,
    /// in a dynamically linked program what its libraries supply -- which is
    /// reached through its GOT slot rather than at its own address.
    /// </summary>
    public static MFunction Run(Function f, List<string> errors, Func<string, bool>? isExternal = null)
    {
        Selector s = new(f, errors, isExternal);
        s.Select();
        return s._m;
    }

    private readonly Func<string, bool>? _external;

    private bool External(string symbol) => _external is not null && _external(symbol);

    /// <summary>
    /// The address of a symbol another image defines, plus an offset: its
    /// GOT slot read (`mov r, [rip + sym@GOTPCREL]`), which the linker turns
    /// back into `lea r, [rip + sym]` when the symbol is defined after all.
    /// </summary>
    private MReg GotAddress(string symbol, long offset, MReg? into = null)
    {
        MReg t = into ?? Temp();
        EmitW(MOp.Mov, 8, t, MMem.RipGot(symbol));
        if (offset != 0)
        {
            Emit(MOp.Lea, t, new MMem(t, checked((int)offset)));
        }
        return t;
    }

    private void Error(string what) => _errors.Add($"{_f.Name}: {what}");

    // ---- emission ----------------------------------------------------------

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

    /// <summary>A block placed after the current one.</summary>
    private MBlock Aside() => _m.NewBlock($"{_cur.Name}.{_splits++}", _cur);

    private MReg Temp() => _m.NewReg();

    private MReg FTemp() => _m.NewFloat();

    private static MImm Imm(long v) => new(v);

    private static int Width(IrType t) => t is IrType.I64 or IrType.F64 ? 8 : 4;

    /// <summary>A move of an integer of the given width. A move of 4 bytes into a register clears the top half.</summary>
    private void Mov(MOperand dst, MOperand src, int width = 8) => EmitW(MOp.Mov, width, dst, src);

    // ---- operands ----------------------------------------------------------

    /// <summary>The machine register a virtual one lives in, of the class its type says.</summary>
    private MReg V(VReg v)
    {
        if (!_regs.TryGetValue(v.Id, out MReg? r))
        {
            r = v.Type.IsFloat() ? _m.NewFloat() : _m.NewReg();
            _regs[v.Id] = r;
        }
        return r;
    }

    /// <summary>An integer operand in a register, materialising a constant, a symbol or a frame address.</summary>
    private MReg R(Operand o)
    {
        switch (o)
        {
            case RegOperand r:
                return V(r.Reg);
            case SlotOperand s:
            {
                MReg t = Temp();
                Emit(MOp.Lea, t, MMem.Frame(_m.Frame.SlotOffset(s.Slot)));
                return t;
            }
            case SymOperand s:
            {
                if (External(s.Name))
                {
                    return GotAddress(s.Name, s.Offset);
                }
                MReg t = Temp();
                Emit(MOp.Lea, t, MMem.Rip(s.Name, checked((int)s.Offset)));
                return t;
            }
            case ImmOperand i:
            {
                MReg t = Temp();
                LoadConstant(t, i.Value, Width(i.Type));
                return t;
            }
            default:
                throw new InvalidOperationException(o.GetType().Name);
        }
    }

    /// <summary>A constant into a register, by the shortest form that makes it.</summary>
    private void LoadConstant(MReg r, long value, int width)
    {
        if (width == 4)
        {
            Mov(r, Imm(unchecked((int)value)), 4);
        }
        else if (value is >= 0 and <= uint.MaxValue)
        {
            // A 32-bit move zero-extends: five bytes rather than ten.
            Mov(r, Imm(unchecked((int)(uint)value)), 4);
        }
        else
        {
            // mov r64, imm32 sign-extends; anything else is movabs.
            Mov(r, Imm(value), 8);
        }
    }

    /// <summary>
    /// An integer operand as a register or an immediate, the source side of
    /// an ALU instruction. An immediate there is 32 bits, sign-extended to
    /// the width of the operation, so a 64-bit constant that is not such a
    /// value goes through a register.
    /// </summary>
    private MOperand RM(Operand o, int width)
    {
        if (o is ImmOperand i)
        {
            long v = width == 4 ? unchecked((int)i.Value) : i.Value;
            if (v is >= int.MinValue and <= int.MaxValue)
            {
                return Imm(v);
            }
        }
        return R(o);
    }

    /// <summary>A floating-point operand in an XMM register.</summary>
    private MReg F(Operand o)
    {
        if (o is RegOperand r)
        {
            return V(r.Reg);
        }
        if (o is ImmOperand i)
        {
            // A float constant arrives as its bits.
            MReg bits = Temp();
            LoadConstant(bits, i.Value, 8);
            MReg f = FTemp();
            EmitW(MOp.MovGx, Width(o.Type), f, bits);
            return f;
        }
        throw new InvalidOperationException($"{o} is not a floating-point value");
    }

    /// <summary>The memory an address operand plus displacement names.</summary>
    private MMem Address(Operand addr, long offset)
    {
        int disp = checked((int)offset);
        switch (addr)
        {
            case RegOperand r:
                return new MMem(V(r.Reg), disp);
            case SymOperand s:
                if (External(s.Name))
                {
                    return new MMem(GotAddress(s.Name, 0), checked((int)(s.Offset + disp)));
                }
                return MMem.Rip(s.Name, checked((int)(s.Offset + disp)));
            case SlotOperand s:
                return MMem.Frame(_m.Frame.SlotOffset(s.Slot) + disp);
            case ImmOperand i:
            {
                long at = i.Value + disp;
                if (at is >= int.MinValue and <= int.MaxValue)
                {
                    // [disp32] with no base: the encoder writes it with a SIB
                    // byte, since the plain form means RIP-relative in long mode.
                    return new MMem(null, (int)at);
                }
                MReg t = Temp();
                LoadConstant(t, at, 8);
                return new MMem(t, 0);
            }
            default:
                Error($"cannot address through {addr} of type {addr.Type}");
                return new MMem(null, 0);
        }
    }

    // ---- the walk -------------------------------------------------------------

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
            _constants.Clear();
            for (int n = 0; n < b.Instrs.Count; n++)
            {
                Instr i = b.Instrs[n];
                _line = i.Line;
                NoteConstant(i);
                if (IsCompare(i.Op) && i.Dest is not null && n + 1 < b.Instrs.Count && FusesInto(i, b.Instrs[n + 1]))
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
    /// The parameters, from where the convention put them: the first six
    /// integers and first eight floats in registers, each taken from its
    /// register into a virtual one at once so the allocator is free to put
    /// it anywhere, and the rest from their slots above the return address.
    /// </summary>
    private void LoadParams()
    {
        int ints = 0, floats = 0, stack = 16;
        foreach (VReg p in _f.Params)
        {
            if (p.Type.IsFloat())
            {
                if (floats < FloatArgRegs)
                {
                    EmitW(MOp.MovF, Width(p.Type), V(p), MReg.Xmm(floats++));
                }
                else
                {
                    EmitW(MOp.MovF, Width(p.Type), V(p), MMem.Frame(stack));
                    stack += 8;
                }
            }
            else if (p.Type.IsInt())
            {
                if (ints < IntArgRegs.Length)
                {
                    Mov(V(p), MReg.Of(IntArgRegs[ints++]), Width(p.Type));
                }
                else
                {
                    Mov(V(p), MMem.Frame(stack), Width(p.Type));
                    stack += 8;
                }
            }
            else
            {
                Error($"parameter {p} has type {p.Type}");
            }
        }
    }

    private void NoteConstant(Instr i)
    {
        if (i.Dest is null)
        {
            return;
        }
        if (i.Op == Opcode.Copy && i.Operands[0] is ImmOperand imm && i.Dest.Type.IsInt())
        {
            _constants[i.Dest.Id] = imm.Value;
        }
        else
        {
            _constants.Remove(i.Dest.Id);
        }
    }

    private static bool IsCompare(Opcode op) => op is >= Opcode.Eq and <= Opcode.GeU or >= Opcode.FEq and <= Opcode.FGe;

    private bool FusesInto(Instr cmp, Instr next)
        => next.Op == Opcode.Branch && next.Operands[0] is RegOperand r && r.Reg == cmp.Dest
           && _useCount.GetValueOrDefault(r.Reg.Id) == 1;

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
            {
                int w = Width(i.Dest!.Type);
                MReg d = V(i.Dest);
                Mov(d, RM(i.Operands[0], w), w);
                EmitW(i.Op == Opcode.Neg ? MOp.Neg : MOp.Not, w, d);
                break;
            }
            case Opcode.ByteSwap:
            {
                int w = Width(i.Dest!.Type);
                MReg d = V(i.Dest);
                Mov(d, RM(i.Operands[0], w), w);
                EmitW(MOp.Bswap, w, d);
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
            {
                Cond c = IntCompare(i);
                SetFromFlags(V(i.Dest!), c);
                break;
            }
            case Opcode.FAdd:
            case Opcode.FSub:
            case Opcode.FMul:
            case Opcode.FDiv:
                SelectFloatAlu(i);
                break;
            case Opcode.FNeg:
                SelectFloatNeg(i);
                break;
            case Opcode.FSqrt:
                EmitW(MOp.SqrtF, Width(i.Dest!.Type), V(i.Dest), F(i.Operands[0]));
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
                Mov(V(i.Dest!), RM(i.Operands[0], 4), 4);
                break;
            case Opcode.SExt32:
                if (i.Operands[0] is ImmOperand sextImm)
                {
                    LoadConstant(V(i.Dest!), unchecked((int)sextImm.Value), 8);
                }
                else
                {
                    EmitW(MOp.Movsx, 4, V(i.Dest!), R(i.Operands[0]));
                }
                break;
            case Opcode.ZExt32:
                // A 32-bit move clears the upper half: that IS the extension.
                Mov(V(i.Dest!), RM(i.Operands[0], 4), 4);
                break;
            case Opcode.FConv:
                Emit(new MInstr(MOp.CvtFToF, V(i.Dest!), F(i.Operands[0])) { Width = Width(i.Dest!.Type), SourceWidth = Width(i.Operands[0].Type) });
                break;
            case Opcode.IToF:
                Emit(new MInstr(MOp.CvtIntToF, V(i.Dest!), R(i.Operands[0])) { Width = Width(i.Dest!.Type), SourceWidth = Width(i.Operands[0].Type) });
                break;
            case Opcode.UToF:
                SelectUnsignedToFloat(i);
                break;
            case Opcode.FToI:
            case Opcode.FToU:
                SelectFloatToInteger(i);
                break;
            case Opcode.Bits:
                SelectBits(i);
                break;
            case Opcode.Load:
                SelectLoad(i);
                break;
            case Opcode.ArrayLength:
                Mov(V(i.Dest!), Address(i.Operands[0], Target.Current.ArrayCountOffset), Width(i.Dest!.Type));
                break;
            case Opcode.InitArrayLength:
            {
                int w = Width(i.Operands[1].Type);
                Mov(Address(i.Operands[0], Target.Current.ArrayCountOffset), RM(i.Operands[1], w), w);
                break;
            }
            case Opcode.Store:
                SelectStore(i);
                break;
            case Opcode.MemCopy:
                SelectMemCopy(i);
                break;
            case Opcode.MemSet:
                SelectMemSet(i);
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
                Emit(MOp.Mfence);
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
                Emit(MOp.Ud2);
                break;
            case Opcode.Unwind:
                SelectUnwind(i);
                break;
            case Opcode.LabelAddr:
                Emit(MOp.Lea, V(i.Dest!), new MMem(null, 0) { Label = _heads[i.Targets[0]] });
                break;
            case Opcode.StackPointer:
                Mov(V(i.Dest!), Rsp);
                break;
            case Opcode.FramePointer:
                Mov(V(i.Dest!), Rbp);
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

    // ---- moves and arithmetic -----------------------------------------------------

    private void SelectCopy(Instr i)
    {
        VReg d = i.Dest!;
        Operand s = i.Operands[0];
        if (d.Type.IsFloat())
        {
            if (s is RegOperand r)
            {
                EmitW(MOp.MovF, Width(d.Type), V(d), V(r.Reg));
            }
            else if (s is ImmOperand imm)
            {
                if (imm.Value == 0)
                {
                    MReg z = V(d);
                    EmitW(MOp.XorF, 8, z, z);
                }
                else
                {
                    MReg bits = Temp();
                    LoadConstant(bits, imm.Value, 8);
                    EmitW(MOp.MovGx, Width(d.Type), V(d), bits);
                }
            }
            else
            {
                Error($"copy of {s} into a float");
            }
            return;
        }
        int w = Width(d.Type);
        switch (s)
        {
            case ImmOperand imm:
                if ((w == 4 ? unchecked((int)imm.Value) : imm.Value) == 0)
                {
                    MReg z = V(d);
                    EmitW(MOp.Xor, 4, z, z);
                }
                else
                {
                    LoadConstant(V(d), imm.Value, w);
                }
                break;
            case SymOperand sym:
                if (External(sym.Name))
                {
                    GotAddress(sym.Name, sym.Offset, V(d));
                    break;
                }
                Emit(MOp.Lea, V(d), MMem.Rip(sym.Name, checked((int)sym.Offset)));
                break;
            case SlotOperand slot:
                Emit(MOp.Lea, V(d), MMem.Frame(_m.Frame.SlotOffset(slot.Slot)));
                break;
            default:
                Mov(V(d), R(s), w);
                break;
        }
    }

    private static bool Aliases(VReg? dest, Operand o) => dest is not null && o is RegOperand r && r.Reg == dest;

    private void SelectAlu(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        MOp op = i.Op switch
        {
            Opcode.Add => MOp.Add,
            Opcode.Sub => MOp.Sub,
            Opcode.And => MOp.And,
            Opcode.Or => MOp.Or,
            _ => MOp.Xor,
        };
        Operand a = i.Operands[0], b = i.Operands[1];

        // An address plus a constant, or two registers added, is one LEA and
        // no move: the three-operand form x86 has always had.
        if (op == MOp.Add && w == 8 && a is RegOperand ar && !Aliases(d, a))
        {
            if (b is ImmOperand bi && bi.Value is >= int.MinValue and <= int.MaxValue)
            {
                Emit(MOp.Lea, V(d), new MMem(V(ar.Reg), (int)bi.Value));
                return;
            }
            if (b is RegOperand br && !Aliases(d, b))
            {
                Emit(MOp.Lea, V(d), new MMem(V(ar.Reg), 0) { Index = V(br.Reg) });
                return;
            }
        }

        MOperand rhs = RM(b, w);
        if (Aliases(d, b) && !Aliases(d, a))
        {
            // d = a op d: the right operand would be overwritten by the move.
            MReg t = Temp();
            Mov(t, rhs, w);
            rhs = t;
        }
        MReg dr = V(d);
        Mov(dr, RM(a, w), w);
        EmitW(op, w, dr, rhs);
    }

    private void SelectMul(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        Operand a = i.Operands[0], b = i.Operands[1];
        if (b is ImmOperand imm && (w == 4 || imm.Value is >= int.MinValue and <= int.MaxValue))
        {
            Emit(new MInstr(MOp.Imul3, V(d), R(a), Imm(w == 4 ? unchecked((int)imm.Value) : imm.Value)) { Width = w });
            return;
        }
        if (a is ImmOperand aimm && (w == 4 || aimm.Value is >= int.MinValue and <= int.MaxValue))
        {
            Emit(new MInstr(MOp.Imul3, V(d), R(b), Imm(w == 4 ? unchecked((int)aimm.Value) : aimm.Value)) { Width = w });
            return;
        }
        MReg rhs = R(b);
        if (Aliases(d, b) && !Aliases(d, a))
        {
            MReg t = Temp();
            Mov(t, rhs, w);
            rhs = t;
        }
        MReg dr = V(d);
        Mov(dr, R(a), w);
        EmitW(MOp.Imul, w, dr, rhs);
    }

    /// <summary>
    /// IDIV and DIV, at the width of the operation: RDX:RAX over the operand.
    /// A zero divisor, and the most negative value over minus one, trap as the
    /// hardware traps -- which is what the runtime turns into the exception
    /// C# promises, exactly as on x86.
    /// </summary>
    private void SelectDiv(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        bool signed = i.Op is Opcode.DivS or Opcode.RemS;
        bool rem = i.Op is Opcode.RemS or Opcode.RemU;
        if (i.Operands[1] is ImmOperand constant && SelectDivByConstant(i, constant.Value, w, signed, rem))
        {
            return;
        }
        MReg divisor = Temp();
        Mov(divisor, RM(i.Operands[1], w), w);
        Mov(Rax, RM(i.Operands[0], w), w);
        if (signed)
        {
            EmitW(MOp.Cwd, w);
        }
        else
        {
            EmitW(MOp.Xor, 4, Rdx, Rdx);
        }
        EmitW(signed ? MOp.Idiv : MOp.Div, w, divisor);
        Mov(V(d), rem ? Rdx : Rax, w);
    }

    /// <summary>
    /// DIVISION BY A CONSTANT, AS A MULTIPLICATION: the high half of the
    /// dividend times a magic number, shifted, which is what every compiler
    /// since Granlund and Montgomery (1994) does -- a multiply is a few cycles
    /// where a 64-bit DIV on a K8 is dozens. Unsigned uses their round-up
    /// method with the one-bit fix-up that makes it exact for every dividend;
    /// signed, Hacker's Delight's (10-1). The remainder is n - q*d. Zero, one
    /// and minus one, and the most negative value, are left to DIV, whose
    /// trap or identity is the answer they need.
    /// </summary>
    private bool SelectDivByConstant(Instr i, long value, int w, bool signed, bool rem)
    {
        int bits = w * 8;
        long divisor = w == 4 ? (signed ? unchecked((int)value) : (long)(uint)value) : value;
        if (divisor == 0 || divisor == 1 || (signed && divisor == -1))
        {
            return false;
        }
        if (signed && divisor == (w == 4 ? int.MinValue : long.MinValue))
        {
            return false;
        }
        if (!signed && w == 8 && divisor < 0)
        {
            // An unsigned 64-bit divisor at or above 2^63: the quotient is 0 or 1.
            return false;
        }

        VReg d = i.Dest!;
        MReg n = Temp();
        Mov(n, RM(i.Operands[0], w), w);
        MReg q = Temp();

        if (!signed)
        {
            ulong ud = (ulong)divisor;
            int l = 0;
            while (l < bits && (UInt128.One << l) < ud)
            {
                l++;
            }
            UInt128 magic = ((UInt128.One << bits) * ((UInt128.One << l) - ud)) / ud + 1;
            MulHigh(q, n, unchecked((long)(ulong)magic), w, signedMultiply: false);
            // t = (n - q) >> 1; q = (t + q) >> (l - 1)
            MReg t = Temp();
            Mov(t, n, w);
            EmitW(MOp.Sub, w, t, q);
            EmitW(MOp.Shr, w, t, Imm(1));
            EmitW(MOp.Add, w, q, t);
            if (l > 1)
            {
                EmitW(MOp.Shr, w, q, Imm(l - 1));
            }
        }
        else
        {
            (long magic, int shift) = SignedMagic(divisor, bits);
            MulHigh(q, n, magic, w, signedMultiply: true);
            if (divisor > 0 && magic < 0)
            {
                EmitW(MOp.Add, w, q, n);
            }
            else if (divisor < 0 && magic > 0)
            {
                EmitW(MOp.Sub, w, q, n);
            }
            if (shift > 0)
            {
                EmitW(MOp.Sar, w, q, Imm(shift));
            }
            // Toward zero: one more when the quotient came out negative.
            MReg sign = Temp();
            Mov(sign, q, w);
            EmitW(MOp.Shr, w, sign, Imm(bits - 1));
            EmitW(MOp.Add, w, q, sign);
        }

        if (!rem)
        {
            Mov(V(d), q, w);
            return true;
        }
        MReg product = Temp();
        if (divisor is >= int.MinValue and <= int.MaxValue)
        {
            Emit(new MInstr(MOp.Imul3, product, q, Imm(divisor)) { Width = w });
        }
        else
        {
            Mov(product, q, w);
            MReg k = Temp();
            LoadConstant(k, divisor, 8);
            EmitW(MOp.Imul, w, product, k);
        }
        MReg r = Temp();
        Mov(r, n, w);
        EmitW(MOp.Sub, w, r, product);
        Mov(V(d), r, w);
        return true;
    }

    /// <summary>
    /// The high half of n times a constant: for 32 bits, the exact 64-bit
    /// product of the widened operands, shifted down; for 64 bits, the upper
    /// word MUL or IMUL leaves in RDX.
    /// </summary>
    private void MulHigh(MReg into, MReg n, long magic, int w, bool signedMultiply)
    {
        if (w == 4)
        {
            MReg wide = Temp(), m = Temp();
            if (signedMultiply)
            {
                EmitW(MOp.Movsx, 4, wide, n);
                LoadConstant(m, unchecked((int)magic), 8);
                EmitW(MOp.Imul, 8, wide, m);
                EmitW(MOp.Sar, 8, wide, Imm(32));
            }
            else
            {
                Mov(wide, n, 4);
                LoadConstant(m, (long)(uint)magic, 8);
                EmitW(MOp.Imul, 8, wide, m);
                EmitW(MOp.Shr, 8, wide, Imm(32));
            }
            Mov(into, wide, 4);
            return;
        }
        MReg k = Temp();
        LoadConstant(k, magic, 8);
        Mov(Rax, n, 8);
        EmitW(signedMultiply ? MOp.ImulWide : MOp.MulWide, 8, k);
        Mov(into, Rdx, 8);
    }

    /// <summary>
    /// The signed magic number and shift for dividing by d in `bits`-bit
    /// arithmetic: Hacker's Delight, figure 10-1, in 128-bit arithmetic so one
    /// routine serves both widths.
    /// </summary>
    private static (long Magic, int Shift) SignedMagic(long d, int bits)
    {
        UInt128 two = UInt128.One << (bits - 1);
        UInt128 mask = (UInt128.One << bits) - 1;
        UInt128 ad = (UInt128)(ulong)Math.Abs(d);
        UInt128 t = two + (d < 0 ? UInt128.One : UInt128.Zero);
        UInt128 anc = t - 1 - t % ad;
        int p = bits - 1;
        UInt128 q1 = two / anc, r1 = two - q1 * anc;
        UInt128 q2 = two / ad, r2 = two - q2 * ad;
        UInt128 delta;
        do
        {
            p++;
            q1 = (2 * q1) & mask;
            r1 = (2 * r1) & mask;
            if (r1 >= anc)
            {
                q1 = (q1 + 1) & mask;
                r1 = (r1 - anc) & mask;
            }
            q2 = (2 * q2) & mask;
            r2 = (2 * r2) & mask;
            if (r2 >= ad)
            {
                q2 = (q2 + 1) & mask;
                r2 = (r2 - ad) & mask;
            }
            delta = ad - r2;
        }
        while (q1 < delta || (q1 == delta && r1 == 0));
        UInt128 magic = (q2 + 1) & mask;
        long m = bits == 32 ? unchecked((int)(uint)magic) : unchecked((long)(ulong)magic);
        if (d < 0)
        {
            m = bits == 32 ? unchecked(-(int)m) : unchecked(-m);
        }
        return (m, p - bits);
    }

    /// <summary>
    /// The count is masked by the operand width, which is exactly what the
    /// hardware does with CL at both widths: C#'s rule and the machine's agree.
    /// </summary>
    private void SelectShift(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        MOp op = i.Op switch
        {
            Opcode.Shl => MOp.Shl,
            Opcode.ShrS => MOp.Sar,
            _ => MOp.Shr,
        };
        Operand a = i.Operands[0], count = i.Operands[1];
        if (count is ImmOperand c)
        {
            MReg dr = V(d);
            Mov(dr, RM(a, w), w);
            EmitW(op, w, dr, Imm(c.Value & (w * 8 - 1)));
            return;
        }
        MReg n = Temp();
        Mov(n, R(count), 4);
        MReg value = Temp();
        Mov(value, RM(a, w), w);
        Mov(Rcx, n, 4);
        EmitW(op, w, value, Rcx);
        Mov(V(d), value, w);
    }

    private void SelectNarrow(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        MReg src = R(i.Operands[0]);
        switch (i.Op)
        {
            case Opcode.SExt8:
                EmitW(MOp.Movsx, 1, V(d), src);
                if (w == 4) Mov(V(d), V(d), 4);
                break;
            case Opcode.SExt16:
                EmitW(MOp.Movsx, 2, V(d), src);
                if (w == 4) Mov(V(d), V(d), 4);
                break;
            case Opcode.ZExt8:
                EmitW(MOp.Movzx, 1, V(d), src);
                break;
            default:
                EmitW(MOp.Movzx, 2, V(d), src);
                break;
        }
    }

    // ---- comparisons -------------------------------------------------------------------

    private Cond IntCompare(Instr i)
    {
        int w = Width(i.Operands[0].Type);
        Operand a = i.Operands[0], b = i.Operands[1];
        Cond c = i.Op switch
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
        if (a is ImmOperand && b is not ImmOperand)
        {
            (a, b) = (b, a);
            c = c.Swap();
        }
        MReg ar = R(a);
        MOperand br = RM(b, w);
        if (br is MImm { Value: 0 } && c is Cond.E or Cond.Ne)
        {
            EmitW(MOp.Test, w, ar, ar);
        }
        else
        {
            EmitW(MOp.Cmp, w, ar, br);
        }
        return c;
    }

    /// <summary>A 0 or 1 from the flags: SETcc into the low byte, then zero-extended.</summary>
    private void SetFromFlags(MReg d, Cond c)
    {
        MReg b = Temp();
        Emit(new MInstr(MOp.Setcc, b) { Cond = c, Width = 1 });
        EmitW(MOp.Movzx, 1, d, b);
    }

    /// <summary>
    /// UCOMISS/UCOMISD sets ZF, PF and CF: CF for below, ZF for equal, all
    /// three for unordered. `a &lt; b` is asked as `b above a`, so that an
    /// unordered pair -- which sets CF -- answers false as C# requires.
    /// </summary>
    private (Cond Cond, bool ParityFalse, bool ParityTrue) FloatCompare(Instr i)
    {
        int w = Width(i.Operands[0].Type);
        MReg a = F(i.Operands[0]);
        MReg b = F(i.Operands[1]);
        switch (i.Op)
        {
            case Opcode.FEq:
                EmitW(MOp.UcomiF, w, a, b);
                return (Cond.E, true, false);
            case Opcode.FNe:
                EmitW(MOp.UcomiF, w, a, b);
                return (Cond.Ne, false, true);
            case Opcode.FLt:
                EmitW(MOp.UcomiF, w, b, a);
                return (Cond.A, false, false);
            case Opcode.FLe:
                EmitW(MOp.UcomiF, w, b, a);
                return (Cond.Ae, false, false);
            case Opcode.FGt:
                EmitW(MOp.UcomiF, w, a, b);
                return (Cond.A, false, false);
            default:
                EmitW(MOp.UcomiF, w, a, b);
                return (Cond.Ae, false, false);
        }
    }

    private void SelectFloatCompare(Instr i)
    {
        MReg d = V(i.Dest!);
        (Cond c, bool parityFalse, bool parityTrue) = FloatCompare(i);
        if (!parityFalse && !parityTrue)
        {
            SetFromFlags(d, c);
            return;
        }
        // Equal needs "ZF and not PF"; not-equal "not ZF or PF".
        MReg x = Temp(), y = Temp();
        Emit(new MInstr(MOp.Setcc, x) { Cond = c, Width = 1 });
        Emit(new MInstr(MOp.Setcc, y) { Cond = parityFalse ? Cond.Np : Cond.P, Width = 1 });
        EmitW(parityFalse ? MOp.And : MOp.Or, 1, x, y);
        EmitW(MOp.Movzx, 1, d, x);
    }

    // ---- floating point ------------------------------------------------------------

    private void SelectFloatAlu(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        MOp op = i.Op switch
        {
            Opcode.FAdd => MOp.AddF,
            Opcode.FSub => MOp.SubF,
            Opcode.FMul => MOp.MulF,
            _ => MOp.DivF,
        };
        MReg a = F(i.Operands[0]);
        MReg b = F(i.Operands[1]);
        if (Aliases(d, i.Operands[1]) && !Aliases(d, i.Operands[0]))
        {
            MReg t = FTemp();
            EmitW(MOp.MovF, w, t, b);
            b = t;
        }
        MReg dr = V(d);
        EmitW(MOp.MovF, w, dr, a);
        EmitW(op, w, dr, b);
    }

    /// <summary>Negation flips the sign bit and nothing else, NaN payloads included, as x87's FCHS does.</summary>
    private void SelectFloatNeg(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        MReg bits = Temp();
        EmitW(MOp.MovGx, w, bits, F(i.Operands[0]));
        if (w == 4)
        {
            EmitW(MOp.Xor, 4, bits, Imm(int.MinValue));
        }
        else
        {
            MReg sign = Temp();
            Mov(sign, Imm(long.MinValue), 8);
            EmitW(MOp.Xor, 8, bits, sign);
        }
        EmitW(MOp.MovGx, w, V(d), bits);
    }

    /// <summary>
    /// Unsigned to float. A 32-bit value is a non-negative 64-bit one, which
    /// the signed conversion takes exactly. A 64-bit value with its top bit
    /// set is halved -- keeping the lowest bit, so rounding comes out as it
    /// would for the whole number -- converted, and doubled.
    /// </summary>
    private void SelectUnsignedToFloat(Instr i)
    {
        VReg d = i.Dest!;
        int fw = Width(d.Type);
        if (i.Operands[0].Type == IrType.I32)
        {
            MReg wide = Temp();
            Mov(wide, RM(i.Operands[0], 4), 4);
            Emit(new MInstr(MOp.CvtIntToF, V(d), wide) { Width = fw, SourceWidth = 8 });
            return;
        }
        MReg s = R(i.Operands[0]);
        MReg dr = V(d);
        MBlock big = Aside();
        MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", big);
        EmitW(MOp.Test, 8, s, s);
        Jcc(Cond.S, big);
        Emit(new MInstr(MOp.CvtIntToF, dr, s) { Width = fw, SourceWidth = 8 });
        Jmp(done);
        _cur = big;
        MReg half = Temp(), low = Temp();
        Mov(half, s, 8);
        EmitW(MOp.Shr, 8, half, Imm(1));
        Mov(low, s, 8);
        EmitW(MOp.And, 8, low, Imm(1));
        EmitW(MOp.Or, 8, half, low);
        Emit(new MInstr(MOp.CvtIntToF, dr, half) { Width = fw, SourceWidth = 8 });
        EmitW(MOp.AddF, fw, dr, dr);
        _cur = done;
    }

    /// <summary>
    /// Float to integer as .NET converts since it made the conversion the same
    /// on every machine: NaN is zero, a value beyond the destination's range
    /// is its limit, and a negative value into an unsigned destination is
    /// zero -- the i386 backend's rule, decided the same way, from the IEEE
    /// bits before any conversion instruction runs. In range, the truncating
    /// conversion below answers.
    /// </summary>
    private void SelectFloatToInteger(Instr i)
    {
        VReg d = i.Dest!;
        bool wide = d.Type == IrType.I64, unsigned = i.Op == Opcode.FToU;
        int fw = Width(i.Operands[0].Type);
        bool single = fw == 4;
        MReg x = F(i.Operands[0]);
        MReg bits = Temp(), magnitude = Temp(), limit = Temp(), dr = V(d);
        int dw = wide ? 8 : 4;

        EmitW(MOp.MovGx, fw, bits, x);
        Mov(magnitude, bits, fw);
        if (single)
        {
            EmitW(MOp.And, 4, magnitude, Imm(int.MaxValue));
        }
        else
        {
            MReg mask = Temp();
            Mov(mask, Imm(long.MaxValue), 8);
            EmitW(MOp.And, 8, magnitude, mask);
        }

        MBlock anchor = _cur;
        MBlock New(string name) { MBlock b = _m.NewBlock($"{name}{_splits++}", anchor); anchor = b; return b; }
        MBlock finite = New("cast-finite"), negative = New("cast-negative"), zero = New("cast-zero"),
            maximum = New("cast-maximum"), minimum = New("cast-minimum"), normal = New("cast-normal"),
            done = New("cast-done");

        // NaN: above the infinity's bits.
        LoadConstant(limit, single ? 0x7f800000 : 0x7ff0000000000000, 8);
        EmitW(MOp.Cmp, fw, magnitude, limit);
        Jcc(Cond.A, zero);
        Jmp(finite);

        _cur = finite;
        EmitW(MOp.Test, fw, bits, bits);
        Jcc(Cond.S, unsigned ? zero : negative);
        // 2^31, 2^32, 2^63 or 2^64 in the source format: the first value
        // that does not fit.
        long bound = single
            ? (wide ? (unsigned ? 0x5f800000 : 0x5f000000) : (unsigned ? 0x4f800000 : 0x4f000000))
            : (wide ? (unsigned ? 0x43f0000000000000 : 0x43e0000000000000) : (unsigned ? 0x41f0000000000000 : 0x41e0000000000000));
        LoadConstant(limit, bound, 8);
        EmitW(MOp.Cmp, fw, magnitude, limit);
        Jcc(Cond.Ae, maximum);
        Jmp(normal);

        _cur = negative;
        EmitW(MOp.Cmp, fw, magnitude, limit);
        Jcc(Cond.Ae, minimum);
        Jmp(normal);

        _cur = zero;
        EmitW(MOp.Xor, 4, dr, dr);
        Jmp(done);

        _cur = maximum;
        LoadConstant(dr, wide ? (unsigned ? -1L : long.MaxValue) : (unsigned ? 0xFFFFFFFFL : int.MaxValue), dw);
        Jmp(done);

        _cur = minimum;
        LoadConstant(dr, wide ? long.MinValue : int.MinValue, dw);
        Jmp(done);

        _cur = normal;
        if (!unsigned)
        {
            Emit(new MInstr(MOp.CvtFToInt, dr, x) { Width = dw, SourceWidth = fw });
        }
        else if (!wide)
        {
            // Below 2^32: the 64-bit signed conversion holds it, and its low
            // half is the answer.
            MReg wideResult = Temp();
            Emit(new MInstr(MOp.CvtFToInt, wideResult, x) { Width = 8, SourceWidth = fw });
            Mov(dr, wideResult, 4);
        }
        else
        {
            // Below 2^64: at or above 2^63 the value comes down by 2^63 first
            // (exact, a power of two) and the top bit goes back on after.
            MReg limitBits = Temp();
            LoadConstant(limitBits, single ? 0x5F000000 : 0x43E0000000000000, 8);
            MReg half = FTemp();
            EmitW(MOp.MovGx, fw, half, limitBits);
            MBlock big = New("cast-unsigned-high");
            EmitW(MOp.UcomiF, fw, x, half);
            Jcc(Cond.Ae, big);
            Emit(new MInstr(MOp.CvtFToInt, dr, x) { Width = 8, SourceWidth = fw });
            Jmp(done);
            _cur = big;
            MReg reduced = FTemp();
            EmitW(MOp.MovF, fw, reduced, x);
            EmitW(MOp.SubF, fw, reduced, half);
            Emit(new MInstr(MOp.CvtFToInt, dr, reduced) { Width = 8, SourceWidth = fw });
            MReg top = Temp();
            Mov(top, Imm(long.MinValue), 8);
            EmitW(MOp.Or, 8, dr, top);
        }
        Jmp(done);
        _cur = done;
    }

    private void SelectBits(Instr i)
    {
        VReg d = i.Dest!;
        Operand s = i.Operands[0];
        int w = Width(d.Type);
        if (d.Type.IsFloat())
        {
            EmitW(MOp.MovGx, w, V(d), R(s));
        }
        else
        {
            EmitW(MOp.MovGx, w, V(d), F(s));
        }
    }

    // ---- memory --------------------------------------------------------------------

    private void SelectLoad(Instr i)
    {
        VReg d = i.Dest!;
        MMem m = Address(i.Operands[0], i.Offset);
        if (d.Type.IsFloat())
        {
            int fw = Width(d.Type);
            if (i.Size == fw)
            {
                EmitW(MOp.MovF, fw, V(d), m);
            }
            else
            {
                MReg t = FTemp();
                EmitW(MOp.MovF, i.Size, t, m);
                Emit(new MInstr(MOp.CvtFToF, V(d), t) { Width = fw, SourceWidth = i.Size });
            }
            return;
        }
        MReg r = V(d);
        switch (i.Size)
        {
            case 1:
            case 2:
                if (i.Signed)
                {
                    EmitW(MOp.Movsx, i.Size, r, m);
                    if (d.Type == IrType.I32) Mov(r, r, 4);
                }
                else
                {
                    EmitW(MOp.Movzx, i.Size, r, m);
                }
                break;
            case 4:
                if (d.Type == IrType.I64 && i.Signed)
                {
                    EmitW(MOp.Movsx, 4, r, m);
                }
                else
                {
                    Mov(r, m, 4);
                }
                break;
            case 8:
                if (d.Type != IrType.I64)
                {
                    Error("8-byte load into a 32-bit register");
                    return;
                }
                Mov(r, m, 8);
                break;
            default:
                Error($"load of size {i.Size}");
                break;
        }
    }

    private void SelectStore(Instr i)
    {
        MMem m = Address(i.Operands[0], i.Offset);
        Operand v = i.Operands[1];
        if (v.Type.IsFloat())
        {
            int fw = Width(v.Type);
            MReg x = F(v);
            if (i.Size == fw)
            {
                EmitW(MOp.MovF, fw, m, x);
            }
            else
            {
                MReg t = FTemp();
                Emit(new MInstr(MOp.CvtFToF, t, x) { Width = i.Size, SourceWidth = fw });
                EmitW(MOp.MovF, i.Size, m, t);
            }
            return;
        }
        if (i.Size is not (1 or 2 or 4 or 8))
        {
            Error($"store of size {i.Size}");
            return;
        }
        MOperand src = RM(v, i.Size == 8 ? 8 : 4);
        if (src is MImm imm && i.Size < 4)
        {
            src = Imm(imm.Value & (i.Size == 1 ? 0xFF : 0xFFFF));
        }
        Mov(m, src, i.Size);
    }

    /// <summary>The most bytes a constant clear or copy is written out as moves.</summary>
    private const long InlineBytes = 128;

    /// <summary>Stores of 8, 4, 2 and 1 bytes covering [at, at + count), each from `value(width)`.</summary>
    private void Pieces(long count, Action<int, int> piece)
    {
        int offset = 0;
        foreach (int width in new[] { 8, 4, 2, 1 })
        {
            while (count - offset >= width)
            {
                piece(offset, width);
                offset += width;
            }
        }
    }

    /// <summary>Eight bytes at a time and the tail a byte at a time, as x86 does four and one.</summary>
    private void SelectMemCopy(Instr i)
    {
        MReg dst = R(i.Operands[0]);
        MReg src = R(i.Operands[1]);
        if (i.Operands[2] is ImmOperand { Value: >= 0 and <= InlineBytes } small)
        {
            // A small copy of known length is loads and stores, in order: a
            // string instruction's start-up costs more than the whole copy.
            MReg t = Temp();
            Pieces(small.Value, (offset, width) =>
            {
                if (width >= 4)
                {
                    Mov(t, new MMem(src, offset), width);
                }
                else
                {
                    EmitW(MOp.Movzx, width, t, new MMem(src, offset));
                }
                Mov(new MMem(dst, offset), t, width);
            });
            return;
        }
        if (i.Operands[2] is ImmOperand n)
        {
            Mov(Rdi, dst);
            Mov(Rsi, src);
            if (n.Value / 8 > 0)
            {
                LoadConstant(Rcx, n.Value / 8, 8);
                Emit(MOp.RepMovsq);
            }
            if (n.Value % 8 > 0)
            {
                LoadConstant(Rcx, n.Value % 8, 8);
                Emit(MOp.RepMovsb);
            }
            return;
        }
        MReg count = R(i.Operands[2]);
        MReg remainder = Temp();
        Mov(remainder, count, 8);
        Mov(Rdi, dst);
        Mov(Rsi, src);
        Mov(Rcx, count, 8);
        EmitW(MOp.Shr, 8, Rcx, Imm(3));
        Emit(MOp.RepMovsq);
        EmitW(MOp.And, 4, remainder, Imm(7));
        Mov(Rcx, remainder, 8);
        Emit(MOp.RepMovsb);
    }

    private void SelectMemSet(Instr i)
    {
        MReg dst = R(i.Operands[0]);
        if (i.Operands[2] is ImmOperand { Value: >= 0 and <= InlineBytes } small && i.Operands[1] is ImmOperand smallFill)
        {
            // A small clear of known length is stores, of an immediate when the
            // repeated byte fits one (zero always does), else of a register.
            ulong pattern = (byte)smallFill.Value * 0x0101010101010101UL;
            MOperand source;
            if ((long)pattern is >= int.MinValue and <= int.MaxValue)
            {
                source = Imm((long)pattern);
            }
            else
            {
                MReg p = Temp();
                LoadConstant(p, unchecked((long)pattern), 8);
                source = p;
            }
            Pieces(small.Value, (offset, width) =>
            {
                MOperand value = source is MImm imm ? Imm(width == 8 ? imm.Value : imm.Value & ((1L << (width * 8)) - 1)) : source;
                if (value is MImm narrow && width == 4)
                {
                    value = Imm(unchecked((int)narrow.Value));
                }
                Mov(new MMem(dst, offset), value, width);
            });
            return;
        }
        MReg fill = Temp();
        if (i.Operands[1] is ImmOperand f)
        {
            ulong repeated = (byte)f.Value * 0x0101010101010101UL;
            LoadConstant(fill, unchecked((long)repeated), 8);
        }
        else
        {
            MReg ones = Temp();
            EmitW(MOp.Movzx, 1, fill, R(i.Operands[1]));
            Mov(ones, Imm(0x0101010101010101L), 8);
            EmitW(MOp.Imul, 8, fill, ones);
        }
        if (i.Operands[2] is ImmOperand n)
        {
            Mov(Rdi, dst);
            Mov(Rax, fill);
            if (n.Value / 8 > 0)
            {
                LoadConstant(Rcx, n.Value / 8, 8);
                Emit(MOp.RepStosq);
            }
            if (n.Value % 8 > 0)
            {
                LoadConstant(Rcx, n.Value % 8, 8);
                Emit(MOp.RepStosb);
            }
            return;
        }
        MReg count = R(i.Operands[2]);
        MReg remainder = Temp();
        Mov(remainder, count, 8);
        Mov(Rdi, dst);
        Mov(Rax, fill);
        Mov(Rcx, count, 8);
        EmitW(MOp.Shr, 8, Rcx, Imm(3));
        Emit(MOp.RepStosq);
        EmitW(MOp.And, 4, remainder, Imm(7));
        Mov(Rcx, remainder, 8);
        Emit(MOp.RepStosb);
    }

    /// <summary>
    /// Every atomic at 32 or 64 bits: the K8 has the 64-bit LOCK XADD, XCHG
    /// and CMPXCHG the 486 lacked. And, or and xor have no fetching form, so
    /// they are a compare-exchange loop.
    /// </summary>
    private void SelectAtomic(Instr i)
    {
        VReg d = i.Dest!;
        int w = Width(d.Type);
        MMem m = Address(i.Operands[0], 0);
        switch (i.Op)
        {
            case Opcode.AtomicSwap:
            {
                MReg t = Temp();
                Mov(t, RM(i.Operands[1], w), w);
                EmitW(MOp.Xchg, w, m, t);
                Mov(V(d), t, w);
                return;
            }
            case Opcode.AtomicAdd:
            {
                MReg t = Temp();
                Mov(t, RM(i.Operands[1], w), w);
                Emit(new MInstr(MOp.Xadd, m, t) { Width = w, Lock = true });
                Mov(V(d), t, w);
                return;
            }
            case Opcode.AtomicCas:
            {
                MReg value = R(i.Operands[2]);
                Mov(Rax, RM(i.Operands[1], w), w);
                Emit(new MInstr(MOp.Cmpxchg, m, value) { Width = w, Lock = true });
                Mov(V(d), Rax, w);
                return;
            }
            default:
            {
                MOp op = i.Op switch
                {
                    Opcode.AtomicAnd => MOp.And,
                    Opcode.AtomicOr => MOp.Or,
                    _ => MOp.Xor,
                };
                MOperand v = RM(i.Operands[1], w);
                MReg next = Temp();
                Mov(Rax, m, w);
                MBlock loop = Aside();
                MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", loop);
                _cur = loop;
                Mov(next, Rax, w);
                EmitW(op, w, next, v);
                Emit(new MInstr(MOp.Cmpxchg, m, next) { Width = w, Lock = true });
                Jcc(Cond.Ne, loop);
                _cur = done;
                Mov(V(d), Rax, w);
                return;
            }
        }
    }

    // ---- calls -------------------------------------------------------------------------

    private void SelectCall(Instr i)
    {
        VReg? d = i.Dest;
        if (i.Op == Opcode.Call && i.Callee == "__exception")
        {
            // The head of a landing pad: the unwinder arrived with the
            // exception in RAX and nothing else defined.
            if (d is not null)
            {
                Mov(V(d), Rax);
            }
            return;
        }
        if (i.Op == Opcode.Call && i.Callee is { } callee
            && callee.StartsWith(Corsac.Lang.X86.MachineIntrinsics.Prefix, StringComparison.Ordinal))
        {
            SelectMachineIntrinsic(callee, i);
            return;
        }

        int first = i.Op == Opcode.CallIndirect ? 1 : 0;
        List<(Operand Value, int Reg)> inRegs = new();
        List<Operand> onStack = new();
        int ints = 0, floats = 0;
        for (int k = first; k < i.Operands.Count; k++)
        {
            Operand o = i.Operands[k];
            if (o.Type.IsFloat())
            {
                if (floats < FloatArgRegs) inRegs.Add((o, MReg.XmmBase + floats++));
                else onStack.Add(o);
            }
            else
            {
                if (ints < IntArgRegs.Length) inRegs.Add((o, (int)IntArgRegs[ints++]));
                else onStack.Add(o);
            }
        }

        MReg? target = i.Op == Opcode.CallIndirect ? R(i.Operands[0]) : null;

        // THE STACK ARGUMENTS FIRST, last to first, with a pad beneath them
        // when there is an odd number, so RSP is sixteen-byte aligned at the
        // call as it was at the frame's bottom.
        int pushed = onStack.Count * 8;
        int pad = onStack.Count % 2 == 1 ? 8 : 0;
        if (pad != 0)
        {
            EmitW(MOp.Sub, 8, Rsp, Imm(8));
        }
        for (int k = onStack.Count - 1; k >= 0; k--)
        {
            Operand o = onStack[k];
            if (o.Type.IsFloat())
            {
                EmitW(MOp.Sub, 8, Rsp, Imm(8));
                EmitW(MOp.MovF, Width(o.Type), new MMem(Rsp, 0), F(o));
            }
            else
            {
                Emit(MOp.Push, RM(o, 8));
            }
        }

        // The register arguments are evaluated into virtual registers first
        // and only then moved to their fixed homes: evaluating one may need a
        // temporary, and none of those homes may be taken while it is made.
        List<(MReg Value, int Reg, int Width)> moves = new();
        foreach ((Operand o, int reg) in inRegs)
        {
            if (o.Type.IsFloat())
            {
                moves.Add((F(o), reg, Width(o.Type)));
            }
            else
            {
                moves.Add((R(o), reg, 8));
            }
        }
        foreach ((MReg value, int reg, int width) in moves)
        {
            if (reg >= MReg.XmmBase)
            {
                EmitW(MOp.MovF, width, MReg.Xmm(reg - MReg.XmmBase), value);
            }
            else
            {
                Mov(new MReg(reg), value, 8);
            }
        }

        if (target is null)
        {
            Emit(new MInstr(MOp.Call, MImm.Sym(i.Callee!, 0)) { IntArgs = ints, FloatArgs = floats });
        }
        else
        {
            // R11 is a scratch register no argument uses; the target goes
            // there so no argument register has to be kept away from it.
            MReg r11 = MReg.Of(Gpr.R11);
            Mov(r11, target, 8);
            Emit(new MInstr(MOp.CallInd, r11) { IntArgs = ints, FloatArgs = floats });
        }
        if (pushed + pad > 0)
        {
            EmitW(MOp.Add, 8, Rsp, Imm(pushed + pad));
        }
        if (d is null)
        {
            return;
        }
        if (d.Type.IsFloat())
        {
            EmitW(MOp.MovF, Width(d.Type), V(d), Xmm0);
        }
        else
        {
            Mov(V(d), Rax, Width(d.Type));
        }
    }

    private void SelectRet(Instr i)
    {
        int ints = 0, floats = 0;
        if (i.Operands.Count > 0)
        {
            Operand v = i.Operands[0];
            if (v.Type.IsFloat())
            {
                EmitW(MOp.MovF, Width(v.Type), Xmm0, F(v));
                floats = 1;
            }
            else
            {
                int w = Width(v.Type);
                MOperand src = RM(v, w);
                Mov(Rax, src, w);
                ints = 1;
            }
        }
        Emit(new MInstr(MOp.Epilogue) { IntArgs = ints, FloatArgs = floats });
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
        int w = Width(c.Type);
        EmitW(MOp.Test, w, r, r);
        Jcc(Cond.Ne, t);
        Jmp(f);
    }

    private void SelectFusedBranch(Instr cmp, Instr br)
    {
        MBlock t = _heads[br.Targets[0]];
        MBlock f = _heads[br.Targets[1]];
        if (cmp.Op is >= Opcode.FEq and <= Opcode.FGe)
        {
            (Cond fc, bool parityFalse, bool parityTrue) = FloatCompare(cmp);
            if (parityFalse)
            {
                Jcc(Cond.P, f);
            }
            else if (parityTrue)
            {
                Jcc(Cond.P, t);
            }
            Jcc(fc, t);
            Jmp(f);
            return;
        }
        Cond c = IntCompare(cmp);
        Jcc(c, t);
        Jmp(f);
    }

    private void SelectSwitch(Instr i)
    {
        MReg idx = Temp();
        Mov(idx, RM(i.Operands[0], 4), 4);
        EmitW(MOp.Cmp, 4, idx, Imm(i.Targets.Count));
        Jcc(Cond.Ae, _heads[i.Default!]);
        List<MBlock> table = i.Targets.Select(b => _heads[b]).ToList();
        // The table's address in a register of its own, loaded by an
        // instruction before the jump: the jump reads both it and the index,
        // so the allocator keeps them apart.
        string symbol = $"{_f.Name}$table{_tables++}";
        MReg at = Temp();
        Emit(MOp.Lea, at, MMem.Rip(symbol, 0));
        Emit(new MInstr(MOp.JmpTable, idx, at) { Table = table, TableSymbol = symbol });
    }

    /// <summary>
    /// Everything into fixed registers before RSP and RBP move: once they
    /// have, no frame-relative operand means what it did.
    /// </summary>
    private void SelectUnwind(Instr i)
    {
        int w = Target.Current.WordSize;
        MReg rec = Temp(), exception = Temp();
        Mov(rec, RM(i.Operands[0], 8));
        Mov(exception, RM(i.Operands[1], 8));
        Mov(Rcx, rec);
        Mov(Rax, exception);
        Mov(Rsp, new MMem(Rcx, 2 * w));
        Mov(Rbp, new MMem(Rcx, 3 * w));
        Emit(MOp.JmpInd, new MMem(Rcx, 1 * w));
    }

    // ---- the operating system and the machine ------------------------------------------

    /// <summary>
    /// Linux x86-64: number in RAX, arguments in RDI, RSI, RDX, R10, R8, R9,
    /// result in RAX; the instruction destroys RCX and R11.
    /// </summary>
    private void SelectSyscall(Instr i)
    {
        MReg[] regs = { Rdi, Rsi, Rdx, R10, R8, R9 };
        int args = i.Operands.Count - 1;
        if (args > 6)
        {
            Error("syscall with more than six arguments");
            return;
        }
        List<MReg> values = new();
        MReg number = R(i.Operands[0]);
        for (int k = 0; k < args; k++)
        {
            values.Add(R(i.Operands[k + 1]));
        }
        for (int k = 0; k < args; k++)
        {
            Mov(regs[k], values[k]);
        }
        Mov(Rax, number);
        Emit(new MInstr(MOp.Syscall) { IntArgs = args });
        if (i.Dest is not null)
        {
            Mov(V(i.Dest), Rax, Width(i.Dest.Type));
        }
    }

    private void SelectMachineIntrinsic(string name, Instr i)
    {
        switch (name)
        {
            case Corsac.Lang.X86.MachineIntrinsics.ThreadBlock:
                // The self pointer at fs:[0]: the first word of the block is
                // its own address, as glibc's thread control block is.
                if (i.Dest is not null)
                {
                    Mov(V(i.Dest), new MMem(null, 0) { Segment = 0x64 });
                }
                return;
            case Corsac.Lang.X86.MachineIntrinsics.In8:
            case Corsac.Lang.X86.MachineIntrinsics.In16:
            case Corsac.Lang.X86.MachineIntrinsics.In32:
            {
                int w = name == Corsac.Lang.X86.MachineIntrinsics.In8 ? 1 : name == Corsac.Lang.X86.MachineIntrinsics.In16 ? 2 : 4;
                Mov(Rdx, R(i.Operands[0]));
                if (w != 4)
                {
                    EmitW(MOp.Xor, 4, Rax, Rax);
                }
                EmitW(MOp.In, w);
                if (i.Dest is not null)
                {
                    Mov(V(i.Dest), Rax, 4);
                }
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.Out8:
            case Corsac.Lang.X86.MachineIntrinsics.Out16:
            case Corsac.Lang.X86.MachineIntrinsics.Out32:
            {
                int w = name == Corsac.Lang.X86.MachineIntrinsics.Out8 ? 1 : name == Corsac.Lang.X86.MachineIntrinsics.Out16 ? 2 : 4;
                MReg port = R(i.Operands[0]);
                MReg value = R(i.Operands[1]);
                Mov(Rdx, port);
                Mov(Rax, value);
                EmitW(MOp.Out, w);
                return;
            }
            case Corsac.Lang.X86.MachineIntrinsics.Cli:
                Emit(MOp.Cli);
                return;
            case Corsac.Lang.X86.MachineIntrinsics.Sti:
                Emit(MOp.Sti);
                return;
            case Corsac.Lang.X86.MachineIntrinsics.Hlt:
                Emit(MOp.Hlt);
                return;
            default:
                Error($"the machine intrinsic {name} has no x86-64 form");
                return;
        }
    }
}
