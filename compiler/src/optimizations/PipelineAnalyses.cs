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
    [ThreadStatic] private static Cfg? _cfg;
    [ThreadStatic] private static Defs? _withCfg, _withoutCfg;

    private static readonly bool Verifying = Switches.VerifyAnalyses;

    /// <summary>Kept for `f` from here until End; false when something is already kept (a nested pipeline), which keeps its own.</summary>
    public static bool Begin(Function f)
    {
        if (_f is not null) return false;
        _f = f;
        _print = 0;
        _cfg = null; _withCfg = null; _withoutCfg = null;
        return true;
    }

    public static void End()
    {
        _f = null;
        _cfg = null; _withCfg = null; _withoutCfg = null;
    }

    private static long Print(Function f)
    {
        long h = 17;
        foreach (Block b in f.Blocks)
        {
            h = h * 31 + RuntimeHelpers.GetHashCode(b);
            foreach (Instr i in b.Instrs)
            {
                h = h * 31 + RuntimeHelpers.GetHashCode(i);
                h = h * 31 + (int)i.Op;
                h = h * 31 + (i.Dest?.Id ?? -1);
                foreach (Block t in i.Targets) h = h * 31 + RuntimeHelpers.GetHashCode(t);
                if (i.Default is { } d) h = h * 31 + RuntimeHelpers.GetHashCode(d);
            }
            h = h * 31 + b.Instrs.Count;
        }
        return h * 31 + f.RegCount;
    }

    /// <summary>Whether what is kept is `f`'s as it is now; if not, nothing is kept any more.</summary>
    private static bool Current(Function f)
    {
        if (!ReferenceEquals(_f, f)) return false;
        long now = Print(f);
        if (now != _print)
        {
            _print = now;
            _cfg = null; _withCfg = null; _withoutCfg = null;
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
