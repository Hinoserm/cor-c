#nullable enable

namespace Corsac.Lang.Ir;

/// <summary>
/// What a backend produces from a module: a relocatable object the linker
/// combines into an executable. One per target.
/// </summary>
public sealed class ObjectFile
{
    public List<Section> Sections { get; } = new();
    public List<Symbol> Symbols { get; } = new();

    public Section Section(string name)
    {
        foreach (Section section in Sections)
        {
            if (section.Name == name)
            {
                return section;
            }
        }
        throw new KeyNotFoundException(name);
    }
}
