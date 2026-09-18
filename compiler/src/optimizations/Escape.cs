#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Escape analysis: an object whose reference never leaves the function
/// that made it does not need the heap.
///
/// The collector is the last resort, not the first (see the design's
/// "Memory" section). Most objects have lifetimes the compiler can prove:
/// the buffer a number is formatted into, an enumerator, a closure that is
/// called and dropped. After inlining, each of those is an allocation and
/// its every use in one function, and this pass decides whether the
/// reference ESCAPES -- is stored somewhere that outlives the function,
/// returned, handed to a callee that keeps it, or captured by anything the
/// analysis cannot see. One that does not escape becomes a frame slot,
/// zeroed as the allocator would have zeroed it, and the call is gone.
///
/// The rules are conservative in every direction the analysis cannot see:
/// a store of the pointer as a VALUE escapes, an indirect call escapes, an
/// atomic escapes, and a callee's parameter escapes unless the callee was
/// analysed and proved otherwise. What does NOT escape: using the pointer
/// as the base of a load or store (that is reading or writing the object),
/// copying it, adding a constant to it (a field address), passing it to a
/// system call (the kernel reads the memory during the call and keeps no
/// pointer -- a rule the runtime keeps by design), and passing it to a
/// callee whose summary says the parameter stays put.
///
/// An allocation inside a loop may be promoted only if nothing derived from
/// the previous iteration's object is still live when the allocation runs
/// again, since the slot is reused: that is the liveness check.
///
/// After promotion, if no allocation is reachable from the entry, the
/// program needs no collector, and Module.NeedsHeap says so.
/// </summary>
public sealed class Escape : IModulePass
{
    public string Name => "escape";

    /// <summary>The allocator every `new` calls. Its label is the runtime contract.</summary>
    public const string Allocator = "m_Runtime_Alloc_1_V$I64";

    /// <summary>At most this many bytes of a frame go to promoted objects.</summary>
    public int FrameBudget { get; init; } = 4096;

    /// <summary>A promoted object may be at most this large.</summary>
    public int ObjectLimit { get; init; } = 1024;

    public int Promoted { get; private set; }

    public void Run(Module m)
    {
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
        {
            byName[f.Name] = f;
        }

        // Which parameters of which functions escape: pessimistic until a
        // function has been analysed, bottom-up over the call graph so a
        // callee's answer is known before its callers ask. Cycles keep the
        // pessimistic answer.
        Dictionary<string, bool[]> summaries = new(StringComparer.Ordinal);
        foreach (Function f in BottomUp(m, byName))
        {
            summaries[f.Name] = ParameterSummary(f, summaries);
        }

        bool canFree = byName.ContainsKey(Freer);
        OwnedFieldEscape fields = new(byName, summaries);
        foreach (Function f in m.Functions)
        {
            PromoteIn(f, summaries, canFree, fields);
        }

        m.NeedsHeap = AnyAllocationReachable(m, byName);

        // A program that needs no collector still allocates on its way to
        // dying -- the exception object, the message it prints -- and those
        // go to a bump allocator that never frees, which the runtime provides
        // under this name. Then nothing calls the collecting allocator, and
        // the collector goes the way of every unreachable function.
        if (!m.NeedsHeap && byName.ContainsKey(BumpAllocator))
        {
            foreach (Function f in m.Functions)
            {
                foreach (Block b in f.Blocks)
                {
                    for (int k = 0; k < b.Instrs.Count; k++)
                    {
                        Instr i = b.Instrs[k];
                        if (i.Op != Opcode.Call)
                        {
                            continue;
                        }

                        // An allocation this pass owns keeps the real
                        // allocator: a bump region cannot take a block back,
                        // and giving the block back is the whole point. Its
                        // frees therefore keep the real free as well, which
                        // is why nothing is retargeted when the module has
                        // owned an allocation anywhere.
                        string? to = null;
                        if (i.Callee == Allocator && !_owned.Contains(i))
                        {
                            to = BumpAllocator;
                        }
                        else if (i.Callee == Freer && Owned == 0)
                        {
                            to = BumpFreer;
                        }
                        if (to is null)
                        {
                            continue;
                        }

                        Instr retargeted = new() { Op = Opcode.Call, Dest = i.Dest, Callee = to, Line = i.Line };
                        retargeted.Operands.AddRange(i.Operands);
                        b.Instrs[k] = retargeted;
                    }
                }
            }
        }
    }

    /// <summary>The allocator for a program that never collects: bump and forget.</summary>
    public const string BumpAllocator = "m_Runtime_AllocBump_1_V$I64";

    /// <summary>
    /// Giving a block back by hand. The compiler calls this only for an
    /// object whose whole life it has proved, which is why the runtime may
    /// keep it as cheap as it likes; it ignores anything that is not the
    /// payload of a live block, so freeing a zero is a no-op.
    /// </summary>
    public const string Freer = "m_Runtime_Free_1_V$I64";

    /// <summary>Free for a program with no heap to free into: nothing to do.</summary>
    public const string BumpFreer = "m_Runtime_FreeBump_1_V$I64";

    /// <summary>How many allocations this pass gave an explicit free rather than a frame slot.</summary>
    public int Owned { get; private set; }

    /// <summary>
    /// Allocation calls this pass took ownership of. They keep calling the
    /// real allocator -- a bump region cannot give memory back -- but they
    /// are not a reason to link a collector, because their memory comes
    /// back by a free the compiler placed, not by a collection.
    /// </summary>
    private readonly HashSet<Instr> _owned = new(ReferenceEqualityComparer.Instance);

    // ---- the escape question ------------------------------------------------------

    /// <summary>
    /// Everything derived from a set of root registers -- copies, width
    /// changes, constant offsets -- and whether any of it escapes. The
    /// derived set grows to a fixed point because a derivation may be
    /// written before the register it derives from is known to matter.
    /// </summary>
    internal sealed class Flow
    {
        public HashSet<VReg> Derived { get; } = new();
        public bool Escapes { get; set; }
        public Instr? Source { get; init; }
    }

    internal static Flow Analyse(Function f, IEnumerable<VReg> roots, Dictionary<string, bool[]> summaries, Instr? source,
        HashSet<Instr>? ownedStores = null)
    {
        Flow flow = new() { Source = source };
        foreach (VReg r in roots)
        {
            flow.Derived.Add(r);
        }

        // A register with more than one definition may hold something else
        // at another time; anything derived through it is unknowable. Count
        // definitions once.
        Dictionary<VReg, int> defs = new();
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null)
                {
                    defs[i.Dest] = defs.GetValueOrDefault(i.Dest) + 1;
                }
            }
        }
        foreach (VReg p in f.Params)
        {
            defs[p] = defs.GetValueOrDefault(p) + 1;
        }

        bool changed = true;
        while (changed && !flow.Escapes)
        {
            changed = false;
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (ReferenceEquals(i, source))
                    {
                        continue;
                    }

                    bool touches = false;
                    foreach (VReg r in IrInfo.Uses(i))
                    {
                        if (flow.Derived.Contains(r))
                        {
                            touches = true;
                            break;
                        }
                    }
                    if (!touches)
                    {
                        continue;
                    }

                    switch (i.Op)
                    {
                        case Opcode.Copy:
                        case Opcode.Trunc64:
                        case Opcode.ZExt32:
                        case Opcode.SExt32:
                            Derive(i.Dest);
                            break;

                        case Opcode.Add:
                        case Opcode.Sub:
                            // A field or element address: the pointer plus a
                            // constant, or plus a register that is not itself
                            // derived (an index). Two derived pointers added
                            // together is not an address anyone means.
                            if (i.Operands[1] is RegOperand r1 && flow.Derived.Contains(r1.Reg)
                                && i.Operands[0] is RegOperand r0 && flow.Derived.Contains(r0.Reg))
                            {
                                flow.Escapes = true;
                            }
                            Derive(i.Dest);
                            break;

                        case Opcode.Load:
                            // Reading the object, or reading through a field
                            // address. The loaded value is not the pointer.
                            break;

                        case Opcode.Store:
                            // Writing INTO the object is fine; writing the
                            // pointer itself somewhere is the escape.
                            if (i.Operands[1] is RegOperand v && flow.Derived.Contains(v.Reg)
                                && (ownedStores is null || !ownedStores.Contains(i)))
                            {
                                flow.Escapes = true;
                            }
                            break;

                        case Opcode.MemCopy:
                        case Opcode.MemSet:
                            // Bytes move; pointers do not.
                            break;

                        case Opcode.Eq:
                        case Opcode.Ne:
                        case Opcode.LtS:
                        case Opcode.LeS:
                        case Opcode.GtS:
                        case Opcode.GeS:
                        case Opcode.LtU:
                        case Opcode.LeU:
                        case Opcode.GtU:
                        case Opcode.GeU:
                        case Opcode.Branch:
                        case Opcode.Switch:
                            break;

                        case Opcode.Syscall:
                            // The kernel reads during the call and keeps no
                            // pointer: the runtime's contract with this pass.
                            break;

                        case Opcode.Call:
                        {
                            if (i.Callee is null || !summaries.TryGetValue(i.Callee, out bool[]? summary))
                            {
                                flow.Escapes = true;
                                break;
                            }
                            for (int a = 0; a < i.Operands.Count; a++)
                            {
                                if (i.Operands[a] is RegOperand arg && flow.Derived.Contains(arg.Reg)
                                    && (a >= summary.Length || summary[a]))
                                {
                                    flow.Escapes = true;
                                }
                            }
                            // The result of a call that took the pointer is
                            // not assumed to be the pointer: a callee that
                            // returns its argument is summarised as escaping
                            // that argument.
                            break;
                        }

                        default:
                            // Returned, unwound, atomically exchanged, passed
                            // indirectly, or anything else: gone.
                            flow.Escapes = true;
                            break;
                    }

                    if (flow.Escapes)
                    {
                        break;
                    }
                }
                if (flow.Escapes)
                {
                    break;
                }
            }
        }

        return flow;

        void Derive(VReg? d)
        {
            if (d is null)
            {
                return;
            }
            if (defs.GetValueOrDefault(d) > 1)
            {
                flow.Escapes = true;    // shared with another value; unknowable
                return;
            }
            if (flow.Derived.Add(d))
            {
                changed = true;
            }
        }
    }

    /// <summary>For each parameter: whether the function lets it escape. Returning it counts.</summary>
    private static bool[] ParameterSummary(Function f, Dictionary<string, bool[]> summaries)
    {
        bool[] result = new bool[f.Params.Count];
        for (int p = 0; p < f.Params.Count; p++)
        {
            VReg param = f.Params[p];
            if (param.Type is not (IrType.I32 or IrType.I64))
            {
                continue;
            }
            Flow flow = Analyse(f, new[] { param }, summaries, null);
            result[p] = flow.Escapes;
        }
        return result;
    }

    // ---- promotion -----------------------------------------------------------------

    private static List<Block> PromotionOrder(Function f)
    {
        // Spend a bounded frame budget on repeatedly executed allocations
        // before one-time setup. This is only a selection heuristic: no IR
        // moves, and every escape, renewal and liveness proof still runs.
        Cfg cfg = new(f);
        if (cfg.Roots.Count != 1) return f.Blocks.ToList();
        Dictionary<Block, int> depth = new();
        foreach (Block header in f.Blocks)
        {
            Block[] latches = cfg.Preds(header).Where(p => cfg.Dominates(header, p)).ToArray();
            if (latches.Length == 0) continue;
            HashSet<Block> loop = new() { header };
            Stack<Block> work = new(latches);
            while (work.TryPop(out Block? block))
                if (loop.Add(block)) foreach (Block pred in cfg.Preds(block)) work.Push(pred);
            if (loop.Any(b => !cfg.Dominates(header, b))) continue;
            foreach (Block block in loop) depth[block] = depth.GetValueOrDefault(block) + 1;
        }
        // Stable ties preserve the existing owner-before-child opportunity.
        return f.Blocks.OrderByDescending(b => depth.GetValueOrDefault(b)).ToList();
    }

    private void PromoteIn(Function f, Dictionary<string, bool[]> summaries, bool canFree, OwnedFieldEscape fields)
    {
        // An async body's frame does not outlive a suspension, and an object
        // held across one would be gone when it resumed.
        if (f.Async is not null)
        {
            return;
        }

        int budget = FrameBudget;
        Liveness? liveness = null;
        List<OwnedFieldEscape.Owner> owners = new();

        foreach (Block b in PromotionOrder(f))
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Call || i.Callee != Allocator || i.Dest is null || i.Operands.Count != 1
                    || _owned.Contains(i))
                {
                    continue;
                }

                bool sized = ConstantSize(f, i.Operands[0], out long size)
                             && size > 0 && size <= ObjectLimit && size <= budget;
                if (!sized && !canFree)
                {
                    continue;
                }

                Flow flow = Analyse(f, new[] { i.Dest }, summaries, i);
                OwnedFieldEscape.Owner promotedOwner = new() { Block = b, Root = i.Dest, Bytes = size };
                promotedOwner.Aliases.Add(i.Dest);
                bool canAnchor = true;
                if (flow.Escapes && sized && owners.Count != 0 && new Defs(f).IsSingle(i.Dest))
                {
                    HashSet<VReg> roots = new() { i.Dest };
                    HashSet<Instr> stores = new();
                    Defs fieldDefs = new(f);
                    for (int attempt = 0; attempt < 16 && flow.Escapes; attempt++)
                    {
                        bool added = false;
                        foreach (var owner in owners)
                        {
                            // The owner must be freshly made on every execution
                            // of this child allocation, including loop iterations.
                            // Across blocks, every cycle back to the child must
                            // pass through the owner creation again. An owner
                            // outside an inner allocation loop does not qualify.
                            if (!fieldDefs.IsSingle(owner.Root)
                                || !OwnedFieldEscape.OwnerRenews(fieldDefs.Cfg, owner.Block, b)) continue;
                            var addresses = OwnedFieldEscape.Addresses(f, owner.Aliases);
                            foreach (var targetBlock in f.Blocks)
                            foreach (Instr store in targetBlock.Instrs)
                            {
                                if (store.Op != Opcode.Store || stores.Contains(store) || store.Size != IrTypes.Word.Bytes()
                                    || !fieldDefs.Cfg.Dominates(b, targetBlock)
                                    || (targetBlock == b && targetBlock.Instrs.IndexOf(store) <= k)
                                    || store.Operands[1] is not RegOperand value || !flow.Derived.Contains(value.Reg)
                                    || store.Operands[0] is not RegOperand address || !addresses.TryGetValue(address.Reg, out long offset)
                                    || offset < 0 || store.Offset < 0 || offset > owner.Bytes - store.Offset) continue;
                                long field = offset + store.Offset;
                                if (field > owner.Bytes - store.Size) continue;
                                HashSet<VReg> loaded = new();
                                OwnedFieldEscape.Field referenceField = new(field, store.Size);
                                if (!fields.ReadsOwner(f, owner, new[] { referenceField }, loaded)) continue;
                                var childAddresses = OwnedFieldEscape.Addresses(f, promotedOwner.Aliases);
                                if (!childAddresses.TryGetValue(value.Reg, out long childOffset) || childOffset != 0)
                                    canAnchor = false;
                                promotedOwner.Aliases.UnionWith(loaded);
                                promotedOwner.Stores.Add(store);
                                promotedOwner.Parents.Add((owner, referenceField));
                                stores.Add(store); roots.UnionWith(loaded); added = true;
                            }
                        }
                        if (!added) break;
                        flow = Analyse(f, roots, summaries, i, stores);
                    }
                }
                if (flow.Escapes)
                {
                    continue;
                }

                // Inside a loop the slot -- or, for an owned allocation, the
                // one pointer the function remembers -- is reused each time
                // round, so the previous object must be dead by the time this
                // runs again.
                liveness ??= new Liveness(f);
                if (LiveAtSelf(liveness, b, i, flow.Derived))
                {
                    continue;
                }

                if (!sized)
                {
                    // Tier 1 all the same: the size is only known at run time
                    // (`new byte[n]`, a string of a computed length), so there
                    // is no frame slot to put it in, but the lifetime is just
                    // as proved. Keep the allocation and free it on the way
                    // out -- and, in a loop, free last time's before making
                    // this one, so the function holds at most one at once.
                    if (Own(f, b, i))
                    {
                        _owned.Add(i);
                        Owned++;
                        liveness = null;
                        // The allocation moved: instructions went in before it,
                        // and two after it that must not be scanned again.
                        k = b.Instrs.IndexOf(i) + 2;
                    }
                    continue;
                }

                int bytes = (int)((size + 7) & ~7L);
                FrameSlot slot = f.NewSlot(bytes, 8, "obj");
                VReg addr = f.NewReg(IrTypes.Word, "stackobj");

                // The allocator answers zeroed memory; so does this.
                List<Instr> replacement = new()
                {
                    new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = i.Line },
                    new Instr { Op = Opcode.MemSet, Operands = { new RegOperand(addr), new ImmOperand(0, IrType.I32), new ImmOperand(bytes, IrTypes.Word) }, Line = i.Line },
                };
                if (i.Dest.Type == IrTypes.Word)
                {
                    replacement.Add(new Instr { Op = Opcode.Copy, Dest = i.Dest, Operands = { new RegOperand(addr) }, Line = i.Line });
                }
                else
                {
                    replacement.Add(new Instr { Op = Opcode.ZExt32, Dest = i.Dest, Operands = { new RegOperand(addr) }, Line = i.Line });
                }

                b.Instrs.RemoveAt(k);
                b.Instrs.InsertRange(k, replacement);
                k += replacement.Count - 1;
                budget -= bytes;
                Promoted++;
                // Only base references may anchor another generation: an
                // interior reference would need a translated descendant path.
                if (canAnchor) owners.Add(promotedOwner);
                liveness = null;        // the block changed; recompute if asked again
            }
        }
    }

    /// <summary>
    /// Take ownership of an allocation whose size is not a constant: the
    /// function keeps its pointer in one frame slot, frees whatever the slot
    /// held before allocating again, and frees the slot on every return.
    ///
    /// The slot, not the register, is what the frees read, because the
    /// allocation need not run on the path that reaches a given return: a
    /// slot zeroed on entry says "nothing to free", and the runtime's free
    /// ignores a zero. It is also what makes a loop safe -- the pointer the
    /// slot holds is last time's object, which the liveness check has already
    /// proved dead here.
    ///
    /// Conservative on two counts. An Unwind out of the function does not
    /// free (the same terminator also jumps to a landing pad inside the
    /// function, where the object may still be live, and the two are not
    /// distinguished here), so an escaping exception leaks one object per
    /// site. And nothing is freed at a call that never returns; the process
    /// is ending there anyway.
    /// </summary>
    private static bool Own(Function f, Block block, Instr alloc)
    {
        List<(Block Block, int Index)> exits = new();
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is { Op: Opcode.Ret })
            {
                exits.Add((b, b.Instrs.Count - 1));
            }
        }
        // A function with no return -- the entry, which exits by a system
        // call -- still gains: the free before the next allocation is what
        // keeps a loop flat, and what the last iteration holds dies with the
        // process.
        int word = IrTypes.Word.Bytes();
        FrameSlot slot = f.NewSlot(word, word, "owned");

        // Entry: nothing owned yet.
        VReg entryAddr = f.NewReg(IrTypes.Word, "ownedp");
        f.Entry.Instrs.InsertRange(0, new List<Instr>
        {
            new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(slot) }, Line = alloc.Line },
            new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = alloc.Line },
        });

        int at = block.Instrs.IndexOf(alloc);
        if (at < 0)
        {
            return false;
        }

        // Before the allocation: give back what the last one made.
        VReg addr = f.NewReg(IrTypes.Word, "ownedp");
        VReg prev = f.NewReg(IrTypes.Word, "owned");
        List<Instr> releasePrevious = new()
        {
            new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = alloc.Line },
            new Instr { Op = Opcode.Load, Size = word, Dest = prev, Operands = { new RegOperand(addr) }, Line = alloc.Line },
        };
        AppendFree(f, releasePrevious, prev, alloc.Line);
        block.Instrs.InsertRange(at, releasePrevious);

        // After it: remember the new one.
        VReg made = f.NewReg(IrTypes.Word, "owned");
        Instr resize = alloc.Dest!.Type == IrTypes.Word
            ? new Instr { Op = Opcode.Copy, Dest = made, Operands = { new RegOperand(alloc.Dest) }, Line = alloc.Line }
            : new Instr { Op = IrTypes.Word == IrType.I32 ? Opcode.Trunc64 : Opcode.ZExt32,
                Dest = made, Operands = { new RegOperand(alloc.Dest) }, Line = alloc.Line };
        block.Instrs.InsertRange(block.Instrs.IndexOf(alloc) + 1, new List<Instr>
        {
            resize,
            new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(addr), new RegOperand(made) }, Line = alloc.Line },
        });

        foreach ((Block b, int _) in exits)
        {
            int r = b.Instrs.Count - 1;
            VReg a = f.NewReg(IrTypes.Word, "ownedp");
            VReg p = f.NewReg(IrTypes.Word, "owned");
            List<Instr> releaseExit = new()
            {
                new Instr { Op = Opcode.Copy, Dest = a, Operands = { new SlotOperand(slot) }, Line = alloc.Line },
                new Instr { Op = Opcode.Load, Size = word, Dest = p, Operands = { new RegOperand(a) }, Line = alloc.Line },
            };
            AppendFree(f, releaseExit, p, alloc.Line);
            b.Instrs.InsertRange(r, releaseExit);
        }

        return true;
    }

    private static void AppendFree(Function f, List<Instr> output, VReg pointer, int line)
    {
        // Ownership slots are machine words, but Runtime.Free(long) has a
        // language-level 64-bit ABI on every target. Never omit the high word.
        VReg argument = pointer;
        if (pointer.Type == IrType.I32)
        {
            argument = f.NewReg(IrType.I64, "freeAddress");
            output.Add(new Instr { Op = Opcode.ZExt32, Dest = argument,
                Operands = { new RegOperand(pointer) }, Line = line });
        }
        output.Add(new Instr { Op = Opcode.Call, Callee = Freer,
            Operands = { new RegOperand(argument) }, Line = line });
    }

    /// <summary>Whether any register derived from the allocation is live just before it: a loop carrying last time's object.</summary>
    internal static bool LiveAtSelf(Liveness liveness, Block b, Instr alloc, HashSet<VReg> derived)
    {
        foreach ((Instr i, ulong[] liveAfter) in liveness.WalkBackwards(b))
        {
            if (!ReferenceEquals(i, alloc))
            {
                continue;
            }
            // Live after the allocation minus what it defines is live before
            // it, as far as derived registers go: none of them is defined
            // by the allocation except its own result.
            foreach (VReg r in derived)
            {
                if (!ReferenceEquals(r, alloc.Dest) && Liveness.Test(liveAfter, r.Id))
                {
                    return true;
                }
            }
            return false;
        }
        return true;
    }

    private static bool ConstantSize(Function f, Operand o, out long size)
    {
        size = 0;
        for (int hops = 0; hops < 8; hops++)
        {
            switch (o)
            {
                case ImmOperand imm:
                    size = imm.Value;
                    return true;
                case RegOperand r:
                {
                    Instr? def = SingleDefinition(f, r.Reg);
                    if (def is null || def.Operands.Count != 1)
                    {
                        return false;
                    }
                    if (def.Op is not (Opcode.Copy or Opcode.SExt32 or Opcode.ZExt32 or Opcode.Trunc64))
                    {
                        return false;
                    }
                    o = def.Operands[0];
                    break;
                }
                default:
                    return false;
            }
        }
        return false;
    }

    private static Instr? SingleDefinition(Function f, VReg r)
    {
        Instr? found = null;
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (ReferenceEquals(i.Dest, r))
                {
                    if (found is not null)
                    {
                        return null;
                    }
                    found = i;
                }
            }
        }
        return found;
    }

    // ---- the module's verdict ----------------------------------------------------------

    private bool AnyAllocationReachable(Module m, Dictionary<string, Function> byName)
    {
        if (m.Entry is null || !byName.TryGetValue(m.Entry, out Function? entry))
        {
            return true;        // a library: its consumers decide
        }

        // Reachability over code AND data from the entry. A function
        // reaches what it calls and every symbol it names; a data item --
        // a vtable, a descriptor -- reaches every symbol its relocations
        // name. A vtable nothing live names is not live, and neither are the
        // methods in it.
        Dictionary<string, DataItem> data = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            data[d.Name] = d;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> work = new();
        work.Push(entry.Name);

        while (work.Count > 0)
        {
            string name = work.Pop();
            if (!seen.Add(name))
            {
                continue;
            }
            if (name == Allocator)
            {
                return true;
            }

            if (data.TryGetValue(name, out DataItem? item))
            {
                foreach (DataReloc r in item.Relocs)
                {
                    work.Push(r.Symbol);
                }
                continue;
            }

            if (!byName.TryGetValue(name, out Function? f))
            {
                continue;
            }

            foreach (Block b in f.Blocks)
            {
                // A block that ends in a trap is the program dying: a failed
                // bounds check, an unhandled exception. What it allocates on
                // the way is never freed by anyone, collector or not, so
                // nothing it names decides whether a collector is needed.
                if (b.Terminator is { Op: Opcode.Unreachable })
                {
                    continue;
                }
                foreach (Instr i in b.Instrs)
                {
                    // An allocation the pass owns is freed on every path out
                    // of its function, so it is tier 1 and names no heap.
                    if (i.Callee is not null && !_owned.Contains(i))
                    {
                        work.Push(i.Callee);
                    }
                    foreach (Operand o in i.Operands)
                    {
                        if (o is SymOperand s)
                        {
                            work.Push(s.Name);
                        }
                    }
                }
            }
        }
        return false;
    }

    private static List<Function> BottomUp(Module m, Dictionary<string, Function> byName)
    {
        List<Function> order = new();
        HashSet<Function> done = new();
        HashSet<Function> onPath = new();

        void Visit(Function f)
        {
            if (!done.Add(f))
            {
                return;
            }
            onPath.Add(f);
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is not null
                        && byName.TryGetValue(i.Callee, out Function? callee) && !onPath.Contains(callee))
                    {
                        Visit(callee);
                    }
                }
            }
            onPath.Remove(f);
            order.Add(f);
        }

        foreach (Function f in m.Functions)
        {
            Visit(f);
        }
        return order;
    }
}
