using System.Diagnostics;

namespace Corsac.Build;

public sealed class ProcessRunner
{
    private readonly SemaphoreSlim slots;
    private readonly string logs;
    private int sequence;

    public ProcessRunner(int jobs, string logs)
    {
        slots = new SemaphoreSlim(jobs);
        this.logs = logs;
    }

    public async Task<ProcessResult> Run(string label, string executable, IEnumerable<string> arguments,
        string directory, IDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancel,
        bool useAvailableWorkers = false, Func<int, IEnumerable<string>>? workerArguments = null, bool interactive = false)
    {
        await slots.WaitAsync(cancel);
        int workers = 1;
        try
        {
            // Never wait while holding a partial multi-slot reservation: two
            // compilers each waiting for the rest would deadlock. Lease the
            // currently idle budget and return every slot on all exit paths.
            if (useAvailableWorkers) while (slots.Wait(0)) workers++;
            Directory.CreateDirectory(logs);
            string prefix = Path.Combine(logs, Interlocked.Increment(ref sequence).ToString("D4") + "-" + label.Replace('/', '_'));
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
            using Process process = new() { StartInfo = start };
            Stopwatch watch = Stopwatch.StartNew();
            process.Start();
            if (!interactive) process.StandardInput.Close();
            await using FileStream stdout = File.Create(prefix + ".out.log");
            await using FileStream stderr = File.Create(prefix + ".err.log");
            Task outCopy = interactive ? Task.CompletedTask : process.StandardOutput.BaseStream.CopyToAsync(stdout);
            Task errCopy = interactive ? Task.CompletedTask : process.StandardError.BaseStream.CopyToAsync(stderr);
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
        finally { slots.Release(workers); }
    }
}
