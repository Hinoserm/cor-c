#nullable enable
using Role = Corsac.Lang.X64.Roles.Role;

namespace Corsac.Lang.X64;

/// <summary>
/// The i386 backend's if-conversion (X86.IfConvert), for long mode, where
/// every processor has the conditional moves. Its constant zero is selected
/// as `xor v, v`, which an arm moves as the `mov v, 0` it means; a move or a
/// cmov keeps its width, four bytes or eight; a source in memory is the
/// frame's or RIP-relative, never another segment's.
///
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
/// AN ARM THAT COMPUTES -- Abs's `-x`, `a > b ? b + c : a - c` -- computes
/// before A's compare, in registers only it uses, what it would have moved:
/// up to MostComputed instructions of plain arithmetic on registers and
/// constants, none that can fault or that reads memory, ahead of the moves.
/// Only where A ends in a compare or a test: the computation then writes the
/// flags before the compare writes them again, and reads what the arm would
/// have read, the compare writing no register.
///
/// The moves become, in a diamond, `mov v, a` with one arm's value, and
/// `cmovcc v, b` with the other's, a constant b first put in a fresh
/// register, since a cmov has no immediate form -- before A's compare where
/// it ends in one, after it otherwise; MOV leaves the flags alone, so the
/// condition is still the compare's. The arms that moved are removed.
///
/// NOT A SELECT AN INNER LOOP CARRIES: `if (x > m) m = x;` in a loop that
/// holds no other, where the m it chooses is read on the next lap before
/// anything writes it, so each lap's compare and cmov wait for the last
/// one's -- a chain a predicted branch does not have. A select whose value
/// is used up within the lap, `n += x > t ? 1 : 0`'s, keeps its cmov.
/// </summary>
internal static class IfConvert
{
    /// <summary>The most moves an arm may make and still be folded into A.</summary>
    private const int MostMoves = 2;

    /// <summary>The most registers the triangles of one function have their definitions followed for.</summary>
    private const int MostFollowed = 64;

    /// <summary>The most instructions an arm may compute with ahead of its moves.</summary>
    private const int MostComputed = 3;

    /// <summary>An arm: what it computes (into registers only it uses), the moves it ends with, and where it goes.</summary>
    private readonly record struct Arm(MBlock Block, List<MInstr> Computed, List<MInstr> Moves, MBlock Join);

    public static void Run(MFunction m)
    {
        if (m.Blocks.Count < 3) return;
        Dictionary<MBlock, int> refs = References(m);
        int[] occurrences = Occurrences(m);
        Loops? loops = null;
        bool[]? narrow = null;

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
            Arm? onX = MovingArm(x, refs, occurrences), onY = MovingArm(y, refs, occurrences);
            // Computing ahead of A's compare: only where A ends in one.
            bool compares = n >= 3 && instrs[n - 3].Op is MOp.Cmp or MOp.Test;
            if (!compares && onX is { Computed.Count: > 0 }) onX = null;
            if (!compares && onY is { Computed.Count: > 0 }) onY = null;
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
            loops ??= new Loops(m);
            if (loops.Carries(a, taken, notTaken)) continue;
            if (!Widths(taken, notTaken, narrow ??= Narrow(m))) continue;
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
            // And what the arms compute, there too: in their own registers,
            // the flags they write written again by the compare.
            foreach (MInstr computed in taken.Computed.Concat(notTaken?.Computed ?? Enumerable.Empty<MInstr>()))
                instrs.Insert(compare++, computed);
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
                    // Both arms' moves are of one width (Widths).
                    // The plain move after the compare, MOV leaving the flags
                    // alone: before it, v would be live beside everything
                    // the compare reads, and in a loop of six registers that
                    // spilled the array (a `xor` for a zero is not worth it).
                    instrs.Add(new MInstr(MOp.Mov, v, a2) { Width = move.Width, Line = move.Line });
                    // The same value either way is the move alone.
                    if (Same(a2, b)) continue;
                }
                if (b is MImm imm)
                {
                    MReg t = m.NewReg();
                    MInstr load = new(MOp.Mov, t, imm) { Width = move.Width, Line = move.Line };
                    if (before) instrs.Insert(compare++, load); else instrs.Add(load);
                    b = t;
                }
                cmovs.Add(new MInstr(MOp.Cmovcc, v, b) { Cond = cc, Width = move.Width, Line = move.Line });
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
    /// WHETHER A CMOV OF THE MOVES' WIDTH DOES WHAT THEY DID. A four-byte move
    /// zero-extends what it writes, and so does a four-byte cmov -- also when
    /// its condition fails, to what it keeps: in a diamond both arms' moves
    /// must be of one width, and in a triangle a four-byte one only of a
    /// register every write of which is four bytes (`narrow`), whose upper
    /// half is nought already. An eight-byte cmov keeps all of what it keeps.
    /// </summary>
    private static bool Widths(Arm taken, Arm? notTaken, bool[] narrow)
    {
        foreach (MInstr move in taken.Moves)
        {
            int v = ((MReg)move.Operands[0]).Id;
            if (notTaken is { } other)
            {
                if (other.Moves.First(o => ((MReg)o.Operands[0]).Id == v).Width != move.Width) return false;
            }
            else if (move.Width == 4 && !narrow[v]) return false;
        }
        return true;
    }

    /// <summary>Each virtual register every write of which is four bytes wide.</summary>
    private static bool[] Narrow(MFunction m)
    {
        bool[] narrow = new bool[m.NextVReg];
        Array.Fill(narrow, true);
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is MReg { IsPhys: false } r && (Roles.Of(i, k) & Role.Def) != 0
                        && (i.Width != 4 || i.Op is MOp.Movsx or MOp.Movzx or MOp.Lea or MOp.Pop or MOp.MovGx or MOp.CvtFToInt))
                        narrow[r.Id] = false;
        return narrow;
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
                    else if (o is MMem { Label: { } lm }) Add(lm);
                }
            }
            if (!b.EndsUnconditionally && k + 1 < m.Blocks.Count) Add(m.Blocks[k + 1]);
        }
        return refs;
    }

    /// <summary>
    /// The arm when this block is one: what A alone reaches -- a computation
    /// in registers nothing else names, then moves -- and a jump on.
    /// </summary>
    private static Arm? MovingArm(MBlock b, Dictionary<MBlock, int> refs, int[] occurrences)
    {
        List<MInstr> instrs = b.Instrs;
        if (refs.GetValueOrDefault(b) != 1 || instrs.Count < 2 || instrs.Count > MostComputed + MostMoves + 1
            || instrs[^1] is not { Op: MOp.Jmp } jump || jump.Operands[0] is not MLabel { Target: var join }
            || ReferenceEquals(join, b))
            return null;
        // The moves: the last instructions, each `mov v, s` of a whole word.
        int first = instrs.Count - 1;
        while (first > 0 && instrs.Count - 1 - first < MostMoves && IsMove(instrs[first - 1])) first--;
        if (first == instrs.Count - 1) return null;
        List<MInstr> moves = new(instrs.Count - 1 - first);
        for (int k = first; k < instrs.Count - 1; k++)
        {
            MInstr i = instrs[k];
            MReg v = (MReg)i.Operands[0];
            // `xor v, v` is the selector's zero: a move of it.
            if (i.Op == MOp.Xor) i = new MInstr(MOp.Mov, v, new MImm(0)) { Width = 4, Line = i.Line };
            foreach (MInstr earlier in moves)
                if (((MReg)earlier.Operands[0]).Id == v.Id) return null;
            moves.Add(i);
        }
        // What comes before them computes, in registers only this block names.
        List<MInstr> computed = instrs.GetRange(0, first);
        if (computed.Count > MostComputed) return null;
        Dictionary<int, int> here = new();
        foreach (MInstr i in instrs)
            foreach (MOperand o in i.Operands)
                if (o is MReg { IsPhys: false } r) here[r.Id] = here.GetValueOrDefault(r.Id) + 1;
        foreach (MInstr i in computed)
        {
            if (!Computes(i) || i.Operands[0] is not MReg { IsPhys: false } t || here[t.Id] != occurrences[t.Id]) return null;
            // A move's value read from a register the computation writes is
            // the computed one: the move then reads that register, which
            // only this block names, so the cmov reads it too.
        }
        return new Arm(b, computed, moves, join);
    }

    private static bool IsMove(MInstr i)
        => i.Op == MOp.Mov && i.Width is 4 or 8 && !i.Lock && i.Operands.Count == 2
           && i.Operands[0] is MReg { IsPhys: false, IsFloat: false } && Source(i.Operands[1])
           || i.Op == MOp.Xor && i.Width == 4 && i.Operands is [MReg { IsPhys: false, IsFloat: false } x, MReg y] && x.Id == y.Id;

    /// <summary>
    /// Arithmetic that may run whichever way A goes: on registers and
    /// constants, of a whole word, nothing that reads memory, faults or
    /// writes anything but its destination and the flags.
    /// </summary>
    private static bool Computes(MInstr i)
    {
        if (i.Lock || i.Width is not (4 or 8) || i.Operands.Count == 0 || i.Operands[0] is not MReg { IsPhys: false, IsFloat: false }) return false;
        foreach (MOperand o in i.Operands)
            if (o is MMem || o is MReg { IsPhys: true } || o is MReg { IsFloat: true } || o is MImm { FitsInt32: false } && i.Op != MOp.Mov
                || o is MImm { IsPlain: false } || o is MLabel) return false;
        return i.Op switch
        {
            MOp.Mov or MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Imul => i.Operands.Count == 2,
            MOp.Imul3 => i.Operands.Count == 3,
            MOp.Neg or MOp.Not => i.Operands.Count == 1,
            MOp.Shl or MOp.Shr or MOp.Sar => i.Operands.Count == 2 && i.Operands[1] is MImm,
            _ => false,
        };
    }

    /// <summary>How many times each virtual register is named in the function.</summary>
    private static int[] Occurrences(MFunction m)
    {
        int[] counts = new int[m.NextVReg];
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach (MOperand o in i.Operands)
                {
                    if (o is MReg { IsPhys: false } r) counts[r.Id]++;
                    else if (o is MMem mem)
                    {
                        if (mem.Base is { IsPhys: false } mb) counts[mb.Id]++;
                        if (mem.Index is { IsPhys: false } mx) counts[mx.Id]++;
                    }
                }
        return counts;
    }

    /// <summary>
    /// The function's innermost loops, for the select one carries: natural
    /// loops by their back edges (BlockDominators), each block's innermost
    /// loop that holds no other.
    /// </summary>
    private sealed class Loops
    {
        private readonly MFunction _m;
        private readonly Dictionary<MBlock, int> _index = new(ReferenceEqualityComparer.Instance);
        // For each block, the header and body of the innermost loop it is in,
        // where that loop holds no other; null for one in no such loop.
        private readonly (int Header, HashSet<int> Body)?[] _inner;
        private readonly List<int>[] _succ;

        public Loops(MFunction m)
        {
            _m = m;
            int n = m.Blocks.Count;
            for (int b = 0; b < n; b++) _index[m.Blocks[b]] = b;
            List<int>[] succ = _succ = new List<int>[n];
            List<int>[] pred = new List<int>[n];
            for (int b = 0; b < n; b++) { succ[b] = new(); pred[b] = new(); }
            for (int b = 0; b < n; b++)
                foreach (MBlock s in m.Successors(b))
                    if (_index.TryGetValue(s, out int si)) { succ[b].Add(si); pred[si].Add(b); }
            int[] idom = BlockDominators.Of(n, b => succ[b]);
            Dictionary<int, HashSet<int>> bodies = new();
            for (int b = 0; b < n; b++)
                foreach (int h in succ[b])
                    if (h == b || BlockDominators.Strictly(idom, h, b))
                    {
                        HashSet<int> body = bodies.TryGetValue(h, out HashSet<int>? had) ? had : bodies[h] = new() { h };
                        Stack<int> work = new();
                        if (body.Add(b)) work.Push(b);
                        while (work.Count > 0)
                            foreach (int p in pred[work.Pop()])
                                if (body.Add(p)) work.Push(p);
                    }
            _inner = new (int, HashSet<int>)?[n];
            foreach (var (h, body) in bodies)
            {
                if (bodies.Keys.Any(other => other != h && body.Contains(other))) continue;
                foreach (int b in body) _inner[b] = (h, body);
            }
        }

        /// <summary>
        /// Whether the select at the end of `a` is carried by an innermost
        /// loop: a register it chooses is read in the loop, from its header
        /// on, before anything writes it -- its value goes round the loop to
        /// the next lap.
        /// </summary>
        public bool Carries(MBlock a, Arm taken, Arm? notTaken)
        {
            if (!_index.TryGetValue(a, out int at) || _inner[at] is not { } loop) return false;
            foreach (MInstr move in taken.Moves)
                if (LiveAt(loop.Header, loop.Body, ((MReg)move.Operands[0]).Id)) return true;
            return false;
        }

        /// <summary>Whether v is read on some way through the loop from its header before it is written.</summary>
        private bool LiveAt(int header, HashSet<int> body, int v)
        {
            HashSet<int> seen = new() { header };
            Stack<int> work = new();
            work.Push(header);
            while (work.Count > 0)
            {
                int b = work.Pop();
                bool written = false;
                foreach (MInstr i in _m.Blocks[b].Instrs)
                {
                    // `xor v, v` makes zero whatever v held: a write only.
                    bool zeroing = i.Op == MOp.Xor && i.Operands.Count == 2 && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
                    for (int k = 0; k < i.Operands.Count && !zeroing; k++)
                    {
                        MOperand o = i.Operands[k];
                        if (o is MMem mem && (mem.Base?.Id == v || mem.Index?.Id == v)) return true;
                        if (o is MReg r && r.Id == v && (Roles.Of(i, k) & Role.Use) != 0) return true;
                    }
                    for (int k = 0; k < i.Operands.Count; k++)
                        if (i.Operands[k] is MReg r && r.Id == v && (Roles.Of(i, k) & Role.Def) != 0) written = true;
                    if (written) break;
                }
                if (written) continue;
                foreach (int s in _succ[b])
                    if (body.Contains(s) && s != header && seen.Add(s)) work.Push(s);
            }
            return false;
        }
    }

    /// <summary>
    /// What a cmov may read where a branch read it only on its own way: a
    /// virtual register, a constant (by a register), or memory reading which
    /// cannot fault -- the frame's slots and a static's own storage, with no
    /// index that might take the address anywhere else.
    /// </summary>
    private static bool Source(MOperand o) => o switch
    {
        MReg r => !r.IsPhys && !r.IsFloat,
        MImm imm => imm.IsPlain,
        MMem mem => mem.Index is null && mem.Segment == 0 && !mem.Got
            && (mem.Base is { IsPhys: true, Id: (int)Gpr.Rbp or (int)Gpr.Rsp }
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
        for (int b = 0; b < nb; b++)
        {
            MBlock block = m.Blocks[b];
            outside[b] = b == 0 || block.Source?.IsLandingPad == true;
            foreach (MInstr i in block.Instrs)
            {
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is MReg r && followed.TryGetValue(r.Id, out int bit) && (Roles.Of(i, k) & Role.Def) != 0)
                        written[b] |= 1UL << bit;
                    if (i.Operands[k] is MMem { Label: { } lm } && index.TryGetValue(lm, out int tm)) outside[tm] = true;
                }
            }
            foreach (MBlock s in m.Successors(b))
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
