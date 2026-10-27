#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A store to a static nothing ever reads is not a store.
///
/// A program links the whole standard library as source and reaches a
/// fraction of it, but every static initialiser of every type still runs
/// before Main: `Path.DirectorySeparatorChar` is written by a program that
/// never names Path. The write is a word of code and a word of data for
/// nothing. This finds every symbol the module loads from, takes its
/// address for, or exports, and removes the stores to the rest -- then the
/// data item itself, since nothing is left to refer to it.
///
/// Only symbols with no address taken: a store through a pointer someone
/// computed from the symbol is a read this cannot see. A Copy of the
/// SymOperand into a register is exactly that and disqualifies the symbol.
///
/// Run twice: in the module stage, and again after the late passes
/// (Pipeline.RunLate), where a hook only the dropped collector read is
/// written by code that stays -- and what was stored into it, kept.
/// </summary>
public sealed class DeadStatics : IModulePass
{
    public string Name => "dead-statics";

    public void Run(Module m) => Sweep(m);

    /// <summary>The pass; answers the functions it took stores out of, none when it found nothing.</summary>
    internal static List<Function> Sweep(Module m)
    {
        List<Function> changed = new();
        if (m.Entry is null)
        {
            return changed;     // a library: its statics belong to whoever links it
        }

        HashSet<string> read = new(StringComparer.Ordinal);
        HashSet<string> written = new(StringComparer.Ordinal);

        // What an object linked in beside this one names, it may read.
        read.UnionWith(m.ObjectNames);
        // And what the backend reads that no IR names: a Windows image's
        // TLS slot, which every read of the thread block goes through.
        read.Add(Corsac.Lang.X86.Selector.TlsBaseSymbol);
        read.Add(Corsac.Lang.X86.Selector.TlsOffsetSymbol);
        foreach (DataItem d in m.Data)
        {
            if (m.PreserveExports && d.Exported) read.Add(d.Name);
            foreach (DataReloc r in d.Relocs)
            {
                read.Add(r.Symbol);
            }
        }

        foreach (Function f in m.Functions)
        {
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (i.Operands[k] is not SymOperand s)
                        {
                            continue;
                        }

                        // The address as the destination of a store is the
                        // one use that is not a read.
                        if (i.Op == Opcode.Store && k == 0)
                        {
                            written.Add(s.Name);
                        }
                        else
                        {
                            read.Add(s.Name);
                        }
                    }
                }
            }
        }

        HashSet<string> dead = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            if (d.Zero && !read.Contains(d.Name) && written.Contains(d.Name))
            {
                dead.Add(d.Name);
            }
        }

        if (dead.Count == 0)
        {
            return changed;
        }

        foreach (Function f in m.Functions)
        {
            int gone = 0;
            foreach (Block b in f.Blocks)
            {
                gone += b.Instrs.RemoveAll(i => i.Op == Opcode.Store
                                             && i.Operands[0] is SymOperand s
                                             && dead.Contains(s.Name));
            }
            if (gone > 0) changed.Add(f);
        }

        m.Data.RemoveAll(d => dead.Contains(d.Name));
        return changed;
    }
}

/// <summary>
/// A static nothing writes is zero, in an image that is the whole program.
///
/// The runtime keeps switches for whoever wants them: Gc.Revoke sets the
/// range the collector walks for, FreeJournalOn the journal, CheckFrees the
/// quarantine, VerifyHeapOn the checks after every collection, and a kernel
/// sets the census, the allocation sampler and lazily committed maps from
/// its command line. A program that calls none of them still carried every
/// test of every switch and all the code the tests guard -- loaded from a
/// word that is zero when the image starts and that no instruction of the
/// image ever changes. In a closed image (Module.ClosedImage) that is a
/// fact, and each load of such a static is the constant 0: the branch it
/// decides goes, and what only that branch called goes with it
/// (Pipeline.SweepLate).
///
/// A zero-filled item is taken only when everything that can name it is in
/// sight and none of it can make it anything but zero:
/// - every mention of it is the address of a load or a store, within its
///   bytes. A copy of the address, a call or an intrinsic given it, an
///   atomic on it, a relocation in data naming it: each is a write this
///   cannot follow, and the item stays;
/// - no object linked in from outside names it (Module.ObjectNames): the
///   kernel's entry stub writes the loader's pointer into a static by its
///   mangled name;
/// - it is not an export another unit may write;
/// - and every store to it that can run writes 0, with every load of the
///   items taken read as the 0 it is (Unzeroed): `_verify = _verify ||
///   false`, a switch copied from another that is never set, a store under
///   a test of the switch itself. Taken together, that is an induction over
///   the run -- the first store to make one of them anything but zero would
///   have to be one of these, every one of which writes 0 while they all
///   are -- so an item a store fails for is let go, and the rest are judged
///   again without it.
/// The item itself goes when nothing names it any more, as all data does
/// (Inline.RemoveDeadFunctions), but for what a stub the backend writes reads.
/// Thread statics are cells of each thread's block, not items of the
/// module, and are not touched.
/// </summary>
public static class UnwrittenStatics
{
    /// <summary>Every load of every such static made the constant 0 and every store to one gone: the functions that changed.</summary>
    public static List<Function> Run(Module m)
    {
        List<Function> changed = new();
        if (!m.ClosedImage || m.Entry is null) return changed;

        Dictionary<string, DataItem> zero = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            if (d.Zero && !(m.PreserveExports && d.Exported) && !m.ObjectNames.Contains(d.Name)) zero[d.Name] = d;
        }
        foreach (DataItem d in m.Data)
        {
            foreach (DataReloc r in d.Relocs) zero.Remove(r.Symbol);
        }
        if (zero.Count == 0) return changed;

        // Named other than as a load's or a store's address: let go. And
        // what each function loads of the rest, where it stores into them
        // anything but the constant 0.
        HashSet<string> named = new(StringComparer.Ordinal);
        List<Function> mentioning = new();
        List<(Function Function, HashSet<string> Loads)> storing = new();
        foreach (Function f in m.Functions)
        {
            bool mentions = false, stores = false;
            HashSet<string>? loads = null;
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (i.Operands[k] is not SymOperand s || !zero.TryGetValue(s.Name, out DataItem? d)) continue;
                        mentions = true;
                        if (k == 0 && i.Op == Opcode.Load && Within(d, s, i))
                        {
                            (loads ??= new(StringComparer.Ordinal)).Add(s.Name);
                            continue;
                        }
                        if (k == 0 && i.Op == Opcode.Store && Within(d, s, i))
                        {
                            stores |= !IrInfo.IsImm(i.StoredValue, 0);
                            continue;
                        }
                        named.Add(s.Name);
                    }
                }
            }
            if (mentions) mentioning.Add(f);
            if (stores) storing.Add((f, loads ?? new(StringComparer.Ordinal)));
        }
        foreach (string name in named) zero.Remove(name);

        // Written anything but 0 while they all are zero: let go, and the
        // stores of every function that loads what went judged again.
        HashSet<string> gone = new(StringComparer.Ordinal);
        foreach ((Function f, _) in storing)
        {
            foreach (string name in Unzeroed(f, zero)) gone.Add(name);
        }
        while (gone.Count > 0)
        {
            foreach (string name in gone) zero.Remove(name);
            HashSet<string> went = gone;
            gone = new(StringComparer.Ordinal);
            foreach ((Function f, HashSet<string> loads) in storing)
            {
                if (!loads.Overlaps(went)) continue;
                foreach (string name in Unzeroed(f, zero))
                {
                    if (zero.ContainsKey(name)) gone.Add(name);
                }
            }
        }
        if (zero.Count == 0) return changed;

        foreach (Function f in mentioning)
        {
            bool touched = false;
            foreach (Block b in f.Blocks)
            {
                List<Instr> instrs = b.Instrs;
                for (int k = 0; k < instrs.Count; k++)
                {
                    Instr i = instrs[k];
                    if (i.Op is not (Opcode.Load or Opcode.Store) || i.Operands[0] is not SymOperand s || !zero.ContainsKey(s.Name)) continue;
                    touched = true;
                    if (i.Op == Opcode.Store || i.Dest is null)
                    {
                        instrs.RemoveAt(k--);
                        continue;
                    }
                    // A float's zero as lowering makes a float constant: the
                    // bits, an integer, made the float (Lowering.ConstValue).
                    VReg dest = i.Dest;
                    if (dest.Type.IsFloat())
                    {
                        VReg bits = f.NewReg(dest.Type == IrType.F32 ? IrType.I32 : IrType.I64);
                        instrs[k] = new Instr { Op = Opcode.Copy, Dest = bits, Line = i.Line, Operands = { new ImmOperand(0, bits.Type) } };
                        instrs.Insert(++k, new Instr { Op = Opcode.Bits, Dest = dest, Line = i.Line, Operands = { RegOperand.Of(bits) } });
                        continue;
                    }
                    instrs[k] = new Instr { Op = Opcode.Copy, Dest = dest, Line = i.Line, Operands = { new ImmOperand(0, dest.Type) } };
                }
            }
            if (touched) changed.Add(f);
        }
        return changed;
    }

    /// <summary>Whether the bytes a load or store at the symbol touches are the item's own.</summary>
    private static bool Within(DataItem d, SymOperand s, Instr i)
    {
        long at = s.Offset + i.Offset;
        return i.Size > 0 && at >= 0 && at + i.Size <= d.Bytes.Length;
    }

    /// <summary>What a register is known to hold: nothing yet, one constant, or more than one.</summary>
    private enum Fact : byte
    {
        Unreached,
        Constant,
        Varies,
    }

    private readonly record struct Known(Fact Fact, long Value)
    {
        public static readonly Known Varying = new(Fact.Varies, 0);
        public static Known Of(long value) => new(Fact.Constant, value);

        public Known Meet(Known other) =>
            Fact == Fact.Unreached ? other
            : other.Fact == Fact.Unreached ? this
            : Fact == Fact.Constant && other.Fact == Fact.Constant && Value == other.Value ? this
            : Varying;
    }

    /// <summary>
    /// THE ITEMS `f` MAY STORE ANYTHING BUT 0 INTO, run with every load of
    /// one of them the 0 it is assumed to hold. Sccp's rule, on registers
    /// that may be written more than once: a block runs when control can
    /// reach it from a root (the entry, a landing pad, a handler whose
    /// address is taken), a branch on a constant one way only; a register
    /// holds the meet of what its definitions in blocks that run make of
    /// their operands, a parameter or a register nothing defines anything.
    /// Swept in block order until nothing changes: facts only fall, so that
    /// ends. A store of a register nothing that runs defines yet is taken to
    /// write anything.
    /// </summary>
    private static List<string> Unzeroed(Function f, Dictionary<string, DataItem> zero)
    {
        Cfg cfg = new(f);
        Known[] values = new Known[f.RegCount];
        bool[] defined = new bool[f.RegCount];
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is { } dest) defined[dest.Id] = true;
            }
        }
        foreach (VReg p in f.Params) values[p.Id] = Known.Varying;
        bool[] runs = new bool[f.Blocks.Count];
        bool moved = false;
        void Reach(Block b)
        {
            moved |= !runs[b.Order];
            runs[b.Order] = true;
        }
        Known Of(Operand o) => o switch
        {
            ImmOperand imm => Known.Of(IrInfo.Normalise(imm.Value, imm.Type)),
            RegOperand { Reg: var r } => defined[r.Id] ? values[r.Id] : Known.Varying,
            _ => Known.Varying,
        };
        Known Evaluate(Instr i)
        {
            if (i.Op == Opcode.Load) return i.Operands[0] is SymOperand s && zero.ContainsKey(s.Name) ? Known.Of(0) : Known.Varying;
            // The same bits, as the other kind of register.
            if (i.Op is Opcode.Copy or Opcode.Bits) return Of(i.Operands[0]);
            if (!IrInfo.IsPure(i) || i.Op is Opcode.ArrayLength) return Known.Varying;
            bool unreached = false;
            foreach (Operand o in i.Operands)
            {
                Known k = Of(o);
                if (k.Fact == Fact.Varies) return Known.Varying;
                unreached |= k.Fact == Fact.Unreached;
            }
            if (unreached) return default;
            // Every operand a constant: the instruction folded as written so.
            Instr probe = new() { Op = i.Op, Dest = i.Dest };
            foreach (Operand o in i.Operands) probe.Operands.Add(new ImmOperand(Of(o).Value, o.Type));
            return ConstantFold.Fold(probe) is { Operands: [ImmOperand result] } ? Known.Of(IrInfo.Normalise(result.Value, i.Dest!.Type)) : Known.Varying;
        }

        foreach (Block root in cfg.Roots) Reach(root);
        while (moved)
        {
            moved = false;
            foreach (Block b in f.Blocks)
            {
                if (!runs[b.Order]) continue;
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Branch)
                    {
                        Known test = Of(i.Operands[0]);
                        if (test.Fact == Fact.Constant) Reach(IrInfo.Normalise(test.Value, i.Operands[0].Type) != 0 ? i.Targets[0] : i.Targets[1]);
                        else if (test.Fact == Fact.Varies) { Reach(i.Targets[0]); Reach(i.Targets[1]); }
                    }
                    else if (i.IsTerminator)
                    {
                        foreach (Block next in cfg.Succs(b)) Reach(next);
                    }
                    else if (i.Dest is { } dest && values[dest.Id].Fact != Fact.Varies)
                    {
                        Known was = values[dest.Id], now = was.Meet(Evaluate(i));
                        if (now == was) continue;
                        values[dest.Id] = now;
                        moved = true;
                    }
                }
            }
        }

        List<string> unzeroed = new();
        foreach (Block b in f.Blocks)
        {
            if (!runs[b.Order]) continue;
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Store && i.Operands[0] is SymOperand s && zero.ContainsKey(s.Name)
                    && Of(i.StoredValue) is not { Fact: Fact.Constant, Value: 0 })
                {
                    unzeroed.Add(s.Name);
                }
            }
        }
        return unzeroed;
    }
}
