#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Where every register is defined, and the question the forwarding passes
/// keep asking: may a register be read somewhere other than where it was
/// read originally?
///
/// The IR is not SSA, so "single definition" is the only thing that makes
/// a register's value knowable without a reaching-definitions pass. A
/// parameter counts as one definition, on entry. But one definition is not
/// enough: a definition inside a loop executes many times, and a copy of
/// it taken in one iteration is not the register as read in the next.
/// <see cref="CanForward"/> is the check, and every pass that replaces a
/// use of one register by another goes through it. When SSA arrives this
/// class becomes trivial and the passes need not change.
/// </summary>
public sealed class Defs
{
    public Function Function { get; }
    private readonly Cfg? _cfg;
    public Cfg Cfg => _cfg ?? throw new InvalidOperationException("definition-only analysis has no control-flow snapshot");

    // BY REGISTER NUMBER, NOT BY IDENTITY. A function numbers its registers
    // densely as it makes them and says how many there are, so the map here
    // is an array: as dictionaries keyed on the register object they hashed
    // a reference for every lookup, and this analysis is built fifty
    // thousand times compiling one source. A register from outside the
    // function -- there should be none -- reads as never defined.
    //
    // ONE WORD A REGISTER: 0 never defined, Many defined more than once (a
    // parameter's entry counting as one), Param a parameter alone, else the
    // defining instruction's place counted through the blocks in order, plus
    // one; the block is found from the block starts (_blockStart). A count,
    // a block and an index a register were three arrays, built for every
    // question, and the most of them that a native compile left behind.
    private const int Many = -1, Param = -2;
    private readonly int[] _site;
    private readonly Block[] _blocks;
    private readonly int[] _blockStart;

    /// <summary>
    /// Whether the function is in SSA form: every register has one
    /// definition that dominates all its uses. Then a value read anywhere
    /// is the value read everywhere, and <see cref="CanForward"/> need
    /// only worry about landing-pad re-entry. The caller says so; it is
    /// not inferred, because single definitions alone do not imply it.
    /// </summary>
    public bool Ssa { get; }

    public Defs(Function f, bool ssa = false, bool buildCfg = true)
    {
        Ssa = ssa;
        _cfg = buildCfg ? new Cfg(f) : null;
        Function = f;
        int registers = f.RegCount;
        int[] site = _site = new int[registers];
        int blocks = f.Blocks.Count;
        _blocks = blocks == 0 ? Array.Empty<Block>() : new Block[blocks];
        _blockStart = new int[blocks + 1];
        foreach (VReg p in Function.Params)
        {
            if (p.Id < registers) site[p.Id] = site[p.Id] == 0 ? Param : Many;
        }
        int at = 0;
        for (int n = 0; n < blocks; n++)
        {
            Block b = f.Blocks[n];
            _blocks[n] = b;
            _blockStart[n] = at;
            List<Instr> instrs = b.Instrs;
            for (int k = 0; k < instrs.Count; k++)
            {
                VReg? d = instrs[k].Dest;
                if (d is not null && d.Id < registers)
                {
                    site[d.Id] = site[d.Id] == 0 ? at + k + 1 : Many;
                }
            }
            at += instrs.Count;
        }
        _blockStart[blocks] = at;
    }

    public Defs(Cfg cfg, bool ssa = false) : this(cfg.Function, ssa, false)
    {
        _cfg = cfg;
    }

    /// <summary>How many times `r` is defined, a parameter's entry counting as one: 0, 1, or 2 for more.</summary>
    public int Count(VReg r)
    {
        int w = r.Id < _site.Length ? _site[r.Id] : 0;
        return w == 0 ? 0 : w == Many ? 2 : 1;
    }

    private bool TrySite(VReg r, out (Block Block, int Index) site)
    {
        int w = r.Id < _site.Length ? _site[r.Id] : 0;
        if (w <= 0)
        {
            site = default;
            return false;
        }
        int at = w - 1;
        int lo = 0, hi = _blocks.Length - 1;
        // The last block starting at or before `at`: blocks without
        // instructions start where the next one does, and hold none of them.
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_blockStart[mid] <= at) lo = mid;
            else hi = mid - 1;
        }
        site = (_blocks[lo], at - _blockStart[lo]);
        return true;
    }

    public bool IsSingle(VReg r) => Count(r) == 1;

    /// <summary>Where a single-def register is defined, or null for a parameter or a multi-def register.</summary>
    public (Block Block, int Index)? Site(VReg r)
        => IsSingle(r) && TrySite(r, out (Block Block, int Index) s) ? s : null;

    /// <summary>The one instruction defining a single-def register, or null for a parameter or a multi-def register.</summary>
    public Instr? Definition(VReg r)
        => IsSingle(r) && TrySite(r, out (Block Block, int Index) s) ? s.Block.Instrs[s.Index] : null;

    /// <summary>
    /// Whether a read of <paramref name="r"/> made at (<paramref name="fromBlock"/>,
    /// <paramref name="fromIndex"/>) may be moved to (<paramref name="useBlock"/>,
    /// <paramref name="useIndex"/>) and still see the same value. That is:
    /// on no path from the first point to the second does r's definition
    /// execute -- ignoring paths that pass the first point again, because
    /// those re-take the original read as well and a later use sees the
    /// later value either way.
    ///
    /// This is what lets <c>v = copy w</c> followed somewhere by a use of v
    /// become a use of w, and what lets <c>t = lt a b; c = eq t 0</c>
    /// become <c>c = ge a b</c>: the operands are re-read at the new place.
    /// </summary>
    public bool CanForward(VReg r, Block fromBlock, int fromIndex, Block useBlock, int useIndex)
    {
        if (!IsSingle(r))
        {
            return false;
        }
        if (!TrySite(r, out (Block Block, int Index) s))
        {
            return true;        // a parameter: defined once, on entry, never again
        }

        if (Cfg.IsRoot(s.Block) && !(ReferenceEquals(s.Block, fromBlock) && ReferenceEquals(s.Block, useBlock)))
        {
            // A landing pad runs again on every unwind into it, along an
            // edge the graph does not show, so a value defined there (the
            // exception itself, say) is fresh each time and only its own
            // block can trust it.
            return false;
        }
        if (Ssa)
        {
            return true;
        }

        if (ReferenceEquals(s.Block, fromBlock))
        {
            if (s.Index >= fromIndex)
            {
                // Defined after the read in the same block, so the read saw
                // the previous trip's value. The definition executes before
                // any later point of this block is reached again, except
                // the straight run from the read up to the definition.
                return ReferenceEquals(useBlock, fromBlock) && useIndex > fromIndex && useIndex <= s.Index;
            }
            // Defined earlier in this block: any path that runs it again
            // re-enters the block at its top and passes the read too --
            // unless the use sits between the definition and the read, in
            // which case the next trip reaches it with the new value first.
            return !(ReferenceEquals(useBlock, fromBlock) && useIndex < fromIndex && useIndex > s.Index);
        }

        // Defined in another block: unsafe exactly when that block lies on
        // some path onward from the read that does not pass the read again.
        return !Cfg.ReachesWithoutReentering(fromBlock, s.Block);
    }
}
