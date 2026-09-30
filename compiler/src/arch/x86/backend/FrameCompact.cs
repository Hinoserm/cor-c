#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE BUSIEST LOCALS NEAREST EBP. A frame's blocks are handed out as the
/// selector and the allocator first ask for them, so in a big function the
/// loop counter spilled last lands a kilobyte below EBP, and every access to
/// it carries a four-byte displacement where one byte would do: three bytes
/// on each of the instructions that touch it most. Once every instruction is
/// final, the blocks are laid out again, the most used per byte first, so
/// what the code touches most sits within the 128 bytes a one-byte
/// displacement reaches; every EBP-relative operand, safepoint and the frame's
/// own records follow. The frame does not grow. A function with an EBP-relative
/// access this cannot place in a block is left as it was.
/// </summary>
internal static class FrameCompact
{
    public static void Run(MFunction m)
    {
        var allocations = m.Frame.Allocations.OrderBy(a => a.Offset).ToList();
        if (allocations.Count < 2) return;
        int Find(int disp)
        {
            int lo = 0, hi = allocations.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var a = allocations[mid];
                if (disp < a.Offset) hi = mid - 1;
                else if (disp >= a.Offset + Math.Max(a.Bytes, 1)) lo = mid + 1;
                else return mid;
            }
            return -1;
        }
        static bool Local(MOperand o, out MMem mem)
        {
            mem = null!;
            if (o is not MMem { Base: { IsPhys: true } b, Symbol: null, Label: null } x || b.Phys != Gpr.Ebp || x.Disp >= 0) return false;
            mem = x;
            return true;
        }

        int[] uses = new int[allocations.Count];
        foreach (MBlock block in m.Blocks)
            foreach (MInstr i in block.Instrs)
                foreach (MOperand o in i.Operands)
                {
                    if (!Local(o, out MMem mem)) continue;
                    int at = Find(mem.Disp);
                    if (at < 0) return;       // somewhere no block is: leave the frame alone
                    uses[at]++;
                }
        foreach (Safepoint point in m.Safepoints.Values)
            foreach (int offset in point.SlotOffsets)
                if (offset < 0 && Find(offset) < 0) return;

        // Densest first; the original order breaks ties, so the result is
        // the same every build.
        int[] order = Enumerable.Range(0, allocations.Count)
            .OrderByDescending(k => (double)uses[k] / Math.Max(allocations[k].Bytes, 1)).ThenBy(k => k).ToArray();
        int size = 0;
        int[] placed = new int[allocations.Count];
        foreach (int k in order)
        {
            int align = Math.Max(allocations[k].Align, 4);
            size = (size + allocations[k].Bytes + align - 1) / align * align;
            placed[k] = -size;
        }
        if (size > m.Frame.Size) return;

        Dictionary<int, int> moved = new();
        for (int k = 0; k < allocations.Count; k++)
            if (placed[k] != allocations[k].Offset) moved[allocations[k].Offset] = placed[k];
        if (moved.Count == 0) return;
        int Map(int disp)
        {
            int at = Find(disp);
            return at < 0 ? disp : placed[at] + (disp - allocations[at].Offset);
        }

        foreach (MBlock block in m.Blocks)
            foreach (MInstr i in block.Instrs)
                for (int n = 0; n < i.Operands.Count; n++)
                {
                    if (!Local(i.Operands[n], out MMem mem)) continue;
                    int to = Map(mem.Disp);
                    if (to == mem.Disp) continue;
                    i.Operands[n] = new MMem(mem.Base, to)
                    {
                        IsSpill = mem.IsSpill, Index = mem.Index, Scale = mem.Scale,
                        Symbol = mem.Symbol, Reloc = mem.Reloc, Label = mem.Label,
                    };
                }
        foreach (Safepoint point in m.Safepoints.Values)
            for (int n = 0; n < point.SlotOffsets.Count; n++)
                point.SlotOffsets[n] = Map(point.SlotOffsets[n]);
        m.Frame.Move(moved);
    }
}
