namespace Corsac.Build;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            BuildOptions options = BuildOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("build [target/path] [--file corsac.build] [--list] [--plan] [--jobs N]\n"
                    + "      [--configuration Release] [--toolchain dotnet] [--property Name=Value]");
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
            ProcessRunner processes = new(options.Jobs, logs);
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
        catch (OperationCanceledException) { Console.Error.WriteLine("build: cancelled"); return 130; }
        catch (Exception error) { Console.Error.WriteLine("build: " + error.Message); return 1; }
    }
}
