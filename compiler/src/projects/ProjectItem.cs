namespace Corsac.Projects;

public sealed class ProjectItem
{
    public required string Type { get; init; }
    public required string Include { get; init; }
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
}
