#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

public sealed class Instr
{
    public Opcode Op { get; init; }
    public VReg? Dest { get; set; }
    public List<Operand> Operands { get; } = new();

    /// <summary>Load and Store: how many bytes, and whether a narrow load sign-extends.</summary>
    public int Size { get; init; }
    public bool Signed { get; init; }

    /// <summary>Load and Store: a constant displacement from the address operand.</summary>
    public long Offset { get; init; }

    /// <summary>Call: who.</summary>
    public string? Callee { get; init; }

    /// <summary>Jump, Branch, Switch, LabelAddr: where. Phi: the predecessor each operand comes from.</summary>
    public List<Block> Targets { get; } = new();

    /// <summary>Switch: where an out-of-range index goes.</summary>
    public Block? Default { get; set; }

    /// <summary>
    /// Source line, for the dump and for the line table a stack trace reads.
    ///
    /// Settable so the builder can stamp it as instructions go by, rather than
    /// every one of the hundreds of places that build an Instr having to
    /// remember to pass it.
    /// </summary>
    public int Line { get; set; }

    public bool IsTerminator => Op is Opcode.Ret or Opcode.Jump or Opcode.Branch
                                   or Opcode.Switch or Opcode.Unreachable or Opcode.Unwind;

    public override string ToString()
    {
        StringBuilder sb = new();
        if (Dest is not null)
        {
            sb.Append(Dest).Append(" = ");
        }
        sb.Append(Op.ToString().ToLowerInvariant());
        if (Op is Opcode.Load or Opcode.Store)
        {
            sb.Append(Signed ? ".s" : ".u").Append(Size);
        }
        if (Callee is not null)
        {
            sb.Append(' ').Append(Callee);
        }
        foreach (Operand o in Operands)
        {
            sb.Append(' ').Append(o);
        }
        if (Offset != 0)
        {
            sb.Append(" +").Append(Offset);
        }
        foreach (Block b in Targets)
        {
            sb.Append(" ->").Append(b.Label);
        }
        if (Default is not null)
        {
            sb.Append(" default->").Append(Default.Label);
        }
        return sb.ToString();
    }
}
