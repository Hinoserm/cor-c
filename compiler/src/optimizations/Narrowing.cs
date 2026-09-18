#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Width changes that cancel.
///
/// The language carries addresses and counts as 64-bit longs and the
/// machine holds them in 32-bit words, so an intrinsic call widens an int
/// to a long and the lowering truncates it straight back: `trunc64(sext32
/// x)` is x, and on a 486 the pair it built was two instructions of
/// nothing. Immediates through a conversion become immediates. A widening
/// whose only use is a truncation disappears with the truncation.
///
/// Only single-definition registers are rewritten, for the same reason as
/// every forwarding pass here: without SSA a register with two
/// definitions is not a value.
/// </summary>
public sealed class Narrowing : IPass
{
    public string Name => "narrowing";

    public void Run(Function f)
    {
        Defs defs = new(f);
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];

                // The address of a symbol plus a constant is an address the
                // linker can compute: one immediate rather than a load and
                // an add. Subtraction is an add of the negation.
                if (i.Op is Opcode.Add or Opcode.Sub && i.Dest is not null && i.Dest.Type == IrTypes.Word
                    && i.Operands[0] is RegOperand basis && i.Operands[1] is ImmOperand delta
                    && defs.IsSingle(basis.Reg) && defs.Definition(basis.Reg) is { Op: Opcode.Copy } symDef
                    && symDef.Operands[0] is SymOperand sym)
                {
                    long offset = i.Op == Opcode.Add ? delta.Value : -delta.Value;
                    b.Instrs[k] = new Instr
                    {
                        Op = Opcode.Copy, Dest = i.Dest, Line = i.Line,
                        Operands = { new SymOperand(sym.Name, sym.Offset + offset) },
                    };
                    continue;
                }

                if (i.Dest is null || i.Operands.Count != 1 || !defs.IsSingle(i.Dest))
                {
                    continue;
                }

                Operand src = i.Operands[0];

                // A conversion of a constant is a constant.
                if (src is ImmOperand imm)
                {
                    long v = imm.Value;
                    long? folded = i.Op switch
                    {
                        Opcode.SExt32 => (int)v,
                        Opcode.ZExt32 => (uint)v,
                        Opcode.Trunc64 => (int)v,
                        Opcode.SExt8 => (sbyte)v,
                        Opcode.ZExt8 => (byte)v,
                        Opcode.SExt16 => (short)v,
                        Opcode.ZExt16 => (ushort)v,
                        _ => null,
                    };
                    if (folded is long value)
                    {
                        b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Operands = { new ImmOperand(value, i.Dest.Type) }, Line = i.Line };
                    }
                    continue;
                }

                if (i.Op != Opcode.Trunc64 || src is not RegOperand r)
                {
                    continue;
                }

                // trunc64 of a widening of x is x, when x is knowable here.
                Instr? def = defs.IsSingle(r.Reg) ? defs.Definition(r.Reg) : null;
                if (def is { Op: Opcode.SExt32 or Opcode.ZExt32 } && def.Operands[0] is RegOperand inner
                    && defs.Site(r.Reg) is (Block defBlock, int defIndex)
                    && defs.CanForward(inner.Reg, defBlock, defIndex, b, k))
                {
                    b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Operands = { inner }, Line = i.Line };
                    continue;
                }

                // trunc64 of an operation whose low word depends only on its
                // operands' low words is that operation on the low words: an
                // address widened to a long, offset, and truncated back is
                // one 32-bit add, not a pair add and two moves.
                if (def is { Op: Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor }
                    && defs.Site(r.Reg) is not null)
                {
                    Operand? a = LowWord(def.Operands[0], defs, b, k);
                    Operand? c = a is null ? null : LowWord(def.Operands[1], defs, b, k);
                    if (a is not null && c is not null)
                    {
                        b.Instrs[k] = new Instr { Op = def.Op, Dest = i.Dest, Operands = { a, c }, Line = i.Line };
                    }
                }
            }
        }

        // Copy propagation and dead-code elimination finish the job: the
        // Copies above are what those passes eat.
    }

    /// <summary>
    /// The low 32 bits of a 64-bit operand as an I32 operand at the use
    /// site, or null when that would need a value not knowable there. A
    /// widening of an I32 register is that register; an immediate is its
    /// low word. Anything else is left alone: inserting a truncation would
    /// shift every index the definition table holds for this block, and a
    /// stale table is how a later fold reads the wrong instruction.
    /// </summary>
    private static Operand? LowWord(Operand o, Defs defs, Block use, int useIndex)
    {
        switch (o)
        {
            case ImmOperand imm:
                return new ImmOperand((int)imm.Value, IrType.I32);

            case RegOperand r:
            {
                Instr? d = defs.IsSingle(r.Reg) ? defs.Definition(r.Reg) : null;
                if (d is { Op: Opcode.SExt32 or Opcode.ZExt32 } && d.Operands[0] is RegOperand inner
                    && defs.Site(r.Reg) is (Block dBlock, int dIndex)
                    && defs.CanForward(inner.Reg, dBlock, dIndex, use, useIndex))
                {
                    return inner;
                }
                return null;
            }

            default:
                return null;
        }
    }
}
