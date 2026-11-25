using Corsac.Lang.Frames;
using Corsac.Lang.Ir;

namespace Corsac.Tests.Metadata;

/// <summary>
/// The stack maps' shared parts, by hand-made inputs: the table's codec in
/// each target's layout (i386's bytes as they have always been; x86-64's
/// and any other's in the general version), and the frame slots' liveness
/// over a view of code no machine made (FrameCode) -- what a call's map
/// names, and what must be zeroed at entry, for the shapes the rules are
/// about: a word written after a call, a slot whose address is taken, a
/// handler's reads, a store after a branch, an access through an index.
/// </summary>
public static class StackMapTests
{
    public static void Run(string work)
    {
        Codec(StackMapLayout.I386, 36, StackMapTable.Version);
        Codec(StackMapLayout.X64, 56, StackMapTable.GeneralVersion);
        Codec(new StackMapLayout(8, new[] { 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 }), 56, StackMapTable.GeneralVersion);
        Liveness();
        Console.WriteLine("stack maps: the table round-trips in i386's, x86-64's and a ten-register layout; frame slots live, exposed, handler-read and zeroed as the rules say");
    }

    private static void Codec(StackMapLayout layout, int header, uint version)
    {
        uint Reg(int k) => 1u << layout.Registers[k];
        List<StackMapTable.Function> functions = new()
        {
            new()
            {
                Start = 0, FrameSize = 64, Saved = Reg(0) | Reg(layout.Registers.Length - 1), Objects = new[] { 0, 1 },
                Sites =
                {
                    new(16, false, Reg(0), new[] { 2, 5 }),
                    new(40, true, 0, Array.Empty<int>()),
                    // Past sixteen words: a pooled bitmap, and one that differs from it by a word.
                    new(72, false, Reg(layout.Registers.Length - 1), Enumerable.Range(0, 40).ToArray()),
                    new(96, false, 0, Enumerable.Range(0, 40).Where(w => w != 7).Append(45).ToArray()),
                },
            },
            new() { Start = 128, FrameSize = 0, Saved = 0, Objects = Array.Empty<int>(), Sites = { new(8, false, 0, Array.Empty<int>()) } },
        };
        byte[] bytes = StackMapTable.Encode(functions, layout);
        uint written = (uint)(bytes[4] | bytes[5] << 8 | bytes[6] << 16 | bytes[7] << 24);
        if (written != version) throw new Exception("stack maps: " + Describe(layout) + " written as version " + written);
        List<StackMapTable.Function> read = StackMapTable.Decode(bytes, out StackMapLayout decoded)
            ?? throw new Exception("stack maps: " + Describe(layout) + " does not decode");
        if (decoded.WordBytes != layout.WordBytes || !decoded.Registers.SequenceEqual(layout.Registers) || decoded.HeaderBytes != header)
            throw new Exception("stack maps: " + Describe(layout) + " decoded as " + Describe(decoded));
        if (read.Count != functions.Count) throw new Exception("stack maps: " + Describe(layout) + " lost a function");
        for (int f = 0; f < functions.Count; f++)
        {
            StackMapTable.Function a = functions[f], b = read[f];
            if (a.Start != b.Start || a.FrameSize != b.FrameSize || a.Saved != b.Saved || !a.Objects.SequenceEqual(b.Objects) || a.Sites.Count != b.Sites.Count)
                throw new Exception("stack maps: " + Describe(layout) + " function " + f + " changed in the table");
            for (int s = 0; s < a.Sites.Count; s++)
                if (a.Sites[s].Return != b.Sites[s].Return || a.Sites[s].NoMap != b.Sites[s].NoMap || a.Sites[s].Live != b.Sites[s].Live
                    || !a.Sites[s].Slots.SequenceEqual(b.Sites[s].Slots))
                    throw new Exception("stack maps: " + Describe(layout) + " function " + f + " site " + s + " changed in the table");
        }
        // Written again from what was read: the same bytes (the duplicate cutter's round).
        if (!StackMapTable.Encode(read, decoded).AsSpan().SequenceEqual(bytes)) throw new Exception("stack maps: " + Describe(layout) + " is not written back as it was read");
        // A register the layout does not name is refused, never dropped.
        int stranger = Enumerable.Range(0, 32).First(r => !layout.Registers.Contains(r));
        try
        {
            StackMapTable.Encode(new[] { new StackMapTable.Function { Saved = 1u << stranger, Sites = { new(4, false, 0, Array.Empty<int>()) } } }, layout);
            throw new Exception("stack maps: " + Describe(layout) + " wrote a register it does not name");
        }
        catch (InvalidOperationException) { }
    }

    private static string Describe(StackMapLayout layout) => layout.WordBytes + "-byte words, registers " + string.Join(",", layout.Registers);

    private static void Liveness()
    {
        foreach (int word in new[] { 4, 8 })
        {
            object call1 = new(), call2 = new(), call3 = new();
            // Three one-word slots A, B, C at -w, -2w, -3w.
            FrameCode Fresh(out int entry)
            {
                FrameCode code = new(word, 3 * word);
                code.Slot(-word, word); code.Slot(-2 * word, word); code.Slot(-3 * word, word);
                entry = code.NewBlock(false);
                return code;
            }
            int[] At(FrameSlotLiveness.Result r, object call) => r.At[call].Order().ToArray();
            void Same(int[] got, int[] want, string what)
            {
                if (!got.Order().SequenceEqual(want.Order()))
                    throw new Exception("frame slots (" + word + "-byte): " + what + ": [" + string.Join(",", got) + "], expected [" + string.Join(",", want) + "]");
            }

            // 1. A written before the call and read after: named, not zeroed. B written only after the call: not named.
            {
                FrameCode code = Fresh(out int b);
                code.Instr(b, null); code.Does(b, FrameCode.Effect.WholeStore, -word);
                code.Instr(b, call1);
                code.Instr(b, null); code.Does(b, FrameCode.Effect.WholeStore, -2 * word);
                code.Instr(b, null); code.Does(b, FrameCode.Effect.ReadWord, -word);
                code.Instr(b, null); code.Does(b, FrameCode.Effect.ReadWord, -2 * word);
                FrameSlotLiveness.Result r = FrameSlotLiveness.Run(code, out _) ?? throw new Exception("frame slots: a straight line left whole");
                Same(At(r, call1), new[] { -word }, "live across a call");
                Same(r.Zero, Array.Empty<int>(), "nothing unwritten is named");
            }
            // 2. C's address taken before the call, never written: named from then on, and zeroed.
            {
                FrameCode code = Fresh(out int b);
                code.Instr(b, call1);
                code.Instr(b, null); code.Does(b, FrameCode.Effect.Address, -3 * word);
                code.Instr(b, call2);
                FrameSlotLiveness.Result r = FrameSlotLiveness.Run(code, out _) ?? throw new Exception("frame slots: an address taken left whole");
                // Before its address is taken the slot is still read by the taking: live there too.
                Same(At(r, call1), new[] { -3 * word }, "an address about to be taken");
                Same(At(r, call2), new[] { -3 * word }, "exposed after its address is taken");
                Same(r.Zero, new[] { -3 * word }, "an exposed slot no path wrote is zeroed");
            }
            // 3. A store after a branch does not count on the branch's path: A is read at the join, written on one side only.
            {
                FrameCode code = Fresh(out int b);
                int side = code.NewBlock(false), join = code.NewBlock(false);
                code.Edge(b, side); code.Edge(b, join); code.Edge(side, join);
                code.Instr(side, null); code.Does(side, FrameCode.Effect.WholeStore, -word);
                code.Instr(join, call1);
                code.Instr(join, null); code.Does(join, FrameCode.Effect.ReadWord, -word);
                FrameSlotLiveness.Result r = FrameSlotLiveness.Run(code, out _) ?? throw new Exception("frame slots: a diamond left whole");
                Same(At(r, call1), new[] { -word }, "read after a join");
                Same(r.Zero, new[] { -word }, "written on one path only is zeroed");
            }
            // 4. What a handler reads is live at every call, and a store does not end it.
            {
                FrameCode code = Fresh(out int b);
                int pad = code.NewBlock(true);
                code.Instr(b, call1);
                code.Instr(b, null); code.Does(b, FrameCode.Effect.WholeStore, -2 * word);
                code.Instr(b, call2);
                code.Instr(pad, null); code.Does(pad, FrameCode.Effect.ReadWord, -2 * word);
                FrameSlotLiveness.Result r = FrameSlotLiveness.Run(code, out _) ?? throw new Exception("frame slots: a handler left whole");
                Same(At(r, call1), new[] { -2 * word }, "a handler's read before the store");
                Same(At(r, call2), new[] { -2 * word }, "a handler's read after the store");
                Same(r.Zero, new[] { -2 * word }, "a handler's read may come before any store");
            }
            // 5. The base escaping exposes every slot from the start; an address in no slot is the same.
            foreach (bool byFlag in new[] { true, false })
            {
                FrameCode code = Fresh(out int b);
                if (byFlag) code.BaseEscapes = true;
                code.Instr(b, call1);
                if (!byFlag) { code.Instr(b, null); code.Does(b, FrameCode.Effect.Address, 2 * word); }
                code.Instr(b, call3);
                FrameSlotLiveness.Result r = FrameSlotLiveness.Run(code, out _) ?? throw new Exception("frame slots: an escaped base left whole");
                Same(At(r, call1), new[] { -word, -2 * word, -3 * word }, "every slot once the base escapes");
                Same(r.Zero, new[] { -word, -2 * word, -3 * word }, "every unwritten slot zeroed once the base escapes");
            }
            // 6. What cannot be read is left whole: opaque code, and an entry block something jumps back to.
            {
                FrameCode code = Fresh(out int b);
                code.Opaque = true;
                code.Instr(b, call1);
                if (FrameSlotLiveness.Run(code, out int why) is not null || why != FrameSlotLiveness.LeftOpaque) throw new Exception("frame slots: opaque code was read");
                code = Fresh(out b);
                code.Edge(b, b);
                code.Instr(b, call1);
                if (FrameSlotLiveness.Run(code, out why) is not null || why != FrameSlotLiveness.LeftEntry) throw new Exception("frame slots: an entry jumped back to was read");
            }
            // 7. A function with no IR slot says so, and names nothing.
            {
                FrameCode code = new(word, 2 * word);
                int b = code.NewBlock(false);
                code.Instr(b, call1);
                FrameSlotLiveness.Result r = FrameSlotLiveness.Run(code, out _) ?? throw new Exception("frame slots: no slots left whole");
                if (!r.NoSlots || r.At.Count != 0) throw new Exception("frame slots: a function without slots names some");
            }
        }
    }
}
