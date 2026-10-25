#nullable enable
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE FREES BEFORE A TRAP, WRITTEN ONCE. A block that ends in a call that
/// never returns -- Runtime.Overflow, DivideByZero, IndexOutOfRange -- is
/// given every owned slot's free before it (Escape.ReleaseAtThrows), and a
/// function has one such block per check it makes: EmfPlayer.Run, with the
/// record dispatcher inlined into it, had a hundred and fifty of them and
/// forty-five frees in each, 6,766 calls and 200 KB of the GUI module's
/// code, twice over (Play is the same). The trap itself must stay where it
/// is -- its return address is the line a stack trace shows -- so only what
/// comes before it is shared: each such trap's frees jump to one copy of
/// them, its number set, and that copy goes back by a jump table to the
/// trap's own call. A trap is the cold path; the cost there is a jump and a
/// table read.
///
/// WHAT COMES BEFORE A TRAP is a region: the trap's block and, going back,
/// each block that leads nowhere else -- one whose only way on is into the
/// region, or a free's diamond (a test of the slot, the call, the join) --
/// up to a block reached from elsewhere, its head. Two traps' regions are
/// one when they are the same instructions block for block, reading the
/// same registers of the function: a register used nowhere but in one
/// region is the region's own, and is matched by where it first appears.
/// The trap's call is the trap's, not the region's: its arguments must not
/// be written in the region. Not in a function with a landing pad, where a
/// call's block says where its unwind lands, or where phis are still in
/// place.
/// </summary>
public static class SharedTrapFrees
{
    /// <summary>
    /// What sharing costs, in machine bytes as the x86 writes them: each trap
    /// its number and a jump, and an entry of the table (SiteBytes); the
    /// shared copy its switch (SwitchBytes). A region is weighed by its calls
    /// -- a free is the call and its argument, about eight bytes, and its
    /// test (BranchBytes) -- and four bytes for anything else.
    /// </summary>
    private const int SiteBytes = 14, SwitchBytes = 20, CallBytes = 8, BranchBytes = 6, OtherBytes = 4;

    private sealed class Region
    {
        public readonly List<Block> Blocks = new();
        public Block Head => Blocks[0];
        public Block Trap => Blocks[^1];
        public int Call;
    }

    public static void Run(Function f)
    {
        if (f.Async is not null || f.Blocks.Count < 3) return;
        foreach (Block b in f.Blocks)
        {
            if (b.IsLandingPad) return;
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Phi) return;
        }
        Dictionary<Block, List<Block>> preds = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks) preds[b] = new();
        foreach (Block b in f.Blocks)
            foreach (Block s in Successors(b))
                if (preds.TryGetValue(s, out var list) && !list.Contains(b)) list.Add(b);

        HashSet<Block> taken = new(ReferenceEqualityComparer.Instance);
        List<Region> regions = new();
        foreach (Block b in f.Blocks)
        {
            int call = TrapCall(b);
            if (call < 0 || taken.Contains(b)) continue;
            Region r = Grow(f, b, preds, taken);
            r.Call = call;
            foreach (Block x in r.Blocks) taken.Add(x);
            regions.Add(r);
        }
        if (regions.Count < 2) return;

        // A register is a region's own when no instruction outside that
        // region names it.
        Dictionary<VReg, Region?> owner = new(ReferenceEqualityComparer.Instance);
        Dictionary<Block, Region> regionOf = new(ReferenceEqualityComparer.Instance);
        foreach (Region r in regions)
            foreach (Block x in r.Blocks) regionOf[x] = r;
        foreach (Block b in f.Blocks)
        {
            regionOf.TryGetValue(b, out Region? r);
            foreach (Instr i in b.Instrs)
            {
                foreach (VReg v in Registers(i))
                {
                    if (!owner.TryGetValue(v, out Region? was)) owner[v] = r;
                    else if (!ReferenceEquals(was, r)) owner[v] = null;
                }
            }
        }

        Dictionary<VReg, int> names = new(ReferenceEqualityComparer.Instance);
        Dictionary<object, int> slots = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, List<Region>> groups = new(StringComparer.Ordinal);
        foreach (Region r in regions)
        {
            if (Key(r, owner, names, slots) is not string key) continue;
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new());
            group.Add(r);
        }
        foreach (var group in groups.Values)
        {
            if (group.Count < 2) continue;
            int bytes = 0;
            Region first = group[0];
            foreach (Block x in first.Blocks)
            {
                int end = ReferenceEquals(x, first.Trap) ? first.Call : x.Instrs.Count;
                for (int k = 0; k < end; k++)
                    bytes += x.Instrs[k].Op switch
                    {
                        Opcode.Call or Opcode.CallIndirect => CallBytes,
                        Opcode.Branch => BranchBytes,
                        Opcode.Jump => 0,
                        _ => OtherBytes,
                    };
            }
            if ((group.Count - 1) * bytes > group.Count * SiteBytes + SwitchBytes) Share(f, group, preds);
        }
    }

    private static IEnumerable<Block> Successors(Block b)
    {
        if (b.Instrs.Count == 0) yield break;
        Instr end = b.Instrs[^1];
        for (int k = 0; k < end.Targets.Count; k++) yield return end.Targets[k];
        if (end.Default is Block d) yield return d;
    }

    private static IEnumerable<VReg> Registers(Instr i)
    {
        if (i.Dest is not null) yield return i.Dest;
        foreach (Operand o in i.Operands)
            if (o is RegOperand r) yield return r.Reg;
    }

    /// <summary>
    /// The call a block ends in that never returns -- followed by nothing but
    /// the machine's trap (Opcode.Trap) and Unreachable -- or -1.
    /// </summary>
    private static int TrapCall(Block b)
    {
        int k = b.Instrs.Count - 1;
        if (k < 1 || b.Instrs[k].Op != Opcode.Unreachable) return -1;
        k--;
        if (b.Instrs[k].Op == Opcode.Trap) k--;
        return k >= 0 && b.Instrs[k].Op == Opcode.Call && b.Instrs[k].Dest is null && b.Instrs[k].Callee is not null ? k : -1;
    }

    /// <summary>
    /// A trap's region, its head first: back from the trap's block through
    /// each block whose one way on is the head, and each free's diamond (a
    /// test whose two ways are the head and a block that only goes there).
    /// Never the entry, which nothing jumps to.
    /// </summary>
    private static Region Grow(Function f, Block trap, Dictionary<Block, List<Block>> preds, HashSet<Block> taken)
    {
        Region r = new();
        r.Blocks.Add(trap);
        Block head = trap;
        while (true)
        {
            List<Block> into = preds[head];
            if (into.Count == 1)
            {
                Block c = into[0];
                if (Usable(f, c, taken, r) && Successors(c).All(s => ReferenceEquals(s, head)))
                {
                    r.Blocks.Insert(0, c);
                    head = c;
                    continue;
                }
                break;
            }
            if (into.Count == 2)
            {
                Block? test = null, free = null;
                foreach (Block x in into)
                {
                    Block other = ReferenceEquals(x, into[0]) ? into[1] : into[0];
                    if (preds[x].Count == 1 && ReferenceEquals(preds[x][0], other) && Successors(x).All(s => ReferenceEquals(s, head))
                        && x.Instrs.Count > 0 && x.Instrs[^1].Op == Opcode.Jump)
                    {
                        free = x;
                        test = other;
                    }
                }
                if (test is not null && free is not null && Usable(f, test, taken, r) && Usable(f, free, taken, r)
                    && test.Instrs.Count > 0 && test.Instrs[^1] is { Op: Opcode.Branch } branch && branch.Targets.Count == 2
                    && (ReferenceEquals(branch.Targets[0], free) && ReferenceEquals(branch.Targets[1], head)
                        || ReferenceEquals(branch.Targets[1], free) && ReferenceEquals(branch.Targets[0], head)))
                {
                    r.Blocks.Insert(0, free);
                    r.Blocks.Insert(0, test);
                    head = test;
                    continue;
                }
            }
            break;
        }
        return r;
    }

    private static bool Usable(Function f, Block b, HashSet<Block> taken, Region r) =>
        !ReferenceEquals(b, f.Entry) && !taken.Contains(b) && !r.Blocks.Contains(b) && !b.IsLandingPad;

    /// <summary>
    /// A region as text, but its trap's call: each block's instructions, a
    /// target by its place in the region, a register of the region's own by
    /// where it first appears and any other by which it is, a frame slot by
    /// which it is. Null for an operand of any other kind, or a trap whose
    /// arguments the region writes; such a region is shared with none.
    /// </summary>
    private static string? Key(Region r, Dictionary<VReg, Region?> owner, Dictionary<VReg, int> names, Dictionary<object, int> slots)
    {
        Dictionary<VReg, int> own = new(ReferenceEqualityComparer.Instance);
        HashSet<VReg> written = new(ReferenceEqualityComparer.Instance);
        StringBuilder sb = new();
        string Name(VReg v)
        {
            if (owner.TryGetValue(v, out Region? o) && ReferenceEquals(o, r))
            {
                if (!own.TryGetValue(v, out int n)) own.Add(v, n = own.Count);
                return "o" + n + ":" + v.Type;
            }
            if (!names.TryGetValue(v, out int m)) names.Add(v, m = names.Count);
            return "g" + m;
        }
        for (int b = 0; b < r.Blocks.Count; b++)
        {
            Block x = r.Blocks[b];
            int end = b == r.Blocks.Count - 1 ? r.Call : x.Instrs.Count;
            sb.Append('{');
            for (int k = 0; k < end; k++)
            {
                Instr i = x.Instrs[k];
                sb.Append(i.Op).Append('|').Append(i.Size).Append('|').Append(i.Signed).Append('|').Append(i.Offset)
                  .Append('|').Append(i.Callee).Append('|').Append(i.Number).Append('|').Append(i.Family).Append('|').Append(i.Field)
                  .Append('|').Append(i.RegionSite).Append('|').Append(i.DispatchType).Append('|');
                foreach (Operand o in i.Operands)
                {
                    switch (o)
                    {
                        case RegOperand g: sb.Append(Name(g.Reg)); break;
                        case ImmOperand m: sb.Append('i').Append(m.Value).Append(':').Append(m.Type); break;
                        case SymOperand y: sb.Append('s').Append(y.Name).Append('+').Append(y.Offset).Append(':').Append(y.Type); break;
                        case SlotOperand l:
                            if (!slots.TryGetValue(l.Slot, out int n)) slots.Add(l.Slot, n = slots.Count);
                            sb.Append('f').Append(n).Append(':').Append(l.Type);
                            break;
                        default: return null;
                    }
                    sb.Append(',');
                }
                for (int t = 0; t < i.Targets.Count; t++)
                {
                    int at = r.Blocks.IndexOf(i.Targets[t]);
                    if (at < 0) return null;
                    sb.Append("->").Append(at);
                }
                if (i.Default is not null) return null;
                if (i.Dest is not null)
                {
                    written.Add(i.Dest);
                    sb.Append("=>").Append(Name(i.Dest));
                }
                sb.Append(';');
            }
            sb.Append('}');
        }
        foreach (Operand o in r.Trap.Instrs[r.Call].Operands)
        {
            if (o is RegOperand g && written.Contains(g.Reg)) return null;
            if (o is not (RegOperand or ImmOperand or SymOperand)) return null;
        }
        return sb.ToString();
    }

    private static void Share(Function f, List<Region> group, Dictionary<Block, List<Block>> preds)
    {
        Region kept = group[0];
        VReg site = f.NewReg(IrType.I32, "trapsite");
        int line = kept.Trap.Instrs[kept.Call].Line;

        // Each trap's own call, in a block of its own.
        List<Block> traps = new();
        foreach (Region r in group)
        {
            Block t = f.NewBlock("trap");
            t.Instrs.AddRange(r.Trap.Instrs.GetRange(r.Call, r.Trap.Instrs.Count - r.Call));
            r.Trap.Instrs.RemoveRange(r.Call, r.Trap.Instrs.Count - r.Call);
            traps.Add(t);
        }

        // The kept region entered with its number: what jumped to its head
        // jumps to a block that sets it first.
        Block head = kept.Head;
        Block enter = f.NewBlock("trapsite");
        enter.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = site, Operands = { new ImmOperand(0, IrType.I32) }, Line = line });
        enter.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { head }, Line = line });
        foreach (Block p in preds[head])
        {
            if (kept.Blocks.Contains(p)) continue;
            Instr end = p.Instrs[^1];
            for (int k = 0; k < end.Targets.Count; k++)
                if (ReferenceEquals(end.Targets[k], head)) end.SetTarget(k, enter);
            if (ReferenceEquals(end.Default, head)) end.Default = enter;
        }
        Instr choose = new() { Op = Opcode.Switch, Operands = { RegOperand.Of(site) }, Default = traps[0], Line = line };
        choose.WritableTargets.AddRange(traps);
        kept.Trap.Instrs.Add(choose);

        // The others: their heads set their numbers and jump to it, and the
        // rest of each goes.
        for (int g = 1; g < group.Count; g++)
        {
            Region r = group[g];
            int at = traps[g].Instrs[0].Line;
            for (int k = 1; k < r.Blocks.Count; k++) f.Blocks.Remove(r.Blocks[k]);
            r.Head.Instrs.Clear();
            r.Head.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = site, Operands = { new ImmOperand(g, IrType.I32) }, Line = at });
            r.Head.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { head }, Line = at });
        }
    }
}
