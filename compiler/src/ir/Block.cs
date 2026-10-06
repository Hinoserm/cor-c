#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

public sealed class Block
{
    public string Label { get; }
    public List<Instr> Instrs { get; } = new();

    // WHAT A BLOCK WEIGHS: a large unit holds about a hundred thousand of
    // them through code generation. Its two flags ride in the top bits of
    // Order, and the loop's bytes, which only a region loop's header has,
    // in a side object: a block is its label, its list, one word and one
    // reference nearly always null.
    private const int LandingPadBit = 1 << 30, RegionLoopBit = 1 << 29, OrderMask = RegionLoopBit - 1;
    private int _orderAndFlags;
    private BlockRare? _rare;

    /// <summary>
    /// Whether control can arrive here by an Unwind rather than a branch --
    /// a catch or finally landing pad. The backend must assume every
    /// register is dead on entry to such a block.
    /// </summary>
    public bool IsLandingPad
    {
        get => (_orderAndFlags & LandingPadBit) != 0;
        set => _orderAndFlags = value ? _orderAndFlags | LandingPadBit : _orderAndFlags & ~LandingPadBit;
    }

    /// <summary>
    /// The header of a loop the link gave a region of its own
    /// (Lto.RegionFacts.Loops, marked by Opt.RegionPointsTo.MarkLoops on the
    /// IR it regenerates the unit from), opened when the late passes reach
    /// the region pass. Never copied with the block: a copy is not the loop
    /// the link judged.
    /// </summary>
    public bool RegionLoop
    {
        get => (_orderAndFlags & RegionLoopBit) != 0;
        set => _orderAndFlags = value ? _orderAndFlags | RegionLoopBit : _orderAndFlags & ~RegionLoopBit;
    }

    /// <summary>With RegionLoop: the most one lap makes in the loop's region, as the link proved it (RegionFacts.LoopBytes); 0 when it could not.</summary>
    public long RegionLoopBytes
    {
        get => _rare?.RegionLoopBytes ?? 0;
        set { if (value != 0) (_rare ??= new()).RegionLoopBytes = value; else if (_rare is not null) _rare.RegionLoopBytes = 0; }
    }

    /// <summary>
    /// This block's position in its function, as the control-flow graph last
    /// saw it. Owned by <see cref="Opt.Cfg"/>, which is a snapshot of one
    /// function and numbers the blocks it was built from: a dense key lets
    /// its maps be arrays rather than dictionaries hashed on the block's
    /// reference. Meaningless before a graph has been built.
    /// </summary>
    public int Order
    {
        get => _orderAndFlags & OrderMask;
        set => _orderAndFlags = (_orderAndFlags & ~OrderMask) | (value & OrderMask);
    }

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

/// <summary>What few blocks carry (Block): a region loop's bytes a lap.</summary>
internal sealed class BlockRare
{
    public long RegionLoopBytes;
}
