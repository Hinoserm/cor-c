#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE ALLOCATION PUT IN PLACE AT EVERY `new`, last. Lowering makes each
/// allocation a call to Runtime.AllocObject, AllocLeaf or Alloc, and every
/// pass that reasons about objects knows a `new` by that call -- Escape and
/// the lifetime rules, ScalarObjects, the region passes that make it
/// AllocRegion -- so it stays one through all of them, as the card marks do
/// (CardMarks). After the last of them, this makes each such call
/// Runtime.AllocFast with the kind the runtime's own function passes to
/// Gc.AllocWord, and puts AllocFast's body where the call was: a pop off the
/// thread's list for the class, its header and kind written, and a call to
/// Runtime.AllocMissed only when that fails. With a constant size the class
/// test and the rounding fold away (the cleanup after).
///
/// Only where AllocFast's body is to be had -- a runtime that defines it --
/// and never inside AllocFast or AllocMissed, whose calls to the three are
/// the long way itself.
/// </summary>
public static class AllocatorFastPaths
{
    public const string Fast = Corsac.Lang.Lto.RuntimeAbi.AllocFast;
    public const string Missed = Corsac.Lang.Lto.RuntimeAbi.AllocMissed;

    // The kinds Runtime passes Gc.AllocWord for each (Gc.KindObject, KindLeaf;
    // 0 for a block scanned word by word), as RegionPointsTo knows them.
    private const long LeafKind = 0x4C454146, ObjectKind = 0x4F424A54;

    private static long? KindOf(string? callee) => callee switch
    {
        Corsac.Lang.Lto.RuntimeAbi.AllocObject => ObjectKind,
        Corsac.Lang.Lto.RuntimeAbi.AllocLeaf => LeafKind,
        Corsac.Lang.Lto.RuntimeAbi.Alloc => 0,
        _ => null,
    };

    /// <summary>Each allocation call of `f` made a call to AllocFast; whether there was one.</summary>
    public static bool Retarget(Function f)
    {
        if (f.Name is Fast or Missed) return false;
        bool any = false;
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Call || i.Operands.Count != 1 || KindOf(i.Callee) is not long kind) continue;
                b.Instrs[k] = new Instr
                {
                    Op = Opcode.Call, Dest = i.Dest, Callee = Fast,
                    Operands = { i.Operands[0], new ImmOperand(kind, IrTypes.Word) },
                    Line = i.Line,
                };
                any = true;
            }
        }
        return any;
    }

    /// <summary>
    /// `function`'s allocations made AllocFast and its body put in place, by
    /// an inliner given that body alone -- nothing else `function` calls
    /// changes -- and `cleanup` run over what came in. `body` finds
    /// AllocFast's definition; none, and the calls are left as they were.
    /// </summary>
    public static void Run(Function function, Module local, Func<string, Function?> body, Pipeline cleanup)
    {
        if (function.Name is Fast or Missed || Skipped) return;
        if (!function.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.Call && i.Operands.Count == 1 && KindOf(i.Callee) is not null))) return;
        if (body(Fast) is not Function fast || fast.Name != Fast || ReferenceEquals(fast, function)) return;
        Retarget(function);
        Module tail = new(local.Name) { Entry = function.Name, PreserveExports = true, NeedsHeap = local.NeedsHeap };
        tail.Functions.Add(function);
        tail.Functions.Add(fast);
        new Inline { SmallBody = 200, GrowthLimit = 1 << 20, ConstantBranchBody = 200, FreshOwnerBody = 0 }.Run(tail);
        cleanup.Run(tail);
    }

    /// <summary>
    /// --skip-passes InlineAllocators: every allocation left the call it was,
    /// at the link as in a unit's compile -- the A/B of this against the two
    /// calls, and of the code it grows.
    /// </summary>
    public static bool Skipped => Switches.SkipPasses is { Length: > 0 } skip
        && skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(nameof(InlineAllocators));

    /// <summary>The cleanup run over what came in: the folds that make a constant size's test and rounding nothing.</summary>
    public static Pipeline Cleanup()
    {
        Pipeline cleanup = new() { Rounds = 2 };
        cleanup.Passes.Add(new ConstantAndCopyPropagation());
        cleanup.Passes.Add(new ConstantFold());
        cleanup.Passes.Add(new Peephole());
        cleanup.Passes.Add(new BranchSimplify());
        cleanup.Passes.Add(new DeadCodeElimination());
        return cleanup;
    }
}

/// <summary>
/// AllocatorFastPaths over a whole module (Pipeline.Default's late passes,
/// after the region pass and before the last inliner): AllocFast's body is
/// the module's own, kept until here by the inliners that pin the helpers
/// the late passes call (Inline.KeepFreeHelper).
/// </summary>
public sealed class InlineAllocators : IModulePass
{
    public string Name => "inline-allocators";

    public void Run(Module m)
    {
        Function? fast = m.Functions.FirstOrDefault(f => f.Name == AllocatorFastPaths.Fast);
        if (fast is null) return;
        Pipeline cleanup = AllocatorFastPaths.Cleanup();
        foreach (Function f in m.Functions.ToArray())
        {
            if (ReferenceEquals(f, fast)) continue;
            AllocatorFastPaths.Run(f, m, name => name == AllocatorFastPaths.Fast ? fast : null, cleanup);
        }
    }
}
