namespace Corsac.Build;

public sealed class BuildScheduler
{
    private readonly TaskExecutor executor;
    private readonly CancellationToken cancel;
    private readonly Dictionary<BuildTarget, Task> running = new();
    private readonly object gate = new();

    public BuildScheduler(TaskExecutor executor, CancellationToken cancel)
    {
        this.executor = executor;
        this.cancel = cancel;
    }

    public Task Run(BuildTarget target)
    {
        lock (gate)
        {
            if (running.TryGetValue(target, out Task? existing)) return existing;
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running.Add(target, completion.Task);
            _ = Complete(target, completion);
            return completion.Task;
        }
    }

    private async Task Complete(BuildTarget target, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await Task.WhenAll(target.Dependencies.Concat(target.OrderAfter).Distinct().Select(Run));
            foreach (BuildTarget step in target.Steps) await Run(step);
            cancel.ThrowIfCancellationRequested();
            foreach (var task in target.Tasks) await executor.Execute(target, task, cancel);
            Console.WriteLine("done /" + target.Path);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (target.Element.Element("Finally") is { } cleanup)
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
                foreach (var task in cleanup.Elements())
                {
                    try { await executor.Execute(target, task, deadline.Token); }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine("cleanup /" + target.Path + ": " + error.Message);
                        failure ??= error;
                    }
                }
            }
        }
        if (failure is null) completion.SetResult();
        else
        {
            Console.Error.WriteLine("failed /" + target.Path + ": " + failure.Message);
            completion.SetException(failure);
        }
    }
}
