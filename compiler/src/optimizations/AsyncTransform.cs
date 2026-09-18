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
/// </summary>
public static class AsyncTransform
{
    public static void Run(Module m, int wordSize)
    {
        foreach (Function f in m.Functions)
        {
            if (f.Async is AsyncFrame frame)
            {
                int size = Transform(f, frame, wordSize);
                DataItem? item = m.Data.FirstOrDefault(d => d.Name == frame.SizeSymbol);
                if (item is not null)
                {
                    for (int i = 0; i < wordSize; i++)
                    {
                        item.Bytes[i] = (byte)(size >> (8 * i));
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

    private static int Transform(Function f, AsyncFrame frame, int wordSize)
    {
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
                        Operands = { new RegOperand(machine), new ImmOperand(slotField[slot.Slot], word) },
                    });
                    k++;
                    i.Operands[o] = new RegOperand(addr);
                }
            }
        }
        f.Slots.Clear();

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
                foreach (VReg u in IrInfo.Uses(i))
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
                saves.Add(new Instr
                {
                    Op = Opcode.Store, Size = v.Type.Bytes(), Offset = regField[v],
                    Operands = { new RegOperand(machine), new RegOperand(v) },
                });
            }
            saves.Add(new Instr
            {
                Op = Opcode.Store, Size = 4, Offset = frame.StateOffset,
                Operands = { new RegOperand(machine), new ImmOperand(state, IrType.I32) },
            });
            sb.Instrs.RemoveAt(s);
            sb.Instrs.InsertRange(s, saves);

            // At the resume: this invocation ends, and a new block continues.
            (Block rb, int r) = Locate(f, resume.Instr);
            List<Instr> after = rb.Instrs.GetRange(r + 1, rb.Instrs.Count - r - 1);
            rb.Instrs.RemoveRange(r, rb.Instrs.Count - r);
            rb.Instrs.Add(new Instr { Op = Opcode.Ret });

            Block resumeBlock = f.NewBlock($"resume{state}_");
            foreach (VReg v in resume.Live)
            {
                resumeBlock.Instrs.Add(new Instr
                {
                    Op = Opcode.Load, Dest = v, Size = v.Type.Bytes(), Offset = regField[v], Signed = true,
                    Operands = { new RegOperand(machine) },
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
                Operands = { new RegOperand(machine) },
            });
            Instr sw = new() { Op = Opcode.Switch, Operands = { new RegOperand(current) }, Default = originalEntry };
            sw.Targets.Add(originalEntry);
            sw.Targets.AddRange(resumptions);
            dispatch.Instrs.Add(sw);
        }

        return Align(next, 8);
    }
}
