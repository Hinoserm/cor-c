#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Values a catch or finally reads must be somewhere an unwind can find them.
///
/// An unwind restores the stack and frame pointers from the handler record
/// and jumps to the landing pad with the exception in the result register;
/// every other register holds whatever the thrower left in it. A register
/// the pad reads -- a local the catch prints, the state machine an async
/// method's catch-all completes -- is therefore gone unless it lives in the
/// frame. This gives each such register a frame home, stores it there after
/// every definition, and reloads it at the top of each pad that reads it.
///
/// For an ordinary function it runs last, after the async transform. An async
/// method is homed in two halves. A landing pad has no edge into it that
/// liveness can see, so nothing tells the async transform that a value only a
/// pad reads has to survive a suspension; the transform therefore homes those
/// values itself, BEFORE it moves frame memory into the state machine, so each
/// home outlives every frame the method runs in and a pad entered after a
/// resumption reads what was stored before the suspension. The state machine
/// register is the exception: its home cannot be a field of the machine, since
/// reading that field needs the register the pad has lost. It is homed here,
/// afterwards, in the frame -- written on entry to every invocation, so it is
/// the running frame's copy that a pad reloads, and reloaded first, before the
/// homes that are addressed off it.
/// </summary>
public static class LandingPadHomes
{
    public static void Run(Module m)
    {
        foreach (Function f in m.Functions)
        {
            if (!f.Blocks.Any(b => b.IsLandingPad))
            {
                continue;
            }
            if (f.Async is AsyncFrame frame)
            {
                // Everything else was homed by the async transform; homing it
                // again would reload from frame slots nothing has written.
                Place(f, r => ReferenceEquals(r, frame.StateMachine));
            }
            else
            {
                Place(f, _ => true);
            }
        }
    }

    internal static void Place(Function f, Func<VReg, bool> wanted)
    {
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

        // What each pad needs, apart from the exception it is handed.
        Dictionary<Block, List<VReg>> needs = new();
        HashSet<VReg> homed = new();
        foreach (Block pad in f.Blocks.Where(b => b.IsLandingPad))
        {
            VReg? exception = pad.Instrs.FirstOrDefault(i => i.Op == Opcode.Call && i.Callee == "__exception")?.Dest;
            List<VReg> list = new();
            foreach (VReg r in liveness.LiveIn(pad))
            {
                if (!ReferenceEquals(r, exception) && wanted(r))
                {
                    list.Add(r);
                    homed.Add(r);
                }
            }
            needs[pad] = list;
        }
        if (homed.Count == 0)
        {
            return;
        }

        Dictionary<VReg, FrameSlot> home = new();
        foreach (VReg r in homed.OrderBy(x => x.Id))
        {
            home[r] = f.NewSlot(8, 8, $"home{r.Id}");
        }

        // After every definition: parameters on entry, everything else where
        // it is written.
        List<Instr> onEntry = new();
        foreach (VReg p in f.Params)
        {
            if (home.TryGetValue(p, out FrameSlot? slot))
            {
                onEntry.Add(StoreTo(slot, p));
            }
        }

        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Dest is not null && home.TryGetValue(i.Dest, out FrameSlot? slot) && !i.IsTerminator)
                {
                    b.Instrs.Insert(k + 1, StoreTo(slot, i.Dest));
                    k++;
                }
            }
        }

        // At each pad, after the exception is taken.
        foreach ((Block pad, List<VReg> list) in needs)
        {
            int at = pad.Instrs.FindIndex(i => i.Op == Opcode.Call && i.Callee == "__exception");
            List<Instr> reloads = list.Select(r => new Instr
            {
                Op = Opcode.Load, Dest = r, Size = r.Type.Bytes(), Signed = true,
                Operands = { new SlotOperand(home[r]) },
            }).ToList();
            pad.Instrs.InsertRange(at + 1, reloads);
        }

        f.Entry.Instrs.InsertRange(0, onEntry);
    }

    private static Instr StoreTo(FrameSlot slot, VReg r) => new()
    {
        Op = Opcode.Store, Size = r.Type.Bytes(),
        Operands = { new SlotOperand(slot), new RegOperand(r) },
    };
}
