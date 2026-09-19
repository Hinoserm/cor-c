#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

public sealed class Block
{
    public string Label { get; }
    public List<Instr> Instrs { get; } = new();

    /// <summary>
    /// Whether control can arrive here by an Unwind rather than a branch --
    /// a catch or finally landing pad. The backend must assume every
    /// register is dead on entry to such a block.
    /// </summary>
    public bool IsLandingPad { get; set; }

    /// <summary>
    /// This block's position in its function, as the control-flow graph last
    /// saw it. Owned by <see cref="Opt.Cfg"/>, which is a snapshot of one
    /// function and numbers the blocks it was built from: a dense key lets
    /// its maps be arrays rather than dictionaries hashed on the block's
    /// reference. Meaningless before a graph has been built.
    /// </summary>
    public int Order;

    internal Block(string label) => Label = label;

    public Instr? Terminator => Instrs.Count > 0 && Instrs[^1].IsTerminator ? Instrs[^1] : null;

    public IEnumerable<Block> Successors
    {
        get
        {
            Instr? t = Terminator;
            if (t is null)
            {
                yield break;
            }
            foreach (Block b in t.Targets)
            {
                yield return b;
            }
            if (t.Default is not null)
            {
                yield return t.Default;
            }
        }
    }

    public override string ToString() => Label;
}
