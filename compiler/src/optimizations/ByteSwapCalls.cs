#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Expose the compiler runtime's byte-reversal primitive before its loop is inlined.</summary>
public sealed class ByteSwapCalls : IPass
{
    public string Name => "byte-swap-calls";
    public void Run(Function f)
    {
        foreach (var block in f.Blocks)
        for (int k = 0; k < block.Instrs.Count; k++)
        {
            Instr i = block.Instrs[k];
            // Sys.ByteSwap's two widths: one BSWAP r32 for the int form, where
            // the long form's low half alone was a 64-bit swap and a shift.
            if (i.Op == Opcode.Call && i.Operands.Count == 1
                && (i.Callee == "m_Runtime_ByteSwap_1_V$I64_r$V$I64" && i.Dest?.Type == IrType.I64 && i.Operands[0].Type == IrType.I64
                    || i.Callee == "m_Runtime_ByteSwap_1_V$I32_r$V$I32" && i.Dest?.Type == IrType.I32 && i.Operands[0].Type == IrType.I32))
                block.Instrs[k] = new Instr { Op = Opcode.ByteSwap, Dest = i.Dest, Line = i.Line,
                    Operands = { i.Operands[0] } };
        }
    }
}
