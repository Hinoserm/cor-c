#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>Propagate literal return values without duplicating the callee
/// or removing its observable effects, possible exceptions, or divergence.</summary>
public sealed class ConstantReturns : IParallelModulePass
{
    public string Name => "constant-returns";
    public int Workers { get; set; } = 1;
    public void Run(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        ClosedFunctionCalls graph = new(module);
        Dictionary<string, ImmOperand> values = new(StringComparer.Ordinal);
        if (Workers > 1 && module.Functions.Count > 1)
            AnalyzeParallel(module, graph, values);
        else foreach (Function function in module.Functions)
        {
            ImmOperand? value = Analyze(function, graph);
            if (value is not null) values[function.Name] = value;
        }
        if (values.Count == 0) return;
        if (Workers > 1 && module.Functions.Count > 1) RewriteParallel(module, values);
        else foreach (Function function in module.Functions) Rewrite(function, values);
    }

    private void AnalyzeParallel(Module module, ClosedFunctionCalls graph, Dictionary<string, ImmOperand> values)
    {
        ImmOperand?[] results = new ImmOperand?[module.Functions.Count];
        FunctionWorkers.Run(module, Workers, (function, index) => results[index] = Analyze(function, graph));
        for (int i = 0; i < results.Length; i++)
            if (results[i] is ImmOperand value) values[module.Functions[i].Name] = value;
    }

    private static ImmOperand? Analyze(Function function, ClosedFunctionCalls graph)
    {
        if (!function.Returns.IsInt() || !graph.CanChange(function, out _)) return null;
        ImmOperand? value = null;
        foreach (var block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
            {
                if (instruction.Op != Opcode.Ret) continue;
                if (instruction.Operands.Count != 1 || instruction.Operands[0] is not ImmOperand literal
                    || literal.Type != function.Returns) return null;
                long normalized = literal.Type == IrType.I32 ? unchecked((int)literal.Value) : literal.Value;
                if (value is not null && normalized != value.Value) return null;
                if (value is null) value = new ImmOperand(normalized, function.Returns);
            }
        return value;
    }

    private void RewriteParallel(Module module, Dictionary<string, ImmOperand> values)
        => FunctionWorkers.Run(module, Workers, (function, index) => Rewrite(function, values));

    private static void Rewrite(Function function, Dictionary<string, ImmOperand> values)
    {
        // All summaries are published before any call is rewritten. Workers
        // own disjoint caller graphs and only read the completed value table.
        foreach (var block in function.Blocks)
        for (int index = 0; index < block.Instrs.Count; index++)
        {
            Instr call = block.Instrs[index];
            if (call.Op != Opcode.Call || call.Dest is not { } dest || call.Callee is not { } name
                || !values.TryGetValue(name, out ImmOperand? value) || value.Type != dest.Type) continue;
            // A throwing call never reaches this copy. The call's argument
            // evaluation, stores and synchronization remain exactly in place.
            call.Dest = null;
            block.Instrs.Insert(++index, new Instr { Op = Opcode.Copy, Dest = dest, Line = call.Line,
                Operands = { value } });
        }
    }
}
