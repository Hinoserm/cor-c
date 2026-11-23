namespace Corsac.Build;

/// <summary>
/// `corc build`: runs a corsac.build manifest. One executable holds the
/// build, the compiler and the linker; the processes it runs share one worker
/// budget (WorkerPool), named to manifests as $(WorkerPool).
/// </summary>
public static class BuildCommand
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            BuildOptions options = BuildOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("corc build [target/path] [Name=Value ...] [--file corsac.build] [--list] [--plan] [--jobs N]\n"
                    + "      [--configuration Release] [--toolchain corc] [--property Name=Value]\n"
                    + "MSBuild is allowed only for the build-tool component within bootstrap.\n"
                    + "Default jobs: available logical CPUs; --jobs lowers the global worker budget.");
                return 0;
            }
            BuildManifest manifest = BuildManifest.Load(options.File ?? BuildManifest.Discover(Environment.CurrentDirectory), options);
            if (options.List)
            {
                foreach (BuildTarget item in manifest.Targets.Values.OrderBy(t => t.Path, StringComparer.Ordinal))
                    Console.WriteLine(item.Path + "  " + (string?)item.Element.Attribute("Description"));
                return 0;
            }
            BuildTarget target = manifest.Select(options.Target, Environment.CurrentDirectory);
            BuildGraph graph = new(target);
            string logs = Path.Combine(manifest.Root, "build", "logs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            TestReport report = new();
            // THE BUILD'S WORKER BUDGET (WorkerPool): --jobs tokens every process
            // of the build draws on, the build tool's own tasks included,
            // named to manifests as $(WorkerPool). One directory per build,
            // gone when it ends however it ends; those of builds that died
            // without their cleanup are swept first.
            string workersRoot = Path.Combine(manifest.Root, "build", "workers");
            WorkerPool.SweepDead(workersRoot);
            string budget = Path.Combine(workersRoot, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            manifest.UseWorkerPool(budget);
            using ProcessRunner processes = new(options.Jobs, logs, budget);
            try
            {
            TaskExecutor executor = new(manifest, options, processes, report);
            foreach (BuildTarget node in graph.Ordered)
            {
                foreach (var task in node.Tasks) executor.Validate(task, options.Plan);
                if (node.Element.Element("Finally") is { } cleanup)
                {
                    BuildManifest.Check(cleanup, "Finally");
                    foreach (var task in cleanup.Elements()) executor.Validate(task, options.Plan);
                }
            }
            if (options.Plan)
            {
                foreach (BuildTarget node in graph.Ordered)
                    Console.WriteLine("/" + node.Path + " <- " + string.Join(", ", node.Edges.Select(t => "/" + t.Path)));
                return 0;
            }
            using CancellationTokenSource cancel = new();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try { await new BuildScheduler(executor, cancel.Token).Run(target); }
            finally
            {
                Console.CancelKeyPress -= handler;
                report.Write(Path.Combine(logs, "tests.xml"));
            }
            return 0;
            }
            finally
            {
                processes.Dispose();
                try { Directory.Delete(budget, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("corc build: cancelled"); return 130; }
        catch (Exception error) { Console.Error.WriteLine("corc build: " + error.Message); return 1; }
    }
}
