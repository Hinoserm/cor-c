#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>Closed-executable call graph, following the same entry/export
/// boundary as inlining. Library bodies and address-visible functions keep
/// their ABI. Staged large-batch infrastructure, not enabled by default.</summary>
internal sealed class ClosedFunctionCalls
{
    public Dictionary<string, List<Instr>> Calls { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _addressed;
    private readonly Module _module;

    public ClosedFunctionCalls(Module module)
    {
        _module = module;
        _addressed = Inline.AddressTaken(module);
        foreach (Instr instruction in module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instrs))
        {
            if (instruction.Op != Opcode.Call || instruction.Callee is not { } name) continue;
            if (!Calls.TryGetValue(name, out List<Instr>? uses)) Calls[name] = uses = new();
            uses.Add(instruction);
        }
    }

    public bool CanChange(Function function, out List<Instr> calls)
    {
        calls = Calls.GetValueOrDefault(function.Name) ?? new();
        return _module.Entry is not null && !function.FromLibrary && function.Async is null
            && !_addressed.Contains(function.Name) && Inline.Inlineable(function, _addressed)
            && calls.Count != 0 && calls.All(c => c.Operands.Count == function.Params.Count);
    }
}

/// <summary>Remove unread formal arguments and their direct-call operands.
/// Argument-producing effects remain in the caller for ordinary DCE to handle.</summary>
public sealed class DeadArguments : IParallelModulePass
{
    public string Name => "dead-arguments";
    public int Workers { get; set; } = 1;
    public void Run(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        ClosedFunctionCalls graph = new(module);
        List<int>?[] plans = new List<int>?[module.Functions.Count];
        if (Workers > 1 && module.Functions.Count > 1) AnalyzeParallel(module, graph, plans);
        else for (int i = 0; i < plans.Length; i++) plans[i] = Analyze(module.Functions[i], graph);
        // Calls belong to their callers, not to the callee being analyzed.
        // Freeze all bodies until every read-set has been computed, then
        // apply signature/call changes in module order. This same boundary
        // applies to one worker, so scheduling cannot affect pass results.
        for (int i = 0; i < plans.Length; i++)
        {
            if (plans[i] is not List<int> removed) continue;
            Function function = module.Functions[i];
            foreach (int index in removed)
            {
                function.Params.RemoveAt(index);
                foreach (Instr call in graph.Calls[function.Name]) call.Operands.RemoveAt(index);
            }
        }
    }

    private void AnalyzeParallel(Module module, ClosedFunctionCalls graph, List<int>?[] plans)
        => FunctionWorkers.Run(module, Workers, (function, index) => plans[index] = Analyze(function, graph));

    private static List<int>? Analyze(Function function, ClosedFunctionCalls graph)
    {
        if (function.Params.Count == 0 || !graph.CanChange(function, out _)) return null;
        HashSet<VReg> read = new(function.Blocks.SelectMany(b => b.Instrs).SelectMany(IrInfo.Uses));
        List<int>? removed = null;
        for (int index = function.Params.Count - 1; index >= 0; index--)
        {
            if (read.Contains(function.Params[index])) continue;
            removed ??= new();
            removed.Add(index);
        }
        return removed;
    }
}

/// <summary>Erase unused integer return transport, retaining calls and all
/// effectful evaluation in the body. Floating return-stack conventions stay.</summary>
public sealed class DeadReturns : IParallelModulePass
{
    public string Name => "dead-returns";
    public int Workers { get; set; } = 1;
    public void Run(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        ClosedFunctionCalls graph = new(module);
        if (Workers > 1 && module.Functions.Count > 1) RunParallel(module, graph);
        else foreach (Function function in module.Functions) Fold(function, graph);
    }
    private void RunParallel(Module module, ClosedFunctionCalls graph)
        => FunctionWorkers.Run(module, Workers, (function, index) => Fold(function, graph));

    private static void Fold(Function function, ClosedFunctionCalls graph)
    {
        // The call graph and call destinations remain frozen. Each worker
        // changes only its own return signature and return instructions.
        if (!function.Returns.IsInt() || !graph.CanChange(function, out List<Instr> calls)
            || calls.Any(c => c.Dest is not null)) return;
        function.Returns = IrType.Void;
        foreach (var block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
                if (instruction.Op == Opcode.Ret) instruction.Operands.Clear();
    }
}

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
