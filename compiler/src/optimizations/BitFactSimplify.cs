#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>Consumers of shared bit facts. Staged for the large batch; no
/// memory operation, division, checked-overflow check or FP operation moves.</summary>
public sealed class BitFactSimplify : IPass
{
    public string Name => "bit-fact-simplify";

    public void Run(Function function)
    {
        IntegerBitFacts facts = new(function);
        foreach (var block in function.Blocks)
        for (int index = 0; index < block.Instrs.Count; index++)
        {
            Instr instruction = block.Instrs[index];
            if (instruction.Dest is not { } dest || !dest.Type.IsInt()) continue;
            Instr? replacement = Simplify(instruction, facts);
            if (replacement is not null) block.Instrs[index] = replacement;
        }
    }

    private static Instr? Simplify(Instr i, IntegerBitFacts facts)
    {
        if (IrInfo.IsIntCompare(i.Op))
        {
            bool? answer = Compare(i, facts);
            return answer is { } result ? IrInfo.CopyOf(i, new ImmOperand(result ? 1 : 0, IrType.I32)) : null;
        }
        // An unused read can still fault or be externally observable. Never
        // replace the read itself with a value fact.
        if (i.Op is not (Opcode.Copy or Opcode.Add or Opcode.Sub or Opcode.Mul
            or Opcode.And or Opcode.Or or Opcode.Xor or Opcode.Not or Opcode.Neg
            or Opcode.Shl or Opcode.ShrS or Opcode.ShrU or Opcode.Trunc64
            or Opcode.ZExt8 or Opcode.ZExt16 or Opcode.ZExt32
            or Opcode.SExt8 or Opcode.SExt16 or Opcode.SExt32)) return null;
        ulong mask = IntegerBitFacts.Mask(i.Dest!.Type);
        IntegerBitFacts.Bits resultBits = facts.Evaluate(i);
        if ((resultBits.Zero | resultBits.One) == mask)
        {
            long value = i.Dest.Type == IrType.I32 ? unchecked((int)resultBits.One) : unchecked((long)resultBits.One);
            return IrInfo.CopyOf(i, new ImmOperand(value, i.Dest.Type));
        }
        if (i.Operands.Count == 0) return null;
        var a = facts.Get(i.Operands[0]);
        if (i.Op == Opcode.ShrS && (a.Zero & (1UL << (i.Dest.Type.Bytes() * 8 - 1))) != 0)
            return Rewrite(i, Opcode.ShrU);
        if (i.Op == Opcode.SExt32 && (a.Zero & 0x80000000UL) != 0)
            return Rewrite(i, Opcode.ZExt32);
        if (i.Operands.Count != 2) return null;
        var b = facts.Get(i.Operands[1]);
        if (i.Op is Opcode.Add or Opcode.Xor && (mask & ~a.Zero & ~b.Zero) == 0)
            return Rewrite(i, Opcode.Or);
        if (i.Op == Opcode.Sub && (mask & ~b.Zero & ~a.One) == 0)
            return Rewrite(i, Opcode.Xor);
        if (i.Op == Opcode.And)
        {
            if ((mask & ~a.Zero & ~b.One) == 0) return IrInfo.CopyOf(i, i.Operands[0]);
            if ((mask & ~b.Zero & ~a.One) == 0) return IrInfo.CopyOf(i, i.Operands[1]);
        }
        if (i.Op == Opcode.Or)
        {
            if ((mask & ~b.Zero & ~a.One) == 0) return IrInfo.CopyOf(i, i.Operands[0]);
            if ((mask & ~a.Zero & ~b.One) == 0) return IrInfo.CopyOf(i, i.Operands[1]);
        }
        return null;
    }

    private static Instr Rewrite(Instr original, Opcode op)
    {
        Instr result = new() { Op = op, Dest = original.Dest, Line = original.Line };
        result.Operands.AddRange(original.Operands);
        return result;
    }

    private static bool? Compare(Instr i, IntegerBitFacts facts)
    {
        if (i.Operands.Count != 2 || !i.Operands[0].Type.IsInt()
            || i.Operands[0].Type != i.Operands[1].Type) return null;
        IrType type = i.Operands[0].Type;
        ulong mask = IntegerBitFacts.Mask(type);
        var a = facts.Get(i.Operands[0]); var b = facts.Get(i.Operands[1]);
        if (i.Op is Opcode.Eq or Opcode.Ne)
        {
            if (((a.One & b.Zero) | (b.One & a.Zero)) != 0) return i.Op == Opcode.Ne;
            if ((a.One | a.Zero) == mask && (b.One | b.Zero) == mask)
                return i.Op == Opcode.Eq ? a.One == b.One : a.One != b.One;
            return null;
        }
        ulong minA = a.One, maxA = mask & ~a.Zero, minB = b.One, maxB = mask & ~b.Zero;
        int maxAgainstMin, minAgainstMax;
        if (i.Op is Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS)
        {
            ulong sign = 1UL << (type.Bytes() * 8 - 1);
            minA |= sign & ~a.Zero; minB |= sign & ~b.Zero;
            if ((a.One & sign) == 0) maxA &= ~sign;
            if ((b.One & sign) == 0) maxB &= ~sign;
            long Signed(ulong value) => type == IrType.I32 ? unchecked((int)value) : unchecked((long)value);
            maxAgainstMin = Signed(maxA).CompareTo(Signed(minB));
            minAgainstMax = Signed(minA).CompareTo(Signed(maxB));
        }
        else
        {
            maxAgainstMin = maxA.CompareTo(minB);
            minAgainstMax = minA.CompareTo(maxB);
        }
        return i.Op switch
        {
            Opcode.LtS or Opcode.LtU => maxAgainstMin < 0 ? true : minAgainstMax >= 0 ? false : null,
            Opcode.LeS or Opcode.LeU => maxAgainstMin <= 0 ? true : minAgainstMax > 0 ? false : null,
            Opcode.GtS or Opcode.GtU => minAgainstMax > 0 ? true : maxAgainstMin <= 0 ? false : null,
            Opcode.GeS or Opcode.GeU => minAgainstMax >= 0 ? true : maxAgainstMin < 0 ? false : null,
            _ => null,
        };
    }
}
