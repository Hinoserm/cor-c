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
/// comes before it is shared: each such block sets its number and jumps to
/// one copy of the frees, which goes back by a jump table to that block's own
/// trap. A trap is the cold path; the cost there is a jump and a table read.
///
/// What is shared is the longest run before the trap that reads nothing it
/// did not compute itself, and blocks share it when their runs are the same
/// instructions but for their own registers' names. Only a trap called with
/// no arguments, so nothing is carried through the shared copy; not in a
/// function with a landing pad, where a call's block says where its unwind
/// lands; not where phis are still in place.
/// </summary>
public static class SharedTrapFrees
{
    /// <summary>The fewest instructions a run is shared for: about two frees.</summary>
    private const int Fewest = 8;

    public static void Run(Function f)
    {
        if (f.Async is not null || f.Blocks.Count < 3) return;
        foreach (Block b in f.Blocks)
        {
            if (b.IsLandingPad) return;
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Phi) return;
        }
        Dictionary<string, List<(Block Block, int Start)>> groups = new(StringComparer.Ordinal);
        Dictionary<object, int> slots = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
        {
            int trap = b.Instrs.Count - 2;
            if (trap < Fewest || b.Instrs[^1].Op != Opcode.Unreachable) continue;
            Instr call = b.Instrs[trap];
            if (call.Op != Opcode.Call || call.Dest is not null || call.Operands.Count != 0 || call.Callee is null) continue;
            int start = Closed(b.Instrs, trap);
            if (trap - start < Fewest) continue;
            if (Key(b.Instrs, start, trap, slots) is not string key) continue;
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new());
            group.Add((b, start));
        }
        foreach (var group in groups.Values)
        {
            if (group.Count >= 2) Share(f, group);
        }
    }

    /// <summary>
    /// Where the longest run ending before `end` begins whose every register
    /// read is written earlier in the run: nothing from before it lives on
    /// into it. `end` when there is none.
    /// </summary>
    private static int Closed(List<Instr> instrs, int end)
    {
        HashSet<VReg> needed = new();
        int best = end;
        for (int k = end - 1; k >= 0; k--)
        {
            Instr i = instrs[k];
            if (i.IsTerminator || i.Op is Opcode.Phi or Opcode.LabelAddr or Opcode.StackPointer or Opcode.FramePointer || i.Targets.Count != 0) break;
            if (i.Dest is not null) needed.Remove(i.Dest);
            foreach (Operand o in i.Operands)
                if (o is RegOperand r) needed.Add(r.Reg);
            if (needed.Count == 0) best = k;
        }
        return best;
    }

    /// <summary>
    /// A run's instructions with its registers named by their first
    /// appearance in it and its frame slots by which slot they are; null for
    /// a run with an operand of any other kind, which is shared with none.
    /// </summary>
    private static string? Key(List<Instr> instrs, int start, int end, Dictionary<object, int> slots)
    {
        Dictionary<VReg, int> names = new();
        StringBuilder sb = new();
        string Name(VReg r)
        {
            if (!names.TryGetValue(r, out int n)) names.Add(r, n = names.Count);
            return "r" + n + ":" + r.Type;
        }
        for (int k = start; k < end; k++)
        {
            Instr i = instrs[k];
            sb.Append(i.Op).Append('|').Append(i.Size).Append('|').Append(i.Signed).Append('|').Append(i.Offset)
              .Append('|').Append(i.Callee).Append('|').Append(i.Number).Append('|').Append(i.Family).Append('|').Append(i.Field)
              .Append('|').Append(i.RegionSite).Append('|').Append(i.DispatchType).Append('|');
            foreach (Operand o in i.Operands)
            {
                switch (o)
                {
                    case RegOperand r: sb.Append(Name(r.Reg)); break;
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
            sb.Append("->").Append(i.Dest is null ? "" : Name(i.Dest)).Append(';');
        }
        return sb.ToString();
    }

    private static void Share(Function f, List<(Block Block, int Start)> group)
    {
        Block shared = f.NewBlock("trapfrees");
        VReg site = f.NewReg(IrType.I32, "trapsite");
        (Block first, int from) = group[0];
        int line = first.Instrs[^2].Line;
        shared.Instrs.AddRange(first.Instrs.GetRange(from, first.Instrs.Count - 2 - from));
        List<Block> traps = new();
        for (int k = 0; k < group.Count; k++)
        {
            (Block b, int start) = group[k];
            Block trap = f.NewBlock("trap");
            trap.Instrs.Add(b.Instrs[^2]);
            trap.Instrs.Add(b.Instrs[^1]);
            traps.Add(trap);
            int at = b.Instrs[^2].Line;
            b.Instrs.RemoveRange(start, b.Instrs.Count - start);
            b.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = site, Operands = { new ImmOperand(k, IrType.I32) }, Line = at });
            b.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { shared }, Line = at });
        }
        Instr choose = new() { Op = Opcode.Switch, Operands = { RegOperand.Of(site) }, Default = traps[0], Line = line };
        choose.WritableTargets.AddRange(traps);
        shared.Instrs.Add(choose);
    }
}
