#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// Builds instructions into a function one at a time, keeping track of the
/// current block. Lowering talks to this rather than constructing Instr by
/// hand, so an instruction's shape is decided in one place.
/// </summary>
public sealed class Builder
{
    public Function Function { get; }
    public Block Block { get; private set; }

    public Builder(Function f, Block entry)
    {
        Function = f;
        Block = entry;
    }

    public void SetBlock(Block b) => Block = b;

    /// <summary>
    /// The source line everything appended from here on belongs to.
    ///
    /// Set as the lowering walks the statements, and stamped on each
    /// instruction on its way into a block: a statement becomes a dozen
    /// instructions and they all come from the line it was written on.
    /// </summary>
    public int Line { get; set; }

    /// <summary>Whether the current block already ended: nothing more may be appended.</summary>
    public bool Closed => Block.Terminator is not null;

    private Instr Append(Instr i)
    {
        if (i.Line == 0)
        {
            i.Line = Line;
        }
        if (!Closed)
        {
            Block.Instrs.Add(i);
        }
        return i;
    }

    private static RegOperand R(VReg r) => new(r);

    public VReg Copy(VReg src)
    {
        VReg d = Function.NewReg(src.Type);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { R(src) } });
        return d;
    }

    public void CopyTo(VReg dest, Operand src)
        => Append(new Instr { Op = Opcode.Copy, Dest = dest, Operands = { src } });

    public VReg Const(long value, IrType type)
    {
        VReg d = Function.NewReg(type);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { new ImmOperand(value, type) } });
        return d;
    }

    public VReg Address(string symbol, long offset = 0)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { new SymOperand(symbol, offset) } });
        return d;
    }

    public VReg SlotAddress(FrameSlot slot)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Append(new Instr { Op = Opcode.Copy, Dest = d, Operands = { new SlotOperand(slot) } });
        return d;
    }

    public VReg Binary(Opcode op, Operand a, Operand b, IrType result)
    {
        VReg d = Function.NewReg(result);
        Append(new Instr { Op = op, Dest = d, Operands = { a, b } });
        return d;
    }

    public VReg Binary(Opcode op, VReg a, VReg b) => Binary(op, R(a), R(b), ResultOf(op, a.Type));
    public VReg Binary(Opcode op, VReg a, long imm) => Binary(op, R(a), new ImmOperand(imm, a.Type), ResultOf(op, a.Type));

    public VReg Unary(Opcode op, Operand a, IrType result)
    {
        VReg d = Function.NewReg(result);
        Append(new Instr { Op = op, Dest = d, Operands = { a } });
        return d;
    }

    public VReg Unary(Opcode op, VReg a) => Unary(op, R(a), ResultOf(op, a.Type));

    public static IrType ResultOf(Opcode op, IrType operand) => op switch
    {
        Opcode.Eq or Opcode.Ne or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS
            or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU
            or Opcode.FEq or Opcode.FNe or Opcode.FLt or Opcode.FLe or Opcode.FGt or Opcode.FGe => IrType.I32,
        Opcode.Trunc64 => IrType.I32,
        Opcode.SExt32 or Opcode.ZExt32 => IrType.I64,
        _ => operand,
    };

    public VReg Load(IrType type, Operand address, long offset = 0, int size = 0, bool signed = true)
    {
        VReg d = Function.NewReg(type);
        Append(new Instr
        {
            Op = Opcode.Load, Dest = d, Operands = { address },
            Offset = offset, Size = size == 0 ? type.Bytes() : size, Signed = signed,
        });
        return d;
    }

    public VReg Load(IrType type, VReg address, long offset = 0, int size = 0, bool signed = true)
        => Load(type, R(address), offset, size, signed);

    public void Store(Operand address, Operand value, long offset = 0, int size = 0)
        => Append(new Instr
        {
            Op = Opcode.Store, Operands = { address, value },
            Offset = offset, Size = size == 0 ? value.Type.Bytes() : size,
        });

    public void Store(VReg address, VReg value, long offset = 0, int size = 0)
        => Store(R(address), R(value), offset, size);

    public VReg? Call(string callee, IrType returns, params Operand[] args)
    {
        VReg? d = returns == IrType.Void ? null : Function.NewReg(returns);
        Instr i = new() { Op = Opcode.Call, Dest = d, Callee = callee };
        i.Operands.AddRange(args);
        Append(i);
        return d;
    }

    public VReg? Call(string callee, IrType returns, IEnumerable<VReg> args)
        => Call(callee, returns, args.Select(a => (Operand)R(a)).ToArray());

    public VReg? CallIndirect(Operand target, IrType returns, IEnumerable<Operand> args)
    {
        VReg? d = returns == IrType.Void ? null : Function.NewReg(returns);
        Instr i = new() { Op = Opcode.CallIndirect, Dest = d };
        i.Operands.Add(target);
        i.Operands.AddRange(args);
        Append(i);
        return d;
    }

    public VReg Syscall(Operand number, IEnumerable<Operand> args)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Instr i = new() { Op = Opcode.Syscall, Dest = d };
        i.Operands.Add(number);
        i.Operands.AddRange(args);
        Append(i);
        return d;
    }

    public void Ret(Operand? value = null)
    {
        Instr i = new() { Op = Opcode.Ret };
        if (value is not null)
        {
            i.Operands.Add(value);
        }
        Append(i);
    }

    public void Jump(Block target)
        => Append(new Instr { Op = Opcode.Jump, Targets = { target } });

    public void Branch(Operand cond, Block ifTrue, Block ifFalse)
        => Append(new Instr { Op = Opcode.Branch, Operands = { cond }, Targets = { ifTrue, ifFalse } });

    public void Branch(VReg cond, Block ifTrue, Block ifFalse) => Branch(R(cond), ifTrue, ifFalse);

    public void Switch(Operand index, IReadOnlyList<Block> targets, Block fallback)
    {
        Instr i = new() { Op = Opcode.Switch, Operands = { index }, Default = fallback };
        i.Targets.AddRange(targets);
        Append(i);
    }

    public void Unreachable() => Append(new Instr { Op = Opcode.Unreachable });

    public Instr Emit(Opcode op, VReg? dest, params Operand[] operands)
    {
        Instr i = new() { Op = op, Dest = dest };
        i.Operands.AddRange(operands);
        return Append(i);
    }

    public VReg LabelAddress(Block target)
    {
        VReg d = Function.NewReg(IrTypes.Word);
        Append(new Instr { Op = Opcode.LabelAddr, Dest = d, Targets = { target } });
        return d;
    }

    public VReg Reg(IrType type, string? name = null) => Function.NewReg(type, name);
}
