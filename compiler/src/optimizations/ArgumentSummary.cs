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

    /// <summary>A function of more instructions than this is asked nothing of versions: none is made of one so long.</summary>
    public const int VersionBody = 96;

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
                // (A static cell's address in data: what it holds, anything
                // reading that word may write.)
                if (IsCell(r.Symbol)) hints.CellsWritten.Add(r.Symbol);
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
                Copies = f.Coalescible || f.GenericCode,
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
            Cells(f, hints);
            // A parameter the function writes again is not the value it came
            // with for long: what a caller passed says nothing of its reads.
            // One a branch is taken on -- itself, or what is compared or
            // copied from it (Inline.ConstantControlsBranch's reach, one
            // parameter at a time) -- is one whose number, known, takes a
            // way of the function out: said with how many instructions
            // read it or what follows from it.
            for (int k = 0; k < f.Params.Count; k++)
            {
                if (definitions.ContainsKey(f.Params[k].Id)) { said.Shapes[k] = ArgumentShape.Written; continue; }
                if (said.Reads[k] == 0 || size > VersionBody) continue;
                HashSet<int> follows = new() { f.Params[k].Id };
                int folds = 0;
                bool decides = false, more = true;
                while (more)
                {
                    more = false;
                    folds = 0;
                    foreach (Block b in f.Blocks)
                        foreach (Instr i in b.Instrs)
                        {
                            bool reads = false;
                            foreach (Operand o in i.Operands) if (o is RegOperand r && follows.Contains(r.Reg.Id)) { reads = true; break; }
                            if (!reads) continue;
                            folds++;
                            if (i.Op is Opcode.Branch or Opcode.Switch) decides = true;
                            else if (i.Dest is { } made && i.Op is Opcode.Copy or Opcode.SExt32 or Opcode.ZExt32 or Opcode.Trunc64 or Opcode.Eq or Opcode.Ne
                                or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU
                                && definitions.TryGetValue(made.Id, out var once) && once.Count == 1)
                                more |= follows.Add(made.Id);
                        }
                }
                said.Folds[k] = folds;
                if (decides) said.Shapes[k] = ArgumentShape.Compared;
            }
            // A STRUCT'S ADDRESS ONLY READ THROUGH (ArgumentFunction.Fields):
            // every use of the parameter the address of a load of one word,
            // at no more than three offsets. Not `this`: a method's object
            // is the caller's own, which what the method calls may change
            // while it runs; a struct passed by value is the function's
            // alone for the call.
            for (int k = 0; k < f.Params.Count; k++)
            {
                VReg parameter = f.Params[k];
                if (parameter.Type != IrTypes.Word || parameter.Name == "this" || said.Reads[k] == 0 || definitions.ContainsKey(parameter.Id)) continue;
                SortedSet<int> offsets = new();
                bool only = true;
                foreach (Block b in f.Blocks)
                {
                    foreach (Instr i in b.Instrs)
                    {
                        for (int o = 0; o < i.Operands.Count && only; o++)
                        {
                            if (i.Operands[o] is not RegOperand read || read.Reg.Id != parameter.Id) continue;
                            if (i.Op == Opcode.Load && o == 0 && i.Size == IrTypes.Word.Bytes() && i.Dest is { Type: var loaded } && loaded == IrTypes.Word
                                && i.Offset is >= 0 and < 4096 && !i.Device) offsets.Add((int)i.Offset);
                            else only = false;
                        }
                        if (!only) break;
                    }
                    if (!only) break;
                }
                if (only && offsets.Count is > 0 and <= ArgumentFunction.MostFields) said.Fields[k] = offsets.ToArray();
            }
            // How deep in loops each block lies, by the jumps back: a jump
            // from a block to one laid out at or before it closes a loop
            // over every block between, as the IR lays them. Up to 3.
            Dictionary<Block, int> blockAt = new(ReferenceEqualityComparer.Instance);
            for (int b = 0; b < f.Blocks.Count; b++) blockAt[f.Blocks[b]] = b;
            byte[] depth = new byte[f.Blocks.Count];
            for (int b = 0; b < f.Blocks.Count; b++)
                foreach (Instr i in f.Blocks[b].Instrs)
                    foreach (Block target in i.Targets)
                        if (blockAt.TryGetValue(target, out int to) && to <= b)
                            for (int inside = to; inside <= b; inside++) if (depth[inside] < 3) depth[inside]++;

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
            for (int at = 0; at < f.Blocks.Count; at++)
                foreach (Instr i in f.Blocks[at].Instrs)
                {
                    if (i.Op == Opcode.Call && i.Callee is { } callee)
                    {
                        // (An intrinsic, an asm block among them, calls no
                        // function of this code: InlineAsm refuses a call, and
                        // a jump but to the block's own labels.)
                        if (NativeCall.Is(i) || callee.StartsWith("__x86.i.", StringComparison.Ordinal) || callee.StartsWith("__x64.i.", StringComparison.Ordinal)) continue;
                        if (callee == f.Name) said.Recursive = true;
                        ArgumentValue[] arguments = new ArgumentValue[i.Operands.Count];
                        for (int a = 0; a < arguments.Length; a++) arguments[a] = Value(i.Operands[a]);
                        said.Calls.Add(new ArgumentCall(callee, depth[at], 1, arguments) { ByValue = i.PassedByValue });
                    }
                    else if (i.Op == Opcode.CallIndirect && !NativeCall.Is(i))
                    {
                        // A CALL THROUGH A WORD NOTHING NAMES: of a function
                        // of as many parameters as it passes arguments, or
                        // fewer (the target the first operand). Not a native
                        // one's: C's convention, every argument on the stack,
                        // reaches no method of this code but one of C's ways
                        // in (Fixed: CalleePops, [StackArguments]) -- none of
                        // a descriptor's slots.
                        slots ??= Escape.VirtualCallees(new[] { f });
                        if (!slots.TryGetValue(i, out string[]? names) || names.Length == 0) { Unnamed(f, i, hints); continue; }
                        ArgumentValue[] arguments = new ArgumentValue[i.Operands.Count - 1];
                        for (int a = 0; a < arguments.Length; a++) arguments[a] = Value(i.Operands[a + 1]);
                        foreach (string name in names) said.Calls.Add(new ArgumentCall(name, depth[at], 1, arguments));
                    }
                }
            // The same call twice is said once, with how often; in an order
            // that is the calls' own, not the blocks'.
            List<ArgumentCall> sorted = said.Calls.GroupBy(Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.First() with { Count = group.Count() }).ToList();
            said.Calls.Clear();
            said.Calls.AddRange(sorted);
            // Two functions of one name in a unit: nothing is said of either.
            if (!hints.Functions.TryAdd(f.Name, said)) hints.Functions[f.Name].Fixed = true;
        }
        return hints;

        static string Key(ArgumentCall call)
            => call.Callee + "@" + call.Depth + "/" + call.ByValue.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "(" + string.Join(",", call.Arguments.Select(a => a.Kind switch
            {
                ArgumentValueKind.Constant => "#" + a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArgumentValueKind.Address => "&" + a.Symbol + "+" + a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArgumentValueKind.Parameter => "p" + a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => "?",
            })) + ")";
    }

    /// <summary>A static field's cell (Lowering's `s_` labels).</summary>
    private static bool IsCell(string name) => name.StartsWith("s_", StringComparison.Ordinal);

    /// <summary>
    /// AN UNNAMED CALL, said by where its target comes from: a slot's method
    /// read out of an object's descriptor, of the type the call dispatches
    /// on (SlotCalls, by the slot's plain name); else, every value the
    /// target may hold a static cell's word or a named address or a number
    /// (CellCalls, by cell -- a named address is a function whose address
    /// is taken, never judged); else anything (Unnamed). Each with how many
    /// arguments it passes, the target the first operand.
    /// </summary>
    private static void Unnamed(Function f, Instr call, ArgumentHints hints)
    {
        int count = call.Operands.Count - 1;
        Dictionary<int, List<Instr>> defs = Definitions(f);
        if (call.DispatchType is string declaring && call.Operands[0] is RegOperand { Reg: var target }
            && defs.TryGetValue(target.Id, out List<Instr>? methods) && methods is [{ Op: Opcode.Load } method]
            && method.Operands.Count == 1 && method.Operands[0] is RegOperand { Reg: var table }
            && defs.TryGetValue(table.Id, out List<Instr>? tables) && tables is [{ Op: Opcode.Load, Offset: 0 }])
        {
            Add(hints.SlotCalls, Escape.VirtualCallee(declaring, method.Offset), count);
            return;
        }
        HashSet<string> cells = new(StringComparer.Ordinal);
        HashSet<int> parameters = new();
        if (!Sources(f, defs, call.Operands[0], cells, 8, new HashSet<int>(), parameters)) { hints.Unnamed.Add(count); return; }
        foreach (string cell in cells) Add(hints.CellCalls, cell, count);
        foreach (int k in parameters) Add(hints.ParamCalls, ParameterName(f, k), count);

        static void Add(SortedDictionary<string, SortedSet<int>> calls, string name, int count)
        {
            if (!calls.TryGetValue(name, out SortedSet<int>? counts)) calls[name] = counts = new();
            counts.Add(count);
        }
    }

    /// <summary>A function's parameter as the hints name it: `k:function`.</summary>
    internal static string ParameterName(Function f, int k) => k.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + f.Name;

    /// <summary>Every instruction writing each register of a function, by its id.</summary>
    private static Dictionary<int, List<Instr>> Definitions(Function f)
    {
        Dictionary<int, List<Instr>> defs = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) (defs.TryGetValue(d.Id, out List<Instr>? l) ? l : defs[d.Id] = new()).Add(i);
        return defs;
    }

    /// <summary>
    /// Whether every value `o` may hold is a number, a named address, a
    /// word read whole from a static cell (each added to `cells`), or a
    /// parameter's value as it came in, never written in the function (each
    /// added to `parameters`, where it is asked for): through copies,
    /// nothing else.
    /// </summary>
    private static bool Sources(Function f, Dictionary<int, List<Instr>> defs, Operand o, HashSet<string> cells, int depth, HashSet<int> seen,
        HashSet<int>? parameters = null)
    {
        if (o is ImmOperand or SymOperand) return true;
        if (o is not RegOperand { Reg: var r } || depth == 0) return false;
        int index = f.Params.IndexOf(r);
        if (index >= 0)
        {
            if (parameters is null || defs.ContainsKey(r.Id)) return false;
            parameters.Add(index);
            return true;
        }
        if (!defs.TryGetValue(r.Id, out List<Instr>? made)) return false;
        if (!seen.Add(r.Id)) return true;
        foreach (Instr d in made)
        {
            if (d.Op == Opcode.Copy && d.Operands.Count == 1)
            {
                if (!Sources(f, defs, d.Operands[0], cells, depth - 1, seen, parameters)) return false;
            }
            else if (d.Op == Opcode.Load && !d.Device && d.Operands.Count == 1 && d.Operands[0] is SymOperand { Name: var cell } && IsCell(cell)
                && d.Size == IrTypes.Word.Bytes())
                cells.Add(cell);
            else return false;
        }
        return true;
    }

    /// <summary>
    /// What a function does with static cells (ArgumentHints.CellsWritten):
    /// a cell it writes with what is not a number or a named address, or
    /// whose address it uses but to read or write the cell.
    /// </summary>
    private static void Cells(Function f, ArgumentHints hints)
    {
        Dictionary<int, List<Instr>>? defs = null;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not SymOperand { Name: var cell } || !IsCell(cell)) continue;
                    if (k == 0 && i.Op == Opcode.Load && i.Operands.Count == 1) continue;
                    // (A whole word, the cell's own: a narrower or wider store
                    // is a part of something, said written.)
                    if (k == 0 && i.Op == Opcode.Store && i.Operands.Count == 2 && i.Offset == 0 && i.Size == IrTypes.Word.Bytes()
                        && ((SymOperand)i.Operands[0]).Offset == 0)
                    {
                        HashSet<string> from = new(StringComparer.Ordinal);
                        HashSet<int> parameters = new();
                        defs ??= Definitions(f);
                        if (Sources(f, defs, i.Operands[1], from, 8, new HashSet<int>(), parameters) && from.Count == 0)
                        {
                            // (A parameter's value: the callers say what it is.)
                            foreach (int p in parameters)
                            {
                                if (!hints.CellParams.TryGetValue(cell, out SortedSet<string>? those)) hints.CellParams[cell] = those = new(StringComparer.Ordinal);
                                those.Add(ParameterName(f, p));
                            }
                            continue;
                        }
                    }
                    hints.CellsWritten.Add(cell);
                }
    }

    /// <summary>
    /// THE VERSIONS THE LINK ASKED FOR, made by the unit that defines each
    /// function (ArgumentFacts.Versions), before Apply and from the function
    /// as it still is (Version), each put after the functions; the made
    /// versions and their functions returned. Exported as the function is,
    /// so the calls of every unit find it.
    /// </summary>
    public static List<(Function Target, Function Version, ArgumentVersion Asked)> MakeVersions(Module m, ArgumentFacts facts)
    {
        List<(Function, Function, ArgumentVersion)> made = new();
        if (facts.Versions.Count == 0) return made;
        Pipeline folding = Folding(1);
        int functions = m.Functions.Count;
        for (int at = 0; at < functions; at++)
        {
            Function target = m.Functions[at];
            if (!facts.Versions.TryGetValue(target.Name, out ArgumentVersion[]? versions) || target.Blocks.Count == 0) continue;
            foreach (ArgumentVersion asked in versions)
            {
                Function version = Version(target, asked, facts, folding);
                m.Functions.Add(version);
                made.Add((target, version, asked));
            }
        }
        return made;
    }

    /// <summary>
    /// One version of `target` (a function of the parameters left once the
    /// version's numbers and what Dropped takes are gone), whose whole body
    /// is a call of the function with those numbers written in -- then the
    /// function put in its place (Inline.Expand, as ConstantSpecialize makes
    /// its own) and what the numbers fold folded. `target` is read, not
    /// changed. Called only by name: never through a slot, never a body any
    /// pass after takes in, and kept a call where the function is.
    /// </summary>
    public static Function Version(Function target, ArgumentVersion asked, ArgumentFacts facts, Pipeline folding, Function? like = null)
    {
        ArgumentDrop[] dropped = facts.Dropped.GetValueOrDefault(target.Name) ?? Array.Empty<ArgumentDrop>();
        // What the function is, from `like` where given: the function as
        // compiled, where `target` is a copy the codec made, which keeps
        // only the body.
        Function version = Described(like ?? target, asked.Name, version: true);
        // The function's parameters in order: one the link took is its
        // number (or nothing, never read: a zero); one of those left is the
        // version's number where the version sets it, and the version's own
        // parameter where it does not.
        Operand[] arguments = new Operand[target.Params.Count];
        int place = 0;
        for (int k = 0; k < target.Params.Count; k++)
        {
            VReg parameter = target.Params[k];
            int taken = Array.FindIndex(dropped, drop => drop.Parameter == k);
            if (taken >= 0) { arguments[k] = new ImmOperand(dropped[taken].Value.Kind == ArgumentValueKind.Constant ? dropped[taken].Value.Number : 0, parameter.Type); continue; }
            int set = Array.FindIndex(asked.Bound, one => one.Parameter == place);
            place++;
            if (set >= 0) { arguments[k] = new ImmOperand(asked.Bound[set].Value.Number, parameter.Type); continue; }
            VReg copy = version.NewReg(parameter.Type, parameter.Name);
            copy.Number = parameter.Number;
            version.Params.Add(copy);
            arguments[k] = RegOperand.Of(copy);
        }
        Block entry = version.NewBlock("entry");
        Builder builder = new(version, entry);
        VReg? result = builder.Call(target.Name, target.Returns, arguments);
        builder.Ret(result is null ? null : RegOperand.Of(result));
        Inline.Expand(version, entry, 0, entry.Instrs[0], target);
        folding.Run(version);
        return version;
    }

    /// <summary>A function of no body, named `name`, that says what `f` says of itself: where it was written, whether exported, inlined, the library's -- what a version of `f` says too.</summary>
    public static Function Described(Function f, string name, bool version = false) => new(name, f.Returns)
    {
        Exported = f.Exported, SourceFile = f.SourceFile, Line = f.Line, Display = f.Display,
        FromLibrary = f.FromLibrary, SystemCode = f.SystemCode, SourcePath = f.SourcePath, InsideState = f.InsideState,
        Version = version, NoInlining = f.NoInlining, Unjudged = f.Unjudged,
    };

    /// <summary>
    /// The passes that fold what a number in a parameter's place lets them:
    /// constants and copies carried, branches decided, the dead gone. Six
    /// rounds: each carries a number one step down a chain of what it
    /// makes, and `at & 63` shifted into an element's offset is four steps
    /// from the parameter (three left the shift a shift of two numbers).
    /// </summary>
    public static Pipeline Folding(int workers)
    {
        Pipeline folding = new() { Rounds = 6, Workers = workers };
        folding.Passes.Add(new ConstantFold { AcrossFunction = true }); folding.Passes.Add(new ConstantAndCopyPropagation());
        folding.Passes.Add(new DeadCodeElimination()); folding.Passes.Add(new IdentitySelect()); folding.Passes.Add(new BranchSimplify());
        return folding;
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
        // A way in is the i386 backend's to lay down (Function.ConstantEntry):
        // nowhere else is one made, or called.
        bool ways = Target.Current.WordSize == 4;
        bool given = GivenWay(f, facts, ways);
        return Taken(f, facts, errors, ways) | given;
    }

    /// <summary>The function's own way in, noted on it for the backend: after what Taken does to its parameters, which the places are of.</summary>
    private static bool GivenWay(Function f, ArgumentFacts facts, bool ways)
    {
        if (ways && facts.Entries.TryGetValue(f.Name, out ArgumentDrop[]? set))
            f.ConstantEntry = (ArgumentFacts.EntryName(f.Name, set.Select(one => one.Parameter)), set.Select(one => (one.Parameter, one.Value.Number)).ToArray());
        return false;
    }

    private static bool Taken(Function f, ArgumentFacts facts, List<string> errors, bool ways)
    {
        // Whether a call here passes less or other than it did: what it
        // passed may be dead now -- arithmetic for a number never read, a
        // struct's address now its words -- and its struct the frame's no
        // longer (SlotScalars, and the cleanup after).
        bool changed = false;
        {
            Dictionary<Instr, string[]>? slots = null;
            foreach (Block b in f.Blocks)
                for (int at = 0; at < b.Instrs.Count; at++)
                {
                    Instr i = b.Instrs[at];
                    if (i.Op == Opcode.Call && i.Callee is { } callee && facts.Dropped.TryGetValue(callee, out ArgumentDrop[]? gone))
                    {
                        if (gone[^1].Parameter >= i.Operands.Count) { errors.Add(Misfit(f, i, callee)); continue; }
                        for (int k = gone.Length - 1; k >= 0; k--) i.Operands.RemoveAt(gone[k].Parameter);
                        changed = true;
                    }
                    // A STRUCT THE FUNCTION TAKES AS ITS WORDS (ArgumentFacts.
                    // Splits): each loaded here, from the address the call
                    // would have passed, and passed in the address's place.
                    if (i.Op == Opcode.Call && i.Callee is { } taking && facts.Splits.TryGetValue(taking, out ArgumentSplit[]? words))
                    {
                        if (words[^1].Place >= i.Operands.Count) { errors.Add(Misfit(f, i, taking)); continue; }
                        List<Instr> loads = new();
                        for (int k = words.Length - 1; k >= 0; k--)
                        {
                            Operand address = i.Operands[words[k].Place];
                            i.Operands.RemoveAt(words[k].Place);
                            for (int o = words[k].Offsets.Length - 1; o >= 0; o--)
                            {
                                VReg word = f.NewReg(IrTypes.Word);
                                loads.Insert(0, new Instr { Op = Opcode.Load, Dest = word, Operands = { address }, Offset = words[k].Offsets[o], Size = IrTypes.Word.Bytes(), Signed = true, Line = i.Line });
                                i.Operands.Insert(words[k].Place, word);
                            }
                        }
                        b.Instrs.InsertRange(at, loads);
                        at += loads.Count;
                        changed = true;
                    }
                    // A VERSION OF THE FUNCTION, where the call passes exactly
                    // the numbers one has inside it (ArgumentFacts.Versions):
                    // called in the function's place, those operands gone.
                    if (i.Op == Opcode.Call && i.Callee is { } whole && facts.Versions.TryGetValue(whole, out ArgumentVersion[]? versions))
                    {
                        foreach (ArgumentVersion version in versions)
                        {
                            if (!version.Bound.All(one => one.Parameter < i.Operands.Count && i.Operands[one.Parameter] is ImmOperand { Type: IrType.I32 or IrType.I64 } passed
                                && passed.Value == one.Value.Number)) continue;
                            Instr instead = new()
                            {
                                Op = Opcode.Call, Dest = i.Dest, Callee = version.Name, Line = i.Line,
                                Field = i.Field, RegionSite = i.RegionSite, DispatchType = i.DispatchType, Number = i.Number, Family = i.Family,
                            };
                            instead.Operands.AddRange(i.Operands);
                            for (int k = version.Bound.Length - 1; k >= 0; k--) instead.Operands.RemoveAt(version.Bound[k].Parameter);
                            b.Instrs[at] = i = instead;
                            changed = true;
                            break;
                        }
                    }
                    // BY ITS WAY IN, where the call passes exactly the numbers
                    // that sets (ArgumentFacts.Entries): the operands stay,
                    // each in its place -- the backend loads none of those the
                    // name says are set (RegisterArgs.Given). A call that
                    // passes anything else there calls the function as ever.
                    if (ways && i.Op == Opcode.Call && i.Callee is { } named && facts.Entries.TryGetValue(named, out ArgumentDrop[]? set)
                        && set.All(one => one.Parameter < i.Operands.Count && i.Operands[one.Parameter] is ImmOperand { Type: IrType.I32 } passed && passed.Value == one.Value.Number))
                    {
                        // The same call to another name: what it was marked keeps (ConstantSpecialize makes its own so).
                        Instr through = new()
                        {
                            Op = Opcode.Call, Dest = i.Dest, Callee = ArgumentFacts.EntryName(named, set.Select(one => one.Parameter)), Line = i.Line,
                            Field = i.Field, RegionSite = i.RegionSite, DispatchType = i.DispatchType, Number = i.Number, Family = i.Family,
                        };
                        through.Operands.AddRange(i.Operands);
                        through.PassedByValue = i.PassedByValue;
                        b.Instrs[at] = through;
                    }
                    else if (i.Op == Opcode.CallIndirect && (facts.DroppedSlots.Count > 0 || facts.UnsetSlots.Count > 0) && !NativeCall.Is(i))
                    {
                        slots ??= Escape.VirtualCallees(new[] { f });
                        if (!slots.TryGetValue(i, out string[]? names)) continue;
                        // AN ARGUMENT NO FUNCTION THE SLOT REACHES READS, its
                        // place kept (ArgumentFacts.UnsetSlots): passed as 0,
                        // what it was made of dead here. Not a struct passed
                        // by value, whose copy the call itself may make.
                        // (Every name the call has must say the same.)
                        int[]? unset = null;
                        foreach (string name in names)
                        {
                            if (!facts.UnsetSlots.TryGetValue(name, out int[]? of) && !facts.UnsetSlots.TryGetValue(VirtualTargets.Plain(name), out of)) { unset = null; break; }
                            if (unset is not null && !unset.AsSpan().SequenceEqual(of)) { unset = null; break; }
                            unset = of;
                        }
                        if (unset is not null)
                            foreach (int k in unset)
                            {
                                if (1 + k >= i.Operands.Count || ((i.PassedByValue >> k) & 3) != 0 || i.Operands[1 + k] is ImmOperand
                                    || i.Operands[1 + k].Type is not (IrType.I32 or IrType.I64)) continue;
                                i.Operands[1 + k] = new ImmOperand(0, i.Operands[1 + k].Type);
                                changed = true;
                            }
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
                        changed = true;
                    }
                }
            // Its own parameters: the loads through a struct's address made
            // copies of its words first (Split), then what the link took
            // taken, then each such address's parameter made its words --
            // found by the register itself, wherever the taking left it.
            List<(VReg Address, VReg[] Words)>? asWords = Split(f, facts, errors);
            bool numbers = Drop();
            foreach ((VReg address, VReg[] made) in asWords ?? new())
            {
                int index = f.Params.IndexOf(address);
                f.Params.RemoveAt(index);
                f.Params.InsertRange(index, made);
            }
            return changed | numbers | asWords is not null;

            bool Drop()
            {
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
        }

        // ITS OWN STRUCTS THAT COME AS THEIR WORDS (ArgumentFacts.Splits):
        // a parameter for each word where the address was, and every load
        // through the address a copy of its word. Places are of the
        // parameters once Dropped has taken its own, but the two take from
        // different parameters, so each finds its own by counting past the
        // other's: this one first, by the places as they will be.
        static List<(VReg Address, VReg[] Words)>? Split(Function f, ArgumentFacts facts, List<string> errors)
        {
            if (!facts.Splits.TryGetValue(f.Name, out ArgumentSplit[]? splits)) return null;
            ArgumentDrop[] dropped = facts.Dropped.GetValueOrDefault(f.Name) ?? Array.Empty<ArgumentDrop>();
            // Each place as an index among the parameters as they are now,
            // before the link's taking: counted past what it takes.
            List<(VReg Address, VReg[] Words)> plan = new();
            Dictionary<int, (int Split, VReg[] Words)> taken = new();
            for (int s = 0; s < splits.Length; s++)
            {
                int place = -1, index = 0;
                for (; index < f.Params.Count; index++)
                {
                    if (Array.FindIndex(dropped, drop => drop.Parameter == index) >= 0) continue;
                    if (++place == splits[s].Place) break;
                }
                if (index >= f.Params.Count)
                {
                    errors.Add("'" + (f.Display ?? f.Name) + "' has no parameter at place " + splits[s].Place + " for the link to take as words (ArgumentFacts)");
                    return null;
                }
                VReg address = f.Params[index];
                VReg[] made = splits[s].Offsets.Select(offset => f.NewReg(IrTypes.Word, address.Name is null ? null : address.Name + "$" + offset)).ToArray();
                taken[address.Id] = (s, made);
                plan.Add((address, made));
            }
            foreach (Block b in f.Blocks)
                for (int at = 0; at < b.Instrs.Count; at++)
                {
                    Instr i = b.Instrs[at];
                    for (int o = 0; o < i.Operands.Count; o++)
                    {
                        if (i.Operands[o] is not RegOperand read || !taken.TryGetValue(read.Reg.Id, out var of)) continue;
                        int which = i.Op == Opcode.Load && o == 0 && i.Size == IrTypes.Word.Bytes() ? Array.IndexOf(splits[of.Split].Offsets, (int)i.Offset) : -1;
                        if (which < 0 || i.Dest is null)
                        {
                            errors.Add("'" + (f.Display ?? f.Name) + "' (line " + i.Line + ") uses a struct's address the link took as its words for more than reading them (ArgumentFacts)");
                            return null;
                        }
                        b.Instrs[at] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Operands = { of.Words[which] }, Line = i.Line };
                        break;
                    }
                }
            return plan;
        }

        static string Misfit(Function f, Instr i, string callee)
            => "'" + (f.Display ?? f.Name) + "' (line " + i.Line + ") calls '" + callee + "' with " + i.Operands.Count
                + " operands, fewer than the parameters the link took from it say (ArgumentFacts)";
    }
}
