#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Metadata;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// The unit's UsesNotes, taken from the module as lowering made it -- before
/// any pass -- so what a definition names is what its source wrote: a call
/// the inliner will take away, a static DeadStatics will drop, are still
/// there to count (UnusedReport).
/// </summary>
public static class UsesCapture
{
    public static List<UsesNotes.Definition> Of(Module module)
    {
        List<UsesNotes.Definition> made = new(module.Functions.Count + module.Data.Count);
        foreach (Function function in module.Functions)
        {
            HashSet<string> reads = new(StringComparer.Ordinal);
            HashSet<string> writes = new(StringComparer.Ordinal);
            foreach (Block block in function.Blocks)
            {
                foreach (Instr instruction in block.Instrs)
                {
                    if (instruction.Op == Opcode.Call && instruction.Callee is not null) reads.Add(instruction.Callee);
                    for (int k = 0; k < instruction.Operands.Count; k++)
                    {
                        if (instruction.Operands[k] is not SymOperand symbol) continue;
                        // The address as a store's destination is the one use
                        // that is not a read (DeadStatics).
                        if (instruction.Op == Opcode.Store && k == 0) writes.Add(symbol.Name);
                        else reads.Add(symbol.Name);
                    }
                }
            }
            writes.ExceptWith(reads);
            made.Add(new(UsesNotes.Kind.Function, function.Name, function.Exported, function.SystemCode, function.SourceFile, function.Line,
                CompilerMade(function.Display) ? null : function.Display, reads.Order(StringComparer.Ordinal).ToArray(), writes.Order(StringComparer.Ordinal).ToArray()));
        }
        foreach (DataItem item in module.Data)
            made.Add(new(UsesNotes.Kind.Data, item.Name, item.Exported, item.SystemCode, item.SourceFile, item.Line, item.Display,
                item.Relocs.Select(relocation => relocation.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Array.Empty<string>()));
        // What lowering never emitted (Module.Unlowered): nothing names it,
        // so nothing reaches it; a generic method's note shares its place in
        // the source with the copies other units make of it.
        int unlowered = 0;
        foreach (var (method, display, file, line) in module.Unlowered)
            made.Add(new(method ? UsesNotes.Kind.Function : UsesNotes.Kind.Data, "unlowered$" + unlowered++, false, false, file, line, display,
                Array.Empty<string>(), Array.Empty<string>()));
        return made;
    }

    /// <summary>
    /// The machinery's types the compiler made of what somebody wrote: a
    /// lambda's closure, an iterator's or async method's state machine, an
    /// array's interface view. Nobody wrote their methods, and what did write
    /// them is judged instead: the method the lambda sits in reaches the
    /// closure's descriptor, and through it the Invoke, whenever it runs.
    /// </summary>
    static readonly string[] MadeTypes = { "Lambda$", "Iter$", "Async$", "ArrayView$", "ArrayEnumerator$", "ValueTuple$" };

    static bool CompilerMade(string? display)
    {
        if (display is null) return false;
        int open = display.IndexOf('(');
        string owner = open < 0 ? display : display[..open];
        int member = owner.LastIndexOf('.');
        if (member > 0) owner = owner[..member];
        return MadeTypes.Any(prefix => owner.StartsWith(prefix, StringComparison.Ordinal));
    }
}
