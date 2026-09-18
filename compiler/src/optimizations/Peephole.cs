#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Algebraic identities on one instruction at a time: x+0, x*1, x*0,
/// multiplication and unsigned division by a power of two as shifts,
/// unsigned remainder by one as a mask, and a compare result tested
/// against zero folded into the compare. Everything here is exact in
/// two's complement at the operand's width; nothing here reasons about
/// floats, whose identities (x*1, x+0) fail for NaN and signed zero.
///
/// Immediates are recognised only as immediates: the propagation pass
/// puts them there first. The compare rules look through a register to
/// its single definition, and re-reading that compare's operands at a
/// new place goes through <see cref="Defs.CanForward"/>, because the IR
/// is not SSA and the operands may have moved on.
/// </summary>
public sealed class Peephole : IPass
{
    private readonly bool _ssa;

    /// <param name="ssa">Whether the function is in SSA form when the pass runs; see <see cref="Defs.Ssa"/>.</param>
    public Peephole(bool ssa = false) => _ssa = ssa;

    public string Name => _ssa ? "peephole-ssa" : "peephole";

    public void Run(Function f)
    {
        Defs? defs = null;      // built on first need; most functions never need it
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Dest is null)
                {
                    continue;
                }
                Instr? r = Algebra(i);
                if (r is null && i.Op is (Opcode.Not or Opcode.Neg or Opcode.ByteSwap))
                {
                    defs ??= new Defs(f, _ssa);
                    r = CancelUnary(i, b, k, defs);
                }
                if (r is null && i.Op is (Opcode.Eq or Opcode.Ne))
                {
                    defs ??= new Defs(f, _ssa);
                    r = CompareOfCompare(i, b, k, defs);
                }
                if (r is not null)
                {
                    b.Instrs[k] = r;
                }
            }
        }
    }

    private static Instr? CancelUnary(Instr i, Block block, int index, Defs defs)
    {
        // These are modular integer operations, never checked arithmetic or FP.
        if (i.Dest!.Type is not (IrType.I32 or IrType.I64)
            || i.Operands.Count != 1 || i.Operands[0] is not RegOperand input
            || defs.Site(input.Reg) is not { } site) return null;
        Instr inner = site.Block.Instrs[site.Index];
        if (inner.Op != i.Op || inner.Dest?.Type != i.Dest.Type
            || inner.Operands.Count != 1 || inner.Operands[0].Type != i.Dest.Type) return null;
        // Be deliberately block-local: no implicit dominance assumption at joins.
        if (!ReferenceEquals(site.Block, block) || site.Index >= index) return null;
        Operand original = inner.Operands[0];
        if (original is RegOperand source
            && !defs.CanForward(source.Reg, block, site.Index, block, index)) return null;
        if (original is not (RegOperand or ImmOperand)) return null;
        return IrInfo.CopyOf(i, original);
    }

    private static Instr? Algebra(Instr i)
    {
        if (i.Operands.Count != 2 || !i.Dest!.Type.IsInt())
        {
            return null;
        }
        Operand x = i.Operands[0];
        Operand y = i.Operands[1];
        IrType t = i.Dest.Type;
        bool sameReg = x is RegOperand rx && y is RegOperand ry && ReferenceEquals(rx.Reg, ry.Reg);

        switch (i.Op)
        {
            case Opcode.Add:
                if (IrInfo.IsImm(y, 0))
                {
                    return IrInfo.CopyOf(i, x);
                }
                if (IrInfo.IsImm(x, 0))
                {
                    return IrInfo.CopyOf(i, y);
                }
                break;

            case Opcode.Sub:
                if (IrInfo.IsImm(y, 0))
                {
                    return IrInfo.CopyOf(i, x);
                }
                if (sameReg)
                {
                    return IrInfo.CopyOf(i, new ImmOperand(0, t));
                }
                break;

            case Opcode.Mul:
                if (IrInfo.IsImm(y, 1))
                {
                    return IrInfo.CopyOf(i, x);
                }
                if (IrInfo.IsImm(x, 1))
                {
                    return IrInfo.CopyOf(i, y);
                }
                if (IrInfo.IsImm(y, 0) || IrInfo.IsImm(x, 0))
                {
                    return IrInfo.CopyOf(i, new ImmOperand(0, t));
                }
                if (PowerOfTwo(y, out int sy))
                {
                    return Shift(i, Opcode.Shl, x, sy);
                }
                if (PowerOfTwo(x, out int sx))
                {
                    return Shift(i, Opcode.Shl, y, sx);
                }
                break;

            case Opcode.DivU:
                if (IrInfo.IsImm(y, 1))
                {
                    return IrInfo.CopyOf(i, x);
                }
                if (PowerOfTwo(y, out int sd))
                {
                    return Shift(i, Opcode.ShrU, x, sd);
                }
                break;

            case Opcode.RemU:
                if (IrInfo.IsImm(y, 1))
                {
                    return IrInfo.CopyOf(i, new ImmOperand(0, t));
                }
                if (PowerOfTwo(y, out int sr))
                {
                    return new Instr
                    {
                        Op = Opcode.And, Dest = i.Dest, Line = i.Line,
                        Operands = { x, new ImmOperand((1L << sr) - 1, t) },
                    };
                }
                break;

            case Opcode.DivS:
                // Signed division by a power of two is not a shift: -7/2 is
                // -3 and -7>>1 is -4. Only the trivial divisor folds.
                if (IrInfo.IsImm(y, 1))
                {
                    return IrInfo.CopyOf(i, x);
                }
                break;

            case Opcode.And:
                if (IrInfo.IsImm(y, 0) || IrInfo.IsImm(x, 0))
                {
                    return IrInfo.CopyOf(i, new ImmOperand(0, t));
                }
                if (IrInfo.IsImm(y, -1) || sameReg)
                {
                    return IrInfo.CopyOf(i, x);
                }
                if (IrInfo.IsImm(x, -1))
                {
                    return IrInfo.CopyOf(i, y);
                }
                break;

            case Opcode.Or:
                if (IrInfo.IsImm(y, 0) || sameReg)
                {
                    return IrInfo.CopyOf(i, x);
                }
                if (IrInfo.IsImm(x, 0))
                {
                    return IrInfo.CopyOf(i, y);
                }
                break;

            case Opcode.Xor:
                if (IrInfo.IsImm(y, 0))
                {
                    return IrInfo.CopyOf(i, x);
                }
                if (IrInfo.IsImm(x, 0))
                {
                    return IrInfo.CopyOf(i, y);
                }
                if (sameReg)
                {
                    return IrInfo.CopyOf(i, new ImmOperand(0, t));
                }
                break;

            case Opcode.Shl:
            case Opcode.ShrS:
            case Opcode.ShrU:
                // The count is masked by the width, so 32 on an I32 is 0 too.
                if (y is ImmOperand c && (c.Value & (t == IrType.I64 ? 63 : 31)) == 0)
                {
                    return IrInfo.CopyOf(i, x);
                }
                break;
        }
        return null;
    }

    /// <summary>
    /// <c>c = ne t, 0</c> where t is a compare result is just t; <c>c = eq t, 0</c>
    /// is the opposite compare when its operands can be re-read here, and
    /// <c>t xor 1</c> otherwise -- t is 0 or 1, so that is exact, and it is
    /// the only form a float compare can take, since the inverse of an
    /// ordered compare is not an ordered compare.
    /// </summary>
    private static Instr? CompareOfCompare(Instr i, Block b, int k, Defs defs)
    {
        Operand x = i.Operands[0];
        Operand y = i.Operands[1];
        if (IrInfo.IsImm(x, 0) && y is RegOperand)
        {
            (x, y) = (y, x);
        }
        if (x is not RegOperand rt || !IrInfo.IsImm(y, 0))
        {
            return null;
        }
        Instr? def = defs.Definition(rt.Reg);
        if (def is null || !(IrInfo.IsIntCompare(def.Op) || IrInfo.IsFloatCompare(def.Op)))
        {
            return null;
        }

        if (i.Op == Opcode.Ne)
        {
            return IrInfo.CopyOf(i, x);
        }

        Opcode? inverse = IrInfo.InvertIntCompare(def.Op);
        if (inverse is not null && CanReRead(rt.Reg, b, k, defs))
        {
            return new Instr
            {
                Op = inverse.Value, Dest = i.Dest, Line = i.Line,
                Operands = { def.Operands[0], def.Operands[1] },
            };
        }
        return new Instr
        {
            Op = Opcode.Xor, Dest = i.Dest, Line = i.Line,
            Operands = { x, new ImmOperand(1, IrType.I32) },
        };
    }

    /// <summary>Whether every register operand of the compare defining <paramref name="t"/> still holds the same value at (b, k).</summary>
    private static bool CanReRead(VReg t, Block b, int k, Defs defs)
    {
        (Block Block, int Index)? at = defs.Site(t);
        if (at is null)
        {
            return false;
        }
        foreach (VReg r in IrInfo.Uses(at.Value.Block.Instrs[at.Value.Index]))
        {
            if (!defs.CanForward(r, at.Value.Block, at.Value.Index, b, k))
            {
                return false;
            }
        }
        return true;
    }

    private static bool PowerOfTwo(Operand o, out int shift)
    {
        shift = 0;
        if (o is not ImmOperand imm)
        {
            return false;
        }
        // Read as unsigned so the top bit counts: 0x80000000 is 2^31, and
        // a shift by 31 wraps exactly as the multiply would.
        ulong v = imm.Type == IrType.I64 ? (ulong)imm.Value : (uint)imm.Value;
        if (v == 0 || (v & (v - 1)) != 0)
        {
            return false;
        }
        shift = System.Numerics.BitOperations.TrailingZeroCount(v);
        return true;
    }

    private static Instr Shift(Instr i, Opcode op, Operand x, int by)
        => new()
        {
            Op = op, Dest = i.Dest, Line = i.Line,
            Operands = { x, new ImmOperand(by, IrType.I32) },
        };
}
