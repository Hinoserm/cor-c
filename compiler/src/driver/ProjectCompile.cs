#nullable enable
using Corsac.Lang.Metadata;

namespace Corsac;

/// <summary>
/// Compiles every source of one project in a single process.
///
/// A project's sources are compiled separately so that editing one rebuilds
/// one object, and that is worth keeping. What is not worth keeping is doing
/// the WORK of separateness: each source opened the declaration index again,
/// read the same standard library records out of it again, and lexed the same
/// header text again, because nothing outlived the process. Running them
/// together keeps the separate objects and the separate receipts and shares
/// only what does not depend on which source is being compiled.
///
/// The per-source work is still <see cref="Driver"/>'s ordinary compile path,
/// called once per source, so there is no second implementation of anything
/// to drift out of step with the first.
/// </summary>
public static class ProjectCompile
{
    /// <summary>One line of the work list: what to compile and where it goes.</summary>
    private readonly record struct Unit(string Source, string Object, string Receipt, bool Entry);

    public static int Run(string[] argv)
    {
        string[] args = Driver.Response(argv);
        string? list = Driver.Value(args, "--units");
        string? index = Driver.Value(args, "--decl-index");
        string? assembly = Driver.Value(args, "--assembly");
        if (list is null || index is null || assembly is null)
            return Driver.Fail("compile-project needs --units <file>, --decl-index <index> and --assembly <identity>");
        if (!File.Exists(list)) return Driver.Fail("compile-project: no unit list at " + list);

        int workers = 1;
        if (Driver.Value(args, "--jobs") is string jobs && (!int.TryParse(jobs, out workers) || workers < 1))
            return Driver.Fail("--jobs requires a positive worker count");

        List<Unit> units = new();
        foreach (string line in File.ReadAllLines(list))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] part = line.Split('\t');
            if (part.Length < 3) return Driver.Fail("compile-project: each unit line needs source, object and receipt");
            units.Add(new Unit(part[0], part[1], part[2], part.Length > 3 && part[3] == "entry"));
        }
        if (units.Count == 0) return Driver.Fail("compile-project: the unit list is empty");

        // Everything that is not the source path or its outputs is passed
        // through to each compilation exactly as it was given.
        List<string> common = new();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--units" or "--jobs" or "--worker-pool") { i++; continue; }
            common.Add(args[i]);
        }

        long declBudget = long.TryParse(Environment.GetEnvironmentVariable("CORC_DECL_BUDGET"), out long d) ? d : 32L * 1024 * 1024;
        long tokBudget = long.TryParse(Environment.GetEnvironmentVariable("CORC_TOKEN_BUDGET"), out long t) ? t : 256L * 1024 * 1024;
        using DeclarationSession session = new(index, assembly, declBudget, tokBudget);
        using WorkerPool? pool = WorkerPool.Open(Driver.Value(args, "--worker-pool"));
        int limit = pool is null ? workers : Math.Max(workers, pool.Size);
        Driver.Session = session;
        int failures = 0;
        object gate = new();
        int done = 0;

        // WHAT IS ALREADY BUILT IS NOT BUILT AGAIN. The receipt each unit
        // wrote says which declarations it read and what they were; if none
        // of them has moved and the object is still there, the unit is
        // current. Asking here rather than in the build tool saves a process
        // per unit just to put the question.
        int skipped = 0;
        bool Current(Unit unit)
        {
            if (!File.Exists(unit.Object) || !File.Exists(unit.Receipt)) return false;
            try { return UnitDependencies.IsCurrent(unit.Receipt, index); }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        int One(Unit unit)
        {
            if (Current(unit))
            {
                lock (gate) { done++; skipped++; }
                return 0;
            }
            List<string> one = new() { unit.Source };
            one.AddRange(common);
            if (!unit.Entry) one.Add("--lib");
            one.Add("--dependency-file"); one.Add(unit.Receipt);
            one.Add("-o"); one.Add(unit.Object);
            long before = GC.GetTotalAllocatedBytes();
            int code = Driver.Compile(one.ToArray());
            long after = GC.GetTotalAllocatedBytes();
            lock (gate)
            {
                done++;
                Console.Error.WriteLine("unit " + done + "/" + units.Count + " " + Path.GetFileName(unit.Source)
                    + " unit-allocated=" + (after - before) + (code == 0 ? "" : " FAILED"));
            }
            return code;
        }

        // EVERY SOURCE TAKES A WORKER FROM THE SHARED BUDGET while it is
        // compiled and gives it back after. That is the whole of the
        // scheduling: the build runs several projects at once and none of
        // them can know what share it should have, because the answer changes
        // as the others finish. Asking per source means the kernel picks up
        // the workers the one-file utilities free as they go, rather than
        // being stuck with whatever was spare when it started.
        //
        // Without a budget -- a compiler run by hand -- the thread count is
        // simply what was asked for.
        int Gated(Unit unit)
        {
            if (pool is null) return One(unit);
            while (!pool.TryTake()) Thread.Sleep(15);
            try { return One(unit); }
            finally { pool.Give(); }
        }

        // THE ENTRY SOURCE IS COMPILED ALONE AND FIRST. It is the one unit of
        // a project that is not a library part, and the settings that say so
        // are process-wide; the rest share one set of settings and may run
        // together. See Lowering's static configuration.
        foreach (Unit unit in units.Where(unit => unit.Entry))
            if (Gated(unit) != 0) failures++;

        Unit[] rest = units.Where(unit => !unit.Entry).ToArray();
        if (limit <= 1 || rest.Length <= 1)
        {
            foreach (Unit unit in rest) if (Gated(unit) != 0) failures++;
        }
        else
        {
            Parallel.ForEach(rest, new ParallelOptions { MaxDegreeOfParallelism = limit }, unit =>
            {
                if (Gated(unit) != 0) Interlocked.Increment(ref failures);
            });
        }

        Driver.Session = null;
        Console.Error.WriteLine("project " + assembly + ": " + units.Count + " units, " + skipped + " already current, "
            + session.Tokens.Hits + " header lexes reused, " + session.Catalog.PayloadLoads + " index payload loads, "
            + "allocated=" + GC.GetTotalAllocatedBytes());
        return failures == 0 ? 0 : 1;
    }
}
