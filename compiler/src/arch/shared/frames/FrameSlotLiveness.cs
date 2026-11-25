#nullable enable
namespace Corsac.Lang.Frames;

/// <summary>
/// A FUNCTION'S FINAL CODE AS THE FRAME ANALYSES SEE IT, whatever machine it
/// is for: its blocks and where each can go, and for each instruction what
/// it does to the words of the frame's IR slots -- nothing about what the
/// instruction is. A target fills one from its own machine code
/// (X86FrameCode, X64FrameCode) and the analyses here never see a register
/// or an opcode: another machine brings its own filler and nothing else.
///
/// The frame is the target's as the stack maps describe it (StackMapLayout):
/// locals below a base register, word b the word at base - WordBytes(b + 1).
/// Offsets given here are byte offsets from that base, negative.
/// </summary>
public sealed class FrameCode
{
    /// <summary>What one instruction does to a frame slot's words.</summary>
    public enum Effect : byte
    {
        /// <summary>A whole store of one word: what was there is gone, unread.</summary>
        WholeStore,
        /// <summary>A read of one word (a word's own load).</summary>
        ReadWord,
        /// <summary>A read of the slot the word is in, whole: any access whose width or reach is not one word's.</summary>
        ReadSlot,
        /// <summary>The address of the slot the word is in, taken: the slot is read (what holds the address may read it) and exposed from here on.</summary>
        Address,
    }

    public int WordBytes { get; }
    public int FrameBytes { get; }
    internal readonly List<(int Offset, int Bytes)> Slots = new();
    internal readonly List<Block> Blocks = new();
    /// <summary>The frame's base escapes as a value somewhere (a handler's record of its frame, an intrinsic): every slot is exposed from the start.</summary>
    public bool BaseEscapes { get; set; }
    /// <summary>Something this view cannot describe (inline assembly, the base register used as an index): the function is left as it was.</summary>
    public bool Opaque { get; set; }

    internal sealed class Block
    {
        public bool LandingPad;
        public readonly List<int> Successors = new();
        // Instruction k's effects are Effects[First[k] .. First[k + 1]).
        public readonly List<int> First = new();
        public readonly List<(Effect Kind, int Offset)> Effects = new();
        public readonly List<object?> Calls = new();
    }

    public FrameCode(int wordBytes, int frameBytes)
    {
        WordBytes = wordBytes;
        FrameBytes = frameBytes;
    }

    /// <summary>An IR frame slot: its lowest byte's offset from the base (negative) and its size.</summary>
    public void Slot(int offset, int bytes) => Slots.Add((offset, bytes));

    /// <summary>The next block, in the function's order; the first is its entry.</summary>
    public int NewBlock(bool landingPad)
    {
        Blocks.Add(new Block { LandingPad = landingPad });
        return Blocks.Count - 1;
    }

    /// <summary>Control can pass from one block to another.</summary>
    public void Edge(int from, int to) => Blocks[from].Successors.Add(to);

    /// <summary>The next instruction of a block; `call` is its identity when the collector may run under it (a call, a safepoint poll), null otherwise.</summary>
    public void Instr(int block, object? call)
    {
        Block b = Blocks[block];
        b.First.Add(b.Effects.Count);
        b.Calls.Add(call);
    }

    /// <summary>What the instruction just begun does to the frame word at `offset` (or to that word's slot).</summary>
    public void Does(int block, Effect kind, int offset) => Blocks[block].Effects.Add((kind, offset));
}

/// <summary>
/// WHICH WORDS OF A FUNCTION'S IR FRAME SLOTS THE COLLECTOR READS AT EACH
/// CALL, instead of all of them at every one -- for every target, over a
/// FrameCode.
///
/// A spill slot was always in a call's map only where its value is live.
/// The IR frame slots -- the objects the lifetime passes put in the frame,
/// structs, the lowering's temporaries -- were one list a function, read
/// "as they are" at every call: written yet or not, read again or not. A
/// frame laid over what a deep, finished call left on the stack then kept,
/// through a slot it had not reached, whatever the old word pointed at --
/// the compiler's driver held a unit's whole front end, most of a gigabyte,
/// through its code generation and into the next unit, where .NET, which
/// reads no dead slot, held a fifth of it.
///
/// How the others do it, and what is taken from each:
/// - OCaml's frame tables and HotSpot C2's oop maps list only what is live
///   at the return address: a liveness pass over the slots, as here.
/// - Go's compiler (plive) does the same for a variable never address-taken,
///   and zeroed at entry one that is "ambiguously live" -- live at a call on
///   some path that has not written it; the zeroing here is that rule, and
///   only for such words.
/// - RyuJIT reports a slot it cannot track (address-exposed, a struct's
///   field) for the whole method and zeroes it in the prolog; here a slot
///   whose address is taken is reported from where it may have been taken
///   on, not before, and zeroed at entry only if some call may then find it
///   unwritten.
///
/// THE RULES:
///
///   A WORD IS LIVE at a point when some path from it reads the word before
///   a whole store of it (FrameCode.Effect: WholeStore, ReadWord, ReadSlot;
///   taking a slot's address reads it).
///
///   A SLOT IS EXPOSED from where its address may have been taken to the
///   function's end: what holds the address may read or write it at any
///   call after. Where the frame's base escapes as a value, every slot is
///   exposed from the start.
///
///   A CALL'S MAP names the words live after it and the words of the slots
///   exposed before it.
///
///   A FAULT MAY ENTER A HANDLER from anywhere, so what a handler reads is
///   live everywhere in a function that has one, and its slots' exposure is
///   taken for the whole function.
///
///   A WORD A CALL'S MAP NAMES THAT SOME PATH TO THE CALL HAS NOT WHOLLY
///   WRITTEN is to be zeroed where the function begins (Result.Zero), so
///   the collector never reads what an earlier call left there. The target
///   writes the stores, after its prologue.
///
/// What cannot be read this way leaves the function as it was -- every slot
/// word at every call -- and Run answers null: an opaque code, slots that
/// overlap, an entry block something jumps back to (the zeroing would run
/// again), a liveness that does not settle.
/// </summary>
public static class FrameSlotLiveness
{
    public sealed class Result
    {
        /// <summary>Each call (by the identity its FrameCode gave): the slot words its map names, as byte offsets from the frame's base.</summary>
        public readonly Dictionary<object, int[]> At = new(ReferenceEqualityComparer.Instance);
        /// <summary>The words to zero where the function begins, as byte offsets from the frame's base, ascending by word.</summary>
        public int[] Zero = Array.Empty<int>();
    }

    /// <summary>Why a function was left as it was (Run's `why`): for a target's report.</summary>
    public const int Analysed = 0, LeftOpaque = 1, LeftOverlap = 2, LeftEntry = 3, LeftUnsettled = 4, Reasons = 5;

    public static Result? Run(FrameCode code, out int why)
    {
        why = Analysed;
        if (code.Opaque) { why = LeftOpaque; return null; }
        int word = code.WordBytes;
        int words = (code.FrameBytes + word - 1) / word;
        int nb = code.Blocks.Count;
        if (words == 0 || nb == 0 || code.Slots.Count == 0) return new Result();
        // The IR slots' words, each with its slot: word b is the one at base - word(b + 1).
        int[] slotOf = new int[words];
        Array.Fill(slotOf, -1);
        List<(int First, int Count)> slots = new();
        foreach ((int offset, int bytes) in code.Slots)
        {
            if (offset >= 0) continue;
            int low = offset & ~(word - 1), high = offset + bytes;
            int first = -1, count = 0;
            for (int at = low; at < high && at < 0; at += word)
            {
                int b = -at / word - 1;
                if (b < 0 || b >= words || slotOf[b] >= 0) { why = LeftOverlap; return null; }
                slotOf[b] = slots.Count;
                if (first < 0 || b < first) first = b;
                count++;
            }
            if (count > 0) slots.Add((first, count));
        }
        if (slots.Count == 0) return new Result();
        List<int>[] predecessors = new List<int>[nb];
        for (int b = 0; b < nb; b++) predecessors[b] = new();
        for (int b = 0; b < nb; b++)
            foreach (int to in code.Blocks[b].Successors) predecessors[to].Add(b);
        // Zeroing goes where the function begins, once: not into a block something jumps back to.
        if (predecessors[0].Count > 0 || code.Blocks[0].LandingPad) { why = LeftEntry; return null; }
        bool handlers = false;
        foreach (FrameCode.Block block in code.Blocks) if (block.LandingPad) handlers = true;

        int cells = (words + 63) / 64;
        static void Set(ulong[] set, int b) => set[b >> 6] |= 1UL << (b & 63);
        static void Clear(ulong[] set, int b) => set[b >> 6] &= ~(1UL << (b & 63));
        static bool Has(ulong[] set, int b) => (set[b >> 6] & 1UL << (b & 63)) != 0;
        void SetSlot(ulong[] set, int slot)
        {
            (int first, int count) = slots[slot];
            for (int b = first; b < first + count; b++) Set(set, b);
        }
        // The slot word a byte offset is in; -1 for one outside every IR slot.
        int WordAt(int offset)
        {
            if (offset >= 0) return -1;
            int b = -(offset & ~(word - 1)) / word - 1;
            return b >= 0 && b < words && slotOf[b] >= 0 ? b : -1;
        }

        // BACKWARDS: what is read after each point. `after` is called at
        // every call with what is live once it has run.
        ulong[][] liveIn = new ulong[nb][];
        for (int b = 0; b < nb; b++) liveIn[b] = new ulong[cells];
        ulong[] handlerLive = new ulong[cells];
        ulong[] WalkBack(int b, Action<object, ulong[]>? after)
        {
            FrameCode.Block block = code.Blocks[b];
            ulong[] live = new ulong[cells];
            foreach (int s in block.Successors)
                for (int c = 0; c < cells; c++) live[c] |= liveIn[s][c];
            for (int c = 0; c < cells; c++) live[c] |= handlerLive[c];
            for (int k = block.First.Count - 1; k >= 0; k--)
            {
                if (after is not null && block.Calls[k] is object call) after(call, live);
                int from = block.First[k], to = k + 1 < block.First.Count ? block.First[k + 1] : block.Effects.Count;
                // The stores first: an instruction that reads and wholly
                // stores one word (none does today) reads it before.
                for (int e = from; e < to; e++)
                {
                    (FrameCode.Effect kind, int offset) = block.Effects[e];
                    // (What a handler reads stays live across every store: a fault may come before it.)
                    if (kind == FrameCode.Effect.WholeStore && WordAt(offset) is int stored and >= 0 && !Has(handlerLive, stored)) Clear(live, stored);
                }
                for (int e = from; e < to; e++)
                {
                    (FrameCode.Effect kind, int offset) = block.Effects[e];
                    if (kind == FrameCode.Effect.WholeStore || WordAt(offset) is not (int read and >= 0)) continue;
                    if (kind == FrameCode.Effect.ReadWord) Set(live, read);
                    else SetSlot(live, slotOf[read]);
                }
            }
            return live;
        }
        bool changed = true;
        int rounds = 0;
        while (changed)
        {
            if (++rounds > 1000 + 16 * nb) { why = LeftUnsettled; return null; }
            changed = false;
            for (int b = nb - 1; b >= 0; b--)
            {
                ulong[] now = WalkBack(b, null);
                bool moved = false;
                for (int c = 0; c < cells; c++) if (now[c] != liveIn[b][c]) moved = true;
                if (!moved) continue;
                liveIn[b] = now;
                changed = true;
                if (code.Blocks[b].LandingPad)
                    for (int c = 0; c < cells; c++) handlerLive[c] |= now[c];
            }
        }

        // FORWARDS: the slots whose address may have been taken (any path),
        // and the words wholly written (every path).
        ulong[] everExposed = new ulong[cells];
        if (code.BaseEscapes)
            for (int k = 0; k < slots.Count; k++) SetSlot(everExposed, k);
        foreach (FrameCode.Block block in code.Blocks)
            foreach ((FrameCode.Effect kind, int offset) in block.Effects)
                if (kind == FrameCode.Effect.Address && WordAt(offset) is int taken and >= 0) SetSlot(everExposed, slotOf[taken]);
        ulong[][] exposedIn = new ulong[nb][], writtenIn = new ulong[nb][];
        ulong[] all = new ulong[cells];
        for (int b = 0; b < words; b++) Set(all, b);
        for (int b = 0; b < nb; b++)
        {
            // With a handler anywhere, or the base escaped, exposure is the whole function's; nothing is taken as written inside a handler.
            exposedIn[b] = handlers || code.BaseEscapes ? (ulong[])everExposed.Clone() : new ulong[cells];
            writtenIn[b] = b == 0 || code.Blocks[b].LandingPad ? new ulong[cells] : (ulong[])all.Clone();
        }
        void WalkForward(int b, ulong[] exposed, ulong[] written, Action<object, ulong[], ulong[]>? before)
        {
            FrameCode.Block block = code.Blocks[b];
            for (int k = 0; k < block.First.Count; k++)
            {
                int from = block.First[k], to = k + 1 < block.First.Count ? block.First[k + 1] : block.Effects.Count;
                for (int e = from; e < to; e++)
                    if (block.Effects[e].Kind == FrameCode.Effect.Address && WordAt(block.Effects[e].Offset) is int taken and >= 0) SetSlot(exposed, slotOf[taken]);
                if (before is not null && block.Calls[k] is object call) before(call, exposed, written);
                for (int e = from; e < to; e++)
                    if (block.Effects[e].Kind == FrameCode.Effect.WholeStore && WordAt(block.Effects[e].Offset) is int stored and >= 0) Set(written, stored);
            }
        }
        changed = true;
        rounds = 0;
        while (changed)
        {
            if (++rounds > 1000 + 16 * nb) { why = LeftUnsettled; return null; }
            changed = false;
            for (int b = 0; b < nb; b++)
            {
                ulong[] exposed = (ulong[])exposedIn[b].Clone(), written = (ulong[])writtenIn[b].Clone();
                WalkForward(b, exposed, written, null);
                foreach (int s in code.Blocks[b].Successors)
                {
                    bool pad = code.Blocks[s].LandingPad;
                    for (int c = 0; c < cells; c++)
                    {
                        ulong e = exposedIn[s][c] | exposed[c];
                        if (e != exposedIn[s][c]) { exposedIn[s][c] = e; changed = true; }
                        if (pad) continue;
                        ulong w = writtenIn[s][c] & written[c];
                        if (w != writtenIn[s][c]) { writtenIn[s][c] = w; changed = true; }
                    }
                }
            }
        }

        // EACH CALL: live after it, with what is exposed before it; and of
        // those, what some path has not written.
        Result result = new();
        Dictionary<object, ulong[]> liveAfter = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < nb; b++)
            WalkBack(b, (call, live) => liveAfter[call] = (ulong[])live.Clone());
        ulong[] zero = new ulong[cells];
        for (int b = 0; b < nb; b++)
        {
            ulong[] exposed = (ulong[])exposedIn[b].Clone(), written = (ulong[])writtenIn[b].Clone();
            WalkForward(b, exposed, written, (call, exposedNow, writtenNow) =>
            {
                if (!liveAfter.TryGetValue(call, out ulong[]? live)) return;
                List<int> named = new();
                for (int c = 0; c < cells; c++)
                {
                    ulong read = live[c] | exposedNow[c] | handlerLive[c];
                    zero[c] |= read & ~writtenNow[c];
                    for (int bit = 0; read != 0; bit++, read >>= 1)
                        if ((read & 1) != 0 && c * 64 + bit < words && slotOf[c * 64 + bit] >= 0) named.Add(-word * (c * 64 + bit + 1));
                }
                result.At[call] = named.ToArray();
            });
        }
        List<int> zeroed = new();
        for (int b = 0; b < words; b++)
            if (Has(zero, b) && slotOf[b] >= 0) zeroed.Add(-word * (b + 1));
        result.Zero = zeroed.ToArray();
        return result;
    }
}
