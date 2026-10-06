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

    /// <summary>--trace-opt's function, said after every module and late pass as well (TraceModulePass).</summary>
    public string? TraceFunction { get; set; }

    private void TraceModulePass(string pass, Module m)
    {
        if (TraceFunction is null) return;
        foreach (Function f in m.Functions)
        {
            if (f.Name != TraceFunction) continue;
            System.Text.StringBuilder text = new();
            f.Dump(text);
            Console.Error.WriteLine("---- after module " + pass);
            Console.Error.Write(text);
        }
    }

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
    /// --verify-marks: each pass checked for a mark it lost off an
    /// instruction it kept or replaced one for one (MarkVerifier), said on
    /// standard error with the pass's name. Off but for that switch: it
    /// notes every function's marks before every pass.
    /// </summary>
    public bool VerifyMarks { get; set; } = Switches.VerifyMarks;

    /// <summary>
    /// How many times to repeat the whole list. Each pass exposes work for
    /// the others -- folding makes copies, propagation makes dead code,
    /// dead code makes empty blocks -- so one round is rarely enough and
    /// three almost always is.
    /// </summary>
    public int Rounds { get; set; } = 1;

    /// <summary>
    /// Called once, with the module as the late passes will find it: after
    /// the function and module passes, before the lifetime rules. A unit
    /// keeps its IR from here for the link (IrUnitCodec.Snapshot), which runs
    /// the late passes again over the whole program's answers.
    /// </summary>
    public Action<Module>? BeforeLate { get; set; }
    public int Workers { get; set; } = 1;

    /// <summary>
    /// The standard optimiser: cheap, safe, and enough to clean up what
    /// lowering leaves behind. Ordered so each pass feeds the next.
    /// </summary>
    public static Pipeline Default(int rounds = 3, bool optimizeSize = false, bool experimentalBatch = false, string? regionReport = null, bool regions = true)
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
        // --verify-passes: the IR checked after every pass, late ones
        // included, so a pass that breaks the IR is named where it does it
        // and not at the link that imports the unit.
        if (experimentalBatch || Switches.VerifyPasses) p.Verify = true;
        p.Passes.Add(new ConstantFold());
        p.Passes.Add(new Narrowing());
        p.Passes.Add(new ConstantAndCopyPropagation());
        p.Passes.Add(new ArrayLengthFacts());
        // After copies are propagated, so a loop's test and the bounds check
        // inside it name the same registers (InductionBounds).
        p.Passes.Add(new InductionBounds());
        // A branch on a test decided on the way in: an inlined getter's
        // repeat of its caller's (DominatingBranch).
        p.Passes.Add(new DominatingBranch());
        p.Passes.Add(new FrameAddressFold());
        // A struct copied for a call from a slot nothing touches again.
        p.Passes.Add(new CopyForward());
        // Stores nothing reads before they are written again: a promoted
        // object's zeroing that its constructor writes over, first of all.
        p.Passes.Add(new Dse());
        p.Passes.Add(new LocalCopies());
        p.Passes.Add(new WideProductSharing());
        p.Passes.Add(new IntegerReassociate());
        p.Passes.Add(new IntegerValueReuse());
        p.Passes.Add(new CarryRecognition());
        p.Passes.Add(new DivRemReuse());
        p.Passes.Add(new SignedPowerOfTwo());
        p.Passes.Add(new ByteSwapCalls());
        p.Passes.Add(new Peephole());
        p.Passes.Add(new ConstantFold { AcrossFunction = true });
        p.Passes.Add(new DeadCodeElimination());
        p.Passes.Add(new BooleanMaskDiamonds());
        if (experimentalBatch)
        {
            p.Passes.Add(new BitFactSimplify());
            p.Passes.Add(new EdgePredicateSimplify());
            p.Passes.Add(new StoreBackElimination());
        }
        // A field read again with no write, call or lock between: `while
        // (!Done) { char c = Cur; ...` read pos, the source and its length
        // twice over in every lap of every lexer loop.
        // An inlined callee's null check that its first field read makes
        // again (NullCheckFold), before the loads are compared.
        p.Passes.Add(new NullCheckFold());
        p.Passes.Add(new LoadReuse());
        p.Passes.Add(new DeadCodeElimination());
        p.Passes.Add(new LoopInvariant());
        // A type's initialiser guard behind another for the same type: the
        // inliner set String's twice in a row where a list was made and
        // string.Empty read.
        p.Passes.Add(new StaticInitGuards());
        p.Passes.Add(new BranchSimplify());
        if (experimentalBatch) p.Passes.Add(new CommonTailMerge());
        p.ModulePasses.Add(new DeadStatics());
        p.ModulePasses.Add(new DeadClosureThis());
        // Before any inlining: the collections whose every use is still a
        // call this rule knows, and those calls kept (OwnedElements).
        p.ModulePasses.Add(new MarkOwnedElements());
        p.ModulePasses.Add(new ConstantSpecialize());
        p.ModulePasses.Add(Inliner(keepFree: true));
        // After inlining, because a literal's length is only visible once
        // the routine reading it has been folded into the caller that
        // named the literal.
        p.ModulePasses.Add(new ReadOnlyFold());
        // After the read-only fold has made what it can of the descriptors,
        // and before the late inliner, so a call it makes direct can be
        // inlined and the lifetime rules see into it.
        p.ModulePasses.Add(new Devirtualize());
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
        p.LatePasses.Add(new LateCleanup());
        p.LatePasses.Add(new ScalarObjects());
        p.LatePasses.Add(new Escape());
        // What the lifetime passes left to the collector, given to regions
        // where a boundary's return is proved to leave it dead.
        // Not without an operating system: there a thread block is a
        // processor's, and the runtime keeps no arena (baremetal.cor).
        if (regions) p.LatePasses.Add(new RegionPointsTo { Report = regionReport });
        p.LatePasses.Add(Inliner());
        // Every allocation still a call to the runtime's allocators made the
        // allocation itself, in place: after every pass that knows a `new` by
        // that call (AllocatorFastPaths), and after the last inliner, whose
        // choices are then the ones it made with the calls.
        p.LatePasses.Add(new InlineAllocators());
        // The card marks written out, last: to every pass above they are a
        // call that lets nothing go (CardMarks).
        p.LatePasses.Add(new CardMarks());
        // A DIAGNOSTIC SWITCH, for finding which pass a miscompile comes out
        // of: --skip-passes Escape,Inline leaves those out of every list.
        // Nothing is ever built with it set.
        if (Switches.SkipPasses is { Length: > 0 } skip)
        {
            HashSet<string> names = new(skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
            p.Passes.RemoveAll(pass => names.Contains(pass.GetType().Name));
            p.ModulePasses.RemoveAll(pass => names.Contains(pass.GetType().Name));
            p.LatePasses.RemoveAll(pass => names.Contains(pass.GetType().Name));
        }
        return p;
    }

    /// <summary>--report-phases: told the name of each module and late pass as it ends (the driver's Phase).</summary>
    public static Action<string>? Stage { get; set; }

    /// <summary>Whether this pipeline tells Stage (the compile's own, not the cleanups inside passes).</summary>
    public bool Reports { get; set; }

    public void Run(Module m)
    {
        // The cheap passes first, so inlining sees bodies already folded and
        // its size estimates are of real code; then again after, over what
        // inlining exposed.
#if COR_SELFHOST_BENCHMARK
        Corsac.Program.BenchmarkStage("opt-functions-initial");
#endif
        Dump(m, "lowered");
        RunFunctions(m);
        foreach (IModulePass p in ModulePasses)
        {
            if (p is IParallelModulePass parallel) parallel.Workers = Workers;
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-module-begin " + p.Name);
#endif
            Dictionary<Function, MarkVerifier>? marks = VerifyMarks ? MarkVerifier.Snapshot(m) : null;
            if (Accounting)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), b0 = GC.GetTotalAllocatedBytes();
                p.Run(m);
                Account("module:" + p.Name, System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetTotalAllocatedBytes() - b0);
            }
            else p.Run(m);
            if (marks is not null) MarkVerifier.Report(marks, m, p.Name);
            TraceModulePass(p.Name, m);
            if (Reports) Stage?.Invoke("module " + p.Name);
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-module-end " + p.Name);
#endif
            // After every module pass too, as after a late one: the inliner,
            // the lifetime rules and the region passes edit whole functions,
            // and a wrong type they leave was found only by the next
            // function pass, under another pass's name -- or not at all.
            if (Verify)
                foreach (Function checkedFunction in m.Functions) Verifier.Check(checkedFunction, $"after {p.Name}");
        }
        if (ModulePasses.Count > 0)
        {
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-functions-middle");
#endif
            RunFunctions(m);
        }
        if (Reports) Stage?.Invoke("functions");
        BeforeLate?.Invoke(m);
        if (Reports) Stage?.Invoke("before-late");
        RunLate(m);
        Dump(m, "optimised");
    }

    /// <summary>--dump-function SYMBOL: that function's IR as lowered and as optimised, on standard error.</summary>
    private static void Dump(Module m, string when)
    {
        if (Switches.DumpFunction is not { Length: > 0 } wanted) return;
        foreach (Function f in m.Functions)
        {
            if (f.Name != wanted) continue;
            System.Text.StringBuilder text = new();
            f.Dump(text);
            Console.Error.WriteLine("== " + when + " " + f.Name + "\n" + text);
        }
    }

    /// <summary>The late passes alone and the cheap passes after them: what the link runs over a unit's archived IR.</summary>
    public void RunLate(Module m)
    {
        if (LatePasses.Count > 0)
        {
            foreach (IModulePass p in LatePasses)
            {
                if (p is IParallelModulePass parallel) parallel.Workers = Workers;
#if COR_SELFHOST_BENCHMARK
                Corsac.Program.BenchmarkStage("opt-late-begin " + p.Name);
#endif
                Dictionary<Function, MarkVerifier>? marks = VerifyMarks ? MarkVerifier.Snapshot(m) : null;
                p.Run(m);
                if (marks is not null) MarkVerifier.Report(marks, m, p.Name);
                TraceModulePass(p.Name, m);
                if (Reports) Stage?.Invoke("late " + p.Name);
#if COR_SELFHOST_BENCHMARK
                Corsac.Program.BenchmarkStage("opt-late-end " + p.Name);
#endif
                if (Verify)
                {
                    foreach (Function checkedFunction in m.Functions) Verifier.Check(checkedFunction, $"after {p.Name}");
                }
            }
#if COR_SELFHOST_BENCHMARK
            Corsac.Program.BenchmarkStage("opt-functions-final");
#endif
            RunFunctions(m);
            // STRUCTS IN THE FRAME WHOSE ADDRESS GOES NOWHERE, IN REGISTERS
            // (SlotScalars): after everything, when no pass reads a slot as
            // it was made and the cleanup has left each slot's address named
            // plainly; the copies it leaves propagated and the dead ones gone.
            foreach (Function f in m.Functions)
            {
                if (!SlotScalars.Apply(f)) continue;
                new ConstantAndCopyPropagation().Run(f);
                new LocalCopies().Run(f);
                // With the slot's stores gone, an inlined method's null check
                // and the field read after it meet, and loads of the same
                // field across what were stores to the frame are one.
                new NullCheckFold().Run(f);
                new LoadReuse().Run(f);
                new ConstantAndCopyPropagation().Run(f);
                new DeadCodeElimination().Run(f);
            }
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
    /// --report-passes is given. Allocation per pass is the number that
    /// matters: the collector's pauses are what stop eight workers from
    /// being eight times one, and a pass that allocates a gigabyte to reach
    /// its answer is where those pauses come from.
    /// </summary>
    public static readonly bool Accounting = Switches.ReportPasses;
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

    /// <summary>--trace-functions: each function's name and size as the pipeline starts on it, for finding one that never finishes.</summary>
    private static readonly bool TraceFunctions = Switches.TraceFunctions;

    public void Run(Function f)
    {
        if (TraceFunctions)
        {
            Console.Error.WriteLine($"pipeline {f.Name} ({f.Blocks.Sum(b => b.Instrs.Count)} instructions, {f.Blocks.Count} blocks)");
        }
        if (Verify)
        {
            Verifier.Check(f, "before optimisation");
        }
        for (int round = 0; round < Rounds; round++)
        {
            foreach (IPass p in Passes)
            {
                MarkVerifier? marks = VerifyMarks ? MarkVerifier.Snapshot(f) : null;
                if (Accounting)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), b0 = GC.GetAllocatedBytesForCurrentThread();
                    p.Run(f);
                    Account(p.Name, System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - b0);
                }
                else p.Run(f);
                marks?.Report(f, p.Name);
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
