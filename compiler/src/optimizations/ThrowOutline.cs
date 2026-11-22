#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A SMALL FUNCTION'S THROW MOVED OUT OF IT, so the function can be put in
/// its callers. A `throw` is lowered where it stands (Lowering.EmitThrow):
/// the exception made, the frame pointer read for its trace
/// (Runtime.Capture), the chain of handlers unwound -- and a body that reads
/// its own frame or unwinds is no body for the inliner (Inline.NotInlineable:
/// put in a caller, the trace would name the wrong frame). So a span's
/// indexer, three instructions and a bounds check on its way in, was a call
/// for every element of every loop over a span.
///
/// The throw's blocks -- every block from which no return is reached, from
/// one block every other way into them goes through -- become a function of
/// their own, `NAME$throw$K`, whose parameters are the values the blocks read
/// and the function made before; the block that led into them becomes a call
/// of it. That function is a call wherever the function now goes, its own
/// frame what the trace starts from: the return address into the function
/// with the line of the throw, which the call carries -- so the trace names
/// the throw's line by that return address, as .NET names a throw helper's
/// caller, and the throw's own row (Capture's site) is not said again:
/// null. Once the function is put in a caller, the row is the caller's, at
/// the line of its call of the function -- the function's own row gone, as
/// .NET's inlined methods are from its traces.
///
/// NAMED BY WHAT IT IS: `NAME$throw$` and a hash of its own body, placeless
/// (IrFunctionCodec), so one name is one body in every unit -- another
/// unit's copy of the function, which may have moved its throws otherwise,
/// names its own otherwise -- and it is exported and coalescible, copies
/// of one body one definition at the link. Local, a function that took a
/// small function's moved throw in (NotNull put in its callers) named a
/// local and was no body the link brings into another unit (IrUnitCodec):
/// every such caller stopped being taken across units, and with them the
/// regions and lifetimes the link worked out through them (1298, 1319).
/// The driver's sweep takes it as its caller's code, not a root of its own
/// (Driver.LibraryPrivateKept).
///
/// Only in a function small enough, without them, to be put in its callers
/// (SmallBody, LoopBody), that nothing else keeps out of them -- a handler of its
/// own, NoInlining, an async body -- and only blocks that name no frame slot
/// (another frame's would be another slot) and hold no instruction kept as a
/// call (Module.KeepCalls). Moved out anywhere else, a throw costs a call, a
/// function's alignment and a frame-table row, for nothing.
/// </summary>
public sealed class ThrowOutline : IModulePass
{
    public string Name => "throw-outline";

    /// <summary>
    /// The most instructions a function keeps, its throws moved out, to have
    /// them moved: as many as the inliner may take in -- its small body and
    /// the credit for the function's argument words (Inline.SmallLimit), or
    /// LoopBody, where the inliner weighs a loop's call by the hot path
    /// alone. Pipeline.Default sets them as it sets the inliner's.
    /// </summary>
    public int SmallBody { get; init; } = 40;
    public int ArgumentWordCredit { get; init; }
    public int LoopBody { get; init; } = 64;

    /// <summary>What marks a function's throw as its own frame's (Inline.NotInlineable).</summary>
    private static bool Blocks(Instr i) => i.Op is Opcode.FramePointer or Opcode.Unwind;

    /// <summary>The throw's own: the frame read for its trace.</summary>
    private static bool Throw(Instr i) => i.Op == Opcode.FramePointer;

    public void Run(Module module)
    {
        List<Function> made = new();
        foreach (Function f in module.Functions)
            if (Candidate(f)) made.AddRange(Outline(module, f));
        module.Functions.AddRange(made);
    }

    private static bool Candidate(Function f)
        => f.Blocks.Count > 1 && f.Async is null && !f.NoInlining && !f.ProgramEntry && !f.Version
           && !Escape.IsStaticInitialiser(f.Name)
           && !f.Blocks.Any(b => b.IsLandingPad)
           && f.Blocks.Any(b => b.Instrs.Any(Blocks));

    private List<Function> Outline(Module module, Function f)
    {
        List<Function> made = new();
        // Every block's ways in, and the blocks a return is reached from.
        Dictionary<Block, List<Block>> into = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks) into[b] = new();
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is not { } end) continue;
            foreach (Block t in end.Targets) if (into.TryGetValue(t, out List<Block>? ways)) ways.Add(b);
            if (end.Default is { } fallback && into.TryGetValue(fallback, out List<Block>? more)) more.Add(b);
        }
        HashSet<Block> warm = new(ReferenceEqualityComparer.Instance);
        Stack<Block> pending = new();
        foreach (Block b in f.Blocks)
            if ((b.Terminator is not { } end || end.Op == Opcode.Ret) && warm.Add(b)) pending.Push(b);
        while (pending.Count > 0)
            foreach (Block p in into[pending.Pop()])
                if (warm.Add(p)) pending.Push(p);

        // THE THROWS: from each cold block a warm one leads into, the cold
        // blocks it reaches; taken where every other way into them is from
        // among them, and something in them reads the frame or unwinds.
        List<(Block Head, List<Block> Region)> throws = new();
        HashSet<Block> taken = new(ReferenceEqualityComparer.Instance);
        foreach (Block head in f.Blocks)
        {
            if (warm.Contains(head) || ReferenceEquals(head, f.Blocks[0]) || !into[head].Any(warm.Contains)) continue;
            List<Block> region = new() { head };
            HashSet<Block> inside = new(ReferenceEqualityComparer.Instance) { head };
            for (int k = 0; k < region.Count; k++)
            {
                if (region[k].Terminator is not { } end) continue;
                IEnumerable<Block> next = end.Targets.ToArray();
                if (end.Default is { } fallback) next = next.Append(fallback);
                foreach (Block t in next)
                    if (!warm.Contains(t) && inside.Add(t)) region.Add(t);
            }
            bool alone = region.Skip(1).All(b => into[b].All(inside.Contains)) && !region.Any(taken.Contains)
                // ONE THROW: its line is the call's, and two would share one.
                && region.Sum(b => b.Instrs.Count(Throw)) == 1
                && !region.Any(b => b.Instrs.Any(i => module.KeepCalls.Contains(i) || i.Op is Opcode.StackPointer or Opcode.LabelAddr
                    || i.Operands.Any(o => o is SlotOperand)));
            if (!alone) continue;
            throws.Add((head, region));
            foreach (Block b in region) taken.Add(b);
        }
        if (throws.Count == 0) return made;
        // What is left must be a body the inliner takes: nothing else of the
        // kind, and small.
        int left = f.Blocks.Where(b => !taken.Contains(b)).Sum(b => b.Instrs.Count) + 3 * throws.Count;
        int words = ArgumentWordCredit == 0 ? 0 : f.Params.Sum(p => (p.Type.Bytes() + IrTypes.Word.Bytes() - 1) / IrTypes.Word.Bytes());
        if (left > Math.Max(LoopBody, SmallBody + Math.Min(24, words * ArgumentWordCredit)) || f.Blocks.Where(b => !taken.Contains(b)).Any(b => b.Instrs.Any(Blocks))) return made;

        // Registers made anywhere outside the throws: read inside one, each is
        // a parameter of its function.
        HashSet<VReg> outside = new(f.Params);
        foreach (Block b in f.Blocks)
            if (!taken.Contains(b))
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d) outside.Add(d);

        foreach ((Block head, List<Block> region) in throws)
        {
            List<VReg> reads = new();
            HashSet<VReg> seen = new();
            foreach (Block b in region)
                foreach (Instr i in b.Instrs)
                    foreach (Operand o in i.Operands)
                        if (o is RegOperand r && outside.Contains(r.Reg) && seen.Add(r.Reg)) reads.Add(r.Reg);
            // The throw's line: where its trace is read (Lowering.EmitThrow
            // reads the frame there); else the region's first.
            int line = region.SelectMany(b => b.Instrs).Where(Throw).Select(i => i.Line).FirstOrDefault(l => l > 0);
            if (line <= 0) line = region.SelectMany(b => b.Instrs).Select(i => i.Line).FirstOrDefault(l => l > 0);

            // The throw's blocks as a body of their own (Inline.Expand puts
            // them in the new function, each register and block its own).
            Function body = new(f.Name + "$throw", IrType.Void);
            body.Params.AddRange(reads);
            foreach (Block b in region)
            {
                body.Blocks.Add(b);
                // THE THROW'S ROW is its caller's return address now: no site.
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Callee is { } callee && callee.StartsWith(CaptureCall, StringComparison.Ordinal) && i.Operands.Count == 3)
                        i.Operands[2] = new ImmOperand(0, IrTypes.Word);
            }
            // Made once under a name every stub is first given, to read what
            // it is, and again under the name that says so.
            Function Made(string name)
            {
                Function fn = new(name, IrType.Void)
                {
                    Exported = true, Coalescible = true, GenericCode = f.GenericCode, FromLibrary = f.FromLibrary,
                    SystemCode = f.SystemCode, SourceFile = f.SourceFile, Line = line > 0 ? line : f.Line, Display = f.Display,
                    SourcePath = f.SourcePath, Unjudged = f.Unjudged, NoInlining = true,
                };
                List<VReg> passed = new();
                foreach (VReg r in reads)
                {
                    VReg p = fn.NewReg(r.Type, r.Name);
                    p.Number = r.Number;
                    fn.Params.Add(p);
                    passed.Add(p);
                }
                Block entry = fn.NewBlock("entry");
                Builder builder = new(fn, entry);
                builder.Call(body.Name, IrType.Void, passed);
                // (Its line every instruction's that Expand puts in its place: a
                // fault in the throw's own work is said at the throw.)
                entry.Instrs[0].Line = line;
                entry.Instrs.Add(new Instr { Op = Opcode.Unreachable, Line = line });
                Inline.Expand(fn, entry, 0, entry.Instrs[0], body);
                return fn;
            }
            byte[] identity = FastHash.Of(Corsac.Lang.Metadata.IrFunctionCodec.Write(Made(f.Name + StubMark), placeless: true));
            string named = f.Name + StubMark + Convert.ToHexString(identity, 0, 8);
            // TWO THROWS OF ONE BODY -- a span's two range checks, each the
            // same refusal -- are one function, as any two copies of it are.
            Function stub = made.Find(m => m.Name == named) ?? module.Functions.Find(m => m.Name == named) ?? Made(named);
            if (!made.Contains(stub) && !module.Functions.Contains(stub)) made.Add(stub);

            // And the way into them a call of it, which never returns.
            foreach (Block b in region.Skip(1)) f.Blocks.Remove(b);
            head.Instrs.Clear();
            Instr call = new() { Op = Opcode.Call, Callee = stub.Name, Line = line };
            foreach (VReg r in reads) call.Operands.Add(r);
            head.Instrs.Add(call);
            head.Instrs.Add(new Instr { Op = Opcode.Trap, Line = line });
            head.Instrs.Add(new Instr { Op = Opcode.Unreachable, Line = line });
        }
        f.Edited();
        return made;
    }

    /// <summary>What a throw moved out of its function has in its name, after the function's: then the hash of its body (a copy of it keeps the mark wherever it is put).</summary>
    public const string StubMark = "$throw$";

    /// <summary>Whether a function is a throw moved out of another (its calls the cold path they were there: Inline).</summary>
    public static bool IsStub(string name) => name.Contains(StubMark, StringComparison.Ordinal);

    /// <summary>Runtime.Capture's label, as lowering calls it (Lowering.EmitThrow).</summary>
    private const string CaptureCall = "m_Runtime_Capture_3_";
}
