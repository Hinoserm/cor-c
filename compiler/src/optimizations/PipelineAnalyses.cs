#nullable enable
using System.Runtime.CompilerServices;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE FLOW GRAPH AND THE DEFINITIONS OF THE FUNCTION THE PIPELINE IS ON,
/// kept from one pass to the next while the function still has the shape
/// they were made of (Pipeline.Run opens it per function, per thread). Pass
/// after pass made its own -- a Cfg and a Defs each, arrays sized by every
/// block and register -- and most passes change nothing: in a native compile
/// the flow graphs, their dominators and the definitions were a fifth of what
/// the collector freed.
///
/// KEYED BY A FINGERPRINT OF WHAT THEY ARE MADE OF, not by what passes say:
/// the blocks in order, and for every instruction the object it is, its
/// opcode, the register it defines and the blocks it goes to. A pass that
/// puts a new instruction in an old one's place, retargets a branch, moves a
/// block or renames a definition changes it; one that rewrites operands in
/// place does not, and changes neither a flow graph nor where a register is
/// defined. Liveness, which reads operands, is not kept here.
///
/// Read and never written by their users, as AnalysisCache's. Under
/// --verify-analyses every answer kept is checked against one made afresh.
/// </summary>
internal static class PipelineAnalyses
{
    [ThreadStatic] private static Function? _f;
    [ThreadStatic] private static long _print;
    [ThreadStatic] private static long _shapePrint;
    [ThreadStatic] private static Cfg? _cfg;
    [ThreadStatic] private static Defs? _withCfg, _withoutCfg, _ssaWithCfg;

    private static readonly bool Verifying = Switches.VerifyAnalyses;

    /// <summary>Kept for `f` from here until End; false when something is already kept (a nested pipeline), which keeps its own.</summary>
    public static bool Begin(Function f)
    {
        if (_f is not null) return false;
        _f = f;
        _print = 0;
        _shapePrint = 0;
        _cfg = null; _withCfg = null; _withoutCfg = null; _ssaWithCfg = null;
        return true;
    }

    public static void End()
    {
        _f = null;
        _cfg = null; _withCfg = null; _withoutCfg = null; _ssaWithCfg = null;
    }

    // By index, and the targets' array itself: the fingerprint is taken at
    // every question, and enumerators over every instruction's targets were
    // a twentieth of a native compile. An instruction's opcode is fixed as
    // it is made (init), so the object it is says it.
    //
    // TWO PRINTS IN ONE WALK: everything, for the definitions; and the
    // graph's own shape -- the blocks in order, which are landing pads, the
    // blocks a LabelAddr names and every terminator's targets -- for the flow
    // graph, which nothing else goes into (Cfg's constructor). Most passes
    // rewrite instructions and leave the edges alone: the graph, its
    // dominators and its order were made again after every one of them, a
    // tenth of what the collector took in a native compile.
    private static (long All, long Shape) Prints(Function f)
    {
        long h = 17, g = 19;
        List<Block> blocks = f.Blocks;
        for (int n = 0; n < blocks.Count; n++)
        {
            Block b = blocks[n];
            int id = RuntimeHelpers.GetHashCode(b);
            h = h * 31 + id;
            g = g * 31 + id + (b.IsLandingPad ? 1 : 0);
            List<Instr> instrs = b.Instrs;
            int count = instrs.Count;
            for (int k = 0; k < count; k++)
            {
                Instr i = instrs[k];
                h = h * 31 + RuntimeHelpers.GetHashCode(i);
                if (i.Dest is { } d) h = h * 31 + d.Id;
                if (i.TargetBlocks is { } targets)
                {
                    // A terminator's targets and a LabelAddr's are the graph's.
                    bool edges = k == count - 1 && i.IsTerminator || i.Op == Opcode.LabelAddr;
                    for (int t = 0; t < targets.Length; t++)
                    {
                        int target = RuntimeHelpers.GetHashCode(targets[t]);
                        h = h * 31 + target;
                        if (edges) g = g * 37 + target;
                    }
                }
                if (i.Default is { } fallback)
                {
                    int target = RuntimeHelpers.GetHashCode(fallback);
                    h = h * 31 + target;
                    if (k == count - 1 && i.IsTerminator) g = g * 37 + target;
                }
            }
            h = h * 31 + count;
            // Where a block ends without a terminator, the graph has no edge out of it.
            g = g * 41 + (count > 0 && instrs[count - 1].IsTerminator ? 1 : 0);
        }
        return (h * 31 + f.RegCount, g * 31 + blocks.Count);
    }

    /// <summary>Whether what is kept is `f`'s as it is now; what no longer is, is kept no more.</summary>
    private static bool Current(Function f)
    {
        if (!ReferenceEquals(_f, f)) return false;
        (long now, long shape) = Prints(f);
        if (shape != _shapePrint)
        {
            _shapePrint = shape;
            _cfg = null; _withCfg = null; _ssaWithCfg = null;
        }
        if (now != _print)
        {
            _print = now;
            _withCfg = null; _withoutCfg = null; _ssaWithCfg = null;
        }
        return true;
    }

    public static Cfg CfgOf(Function f)
    {
        if (!Current(f)) return new Cfg(f);
        if (_cfg is { } kept)
        {
            if (Verifying) Same(kept, new Cfg(f), f);
            return kept;
        }
        return _cfg = new Cfg(f);
    }

    /// <summary>
    /// The definitions in SSA form (Defs.Ssa), on the flow graph kept: what
    /// constant propagation and the peephole ask of a function in SSA.
    /// </summary>
    public static Defs SsaDefsOf(Function f)
    {
        if (!Current(f)) return new Defs(f, ssa: true);
        if (_ssaWithCfg is { } kept)
        {
            if (Verifying) Same(kept, new Defs(f, buildCfg: false), f);
            return kept;
        }
        return _ssaWithCfg = new Defs(_cfg ??= new Cfg(f), ssa: true);
    }

    public static Defs DefsOf(Function f, bool buildCfg = true)
    {
        if (!Current(f)) return new Defs(f, buildCfg: buildCfg);
        if ((buildCfg ? _withCfg : _withoutCfg) is { } kept)
        {
            if (Verifying) Same(kept, new Defs(f, buildCfg: false), f);
            return kept;
        }
        Defs made = buildCfg ? new Defs(_cfg ??= new Cfg(f)) : new Defs(f, buildCfg: false);
        if (buildCfg) _withCfg = made; else _withoutCfg = made;
        return made;
    }

    private static void Same(Cfg kept, Cfg fresh, Function f)
    {
        bool same = kept.ReversePostorder.Count == fresh.ReversePostorder.Count;
        for (int k = 0; same && k < fresh.ReversePostorder.Count; k++) same = ReferenceEquals(kept.ReversePostorder[k], fresh.ReversePostorder[k]);
        foreach (Block b in f.Blocks)
        {
            if (!same) break;
            same = kept.Preds(b).Count == fresh.Preds(b).Count && kept.Succs(b).Count == fresh.Succs(b).Count;
        }
        if (!same) throw new InvalidOperationException("pipeline analyses: " + f.Name + "'s flow graph changed without its fingerprint");
    }

    private static void Same(Defs kept, Defs fresh, Function f)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d && (kept.Count(d) != fresh.Count(d) || kept.Site(d) != fresh.Site(d)))
                    throw new InvalidOperationException("pipeline analyses: " + f.Name + "'s definition of " + d + " changed without its fingerprint");
    }
}
