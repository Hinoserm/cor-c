#nullable enable
using System.Numerics;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>
/// Known-zero/known-one information for modular integer IR. Unlike an SSA
/// value analysis, facts intersect every definition of a mutable register.
/// Parameters include an unknown entry value. No memory is moved or removed.
/// Reference model: llvm/Support/KnownBits; this implementation uses the local
/// IR's two widths and never assumes LLVM poison or signed-overflow flags.
/// </summary>
public sealed class IntegerBitFacts
{
    public readonly record struct Bits(ulong Zero, ulong One);
    private readonly Dictionary<VReg, Bits> _facts = new();

    public IntegerBitFacts(Function function)
    {
        HashSet<VReg> parameters = new(function.Params);
        Dictionary<VReg, List<Instr>> definitions = new();
        foreach (Instr instruction in function.Blocks.SelectMany(b => b.Instrs))
        {
            if (instruction.Dest is not { } dest || !dest.Type.IsInt()) continue;
            if (!definitions.TryGetValue(dest, out List<Instr>? sites)) definitions[dest] = sites = new();
            sites.Add(instruction);
        }
        // Starting at unknown makes every intermediate approximation sound.
        // A work limit may miss a fact but must never invent one.
        for (int round = 0; round < 8; round++)
        {
            bool changed = false;
            foreach (var (register, sites) in definitions)
            {
                if (parameters.Contains(register)) continue;
                ulong mask = Mask(register.Type);
                Bits combined = new(mask, mask);
                foreach (Instr site in sites)
                {
                    Bits next = Evaluate(site);
                    combined = new(combined.Zero & next.Zero, combined.One & next.One);
                }
                if (combined != _facts.GetValueOrDefault(register))
                {
                    _facts[register] = combined;
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    public static ulong Mask(IrType type) => type == IrType.I32 ? uint.MaxValue : ulong.MaxValue;
    private static ulong Low(int count) => count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
    public Bits Get(Operand operand) => operand switch
    {
        ImmOperand value => Constant(unchecked((ulong)value.Value), Mask(value.Type)),
        RegOperand value => _facts.GetValueOrDefault(value.Reg),
        _ => default,
    };
    private static Bits Constant(ulong value, ulong mask) => new(~value & mask, value & mask);
    private static Bits Clip(Bits bits, ulong mask) => new(bits.Zero & mask, bits.One & mask);

    public Bits Evaluate(Instr instruction)
    {
        if (instruction.Dest is not { } dest || !dest.Type.IsInt()) return default;
        ulong mask = Mask(dest.Type);
        int width = dest.Type.Bytes() * 8;
        if (IrInfo.IsIntCompare(instruction.Op)) return new(mask & ~1UL, 0);
        if (instruction.Op == Opcode.Load && !instruction.Signed && instruction.Size is 1 or 2 or 4 or 8)
            return new(mask & ~Low(instruction.Size * 8), 0);
        if (instruction.Operands.Count == 0) return default;
        Bits a = Get(instruction.Operands[0]);
        if (instruction.Op is Opcode.Copy or Opcode.Trunc64) return Clip(a, mask);
        if (instruction.Op == Opcode.Not) return Clip(new(a.One, a.Zero), mask);
        int extension = instruction.Op switch
        {
            Opcode.ZExt8 or Opcode.SExt8 => 8,
            Opcode.ZExt16 or Opcode.SExt16 => 16,
            Opcode.ZExt32 or Opcode.SExt32 => 32,
            _ => 0,
        };
        if (extension != 0)
        {
            ulong lower = Low(extension), upper = mask & ~lower;
            ulong sign = 1UL << (extension - 1);
            bool unsigned = instruction.Op is Opcode.ZExt8 or Opcode.ZExt16 or Opcode.ZExt32;
            return new((a.Zero & lower) | (unsigned || (a.Zero & sign) != 0 ? upper : 0),
                (a.One & lower) | (!unsigned && (a.One & sign) != 0 ? upper : 0));
        }
        if (instruction.Op == Opcode.Neg) return Add(Constant(0, mask), a, width, subtract: true);
        if (instruction.Operands.Count != 2) return default;
        Bits b = Get(instruction.Operands[1]);
        if (instruction.Op is Opcode.Shl or Opcode.ShrU or Opcode.ShrS
            && instruction.Operands[1] is ImmOperand amount)
        {
            int count = (int)(amount.Value & (width - 1));
            if (count == 0) return Clip(a, mask);
            if (instruction.Op == Opcode.Shl)
                return Clip(new((a.Zero << count) | Low(count), a.One << count), mask);
            ulong upper = mask & ~Low(width - count), sign = 1UL << (width - 1);
            bool logical = instruction.Op == Opcode.ShrU;
            return new((a.Zero >> count) | (logical || (a.Zero & sign) != 0 ? upper : 0),
                (a.One >> count) | (!logical && (a.One & sign) != 0 ? upper : 0));
        }
        return instruction.Op switch
        {
            Opcode.And => Clip(new(a.Zero | b.Zero, a.One & b.One), mask),
            Opcode.Or => Clip(new(a.Zero & b.Zero, a.One | b.One), mask),
            Opcode.Xor => Clip(new((a.Zero & b.Zero) | (a.One & b.One),
                (a.Zero & b.One) | (a.One & b.Zero)), mask),
            Opcode.Add => Add(a, b, width, subtract: false),
            Opcode.Sub => Add(a, b, width, subtract: true),
            Opcode.Mul => Product(a, b, width),
            _ => default,
        };
    }

    private static Bits Product(Bits a, Bits b, int width)
    {
        ulong mask = Low(width);
        if ((a.Zero | a.One) == mask && (b.Zero | b.One) == mask)
            return Constant(unchecked(a.One * b.One), mask);
        ulong possibleA = mask & ~a.Zero, possibleB = mask & ~b.Zero;
        if (possibleA == 0 || possibleB == 0) return Constant(0, mask);
        int active = BitOperations.Log2(possibleA) + BitOperations.Log2(possibleB) + 2;
        int trailing = Math.Min(width, BitOperations.TrailingZeroCount(~a.Zero)
            + BitOperations.TrailingZeroCount(~b.Zero));
        return new((Low(trailing) | (active < width ? mask & ~Low(active) : 0)) & mask, 0);
    }

    private static Bits Add(Bits a, Bits b, int width, bool subtract)
    {
        if (subtract) b = new(b.One, b.Zero);
        int carries = subtract ? 2 : 1;
        ulong zero = 0, one = 0;
        for (int bit = 0; bit < width; bit++)
        {
            ulong place = 1UL << bit;
            int av = (a.One & place) != 0 ? 2 : (a.Zero & place) != 0 ? 1 : 3;
            int bv = (b.One & place) != 0 ? 2 : (b.Zero & place) != 0 ? 1 : 3;
            int sums = 0, next = 0;
            for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int c = 0; c < 2; c++)
            {
                if ((av & (1 << x)) == 0 || (bv & (1 << y)) == 0 || (carries & (1 << c)) == 0) continue;
                int sum = x + y + c;
                sums |= 1 << (sum & 1); next |= 1 << (sum >> 1);
            }
            if (sums == 1) zero |= place;
            if (sums == 2) one |= place;
            carries = next;
        }
        return new(zero, one);
    }
}
