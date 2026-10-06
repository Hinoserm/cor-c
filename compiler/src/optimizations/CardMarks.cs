#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE CARD MARK, WRITTEN OUT. Lowering emits the generational barrier as a
/// call to Runtime.CardMark after every store of a reference (Gc,
/// generations), and it stays a call through every lifetime pass: to them it
/// is a note to the collector, like the snapshot barrier, and an object whose
/// address it is given is not thereby let go of (EscapeFields.IsCollectorNote).
/// Written out as instructions there, the address it shifts would read to them
/// as the object leaving.
///
/// So this runs last, after escape analysis and the final inliner, and makes
/// each call what it stands for: the table read, and when there is one, the
/// byte for the store's kilobyte set.
///
///     t = load Runtime.Cards
///     branch t -> mark, after
///   mark:
///     store.u1 [t + (slot >> 10)], 1
///     jump after
/// </summary>
public sealed class CardMarks : IModulePass
{
    public string Name => "card-marks";

    /// <summary>Runtime.CardMark: the call lowering emits.</summary>
    public const string CardMark = Corsac.Lang.Lto.RuntimeAbi.CardMark;

    /// <summary>Runtime.Cards: the table, 0 when the collector keeps none.</summary>
    public const string Cards = "s_Runtime_Cards";

    /// <summary>A card is a kilobyte (Runtime.CardShift).</summary>
    public const int CardShift = 10;

    public void Run(Module m)
    {
        // THE STORE SEQUENCES, where the module's stores are made so
        // (Lowering.StoreSequences, which says it in the runtime helpers).
        bool sequences = Target.Current.Name == "x86" && m.RuntimeHelpers.Contains(Corsac.Lang.Lto.RuntimeAbi.RefStore);
        foreach (Function f in m.Functions)
        {
            DropFrameNotes(f);
            if (sequences) FuseStores(f);
            else DropNullCards(f);
            Expand(f);
        }
    }

    /// <summary>The image's sequence of a reference store with its whole barrier (X86 MachineIntrinsics.RefStore).</summary>
    public const string RefStore = "__x86.i.refstore";

    /// <summary>The image's sequence of a store with its card alone (X86 MachineIntrinsics.CardStore).</summary>
    public const string CardStore = "__x86.i.cardstore";

    /// <summary>
    /// EVERY REFERENCE STORE MADE ONE SEQUENCE (Lowering.StoreSequences): an
    /// image whose threads its kernel stops at any instruction stores
    /// through stubs that kernel sends no thread out of (X86Backend's store
    /// sequences). Lowering wrote each store as every pass reads one --
    ///
    ///     t = load Runtime.Marking          the barrier's test
    ///     branch t -> report, stored
    ///   report:
    ///     call Runtime.WriteBarrier(slot, value)
    ///     jump stored
    ///   stored:
    ///     call Runtime.CardMark(slot)       before, where cards are so marked
    ///     store [address + offset], value
    ///     call Runtime.CardMark(slot)
    ///
    /// -- and here, after every pass, the store becomes the sequence where it
    /// stands, `call refstore(slot, value)`, which tests Marking, reports,
    /// stores and marks the card, and the test, the report block and the card
    /// marks go. Nothing moves: what came before the store in `stored` still
    /// runs before it. A store with a card mark and no barrier -- a new
    /// block's (Lowering.StoreNew) -- becomes `call cardstore(slot, value)`,
    /// the store and the card. A store the shapes above cannot be found in is
    /// left as it was and said: its barrier's test and its store are two
    /// places a thread can be stopped between.
    /// </summary>
    public static void FuseStores(Function f)
    {
        Dictionary<VReg, Instr?> defs = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) defs[d] = defs.ContainsKey(d) ? null : i;
        foreach (VReg p in f.Params) defs[p] = null;

        // Where an operand points, as a base and a constant offset: through
        // copies, widenings and additions of a constant to their source.
        (Operand Base, long Offset) Canonical(Operand o)
        {
            long offset = 0;
            for (int hops = 0; hops < 8; hops++)
            {
                if (o is not RegOperand { Reg: var r } || !defs.TryGetValue(r, out Instr? def) || def is null) break;
                if (def.Op is Opcode.Copy or Opcode.ZExt32 or Opcode.Trunc64 && def.Operands.Count == 1) { o = def.Operands[0]; continue; }
                if (def.Op == Opcode.Add && def.Operands.Count == 2 && def.Operands[1] is ImmOperand right) { offset += right.Value; o = def.Operands[0]; continue; }
                if (def.Op == Opcode.Add && def.Operands.Count == 2 && def.Operands[0] is ImmOperand left) { offset += left.Value; o = def.Operands[1]; continue; }
                break;
            }
            return (o, offset);
        }
        static bool Same(Operand a, Operand b) => (a, b) switch
        {
            (RegOperand x, RegOperand y) => ReferenceEquals(x.Reg, y.Reg),
            (SymOperand x, SymOperand y) => x.Name == y.Name && x.Offset == y.Offset,
            (SlotOperand x, SlotOperand y) => ReferenceEquals(x.Slot, y.Slot),
            (ImmOperand x, ImmOperand y) => x.Value == y.Value,
            _ => false,
        };
        bool SameSlot(Operand slot, (Operand Base, long Offset) at)
        {
            (Operand b, long o) = Canonical(slot);
            return o == at.Offset && Same(b, at.Base);
        }
        (Operand Base, long Offset) SlotOf(Instr store)
        {
            (Operand b, long o) = Canonical(store.Operands[0]);
            return (b, o + store.Offset);
        }
        bool IsWordStore(Instr i) => i.Op == Opcode.Store && i.Operands.Count == 2 && i.Size == IrTypes.Word.Bytes();
        bool IsCardMark(Instr i) => i.Op == Opcode.Call && i.Dest is null && i.Callee == CardMark && i.Operands.Count == 1;

        // The store as its sequence, in place, with the card marks for its
        // slot on either side of it that nothing else falls between.
        void Fuse(Block block, int at, string sequence)
        {
            Instr store = block.Instrs[at];
            (Operand Base, long Offset) slot = SlotOf(store);
            for (int k = at + 1; k < block.Instrs.Count; k++)
            {
                Instr next = block.Instrs[k];
                if (IsCardMark(next) && SameSlot(next.Operands[0], slot)) { block.Instrs.RemoveAt(k); break; }
                if (next.Op is Opcode.Store or Opcode.Call or Opcode.CallIndirect or Opcode.MemCopy) break;
            }
            for (int k = at - 1; k >= 0; k--)
            {
                Instr before = block.Instrs[k];
                if (IsCardMark(before) && SameSlot(before.Operands[0], slot)) { block.Instrs.RemoveAt(k); at--; break; }
                if (before.Op is Opcode.Store or Opcode.Call or Opcode.CallIndirect or Opcode.MemCopy) break;
            }
            Operand address = store.Operands[0];
            if (store.Offset != 0)
            {
                VReg sum = f.NewReg(IrTypes.Word);
                block.Instrs.Insert(at, new Instr { Op = Opcode.Add, Dest = sum, Operands = { address, new ImmOperand(store.Offset, IrTypes.Word) }, Line = store.Line });
                at++;
                address = RegOperand.Of(sum);
            }
            block.Instrs[at] = new Instr { Op = Opcode.Call, Callee = sequence, Operands = { address, store.Operands[1] }, Line = store.Line };
        }

        int unfused = 0;

        // 1. The barriers' diamonds: the report block, its test's branch,
        //    the store it guards.
        Dictionary<Block, int> arrivals = new();
        foreach (Block b in f.Blocks)
        {
            if (b.Instrs.Count == 0) continue;
            Instr last = b.Instrs[^1];
            foreach (Block t in last.Targets) arrivals[t] = arrivals.GetValueOrDefault(t) + 1;
            if (last.Default is { } fallback) arrivals[fallback] = arrivals.GetValueOrDefault(fallback) + 1;
        }
        List<Block> gone = new();
        foreach (Block test in f.Blocks)
        {
            if (test.Instrs.Count == 0 || test.Instrs[^1] is not { Op: Opcode.Branch } branch || branch.Targets.Count != 2) continue;
            for (int side = 0; side < 2; side++)
            {
                Block report = branch.Targets[side], stored = branch.Targets[1 - side];
                if (ReferenceEquals(report, stored) || gone.Contains(report) || arrivals.GetValueOrDefault(report) != 1 || report.IsLandingPad) continue;
                if (report.Instrs.Count == 0 || report.Instrs[^1] is not { Op: Opcode.Jump } back || back.Targets.Count != 1 || !ReferenceEquals(back.Targets[0], stored)) continue;
                Instr? call = report.Instrs.FirstOrDefault(i => i.Op == Opcode.Call && i.Callee == Barrier && i.Operands.Count == 2);
                if (call is null || report.Instrs.Any(i => i.Op is Opcode.Store or Opcode.CallIndirect || i.Op == Opcode.Call && !ReferenceEquals(i, call))) continue;
                (Operand Base, long Offset) slot = Canonical(call.Operands[0]);
                int at = stored.Instrs.FindIndex(i => IsWordStore(i) && SameSlot(i.Operands[0], (slot.Base, slot.Offset - i.Offset)));
                if (at < 0 || stored.Instrs.Take(at).Any(i => IsWordStore(i) || i.Op is Opcode.CallIndirect || i.Op == Opcode.Call && !IsCardMark(i)))
                {
                    unfused++;
                    break;
                }
                Fuse(stored, at, RefStore);
                // The test goes: the branch is a jump to the store, the
                // report block nobody's.
                test.Instrs[^1] = new Instr { Op = Opcode.Jump, InitialTargets = new[] { stored }, Line = branch.Line };
                gone.Add(report);
                break;
            }
        }
        f.Blocks.RemoveAll(gone.Contains);

        // 2. A store with its card and no barrier.
        foreach (Block block in f.Blocks)
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                if (!IsWordStore(block.Instrs[k])) continue;
                (Operand Base, long Offset) slot = SlotOf(block.Instrs[k]);
                bool carded = false;
                for (int n = k + 1; n < block.Instrs.Count; n++)
                {
                    Instr next = block.Instrs[n];
                    if (IsCardMark(next) && SameSlot(next.Operands[0], slot)) { carded = true; break; }
                    if (next.Op is Opcode.Store or Opcode.Call or Opcode.CallIndirect or Opcode.MemCopy) break;
                }
                if (carded) Fuse(block, k, CardStore);
            }

        // 3. Said, where a barrier's diamond kept its window.
        if (unfused > 0)
            Console.Error.WriteLine("corc: warning: " + f.Name + ": " + unfused + " reference store(s) not made one sequence; a thread stopped between the barrier's test and the store can lose a reference to a concurrent mark");
    }

    /// <summary>
    /// A STORE INTO THIS FRAME TELLS THE COLLECTOR NOTHING. The snapshot
    /// barrier and the card mark are for references held in the heap; a frame
    /// is a root the collector reads whole at each stop. Lowering cannot tell
    /// a struct's `this` in the frame from one in an object and marks both,
    /// and a struct method inlined where its value is a local -- List's
    /// enumerator in every foreach -- kept a barrier test and a card mark
    /// call per element. Dropped where the address is a frame slot plus a
    /// constant, by its only definition. Not in an iterator's or async body,
    /// whose frame may be a block of the heap.
    /// </summary>
    public static void DropFrameNotes(Function f)
    {
        if (f.Async is not null) return;
        bool any = false;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Callee is CardMark or Barrier) { any = true; break; }
        if (!any) return;

        Dictionary<VReg, Instr?> defs = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) defs[d] = defs.ContainsKey(d) ? null : i;
        foreach (VReg p in f.Params) defs[p] = null;

        bool InFrame(Operand o)
        {
            for (int hops = 0; hops < 8; hops++)
            {
                if (o is SlotOperand) return true;
                if (o is not RegOperand { Reg: var r } || !defs.TryGetValue(r, out Instr? def) || def is null) return false;
                if (def.Op is Opcode.Copy or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Trunc64 && def.Operands.Count == 1) { o = def.Operands[0]; continue; }
                if (def.Op is Opcode.Add && def.Operands.Count == 2 && def.Operands[1] is ImmOperand) { o = def.Operands[0]; continue; }
                if (def.Op is Opcode.Add && def.Operands.Count == 2 && def.Operands[0] is ImmOperand) { o = def.Operands[1]; continue; }
                return false;
            }
            return false;
        }

        foreach (Block b in f.Blocks)
            for (int k = b.Instrs.Count - 1; k >= 0; k--)
            {
                Instr i = b.Instrs[k];
                if (i.Op == Opcode.Call && i.Dest is null && i.Callee is CardMark or Barrier && i.Operands.Count >= 1 && InFrame(i.Operands[0]))
                    b.Instrs.RemoveAt(k);
            }
    }

    /// <summary>
    /// A NULL STORED MAKES NO POINTER FOR A CARD TO REPORT. The card tells the
    /// next minor collection that a kilobyte may hold an old object's pointer
    /// to a young one; a store of null takes such a pointer away and never
    /// makes one, so its mark is work for nothing -- a call, and on i386 the
    /// stub's table read, test, shift and byte store. `node.Next = null`,
    /// `items[i] = default`, a list's Clear: every one called it. Dropped where
    /// the mark follows the word store it stands for in the same block with
    /// only computation between (its slot's address), and that store's value
    /// is the constant 0. The snapshot barrier before such a store stays: it
    /// reports the reference the store overwrites, which a null store loses
    /// as surely as any other. Not where stores become sequences (FuseStores),
    /// whose marks it pairs.
    /// </summary>
    public static void DropNullCards(Function f)
    {
        Dictionary<VReg, Instr?>? defs = null;
        foreach (Block b in f.Blocks)
            for (int k = b.Instrs.Count - 1; k >= 0; k--)
            {
                Instr mark = b.Instrs[k];
                if (mark.Op != Opcode.Call || mark.Dest is not null || mark.Callee != CardMark || mark.Operands.Count != 1) continue;
                int at = k - 1;
                while (at >= 0 && b.Instrs[at].Op != Opcode.Store && IrInfo.IsPure(b.Instrs[at])) at--;
                if (at < 0 || b.Instrs[at] is not { Op: Opcode.Store } store || store.Operands.Count != 2
                    || store.Size != IrTypes.Word.Bytes() || store.StoredValue is not ImmOperand { Value: 0 }) continue;
                if (defs is null)
                {
                    defs = new();
                    foreach (Block d in f.Blocks)
                        foreach (Instr i in d.Instrs)
                            if (i.Dest is { } dest) defs[dest] = defs.ContainsKey(dest) ? null : i;
                    foreach (VReg p in f.Params) defs[p] = null;
                }
                (Operand Base, long Offset) slot = Canonical(defs, mark.Operands[0]);
                (Operand Base, long Offset) stored = Canonical(defs, store.Operands[0]);
                // The base not written between: the same name, the same address.
                bool rewritten = stored.Base is RegOperand { Reg: var held }
                    && b.Instrs.Skip(at + 1).Take(k - at - 1).Any(between => ReferenceEquals(between.Dest, held));
                if (!rewritten && slot.Offset == stored.Offset + store.Offset && SameBase(slot.Base, stored.Base)) b.Instrs.RemoveAt(k);
            }
    }

    /// <summary>Where an operand points, as a base and a constant offset, through copies, widenings and constant additions.</summary>
    private static (Operand Base, long Offset) Canonical(Dictionary<VReg, Instr?> defs, Operand o)
    {
        long offset = 0;
        for (int hops = 0; hops < 8; hops++)
        {
            if (o is not RegOperand { Reg: var r } || !defs.TryGetValue(r, out Instr? def) || def is null) break;
            if (def.Op is Opcode.Copy or Opcode.ZExt32 or Opcode.Trunc64 && def.Operands.Count == 1) { o = def.Operands[0]; continue; }
            if (def.Op == Opcode.Add && def.Operands.Count == 2 && def.Operands[1] is ImmOperand right) { offset += right.Value; o = def.Operands[0]; continue; }
            if (def.Op == Opcode.Add && def.Operands.Count == 2 && def.Operands[0] is ImmOperand left) { offset += left.Value; o = def.Operands[1]; continue; }
            break;
        }
        return (o, offset);
    }

    private static bool SameBase(Operand a, Operand b) => (a, b) switch
    {
        (RegOperand x, RegOperand y) => ReferenceEquals(x.Reg, y.Reg),
        (SymOperand x, SymOperand y) => x.Name == y.Name && x.Offset == y.Offset,
        (SlotOperand x, SlotOperand y) => ReferenceEquals(x.Slot, y.Slot),
        _ => false,
    };

    /// <summary>Runtime.WriteBarrier: the snapshot barrier's slow path, which lowering calls.</summary>
    public const string Barrier = Corsac.Lang.Lto.RuntimeAbi.WriteBarrier;

    /// <summary>
    /// What lowering widened to pass as the runtime's `long`: the word it
    /// widened, when that is in the same block and not written again since.
    /// </summary>
    private static Operand Narrow(Block block, int k, Operand wide)
    {
        if (wide is not RegOperand { Reg: var held } || wide.Type == IrTypes.Word) return wide;
        for (int d = k - 1; d >= 0; d--)
        {
            if (!ReferenceEquals(block.Instrs[d].Dest, held)) continue;
            if (block.Instrs[d] is { Op: Opcode.ZExt32, Operands: [var narrow] } && narrow.Type == IrTypes.Word
                && (narrow is not RegOperand { Reg: var source }
                    || !block.Instrs.Skip(d + 1).Take(k - d - 1).Any(between => ReferenceEquals(between.Dest, source))))
                return narrow;
            break;
        }
        return wide;
    }

    public static void Expand(Function f)
    {
        // ON I386 THE SNAPSHOT BARRIER'S SLOW PATH IS A CALL TO THE OBJECT'S
        // STUB, slot and value in registers, which calls the runtime and
        // keeps every register: it runs only while a collection marks.
        //
        // THE CARD MARK IS WRITTEN OUT, as on every target: the table's load,
        // its test, a shift and a byte store. It ran as a call to a stub that
        // kept every register (push, load, test, push, shift, store, pop,
        // pop, ret) on EVERY store of a reference -- `node.Next = prev` in a
        // loop called it each lap. Written out it is four instructions on
        // the path that runs, and the register it needs for the table is
        // free again after the store.
        if (Target.Current.Name == "x86")
        {
            foreach (Block block in f.Blocks)
                for (int k = 0; k < block.Instrs.Count; k++)
                {
                    Instr i = block.Instrs[k];
                    if (i.Op == Opcode.Call && i.Callee == Barrier && i.Operands.Count == 2)
                    {
                        Operand at = Narrow(block, k, i.Operands[0]), value = Narrow(block, k, i.Operands[1]);
                        if (at.Type == IrTypes.Word && value.Type == IrTypes.Word)
                            block.Instrs[k] = new Instr { Op = Opcode.Call, Callee = "__x86.i.barrier", Operands = { at, value }, Line = i.Line };
                    }
                    else if (i.Op == Opcode.Call && i.Callee == CardMark && i.Operands.Count == 1)
                    {
                        // The address the runtime's long was widened from, so
                        // the shift below is of a word.
                        Operand slot = Narrow(block, k, i.Operands[0]);
                        if (!ReferenceEquals(slot, i.Operands[0]))
                            block.Instrs[k] = new Instr { Op = Opcode.Call, Callee = CardMark, Operands = { slot }, Line = i.Line };
                    }
                }
        }
        for (int b = 0; b < f.Blocks.Count; b++)
        {
            Block block = f.Blocks[b];
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr i = block.Instrs[k];
                if (i.Op != Opcode.Call || i.Callee != CardMark || i.Operands.Count != 1)
                {
                    continue;
                }

                Block mark = f.NewBlock("card");
                Block after = f.NewBlock("carded");
                after.Instrs.AddRange(block.Instrs.GetRange(k + 1, block.Instrs.Count - k - 1));
                block.Instrs.RemoveRange(k, block.Instrs.Count - k);

                Builder e = new(f, block);
                VReg table = e.Load(IrTypes.Word, new SymOperand(Cards), 0, IrTypes.Word.Bytes());
                e.Branch(table, mark, after);

                e.SetBlock(mark);
                // The runtime's parameter is a long; the address is a word.
                Operand slot = i.Operands[0];
                VReg word = slot is RegOperand { Reg: { } held } && held.Type == IrTypes.Word
                    ? held
                    : slot.Type == IrTypes.Word
                        ? e.Unary(Opcode.Copy, slot, IrTypes.Word)
                        : e.Unary(IrTypes.Word == IrType.I32 ? Opcode.Trunc64 : Opcode.ZExt32, slot, IrTypes.Word);
                VReg card = e.Binary(Opcode.Add, table, e.Binary(Opcode.ShrU, word, CardShift));
                e.Store(RegOperand.Of(card), new ImmOperand(1, IrType.I32), 0, 1);
                e.Jump(after);
                // The rest of the old block is `after` now; carry on there.
                break;
            }
        }
    }
}
