#nullable enable
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// The list of passes the driver runs between lowering and the backend.
/// Later, heavier passes (SSA construction, inlining, loop work) slot in
/// as more entries; the module-level ones -- inlining needs every function
/// -- will take a <see cref="Module"/> through a second interface, and the
/// pipeline will interleave the two.
/// </summary>
public sealed class Pipeline
{
    public List<IPass> Passes { get; } = new();

    /// <summary>Whole-module passes, run once between the per-function rounds.</summary>
    public List<IModulePass> ModulePasses { get; } = new();

    /// <summary>Whole-module passes that want the code fully folded first; a final per-function round follows them.</summary>
    public List<IModulePass> LatePasses { get; } = new();

    /// <summary>
    /// Called after every pass with the function's dump, for
    /// <c>--opt-dump</c>: the driver decides where the text goes.
    /// </summary>
    public Action<IPass, Function, string>? Trace { get; set; }

    /// <summary>
    /// Whether to run the verifier after every pass. On in debug builds,
    /// where a broken pass should fail on the pass that broke it rather
    /// than in the backend three passes later.
    /// </summary>
    public bool Verify { get; set; }
#if DEBUG
        = true;
#endif

    /// <summary>
    /// How many times to repeat the whole list. Each pass exposes work for
    /// the others -- folding makes copies, propagation makes dead code,
    /// dead code makes empty blocks -- so one round is rarely enough and
    /// three almost always is.
    /// </summary>
    public int Rounds { get; set; } = 1;
    public int Workers { get; set; } = 1;

    /// <summary>
    /// The standard optimiser: cheap, safe, and enough to clean up what
    /// lowering leaves behind. Ordered so each pass feeds the next.
    /// </summary>
    public static Pipeline Default(int rounds = 3, bool optimizeSize = false, bool experimentalBatch = false)
    {
        Inline Inliner(bool keepFree = false) => new()
        {
            SmallBody = optimizeSize ? 8 : 40,
            ArgumentWordCredit = optimizeSize ? 4 : 0,
            ConstantBranchBody = optimizeSize ? 80 : 160,
            GrowthLimit = optimizeSize ? 1200 : 4000,
            // Preserve the opportunity to remove owned child allocations.
            FreshOwnerBody = 320,
            FreshOwnerGrowthLimit = optimizeSize ? 4000 : 0,
            KeepFreeHelper = keepFree,
        };
        Pipeline p = new() { Rounds = rounds };
        if (experimentalBatch) p.Verify = true;
        p.Passes.Add(new ConstantFold());
        p.Passes.Add(new Narrowing());
        p.Passes.Add(new ConstantAndCopyPropagation());
        p.Passes.Add(new ArrayLengthFacts());
        p.Passes.Add(new FrameAddressFold());
        p.Passes.Add(new LocalCopies());
        p.Passes.Add(new WideProductSharing());
        p.Passes.Add(new IntegerReassociate());
        p.Passes.Add(new IntegerValueReuse());
        p.Passes.Add(new CarryRecognition());
        p.Passes.Add(new DivRemReuse());
        p.Passes.Add(new SignedPowerOfTwo());
        p.Passes.Add(new ByteSwapCalls());
        p.Passes.Add(new Peephole());
        p.Passes.Add(new ConstantFold());
        p.Passes.Add(new DeadCodeElimination());
        p.Passes.Add(new BooleanMaskDiamonds());
        if (experimentalBatch)
        {
            p.Passes.Add(new BitFactSimplify());
            p.Passes.Add(new EdgePredicateSimplify());
            p.Passes.Add(new StoreBackElimination());
            p.Passes.Add(new LoadReuse());
            p.Passes.Add(new DeadCodeElimination());
        }
        p.Passes.Add(new LoopInvariant());
        p.Passes.Add(new BranchSimplify());
        if (experimentalBatch) p.Passes.Add(new CommonTailMerge());
        p.ModulePasses.Add(new DeadStatics());
        p.ModulePasses.Add(new ConstantSpecialize());
        p.ModulePasses.Add(Inliner(keepFree: true));
        // After inlining, because a literal's length is only visible once
        // the routine reading it has been folded into the caller that
        // named the literal.
        p.ModulePasses.Add(new ReadOnlyFold());
        if (experimentalBatch)
        {
            p.ModulePasses.Add(new ConstantReturns());
            p.ModulePasses.Add(new DeadReturns());
            p.ModulePasses.Add(new DeadArguments());
        }
        // After inlining AND the folding that follows it, when each object's
        // whole life is in one function and its size is a constant; then the
        // inliner once more, whose dead-function removal drops the allocator
        // and collector when nothing calls them any longer.
        // Per-function cleanup exposes another bounded round of constructor
        // inlining. Do it before lifetime analysis, not only afterwards,
        // otherwise newly visible owned children miss their promotion chance.
        p.LatePasses.Add(Inliner(keepFree: true));
        p.LatePasses.Add(new ScalarObjects());
        p.LatePasses.Add(new Escape());
        p.LatePasses.Add(Inliner());
        return p;
    }

    public void Run(Module m)
    {
        // The cheap passes first, so inlining sees bodies already folded and
        // its size estimates are of real code; then again after, over what
        // inlining exposed.
#if COR_SELFHOST_BENCHMARK
        Corsac.Program.BenchmarkStage("opt-functions-initial");
#endif
        RunFunctions(m);
        foreach (IModulePass p in ModulePasses)
        {
            if (p is IParallelModulePass parallel) parallel.Workers = Workers;
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-module-begin " + p.Name);
#endif
            if (Accounting)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), b0 = GC.GetTotalAllocatedBytes();
                p.Run(m);
                Account("module:" + p.Name, System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetTotalAllocatedBytes() - b0);
            }
            else p.Run(m);
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-module-end " + p.Name);
#endif
        }
        if (ModulePasses.Count > 0)
        {
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-functions-middle");
#endif
            RunFunctions(m);
        }
        if (LatePasses.Count > 0)
        {
            foreach (IModulePass p in LatePasses)
            {
                if (p is IParallelModulePass parallel) parallel.Workers = Workers;
#if COR_SELFHOST_BENCHMARK
                Corsac.Program.BenchmarkStage("opt-late-begin " + p.Name);
#endif
                p.Run(m);
#if COR_SELFHOST_BENCHMARK
                Corsac.Program.BenchmarkStage("opt-late-end " + p.Name);
#endif
            }
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-functions-final");
#endif
            RunFunctions(m);
        }
    }

    private void RunFunctions(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        // Trace callbacks are ordered user-visible output; keep that diagnostic
        // mode serial. Normal stateless passes own disjoint function graphs.
        if (Workers == 1 || Trace is not null || module.Functions.Count < 2)
        {
            foreach (Function function in module.Functions) Run(function);
            return;
        }
        FunctionWorkers.Run(module, Workers, (function, index) => Run(function));
    }

    /// <summary>
    /// What each pass cost over the whole process, by name, kept only when
    /// CORC_REPORT_PASSES is set. Allocation per pass is the number that
    /// matters: the collector's pauses are what stop eight workers from
    /// being eight times one, and a pass that allocates a gigabyte to reach
    /// its answer is where those pauses come from.
    /// </summary>
    public static readonly bool Accounting = Environment.GetEnvironmentVariable("CORC_REPORT_PASSES") is not null;
    private static readonly Dictionary<string, (long Ticks, long Bytes, long Runs)> accounts = new(StringComparer.Ordinal);
    private static readonly object accountGate = new();
    public static void Account(string name, long ticks, long bytes)
    {
        lock (accountGate)
        {
            accounts.TryGetValue(name, out var was);
            accounts[name] = (was.Ticks + ticks, was.Bytes + bytes, was.Runs + 1);
        }
    }
    public static void ReportAccounts()
    {
        if (!Accounting) return;
        lock (accountGate)
            foreach (var (name, cost) in accounts.OrderByDescending(pair => pair.Value.Bytes))
                Console.Error.WriteLine("pass " + name + " " + (cost.Ticks * 1000 / System.Diagnostics.Stopwatch.Frequency) + "ms "
                    + (cost.Bytes >> 20) + "MiB runs=" + cost.Runs);
    }

    public void Run(Function f)
    {
        if (Verify)
        {
            Verifier.Check(f, "before optimisation");
        }
        for (int round = 0; round < Rounds; round++)
        {
            foreach (IPass p in Passes)
            {
                if (Accounting)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), b0 = GC.GetAllocatedBytesForCurrentThread();
                    p.Run(f);
                    Account(p.Name, System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - b0);
                }
                else p.Run(f);
                if (Verify)
                {
                    Verifier.Check(f, $"after {p.Name}");
                }
                if (Trace is not null)
                {
                    StringBuilder sb = new();
                    f.Dump(sb);
                    Trace(p, f, sb.ToString());
                }
            }
        }
    }
}
