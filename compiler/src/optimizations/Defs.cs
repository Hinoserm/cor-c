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

    private readonly Dictionary<VReg, int> _count = new();
    private readonly Dictionary<VReg, (Block Block, int Index)> _site = new();

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
        foreach (VReg p in Function.Params)
        {
            _count[p] = 1;
        }
        foreach (Block b in Function.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                VReg? d = b.Instrs[k].Dest;
                if (d is null)
                {
                    continue;
                }
                _count[d] = _count.GetValueOrDefault(d) + 1;
                _site[d] = (b, k);
            }
        }
    }

    public Defs(Cfg cfg, bool ssa = false) : this(cfg.Function, ssa, false)
    {
        _cfg = cfg;
    }

    public int Count(VReg r) => _count.GetValueOrDefault(r);
    public bool IsSingle(VReg r) => Count(r) == 1;

    /// <summary>Where a single-def register is defined, or null for a parameter or a multi-def register.</summary>
    public (Block Block, int Index)? Site(VReg r)
        => IsSingle(r) && _site.TryGetValue(r, out (Block Block, int Index) s) ? s : null;

    /// <summary>The one instruction defining a single-def register, or null for a parameter or a multi-def register.</summary>
    public Instr? Definition(VReg r)
        => IsSingle(r) && _site.TryGetValue(r, out (Block Block, int Index) s) ? s.Block.Instrs[s.Index] : null;

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
        if (!_site.TryGetValue(r, out (Block Block, int Index) s))
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
