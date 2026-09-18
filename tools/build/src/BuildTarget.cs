using System.Xml.Linq;

namespace Corsac.Build;

public sealed class BuildTarget
{
    public required string Path { get; init; }
    public required XElement Element { get; init; }
    public List<BuildTarget> Dependencies { get; } = new();
    public List<BuildTarget> Steps { get; } = new();
    public List<BuildTarget> OrderAfter { get; } = new();
    public IEnumerable<XElement> Tasks => Element.Elements().Where(e => e.Name != "Target" && e.Name != "Finally");
    public IEnumerable<BuildTarget> Edges => Dependencies.Concat(Steps).Concat(OrderAfter).Distinct();
}
