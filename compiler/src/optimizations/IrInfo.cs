#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Facts about instructions that every pass needs and that Ir.cs does not
/// state in code: which registers an instruction reads, whether it may be
/// deleted when its result is unused, whether it can be folded. Kept in one
/// place so a new opcode is classified once rather than in each pass.
/// </summary>
public static class IrInfo
{
    /// <summary>The registers an instruction reads.</summary>
    public static IEnumerable<VReg> Uses(Instr i)
    {
        foreach (Operand o in i.Operands)
        {
            if (o is RegOperand r)
            {
                yield return r.Reg;
            }
        }
    }

    /// <summary>
    /// Whether an instruction with an unused result may be removed. Anything
    /// that writes memory, calls, traps, or can fault stays: division may
    /// trap on zero, and the IR promises nothing about where, so a division
    /// whose result is dead is still a division that runs.
    /// </summary>
    public static bool IsPure(Instr i)
    {
        switch (i.Op)
        {
            case Opcode.Load:
            case Opcode.ArrayLength:
                // A load through a register may fault on a bad address, and
                // the program is entitled to that fault; one from a static
                // or a frame slot addresses memory the program owns.
                return i.Operands[0] is SymOperand or SlotOperand;
            case Opcode.Copy:
            case Opcode.Add:
            case Opcode.Sub:
            case Opcode.Mul:
            case Opcode.And:
            case Opcode.Or:
            case Opcode.Xor:
            case Opcode.Shl:
            case Opcode.ShrS:
            case Opcode.ShrU:
            case Opcode.Neg:
            case Opcode.Not:
            case Opcode.ByteSwap:
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
            case Opcode.FAdd:
            case Opcode.FSub:
            case Opcode.FMul:
            case Opcode.FDiv:
            case Opcode.FNeg:
            case Opcode.FSqrt:
            case Opcode.FEq:
            case Opcode.FNe:
            case Opcode.FLt:
            case Opcode.FLe:
            case Opcode.FGt:
            case Opcode.FGe:
            case Opcode.SExt8:
            case Opcode.SExt16:
            case Opcode.ZExt8:
            case Opcode.ZExt16:
            case Opcode.Trunc64:
            case Opcode.SExt32:
            case Opcode.ZExt32:
            case Opcode.FConv:
            case Opcode.IToF:
            case Opcode.UToF:
            case Opcode.FToI:
            case Opcode.FToU:
            case Opcode.Bits:
            case Opcode.Phi:
            case Opcode.LabelAddr:
            case Opcode.StackPointer:
            case Opcode.FramePointer:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether the opcode is an integer comparison producing 0 or 1.</summary>
    public static bool IsIntCompare(Opcode op) => op is Opcode.Eq or Opcode.Ne
        or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS
        or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU;

    /// <summary>Whether the opcode is a floating-point comparison producing 0 or 1.</summary>
    public static bool IsFloatCompare(Opcode op) => op is Opcode.FEq or Opcode.FNe
        or Opcode.FLt or Opcode.FLe or Opcode.FGt or Opcode.FGe;

    /// <summary>
    /// The comparison with the opposite truth table. Integer compares are
    /// total, so the inverse is exact; float compares are not (unordered),
    /// which is why this has no float cases -- see <see cref="Peephole"/>.
    /// </summary>
    public static Opcode? InvertIntCompare(Opcode op) => op switch
    {
        Opcode.Eq => Opcode.Ne,
        Opcode.Ne => Opcode.Eq,
        Opcode.LtS => Opcode.GeS,
        Opcode.LeS => Opcode.GtS,
        Opcode.GtS => Opcode.LeS,
        Opcode.GeS => Opcode.LtS,
        Opcode.LtU => Opcode.GeU,
        Opcode.LeU => Opcode.GtU,
        Opcode.GtU => Opcode.LeU,
        Opcode.GeU => Opcode.LtU,
        _ => null,
    };

    /// <summary>Every operand that names a register, replaced through <paramref name="map"/>.</summary>
    public static void ReplaceUses(Instr i, Func<VReg, Operand?> map)
    {
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is RegOperand r)
            {
                Operand? o = map(r.Reg);
                if (o is not null)
                {
                    i.Operands[k] = o;
                }
            }
        }
    }

    /// <summary>Replaces the instruction at an index with an equivalent one, keeping the source line.</summary>
    public static void Replace(Block b, int index, Instr with)
    {
        b.Instrs[index] = with;
    }

    /// <summary>A copy of a value into a register, on the same source line as the instruction it stands in for.</summary>
    public static Instr CopyOf(Instr original, Operand value)
        => new() { Op = Opcode.Copy, Dest = original.Dest, Operands = { value }, Line = original.Line };

    /// <summary>The immediate value an operand carries, if it is one.</summary>
    public static bool IsImm(Operand o, out long value)
    {
        if (o is ImmOperand imm)
        {
            value = imm.Value;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>The immediate value of an operand, if it is that value.</summary>
    public static bool IsImm(Operand o, long expected) => o is ImmOperand imm && Normalise(imm.Value, imm.Type) == Normalise(expected, imm.Type);

    /// <summary>
    /// An immediate as the machine sees it. I32 immediates may arrive with
    /// junk in the high half (a negative int stored in a long, or not), so
    /// comparisons and arithmetic go through the width first.
    /// </summary>
    public static long Normalise(long v, IrType t) => t == IrType.I32 ? (int)v : v;
}
