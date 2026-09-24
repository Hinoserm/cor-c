namespace Corsac.Projects;

public sealed class EvaluatedProject
{
    public required string Path { get; init; }
    public required string AssemblyName { get; init; }
    public required string OutputType { get; init; }
    public required string Framework { get; init; }
    public required string StartupObject { get; init; }
    public required string[] Sources { get; init; }
    public required string[] References { get; init; }
    public required string[] Defines { get; init; }
    /// <summary>The project's global usings: ImplicitUsings' and its &lt;Using&gt; items, `Name` or `Alias=Name`.</summary>
    public string[] Usings { get; init; } = Array.Empty<string>();
    public required string Evaluation { get; init; }
    public bool WarningsAsErrors { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}
