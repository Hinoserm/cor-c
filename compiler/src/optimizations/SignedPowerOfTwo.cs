#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>
/// Signed power-of-two division with a bias that preserves truncation toward
/// zero; and a 64-bit runtime division helper whose divisor has become a
/// constant turned back into the IR division the backend does inline.
/// </summary>
public sealed class SignedPowerOfTwo : IPass
{
    public string Name => "signed-power-of-two";
    public void Run(Function f)
    {
        foreach (var block in f.Blocks)
        {
            // A BLOCK IS COPIED ONLY ONCE THERE IS A DIVISION TO REWRITE in
            // it. Nearly none has one, and every block of every function was
            // copied instruction by instruction on every round of the pipeline.
            List<Instr>? result = null;
            for (int n = 0; n < block.Instrs.Count; n++)
            {
                Instr i = block.Instrs[n];
                // A CONSTANT DIVISOR TO ANY OF THE FOUR, found once propagation
                // has made it one (a divisor wider than 32 bits, whose
                // run-time test folded to the call): the IR division again,
                // which the backend divides by a constant inline -- its
                // reciprocal, a shift, a compare above 2^63 (Select.
                // UnsignedByConstant) -- never a call. Zero, and -1 signed,
                // stay the runtime's: the checks before them throw.
                if (i.Op == Opcode.Call && i.Dest?.Type == IrType.I64 && i.Operands.Count == 2
                    && i.Operands[1] is ImmOperand { Value: not 0 } constantDivisor
                    && RuntimeDivision(i.Callee) is Opcode arithmetic
                    && !(arithmetic is Opcode.DivS or Opcode.RemS && constantDivisor.Value == -1))
                {
                    result ??= block.Instrs.GetRange(0, n);
                    i = new Instr { Op = arithmetic, Dest = i.Dest, Line = i.Line, Operands = { i.Operands[0], i.Operands[1] } };
                }
                Opcode operation = i.Op;
                if (operation is not (Opcode.DivS or Opcode.RemS) || i.Dest?.Type is not (IrType.I32 or IrType.I64)
                    || i.Operands.Count != 2 || i.Operands[1] is not ImmOperand divisor)
                { result?.Add(i); continue; }
                // Let ordinary folding evaluate a wholly constant IR divide
                // directly instead of expanding it into a chain needing more
                // propagation rounds.
                if (i.Op != Opcode.Call && i.Operands[0] is ImmOperand)
                { result?.Add(i); continue; }
                IrType type = i.Dest.Type;
                long signed = type == IrType.I32 ? unchecked((int)divisor.Value) : divisor.Value;
                ulong magnitude = signed < 0 ? unchecked(0UL - (ulong)signed) : (ulong)signed;
                // Leave zero and +/-1 alone, including MinValue/-1 overflow.
                if (magnitude < 2 || (magnitude & (magnitude - 1)) != 0)
                { result?.Add(i); continue; }
                result ??= block.Instrs.GetRange(0, n);
                int shift = System.Numerics.BitOperations.TrailingZeroCount(magnitude);
                VReg sign = Binary(Opcode.ShrS, i.Operands[0], new ImmOperand(type == IrType.I64 ? 63 : 31, IrType.I32));
                VReg bias = Binary(Opcode.And, RegOperand.Of(sign), new ImmOperand((long)(magnitude - 1), type));
                VReg adjusted = Binary(Opcode.Add, i.Operands[0], RegOperand.Of(bias));
                if (operation == Opcode.RemS)
                {
                    // Remainder has the numerator's sign, independent of the
                    // divisor's sign. This also handles the signed minimum.
                    VReg masked = Binary(Opcode.And, RegOperand.Of(adjusted), new ImmOperand((long)(magnitude - 1), type));
                    result.Add(new Instr { Op = Opcode.Sub, Dest = i.Dest, Line = i.Line,
                        Operands = { RegOperand.Of(masked), RegOperand.Of(bias) } });
                }
                else
                {
                    VReg quotient = Binary(Opcode.ShrS, RegOperand.Of(adjusted), new ImmOperand(shift, IrType.I32));
                    if (signed < 0)
                        result.Add(new Instr { Op = Opcode.Neg, Dest = i.Dest, Line = i.Line,
                            Operands = { RegOperand.Of(quotient) } });
                    else result.Add(IrInfo.CopyOf(i, RegOperand.Of(quotient)));
                }

                VReg Binary(Opcode op, Operand a, Operand b)
                {
                    VReg value = f.NewReg(type, "pow2div");
                    result!.Add(new Instr { Op = op, Dest = value, Line = i.Line, Operands = { a, b } });
                    return value;
                }
            }
            if (result is not null) { block.Instrs.Clear(); block.Instrs.AddRange(result); }
        }
    }

    /// <summary>The IR division a runtime helper of lowering's 64-bit division is, by its label; null for any other call.</summary>
    private static Opcode? RuntimeDivision(string? callee) => callee switch
    {
        "m_Runtime_DivU_2_V$I64_V$I64_r$V$I64" => Opcode.DivU,
        "m_Runtime_RemU_2_V$I64_V$I64_r$V$I64" => Opcode.RemU,
        "m_Runtime_DivS_2_V$I64_V$I64_r$V$I64" => Opcode.DivS,
        "m_Runtime_RemS_2_V$I64_V$I64_r$V$I64" => Opcode.RemS,
        // What DivU and RemU are once inlined: their unsigned halves.
        "m_Runtime_UDiv_2_V$U64_V$U64_r$V$U64" => Opcode.DivU,
        "m_Runtime_URem_2_V$U64_V$U64_r$V$U64" => Opcode.RemU,
        _ => null,
    };
}
