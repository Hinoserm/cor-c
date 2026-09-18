#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Signed power-of-two division with a bias that preserves truncation toward zero.</summary>
public sealed class SignedPowerOfTwo : IPass
{
    public string Name => "signed-power-of-two";
    public void Run(Function f)
    {
        foreach (var block in f.Blocks)
        {
            List<Instr> result = new();
            foreach (Instr i in block.Instrs)
            {
                // On targets without native I64, lowering spells language
                // division as these compiler-owned arithmetic helpers. They
                // obey the same signed quotient/remainder contract; unrelated
                // calls must never be treated as pure arithmetic.
                Opcode operation = i.Op;
                if (i.Op == Opcode.Call && i.Dest?.Type == IrType.I64)
                    operation = i.Callee switch
                    {
                        "m_Runtime_DivS_2_V$I64_V$I64" => Opcode.DivS,
                        "m_Runtime_RemS_2_V$I64_V$I64" => Opcode.RemS,
                        _ => i.Op,
                    };
                if (operation is not (Opcode.DivS or Opcode.RemS) || i.Dest?.Type is not (IrType.I32 or IrType.I64)
                    || i.Operands.Count != 2 || i.Operands[1] is not ImmOperand divisor)
                { result.Add(i); continue; }
                // Let ordinary folding evaluate a wholly constant IR divide
                // directly instead of expanding it into a chain needing more
                // propagation rounds. Runtime calls have no such folder.
                if (i.Op != Opcode.Call && i.Operands[0] is ImmOperand)
                { result.Add(i); continue; }
                IrType type = i.Dest.Type;
                long signed = type == IrType.I32 ? unchecked((int)divisor.Value) : divisor.Value;
                ulong magnitude = signed < 0 ? unchecked(0UL - (ulong)signed) : (ulong)signed;
                // Leave zero and +/-1 alone, including MinValue/-1 overflow.
                if (magnitude < 2 || (magnitude & (magnitude - 1)) != 0)
                { result.Add(i); continue; }
                int shift = System.Numerics.BitOperations.TrailingZeroCount(magnitude);
                VReg sign = Binary(Opcode.ShrS, i.Operands[0], new ImmOperand(type == IrType.I64 ? 63 : 31, IrType.I32));
                VReg bias = Binary(Opcode.And, new RegOperand(sign), new ImmOperand((long)(magnitude - 1), type));
                VReg adjusted = Binary(Opcode.Add, i.Operands[0], new RegOperand(bias));
                if (operation == Opcode.RemS)
                {
                    // Remainder has the numerator's sign, independent of the
                    // divisor's sign. This also handles the signed minimum.
                    VReg masked = Binary(Opcode.And, new RegOperand(adjusted), new ImmOperand((long)(magnitude - 1), type));
                    result.Add(new Instr { Op = Opcode.Sub, Dest = i.Dest, Line = i.Line,
                        Operands = { new RegOperand(masked), new RegOperand(bias) } });
                }
                else
                {
                    VReg quotient = Binary(Opcode.ShrS, new RegOperand(adjusted), new ImmOperand(shift, IrType.I32));
                    if (signed < 0)
                        result.Add(new Instr { Op = Opcode.Neg, Dest = i.Dest, Line = i.Line,
                            Operands = { new RegOperand(quotient) } });
                    else result.Add(IrInfo.CopyOf(i, new RegOperand(quotient)));
                }

                VReg Binary(Opcode op, Operand a, Operand b)
                {
                    VReg value = f.NewReg(type, "pow2div");
                    result.Add(new Instr { Op = op, Dest = value, Line = i.Line, Operands = { a, b } });
                    return value;
                }
            }
            block.Instrs.Clear(); block.Instrs.AddRange(result);
        }
    }
}
