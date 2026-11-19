#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A UNIT'S ARGUMENT HINTS, made from its IR as the link gets it (Driver's
/// beforeLate, with the regions' summary): every function's parameters and
/// every call's arguments, for the link to judge over the whole program
/// (Lto.ArgumentSolver) -- and the link's answer taken (Apply), when the
/// unit is compiled again knowing it.
/// </summary>
public static class ArgumentSummary
{
    /// <summary>What begins the label of a method that takes its arguments on the stack whatever they are ([StackArguments]).</summary>
    private const string StackArguments = "mc_";

    public static ArgumentHints Of(Module m)
    {
        ArgumentHints hints = new();
        HashSet<string> own = new(m.Data.Select(d => d.Name), StringComparer.Ordinal);
        // The helpers the late passes and the backend add calls to by name
        // (RuntimeAbi.Helpers), which no IR made before them shows.
        HashSet<string> helpers = new(RuntimeAbi.Helpers, StringComparer.Ordinal);
        helpers.UnionWith(m.RuntimeHelpers);
        // And the two the backend calls for a long copy or fill (Select.StreamedOr).
        helpers.Add(RuntimeAbi.StreamCopy);
        helpers.Add(RuntimeAbi.StreamFill);
        foreach (DataItem d in m.Data)
            foreach (DataReloc r in d.Relocs)
            {
                if (own.Contains(r.Symbol)) continue;
                if (Escape.IsDescriptor(d.Name)) hints.Slotted.Add(r.Symbol); else hints.Addressed.Add(r.Symbol);
            }
        Dictionary<int, int> parameterOf = new();
        Dictionary<int, (int Count, Instr? Definition)> definitions = new();
        foreach (Function f in m.Functions)
        {
            ArgumentFunction said = new(f.Params.Count)
            {
                // WHAT FOREIGN CODE ENTERS, AND WHAT IS NOT ONE FUNCTION: C's
                // ways in and a method called with its arguments pushed; an
                // async method's or a state machine's body; the entry; a
                // helper the passes after this add calls to; a version made
                // of another function, whose calls are that function's.
                Fixed = f.CalleePops != 0 || f.KeepsCallerRegisters || f.Async is not null || f.InsideState || f.ProgramEntry || f.Version
                    || f.Name == m.Entry || f.Name == m.Main || f.Name.StartsWith(StackArguments, StringComparison.Ordinal) || helpers.Contains(f.Name),
            };
            parameterOf.Clear();
            for (int k = 0; k < f.Params.Count; k++)
            {
                said.Kinds[k] = f.Params[k].Type switch { IrType.I32 => ArgumentKind.Word, IrType.F32 => ArgumentKind.Float, _ => ArgumentKind.Wide };
                // One register twice among the parameters says nothing of either.
                if (!parameterOf.TryAdd(f.Params[k].Id, k)) said.Fixed = true;
            }
            definitions.Clear();
            int size = 0;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    size++;
                    if (i.Dest is { } made)
                        definitions[made.Id] = definitions.TryGetValue(made.Id, out var before) ? (before.Count + 1, null) : (1, i);
                    foreach (Operand o in i.Operands)
                    {
                        if (o is SymOperand named && !own.Contains(named.Name)) hints.Addressed.Add(named.Name);
                        if (o is RegOperand read && parameterOf.TryGetValue(read.Reg.Id, out int k)) said.Reads[k]++;
                    }
                }
            said.Size = size;
            // A parameter the function writes again is not the value it came
            // with for long: what a caller passed says nothing of its reads.
            for (int k = 0; k < f.Params.Count; k++)
                if (definitions.ContainsKey(f.Params[k].Id)) said.Shapes[k] = ArgumentShape.Written;

            Dictionary<Instr, string[]>? slots = null;
            ArgumentValue Value(Operand o)
            {
                switch (o)
                {
                    case ImmOperand number when number.Type is IrType.I32 or IrType.I64:
                        return ArgumentValue.Of(number.Value);
                    case SymOperand symbol:
                        return ArgumentValue.At(symbol.Name, symbol.Offset);
                    case RegOperand register:
                        if (parameterOf.TryGetValue(register.Reg.Id, out int k))
                            return definitions.ContainsKey(register.Reg.Id) ? default : ArgumentValue.Passed(k);
                        // A register made once, by a copy of a number or an address.
                        if (definitions.TryGetValue(register.Reg.Id, out var one) && one is { Count: 1, Definition: { Op: Opcode.Copy } copy }
                            && copy.Operands.Count == 1 && copy.Operands[0] is ImmOperand or SymOperand)
                            return Value(copy.Operands[0]);
                        return default;
                    default:
                        return default;
                }
            }
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is { } callee)
                    {
                        if (NativeCall.Is(i) || callee.StartsWith("__x86.i.", StringComparison.Ordinal) || callee.StartsWith("__x64.i.", StringComparison.Ordinal)) continue;
                        if (callee == f.Name) said.Recursive = true;
                        ArgumentValue[] arguments = new ArgumentValue[i.Operands.Count];
                        for (int a = 0; a < arguments.Length; a++) arguments[a] = Value(i.Operands[a]);
                        said.Calls.Add(new ArgumentCall(callee, 0, arguments));
                    }
                    else if (i.Op == Opcode.CallIndirect && !NativeCall.Is(i))
                    {
                        slots ??= Escape.VirtualCallees(new[] { f });
                        if (!slots.TryGetValue(i, out string[]? names) || names.Length == 0) { hints.Unnamed = true; continue; }
                        ArgumentValue[] arguments = new ArgumentValue[i.Operands.Count - 1];
                        for (int a = 0; a < arguments.Length; a++) arguments[a] = Value(i.Operands[a + 1]);
                        foreach (string name in names) said.Calls.Add(new ArgumentCall(name, 0, arguments));
                    }
                }
            // The same call twice says it once; in an order that is the
            // calls' own, not the blocks'.
            List<ArgumentCall> sorted = said.Calls.DistinctBy(Key, StringComparer.Ordinal).OrderBy(Key, StringComparer.Ordinal).ToList();
            said.Calls.Clear();
            said.Calls.AddRange(sorted);
            // Two functions of one name in a unit: nothing is said of either.
            if (!hints.Functions.TryAdd(f.Name, said)) hints.Functions[f.Name].Fixed = true;
        }
        return hints;

        static string Key(ArgumentCall call)
            => call.Callee + "(" + string.Join(",", call.Arguments.Select(a => a.Kind switch
            {
                ArgumentValueKind.Constant => "#" + a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArgumentValueKind.Address => "&" + a.Symbol + "+" + a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArgumentValueKind.Parameter => "p" + a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => "?",
            })) + ")";
    }

    /// <summary>
    /// THE LINK'S ANSWER TAKEN, last of all the IR's passes and before the
    /// backend: each function named loses the parameters the whole program
    /// found no parameters -- given, where it read one, the value every
    /// caller passed, first thing -- and each call of one, by name or
    /// through a slot whose family lost them, passes them no more.
    ///
    /// LAST, so that every pass before it finds the functions and the calls
    /// as every other fact the link solved speaks of them (the lifetimes'
    /// and the owned fields' are by parameter). What those passes make of a
    /// call -- taken into its caller, turned from a slot's to a function's
    /// -- leaves it a call of the same function with the same values, which
    /// is all the answer was of. A call it finds that does not fit the
    /// function it names is an error, never a guess.
    /// </summary>
    public static bool Apply(Module m, ArgumentFacts facts, List<string> errors)
    {
        bool given = false;
        if (facts.IsEmpty) return false;
        foreach (Function f in m.Functions) given |= Apply(f, facts, errors);
        return given;
    }

    /// <summary>
    /// The same of one function: the calls it makes, and its own parameters.
    /// Whether it was given a value in a parameter's place -- a number its
    /// arithmetic and its branches can now be folded with, by whoever runs
    /// the passes that fold (a copy and a constant propagated, a branch
    /// decided, what is dead gone).
    /// </summary>
    public static bool Apply(Function f, ArgumentFacts facts, List<string> errors)
    {
        if (facts.IsEmpty) return false;
        {
            Dictionary<Instr, string[]>? slots = null;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is { } callee && facts.Dropped.TryGetValue(callee, out ArgumentDrop[]? gone))
                    {
                        if (gone[^1].Parameter >= i.Operands.Count) { errors.Add(Misfit(f, i, callee)); continue; }
                        for (int k = gone.Length - 1; k >= 0; k--) i.Operands.RemoveAt(gone[k].Parameter);
                    }
                    else if (i.Op == Opcode.CallIndirect && facts.DroppedSlots.Count > 0 && !NativeCall.Is(i))
                    {
                        slots ??= Escape.VirtualCallees(new[] { f });
                        if (!slots.TryGetValue(i, out string[]? names)) continue;
                        // By its own name, or the one every form of it shares.
                        int[]? lost = null;
                        foreach (string name in names)
                            if (facts.DroppedSlots.TryGetValue(name, out int[]? of) || facts.DroppedSlots.TryGetValue(VirtualTargets.Plain(name), out of))
                            {
                                if (lost is not null && !lost.AsSpan().SequenceEqual(of)) { errors.Add(Misfit(f, i, name)); lost = null; break; }
                                lost = of;
                            }
                        if (lost is null) continue;
                        if (1 + lost[^1] >= i.Operands.Count) { errors.Add(Misfit(f, i, names[0])); continue; }
                        for (int k = lost.Length - 1; k >= 0; k--) i.Operands.RemoveAt(1 + lost[k]);
                    }
                }
            if (!facts.Dropped.TryGetValue(f.Name, out ArgumentDrop[]? drops)) return false;
            if (drops[^1].Parameter >= f.Params.Count)
            {
                errors.Add("'" + (f.Display ?? f.Name) + "' has " + f.Params.Count + " parameters, and the link took its parameter " + drops[^1].Parameter + " (ArgumentFacts)");
                return false;
            }
            // Each given its value where the function begins: in a block of
            // its own before the first where anything jumps back to that
            // one, so the value is given once.
            List<Instr> given = new();
            for (int k = drops.Length - 1; k >= 0; k--)
            {
                VReg parameter = f.Params[drops[k].Parameter];
                f.Params.RemoveAt(drops[k].Parameter);
                if (drops[k].Value.Kind == ArgumentValueKind.Constant)
                    given.Insert(0, new Instr { Op = Opcode.Copy, Dest = parameter, Operands = { new ImmOperand(drops[k].Value.Number, parameter.Type) } });
            }
            if (given.Count == 0 || f.Blocks.Count == 0) return false;
            Block entry = f.Blocks[0];
            bool reentered = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    foreach (Block target in i.Targets) reentered |= ReferenceEquals(target, entry);
            if (reentered)
            {
                Block first = f.NewBlock("given");
                f.Blocks.RemoveAt(f.Blocks.Count - 1);
                f.Blocks.Insert(0, first);
                first.Instrs.AddRange(given);
                first.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { entry } });
            }
            else entry.Instrs.InsertRange(0, given);
            return true;
        }

        static string Misfit(Function f, Instr i, string callee)
            => "'" + (f.Display ?? f.Name) + "' (line " + i.Line + ") calls '" + callee + "' with " + i.Operands.Count
                + " operands, fewer than the parameters the link took from it say (ArgumentFacts)";
    }
}
