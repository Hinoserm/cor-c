using System.Security.Cryptography;
using System.Text;
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
        long srcBudget = long.TryParse(Environment.GetEnvironmentVariable("CORC_SOURCE_BUDGET"), out long c) ? c : 16L * 1024 * 1024;
        using DeclarationSession session = new(index, assembly, declBudget, tokBudget, srcBudget);
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
        // AND THE UNIT'S OWN SOURCE. The receipt says what a unit READ; it
        // does not say what the unit IS. A change inside a body -- the order
        // of a list, a sign -- moves no declaration anybody read, so the
        // receipt stayed current and the old object was kept, build after
        // build. The stamp beside the receipt is the source's bytes and the
        // options it was compiled with; either changing means compiling.
        // The unit's own role is part of it: the entry unit of a library is
        // compiled with the initialiser on and the rest with it off, and an
        // object made the other way round is not current. "2" is the stamp's
        // own version, so a stamp written before the role was included is
        // never trusted.
        string optionsText = "2\t" + string.Join('\t', common);
        string Stamp(Unit unit)
        {
            using SHA256 sha = SHA256.Create();
            byte[] source = File.ReadAllBytes(unit.Source);
            byte[] options = Encoding.UTF8.GetBytes(optionsText + (unit.Entry ? "\tentry" : "\tlib"));
            sha.TransformBlock(source, 0, source.Length, null, 0);
            sha.TransformFinalBlock(options, 0, options.Length);
            return Convert.ToHexString(sha.Hash!);
        }
        int skipped = 0;
        bool Current(Unit unit)
        {
            if (!File.Exists(unit.Object) || !File.Exists(unit.Receipt)) return false;
            try
            {
                string stampPath = unit.Receipt + ".stamp";
                if (!File.Exists(stampPath) || File.ReadAllText(stampPath) != Stamp(unit)) return false;
                return UnitDependencies.IsCurrent(unit.Receipt, index);
            }
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
            // ONE INITIALISER PER LIBRARY. A shared object gets a function
            // the loader calls before anything uses it -- __corsac_init,
            // which hands the image's statics to the collector and its frame
            // tables to the stack walker. Every unit would emit its own and
            // the link would refuse the duplicates, so the entry unit alone
            // emits it. Switching it off for all of them, as the parallel
            // compile first did, left every library's statics unscanned: the
            // collector freed live objects and the desktop died of it.
            if (!unit.Entry) { one.Add("--lib"); one.Add("--no-shared-init"); }
            one.Add("--dependency-file"); one.Add(unit.Receipt);
            one.Add("-o"); one.Add(unit.Object);
            long before = GC.GetTotalAllocatedBytes();
            int code;
            try
            {
                code = Driver.Compile(one.ToArray());
            }
            // A UNIT THAT CANNOT BE COMPILED IS A FAILED UNIT, not a dead
            // process. Compiling one source by itself reports a bad input and
            // exits; the same input reached through here threw on a worker
            // thread, where nothing was catching, and the runtime took the
            // whole process down with a core dump. What it printed was a
            // stack trace, so a stale index -- an ordinary thing to have --
            // looked like a compiler crash.
            catch (Exception failure) when (failure is InvalidDataException or IOException)
            {
                lock (gate)
                {
                    done++;
                    Console.Error.WriteLine("corc: compiling " + unit.Source + ": " + failure.Message);
                }
                return 1;
            }
            long after = GC.GetTotalAllocatedBytes();
            if (code == 0)
            {
                try { File.WriteAllText(unit.Receipt + ".stamp", Stamp(unit)); }
                catch (IOException) { }
            }
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

        // THE ENTRY SOURCE JOINS THE QUEUE LIKE THE REST. It used to be
        // compiled alone and first because the settings that make a unit the
        // entry are process-wide statics in Lowering -- but on inspection the
        // only one that differs between the entry and a library part is
        // Dynamic, and Dynamic is only ever true with shared libraries on the
        // command line. Everything else is the same for every unit of a
        // project. Sitting the other workers idle for the whole of kmain,
        // which is one of the three largest sources, cost nine tenths of a
        // second of a six second build.
        // A SHARED LIBRARY TOO: its entry unit is the one that emits the
        // initialiser, and SharedObject is a static of the lowering, so it
        // must be compiled alone before the units that switch it off run
        // beside it -- or all of them see it on and the link refuses eight
        // definitions of __corsac_init.
        bool dynamic = common.Contains("--dynamic") || common.Contains("--link-shared") || common.Contains("--shared");
        if (dynamic)
            foreach (Unit unit in units.Where(unit => unit.Entry))
                if (Gated(unit) != 0) failures++;

        // BIGGEST SOURCE FIRST. The longest unit bounds the build however
        // many workers there are, and a long one started last runs alone at
        // the end; started first, the small ones fill in behind it. Giving
        // the largest unit threads of its own was tried and made things
        // worse: the collector, not the scheduler, is what the last two
        // seconds are spent on, and more concurrency means more of it.
        long Size(Unit unit) { try { return new FileInfo(unit.Source).Length; } catch (IOException) { return 0L; } }
        Unit[] rest = units.Where(unit => !unit.Entry || !dynamic)
            .OrderByDescending(Size).ThenBy(unit => unit.Source, StringComparer.Ordinal).ToArray();
        int next = -1;
        void Worker()
        {
            while (true)
            {
                int i = Interlocked.Increment(ref next);
                if (i >= rest.Length) return;
                if (Gated(rest[i]) != 0) Interlocked.Increment(ref failures);
            }
        }
        int threads = Math.Min(limit, rest.Length);
        if (threads <= 1) Worker();
        else
        {
            Thread[] running = new Thread[threads];
            for (int worker = 0; worker < threads; worker++)
            {
                running[worker] = new Thread(Worker, 16 * 1024 * 1024) { IsBackground = true, Name = "compile-" + worker };
                running[worker].Start();
            }
            foreach (Thread thread in running) thread.Join();
        }

        Driver.Session = null;
        Console.Error.WriteLine("project " + assembly + ": " + units.Count + " units, " + skipped + " already current, "
            + session.Tokens.Hits + " header lexes reused, " + session.Catalog.PayloadLoads + " index payload loads, "
            + "caches resident=" + (session.Tokens.ResidentBytes + session.Catalog.ResidentBytes
                + session.Catalog.ResidentSourceBytes) + ", "
            // HOW MUCH OF THE WALL CLOCK THE COLLECTOR TOOK, because that is
            // the first question when eight workers finish no sooner than
            // four, and it is answered here rather than guessed at.
            + "gc pause=" + (long)GC.GetTotalPauseDuration().TotalMilliseconds + "ms gen0=" + GC.CollectionCount(0)
            + " gen1=" + GC.CollectionCount(1) + " gen2=" + GC.CollectionCount(2) + ", "
            + "allocated=" + GC.GetTotalAllocatedBytes());
        return failures == 0 ? 0 : 1;
    }
}
