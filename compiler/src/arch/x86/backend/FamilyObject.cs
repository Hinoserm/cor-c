#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.X86;

/// <summary>
/// What both x86 backends write into an object for each function they
/// place, and once for the object, said once: the registers a prologue
/// saves as the stack maps name them, the calls the link summary may
/// rewrite, and the frame table. Each backend's Generate carried its own
/// copy, which differed only in the word's size and in how the frame
/// table names its base.
/// </summary>
internal static class FamilyObject
{
    /// <summary>
    /// The callee-saved registers a function's prologue saves, as a mask by
    /// hardware number (the StackMapLayout names them). On i386, after
    /// `push ebp; mov ebp, esp; sub esp, FrameSize` they are pushed in the
    /// order EBX, ESI, EDI, so the k-th of them present is at EBP -
    /// FrameSize - 4(k+1). The collector reads them to find where a
    /// caller's register value was kept, and so which saved words are
    /// values some frame still holds live and which are stale copies. Only
    /// a function with calls has records the collector reads: a leaf,
    /// which has no frame and may save its base first (MFunction.
    /// Frameless), has none.
    /// </summary>
    public static uint SavedMask(MFunction m)
    {
        uint mask = 0;
        foreach (Gpr g in m.SavedRegs) mask |= 1u << (int)g;
        return mask;
    }

    /// <summary>
    /// The direct calls of a placed function the link summary may rewrite
    /// (LinkSummary): a relative call, by name with no addend, to a
    /// function of no arguments returning a word -- the call's field is the
    /// four bytes before its return. `eligible` is the caller's to reuse.
    /// </summary>
    public static void DirectCalls(Function f, Encoder encoder, int start, OptimizationSummary summary, HashSet<string> eligible)
    {
        eligible.Clear();
        foreach (Corsac.Lang.Ir.Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Operands.Count == 0 && i.Dest?.Type == IrType.I32 && i.Callee is not null)
                    eligible.Add(i.Callee);
        foreach ((MInstr call, int ret) in encoder.CallSites)
            if (call.Op == MOp.Call && call.CallReloc == RelocKind.Rel32
                && call.Operands[0] is MImm { Symbol: { } callee, Value: 0 } && eligible.Contains(callee))
                summary.Calls.Add(new DirectCall(start + ret - 4, callee));
    }

    /// <summary>
    /// THE FRAME TABLE, once every function's bytes are placed, built
    /// straight into `section` (.rodata: mapped and readable, no section of
    /// its own every linker script would have to learn, so a flat
    /// freestanding image carries it exactly as an ELF does). Its one
    /// address is the first function's: the distance from the word to it
    /// (`pcRelative`, long mode's: a shared object loaded anywhere holds it
    /// without the loader), or the address itself, a word the loader
    /// writes in a shared object -- whose caller then gives the relocated
    /// constants as the section.
    /// </summary>
    public static void PlaceFrameTable(ObjectFile obj, Section section, List<FrameTable.Entry> frames, HashSet<string> defined, bool pcRelative)
    {
        if (frames.Count == 0) return;
        Corsac.Lang.Backends.ObjectData.Pad(section, X86Mode.Word, 0);
        int at = section.Bytes.Count;
        FrameTable.Build(section.Bytes, frames, out int fixup, out string baseSymbol);
        obj.Symbols.Add(new Symbol { Name = FrameTable.Symbol, Section = section, Offset = at, Size = section.Bytes.Count - at, Global = false });
        defined.Add(FrameTable.Symbol);
        section.Relocs.Add(new Relocation(at + fixup, baseSymbol, 0, pcRelative ? RelocKind.Rel32 : RelocKind.Abs32));
    }
}
