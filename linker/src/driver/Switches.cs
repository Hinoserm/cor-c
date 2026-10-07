#nullable enable

namespace Corsac;

/// <summary>
/// THE DIAGNOSTIC SWITCHES OF ONE CORC PROCESS, taken off its command line
/// before the command reads its own (Take) and handed again to every corc it
/// starts (ChildFlags). Never read from the environment: a switch that rides
/// along unseen in the environment changes a build nobody can see the
/// command for.
/// </summary>
public static class Switches
{
    /// <summary>The process-wide flags this corc was given, for every corc it starts.</summary>
    public static readonly List<string> ChildFlags = new();

    /// <summary>--alloc-report all|NAME: the escape analysis says which allocations still need the collector.</summary>
    public static bool AllocReport;

    /// <summary>The NAME of --alloc-report NAME: only the functions and fields whose names contain it.</summary>
    public static string? AllocReportOnly;

    /// <summary>--trace-handed: the escape analysis says what it handed between frames.</summary>
    public static bool HandedTrace;

    /// <summary>--trace-fields NAME: the field escape analysis on fields whose names contain NAME.</summary>
    public static string? FieldTrace;

    /// <summary>--trace-fields-all: the field escape analysis on every field.</summary>
    public static bool FieldTraceAll;

    /// <summary>--trace-forward NAME: devirtualisation's forwarding of the functions whose names contain NAME.</summary>
    public static string? TraceForward;

    /// <summary>--dump-function SYMBOL: that function's IR, as optimised and as the link loads it.</summary>
    public static string? DumpFunction;

    /// <summary>--work-budget BYTES: one batch's working budget, so a large machine works as a small one does.</summary>
    public static long WorkBudget;

    /// <summary>--verify-passes: the IR verified after every pass.</summary>
    public static bool VerifyPasses;

    /// <summary>
    /// --verify-analyses: every analysis the lifetime rules keep of a function
    /// (Opt.AnalysisCache) checked, at each answer, against the function's
    /// whole shape: an in-place edit that did not say so stops the run.
    /// </summary>
    public static bool VerifyAnalyses;
    /// <summary>--no-local-environments: call-only local functions keep heap cells and closures (Binder.ArrangeLocalFunctionEnvironment off), for bisecting.</summary>
    public static bool NoLocalEnvironments;
    /// <summary>--eager-copy-bodies: every method of a copy made per argument checked, used or not (Binder.WantBody off), for bisecting.</summary>
    public static bool EagerCopyBodies;

    /// <summary>--verify-marks: every pass checked for a mark it lost off an instruction it kept (MarkVerifier).</summary>
    public static bool VerifyMarks;

    /// <summary>--skip-passes A,B: those optimisation passes left out.</summary>
    public static string? SkipPasses;

    /// <summary>--report-passes: time and allocation per pass.</summary>
    public static bool ReportPasses;

    /// <summary>--trace-demand: the declarations each discarded frontend pass asked for.</summary>
    public static bool TraceDemand;

    /// <summary>--trace-inline NAME: the inliner's choices in callers whose names contain NAME.</summary>
    public static string? TraceInline;

    /// <summary>--trace-elements NAME: owned-element analysis on names containing NAME.</summary>
    public static string? TraceElements;

    /// <summary>--lto-jobs N: how many backends a link runs at once.</summary>
    public static int LtoJobs;

    /// <summary>--trace-field-sites: each owned-field store site, and whether it frees.</summary>
    public static bool TraceFieldSites;

    /// <summary>--trace-wants: the declarations each unit wants from the index.</summary>
    public static bool TraceWants;

    /// <summary>--report-unit: time and allocation of each unit.</summary>
    public static bool ReportUnit;

    /// <summary>--report-phases: time and allocation per phase of one unit.</summary>
    public static bool ReportPhases;

    /// <summary>--report-alloc: what the whole run allocated, at exit.</summary>
    public static bool ReportAlloc;

    /// <summary>--trace-semantics NAME: the definition semantics of NAME, written to --trace-semantics-out FILE.</summary>
    public static string? TraceSemantics;
    public static string? TraceSemanticsOut;

    /// <summary>--lib-root DIR: the tree the class library is read from.</summary>
    public static string? LibRoot;

    /// <summary>--dump-slots: each type's virtual slots as they are laid out.</summary>
    public static bool DumpSlots;

    /// <summary>--dump-families: the families of generic copies.</summary>
    public static bool DumpFamilies;

    /// <summary>--decl-budget, --token-budget, --source-budget BYTES: the declaration session's caches.</summary>
    public static long DeclBudget = -1;
    public static long TokenBudget = -1;
    public static long SourceBudget = -1;

    /// <summary>--trace-escapes NAME: why each parameter of a function named so escapes at link.</summary>
    public static string? TraceEscapes;

    /// <summary>--trace-functions: each function's name and size as the pipeline starts on it.</summary>
    public static bool TraceFunctions;

    /// <summary>--trace-decl: the parser's declarations as it reads them.</summary>
    public static bool TraceDecl;

    /// <summary>--trace-join: the details of a type refused in a string join.</summary>
    public static bool TraceJoin;

    /// <summary>--compiler-identity HASH: the compiler's hash, made once by the build that starts this process.</summary>
    public static string? CompilerIdentity;

    /// <summary>--gc-workers N: the collector's workers in this process (the native compiler's own collector).</summary>
    public static int GcWorkers = -1;

    /// <summary>--trace-virtuals: the link's descriptor index, and what each virtual call reaches.</summary>
    public static bool TraceVirtuals;

    /// <summary>
    /// --region-engine escape|andersen: which analysis finds the link's
    /// regions (RegionSolver.Solve). Escape, the default, solves each
    /// function and each cycle from the bottom of the calls up
    /// (RegionEscape); andersen is the whole-image inclusion solve with
    /// object contexts. Null for the default.
    /// </summary>
    public static string? RegionEngine;

    /// <summary>Whether the link's regions are found by the whole-image inclusion solve (--region-engine andersen).</summary>
    public static bool AndersenRegions => RegionEngine == "andersen";

    /// <summary>
    /// --no-rta: the link's regions take every override of a virtual call,
    /// on every type, made or not (VirtualTargets.Made) -- what a closed
    /// image's link otherwise drops for types nothing in it makes.
    /// </summary>
    public static bool NoRta;

    /// <summary>Takes this process's switches off the command line, wherever they are written.</summary>
    // KEPT WHEN ABSENT, every one: a switch is the process's, and this runs
    // again for every command a hosted build runs inside it (a unit's
    // compile, the project's link), whose own arguments name none of them.
    // Assigned afresh, each such command turned the process's diagnostics
    // off for the rest of it.
    public static List<string> Take(IEnumerable<string> args)
    {
        List<string> taken = new(args);
        string? report = Valued(taken, "--alloc-report");
        if (report is not null)
        {
            AllocReport = true;
            AllocReportOnly = report == "all" ? null : report;
        }
        HandedTrace |= Switch(taken, "--trace-handed");
        FieldTrace = Valued(taken, "--trace-fields") ?? FieldTrace;
        FieldTraceAll |= Switch(taken, "--trace-fields-all");
        TraceForward = Valued(taken, "--trace-forward") ?? TraceForward;
        DumpFunction = Valued(taken, "--dump-function") ?? DumpFunction;
        if (Number(taken, "--work-budget") is long budget) WorkBudget = budget;
        VerifyPasses |= Switch(taken, "--verify-passes");
        VerifyMarks |= Switch(taken, "--verify-marks");
        VerifyAnalyses |= Switch(taken, "--verify-analyses");
        NoLocalEnvironments |= Switch(taken, "--no-local-environments");
        EagerCopyBodies |= Switch(taken, "--eager-copy-bodies");
        SkipPasses = Valued(taken, "--skip-passes") ?? SkipPasses;
        ReportPasses |= Switch(taken, "--report-passes");
        TraceDemand |= Switch(taken, "--trace-demand");
        TraceInline = Valued(taken, "--trace-inline") ?? TraceInline;
        TraceElements = Valued(taken, "--trace-elements") ?? TraceElements;
        if (Number(taken, "--lto-jobs") is long jobs) LtoJobs = (int)jobs;
        TraceFieldSites |= Switch(taken, "--trace-field-sites");
        TraceWants |= Switch(taken, "--trace-wants");
        ReportUnit |= Switch(taken, "--report-unit");
        ReportPhases |= Switch(taken, "--report-phases");
        ReportAlloc |= Switch(taken, "--report-alloc");
        TraceSemantics = Valued(taken, "--trace-semantics") ?? TraceSemantics;
        TraceSemanticsOut = Valued(taken, "--trace-semantics-out") ?? TraceSemanticsOut;
        LibRoot = Valued(taken, "--lib-root") ?? LibRoot;
        DumpSlots |= Switch(taken, "--dump-slots");
        DumpFamilies |= Switch(taken, "--dump-families");
        if (Number(taken, "--decl-budget") is long decl) DeclBudget = decl;
        if (Number(taken, "--token-budget") is long token) TokenBudget = token;
        if (Number(taken, "--source-budget") is long source) SourceBudget = source;
        TraceEscapes = Valued(taken, "--trace-escapes") ?? TraceEscapes;
        TraceFunctions |= Switch(taken, "--trace-functions");
        TraceDecl |= Switch(taken, "--trace-decl");
        TraceJoin |= Switch(taken, "--trace-join");
        CompilerIdentity = Valued(taken, "--compiler-identity") ?? CompilerIdentity;
        TraceVirtuals |= Switch(taken, "--trace-virtuals");
        // Kept when absent, as --region-engine is.
        if (Switch(taken, "--no-rta")) NoRta = true;
        // Kept when absent: a hosted project link takes the switches again
        // off its own command line, which does not name them.
        if (Valued(taken, "--region-engine") is string engine)
        {
            if (engine is not ("escape" or "andersen")) throw new ArgumentException("--region-engine takes escape or andersen, not '" + engine + "'");
            RegionEngine = engine;
        }
        if (Number(taken, "--gc-workers") is long workers) GcWorkers = (int)Math.Clamp(workers, 0, 15);
        return taken;
    }

    /// <summary>A flag's value; written twice, the later one, as a wrapper script adds its own before the caller's.</summary>
    private static string? Valued(List<string> taken, string flag)
    {
        string? value = null;
        for (int at = taken.IndexOf(flag); at >= 0; at = taken.IndexOf(flag))
        {
            if (at + 1 >= taken.Count) throw new ArgumentException(flag + " needs a value");
            value = taken[at + 1];
            taken.RemoveRange(at, 2);
        }
        if (value is null) return null;
        HandOn(flag, value);
        return value;
    }

    private static long? Number(List<string> taken, string flag)
    {
        if (Valued(taken, flag) is not string text) return null;
        return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new ArgumentException(flag + " takes a whole number, not '" + text + "'");
    }

    private static bool Switch(List<string> taken, string flag)
    {
        if (!taken.Remove(flag)) return false;
        while (taken.Remove(flag)) { }
        HandOn(flag);
        return true;
    }

    /// <summary>
    /// A switch for every corc this one starts (ChildFlags), ONCE. A hosted
    /// project build runs its link in this process (ProjectCommand.LinkApart)
    /// with the switches it hands on written before the command, and taking
    /// them again added each a second time: the link's backend children were
    /// started with `--report-passes --report-passes backend`, took one, and
    /// refused the other as an unknown command. A value given again replaces
    /// the one handed on.
    /// </summary>
    public static void HandOn(string flag, string? value = null)
    {
        int at = ChildFlags.IndexOf(flag);
        if (value is null)
        {
            if (at < 0) ChildFlags.Add(flag);
            return;
        }
        // A valued flag's value follows it; a value that happens to read as a
        // flag name is never taken for one, as values are skipped in pairs.
        for (int k = 0; k < ChildFlags.Count; k++)
        {
            if (ChildFlags[k] != flag || k + 1 >= ChildFlags.Count) continue;
            ChildFlags[k + 1] = value;
            return;
        }
        ChildFlags.Add(flag);
        ChildFlags.Add(value);
    }
}
