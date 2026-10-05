#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// --verify-marks: WHETHER A PASS LOST A MARK THE ANALYSES PUT ON THE IR.
/// An instruction carries what lowering and the link learned of it -- a
/// number never an address (Instr.Number), the field it reads or writes
/// (Field, Family), a site the link chose for a region (RegionSite), a
/// virtual call's declaring type (DispatchType) -- and a block the loop
/// region the link gave it (Block.RegionLoop, RegionLoopBytes), a parameter
/// that it is a number (VReg.Number). Every one of them is a property a
/// pass that rebuilds an instruction must copy by hand, and a pass that
/// forgets says nothing: the program still runs, a number is read as an
/// address again or a field joins every other at its offset, and regions
/// are lost three passes and a link later. FrameAddressFold forgot twice,
/// the archive's rollback once.
///
/// So, before each pass, every marked instruction is noted with its marks
/// (Snapshot); after it (Check), each that is still there -- the same
/// instruction, or the one that took its place one for one -- must have
/// them still. Taking its place: the instruction now defining the same
/// register, of the same kind (a call is a call, direct or not); or, for
/// one defining nothing, the new instruction at the same place in the
/// same block. One that went, or became something else -- a load folded
/// to a copy, a call inlined -- lost nothing: what it was is gone.
///
/// What a pass consumes on purpose is not a loss: the link's region sites
/// and loops, which region-points-to turns into region calls, and the
/// sites escape takes back from elements it cannot place beside their
/// collection (Escape.ElementSites); an
/// owned-elements candidate, which is proved or cleared; a delegate's
/// Invoke made a direct call, no longer a call through a slot.
///
/// Debug only: a snapshot of every function before every pass costs what
/// the passes do. A loss is said on standard error, with the pass, the
/// function and the instruction, and the compile goes on.
/// </summary>
public sealed class MarkVerifier
{
    private readonly record struct Marks(bool Number, string? Family, string? Field, bool RegionSite, string? DispatchType, Opcode Op);
    private sealed record Noted(Instr Instr, Block Block, int Index, Marks Marks);

    private readonly List<Noted> _instrs = new();
    private readonly HashSet<Instr> _present = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Block Block, long Bytes, Instr[] Body)> _loops = new();
    private readonly List<VReg> _numbers = new();

    /// <summary>The passes that consume the link's region sites on purpose, and the one that consumes its loops.</summary>
    private static readonly HashSet<string> SiteConsumers = new(StringComparer.Ordinal) { "region-points-to", "escape" };
    private const string LoopConsumer = "region-points-to";

    private static bool Marked(Instr i)
        => i.Number || i.Family is not null || i.Field is not null || i.RegionSite || i.DispatchType is not null;

    /// <summary>Every mark on a function, as it is before a pass.</summary>
    public static MarkVerifier Snapshot(Function f)
    {
        MarkVerifier noted = new();
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                noted._present.Add(i);
                if (Marked(i)) noted._instrs.Add(new(i, b, k, new(i.Number, i.Family, i.Field, i.RegionSite, i.DispatchType, i.Op)));
            }
            if (b.RegionLoop) noted._loops.Add((b, b.RegionLoopBytes, b.Instrs.ToArray()));
        }
        foreach (VReg p in f.Params)
            if (p.Number) noted._numbers.Add(p);
        return noted;
    }

    /// <summary>Every module function's marks, as they are before a module pass.</summary>
    public static Dictionary<Function, MarkVerifier> Snapshot(Module m)
    {
        Dictionary<Function, MarkVerifier> all = new(ReferenceEqualityComparer.Instance);
        foreach (Function f in m.Functions) all[f] = Snapshot(f);
        return all;
    }

    // A call is a call, direct or through a slot; anything else is its own kind.
    private static int Kind(Opcode op) => op is Opcode.CallIndirect ? (int)Opcode.Call : (int)op;

    /// <summary>
    /// What `pass` lost of the marks noted before it, one line each; none
    /// when it lost nothing.
    /// </summary>
    public List<string> Check(Function f, string pass)
    {
        List<string> lost = new();
        if (_instrs.Count == 0 && _loops.Count == 0 && _numbers.Count == 0) return lost;
        HashSet<Instr> now = new(ReferenceEqualityComparer.Instance);
        Dictionary<VReg, List<Instr>> defining = new();
        HashSet<Block> blocks = new(f.Blocks, ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                now.Add(i);
                if (i.Dest is null) continue;
                if (!defining.TryGetValue(i.Dest, out List<Instr>? those)) defining[i.Dest] = those = new();
                those.Add(i);
            }
        foreach (Noted noted in _instrs)
        {
            Instr? after = null;
            if (now.Contains(noted.Instr)) after = noted.Instr;
            else if (noted.Instr.Dest is { } dest)
            {
                // The new instruction defining what it defined, if there is one alone.
                if (defining.TryGetValue(dest, out List<Instr>? those))
                {
                    List<Instr> made = those.Where(i => !_present.Contains(i) && Kind(i.Op) == Kind(noted.Marks.Op)).ToList();
                    if (made.Count == 1) after = made[0];
                }
            }
            else if (blocks.Contains(noted.Block) && noted.Index < noted.Block.Instrs.Count)
            {
                Instr there = noted.Block.Instrs[noted.Index];
                if (!_present.Contains(there) && Kind(there.Op) == Kind(noted.Marks.Op)) after = there;
            }
            if (after is null) continue;
            List<string> gone = Lost(noted.Marks, after, pass);
            if (gone.Count > 0)
                lost.Add($"verify-marks: {pass} lost {string.Join(", ", gone)} in {f.Name} ({after})");
        }
        if (pass != LoopConsumer)
            foreach ((Block header, long bytes, Instr[] body) in _loops)
            {
                if (blocks.Contains(header))
                {
                    if (!header.RegionLoop) lost.Add($"verify-marks: {pass} lost RegionLoop in {f.Name} (block {header.Label})");
                    else if (header.RegionLoopBytes != bytes) lost.Add($"verify-marks: {pass} lost RegionLoopBytes {bytes} in {f.Name} (block {header.Label})");
                }
                // The header gone and what it held kept elsewhere: merged
                // into another block, which took nothing of the mark.
                else if (body.Length > 0 && body.Any(now.Contains)
                    && !f.Blocks.Any(b => b.RegionLoop && b.Instrs.Any(i => body.Contains(i))))
                    lost.Add($"verify-marks: {pass} lost RegionLoop in {f.Name} (block {header.Label}, merged away)");
            }
        foreach (VReg p in _numbers)
            if (f.Params.Contains(p) && !p.Number)
                lost.Add($"verify-marks: {pass} lost Number in {f.Name} (parameter {p})");
        return lost;
    }

    private static List<string> Lost(Marks was, Instr now, string pass)
    {
        List<string> gone = new();
        // A site made the region's call is that call now, its marks spent
        // with it (RegionPointsTo.Retarget).
        if (was.RegionSite && SiteConsumers.Contains(pass)) return gone;
        if (was.Number && !now.Number) gone.Add("Number");
        if (was.Family is not null && now.Family != was.Family) gone.Add("Family " + was.Family);
        // An owned-elements candidate is proved or cleared (OwnedElements);
        // a delegate's Invoke made a direct call is a slot's call no more.
        if (was.Field is not null && now.Field is null && was.Field != Instr.OwnsCandidate
            && !(was.Field == Instr.DelegateInvoke && was.Op == Opcode.CallIndirect && now.Op == Opcode.Call))
            gone.Add("Field " + Shown(was.Field));
        if (was.RegionSite && !now.RegionSite) gone.Add("RegionSite");
        // A call through a slot made direct names its callee and needs no
        // declaring type; one still through a slot (or a catch's end) does.
        if (was.DispatchType is not null && now.DispatchType is null && now.Op == was.Op) gone.Add("DispatchType " + was.DispatchType);
        return gone;
    }

    private static string Shown(string field) => field.Length > 0 && field[0] == '\u0001' ? field[1..] : field;

    /// <summary>Check, said on standard error.</summary>
    public void Report(Function f, string pass)
    {
        foreach (string line in Check(f, pass)) Console.Error.WriteLine(line);
    }

    /// <summary>Check over a module, after a module pass, said on standard error.</summary>
    public static void Report(Dictionary<Function, MarkVerifier> before, Module m, string pass)
    {
        foreach (Function f in m.Functions)
            if (before.TryGetValue(f, out MarkVerifier? noted)) noted.Report(f, pass);
    }
}
