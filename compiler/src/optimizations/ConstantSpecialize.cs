#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Bounded internal versions for constant arguments controlling branches.
/// Retains the ABI and the original body for other callers. Pruning the
/// version before escape analysis separates retaining/non-retaining paths.
/// </summary>
public sealed class ConstantSpecialize : IModulePass
{
    public string Name => "constant-specialize";
    public int BodyLimit { get; init; } = 512;
    public int ModuleLimit { get; init; } = 64;
    public int PerFunctionLimit { get; init; } = 16;
    /// <summary>Total retained clone instructions per original function.
    /// Small pruned versions share the budget instead of losing to call order.</summary>
    public int PerFunctionGrowth { get; init; } = 2048;

    public void Run(Module module)
    {
        Dictionary<string, Function> originals = module.Functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        HashSet<string> addressed = Inline.AddressTaken(module);
        HashSet<Function> recursive = Inline.RecursiveFunctions(module, originals);
        Dictionary<string, Function> versions = new(StringComparer.Ordinal);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        Dictionary<string, int> growth = new(StringComparer.Ordinal);
        HashSet<string> rejected = new(StringComparer.Ordinal);
        // A FUNCTION WITH CALLS KEPT (OwnedElements) STAYS ITSELF: a version
        // of it would hold them unkept and the candidate be refused; kept in
        // the version, its calls left it small enough that the late inliner
        // took it whole into its callers and dropped it, while the IR the
        // link reruns those passes over still called it.
        HashSet<string> keeping = new(StringComparer.Ordinal);
        foreach (Function f in module.Functions)
            if (f.Blocks.Any(b => b.Instrs.Any(module.KeepCalls.Contains))) keeping.Add(f.Name);
        Pipeline cleanup = new() { Rounds = 3 };
        cleanup.Passes.Add(new ConstantAndCopyPropagation());
        cleanup.Passes.Add(new ConstantFold());
        cleanup.Passes.Add(new Peephole());
        cleanup.Passes.Add(new BranchSimplify());
        cleanup.Passes.Add(new DeadCodeElimination());

        // The library's code this image may not keep (Module.LibraryPrivate):
        // a version of it would be a copy kept, which the compile refuses.
        HashSet<string> hidden = module.LibraryPrivate();
        for (int fn = 0; fn < module.Functions.Count; fn++)
        foreach (Block block in module.Functions[fn].Blocks)
        for (int at = 0; at < block.Instrs.Count; at++)
        {
            Instr call = block.Instrs[at];
            if (call.Op != Opcode.Call || call.Callee is null
                || !originals.TryGetValue(call.Callee, out Function? target)
                || recursive.Contains(target) || keeping.Contains(target.Name) || !Inline.Inlineable(target, addressed)
                // NOR OF A BODY ANOTHER IMAGE SUPPLIES (Module.GoesElsewhere):
                // a version of it is a definition of the library's code no
                // image exports, this image's own copy of it.
                || (module.LibraryCodeIsShared && target.FromLibrary) || module.GoesElsewhere(target) || hidden.Contains(target.Name)
                || call.Operands.Count != target.Params.Count
                || target.Blocks.Sum(b => b.Instrs.Count) > BodyLimit
                || !Inline.ConstantControlsBranch(target, call)) continue;
            string key = target.Name + ":" + string.Join(",", call.Operands.Select(o =>
                o is ImmOperand imm ? $"{imm.Type}={imm.Value}" : "*"));
            if (rejected.Contains(key)) continue;
            if (!versions.TryGetValue(key, out Function? version))
            {
                if (versions.Count >= ModuleLimit || counts.GetValueOrDefault(target.Name) >= PerFunctionLimit) continue;
                // NAMED BY WHAT IT BAKES IN, each constant argument's place and
                // value: a version of generic code is generic code, the same
                // name in every unit that makes it, and the link takes copies
                // of one name for one function (coalescing; the argument solve
                // reads one unit's hints for all). A count of this unit's
                // versions named another unit's other version alike: unit A's
                // first, GetSubArray with its start known, and unit B's first,
                // another, were one function to the link, whose argument facts
                // took from B's the parameter A's never read.
                string name = target.Name + "$constant$" + string.Join("_", call.Operands.Select((o, index) => o is ImmOperand imm
                    ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) + (imm.Value < 0 ? "m" + ((ulong)(-(imm.Value + 1)) + 1).ToString("x", System.Globalization.CultureInfo.InvariantCulture)
                        : "x" + imm.Value.ToString("x", System.Globalization.CultureInfo.InvariantCulture))
                    : null).Where(part => part is not null));
                if (originals.ContainsKey(name)) continue;
                version = new Function(name, target.Returns)
                {
                    Exported = false, SourceFile = target.SourceFile, Line = target.Line, Display = target.Display,
                    // Whose code it is goes with the copy: the x86 backend's
                    // check that float code runs in a kept state asks it
                    // (Selector.X87Fact). And whether it runs inside its
                    // caller's (a $inside copy's, Function.InsideState).
                    FromLibrary = target.FromLibrary, SystemCode = target.SystemCode, SourcePath = target.SourcePath, InsideState = target.InsideState,
                    // A version of a generic copy is one too: any image's own,
                    // never its library's private code (Module.LibraryPrivate)
                    // -- Array.Clear<byte>'s, made once its throws went out of
                    // it (ThrowOutline) and it was small enough to version.
                    GenericCode = target.GenericCode,
                };
                foreach (VReg parameter in target.Params)
                {
                    VReg copy = version.NewReg(parameter.Type, parameter.Name);
                    copy.Number = parameter.Number;
                    version.Params.Add(copy);
                }
                Block entry = version.NewBlock("entry");
                Builder builder = new(version, entry);
                Operand[] arguments = call.Operands.Select((o, index) => o is ImmOperand ? o : RegOperand.Of(version.Params[index])).ToArray();
                VReg? result = builder.Call(target.Name, target.Returns, arguments);
                builder.Ret(result is null ? null : RegOperand.Of(result));
                Inline.Expand(version, entry, 0, entry.Instrs[0], target);
                cleanup.Run(version);
                int cost = version.Blocks.Sum(b => b.Instrs.Count);
                if (cost > PerFunctionGrowth - growth.GetValueOrDefault(target.Name))
                {
                    rejected.Add(key);
                    continue;
                }
                versions[key] = version;
                counts[target.Name] = counts.GetValueOrDefault(target.Name) + 1;
                growth[target.Name] = growth.GetValueOrDefault(target.Name) + cost;
                module.Functions.Add(version);
            }
            // The same call to another copy: what it was marked keeps -- a
            // struct made for it (FreshStruct), a site the link chose for a
            // region, a catch's type, which arguments are structs passed by
            // value (the copy takes the same arguments in the same places).
            Instr replacement = new()
            {
                Op = Opcode.Call, Dest = call.Dest, Callee = version.Name, Line = call.Line,
                Field = call.Field, RegionSite = call.RegionSite, DispatchType = call.DispatchType, Number = call.Number, Family = call.Family,
            };
            replacement.Operands.AddRange(call.Operands);
            replacement.PassedByValue = call.PassedByValue;
            block.Instrs[at] = replacement;
        }
    }
}
