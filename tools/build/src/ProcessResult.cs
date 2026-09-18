namespace Corsac.Build;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public TimeSpan Elapsed { get; init; }
    public required string LogPrefix { get; init; }
}
