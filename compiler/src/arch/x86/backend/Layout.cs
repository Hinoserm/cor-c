#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// THE ORDER THE BLOCKS ARE LAID OUT IN, chosen so that as many jumps as can
/// be are to the block that comes next -- which the encoder then leaves out
/// (a `jmp` to the next block) or turns around (`jcc T; jmp F` with T next
/// becomes `j!cc F`, Peephole). Lowering makes blocks in the order it meets
/// them, which puts a function's exit near its entry and the rest of a test
/// after both of its arms, and every such block cost a taken jump.
///
/// Runs after allocation, so the blocks are final: the allocator's moves
/// and spills are in them, and nothing after this adds a block.
///
/// - A jump to a block that is only a jump goes straight to where that one
///   goes.
/// - A block that falls through keeps the block after it: the two are one
///   SEGMENT, and segments are what move.
/// - From a segment's end the next segment is the one its closing `jmp`
///   goes to, or the conditional jump's target before it, whichever comes
///   first after it in the old order -- the arm the source wrote first, a
///   loop's body before its exit -- and failing both, whichever of them is
///   still to be placed.
/// - When neither can follow, the earliest segment not yet placed does. The
///   entry is always first.
/// - A segment that ends in a trap (the int3 or ud2 after a call that
///   never returns) follows nothing and goes after all the others.
///
/// Deterministic: it reads nothing but the blocks' order and their jumps.
/// </summary>
internal static class Layout
{
    public static void Run(MFunction m)
    {
        int n = m.Blocks.Count;
        if (n < 3)
        {
            return;
        }
        ThreadJumps(m);

        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < n; b++)
        {
            index[m.Blocks[b]] = b;
        }

        // A segment starts at the entry and after every block that cannot
        // fall out of its bottom; segmentOf maps a block to the segment it
        // starts, or -1 when it is reached by falling into it.
        List<int> starts = new();
        int[] segmentOf = new int[n];
        for (int b = 0; b < n; b++)
        {
            if (b == 0 || m.Blocks[b - 1].EndsUnconditionally)
            {
                segmentOf[b] = starts.Count;
                starts.Add(b);
            }
            else
            {
                segmentOf[b] = -1;
            }
        }
        int segments = starts.Count;
        if (segments < 2)
        {
            return;
        }

        // A SEGMENT THAT ENDS IN A TRAP -- the `int3` after a call that never
        // comes back: an index out of range, a throw, a null receiver's --
        // IS COLD, AND GOES AFTER EVERYTHING ELSE. Laid where its test was
        // written, each one sat between the lines the function runs every
        // time: a check's call and its padding in the middle of a packet's
        // path, the bytes after it on another cache line. A processor of
        // 64 KB of instruction cache (the Geode LX; a 486's eight) runs the
        // kernel's network path over more code than that, and what it never
        // executes should not share those lines. At the end, the branch to
        // it is forward and not taken.
        bool[] cold = new bool[segments];
        for (int s = 1; s < segments; s++)
        {
            int end = (s + 1 < segments ? starts[s + 1] : n) - 1;
            // The trap after a call that never returns, not a breakpoint the
            // program runs on from (`int3` with an operand, Peephole.BeforeTrap).
            cold[s] = m.Blocks[end].Instrs.Count > 0 && EndsInTrap(m.Blocks[end].Instrs[^1]);
            if (cold[s])
            {
                for (int b = starts[s]; b <= end; b++)
                {
                    m.Blocks[b].Cold = true;
                }
            }
        }

        bool[] placed = new bool[segments];
        List<int> order = new(segments);
        int nextUnplaced = 0;
        int current = 0;
        while (true)
        {
            placed[current] = true;
            order.Add(current);
            int last = (current + 1 < segments ? starts[current + 1] : n) - 1;
            int follow = Follower(m.Blocks[last], current, index, segmentOf, placed, cold);
            if (follow < 0)
            {
                while (nextUnplaced < segments && (placed[nextUnplaced] || cold[nextUnplaced]))
                {
                    nextUnplaced++;
                }
                if (nextUnplaced == segments)
                {
                    break;
                }
                follow = nextUnplaced;
            }
            current = follow;
        }
        for (int s = 1; s < segments; s++)
        {
            if (cold[s] && !placed[s])
            {
                order.Add(s);
            }
        }

        List<MBlock> laid = new(n);
        foreach (int s in order)
        {
            int end = s + 1 < segments ? starts[s + 1] : n;
            for (int b = starts[s]; b < end; b++)
            {
                laid.Add(m.Blocks[b]);
            }
        }
        m.Blocks.Clear();
        m.Blocks.AddRange(laid);
    }

    /// <summary>
    /// The segment to lay out after <paramref name="current"/>, which ends in
    /// <paramref name="block"/>, or -1. Segments are numbered in the old
    /// order, so one ahead of it has a larger number.
    /// </summary>
    /// <summary>
    /// Whether an instruction is the trap after a call that never returns:
    /// `ud2`, or `int3` with no operand (one with an operand is a breakpoint
    /// the program runs on from, MInstr.EndsFlow).
    /// </summary>
    private static bool EndsInTrap(MInstr i) => i.Op == MOp.Ud2 || i is { Op: MOp.Int3, Operands.Count: 0 };

    private static int Follower(MBlock block, int current, Dictionary<MBlock, int> index, int[] segmentOf, bool[] placed, bool[] cold)
    {
        List<MInstr> instrs = block.Instrs;
        int k = instrs.Count;
        if (k == 0 || instrs[k - 1].Op != MOp.Jmp)
        {
            return -1;
        }
        int jump = Head(instrs[k - 1], index, segmentOf, placed, cold);
        int branch = k >= 2 && instrs[k - 2].Op == MOp.Jcc ? Head(instrs[k - 2], index, segmentOf, placed, cold) : -1;

        // The nearer one ahead in the old order, when either is ahead.
        int best = -1;
        foreach (int s in new[] { branch, jump })
        {
            if (s > current && (best < 0 || s < best))
            {
                best = s;
            }
        }
        if (best >= 0)
        {
            return best;
        }
        return jump >= 0 ? jump : branch;
    }

    /// <summary>The unplaced segment a jump's target starts, or -1 (a cold one is none).</summary>
    private static int Head(MInstr jump, Dictionary<MBlock, int> index, int[] segmentOf, bool[] placed, bool[] cold)
    {
        MBlock target = ((MLabel)jump.Operands[0]).Target;
        if (!index.TryGetValue(target, out int b))
        {
            return -1;
        }
        int s = segmentOf[b];
        // Never a cold one: it waits for the end.
        return s >= 0 && !placed[s] && !cold[s] ? s : -1;
    }

    /// <summary>
    /// Every jump to a block that holds nothing but a jump goes on to that
    /// jump's target instead, as far as a chain of them goes (a cycle of
    /// them, which only a loop with no body makes, is left as it is).
    /// </summary>
    private static void ThreadJumps(MFunction m)
    {
        foreach (MBlock block in m.Blocks)
        {
            List<MInstr> instrs = block.Instrs;
            for (int k = 0; k < instrs.Count; k++)
            {
                MInstr i = instrs[k];
                if (i.Op is not (MOp.Jmp or MOp.Jcc))
                {
                    continue;
                }
                MBlock target = ((MLabel)i.Operands[0]).Target;
                MBlock final = Through(target);
                if (!ReferenceEquals(final, target))
                {
                    MInstr moved = i.Op == MOp.Jmp
                        ? new MInstr(MOp.Jmp, new MLabel(final))
                        : new MInstr(MOp.Jcc, new MLabel(final)) { Cond = i.Cond };
                    moved.Line = i.Line;
                    instrs[k] = moved;
                }
            }
        }
    }

    private static MBlock Through(MBlock target)
    {
        MBlock at = target;
        for (int hops = 0; hops < 8; hops++)
        {
            if (at.Instrs.Count != 1 || at.Instrs[0].Op != MOp.Jmp)
            {
                return at;
            }
            MBlock next = ((MLabel)at.Instrs[0].Operands[0]).Target;
            if (ReferenceEquals(next, target) || ReferenceEquals(next, at))
            {
                return target;
            }
            at = next;
        }
        return at;
    }
}
