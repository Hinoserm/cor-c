#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// WHAT A FUNCTION IS MADE OF, AS IT WAS WHEN AN ANALYSIS OF IT WAS KEPT:
/// its parameters, its register count, its blocks in order with their landing
/// pad marks, and each block's instructions in order with what each writes,
/// reads and goes to. The proof --verify-analyses holds every analysis the
/// lifetime rules keep to (AnalysisCache): a function that still matches is
/// the function the analysis was made of -- an instruction is the same object
/// (its operation and callee cannot change), writing the same register,
/// reading the same operands (a register's operand is the one shared object,
/// RegOperand.Of; any other is the object it was), in the same place. A kept
/// answer whose function no longer matches is an in-place edit that did not
/// say so (Function.Edited), and the run stops there.
/// </summary>
internal sealed class FunctionShape
{
    private readonly Function _f;
    /// <summary>The function the shape was taken of.</summary>
    public Function For => _f;
    private readonly object?[] _parts;
    private readonly int _registers;

    private static readonly object Pad = new(), NotPad = new(), EndBlock = new(), EndInstr = new();

    private FunctionShape(Function f, object?[] parts)
    {
        _f = f;
        _parts = parts;
        _registers = f.RegCount;
    }

    public static FunctionShape Of(Function f)
    {
        // Written straight into an array of the exact size: no list grown,
        // no delegate a part.
        object?[] parts = new object?[Size(f)];
        int at = 0;
        foreach (VReg p in f.Params) parts[at++] = p;
        parts[at++] = EndBlock;
        foreach (Block b in f.Blocks)
        {
            parts[at++] = b;
            parts[at++] = b.IsLandingPad ? Pad : NotPad;
            foreach (Instr i in b.Instrs)
            {
                parts[at++] = i;
                parts[at++] = i.Dest;
                for (int n = 0; n < i.Operands.Count; n++) parts[at++] = i.Operands[n];
                foreach (Block t in i.Targets) parts[at++] = t;
                parts[at++] = i.Default;
                parts[at++] = EndInstr;
            }
            parts[at++] = EndBlock;
        }
        if (at != parts.Length) Array.Resize(ref parts, at);
        return new FunctionShape(f, parts);
    }

    /// <summary>Whether `f` is still the function this shape was taken of, part for part.</summary>
    public bool Matches(Function f)
    {
        if (!ReferenceEquals(f, _f) || f.RegCount != _registers) return false;
        int at = 0;
        object?[] parts = _parts;
        bool same = true;
        // The same walk Of took, stopped at the first difference.
        foreach (VReg p in f.Params) { if (!Next(p)) return false; }
        if (!Next(EndBlock)) return false;
        foreach (Block b in f.Blocks)
        {
            if (!Next(b) || !Next(b.IsLandingPad ? Pad : NotPad)) return false;
            foreach (Instr i in b.Instrs)
            {
                if (!Next(i) || !Next(i.Dest)) return false;
                for (int n = 0; n < i.Operands.Count; n++) if (!Next(i.Operands[n])) return false;
                foreach (Block t in i.Targets) if (!Next(t)) return false;
                if (!Next(i.Default) || !Next(EndInstr)) return false;
            }
            if (!Next(EndBlock)) return false;
        }
        return same && at == parts.Length;

        bool Next(object? part)
        {
            if (at >= parts.Length || !ReferenceEquals(parts[at], part)) return false;
            at++;
            return true;
        }
    }

    private static int Size(Function f)
    {
        int n = f.Params.Count + 1;
        foreach (Block b in f.Blocks)
        {
            n += 3;
            foreach (Instr i in b.Instrs) n += 4 + i.Operands.Count + i.Targets.Count;
        }
        return n;
    }
}

/// <summary>
/// THE LIFETIME RULES' ANALYSES OF THE FUNCTIONS THEY ARE ASKING ABOUT, kept
/// with each function's shape and handed back while the function still has
/// it (FunctionShape): a run asks the same functions for their register
/// writes, their definitions and their liveness again and again -- per
/// allocation judged, per phase, and of a callee in between -- and most of
/// the time nothing between two questions changed the function.
/// A FEW FUNCTIONS, MOST RECENT FIRST: the questions come a function at a
/// time, with its callees asked about in between, so one kept function was
/// mostly the wrong one.
/// KEYED BY WHAT THE FUNCTION SAYS OF ITS EDITS AND BY ITS COUNTS (KeyOf), at
/// the cost of a count of its instructions per question. A snapshot of its
/// whole shape, checked part for part, was tried first and cost as much to
/// take as the analyses it saved; it stays as the proof --verify-analyses
/// checks every kept answer against. Kept for the length of a run (Escape.Run), and only
/// while one runs. Every analysis kept here is read and never written by its
/// users, but for the landing pads' table a liveness works out when asked.
/// </summary>
internal static class AnalysisCache
{
    private const int KeptFunctions = 8;

    private sealed class Entry
    {
        public required Function F;
        public (int Edits, int Registers, int Blocks, int Instructions) Key;
        // Under --verify-analyses, the function's whole shape when the entry
        // was keyed, to prove the key right at every answer it gives.
        public FunctionShape? Proof;
        public RegisterWrites? Writes;
        public Liveness? Liveness;
        public Defs? DefsWithCfg, DefsWithoutCfg;
        // One flow graph for whatever of the above needs one, and the rest
        // kept by their askers' own kinds (Kept).
        public Cfg? Cfg;
        public readonly object?[] Other = new object?[OtherKinds];
    }

    /// <summary>The other kinds kept (Kept): the promotion order, the use index, the single definitions.</summary>
    public const int PromotionOrder = 0, UseIndex = 1, SingleDefs = 2, OtherKinds = 3;

    private static readonly bool Verifying = Switches.VerifyAnalyses;

    [ThreadStatic] private static List<Entry>? _entries;

    public static void Open() => _entries = new(KeptFunctions);

    public static void Close() => _entries = null;

    /// <summary>
    /// What is kept of `f` in the state it is in now: its entry, moved to
    /// the front, emptied and given a new shape when `f` has changed since;
    /// a new entry, the oldest let go, when it was not kept. One shape for
    /// every kind of analysis, taken once a state.
    /// </summary>
    private static Entry Now(Function f, List<Entry> entries)
    {
        var key = KeyOf(f);
        for (int k = 0; k < entries.Count; k++)
        {
            Entry e = entries[k];
            if (!ReferenceEquals(e.F, f)) continue;
            if (k != 0) { entries.RemoveAt(k); entries.Insert(0, e); }
            if (!e.Key.Equals(key))
            {
                e.Key = key;
                e.Writes = null; e.Liveness = null; e.DefsWithCfg = null; e.DefsWithoutCfg = null; e.Cfg = null;
                Array.Clear(e.Other);
                if (Verifying) e.Proof = FunctionShape.Of(f);
            }
            else if (Verifying && !e.Proof!.Matches(f))
                throw new InvalidOperationException("analysis cache: " + f.Name + " was edited in place without saying so (Function.Edited)");
            return e;
        }
        if (entries.Count == KeptFunctions) entries.RemoveAt(KeptFunctions - 1);
        Entry made = new() { F = f, Key = key, Proof = Verifying ? FunctionShape.Of(f) : null };
        entries.Insert(0, made);
        return made;
    }

    /// <summary>
    /// The function's edits (Function.Edited) and its counts: an insertion or
    /// a removal changes a count whether or not its author said so; an
    /// instruction replaced by another is said (Edited).
    /// </summary>
    private static (int, int, int, int) KeyOf(Function f)
    {
        int instructions = 0;
        foreach (Block b in f.Blocks) instructions += b.Instrs.Count;
        return (f.Edits, f.RegCount, f.Blocks.Count, instructions);
    }

    public static RegisterWrites WritesOf(Function f)
    {
        if (_entries is not { } entries) return new RegisterWrites(f);
        Entry e = Now(f, entries);
        return e.Writes ??= new RegisterWrites(f);
    }

    public static Liveness LivenessOf(Function f)
    {
        if (_entries is not { } entries) return new Liveness(f);
        Entry e = Now(f, entries);
        if (e.Liveness is { } kept)
        {
            // As a fresh one has them: where its landing pads are entered is
            // worked out when first asked (Escape.PadLiveAt), from the function
            // as it is then, and the last user may have asked before edits
            // the next one makes before asking.
            kept.PadRegions = null;
            kept.PadFrom = null;
            return kept;
        }
        return e.Liveness = new Liveness(e.Cfg ??= new Cfg(f));
    }

    /// <summary>
    /// An analysis of a kind of the asker's own (`which`, one of the kinds
    /// above), made by `make` when none of the function in this state is
    /// kept. Never written by its users, as the rest.
    /// </summary>
    public static T Kept<T>(Function f, int which, Func<Function, T> make) where T : class
    {
        if (_entries is not { } entries) return make(f);
        Entry e = Now(f, entries);
        if (e.Other[which] is T kept) return kept;
        T made = make(f);
        e.Other[which] = made;
        return made;
    }

    /// <summary>The function's flow graph in this state, shared by the analyses kept that need one.</summary>
    public static Cfg CfgOf(Function f)
    {
        if (_entries is not { } entries) return new Cfg(f);
        Entry e = Now(f, entries);
        return e.Cfg ??= new Cfg(f);
    }

    public static Defs DefsOf(Function f, bool buildCfg = true)
    {
        if (_entries is not { } entries) return new Defs(f, buildCfg: buildCfg);
        Entry e = Now(f, entries);
        return buildCfg ? e.DefsWithCfg ??= new Defs(e.Cfg ??= new Cfg(f)) : e.DefsWithoutCfg ??= new Defs(f, buildCfg: false);
    }
}
