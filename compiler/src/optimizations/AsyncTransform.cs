#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Makes an async method's body resumable.
///
/// Lowering writes the body as an ordinary function with two markers at each
/// await that may suspend: `__suspend`, then the call that registers the
/// continuation, then `__resume`. This pass runs after the optimiser, when
/// the registers are final, and for each such place:
///
///   * at the suspend, stores every register live after the resume into a
///     field of the state machine, records which resumption comes next,
///     registers the continuation, and returns;
///   * adds a resumption block that reloads those registers and carries on
///     with whatever followed the resume marker;
///   * gives the function a new entry that dispatches on the recorded state:
///     zero is the start, k is the k-th resumption.
///
/// Frame memory cannot survive a return, so every frame slot the function
/// uses -- address-taken locals, exception handler records, values held
/// across a finally -- becomes a field of the state machine first. Nothing
/// moves the machine, so an address into it stays good for its whole life.
///
/// It runs always, optimised or not: the markers are not something any
/// backend can emit.
///
/// A MACHINE THAT MAY MOVE (AsyncFrame.MayMove: an `async ValueTask` method's,
/// which starts in its kickoff's frame and is copied to the heap at its first
/// suspension) keeps every register that always points into it as an offset
/// from it across a suspension, so a resumption in the copy finds its own
/// fields. A register that may point into the machine on one path and
/// elsewhere on another cannot be kept so; then the method's StackSymbol word
/// is cleared, and its kickoff makes the machine on the heap, where it never
/// moves. A call handed an address in the machine (a struct result, an out
/// argument) marks the machine's cards after it, since what it stored there
/// went in with no mark of its own.
/// </summary>
public static class AsyncTransform
{
    public static void Run(Module m, int wordSize)
    {
        foreach (Function f in m.Functions)
        {
            if (f.Async is AsyncFrame frame)
            {
                // THE FRAME IS SAVED WITHOUT A BARRIER, word by word into the
                // machine at each suspension; its cards are marked there, when
                // the runtime has a card table to mark (Gc, generations).
                string? cards = m.RuntimeHelpers.Contains(CardMarkObject) ? CardMarkObject : null;
                int size = Transform(f, frame, wordSize, cards, out bool stays);
                if (!stays && frame.StackSymbol is string stack
                    && m.Data.FirstOrDefault(d => d.Name == stack) is DataItem allowed)
                {
                    for (int i = 0; i < allowed.Bytes.Length; i++) allowed.Bytes[i] = 0;
                }
                DataItem? item = m.Data.FirstOrDefault(d => d.Name == frame.SizeSymbol);
                if (item is not null)
                {
                    for (int i = 0; i < wordSize; i++)
                    {
                        // As a long: an int's shift count is taken mod 32, and
                        // an eight-byte word's top half would repeat its bottom.
                        item.Bytes[i] = (byte)((long)size >> (8 * i));
                    }
                }
            }
        }
    }

    private static int Align(int at, int to) => (at + to - 1) / to * to;

    private static (Block Block, int Index) Locate(Function f, Instr wanted)
    {
        foreach (Block b in f.Blocks)
        {
            int at = b.Instrs.IndexOf(wanted);
            if (at >= 0)
            {
                return (b, at);
            }
        }
        throw new InvalidOperationException($"{f.Name}: a suspension marker went missing");
    }

    /// <summary>Runtime.CardMarkObject: every card of an object, from its payload address.</summary>
    public const string CardMarkObject = Corsac.Lang.Lto.RuntimeAbi.CardMarkObject;

    /// <summary>
    /// Whether each register points into the machine: 1 always (the machine,
    /// or the machine plus or minus a number, however copied), 2 on some
    /// path and not on another, absent never. A difference of two addresses
    /// in it is a number.
    /// </summary>
    private static Dictionary<int, int> IntoMachine(Function f, VReg machine)
    {
        Dictionary<int, int> kind = new() { [machine.Id] = 1 };
        int Of(Operand o) => o is RegOperand { Reg: var r } && kind.TryGetValue(r.Id, out int k) ? k : 0;
        int Classify(Instr i)
        {
            switch (i.Op)
            {
                case Opcode.Copy:
                case Opcode.Trunc64:
                case Opcode.ZExt32:
                case Opcode.SExt32:
                    return i.Operands.Count == 1 ? Of(i.Operands[0]) : 0;
                case Opcode.Add:
                {
                    if (i.Operands.Count != 2) return 0;
                    int a = Of(i.Operands[0]), b = Of(i.Operands[1]);
                    if (a == 0 && b == 0) return 0;
                    return (a == 1 && b == 0) || (a == 0 && b == 1) ? 1 : 2;
                }
                case Opcode.Sub:
                {
                    if (i.Operands.Count != 2) return 0;
                    int a = Of(i.Operands[0]), b = Of(i.Operands[1]);
                    if (a == 0 && b == 0) return 0;
                    if (a == 1 && b == 0) return 1;
                    if (a == 1 && b == 1) return 0;
                    return 2;
                }
                case Opcode.Phi:
                {
                    bool any = false, all = true;
                    foreach (Operand o in i.Operands)
                    {
                        int k = Of(o);
                        if (k != 0) any = true;
                        if (k != 1) all = false;
                    }
                    return !any ? 0 : all ? 1 : 2;
                }
                default:
                    return 0;
            }
        }
        for (int round = 0; round < 64; round++)
        {
            Dictionary<int, int> next = new() { [machine.Id] = 1 };
            HashSet<int> defined = new();
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not VReg d || d.Id == machine.Id)
                    {
                        continue;
                    }
                    int k = Classify(i);
                    if (defined.Add(d.Id))
                    {
                        if (k != 0) next[d.Id] = k;
                        continue;
                    }
                    int had = next.TryGetValue(d.Id, out int known) ? known : 0;
                    int both = had == k ? k : 2;
                    if (both == 0) next.Remove(d.Id);
                    else next[d.Id] = both;
                }
            }
            bool same = next.Count == kind.Count;
            if (same)
            {
                foreach (KeyValuePair<int, int> e in next)
                {
                    if (!kind.TryGetValue(e.Key, out int was) || was != e.Value) { same = false; break; }
                }
            }
            kind = next;
            if (same)
            {
                break;
            }
        }
        return kind;
    }

    private static int Transform(Function f, AsyncFrame frame, int wordSize, string? cards, out bool stays)
    {
        stays = true;
        frame.Lowered = true;
        VReg machine = frame.StateMachine;
        IrType word = wordSize == 8 ? IrType.I64 : IrType.I32;
        int next = Align(frame.FieldsStart, 8);

        // Landing pads first: a pad is reached by an unwind, an edge no
        // liveness can see, so a register only a pad reads is not live at any
        // suspension and would not be saved. Homing those registers here --
        // before frame memory becomes machine fields -- puts each home in the
        // machine, where it survives the frames this method runs in.
        // machine itself is left out: a pad reloads it from the frame, since
        // reading a field of the machine needs the register the pad has lost.
        LandingPadHomes.Place(f, r => !ReferenceEquals(r, machine));

        // ---- frame memory moves into the machine ---------------------------------
        Dictionary<FrameSlot, int> slotField = new();
        foreach (FrameSlot s in f.Slots)
        {
            slotField[s] = next;
            next = Align(next + s.Bytes, 8);
        }

        HashSet<VReg> fieldAddrs = new();
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    if (i.Operands[o] is not SlotOperand slot)
                    {
                        continue;
                    }
                    VReg addr = f.NewReg(word, "field");
                    b.Instrs.Insert(k, new Instr
                    {
                        Op = Opcode.Add, Dest = addr, Line = i.Line,
                        Operands = { RegOperand.Of(machine), new ImmOperand(slotField[slot.Slot], word) },
                    });
                    k++;
                    i.Operands[o] = RegOperand.Of(addr);
                    fieldAddrs.Add(addr);
                }
            }
        }

        // A WRITE INTO A SLOT IS A WRITE INTO THE MACHINE, which is the heap's
        // and may be old: a store, a copy or a fill through a slot's address --
        // or through a register copied or moved from one -- marks the
        // machine's cards, as a store into any object does. Written for the
        // stack -- an owned pointer's slot, an object promoted to one, a struct
        // copied in -- none carried a mark, and an iterator's machine kept a
        // young object no minor cycle could see, freed under it.
        if (cards is not null && fieldAddrs.Count > 0)
        {
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                        if (i.Dest is { } d && !fieldAddrs.Contains(d)
                            && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Add or Opcode.Sub or Opcode.Phi
                            && i.Operands.Any(x => x is RegOperand { Reg: var r } && fieldAddrs.Contains(r)))
                        { fieldAddrs.Add(d); grew = true; }
            }
            // One mark covers every write before it back to the last thing
            // that can collect -- a call, a trap, the block's end: the mark
            // names the whole machine, and nothing between the writes and it
            // can make a cycle. An exception record's four words take one.
            foreach (Block b in f.Blocks)
            {
                bool pending = false;
                for (int k = 0; k <= b.Instrs.Count; k++)
                {
                    Instr? i = k < b.Instrs.Count ? b.Instrs[k] : null;
                    if (pending && (i is null || i.IsTerminator
                        || i.Op is Opcode.Call or Opcode.CallIndirect or Opcode.Syscall or Opcode.Trap or Opcode.Pause or Opcode.Unwind or Opcode.Unreachable))
                    {
                        List<Instr> mark = new();
                        VReg at = Escape.Word(f, mark, machine, i?.Line ?? b.Instrs[^1].Line, "cardp");
                        mark.Add(new Instr { Op = Opcode.Call, Callee = cards, Operands = { RegOperand.Of(at) }, Line = i?.Line ?? b.Instrs[^1].Line });
                        b.Instrs.InsertRange(k, mark);
                        k += mark.Count;
                        pending = false;
                    }
                    // A CALL HANDED AN ADDRESS IN THE MACHINE -- a struct's
                    // result buffer, an out argument -- may store a reference
                    // there with no mark: the machine's cards are marked
                    // after it, before the next thing that can collect.
                    if (i is not null && i.Op is Opcode.Call or Opcode.CallIndirect && i.Callee != cards
                        && i.Operands.Any(x => x is RegOperand { Reg: var handed } && fieldAddrs.Contains(handed)))
                    {
                        pending = true;
                        continue;
                    }
                    if (i is null
                        || i.Op is not (Opcode.Store or Opcode.MemCopy or Opcode.MemSet or Opcode.AtomicCas or Opcode.AtomicAdd or Opcode.AtomicAnd or Opcode.AtomicOr or Opcode.AtomicXor)
                        || i.Operands.Count == 0 || i.Operands[0] is not RegOperand { Reg: var target } || !fieldAddrs.Contains(target)
                        || i.Op == Opcode.Store && (i.Size < wordSize || i.Operands.Count > 1 && i.Operands[1] is not RegOperand)) continue;
                    pending = true;
                }
            }
        }
        f.Slots.Clear();

        // What points into a machine that may move: kept as offsets from it.
        Dictionary<int, int> into = frame.MayMove ? IntoMachine(f, machine) : new Dictionary<int, int>();
        if (frame.MayMove)
        {
            // AN ADDRESS IN THE MACHINE STORED INTO THE MACHINE -- a register a
            // landing pad reads, homed in a field (LandingPadHomes), and the
            // like -- would be copied as it is, pointing into the old one. Only
            // a heap machine, which never moves, may hold one.
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Store && i.Operands.Count > 1
                        && i.Operands[0] is RegOperand { Reg: var to } && into.ContainsKey(to.Id)
                        && i.Operands[1] is RegOperand { Reg: var stored } && into.ContainsKey(stored.Id))
                    {
                        stays = false;
                    }
                }
            }
        }

        // ---- find every suspension, with what lives across it -----------------------
        Liveness liveness = new(f);
        Dictionary<int, VReg> registers = new();
        foreach (VReg p in f.Params)
        {
            registers[p.Id] = p;
        }
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null)
                {
                    registers[i.Dest.Id] = i.Dest;
                }
                foreach (Operand uOperand in (i).Operands) if (uOperand is RegOperand { Reg: var u })
                {
                    registers[u.Id] = u;
                }
            }
        }
        // Markers pair by their index. Inlining may have put the continuation
        // registration between them into blocks of its own, so the suspend and
        // the resume need not share a block; every path from one reaches the
        // other, because that is how the inliner joins a call back up.
        Dictionary<long, (Block Block, Instr Instr)> suspends = new();
        Dictionary<long, (Block Block, Instr Instr, List<VReg> Live)> resumes = new();

        foreach (Block b in f.Blocks)
        {
            foreach ((Instr i, ulong[] liveAfter) in liveness.WalkBackwards(b))
            {
                if (i.Op != Opcode.Call || i.Operands.Count != 1 || i.Operands[0] is not ImmOperand index)
                {
                    continue;
                }
                if (i.Callee == AsyncFrame.Suspend)
                {
                    suspends[index.Value] = (b, i);
                }
                else if (i.Callee == AsyncFrame.Resume)
                {
                    List<VReg> live = new();
                    foreach (VReg r in registers.Values.OrderBy(x => x.Id))
                    {
                        if (Liveness.Test(liveAfter, r.Id) && !ReferenceEquals(r, machine))
                        {
                            live.Add(r);
                        }
                    }
                    resumes[index.Value] = (b, i, live);
                }
            }
        }

        // One field per register, shared by every suspension that saves it.
        Dictionary<VReg, int> regField = new();
        foreach ((_, _, List<VReg> live) in resumes.Values)
        {
            foreach (VReg r in live)
            {
                if (!regField.ContainsKey(r))
                {
                    regField[r] = next;
                    next += 8;
                }
            }
        }

        // ---- split each suspension into a save and a resumption ------------------
        Block originalEntry = f.Entry;
        List<Block> resumptions = new();
        int state = 0;

        foreach ((long index, (Block _, Instr suspend)) in suspends.OrderBy(p => p.Key).ToList())
        {
            if (!resumes.TryGetValue(index, out (Block Block, Instr Instr, List<VReg> Live) resume))
            {
                throw new InvalidOperationException($"{f.Name}: suspension {index} has no resumption");
            }
            state++;

            // At the suspend: save what the resumption will need and record
            // which resumption it is. The registration that follows runs last,
            // once everything the continuation needs is in the machine -- on a
            // thread pool it may run at once.
            // Found afresh: splitting an earlier resumption may have moved
            // this marker into that resumption's new block.
            (Block sb, int s) = Locate(f, suspend);
            List<Instr> saves = new();
            foreach (VReg v in resume.Live)
            {
                int pointsInto = into.TryGetValue(v.Id, out int k) ? k : 0;
                if (pointsInto == 2)
                {
                    stays = false;
                }
                Operand kept = RegOperand.Of(v);
                if (pointsInto == 1)
                {
                    // An address in the machine, kept as where in it.
                    VReg offset = f.NewReg(v.Type, "offset");
                    saves.Add(new Instr { Op = Opcode.Sub, Dest = offset, Operands = { RegOperand.Of(v), RegOperand.Of(machine) } });
                    kept = RegOperand.Of(offset);
                }
                saves.Add(new Instr
                {
                    Op = Opcode.Store, Size = v.Type.Bytes(), Offset = regField[v],
                    Operands = { RegOperand.Of(machine), kept },
                });
            }
            if (cards is not null && resume.Live.Count > 0)
            {
                // The runtime takes a machine word, which the machine's address is.
                VReg at = Escape.Word(f, saves, machine, 0, "cardp");
                saves.Add(new Instr { Op = Opcode.Call, Callee = cards, Operands = { RegOperand.Of(at) } });
            }
            saves.Add(new Instr
            {
                Op = Opcode.Store, Size = 4, Offset = frame.StateOffset,
                Operands = { RegOperand.Of(machine), new ImmOperand(state, IrType.I32) },
            });
            sb.Instrs.RemoveAt(s);
            sb.Instrs.InsertRange(s, saves);

            // At the resume: this invocation ends, and a new block continues.
            (Block rb, int r) = Locate(f, resume.Instr);
            List<Instr> after = rb.Instrs.GetRange(r + 1, rb.Instrs.Count - r - 1);
            rb.Instrs.RemoveRange(r, rb.Instrs.Count - r);
            Instr leave = new() { Op = Opcode.Ret };
            if (frame.SuspendResult is Operand produced) leave.Operands.Add(produced);
            rb.Instrs.Add(leave);

            Block resumeBlock = f.NewBlock($"resume{state}_");
            foreach (VReg v in resume.Live)
            {
                if (into.TryGetValue(v.Id, out int k) && k == 1)
                {
                    // Where in the machine, made an address in this one.
                    VReg offset = f.NewReg(v.Type, "offset");
                    resumeBlock.Instrs.Add(new Instr
                    {
                        Op = Opcode.Load, Dest = offset, Size = v.Type.Bytes(), Offset = regField[v], Signed = true,
                        Operands = { RegOperand.Of(machine) },
                    });
                    resumeBlock.Instrs.Add(new Instr { Op = Opcode.Add, Dest = v, Operands = { RegOperand.Of(offset), RegOperand.Of(machine) } });
                    continue;
                }
                resumeBlock.Instrs.Add(new Instr
                {
                    Op = Opcode.Load, Dest = v, Size = v.Type.Bytes(), Offset = regField[v], Signed = true,
                    Operands = { RegOperand.Of(machine) },
                });
            }
            resumeBlock.Instrs.AddRange(after);
            resumptions.Add(resumeBlock);
        }

        // ---- the dispatch ------------------------------------------------------------------
        if (resumptions.Count > 0)
        {
            Block dispatch = f.NewBlock("dispatch");
            f.Blocks.Remove(dispatch);
            f.Blocks.Insert(0, dispatch);

            VReg current = f.NewReg(IrType.I32, "state");
            dispatch.Instrs.Add(new Instr
            {
                Op = Opcode.Load, Dest = current, Size = 4, Offset = frame.StateOffset, Signed = true,
                // The state is a number, never an address (Instr.Number).
                Operands = { RegOperand.Of(machine) }, Number = true,
            });
            Instr sw = new() { Op = Opcode.Switch, Operands = { RegOperand.Of(current) }, Default = originalEntry };
            sw.WritableTargets.Add(originalEntry);
            sw.WritableTargets.AddRange(resumptions);
            dispatch.Instrs.Add(sw);
        }

        return Align(next, 8);
    }
}
