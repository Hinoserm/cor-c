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
        string directory, IDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancel)
    {
        await slots.WaitAsync(cancel);
        try
        {
            Directory.CreateDirectory(logs);
            string prefix = Path.Combine(logs, Interlocked.Increment(ref sequence).ToString("D4") + "-" + label.Replace('/', '_'));
            ProcessStartInfo start = new(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
            Console.WriteLine("run /" + label + ": " + executable);
            using Process process = new() { StartInfo = start };
            Stopwatch watch = Stopwatch.StartNew();
            process.Start();
            process.StandardInput.Close();
            await using FileStream stdout = File.Create(prefix + ".out.log");
            await using FileStream stderr = File.Create(prefix + ".err.log");
            Task outCopy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            Task errCopy = process.StandardError.BaseStream.CopyToAsync(stderr);
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
        finally { slots.Release(); }
    }
}
