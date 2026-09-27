#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// OWNED FIELDS: what an object owned by the compiler holds in its reference
/// fields dies with it, when that is provable.
///
/// Escape analysis frees an object whose whole life it can see (a frame slot,
/// an owned allocation, a fresh function's result). What such an object holds
/// in its fields used to stay behind for the collector however short-lived
/// the owner: a List made, filled and dropped in one call left its array --
/// most of its memory -- as garbage, because the array was made inside
/// List.Grow and stored in a field there, which is an escape as far as Grow
/// is concerned.
///
/// A reference field of an owned object is freed together with the object
/// when every piece of code that touches that field, in the owner's function
/// and in every callee the object is handed to, keeps to three rules:
///
///   1. What it stores there is a FRESH object -- an allocation, or a fresh
///      function's result -- made for the field and let go no other way;
///      or null; or what was just loaded from the same field (put back).
///      Anything else (a shared array, a parameter, a number) makes the
///      field DIRTY, and a dirty field is never freed: freeing it could
///      free something somebody else holds.
///   2. What it loads from there does not escape: it is read, written into,
///      handed to callees that keep nothing, compared -- never stored
///      elsewhere, returned, or handed to a callee that keeps it.
///   3. The object itself is used only through constant field offsets:
///      handed to a callee at its base, read, written. An interior pointer
///      handed away, a copy of the object's bytes, an element index -- the
///      object becomes OPAQUE and none of its fields is freed.
///
/// Each function is summarised, bottom-up, for every parameter that does not
/// escape (what it does to that object's fields: ParameterFields) and, when
/// it returns a fresh object, for what it has left in that object's fields
/// (FreshFields). The owner's function merges the summaries of the callees
/// its object reaches with its own uses, and a field that some code filled
/// with fresh objects (rule 1) and none made dirty is CLEAN: at each point
/// where the owner dies, the field's current object is freed first
/// (Runtime.FreeField), provided no reference loaded from the field is still
/// in use there.
///
/// What a field held before it was overwritten -- the smaller array a List
/// grew out of -- is not freed: nothing here proves it is not still read.
/// The collector takes it, as before.
/// </summary>
public sealed partial class Escape
{
    /// <summary>Runtime.FreeField(long at, long offset): the field's object given back, before the owner's.</summary>
    public const string FieldFreer = "m_Runtime_FreeField_2_V$I64_V$I64";

    /// <summary>
    /// The collector told of a reference: the write barrier, whole or inlined
    /// (Gc.MarkAt, Gc.Report). It keeps no pointer the program can use, and a
    /// block given back is refused when its turn comes to be marked.
    /// </summary>
    internal static bool IsCollectorNote(string? callee) =>
        callee is "m_Gc_MarkAt_1_V$I64" or "m_Gc_Report_1_V$I64"
            or "m_Runtime_WriteBarrier_2_V$I64_V$I64" or "m_Gc_Barrier_2_V$I64_V$I64";

    /// <summary>How many fields this pass arranged to free with their owner.</summary>
    public int FieldsOwned { get; private set; }

    /// <summary>What a piece of code does to the fields of one object.</summary>
    internal sealed class FieldSummary
    {
        /// <summary>Used some way the field rules cannot follow: no field is freed.</summary>
        public bool Opaque;
        /// <summary>Word offsets something other than a fresh object was stored at, or whose loaded object escaped.</summary>
        public readonly HashSet<long> Dirty = new();
        /// <summary>Word offsets a fresh object was stored at.</summary>
        public readonly HashSet<long> FreshStored = new();
        /// <summary>The owner's own loads of each field, for the liveness check where it dies.</summary>
        public readonly List<(long Offset, VReg Value)> Loads = new();

        public void Merge(FieldSummary? other)
        {
            if (other is null) { Opaque = true; return; }
            Opaque |= other.Opaque;
            Dirty.UnionWith(other.Dirty);
            FreshStored.UnionWith(other.FreshStored);
        }

        public IEnumerable<long> Clean() => Opaque ? Enumerable.Empty<long>() : FreshStored.Where(o => !Dirty.Contains(o));
    }

    /// <summary>Per function, per parameter: its field summary; null for a parameter that escapes or is not a reference.</summary>
    private readonly Dictionary<string, FieldSummary?[]> _paramFields = new(StringComparer.Ordinal);

    /// <summary>Per fresh function: what it left in the fields of the object it hands over.</summary>
    private readonly Dictionary<string, FieldSummary> _freshFields = new(StringComparer.Ordinal);

    /// <summary>Instructions this pass inserted to track owned objects: not uses of the objects.</summary>
    private readonly HashSet<Instr> _bookkeeping = new(ReferenceEqualityComparer.Instance);

    /// <summary>An object this pass took ownership of, and where it dies.</summary>
    private sealed class OwnedRecord
    {
        /// <summary>What made it: the allocation, the fresh call, or (for a frame slot) the zeroing that replaced the allocation.</summary>
        public required Instr Origin { get; init; }
        public required VReg Root { get; init; }
        /// <summary>A frame-slot object: the register holding the slot's address, and the slot.</summary>
        public VReg? SlotAddress { get; init; }
        public FrameSlot? Slot { get; init; }
        /// <summary>Where the previous object from the same site dies: before this instruction, or after it (RenewAfter).</summary>
        public Instr? Renew { get; init; }
        public bool RenewAfter { get; init; }
        /// <summary>The frees of an owned allocation or fresh result: the call, and the register with the pointer it frees.</summary>
        public List<(Block Block, Instr Free, VReg Pointer)> Frees { get; } = new();
        public long Bytes { get; init; } = -1;
        public string? FreshCallee { get; set; }
    }

    private readonly Dictionary<Function, List<OwnedRecord>> _records = new();

    private void Record(Function f, OwnedRecord r)
    {
        if (!_records.TryGetValue(f, out List<OwnedRecord>? list)) _records[f] = list = new();
        list.Add(r);
    }

    // ---- the summaries -------------------------------------------------------------------

    private FieldSummary?[] ParameterFields(Function f, Dictionary<string, bool[]> summaries)
    {
        FieldSummary?[] result = new FieldSummary?[f.Params.Count];
        bool[] escapes = summaries[f.Name];
        if (f.Async is not null) return result;
        for (int p = 0; p < f.Params.Count; p++)
        {
            VReg param = f.Params[p];
            if (param.Type is not (IrType.I32 or IrType.I64) || (p < escapes.Length && escapes[p])) continue;
            result[p] = FieldUses(f, new[] { param }, summaries, null, null);
        }
        return result;
    }

    private FieldSummary FreshFields(Function f, Dictionary<string, bool[]> summaries, List<Instr> origins, HashSet<VReg> chain)
    {
        FieldSummary merged = new();
        // A returned register joined from several paths is not followed by
        // the address walk, which wants a single definition: uses of it would
        // go unseen. Only a straight chain is summarised.
        Defs defs = new(f, buildCfg: false);
        foreach (VReg r in chain)
        {
            if (!defs.IsSingle(r) && !f.Params.Contains(r)) { merged.Opaque = true; return merged; }
            if (defs.Definition(r) is { Op: Opcode.Phi }) { merged.Opaque = true; return merged; }
        }
        foreach (Instr origin in origins)
        {
            FieldSummary one = FieldUses(f, new[] { origin.Dest! }, summaries, origin, chain);
            if (origin.Callee is not null && !IsAllocator(origin.Callee))
                one.Merge(_freshFields.GetValueOrDefault(origin.Callee));
            merged.Merge(one);
        }
        return merged;
    }

    // ---- one object's fields ---------------------------------------------------------------

    /// <summary>
    /// What `f` does to the fields of the object whose base the roots hold.
    /// `source` (the instruction that made it) and this pass's own bookkeeping
    /// are not uses; a return of a register in `returnable` hands the object
    /// over and is allowed.
    /// </summary>
    internal FieldSummary FieldUses(Function f, IEnumerable<VReg> roots, Dictionary<string, bool[]> summaries,
        Instr? source, HashSet<VReg>? returnable)
    {
        FieldSummary fs = new();
        int word = IrTypes.Word.Bytes();
        Dictionary<VReg, long> addresses = OwnedFieldEscape.Addresses(f, roots);
        Defs defs = new(f, buildCfg: false);
        List<(long At, VReg Value, Instr Load)> loads = new();
        List<(long At, Instr Store)> stores = new();

        void DirtyRange(long at, long bytes)
        {
            long first = at - ((at % word) + word) % word;
            for (long o = first; o < at + bytes; o += word) fs.Dirty.Add(o);
        }

        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (ReferenceEquals(i, source) || _bookkeeping.Contains(i)) continue;
                bool uses = false;
                foreach (VReg u in IrInfo.Uses(i)) if (addresses.ContainsKey(u)) { uses = true; break; }
                if (!uses) continue;
                // An address of the object derived by a constant: followed already.
                if (i.Dest is not null && addresses.ContainsKey(i.Dest)
                    && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Add or Opcode.Sub) continue;

                switch (i.Op)
                {
                    case Opcode.Load:
                    {
                        if (i.Operands[0] is not RegOperand baseReg || !addresses.TryGetValue(baseReg.Reg, out long off))
                        { fs.Opaque = true; break; }
                        long at = off + i.Offset;
                        if (i.Size == word && ((at % word) + word) % word == 0 && i.Dest is not null && defs.IsSingle(i.Dest))
                            loads.Add((at, i.Dest, i));
                        else if (i.Size >= word)
                            // A pointer read into a register that is written
                            // elsewhere too, or at another width: where it
                            // goes cannot be followed.
                            DirtyRange(at, i.Size);
                        break;
                    }
                    case Opcode.Store:
                    {
                        if (i.Operands.Count < 2) { fs.Opaque = true; break; }
                        // The object's own address stored anywhere -- in itself included.
                        if (i.Operands[1] is RegOperand v0 && addresses.ContainsKey(v0.Reg)) { fs.Opaque = true; break; }
                        if (i.Operands[0] is not RegOperand baseReg || !addresses.TryGetValue(baseReg.Reg, out long off))
                        { fs.Opaque = true; break; }
                        long at = off + i.Offset;
                        if (i.Size == word && ((at % word) + word) % word == 0) stores.Add((at, i));
                        else DirtyRange(at, i.Size);
                        break;
                    }
                    case Opcode.MemSet:
                    {
                        // Zeroing: nulls, which any field may hold.
                        bool zero = i.Operands.Count >= 2 && i.Operands[1] is ImmOperand { Value: 0 };
                        bool baseOnly = i.Operands.Count >= 1 && i.Operands[0] is RegOperand t && addresses.ContainsKey(t.Reg);
                        for (int k = 1; k < i.Operands.Count; k++)
                            if (i.Operands[k] is RegOperand r && addresses.ContainsKey(r.Reg)) baseOnly = false;
                        if (!zero || !baseOnly) fs.Opaque = true;
                        break;
                    }
                    case Opcode.Call:
                    {
                        if (IsCollectorNote(i.Callee)) break;
                        for (int a = 0; a < i.Operands.Count; a++)
                        {
                            if (i.Operands[a] is not RegOperand arg || !addresses.TryGetValue(arg.Reg, out long off)) continue;
                            if (off != 0 || i.Callee is null || !_paramFields.TryGetValue(i.Callee, out FieldSummary?[]? callee)
                                || a >= callee.Length)
                            {
                                fs.Opaque = true;
                                break;
                            }
                            fs.Merge(callee[a]);
                        }
                        break;
                    }
                    case Opcode.Ret:
                        if (returnable is null || i.Operands.Count != 1 || i.Operands[0] is not RegOperand back
                            || !returnable.Contains(back.Reg) || addresses[back.Reg] != 0)
                            fs.Opaque = true;
                        break;
                    case Opcode.Branch:
                    case Opcode.Switch:
                        break;
                    default:
                        if (!IrInfo.IsIntCompare(i.Op)) fs.Opaque = true;
                        break;
                }
                if (fs.Opaque) return fs;
            }
        }

        // What went into each field (rule 1).
        Dictionary<Instr, int> storedOrigins = new(ReferenceEqualityComparer.Instance);
        List<(long At, Instr Origin)> fresh = new();
        foreach ((long at, Instr st) in stores)
        {
            Operand value = st.Operands[1];
            if (value is ImmOperand { Value: 0 }) continue;
            if (value is not RegOperand vr) { fs.Dirty.Add(at); continue; }
            // Put back what was just taken from the same field.
            if (loads.Any(l => l.At == at && ReferenceEquals(l.Value, vr.Reg))) continue;
            Instr? origin = FreshOrigin(f, defs, vr.Reg);
            if (origin is null || ReferenceEquals(origin, source) || _owned.Contains(origin) || _ownedCalls.Contains(origin))
            {
                fs.Dirty.Add(at);
                continue;
            }
            Flow flow = Analyse(f, new[] { origin.Dest! }, summaries, origin, new HashSet<Instr>(ReferenceEqualityComparer.Instance) { st });
            if (flow.Escapes) { fs.Dirty.Add(at); continue; }
            storedOrigins[origin] = storedOrigins.GetValueOrDefault(origin) + 1;
            fresh.Add((at, origin));
        }
        foreach ((long at, Instr origin) in fresh)
        {
            // One object in two fields would be freed twice.
            if (storedOrigins[origin] > 1) fs.Dirty.Add(at);
            else fs.FreshStored.Add(at);
        }

        // What came out of each field (rule 2).
        foreach ((long at, VReg v, Instr load) in loads)
        {
            fs.Loads.Add((at, v));
            if (fs.Dirty.Contains(at)) continue;
            HashSetOfStores putBack = new();
            foreach ((long sat, Instr st) in stores)
                if (sat == at && st.Operands[1] is RegOperand sv && ReferenceEquals(sv.Reg, v)) putBack.Set.Add(st);
            if (Analyse(f, new[] { v }, summaries, load, putBack.Set).Escapes) fs.Dirty.Add(at);
        }
        return fs;
    }

    private sealed class HashSetOfStores
    {
        public HashSet<Instr> Set { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>The allocation or fresh call a register holds the result of, through copies and width changes; or null.</summary>
    private Instr? FreshOrigin(Function f, Defs defs, VReg r)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (!defs.IsSingle(r) || defs.Definition(r) is not Instr d) return null;
            if (d.Op == Opcode.Call && (IsAllocator(d.Callee) || IsFreshCall(d))) return d;
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32)
                || d.Operands.Count != 1 || d.Operands[0] is not RegOperand from) return null;
            r = from.Reg;
        }
        return null;
    }

    // ---- freeing them with their owner ----------------------------------------------------

    /// <summary>
    /// Every object owned in `f`: its clean fields found, and freed at each
    /// point where it dies -- before its own free, or, for a frame slot,
    /// before the slot is zeroed for the next one and on every return (with
    /// the fields cleared on entry, since a frame starts as garbage).
    /// </summary>
    private void OwnFields(Function f, Dictionary<string, bool[]> summaries)
    {
        if (!_records.TryGetValue(f, out List<OwnedRecord>? records)) return;
        int word = IrTypes.Word.Bytes();
        // One analysis for all the records, as in PromoteIn: freeing a
        // record's fields adds only registers and calls of its own.
        Liveness? liveness = null;
        HashSet<VReg>? pads = null;
        foreach (OwnedRecord r in records)
        {
            VReg[] roots = r.SlotAddress is null ? new[] { r.Root } : new[] { r.Root, r.SlotAddress };
            FieldSummary fs = FieldUses(f, roots, summaries, r.Origin, null);
            if (r.FreshCallee is not null) fs.Merge(_freshFields.GetValueOrDefault(r.FreshCallee));
            List<long> clean = fs.Clean().Where(o => o >= 0 && (r.Bytes < 0 || o + word <= r.Bytes)).OrderBy(o => o).ToList();
            if (clean.Count == 0) continue;

            // Where the previous object dies inside the function, nothing
            // loaded from its fields may still be in use.
            if (r.Renew is not null)
            {
                HashSet<VReg> loaded = Derivations(f, fs.Loads.Where(l => clean.Contains(l.Offset)).Select(l => l.Value));
                liveness ??= new Liveness(f);
                pads ??= PadLive(liveness);
                if (LiveAt(f, liveness, pads, r.Renew, loaded)) continue;
            }

            if (r.Slot is not null && r.SlotAddress is not null)
            {
                SlotFieldFrees(f, r, clean);
            }
            else
            {
                foreach ((Block b, Instr free, VReg pointer) in r.Frees)
                {
                    int at = b.Instrs.IndexOf(free);
                    if (at < 0) continue;
                    List<Instr> before = new();
                    foreach (long o in clean) AppendFieldFree(f, before, pointer, o, free.Line);
                    b.Instrs.InsertRange(at, before);
                    _bookkeeping.UnionWith(before);
                }
            }
            FieldsOwned += clean.Count;
        }
    }

    private void SlotFieldFrees(Function f, OwnedRecord r, List<long> clean)
    {
        int word = IrTypes.Word.Bytes();
        int line = r.Origin.Line;
        // On entry: the fields empty, so the first free finds nothing.
        List<Instr> entry = new();
        VReg entryAddr = f.NewReg(IrTypes.Word, "fieldsp");
        entry.Add(new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(r.Slot!) }, Line = line });
        foreach (long o in clean)
            entry.Add(new Instr { Op = Opcode.Store, Size = word, Offset = o, Operands = { new RegOperand(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = line });
        f.Entry.Instrs.InsertRange(0, entry);
        _bookkeeping.UnionWith(entry);

        // Before the slot is zeroed for the next object: the last one's fields.
        foreach (Block b in f.Blocks)
        {
            int at = b.Instrs.IndexOf(r.Renew!);
            if (at < 0) continue;
            List<Instr> before = new();
            VReg addr = f.NewReg(IrTypes.Word, "fieldsp");
            before.Add(new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(r.Slot!) }, Line = line });
            foreach (long o in clean) AppendFieldFree(f, before, addr, o, line);
            b.Instrs.InsertRange(at, before);
            _bookkeeping.UnionWith(before);
            break;
        }

        // On every return.
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is not { Op: Opcode.Ret }) continue;
            List<Instr> before = new();
            VReg addr = f.NewReg(IrTypes.Word, "fieldsp");
            before.Add(new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(r.Slot!) }, Line = line });
            foreach (long o in clean) AppendFieldFree(f, before, addr, o, line);
            b.Instrs.InsertRange(b.Instrs.Count - 1, before);
            _bookkeeping.UnionWith(before);
        }
    }

    private static void AppendFieldFree(Function f, List<Instr> output, VReg owner, long offset, int line)
    {
        VReg argument = owner;
        if (owner.Type == IrType.I32)
        {
            argument = f.NewReg(IrType.I64, "fieldOwner");
            output.Add(new Instr { Op = Opcode.ZExt32, Dest = argument, Operands = { new RegOperand(owner) }, Line = line });
        }
        output.Add(new Instr { Op = Opcode.Call, Callee = FieldFreer,
            Operands = { new RegOperand(argument), new ImmOperand(offset, IrType.I64) }, Line = line });
    }

    /// <summary>The registers holding what the given ones hold, or an address inside it.</summary>
    private static HashSet<VReg> Derivations(Function f, IEnumerable<VReg> roots)
    {
        HashSet<VReg> set = new(roots);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is null || set.Contains(i.Dest)) continue;
                    if (i.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Add or Opcode.Sub or Opcode.Phi)) continue;
                    foreach (VReg u in IrInfo.Uses(i))
                        if (set.Contains(u)) { set.Add(i.Dest); changed = true; break; }
                }
        }
        return set;
    }

    /// <summary>Whether any of `regs` is live just after `at` (and so just before it, since `at` defines none of them).</summary>
    private static bool LiveAt(Function f, Liveness liveness, HashSet<VReg> pads, Instr at, HashSet<VReg> regs)
    {
        if (regs.Count == 0) return false;
        // A handler's reads are live everywhere (Escape.PadLive); a register
        // newer than the analysis has no answer and is taken to be live.
        foreach (VReg r in regs) if (pads.Contains(r) || !liveness.Tracks(r)) return true;
        foreach (Block b in f.Blocks)
        {
            if (!b.Instrs.Contains(at)) continue;
            foreach ((Instr i, ulong[] liveAfter) in liveness.WalkBackwards(b, skipNewer: true))
            {
                if (!ReferenceEquals(i, at)) continue;
                foreach (VReg r in regs)
                    if (!ReferenceEquals(r, at.Dest) && Liveness.Test(liveAfter, r.Id)) return true;
                return false;
            }
        }
        return true;
    }
}
