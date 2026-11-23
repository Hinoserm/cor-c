using System.Diagnostics;

namespace Corsac.Build;

public sealed class ProcessRunner : IDisposable
{
    private readonly WorkerPool pool;
    private readonly int jobs;
    private readonly string logs;
    private int sequence;

    /// <summary>
    /// The processes of one build, drawing on its worker budget: `jobs`
    /// tokens laid out in `budget` (WorkerPool), which every process of the
    /// build -- this tool's own and what the tools it runs start -- shares.
    /// </summary>
    public ProcessRunner(int jobs, string logs, string budget)
    {
        WorkerPool.Create(budget, jobs);
        pool = WorkerPool.Open(budget) ?? throw new BuildException("the worker budget could not be laid out at " + budget);
        this.jobs = jobs;
        this.logs = logs;
    }

    public void Dispose() => pool.Dispose();

    /// <summary>
    /// A process run with its share of the budget:
    /// - ONE TOKEN, held while it runs, for an ordinary task -- one process
    ///   computing.
    /// - NONE for a coordinator that is handed the budget itself
    ///   (Workers="budget", its arguments naming $(WorkerPool)): it draws a
    ///   token for each piece of work it
    ///   does, so a module of 132 units and twenty of two run side by side
    ///   on the same N, and it is told N, not what was free when it began.
    /// - The tokens going spare when it starts, for a Workers="auto" task -- a
    ///   tool that knows only a count, which it keeps.
    /// - None for an interactive one, which waits on a person.
    /// </summary>
    public async Task<ProcessResult> Run(string label, string executable, IEnumerable<string> arguments,
        string directory, IDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancel,
        bool useAvailableWorkers = false, Func<int, IEnumerable<string>>? workerArguments = null, bool interactive = false,
        bool drawsOwn = false)
    {
        int taken = 0;
        int workers = 1;
        if (drawsOwn) workers = jobs;
        else if (!interactive)
        {
            await pool.TakeAsync(cancel);
            taken = 1;
        }
        try
        {
            // Never wait while holding a partial multi-token reservation: two
            // compilers each waiting for the rest would deadlock. Lease what is
            // idle now and give every token back on all exit paths.
            if (useAvailableWorkers && !drawsOwn && !interactive) while (pool.TryTake()) { taken++; workers++; }
            Directory.CreateDirectory(logs);
            // A NAME NEVER USED BEFORE: the step, when it started and which
            // build ran it. Numbered from 1 each build, every run wrote over
            // the last one's logs.
            string prefix = Path.Combine(logs, label.Replace('/', '_') + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss.fff")
                + "-" + Environment.ProcessId + "-" + Interlocked.Increment(ref sequence).ToString("D4"));
            ProcessStartInfo start = new(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = !interactive,
                RedirectStandardError = !interactive,
                RedirectStandardInput = !interactive,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            if (workerArguments is not null)
                foreach (string argument in workerArguments(workers)) start.ArgumentList.Add(argument);
            foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
            start.Environment["CORSAC_BUILD_JOBS"] = workers.ToString();
            start.Environment["DOTNET_PROCESSOR_COUNT"] = workers.ToString();
            Console.WriteLine("run /" + label + ": " + executable + " (workers=" + workers + ")");
            // The logs made before the process starts: one that cannot be made
            // leaves no process behind with nobody reading its pipes.
            await using FileStream stdout = new(prefix + ".out.log", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using FileStream stderr = new(prefix + ".err.log", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using Process process = new() { StartInfo = start };
            Stopwatch watch = Stopwatch.StartNew();
            process.Start();
            if (!interactive) process.StandardInput.Close();
            // Both streams, every line, live (ProgressOutput).
            Task outCopy = interactive ? Task.CompletedTask : ProgressOutput.Copy(process.StandardOutput.BaseStream, stdout,
                line => Console.WriteLine("/" + label + ": " + line));
            Task errCopy = interactive ? Task.CompletedTask : ProgressOutput.Copy(process.StandardError.BaseStream, stderr,
                line => Console.WriteLine("/" + label + ": " + line));
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(timeout);
            bool timedOut = false;
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                timedOut = !cancel.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(outCopy, errCopy);
            watch.Stop();
            cancel.ThrowIfCancellationRequested();
            return new ProcessResult { ExitCode = process.ExitCode, TimedOut = timedOut,
                Elapsed = watch.Elapsed, LogPrefix = prefix };
        }
        finally { for (int k = 0; k < taken; k++) pool.Give(); }
    }
}
