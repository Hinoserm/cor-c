#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Dead store elimination for memory the compiler owns: a store to a
/// static or a frame slot that is overwritten before anything could read
/// it is dropped, and a store to a frame slot that nothing reads before
/// the function returns is dropped too, because the frame is gone then.
///
/// Judged one block at a time, walking backwards with the set of regions
/// a later store covers. Any read that might reach the memory clears the
/// set for it: a load from the same static or slot at an overlapping
/// offset clears that region; a load through a register, a call of any
/// kind, an atomic, a fence, a block move or an unwind clears everything,
/// since a register may hold the address of a static or of a slot whose
/// address was taken, and a callee may read either -- a handler record
/// in a slot is read by the unwinder during a call that throws. This is
/// the conservative version the design asks for; a whole-function one
/// needs escape information about slots that does not exist yet.
/// </summary>
public sealed class Dse : IPass
{
    public string Name => "dse";

    /// <summary>A region a later store writes, by base key.</summary>
    private readonly record struct Region(long Offset, int Size);

    public void Run(Function f)
    {
        foreach (Block b in f.Blocks)
        {
            Dictionary<string, List<Region>> covered = new();
            // After a return no slot is read again; a slot load seen on the
            // way back marks just its region as read.
            bool slotsDead = b.Terminator?.Op == Opcode.Ret;
            Dictionary<string, List<Region>> slotReads = new();

            for (int k = b.Instrs.Count - 1; k >= 0; k--)
            {
                Instr i = b.Instrs[k];
                switch (i.Op)
                {
                    case Opcode.Store:
                        {
                            Operand addr = i.Operands[0];
                            if (addr is not (SymOperand or SlotOperand))
                            {
                                // Unknown target: it may be read by anything
                                // after it, and it may be the read of nothing
                                // we track -- but it could also cover a later
                                // static store's region, which we do not use.
                                covered.Clear();
                                slotsDead = false;
                                continue;
                            }
                            (string key, long off) = Key(addr, i.Offset);
                            bool dead = addr is SlotOperand && slotsDead
                                && !(slotReads.TryGetValue(key, out List<Region>? reads)
                                     && reads.Any(r => r.Offset < off + i.Size && off < r.Offset + r.Size));
                            if (!dead && covered.TryGetValue(key, out List<Region>? regions))
                            {
                                foreach (Region r in regions)
                                {
                                    if (r.Offset <= off && r.Offset + r.Size >= off + i.Size)
                                    {
                                        dead = true;
                                        break;
                                    }
                                }
                            }
                            if (dead)
                            {
                                b.Instrs.RemoveAt(k);
                                continue;
                            }
                            if (!covered.TryGetValue(key, out regions))
                            {
                                regions = new List<Region>();
                                covered[key] = regions;
                            }
                            regions.Add(new Region(off, i.Size));
                            continue;
                        }

                    case Opcode.Load:
                        {
                            Operand addr = i.Operands[0];
                            if (addr is not (SymOperand or SlotOperand))
                            {
                                covered.Clear();
                                slotsDead = false;
                                continue;
                            }
                            (string key, long off) = Key(addr, i.Offset);
                            if (covered.TryGetValue(key, out List<Region>? regions))
                            {
                                regions.RemoveAll(r => r.Offset < off + i.Size && off < r.Offset + r.Size);
                            }
                            if (addr is SlotOperand)
                            {
                                if (!slotReads.TryGetValue(key, out List<Region>? reads))
                                {
                                    reads = new List<Region>();
                                    slotReads[key] = reads;
                                }
                                reads.Add(new Region(off, i.Size));
                            }
                            continue;
                        }

                    case Opcode.Call:
                    case Opcode.CallIndirect:
                    case Opcode.Syscall:
                    case Opcode.Unwind:
                    case Opcode.MemCopy:
                    case Opcode.MemSet:
                    case Opcode.AtomicSwap:
                    case Opcode.AtomicAdd:
                    case Opcode.AtomicAnd:
                    case Opcode.AtomicOr:
                    case Opcode.AtomicXor:
                    case Opcode.AtomicCas:
                    case Opcode.Fence:
                        covered.Clear();
                        slotsDead = false;
                        continue;
                }
            }
        }
    }

    private static (string Key, long Offset) Key(Operand addr, long offset) => addr switch
    {
        SymOperand s => ($"s{s.Name}", s.Offset + offset),
        SlotOperand s => ($"l{s.Slot.Id}", offset),
        _ => throw new InvalidOperationException(),
    };
}
