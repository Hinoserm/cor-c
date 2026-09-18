namespace Corsac.Build;

public sealed class TestResult
{
    public required string Name { get; init; }
    public required string Target { get; init; }
    public required string Status { get; init; }
    public string? Detail { get; init; }
    public ProcessResult? Process { get; init; }
}
