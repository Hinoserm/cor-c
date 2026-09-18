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
        Pipeline cleanup = new() { Rounds = 3 };
        cleanup.Passes.Add(new ConstantAndCopyPropagation());
        cleanup.Passes.Add(new ConstantFold());
        cleanup.Passes.Add(new Peephole());
        cleanup.Passes.Add(new BranchSimplify());
        cleanup.Passes.Add(new DeadCodeElimination());

        for (int fn = 0; fn < module.Functions.Count; fn++)
        foreach (Block block in module.Functions[fn].Blocks)
        for (int at = 0; at < block.Instrs.Count; at++)
        {
            Instr call = block.Instrs[at];
            if (call.Op != Opcode.Call || call.Callee is null
                || !originals.TryGetValue(call.Callee, out Function? target)
                || recursive.Contains(target) || !Inline.Inlineable(target, addressed)
                || call.Operands.Count != target.Params.Count
                || target.Blocks.Sum(b => b.Instrs.Count) > BodyLimit
                || !Inline.ConstantControlsBranch(target, call)) continue;
            string key = target.Name + ":" + string.Join(",", call.Operands.Select(o =>
                o is ImmOperand imm ? $"{imm.Type}={imm.Value}" : "*"));
            if (rejected.Contains(key)) continue;
            if (!versions.TryGetValue(key, out Function? version))
            {
                if (versions.Count >= ModuleLimit || counts.GetValueOrDefault(target.Name) >= PerFunctionLimit) continue;
                string name = target.Name + "$constant$" + versions.Count;
                if (originals.ContainsKey(name)) continue;
                version = new Function(name, target.Returns)
                { Exported = false, SourceFile = target.SourceFile, Line = target.Line, Display = target.Display };
                foreach (VReg parameter in target.Params)
                    version.Params.Add(version.NewReg(parameter.Type, parameter.Name));
                Block entry = version.NewBlock("entry");
                Builder builder = new(version, entry);
                Operand[] arguments = call.Operands.Select((o, index) => o is ImmOperand ? o : new RegOperand(version.Params[index])).ToArray();
                VReg? result = builder.Call(target.Name, target.Returns, arguments);
                builder.Ret(result is null ? null : new RegOperand(result));
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
            Instr replacement = new() { Op = Opcode.Call, Dest = call.Dest, Callee = version.Name, Line = call.Line };
            replacement.Operands.AddRange(call.Operands);
            block.Instrs[at] = replacement;
        }
    }
}
