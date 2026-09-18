#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

namespace Corsac.Tests.Opt;

/// <summary>
/// Runs an integer IR function directly, so a test can compare what a
/// function computes before and after a pass instead of pinning its
/// exact text. Registers are longs; memory is a sparse byte map with
/// symbols and frame slots given addresses on first touch; calls go to
/// delegates the test registers by name. Floats and anything the backend
/// alone can do (unwind, syscall) are not supported and throw.
/// </summary>
public sealed class Interp
{
    public Dictionary<string, Func<long[], long>> Calls { get; } = new();
    public int MaxSteps { get; set; } = 1_000_000;

    private readonly Dictionary<long, byte> _mem = new();
    private readonly Dictionary<string, long> _symbols = new();
    private readonly Dictionary<FrameSlot, long> _slots = new();
    private long _nextAddress = 0x1000;

    /// <summary>The bytes at a symbol, for checking stores.</summary>
    public long ReadSymbol(string name, int bytes = 4) => Read(Address(name), bytes);

    public void WriteSymbol(string name, long value, int bytes = 4) => Write(Address(name), value, bytes);

    public long Run(Function f, params long[] args)
    {
        Dictionary<VReg, long> regs = new();
        for (int k = 0; k < f.Params.Count; k++)
        {
            regs[f.Params[k]] = Norm(args[k], f.Params[k].Type);
        }

        Block? prev = null;
        Block b = f.Entry;
        int steps = 0;
        while (true)
        {
            if (++steps > MaxSteps)
            {
                throw new Exception("interpreter: too many steps");
            }
            Block? next = null;
            long? ret = null;
            // Phis read their operands together, before any is written.
            List<(VReg, long)> phiValues = new();
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Phi)
                {
                    int k = Phi.IndexOf(i, prev!);
                    if (k < 0)
                    {
                        throw new Exception($"interpreter: phi {i} has no entry for {prev}");
                    }
                    phiValues.Add((i.Dest!, Value(i.Operands[k], regs)));
                    continue;
                }
                foreach ((VReg r, long v) in phiValues)
                {
                    regs[r] = v;
                }
                phiValues.Clear();

                switch (i.Op)
                {
                    case Opcode.Ret:
                        ret = i.Operands.Count == 0 ? 0 : Value(i.Operands[0], regs);
                        break;
                    case Opcode.Jump:
                        next = i.Targets[0];
                        break;
                    case Opcode.Branch:
                        next = (int)Value(i.Operands[0], regs) != 0 ? i.Targets[0] : i.Targets[1];
                        break;
                    case Opcode.Switch:
                        {
                            long n = (int)Value(i.Operands[0], regs);
                            next = n >= 0 && n < i.Targets.Count ? i.Targets[(int)n] : i.Default!;
                            break;
                        }
                    case Opcode.Unreachable:
                    case Opcode.Trap:
                        throw new Exception($"interpreter: reached {i}");
                    case Opcode.Fence:
                    case Opcode.Pause:
                        break;
                    case Opcode.Load:
                        regs[i.Dest!] = Load(Value(i.Operands[0], regs) + i.Offset, i.Size, i.Signed, i.Dest!.Type);
                        break;
                    case Opcode.Store:
                        Write(Value(i.Operands[0], regs) + i.Offset, Value(i.Operands[1], regs), i.Size);
                        break;
                    case Opcode.Call:
                        {
                            if (!Calls.TryGetValue(i.Callee!, out Func<long[], long>? fn))
                            {
                                throw new Exception($"interpreter: no implementation of {i.Callee}");
                            }
                            long r = fn(i.Operands.Select(o => Value(o, regs)).ToArray());
                            if (i.Dest is not null)
                            {
                                regs[i.Dest] = Norm(r, i.Dest.Type);
                            }
                            break;
                        }
                    default:
                        {
                            if (i.Dest is null)
                            {
                                throw new Exception($"interpreter: unsupported {i}");
                            }
                            Instr probe = new() { Op = i.Op, Dest = i.Dest };
                            foreach (Operand o in i.Operands)
                            {
                                probe.Operands.Add(new ImmOperand(Value(o, regs), o.Type));
                            }
                            Instr? folded = i.Op == Opcode.Copy ? probe : ConstantFold.Fold(probe);
                            if (folded is null || folded.Operands[0] is not ImmOperand imm)
                            {
                                throw new Exception($"interpreter: cannot evaluate {i}");
                            }
                            regs[i.Dest] = Norm(imm.Value, i.Dest.Type);
                            break;
                        }
                }
                if (ret is not null || next is not null)
                {
                    break;
                }
            }
            if (ret is not null)
            {
                return ret.Value;
            }
            prev = b;
            b = next ?? throw new Exception($"interpreter: block {b} fell off its end");
        }
    }

    private long Value(Operand o, Dictionary<VReg, long> regs) => o switch
    {
        ImmOperand i => Norm(i.Value, i.Type),
        RegOperand r => regs.TryGetValue(r.Reg, out long v) ? v : throw new Exception($"interpreter: {r.Reg} read before it was written"),
        SymOperand s => Address(s.Name) + s.Offset,
        SlotOperand s => SlotAddress(s.Slot),
        _ => throw new Exception("interpreter: unknown operand"),
    };

    private static long Norm(long v, IrType t) => t == IrType.I32 ? (int)v : v;

    private long Address(string name)
    {
        if (!_symbols.TryGetValue(name, out long a))
        {
            a = _nextAddress;
            _nextAddress += 0x1000;
            _symbols[name] = a;
        }
        return a;
    }

    private long SlotAddress(FrameSlot s)
    {
        if (!_slots.TryGetValue(s, out long a))
        {
            a = _nextAddress;
            _nextAddress += 0x1000;
            _slots[s] = a;
        }
        return a;
    }

    private long Load(long address, int size, bool signed, IrType t)
    {
        long v = Read(address, size);
        if (signed && size < 8)
        {
            int shift = 64 - 8 * size;
            v = v << shift >> shift;
        }
        return Norm(v, t);
    }

    private long Read(long address, int size)
    {
        long v = 0;
        for (int k = size - 1; k >= 0; k--)
        {
            v = (v << 8) | (_mem.TryGetValue(address + k, out byte x) ? x : (byte)0);
        }
        return v;
    }

    private void Write(long address, long value, int size)
    {
        for (int k = 0; k < size; k++)
        {
            _mem[address + k] = (byte)(value >> (8 * k));
        }
    }
}
