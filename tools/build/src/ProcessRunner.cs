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
            using Process process = new() { StartInfo = start };
            Stopwatch watch = Stopwatch.StartNew();
            process.Start();
            if (!interactive) process.StandardInput.Close();
            await using FileStream stdout = new(prefix + ".out.log", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using FileStream stderr = new(prefix + ".err.log", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
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
        finally { slots.Release(workers); }
    }
}
