#nullable enable
using Role = Corsac.Lang.X86.Roles.Role;

namespace Corsac.Lang.X86;

/// <summary>
/// A SMALL CHOICE WITHOUT A BRANCH where the processor has conditional moves
/// (X86Feature.Cmov). A ternary, Math.Max, Min and Clamp, and an `if` that only
/// assigns, are selected as a diamond of blocks that each move a value and
/// jump on:
///
///     cmp a, 7 ; jg T ; jmp F         cmp a, 7
///     T: mov v, a ; jmp J        =>   mov v, 7
///     F: mov v, 7 ; jmp J             cmovg v, a
///                                     jmp J
///
/// On a P6, an Athlon or a Pentium 4 a conditional jump the data decides is
/// mispredicted as often as the data is random, ten to twenty cycles each
/// time, against two micro-ops for the cmov. The triangle, an `if` with no
/// `else` whose one arm moves and whose other is the join, keeps the old
/// value where the condition fails, which is what a cmov does.
///
/// Before allocation, on virtual registers, so the allocator gives the moved
/// values their registers in one straight block. Recognised:
///
/// - Block A ends `jcc X ; jmp Y` with no other conditional jump in it, so a
///   floating compare's parity pair stays as it is, and the condition is not
///   parity.
/// - Each arm that moves is one or two `mov v, s` of a whole word into
///   distinct virtual registers, then `jmp J`, and A is the only way into it:
///   no other jump, no fall-through, no address taken, not the entry, not a
///   landing pad.
/// - A diamond: both arms move the same registers and go to the same J. A
///   triangle: one arm moves and goes to J; the other is J, or a block that
///   only jumps to J. In a triangle every moved register must already hold a
///   value on every way to A's end (Defined), since the cmov now reads it
///   there.
/// - A source is a virtual register, a constant, or memory that cannot fault:
///   the frame's or a static's, without an index. A cmov always performs its
///   load, so a load through a register the branch had kept from a null
///   reference would raise the NullReferenceException the program avoided.
/// - No source names a register an arm writes: the arms' moves are each a
///   parallel copy, and they are done here one after another.
///
/// An arm that computes, Abs's `-x`, keeps its branch. The moves become, in a
/// diamond, `mov v, a` with one arm's value, and `cmovcc v, b` with the
/// other's, a constant b first put in a fresh register, since a cmov has no
/// immediate form -- before A's compare where it ends in one, after it
/// otherwise; MOV leaves the flags alone, so the condition is still the
/// compare's. The arms that moved are removed.
/// </summary>
internal static class IfConvert
{
    /// <summary>The most moves an arm may make and still be folded into A.</summary>
    private const int MostMoves = 2;

    /// <summary>The most registers the triangles of one function have their definitions followed for.</summary>
    private const int MostFollowed = 64;

    private readonly record struct Arm(MBlock Block, List<MInstr> Moves, MBlock Join);

    public static void Run(MFunction m)
    {
        if (m.Blocks.Count < 3) return;
        Dictionary<MBlock, int> refs = References(m);

        // Every candidate first, against the unchanged blocks: a triangle's
        // registers are then followed through the function once.
        List<(MBlock A, Arm Taken, Arm? NotTaken, bool Inverted, MBlock? Forwarder)> found = new();
        Dictionary<int, int> followed = new();
        foreach (MBlock a in m.Blocks)
        {
            List<MInstr> instrs = a.Instrs;
            int n = instrs.Count;
            if (n < 2 || instrs[n - 1].Op != MOp.Jmp || instrs[n - 2] is not { Op: MOp.Jcc } jcc
                || jcc.Cond is Cond.P or Cond.Np || jcc.Operands[0] is not MLabel { Target: var x }
                || instrs[n - 1].Operands[0] is not MLabel { Target: var y } || ReferenceEquals(x, y))
                continue;
            bool single = true;
            for (int k = 0; k < n - 2 && single; k++) single = instrs[k].Op is not (MOp.Jcc or MOp.Jmp or MOp.JmpTable or MOp.JmpInd);
            if (!single) continue;
            Arm? onX = MovingArm(x, refs), onY = MovingArm(y, refs);
            if (onX is { } tx && onY is { } ty)
            {
                if (ReferenceEquals(tx.Join, ty.Join) && SameRegisters(tx.Moves, ty.Moves) && !Overlapping(tx.Moves, ty.Moves))
                    found.Add((a, tx, ty, false, null));
                continue;
            }
            // A triangle: the arm that moves, and the other way straight to its join.
            Arm arm;
            MBlock other;
            bool inverted;
            if (onX is { } only) { arm = only; other = y; inverted = false; }
            else if (onY is { } onlyY) { arm = onlyY; other = x; inverted = true; }
            else continue;
            MBlock? forwarder = null;
            if (!ReferenceEquals(other, arm.Join))
            {
                if (other.Instrs.Count != 1 || other.Instrs[0] is not { Op: MOp.Jmp } jump
                    || jump.Operands[0] is not MLabel { Target: var on } || !ReferenceEquals(on, arm.Join))
                    continue;
                forwarder = other;
            }
            if (Overlapping(arm.Moves, arm.Moves)) continue;
            found.Add((a, arm, null, inverted, forwarder));
            foreach (MInstr move in arm.Moves)
            {
                int v = ((MReg)move.Operands[0]).Id;
                if (!followed.ContainsKey(v) && followed.Count < MostFollowed) followed[v] = followed.Count;
            }
        }
        if (found.Count == 0) return;
        Dictionary<MBlock, ulong>? defined = followed.Count == 0 ? null : Defined(m, followed);

        HashSet<MBlock> removed = new(ReferenceEqualityComparer.Instance);
        foreach ((MBlock a, Arm taken, Arm? notTaken, bool inverted, MBlock? forwarder) in found)
        {
            List<MInstr> instrs = a.Instrs;
            Cond c = instrs[^2].Cond;
            if (inverted) c = c.Negate();
            if (notTaken is null)
            {
                // The cmov reads what it may keep: it must be there to read.
                bool all = true;
                foreach (MInstr move in taken.Moves)
                {
                    int v = ((MReg)move.Operands[0]).Id;
                    all &= followed.TryGetValue(v, out int bit) && (defined![a] & (1UL << bit)) != 0;
                }
                if (!all) continue;
            }
            int line = instrs[^2].Line;
            instrs.RemoveRange(instrs.Count - 2, 2);
            // A CONSTANT A CMOV READS goes in its register before the compare
            // where A ends in one: there the flags are not yet live, and the
            // peephole may make a zero the two-byte `xor`.
            int compare = instrs.Count - 1;
            bool before = compare >= 0 && instrs[compare].Op is MOp.Cmp or MOp.Test;
            List<MInstr> cmovs = new();
            foreach (MInstr move in taken.Moves)
            {
                MReg v = (MReg)move.Operands[0];
                MOperand b = move.Operands[1];
                Cond cc = c;
                if (notTaken is { } other)
                {
                    MOperand a2 = other.Moves.First(o => ((MReg)o.Operands[0]).Id == v.Id).Operands[1];
                    // A CONSTANT ON ONE SIDE goes in by the plain move, the
                    // condition turned round: a cmov's source has no
                    // immediate form, and would need a register of its own.
                    if (b is MImm && a2 is not MImm) (b, a2, cc) = (a2, b, c.Negate());
                    instrs.Add(new MInstr(MOp.Mov, v, a2) { Line = move.Line });
                    // The same value either way is the move alone.
                    if (Same(a2, b)) continue;
                }
                if (b is MImm imm)
                {
                    MReg t = m.NewReg();
                    MInstr load = new(MOp.Mov, t, imm) { Line = move.Line };
                    if (before) instrs.Insert(compare++, load); else instrs.Add(load);
                    b = t;
                }
                cmovs.Add(new MInstr(MOp.Cmov, v, b) { Cond = cc, Line = move.Line });
            }
            instrs.AddRange(cmovs);
            instrs.Add(new MInstr(MOp.Jmp, new MLabel(taken.Join)) { Line = line });
            removed.Add(taken.Block);
            if (notTaken is { } gone) removed.Add(gone.Block);
            // A block that only jumped on goes once nothing jumps to it.
            if (forwarder is not null && --refs[forwarder] == 0) removed.Add(forwarder);
        }
        if (removed.Count > 0) m.Blocks.RemoveAll(removed.Contains);
    }

    /// <summary>
    /// How many ways there are into each block: a jump to it, a table's
    /// entry, an address of it taken, the block before it falling into it;
    /// one more for the entry and for a landing pad, which are entered from
    /// outside the function's own jumps.
    /// </summary>
    private static Dictionary<MBlock, int> References(MFunction m)
    {
        Dictionary<MBlock, int> refs = new(ReferenceEqualityComparer.Instance);
        void Add(MBlock b) => refs[b] = refs.GetValueOrDefault(b) + 1;
        for (int k = 0; k < m.Blocks.Count; k++)
        {
            MBlock b = m.Blocks[k];
            if (k == 0 || b.Source?.IsLandingPad == true) Add(b);
            foreach (MInstr i in b.Instrs)
            {
                if (i.Table is { } table) foreach (MBlock t in table) Add(t);
                foreach (MOperand o in i.Operands)
                {
                    if (o is MLabel l) Add(l.Target);
                    else if (o is MImm { Label: { } li }) Add(li);
                    else if (o is MMem { Label: { } lm }) Add(lm);
                }
            }
            if (!b.EndsUnconditionally && k + 1 < m.Blocks.Count) Add(m.Blocks[k + 1]);
        }
        return refs;
    }

    /// <summary>The arm when this block is one: moves A alone reaches, then a jump on.</summary>
    private static Arm? MovingArm(MBlock b, Dictionary<MBlock, int> refs)
    {
        List<MInstr> instrs = b.Instrs;
        if (refs.GetValueOrDefault(b) != 1 || instrs.Count < 2 || instrs.Count > MostMoves + 1
            || instrs[^1] is not { Op: MOp.Jmp } jump || jump.Operands[0] is not MLabel { Target: var join }
            || ReferenceEquals(join, b))
            return null;
        List<MInstr> moves = new(instrs.Count - 1);
        for (int k = 0; k < instrs.Count - 1; k++)
        {
            MInstr i = instrs[k];
            if (i.Op != MOp.Mov || i.Width != 4 || i.Lock || i.Operands.Count != 2
                || i.Operands[0] is not MReg { IsPhys: false } v || !Source(i.Operands[1]))
                return null;
            foreach (MInstr earlier in moves)
                if (((MReg)earlier.Operands[0]).Id == v.Id) return null;
            moves.Add(i);
        }
        return new Arm(b, moves, join);
    }

    /// <summary>
    /// What a cmov may read where a branch read it only on its own way: a
    /// virtual register, a constant (by a register), or memory reading which
    /// cannot fault -- the frame's slots and a static's own storage, with no
    /// index that might take the address anywhere else.
    /// </summary>
    private static bool Source(MOperand o) => o switch
    {
        MReg r => !r.IsPhys,
        MImm => true,
        MMem mem => mem.Index is null
            && (mem.Base is { IsPhys: true, Phys: Gpr.Ebp or Gpr.Esp }
                || mem.Base is null && (mem.Symbol is not null || mem.Label is not null)),
        _ => false,
    };

    private static bool SameRegisters(List<MInstr> a, List<MInstr> b)
    {
        if (a.Count != b.Count) return false;
        foreach (MInstr i in a)
            if (!b.Exists(j => ((MReg)j.Operands[0]).Id == ((MReg)i.Operands[0]).Id)) return false;
        return true;
    }

    /// <summary>Whether a source among these moves names a register either list writes.</summary>
    private static bool Overlapping(List<MInstr> a, List<MInstr> b)
    {
        foreach (MInstr i in a)
            foreach (MInstr j in b)
            {
                int v = ((MReg)i.Operands[0]).Id;
                if (Names(j.Operands[1], v)) return true;
                int w = ((MReg)j.Operands[0]).Id;
                if (Names(i.Operands[1], w)) return true;
            }
        return false;
    }

    private static bool Names(MOperand o, int id) => o switch
    {
        MReg r => r.Id == id,
        MMem mem => mem.Base?.Id == id || mem.Index?.Id == id,
        _ => false,
    };

    private static bool Same(MOperand a, MOperand b) => a switch
    {
        MReg ra => b is MReg rb && ra.Id == rb.Id,
        MImm { IsPlain: true } ia => b is MImm { IsPlain: true } ib && ia.Value == ib.Value,
        _ => false,
    };

    /// <summary>
    /// For each block, the followed registers written on every way from the
    /// entry to its end: a forward pass that keeps what all of a block's
    /// predecessors agree on. The entry, a landing pad and a block whose
    /// address is taken start knowing nothing, since they are entered from
    /// outside the jumps followed here; a block nothing jumps or falls into
    /// is never reached and agrees with anything.
    /// </summary>
    private static Dictionary<MBlock, ulong> Defined(MFunction m, Dictionary<int, int> followed)
    {
        int nb = m.Blocks.Count;
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++) index[m.Blocks[b]] = b;
        ulong[] written = new ulong[nb];
        bool[] outside = new bool[nb];
        List<int>[] preds = new List<int>[nb];
        for (int b = 0; b < nb; b++) preds[b] = new List<int>();
        List<MBlock> successors = new();
        for (int b = 0; b < nb; b++)
        {
            MBlock block = m.Blocks[b];
            outside[b] = b == 0 || block.Source?.IsLandingPad == true;
            foreach (MInstr i in block.Instrs)
            {
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is MReg r && followed.TryGetValue(r.Id, out int bit) && (Roles.Of(i.Op, k) & Role.Def) != 0)
                        written[b] |= 1UL << bit;
                    if (i.Operands[k] is MImm { Label: { } li } && index.TryGetValue(li, out int ti)) outside[ti] = true;
                    if (i.Operands[k] is MMem { Label: { } lm } && index.TryGetValue(lm, out int tm)) outside[tm] = true;
                }
            }
            m.SuccessorsInto(b, successors);
            foreach (MBlock s in successors)
                if (index.TryGetValue(s, out int si)) preds[si].Add(b);
        }
        ulong[] atEnd = new ulong[nb];
        Array.Fill(atEnd, ulong.MaxValue);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int b = 0; b < nb; b++)
            {
                ulong atStart = outside[b] ? 0 : ulong.MaxValue;
                foreach (int p in preds[b]) atStart &= atEnd[p];
                ulong end = atStart | written[b];
                if (end != atEnd[b])
                {
                    atEnd[b] = end;
                    changed = true;
                }
            }
        }
        Dictionary<MBlock, ulong> defined = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++) defined[m.Blocks[b]] = atEnd[b];
        return defined;
    }
}
